namespace CSWarfront.Core
{
    /// <summary>
    /// Task61: a thin UnityEngine-free seam for judging naval units' (Domain.Sea) movement range and
    /// water-surface height (the same supply pattern as IHeightSampler/RoadGraph/CoverMap). Core only
    /// consumes this interface and knows nothing of the implementation. The Game layer
    /// (src/CSWarfront/Game/WaterSampler.cs) implements it against CS's TerrainManager.
    ///
    /// A known MVP limitation: naval movement (MovementStep's Sea branch) does no on-water
    /// pathfinding (A* etc.) at all — it is simple straight-line movement. When the straight line's
    /// next step would tread on land the unit stops in place (see the caller, MovementStep), so
    /// targets behind capes and peninsulas can be physically unreachable (navy-only pathfinding is
    /// future work).
    /// </summary>
    public interface IWaterSampler
    {
        /// <summary>Attempts to get the water-surface height (y) at map coordinates (x, z). Returns
        /// false where there is no water or the check fails. On false the caller must never use
        /// level's value.</summary>
        bool TrySampleWaterLevel(float x, float z, out float level);

        /// <summary>Whether map coordinates (x, z) are water surface (navigable). Unlike
        /// IHeightSampler this is not Try-form: "cannot judge" also returns false (= not water,
        /// treated as land), so a naval unit getting stalled in place is chosen as the safe side over
        /// mistakenly treading onto unknown terrain (see MovementStep's Sea branch).</summary>
        bool IsWater(float x, float z);
    }
}
