using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// Main-thread-only snapshot. Contains only the minimum information needed to decide the
    /// "appearance" of a single military base. A value type that contains no CS entities (Building
    /// etc.) whatsoever (same policy as UnitVisuals.UnitVisualState).
    /// </summary>
    public struct BaseVisualState
    {
        public ushort BaseId;
        public byte FactionId;
        public Vector3 Position;

        /// <summary>Building orientation (radians, exactly CS Building.m_angle).
        /// If BasePlacementWatcher.TryGetAngle could not resolve it, the caller passes 0 (default
        /// orientation).</summary>
        public float Angle;

        /// <summary>Task66: base type (army/navy/air force/missile). Simply carries Core.MilitaryBase.Type
        /// as-is (a field that already exists on Core's State.Bases; the Game layer does not need to compute
        /// anything new). Passed to UnitAssetBindings.TryGetForBase to resolve the per-type model assignment
        /// key.</summary>
        public BaseType Type;
    }

    /// <summary>
    /// Task60: overlay rendering that gives military bases (MilitaryBase) a per-faction appearance.
    ///
    /// Design decision (the only safe path that breaks none of the vanilla features):
    /// A base of each type (army/navy/air force/missile) is a real CS building (placement/AI/info
    /// panel/capture are all handled by the vanilla BuildingManager/BuildingAI) spawned from the
    /// BuildingInfo pointed to by the Options-designated building (BaseBuildingDesignation, the sole
    /// placement path since Task82 removed the power-tab cloned-prefab mechanism); within a type, all
    /// factions share the BuildingInfo of the same asset.
    /// BuildingInfo.m_mesh is a per-prefab (per-asset) field, and the standard CS API offers no way to
    /// "swap the render mesh per building instance". Even if we could rewrite m_mesh per base, that
    /// would rewrite the BuildingInfo itself, which is shared within the type, so the appearance of
    /// already-placed bases of the same type belonging to other factions would change simultaneously
    /// (the exact opposite of the requirement). There is also no official API to individually hide the
    /// vanilla mesh (fiddling with Building.m_flags to "make it invisible" might be possible, but that
    /// risks interfering with flags used by both vanilla and Core for capture/production/info-panel
    /// decisions, violating the constraint "never touch the decision logic of Core/CS entities").
    /// Therefore this class adopts exactly the same technique as UnitVisuals (render our own
    /// GameObject, which never touches CS entities, overlaid at the logical coordinates).
    ///
    /// Task71: the comment above (the original design decision) was revisited after the stacking
    /// report (a bug where the leftover wind turbine and the assigned asset appeared stacked on top of
    /// each other). After decompiling the game itself with ilspycmd, we confirmed that
    /// Building.Flags.Hidden suppresses only rendering (Building.RenderInstance) individually, and
    /// does not affect selection (BuildingManager.RayCast is a geometric grid raycast unrelated to
    /// rendering), capture (the Core side never looks at CS-entity flags), or the info panel
    /// (details in task-71-report.md). So now, only for bases whose overlay has actually been created,
    /// we individually hide the vanilla Building mesh via <see cref="BaseHiddenSync"/>, and on assigned
    /// bases only the assigned asset is visible instead of the designated building asset's own default
    /// appearance (no stacking occurs. Task82: the default-model swap that was dedicated to the
    /// power-tab cloned prefab = WarfrontBasePrefabVisualSwap was deleted along with the removal of
    /// the cloned-prefab mechanism itself).
    ///
    /// Task75 (fix for the base double-rendering bug, details in task-75-report.md):
    ///   The root cause confirmed in live-game logs was on the BaseHiddenSync.ApplyPending side (for
    ///   bases placed via the "Options-designated building asset" path added in Task74, the safety
    ///   check at the time only looked at the power-tab cloned prefab, so Hidden was never set; see the
    ///   comments in BaseHiddenSync.cs for the fix).
    ///   In addition, the theoretically remaining "1-tick gap between overlay creation and Hidden
    ///   taking effect" was also closed in this class: on the first Sync that detects an assignment we
    ///   do not create the overlay GameObject yet; we only issue a BaseHiddenSync.SetDesired(id, true)
    ///   request and record the id in <see cref="_pendingOverlays"/>. Subsequent Syncs wait until
    ///   BaseHiddenSync.IsHiddenApplied(id) returns true (= ApplyPending confirmed that the sim thread
    ///   actually set Building.Flags.Hidden), and only once confirmed do we create the GameObject via
    ///   CreateVisual. This structurally eliminates any moment where "the vanilla entity and the
    ///   overlay are both visible in the same frame" (normally confirmation arrives immediately on the
    ///   next OnSimTick, so there is no perceptible delay).
    ///
    ///   The invariant "a base with no assignment shows only the default model (the prefab's own
    ///   already-swapped appearance)" is guaranteed by exactly one place: the `if (!hasAssignment)`
    ///   branch at the top of Sync() below (if there is no assignment, CreateVisual is never called at
    ///   all, and any existing overlay is destroyed).
    ///   No overlay is ever created for a base of a faction without an assignment (requirement: "only
    ///   bases that have an assignment").
    /// Task60 resolved (faction, "MilitaryBase") → asset using only a single key that did not
    /// distinguish base types (<see cref="UnitAssetBindings.BaseTypeKey"/>, "MilitaryBase"), but Task66
    /// changed this to use <see cref="UnitAssetBindings.TryGetForBase"/>, which supports per-base-type
    /// dedicated keys (<see cref="UnitAssetBindings.BaseTypeKeyFor"/>) (falling back to the old unified
    /// key if the per-type key has no entry — backward compatible)
    /// (UnitMeshSource.TryResolve is not used — that is a heavy resolution chain intended for units,
    /// including Tier fallback / default-model fallback / vehicle-prefab-borrowing fallback, but bases
    /// have neither Tiers nor default built-in model resolution, so a thin implementation calling
    /// UnitAssetBindings.TryGetForBase directly is sufficient).
    ///
    /// We borrow only the mesh (AssetCatalog.TryGetMesh) and the texture (mainTexture only, via
    /// UnitMaterialFactory.TryGetAssetMaterial); we never borrow the CS-side Material/AI (the same
    /// safety guarantee as UnitVisuals/UnitMeshSource. Because no AI is instantiated, side effects and
    /// crashes are impossible in principle).
    ///
    /// Thread boundary: every public method of this class is "main thread only"
    /// (new GameObject / AddComponent / Destroy / transform writes are Unity main-thread constraints).
    /// Never call from the sim thread (MilitaryManager.OnSimTick). Snapshots
    /// (<see cref="BaseVisualState"/>) are assembled by MilitaryManager.OnMainVisualUpdate while
    /// holding _stateLock, from WarState.Bases (Core; positions are immutable values recorded by
    /// BasePlacementWatcher at creation time) and BasePlacementWatcher._baseAngles (Game; values the
    /// sim thread already read from the CS building buffer). This class itself has no direct access to
    /// CS entities (BuildingManager) whatsoever.
    /// </summary>
    public static class BaseVisuals
    {
        private class VisualEntry
        {
            public GameObject GameObject;
        }

        private static readonly Dictionary<ushort, VisualEntry> _visuals = new Dictionary<ushort, VisualEntry>();

        // baseIds whose creation failed (mesh/material could not be resolved, etc.). To avoid retrying
        // every frame and spamming the log, a failed id is recorded here once and skipped by Sync()
        // thereafter (same policy as UnitVisuals._failedInstances).
        private static readonly HashSet<ushort> _failedInstances = new HashSet<ushort>();

        // Task75: the set of baseIds for which an assignment was detected and
        // BaseHiddenSync.SetDesired(id, true) has been requested, but BaseHiddenSync.IsHiddenApplied(id)
        // does not yet return true (= Hidden unconfirmed), so no GameObject has been created.
        // Main-thread-only access.
        private static readonly HashSet<ushort> _pendingOverlays = new HashSet<ushort>();

        // Work areas reused on every Sync() run (avoids GC).
        private static readonly HashSet<ushort> _seenIds = new HashSet<ushort>();
        private static readonly List<ushort> _staleIds = new List<ushort>();
        private static readonly List<ushort> _staleFailedIds = new List<ushort>();
        private static readonly List<ushort> _stalePendingIds = new List<ushort>();

        public static int Count { get { return _visuals.Count; } }

        /// <summary>
        /// Based on the snapshot, creates/moves/destroys overlays only for bases that have a per-faction
        /// asset assigned (main thread only). Bases with no assignment have no overlay (if one already
        /// has an overlay, it is destroyed at this point — the moment the assignment was removed — and
        /// reverts to the default appearance). Ids absent from the snapshot (base demolition, ghost-base
        /// cleanup, etc.) are destroyed here.
        /// </summary>
        public static void Sync(List<BaseVisualState> snapshot)
        {
            if (snapshot == null) return;

            _seenIds.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                BaseVisualState s = snapshot[i];
                _seenIds.Add(s.BaseId);

                try
                {
                    AssetKind kind;
                    string name;
                    // Task66: resolve the per-base-type assignment key (per-type key first, then the old
                    // unified key — backward compatible).
                    bool hasAssignment = UnitAssetBindings.TryGetForBase(s.FactionId, s.Type, out kind, out name);

                    if (!hasAssignment)
                    {
                        // No assignment (still the default model). If it previously had an overlay,
                        // destroy it here and revert to the vanilla/default built-in appearance (this
                        // path is taken both when the faction ownership changes and when the assignment
                        // is removed). This is the embodiment of the invariant "a base with no
                        // assignment shows only the prefab's own appearance".
                        if (_visuals.ContainsKey(s.BaseId)) DestroyVisual(s.BaseId);
                        if (_pendingOverlays.Remove(s.BaseId))
                        {
                            // Task75: the assignment was removed while waiting for Hidden confirmation
                            // (a rare timing). Nothing has been created yet, so simply canceling the
                            // hide request we issued is enough.
                            BaseHiddenSync.SetDesired(s.BaseId, false);
                        }
                        continue;
                    }

                    if (_failedInstances.Contains(s.BaseId))
                    {
                        continue; // Already known to be uncreatable. Skip to the next base to avoid log spam and retries.
                    }

                    VisualEntry entry;
                    if (_visuals.TryGetValue(s.BaseId, out entry) && entry.GameObject != null)
                    {
                        // Bases (vanilla buildings) do not move after placement, but sync only the
                        // position every time just in case (rotation stays fixed at the value from
                        // creation time = buildings never change orientation).
                        entry.GameObject.transform.position = s.Position;
                        continue;
                    }
                    if (entry != null)
                    {
                        // The GameObject has been destroyed (by something outside this class). Discard
                        // the stale entry and fall through to the creation path below (Hidden should
                        // already be set, so confirmation is obtained immediately).
                        _visuals.Remove(s.BaseId);
                    }

                    // Task75: do not create the overlay GameObject yet. First only issue the request to
                    // hide the vanilla entity, and wait until we can confirm the sim thread actually set
                    // Building.Flags.Hidden. This structurally eliminates any moment where "the vanilla
                    // entity and the overlay are both visible in the same frame" (the reported
                    // double-rendering bug).
                    if (!BaseHiddenSync.IsHiddenApplied(s.BaseId))
                    {
                        BaseHiddenSync.SetDesired(s.BaseId, true); // Idempotent: may be called every frame while waiting
                        _pendingOverlays.Add(s.BaseId);
                        continue;
                    }

                    // Hidden confirmed. Only now create the overlay (requirement 2, stacking prevention).
                    _pendingOverlays.Remove(s.BaseId);
                    entry = CreateVisual(s, kind, name);
                    if (entry == null)
                    {
                        _failedInstances.Add(s.BaseId);
                        // Creation failed, so the overlay does not represent this base. Leaving it
                        // hidden would make the base entirely invisible, so cancel the hide request and
                        // revert to the default appearance.
                        BaseHiddenSync.SetDesired(s.BaseId, false);
                        continue;
                    }
                    _visuals[s.BaseId] = entry;
                }
                catch (Exception e)
                {
                    ModConfig.LogError("BaseVisuals.Sync: failed to update base " + s.BaseId + ": " + e);
                }
            }

            // Enumerate and destroy ids not in the snapshot (two-phase to avoid modifying the
            // Dictionary while iterating).
            _staleIds.Clear();
            foreach (var kv in _visuals)
            {
                if (!_seenIds.Contains(kv.Key)) _staleIds.Add(kv.Key);
            }
            for (int i = 0; i < _staleIds.Count; i++)
            {
                DestroyVisual(_staleIds[i]);
            }

            // Also release failed ids not in the snapshot (so they are not blocked forever, in case
            // the id gets reused).
            _staleFailedIds.Clear();
            foreach (var failedId in _failedInstances)
            {
                if (!_seenIds.Contains(failedId)) _staleFailedIds.Add(failedId);
            }
            for (int i = 0; i < _staleFailedIds.Count; i++)
            {
                _failedInstances.Remove(_staleFailedIds[i]);
            }

            // Task75: also abort the Hidden-confirmation wait for ids not in the snapshot (so that a
            // base that drops out of Sync's scope, e.g. due to demolition, does not remain waiting
            // forever. Also cancel the hide request that was issued).
            _stalePendingIds.Clear();
            foreach (var pendingId in _pendingOverlays)
            {
                if (!_seenIds.Contains(pendingId)) _stalePendingIds.Add(pendingId);
            }
            for (int i = 0; i < _stalePendingIds.Count; i++)
            {
                BaseHiddenSync.SetDesired(_stalePendingIds[i], false);
                _pendingOverlays.Remove(_stalePendingIds[i]);
            }
        }

        /// <summary>Destroys all tracked overlays (on level unload and when applying assignment
        /// changes; main thread only). AssetAssignPanel/OptionsModelAssignPage call this on every
        /// assignment change (same calling convention as UnitVisuals.DestroyAll: after destruction,
        /// the next Sync's CreateVisual re-resolves the new assignment).</summary>
        public static void DestroyAll()
        {
            try
            {
                foreach (var kv in _visuals)
                {
                    if (kv.Value != null && kv.Value.GameObject != null)
                    {
                        UnityEngine.Object.Destroy(kv.Value.GameObject);
                    }
                    // Task71: a base that loses its overlay reverts to the default model's appearance
                    // (stop hiding it). If the caller re-Syncs immediately afterwards (the assignment
                    // change UI), bases that still have an assignment flip right back to true in the
                    // CreateVisual immediately following, so in practice this is only a flicker.
                    BaseHiddenSync.SetDesired(kv.Key, false);
                }
                // Task75: likewise cancel the hide request for bases still waiting for Hidden
                // confirmation (which have no GameObject yet). If the caller re-Syncs immediately
                // afterwards, they simply start waiting again from SetDesired(true) if the assignment
                // remains (DestroyAll is not a frequently invoked operation, so this re-wait is
                // acceptable — the same policy as the existing flicker tolerance on the overlay side).
                foreach (var pendingId in _pendingOverlays)
                {
                    BaseHiddenSync.SetDesired(pendingId, false);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseVisuals.DestroyAll error: " + e);
            }
            finally
            {
                _visuals.Clear();
                _pendingOverlays.Clear();
                _failedInstances.Clear();
            }
        }

        private static VisualEntry CreateVisual(BaseVisualState s, AssetKind kind, string name)
        {
            try
            {
                Mesh mesh;
                if (!AssetCatalog.TryGetMesh(kind, name, out mesh) || mesh == null)
                {
                    ModConfig.LogError("BaseVisuals.CreateVisual: base " + s.BaseId + " (" + s.Type + ") failed to resolve mesh (" +
                        kind + ":" + name + "), skipping overlay (keeping default appearance)");
                    return null;
                }

                // For the same reason as Task37's UnitVisuals.CreateVisual, never borrow the CS-side
                // Material. Keep the asset's own appearance (texture); do not paint with the faction color.
                Material material;
                if (!UnitMaterialFactory.TryGetAssetMaterial(kind, name, out material) || material == null)
                {
                    ModConfig.LogError("BaseVisuals.CreateVisual: base " + s.BaseId + " (" + s.Type + ") failed to create material, skipping overlay");
                    return null;
                }

                var go = new GameObject("CSWarfrontBaseOverlay_" + s.BaseId);

                // If the mesh pivot is not at the bottom face, the model can appear half-buried in the
                // ground. To keep the root's transform.position exactly at the base's logical
                // coordinates, this offset is applied only to a child ("Model") dedicated to mesh
                // rendering (same technique as UnitVisuals.CreateVisual).
                float pivotOffsetY = -mesh.bounds.min.y;

                GameObject model = new GameObject("Model");
                model.transform.SetParent(go.transform, false);
                model.transform.localPosition = new Vector3(0f, pivotOffsetY, 0f);
                MeshFilter filter = model.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                MeshRenderer renderer = model.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;

                go.transform.position = s.Position;
                // Align the orientation with the same conversion as Building.m_angle (radians),
                // matching the base game's building-rotation convention (equivalent to
                // BuildingAI.RenderInstance/RefreshInstance; confirmed by disassembling
                // Assembly-CSharp.dll with ildasm to be Quaternion.AngleAxis(m_angle * 57.29578f,
                // Vector3.down), where 57.29578 = Mathf.Rad2Deg). This way the overlay lies on top of
                // the default model (the vanilla building) with the same orientation.
                go.transform.rotation = Quaternion.AngleAxis(s.Angle * Mathf.Rad2Deg, Vector3.down);

                // The overlay is purely visual. It deliberately has no collision (click selection) —
                // base selection and the info panel (BaseInfoPanel) operate via the vanilla building's
                // collider / selection through CityServiceWorldInfoPanel, and the requirement is to
                // never modify or take over that path (capture and the panel keep working as before).

                ModConfig.Log("BaseVisuals: created overlay for base " + s.BaseId + " (" + s.Type + ") faction=" + s.FactionId +
                    " asset=" + kind + ":" + name);

                return new VisualEntry { GameObject = go };
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseVisuals.CreateVisual: base " + s.BaseId + " error: " + e);
                return null;
            }
        }

        private static void DestroyVisual(ushort baseId)
        {
            try
            {
                VisualEntry entry;
                if (_visuals.TryGetValue(baseId, out entry))
                {
                    if (entry != null && entry.GameObject != null)
                    {
                        UnityEngine.Object.Destroy(entry.GameObject);
                    }
                    _visuals.Remove(baseId);
                    // Task71: a base that lost its overlay reverts to the default model's appearance (stop hiding it).
                    BaseHiddenSync.SetDesired(baseId, false);
                    ModConfig.Log("BaseVisuals: destroyed overlay for base " + baseId);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseVisuals.DestroyVisual: base " + baseId + " error: " + e);
            }
        }
    }
}
