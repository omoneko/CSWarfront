using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task106: sim-side handling of trench line placement (MilitaryManager partial, split out due to
    /// the 500-line limit).
    ///
    /// When the UI (TrenchLineTargeting, main thread) confirms two points, RequestTrenchLine pushes
    /// them onto a queue, and ProcessPendingTrenchLines in OnSimTick drains it on the sim thread:
    ///   - Spawns the Trench building asset designated in Options directly along the straight line
    ///     from start to end at TrenchSegmentSpacing intervals via BuildingManager.CreateBuilding
    ///     (since the vanilla placement tool is not used, the "must be placed adjacent to a road"
    ///     requirement is completely bypassed). Orientation is auto-rotated to the line direction.
    ///   - Each segment pays the building's construction cost from the city's funds as usual
    ///     (placement stops when money runs out).
    ///   - Spawned buildings are registered as logical trenches (unaffiliated) by
    ///     BasePlacementWatcher via EventBuildingCreated as usual (no dedicated registration
    ///     handling is needed).
    ///
    /// In addition, SuppressFortificationProblems clears the problem icons (road not connected,
    /// electricity, water, etc.) of registered fortification-type buildings (trenches, bunkers,
    /// artillery positions, supply depots, cargo stations) every tick — treating field
    /// fortifications as not requiring city infrastructure (countermeasure for the
    /// road-not-connected warning).
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>Maximum number of segments placed per line (runaway prevention).</summary>
        public const int MaxTrenchSegmentsPerLine = 64;

        /// <summary>Side length of one building-footprint cell (m). CS's building grid is 8m/cell.</summary>
        private const float CellSizeMeters = 8f;

        private struct TrenchLineRequest { public Vector3 Start, End; }

        // Handoff queue from main thread (UI) to sim thread. Protected by _stateLock.
        private static readonly List<TrenchLineRequest> _pendingTrenchLines = new List<TrenchLineRequest>();

        /// <summary>Called from the main thread (TrenchLineTargeting).</summary>
        public static void RequestTrenchLine(Vector3 start, Vector3 end)
        {
            lock (_stateLock)
            {
                _pendingTrenchLines.Add(new TrenchLineRequest { Start = start, End = end });
            }
        }

        /// <summary>Sim thread (OnSimTick, inside _stateLock): spawns pending trench lines as buildings.</summary>
        private static void ProcessPendingTrenchLines()
        {
            if (_pendingTrenchLines.Count == 0) return;
            List<TrenchLineRequest> requests = new List<TrenchLineRequest>(_pendingTrenchLines);
            _pendingTrenchLines.Clear();

            string assetName;
            if (!BaseBuildingDesignation.TryGet(BaseType.Trench, out assetName))
            {
                ModConfig.Log("TrenchLine: no trench building designated in Options; ignored");
                return;
            }
            BuildingInfo info = PrefabCollection<BuildingInfo>.FindLoaded(assetName);
            if (info == null)
            {
                ModConfig.Log("TrenchLine: designated asset '" + assetName + "' is not loaded; ignored");
                return;
            }

            foreach (TrenchLineRequest req in requests)
                PlaceTrenchLine(info, req.Start, req.End);
        }

        private static void PlaceTrenchLine(BuildingInfo info, Vector3 start, Vector3 end)
        {
            Vector3 delta = end - start;
            delta.y = 0f;
            float length = delta.magnitude;
            Vector3 dir = length > 0.01f ? delta / length : Vector3.forward;

            // Which local axis is the long side depends on the asset, so determine it from the
            // footprint (8m/cell) and use the actual length of that long side as the placement
            // spacing (so segments connect without gaps).
            float sizeX = Mathf.Max(1, info.m_cellWidth) * CellSizeMeters;   // length along local X
            float sizeZ = Mathf.Max(1, info.m_cellLength) * CellSizeMeters;  // length along local Z
            bool longAxisIsX = sizeX > sizeZ;
            float spacing = longAxisIsX ? sizeX : sizeZ;

            // CS building rotation is Quaternion.AngleAxis(m_angle * Rad2Deg, Vector3.down)
            // (confirmed in the IL of Building.CalculateMeshRotation). That is, it rotates
            // "-m_angle around the up axis", so:
            //   local +Z → (-sin θ, 0, cos θ)   … if the long axis is Z, θ = Atan2(-dir.x, dir.z)
            //   local +X → ( cos θ, 0, sin θ)   … if the long axis is X, θ = Atan2(dir.z, dir.x)
            // aligns the long axis with the line direction (the previous Atan2(dir.x, dir.z)
            // pointed in a different direction with the X component flipped).
            float angle = longAxisIsX ? Mathf.Atan2(dir.z, dir.x) : Mathf.Atan2(-dir.x, dir.z);

            // Cover the drawn line segment itself with trenches (place each segment's center along
            // the segment at spacing intervals).
            int segments = Mathf.Min(MaxTrenchSegmentsPerLine,
                Mathf.Max(1, Mathf.RoundToInt(length / spacing)));

            BuildingManager bm = Singleton<BuildingManager>.instance;
            SimulationManager sm = Singleton<SimulationManager>.instance;
            EconomyManager em = Singleton<EconomyManager>.instance;
            TerrainManager tm = Singleton<TerrainManager>.instance;

            int placed = 0;
            for (int i = 0; i < segments; i++)
            {
                Vector3 pos = start + dir * ((i + 0.5f) * spacing);
                pos.y = tm.SampleDetailHeight(pos);

                // Construction cost is paid from the city's funds as usual (stop when it can no
                // longer be paid).
                int cost = info.m_buildingAI.GetConstructionCost();
                if (cost > 0)
                {
                    if (em.PeekResource(EconomyManager.Resource.Construction, cost) < cost)
                    {
                        ModConfig.Log("TrenchLine: out of money after " + placed + " segment(s)");
                        break;
                    }
                    em.FetchResource(EconomyManager.Resource.Construction, cost, info.m_class);
                }

                ushort id;
                if (bm.CreateBuilding(out id, ref sm.m_randomizer, info, pos, angle, 0, sm.m_currentBuildIndex))
                {
                    sm.m_currentBuildIndex++;
                    placed++;
                    // Registration itself happens via EventBuildingCreated → BasePlacementWatcher
                    // (registered as an unaffiliated trench).
                }
            }
            ModConfig.Log("TrenchLine: placed " + placed + " trench segment(s) from (" +
                start.x.ToString("0") + "," + start.z.ToString("0") + ") to (" +
                end.x.ToString("0") + "," + end.z.ToString("0") + ") footprint=" +
                info.m_cellWidth + "x" + info.m_cellLength + " cells, longAxis=" +
                (longAxisIsX ? "X" : "Z") + ", spacing=" + spacing.ToString("0") + "m");
        }

        /// <summary>Sim thread (OnSimTick, inside _stateLock): clears problem icons (road not
        /// connected, electricity, water, etc.) of fortification-type buildings. Field
        /// fortifications are treated as not requiring city infrastructure (Task106).
        /// Targets are registered logical facilities (State.Bases) for which
        /// FortificationRules.IsFortification is true.</summary>
        private static void SuppressFortificationProblems()
        {
            if (State == null) return;
            BuildingManager bm = Singleton<BuildingManager>.instance;
            Building[] buffer = bm.m_buildings.m_buffer;
            Notification.ProblemStruct none = Notification.ProblemStruct.None;
            for (int i = 0; i < State.Bases.Count; i++)
            {
                MilitaryBase b = State.Bases[i];
                if (!FortificationRules.IsFortification(b.Type)) continue;
                if (b.BaseId >= buffer.Length) continue;
                if ((buffer[b.BaseId].m_flags & Building.Flags.Created) == 0) continue;

                Notification.ProblemStruct old = buffer[b.BaseId].m_problems;
                if (old == none) continue;
                buffer[b.BaseId].m_problems = none;
                // Important: problem icons are baked into render groups (Notification.
                // PopulateGroupData), so just rewriting m_problems does not remove the on-screen
                // icon. As vanilla AIs do, call UpdateNotifications to refresh the group (calling
                // from the sim thread is correct).
                bm.UpdateNotifications(b.BaseId, old, none);
            }
        }
    }
}
