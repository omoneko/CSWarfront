using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task114 (Workshop request "a UI panel to reset all destroyed defensive positions"): sim-side
    /// handling of the Save Defense Layout / Rebuild Defenses buttons (MilitaryManager partial,
    /// split out due to the 500-line limit).
    ///
    /// The UI (MilitaryBuildPanel, main thread) only raises request flags; ProcessDefenseLayoutRequests
    /// drains them in OnSimTick on the sim thread (the same handoff pattern as the trench lines):
    ///  - Register: snapshot every eligible fortification (DefenseLayout.IsEligible — the build
    ///    faction's live fortifications plus all trenches) into WarState.DefenseLayout, reading each
    ///    building's angle from the CS building buffer. Persisted with the save (v11).
    ///  - Rebuild: for each saved entry that is no longer satisfied, demolish any defunct wreck still
    ///    standing there (a 0-HP bunker keeps its building with Owner nulled), pay the normal
    ///    construction cost from the city's funds, and re-place the currently designated asset for
    ///    that type via BuildingManager.CreateBuilding (same road-requirement bypass as the trench
    ///    line). BasePlacementWatcher then registers the new building as a logical facility as usual,
    ///    owned by the current build faction (trenches stay unowned by design) — so rebuilding is
    ///    intended to be done with the same build faction that registered the layout.
    ///
    /// Results are reported back to the UI through a small toast queue the panel polls every frame
    /// (sim thread must not touch Unity UI directly).
    /// </summary>
    public static partial class MilitaryManager
    {
        private static bool _registerDefenseRequested;
        private static bool _rebuildDefenseRequested;
        private static readonly List<string> _uiToasts = new List<string>();

        /// <summary>Called from the main thread (MilitaryBuildPanel).</summary>
        public static void RequestRegisterDefenseLayout()
        {
            lock (_stateLock) { _registerDefenseRequested = true; }
        }

        /// <summary>Called from the main thread (MilitaryBuildPanel).</summary>
        public static void RequestRebuildDefenses()
        {
            lock (_stateLock) { _rebuildDefenseRequested = true; }
        }

        /// <summary>Main thread (MilitaryBuildPanel.Update): pops one queued result message, if any.</summary>
        public static bool TryDequeueUiToast(out string message)
        {
            lock (_stateLock)
            {
                if (_uiToasts.Count == 0) { message = null; return false; }
                message = _uiToasts[0];
                _uiToasts.RemoveAt(0);
                return true;
            }
        }

        /// <summary>Sim thread only, already inside _stateLock.</summary>
        private static void QueueUiToast(string message)
        {
            _uiToasts.Add(message);
        }

        /// <summary>Sim thread (OnSimTick, inside _stateLock): drains the pending requests.</summary>
        private static void ProcessDefenseLayoutRequests()
        {
            if (State == null) return;
            if (_registerDefenseRequested)
            {
                _registerDefenseRequested = false;
                RegisterDefenseLayout();
            }
            if (_rebuildDefenseRequested)
            {
                _rebuildDefenseRequested = false;
                RebuildDefenses();
            }
        }

        private static void RegisterDefenseLayout()
        {
            BuildingManager bm = Singleton<BuildingManager>.instance;
            Building[] buffer = bm.m_buildings.m_buffer;
            byte factionId = WarfrontSettings.BuildFactionId;

            State.DefenseLayout.Clear();
            for (int i = 0; i < State.Bases.Count; i++)
            {
                MilitaryBase b = State.Bases[i];
                if (!DefenseLayout.IsEligible(b, factionId)) continue;

                float angle = 0f;
                if (b.BaseId < buffer.Length && (buffer[b.BaseId].m_flags & Building.Flags.Created) != 0)
                    angle = buffer[b.BaseId].m_angle;

                State.DefenseLayout.Add(new DefenseLayoutEntry
                {
                    Type = b.Type,
                    Position = b.Position,
                    Angle = angle
                });
            }

            QueueUiToast(string.Format(WarfrontStrings.BuildPanel_ToastLayoutSaved, State.DefenseLayout.Count));
            ModConfig.Log("DefenseLayout: registered " + State.DefenseLayout.Count +
                " fortification position(s) for faction " + factionId);
        }

        private static void RebuildDefenses()
        {
            if (State.DefenseLayout.Count == 0)
            {
                QueueUiToast(WarfrontStrings.BuildPanel_ToastNoLayout);
                return;
            }

            List<DefenseLayoutEntry> missing = DefenseLayout.FindMissing(State);
            if (missing.Count == 0)
            {
                QueueUiToast(WarfrontStrings.BuildPanel_ToastNothingMissing);
                return;
            }

            BuildingManager bm = Singleton<BuildingManager>.instance;
            SimulationManager sm = Singleton<SimulationManager>.instance;
            EconomyManager em = Singleton<EconomyManager>.instance;
            TerrainManager tm = Singleton<TerrainManager>.instance;

            int rebuilt = 0;
            int skippedNoAsset = 0;
            bool outOfMoney = false;
            for (int i = 0; i < missing.Count; i++)
            {
                DefenseLayoutEntry e = missing[i];

                string assetName;
                if (!BaseBuildingDesignation.TryGet(e.Type, out assetName)) { skippedNoAsset++; continue; }
                BuildingInfo info = PrefabCollection<BuildingInfo>.FindLoaded(assetName);
                if (info == null) { skippedNoAsset++; continue; }

                // A defunct wreck still standing at the spot (dead bunker/artillery post) blocks the
                // footprint — demolish it first. Its logical record is removed by
                // BasePlacementWatcher via the release event as usual.
                ushort blocker;
                if (DefenseLayout.TryFindBlocker(State, e, out blocker) &&
                    blocker < bm.m_buildings.m_buffer.Length &&
                    (bm.m_buildings.m_buffer[blocker].m_flags & Building.Flags.Created) != 0)
                {
                    bm.ReleaseBuilding(blocker);
                }

                // Normal construction cost from the city's funds, same as the trench line (stop when
                // it can no longer be paid).
                int cost = info.m_buildingAI.GetConstructionCost();
                if (cost > 0)
                {
                    if (em.PeekResource(EconomyManager.Resource.Construction, cost) < cost)
                    {
                        outOfMoney = true;
                        break;
                    }
                    em.FetchResource(EconomyManager.Resource.Construction, cost, info.m_class);
                }

                Vector3 pos = new Vector3(e.Position.X, e.Position.Y, e.Position.Z);
                pos.y = tm.SampleDetailHeight(pos);

                ushort id;
                if (bm.CreateBuilding(out id, ref sm.m_randomizer, info, pos, e.Angle, 0, sm.m_currentBuildIndex))
                {
                    sm.m_currentBuildIndex++;
                    rebuilt++;
                    // Registration as a logical facility happens via EventBuildingCreated →
                    // BasePlacementWatcher (owner = current build faction; trenches unowned).
                }
            }

            QueueUiToast(string.Format(
                outOfMoney ? WarfrontStrings.BuildPanel_ToastRebuiltOutOfMoney : WarfrontStrings.BuildPanel_ToastRebuilt,
                rebuilt, missing.Count));
            ModConfig.Log("DefenseLayout: rebuilt " + rebuilt + "/" + missing.Count +
                " missing position(s)" + (outOfMoney ? " (out of money)" : "") +
                (skippedNoAsset > 0 ? " (" + skippedNoAsset + " skipped: no designated/loaded asset)" : ""));
        }
    }
}
