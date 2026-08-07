namespace CSWarfront.Core
{
    /// <summary>
    /// Task53: the fix for "units sinking into the ground". A thin UnityEngine-free seam for sampling
    /// not the raw terrain height but the "visual" ground surface after road/building construction
    /// (including roads on embankments, terrain modified by construction, bridges, etc.) — the same
    /// supply pattern as RoadGraph/CoverMap.
    ///
    /// Core only consumes this interface and knows nothing of the implementation. The Game layer
    /// (src/CSWarfront/Game/SurfaceHeightSampler.cs) implements it against CS's TerrainManager.
    ///
    /// Task53 hardening: the old SampleHeight(float,float):float returned 0f when the TerrainManager
    /// was momentarily uncreated or threw. Map ground is not necessarily near 0f (about 270 in
    /// practice), so adopting that 0f as the unit's Y made a visible bug — a one-tick teleport far
    /// below the ground. The Try form lets the caller (MovementStep) explicitly distinguish "sampling
    /// failed", fall back to Y interpolation on failure, and never adopt a failure value into Y.
    /// </summary>
    public interface IHeightSampler
    {
        /// <summary>Attempts to get the ground-surface height (y) at map coordinates (x, z). The
        /// caller (MovementStep) is expected to call from the sim thread (the Game-layer
        /// implementation reads the TerrainManager). Returns true on success, storing the value in
        /// height. Returns false on failure (terrain system uncreated, exceptions, etc.). On false the
        /// caller must never use height's value (keep the existing Y interpolation/snap
        /// behavior).</summary>
        bool TrySampleHeight(float x, float z, out float height);
    }
}
