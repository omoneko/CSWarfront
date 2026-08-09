using System;
using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// Temporarily blocks road segments according to the combat zones (State.CombatZones) so that
    /// civilian vehicles/pedestrians detour around them (Task54). Sim-thread only (expected to be
    /// called from MilitaryManager.OnSimTick, same thread boundary as RoadGraphBuilder).
    ///
    /// Findings verified in Part 0 (confirmed by decompiling Assembly-CSharp.dll with ilspycmd):
    ///  - The originally proposed NetSegment.Flags.Blocked is NOT used. Reasons:
    ///     (a) In PathFind.ProcessItemCosts (line 1087), Blocked is only a weak penalty ("+0.1 to
    ///         comparisonValue, and only when the lane is Vehicle/TransportVehicle"), not a hard
    ///         exclusion. Moreover, pedestrian/bicycle pathfinding (ProcessItemPedBicycle) never
    ///         reads Blocked at all, so pedestrians would not detour whatsoever.
    ///     (b) RoadBaseAI.SimulationStep (lines 1411-1421) automatically clears the Blocked flag on
    ///         every pass unless the segment's m_trafficBuffer is ushort.MaxValue (i.e. it is truly
    ///         gridlocked by actual congestion). That pass runs roughly every 256 frames per segment,
    ///         about once every 0.094 in-game hours (segments are group-partitioned by
    ///         `m_currentFrameIndex & 0xFF` in NetManager.SimulationStepImpl). So even if we set
    ///         Blocked externally, the game clears it right away unless it judges the segment congested.
    ///  - NetSegment.Flags.PathFailed is used instead. Reasons:
    ///     (a) PathFind.m_disableMask = Collapsed | PathFailed. At the top of both ProcessItemCosts
    ///         (line 916) and ProcessItemPedBicycle (line 1133) there is a hard exclusion: "if any bit
    ///         of this mask is set, the segment is never considered as a candidate". This affects both
    ///         vehicles and pedestrians.
    ///     (b) A search across the full decompilation of NetSegment/NetAI/RoadBaseAI/NetTool/
    ///         NetManager/PathManager/PathFind found no code that ever writes PathFailed (only the
    ///         enum declaration and the disableMask reference). Unlike Blocked/Flooded there is no
    ///         per-tick auto-overwrite competing with us (caveat: not every assembly was exhaustively
    ///         checked, so this is only a "none found" guarantee. As a precaution we defend with the
    ///         per-tick re-assert described below).
    ///     (c) PassengerCarAI/CitizenAI (civilian vehicles and citizens) both call CreatePath with
    ///         ignoreClosed: false (EventClosed is honored) and all other ignore* left at their
    ///         defaults of false, so there is no path that bypasses the disableMask exclusion
    ///         (ignoreClosed is specific to EventClosed and unrelated to PathFailed).
    ///  - Known limitation (per requirements, accepted because it is not judged dangerous): vehicles
    ///     and pedestrians already traveling on/crossing a segment do not turn back the instant the
    ///     flag is set, because PathManager has no visible mechanism to "re-validate an existing path
    ///     and discard the unexecuted remainder" (the flag takes effect the next time that unit
    ///     computes a new path). This matches real-world temporary closures where "a car already
    ///     mid-crossing finishes crossing", and given the generous ZoneLingerHours=2h closure duration
    ///     it works as an effective detour in practice.
    ///  - After changing the flag, NetManager.UpdateSegment(segmentID) is called (the same convention
    ///     NetTool adopts after copying similar flags. PathFind reads NetManager's raw buffers
    ///     directly, so this is not strictly required for pathfinding itself, but we follow it to keep
    ///     adjacent-node update markers etc. consistent).
    /// </summary>
    internal static class CombatRoadBlocker
    {
        /// <summary>The flag this mod uses to block roads. To avoid ever touching pre-existing
        /// Blocked bits from other mods/vanilla, only the PathFailed bit we set ourselves is targeted
        /// (combined with the ownership check "never touch a segment that already has this bit set"
        /// to err on the safe side).</summary>
        private const NetSegment.Flags BlockFlag = NetSegment.Flags.PathFailed;

        /// <summary>Combat-zone rescan interval (in-game hours). Since we linearly scan all segments,
        /// we follow the same "throttle" pattern as the periodic rebuilds of RoadGraphBuilder/
        /// CoverMapBuilder.</summary>
        public const float BlockUpdateIntervalHours = 0.25f;

        /// <summary>Upper bound on the number of simultaneously blocked segments (safety valve).
        /// Even if a huge combat zone overlaps a vast road network, this keeps the per-tick scan/apply
        /// cost and log volume constant.</summary>
        public const int MaxBlockedSegments = 400;

        // Set of segment IDs this mod currently considers "blocked by us" (sim-thread only, no lock
        // needed = only touched from inside MilitaryManager.OnSimTick's _stateLock). Segments that
        // already had BlockedFlag set (i.e. originating from another mod/vanilla) are NEVER added
        // here = never touched.
        private static readonly HashSet<ushort> _owned = new HashSet<ushort>();

        private static float _accum;

        // Failure-log throttling (same pattern as RoadGraphBuilder).
        private static bool _failureAlreadyLogged;

        /// <summary>Number of segments currently blocked by this mod (for diagnostics/tests).</summary>
        public static int OwnedCount => _owned.Count;

        /// <summary>
        /// Called every tick from MilitaryManager.OnSimTick.
        ///  1) Every tick: re-assert BlockFlag on the blocked segments we currently own (defense so
        ///     that even if some vanilla-side writer we failed to find does exist, it gets overwritten
        ///     immediately. Owned segments are capped at MaxBlockedSegments, so doing this every tick
        ///     is cheap).
        ///  2) Every BlockUpdateIntervalHours: linearly scan all road segments to compute the "set of
        ///     segments that should currently be blocked" and apply only the delta (additions/removals).
        /// </summary>
        public static void Advance(WarState state, float dt)
        {
            try
            {
                if (!Singleton<NetManager>.exists)
                {
                    return; // NetManager not ready. If _owned is empty nothing has been done yet, so this is safe.
                }

                NetManager nm = Singleton<NetManager>.instance;
                NetSegment[] segments = nm.m_segments.m_buffer;

                ReassertOwned(nm, segments);

                _accum += dt;
                if (_accum < BlockUpdateIntervalHours) return;
                _accum -= BlockUpdateIntervalHours;
                if (_accum < 0f) _accum = 0f;

                if (state.CombatZones.Zones.Count == 0 && _owned.Count == 0) return; // nothing to do

                NetNode[] nodes = nm.m_nodes.m_buffer;
                HashSet<ushort> desired = ComputeDesired(state, segments, nodes);
                ApplyDelta(nm, segments, desired);

                _failureAlreadyLogged = false;
            }
            catch (Exception e)
            {
                if (!_failureAlreadyLogged)
                {
                    ModConfig.LogError("CombatRoadBlocker.Advance exception: " + e);
                    _failureAlreadyLogged = true;
                }
            }
        }

        /// <summary>Re-sets BlockFlag on the currently owned segments every tick. Segments that are
        /// no longer Created (destroyed/deleted) are silently dropped from the owned set.</summary>
        private static void ReassertOwned(NetManager nm, NetSegment[] segments)
        {
            if (_owned.Count == 0) return;

            List<ushort> stale = null;
            foreach (ushort id in _owned)
            {
                if (id >= segments.Length || (segments[id].m_flags & NetSegment.Flags.Created) == 0)
                {
                    (stale ?? (stale = new List<ushort>())).Add(id);
                    continue;
                }
                segments[id].m_flags |= BlockFlag;
            }

            if (stale != null)
            {
                for (int i = 0; i < stale.Count; i++) _owned.Remove(stale[i]);
            }
        }

        /// <summary>Computes the set of road segments (same Service.Road && Created filter as
        /// RoadGraphBuilder) whose midpoint (average of start/end node positions) falls within the
        /// radius of any combat zone. Once MaxBlockedSegments is reached the rest are ignored (the
        /// scan itself still runs to the end so the logging stays consistent).</summary>
        /// <summary>Task116 (Workshop reports "the game freezes for a second during intense
        /// battles"): every blocked/unblocked segment costs an UpdateSegment call, which triggers
        /// pathfinding/render updates for all traffic touching it. Combat-zone centers wobble as new
        /// reports merge in, so without damping the desired set churns — segments oscillate in and
        /// out every update and mass re-path storms cause the hitches. Two dampers:
        ///  - a segment is unblocked only after it has been out of every zone for this many
        ///    consecutive update passes (block/unblock oscillation suppression);</summary>
        private const int UnblockGracePasses = 2;

        /// <summary>- and at most this many UpdateSegment calls are issued per pass (the rest simply
        /// wait for the next pass, spreading the spike over time).</summary>
        private const int MaxSegmentUpdatesPerPass = 48;

        /// <summary>Consecutive passes each owned segment has been absent from the desired set.</summary>
        private static readonly Dictionary<ushort, int> _undesiredPasses = new Dictionary<ushort, int>();

        private static HashSet<ushort> ComputeDesired(WarState state, NetSegment[] segments, NetNode[] nodes)
        {
            var desired = new HashSet<ushort>();
            IList<CombatZone> zones = state.CombatZones.Zones;
            if (zones.Count == 0) return desired;

            for (int i = 0; i < segments.Length; i++)
            {
                if (desired.Count >= MaxBlockedSegments) break;
                if ((segments[i].m_flags & NetSegment.Flags.Created) == 0) continue;

                NetInfo info = segments[i].Info;
                if (info == null || info.m_class == null) continue;
                if (info.m_class.m_service != ItemClass.Service.Road) continue;

                ushort startNode = segments[i].m_startNode;
                ushort endNode = segments[i].m_endNode;
                if (startNode >= nodes.Length || endNode >= nodes.Length) continue;
                if ((nodes[startNode].m_flags & NetNode.Flags.Created) == 0) continue;
                if ((nodes[endNode].m_flags & NetNode.Flags.Created) == 0) continue;

                UnityEngine.Vector3 sp = nodes[startNode].m_position;
                UnityEngine.Vector3 ep = nodes[endNode].m_position;
                var mid = new WorldPos((sp.x + ep.x) * 0.5f, (sp.y + ep.y) * 0.5f, (sp.z + ep.z) * 0.5f);

                for (int z = 0; z < zones.Count; z++)
                {
                    if (mid.HorizontalDistanceTo(zones[z].Center) <= zones[z].Radius)
                    {
                        desired.Add((ushort)i);
                        break;
                    }
                }
            }
            return desired;
        }

        /// <summary>Moves the owned set (_owned) toward desired: blocks only what needs to be added
        /// and unblocks only what is no longer needed. PathFailed bits already set by someone else are
        /// never touched (we never write to bits we do not own = never steal, never clear). Logs a
        /// one-line summary only when something changed.</summary>
        private static void ApplyDelta(NetManager nm, NetSegment[] segments, HashSet<ushort> desired)
        {
            int added = 0;
            int removed = 0;
            int updates = 0; // Task116: UpdateSegment calls issued this pass (spike spreading)

            // Additions: in desired but not in _owned.
            foreach (ushort id in desired)
            {
                if (_owned.Contains(id)) continue;
                if (updates >= MaxSegmentUpdatesPerPass) break; // rest wait for the next pass
                if (id >= segments.Length) continue;
                if ((segments[id].m_flags & BlockFlag) != NetSegment.Flags.None)
                {
                    // Already set (by another mod/vanilla/something that is not leftover from our previous run) = do not claim ownership.
                    continue;
                }
                segments[id].m_flags |= BlockFlag;
                nm.UpdateSegment(id);
                updates++;
                _owned.Add(id);
                added++;
            }

            // Removals: in _owned but out of desired for UnblockGracePasses consecutive passes
            // (Task116: zone centers wobble, so instant unblocking oscillates — see the field docs).
            List<ushort> toRemove = null;
            foreach (ushort id in _owned)
            {
                if (desired.Contains(id))
                {
                    _undesiredPasses.Remove(id);
                    continue;
                }
                int misses;
                _undesiredPasses.TryGetValue(id, out misses);
                misses++;
                if (misses < UnblockGracePasses)
                {
                    _undesiredPasses[id] = misses;
                    continue;
                }
                (toRemove ?? (toRemove = new List<ushort>())).Add(id);
            }
            if (toRemove != null)
            {
                for (int i = 0; i < toRemove.Count; i++)
                {
                    if (updates >= MaxSegmentUpdatesPerPass) break; // rest wait for the next pass
                    ushort id = toRemove[i];
                    if (id < segments.Length)
                    {
                        segments[id].m_flags &= ~BlockFlag;
                        nm.UpdateSegment(id);
                        updates++;
                    }
                    _owned.Remove(id);
                    _undesiredPasses.Remove(id);
                    removed++;
                }
            }

            if (added != 0 || removed != 0)
            {
                ModConfig.Log("CombatRoadBlocker: blocked +" + added + " -" + removed + " total " + _owned.Count);
            }
        }

        /// <summary>
        /// Called right before saving (Task54). Temporarily clears all PathFailed bits this mod has
        /// set so they are not baked into the save data. The owned set (_owned) itself is kept in
        /// memory, and ReblockAfterSave re-sets the same set. The caller
        /// (MilitaryManager.SerializeLocked) invokes this while holding _stateLock, so there is no
        /// race with the sim thread.
        /// </summary>
        public static void UnblockAllForSave()
        {
            try
            {
                if (_owned.Count == 0) return;
                if (!Singleton<NetManager>.exists) return;
                NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
                foreach (ushort id in _owned)
                {
                    if (id < segments.Length) segments[id].m_flags &= ~BlockFlag;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatRoadBlocker.UnblockAllForSave exception: " + e);
            }
        }

        /// <summary>
        /// Re-sets the PathFailed bits removed by UnblockAllForSave. The _owned set is used as-is to
        /// re-establish the blocks (saving is treated as a temporary operation that does not change
        /// logical state).
        ///
        /// Important contract discovered and fixed in Task72: the caller must NOT invoke this
        /// synchronously "right after WarStateSerializer.Serialize returns". Decompiling
        /// SimulationManager.Data.Serialize/LoadingManager.SaveSimulationData/AsyncTask.Execute with
        /// ilspycmd confirmed that vanilla's save order is:
        /// "(1) call every mod's ISerializableDataExtension.OnSaveData() (= WarStateDataExtension.
        /// OnSaveData, which included the old call site of this method) -> (2) only afterwards call
        /// the Serialize of each vanilla manager such as BuildingManager.Data/NetManager.Data (the
        /// code that actually writes NetSegment.m_flags/Building.m_flags to the stream)", and both
        /// (1) and (2) happen back-to-back inside the same AsyncTask.Execute() (fully synchronous
        /// within the seg, no yields). In other words, the old design of "clear inside OnSaveData() ->
        /// restore immediately in a finally inside OnSaveData()" restored the bits BEFORE vanilla read
        /// NetSegment.m_flags in (2), so the PathFailed bits were in fact still being baked into the
        /// save file (the countermeasure was meaningless). The correct way to call this is for
        /// MilitaryManagerPersistence.SerializeLocked to enqueue it via
        /// Singleton&lt;SimulationManager&gt;.instance.AddAction as "the next action after the current
        /// Saving AsyncTask has fully completed". The `while(m_hasActions){...}` loop at the top of
        /// SimulationManager.SimulationStep sets m_hasActions back to true when AddAction is called
        /// from within a running action, and continues executing that action in the same frame and
        /// before the regular OnSimTick, so we reliably get the timing of "immediately after vanilla
        /// has completely finished writing NetSegment.m_flags".
        /// </summary>
        public static void ReblockAfterSave()
        {
            try
            {
                if (_owned.Count == 0) return;
                if (!Singleton<NetManager>.exists) return;
                NetManager nm = Singleton<NetManager>.instance;
                NetSegment[] segments = nm.m_segments.m_buffer;
                ReassertOwned(nm, segments);
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatRoadBlocker.ReblockAfterSave exception: " + e);
            }
        }

        /// <summary>
        /// On level unload (called from MilitaryManager.Reset()): releases all owned blocks, then
        /// clears internal state. Since the level may be mid-teardown with NetManager already
        /// invalidated, a failed unblock is not propagated as an exception (it is only logged).
        /// (A failed unblock during unload is itself harmless = the level about to be loaded contains
        /// none of this mod's flags.)
        /// </summary>
        public static void Reset()
        {
            try
            {
                if (_owned.Count > 0 && Singleton<NetManager>.exists)
                {
                    NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
                    foreach (ushort id in _owned)
                    {
                        if (id < segments.Length) segments[id].m_flags &= ~BlockFlag;
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatRoadBlocker.Reset: unblock-on-unload failed (harmless, level is tearing down): " + e);
            }
            finally
            {
                _owned.Clear();
                _undesiredPasses.Clear(); // Task116
                _accum = 0f;
                _failureAlreadyLogged = false;
            }
        }
    }
}
