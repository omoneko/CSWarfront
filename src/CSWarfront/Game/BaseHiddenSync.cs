using System;
using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task71: for bases whose per-faction asset overlay (<see cref="BaseVisuals"/>) is shown, hides
    /// the vanilla/default-model building mesh individually via Building.Flags.Hidden (requirement 2:
    /// avoid double rendering (stacking) with the overlay).
    ///
    /// Why Building.Flags.Hidden (confirmed by decompiling the game's Assembly-CSharp.dll with
    /// ilspycmd; details in task-71-report.md):
    ///   - The top of Building.RenderInstance:
    ///     `if ((flags &amp; (Flags.Created | Flags.Deleted | Flags.Hidden)) != Flags.Created) return;`
    ///     skips the mesh/LOD/props/notification-icon render calls entirely.
    ///   - The click-selection hit test (BuildingManager.RayCast → Building.RayCast(buildingID, ray, out t))
    ///     is a geometric grid raycast against the footprint (Width/Length), completely independent of
    ///     rendering, and never consults the Hidden flag (it passes right through unless the caller
    ///     includes Hidden in the ignoreFlags it supplies). Therefore selection, BaseInfoPanel, and
    ///     capture (the Core side never looks at CS-entity flags) do not break when Hidden is set.
    ///   - Only PlayAudio goes silent while Hidden (accepted as a known minor side effect: at most,
    ///     ambient sound disappears on bases whose appearance is swapped).
    ///
    /// Thread boundary:
    ///   - <see cref="SetDesired"/> is main-thread-only. Called only from BaseVisuals (the same places
    ///     that create/destroy the overlay GameObjects). It never touches CS entities (the Building
    ///     struct); it merely pushes a (baseId, hidden) pending entry into a lock-protected dictionary
    ///     (exactly the same bridge pattern as BasePlacementWatcher._pendingCreated/_pendingReleased).
    ///   - <see cref="ApplyPending"/> is sim-thread-only (called from MilitaryManager.OnSimTick while
    ///     holding _stateLock); it drains the pending entries and actually writes into the CS building
    ///     buffer.
    ///   - <see cref="IsHiddenApplied"/> is main-thread-only. Safely reads via _lock whether Hidden has
    ///     actually been applied to the CS building buffer (Task75, see BaseVisuals).
    ///
    /// Task75 (root cause and fix of the base double-rendering bug — the history at the time):
    ///   Checking the live-game log (output_log.txt) showed that every base actually placed by this
    ///   mod had been registered via Task74's "Options-designated building asset" path
    ///   (<see cref="BaseBuildingDesignation"/>) (e.g. Info.name="MilitaryBase_Army.MilitaryBase_Army_Data",
    ///   not the power-tab cloned-prefab name "CSWarfront Military Base" etc. that was still registered
    ///   back then). However, the old implementation of <see cref="ApplyPending"/> only looked at the
    ///   safety check "the target must be limited to ids matching our mod's base prefab registered by
    ///   the then power-tab cloned-prefab mechanism", and did not look at the other legitimate
    ///   registration path added in Task74 (BaseBuildingDesignation) (the comment had never been
    ///   updated since Task61).
    ///   As a result, for bases registered via BaseBuildingDesignation, even though BaseVisuals.Sync
    ///   created an overlay and requested "should be hidden" via SetDesired, the then safety check
    ///   always returned false, so Building.Flags.Hidden was never set = the vanilla entity and the
    ///   overlay kept being rendered simultaneously (if the asset name the player assigned to the
    ///   overlay matched the asset name used for placement, literally "the same building" appeared
    ///   stacked. It did not disappear until that overlay was destroyed, e.g. by capture — matching the
    ///   user report of "for a while"). Fix: aligned with exactly the same check as
    ///   BasePlacementWatcher.ProcessCreated (now that Task82 has completely removed the power-tab
    ///   cloned-prefab mechanism itself, the check is solely BaseBuildingDesignation.TryMatch).
    ///
    ///   Additionally, an independent theoretical race (the 1-tick gap of "overlay created on the main
    ///   thread → Hidden applied on the next sim-thread tick") is also closed. A new
    ///   <see cref="_confirmedHidden"/> (added only at the moment ApplyPending actually sets Hidden)
    ///   was introduced, and BaseVisuals.Sync waits to create the overlay until IsHiddenApplied returns
    ///   true (it first issues only SetDesired(true), and creates the GameObject only after
    ///   confirmation). This eliminates, even in theory, any moment where "the vanilla entity and the
    ///   overlay are visible simultaneously in the same frame".
    /// </summary>
    internal static class BaseHiddenSync
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<ushort, bool> _pending = new Dictionary<ushort, bool>();

        // Task75: the set of baseIds for which ApplyPending actually set Building.Flags.Hidden (and has
        // not yet cleared it). Protected by _lock so it can be accessed safely from both the main
        // thread (via IsHiddenApplied) and the sim thread (via ApplyPending) (shares the same lock as
        // _pending; no extra dedicated lock is added).
        // UnhideAllForSave/ReapplyAfterSave are temporary physical bit operations for the save process;
        // this mod's logical intent to "hide" (= the fact that an overlay represents that base) does
        // not change, so those two methods never update this set (updating it would make the overlay
        // vanish for a moment during saving).
        private static readonly HashSet<ushort> _confirmedHidden = new HashSet<ushort>();

        // Task72: the set of building ids this mod currently believes it has Building.Flags.Hidden set
        // on (sim-thread-only, no lock needed = touched only inside _stateLock via
        // MilitaryManager.OnSimTick/SerializeLocked, or from the main thread via Reset() = level
        // unload. Exactly the same ownership-tracking pattern as CombatRoadBlocker._owned). Updated
        // only at the moments ApplyPending actually sets/clears the flag. The clear/re-assert around a
        // save (UnhideAllForSave/ReapplyAfterSave) and the bulk release on level unload (Reset) rely on
        // this set to know "which buildings are currently hidden".
        private static readonly HashSet<ushort> _hiddenIds = new HashSet<ushort>();

        /// <summary>Main thread only. Records the latest desired state of "should this base be hidden",
        /// to be applied on the next <see cref="ApplyPending"/> (if called multiple times within the
        /// same tick, only the last value remains = overwriting is fine).</summary>
        public static void SetDesired(ushort baseId, bool hidden)
        {
            lock (_lock) { _pending[baseId] = hidden; }
        }

        /// <summary>
        /// Main thread only (Task75). Returns whether the Hidden requested via
        /// <see cref="SetDesired"/>(baseId, true) has actually been applied to the CS building buffer
        /// by the sim thread's <see cref="ApplyPending"/>. BaseVisuals does not create the overlay
        /// GameObject until this returns true (waiting until confirmation structurally prevents the
        /// reported double-rendering bug where the not-yet-hidden vanilla entity would be visible
        /// simultaneously, even for an instant, before creation).
        /// </summary>
        public static bool IsHiddenApplied(ushort baseId)
        {
            lock (_lock) { return _confirmedHidden.Contains(baseId); }
        }

        /// <summary>
        /// Sim thread only (the caller must hold _stateLock). Drains the pending entries and applies
        /// them to the Flags.Hidden bit in the CS building buffer. Targets are strictly limited to ids
        /// matching the Options-designated building (BaseBuildingDesignation) (a safeguard against
        /// accidentally rewriting ids of other mods or ordinary buildings. In theory this is
        /// unnecessary, since only ids already verified via TryMatch by
        /// BasePlacementWatcher.ProcessCreated ever arrive here through the WarState.Bases →
        /// BaseVisuals.Sync snapshot, but given the nature of writing bits directly into the CS
        /// building buffer, it is kept as defense in depth).
        /// </summary>
        public static void ApplyPending()
        {
            Dictionary<ushort, bool> drained;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                drained = new Dictionary<ushort, bool>(_pending);
                _pending.Clear();
            }

            if (!Singleton<BuildingManager>.exists) return;
            Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

            // Task75: collect the confirmation-state changes finalized in this tick, and apply them by
            // taking _lock just once, separately from the Building struct writes (so the loop body does
            // not run while holding _lock).
            List<ushort> newlyHidden = null;
            List<ushort> newlyUnhidden = null;

            foreach (var kv in drained)
            {
                try
                {
                    ushort id = kv.Key;
                    bool hidden = kv.Value;
                    if (id >= buf.Length) continue;
                    if ((buf[id].m_flags & Building.Flags.Created) == 0) continue; // Already demolished, etc.

                    // Task75/Task82: targets are strictly limited to our own base ids registered by
                    // this mod (defense in depth, see the comment at the top of the class). Uses the
                    // exact same check that BasePlacementWatcher.ProcessCreated uses when registering a
                    // base: a name match against the Options-designated building asset
                    // (BaseBuildingDesignation) (the power-tab cloned-prefab mechanism =
                    // WarfrontBasePrefab was removed in Task82).
                    BaseType ignored;
                    if (buf[id].Info == null) continue;
                    bool isOwnBase = BaseBuildingDesignation.TryMatch(buf[id].Info.name, out ignored);
                    if (!isOwnBase) continue;

                    if (hidden)
                    {
                        buf[id].m_flags |= Building.Flags.Hidden;
                        _hiddenIds.Add(id);
                        (newlyHidden ?? (newlyHidden = new List<ushort>())).Add(id);
                    }
                    else
                    {
                        buf[id].m_flags &= ~Building.Flags.Hidden;
                        _hiddenIds.Remove(id);
                        (newlyUnhidden ?? (newlyUnhidden = new List<ushort>())).Add(id);
                    }
                }
                catch (Exception e)
                {
                    ModConfig.LogError("BaseHiddenSync.ApplyPending: base " + kv.Key + " error: " + e);
                }
            }

            if (newlyHidden != null || newlyUnhidden != null)
            {
                lock (_lock)
                {
                    if (newlyHidden != null)
                        for (int i = 0; i < newlyHidden.Count; i++) _confirmedHidden.Add(newlyHidden[i]);
                    if (newlyUnhidden != null)
                        for (int i = 0; i < newlyUnhidden.Count; i++) _confirmedHidden.Remove(newlyUnhidden[i]);
                }
            }
        }

        /// <summary>
        /// Called right before saving (Task72). Temporarily clears all Hidden bits set by this mod so
        /// they are not baked into the save data. <see cref="_hiddenIds"/> itself is kept in memory,
        /// and <see cref="ReapplyAfterSave"/> re-sets the bits on the same set. The caller
        /// (MilitaryManager.SerializeLocked) invokes this while holding _stateLock, so there is no race
        /// with the sim thread. Exactly the same pattern as CombatRoadBlocker.UnblockAllForSave.
        /// </summary>
        public static void UnhideAllForSave()
        {
            try
            {
                if (_hiddenIds.Count == 0) return;
                if (!Singleton<BuildingManager>.exists) return;
                Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                foreach (ushort id in _hiddenIds)
                {
                    if (id < buf.Length) buf[id].m_flags &= ~Building.Flags.Hidden;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseHiddenSync.UnhideAllForSave exception: " + e);
            }
        }

        /// <summary>
        /// Re-sets the Hidden bits that UnhideAllForSave cleared (Task72).
        ///
        /// Important: by contract this method is called not "immediately after
        /// WarStateSerializer.Serialize" but always "after the vanilla BuildingManager.Data.Serialize
        /// has actually finished writing Building.m_flags to the stream"; the caller
        /// (MilitaryManagerPersistence.SerializeLocked) defers execution as the next action via
        /// Singleton&lt;SimulationManager&gt;.instance.AddAction (based on the actual save order
        /// confirmed by decompiling SimulationManager.Data.Serialize/LoadingManager.SaveSimulationData
        /// with ilspycmd; see the comments on SerializeLocked for details).
        /// If a building has been demolished (Created dropped), it is silently given up on and removed
        /// from the set (same as the stale handling in CombatRoadBlocker.ReassertOwned).
        /// </summary>
        public static void ReapplyAfterSave()
        {
            try
            {
                if (_hiddenIds.Count == 0) return;
                if (!Singleton<BuildingManager>.exists) return;
                Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

                List<ushort> stale = null;
                foreach (ushort id in _hiddenIds)
                {
                    if (id >= buf.Length || (buf[id].m_flags & Building.Flags.Created) == 0)
                    {
                        (stale ?? (stale = new List<ushort>())).Add(id);
                        continue;
                    }
                    buf[id].m_flags |= Building.Flags.Hidden;
                }

                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++) _hiddenIds.Remove(stale[i]);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseHiddenSync.ReapplyAfterSave exception: " + e);
            }
        }

        /// <summary>
        /// On level unload (called from MilitaryManager.Reset()): restores every building this mod set
        /// to Hidden back to its plain appearance, then clears internal state (Task72). Same reason and
        /// same shape as CombatRoadBlocker.Reset: Reset() is called on the main thread (via
        /// OnLevelUnloading), and after that the sim thread's OnSimTick (and hence ApplyPending's
        /// draining of _pending) most likely never runs again, so merely pushing into _pending via
        /// SetDesired is no guarantee of restoration. Here we write directly into the CS building
        /// buffer. Since the level may be mid-teardown with BuildingManager already invalidated, a
        /// failure to unhide is only logged and never propagated as an exception.
        /// </summary>
        public static void Reset()
        {
            try
            {
                if (_hiddenIds.Count > 0 && Singleton<BuildingManager>.exists)
                {
                    Building[] buf = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                    foreach (ushort id in _hiddenIds)
                    {
                        if (id < buf.Length) buf[id].m_flags &= ~Building.Flags.Hidden;
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseHiddenSync.Reset: unhide-on-unload failed (harmless, level is tearing down): " + e);
            }
            finally
            {
                _hiddenIds.Clear();
                lock (_lock) { _pending.Clear(); _confirmedHidden.Clear(); } // Task75
            }
        }
    }
}
