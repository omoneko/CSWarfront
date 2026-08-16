using System;
using ColossalFramework;
using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// Builds the cover map (Core.CoverMap) from CS's buildings (BuildingManager) (sim thread only,
    /// Task44). Same supply pattern as RoadGraphBuilder: read the CS buffers on the sim thread,
    /// repack them into UnityEngine-independent POCOs, and hand them to WarState.Cover.
    ///
    /// Verified signatures (confirmed via reflection on Assembly-CSharp.dll, Task44):
    ///  - BuildingManager.m_buildings: Array16&lt;Building&gt; (field m_buffer is Building[])
    ///  - Building.m_flags: Building.Flags ([Flags] enum, has Created/Deleted/Hidden)
    ///  - Building.Info: BuildingInfo property (getter, already used by BasePlacementWatcher)
    ///  - Building.m_position: UnityEngine.Vector3
    ///  - BuildingInfo.m_cellWidth / m_cellLength: both System.Int32 (1 cell = 8 meters)
    ///
    /// About props (Task44, an alternative the requirements permit): PropManager.m_props /
    /// PropInstance.m_flags / PropInstance.Info (PropInfo) / PropInstance.Position could all be
    /// confirmed to exist via reflection, but PropInfo has no numeric field describing size
    /// (nothing equivalent to radius/width/length), and the actual size can only be derived from the
    /// bounds of the UnityEngine.Mesh (m_mesh). A Mesh is an asset-side Unity object, which falls
    /// outside the premise of "reading CS entity data" on the sim thread (this project's rules
    /// forbid touching Unity objects on the sim thread). For this task, therefore, props are out of
    /// scope and only buildings are registered as cover
    /// (this corresponds to the "props turn out to be impractical" case explicitly noted in the spec).
    /// </summary>
    internal static class CoverMapBuilder
    {
        /// <summary>Real-world size of one cell (meters, per CS's building grid).</summary>
        private const float MetersPerCell = 8f;

        /// <summary>Default radius when BuildingInfo.m_cellWidth/m_cellLength are unavailable.</summary>
        private const float FallbackRadius = 8f;

        /// <summary>Cap on the total number of registered cover points (Task44). A defensive cap that
        /// keeps the per-tick cover-search cost constant even in huge cities. Once the cap is reached,
        /// further buildings are simply ignored.</summary>
        public const int MaxCoverPoints = 4000;

        // Same throttling pattern as RoadGraphBuilder: prevents continuous output of build-failure logs.
        private static bool _failureAlreadyLogged;

        /// <summary>
        /// Builds a CoverMap from BuildingManager's building buffer. Returns null on failure (the
        /// caller must keep the existing map).
        /// </summary>
        public static CoverMap Build()
        {
            try
            {
                if (!Singleton<BuildingManager>.exists)
                {
                    if (!_failureAlreadyLogged)
                    {
                        ModConfig.LogError("CoverMapBuilder.Build: BuildingManager not ready; skip");
                        _failureAlreadyLogged = true;
                    }
                    return null;
                }

                Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;

                var map = new CoverMap();
                int accepted = 0;
                int skippedNotCreated = 0;
                int skippedOwnBase = 0;
                int skippedNoInfo = 0;
                int cappedOut = 0;

                for (int i = 0; i < buildings.Length; i++)
                {
                    if (map.Count >= MaxCoverPoints)
                    {
                        cappedOut = buildings.Length - i;
                        break;
                    }

                    Building b = buildings[i];
                    if ((b.m_flags & Building.Flags.Created) == 0)
                    {
                        skippedNotCreated++;
                        continue;
                    }

                    BuildingInfo info = b.Info;
                    if (info == null)
                    {
                        skippedNoInfo++;
                        continue;
                    }

                    // Our own military base buildings (army/navy/air/missile, Task61) are not treated
                    // as cover (a cover point standing directly on top of / right next to a base is
                    // pointless).
                    // Task82: the duplicated-prefab mechanism in the electricity tab
                    // (WarfrontBasePrefab.IsOwnBase, matching by reference identity) was removed, so as
                    // the replacement rule we use exactly the same criterion as
                    // BasePlacementWatcher/BaseHiddenSync — whether Info.name matches one of the
                    // Options-designated buildings (BaseBuildingDesignation). This is the simplest
                    // criterion, consistent with the other base-detection code, and there is no need to
                    // bring new state in here (a cache limited to "base ids actually registered", like
                    // BasePlacementWatcher._baseInfoNames). It is slightly broader by definition
                    // (= buildings whose asset name is currently designated in Options are excluded
                    // from cover even if they are not yet registered with BasePlacementWatcher as a
                    // logical base), but the cover map is an approximate obstacle list, and
                    // over-excluding prospective base sites errs on the safe side (mistakenly treating
                    // a base as cover would do more real harm).
                    BaseType ignoredType;
                    if (BaseBuildingDesignation.TryMatch(info.name, out ignoredType))
                    {
                        skippedOwnBase++;
                        continue;
                    }

                    float radius = RadiusFor(info);
                    var pos = b.m_position;
                    map.Add(new WorldPos(pos.x, pos.y, pos.z), radius, (ushort)i); // Task139: id carried for the garrison flag
                    accepted++;
                }

                ModConfig.Log("CoverMapBuilder: built points=" + map.Count +
                    " acceptedBuildings=" + accepted +
                    " skippedNotCreated=" + skippedNotCreated +
                    " skippedNoInfo=" + skippedNoInfo +
                    " skippedOwnBase=" + skippedOwnBase +
                    " cappedOut=" + cappedOut +
                    " props=0(not supported, see CoverMapBuilder doc comment)");
                _failureAlreadyLogged = false;
                return map;
            }
            catch (Exception e)
            {
                if (!_failureAlreadyLogged)
                {
                    ModConfig.LogError("CoverMapBuilder.Build exception: " + e);
                    _failureAlreadyLogged = true;
                }
                return null;
            }
        }

        /// <summary>Derives an approximate radius (map units) from BuildingInfo.m_cellWidth/
        /// m_cellLength. Falls back to FallbackRadius when unavailable or non-positive.</summary>
        private static float RadiusFor(BuildingInfo info)
        {
            int cellWidth = info.m_cellWidth;
            int cellLength = info.m_cellLength;
            if (cellWidth <= 0 || cellLength <= 0) return FallbackRadius;

            float widthMeters = cellWidth * MetersPerCell;
            float lengthMeters = cellLength * MetersPerCell;
            // Take the average of half the width and half the depth (= distance from center to edge)
            // as the "approximate radius".
            return (widthMeters + lengthMeters) / 4f;
        }
    }
}
