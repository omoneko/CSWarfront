using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Game-layer implementation of Core.IHeightSampler (Task53, fix for the "units sink into the
    /// ground" bug). Queries CS's TerrainManager and returns the "visual" ground surface as it looks
    /// after road/building construction (including roads on embankments, terrain modified by
    /// construction, bridges, etc.).
    ///
    /// Threading note: intended to be called from MovementStep.Advance (the sim thread of
    /// MilitaryManager.OnSimTick). Same premise as DevelopmentSampler/RoadGraphBuilder: calling
    /// TerrainManager's read-only APIs on the sim thread does not violate CS's constraints (what is
    /// main-thread-only is GameObject creation/rendering/UI operations).
    ///
    /// Verified signatures (re-confirmed by decompiling Assembly-CSharp.dll with ILSpy, Task55):
    ///  - TerrainManager.SampleDetailHeight(Vector3 worldPos): float ← this is the one we adopt.
    ///    Implementation (ILSpy decompilation result):
    ///      float x = worldPos.x / 4f + 2160f;
    ///      float z = worldPos.z / 4f + 2160f;
    ///      return SampleDetailHeight(x, z) * (1f / 64f);
    ///    It performs both the conversion from world coordinates to detail-heightmap grid
    ///    coordinates (0..4320, 4321 steps) and the 1/64 scale conversion from the raw stored value
    ///    to world height units inside this method.
    ///  - TerrainManager.SampleDetailHeight(float x, float z): float ← Task53 mistakenly adopted
    ///    this one (root cause of the bug). Implementation (ILSpy decompilation result, excerpt):
    ///      int num3 = Mathf.Clamp((int)x, 0, 4320);
    ///      int num4 = Mathf.Clamp((int)z, 0, 4320);
    ///      ... GetDetailHeight(...) uses the above grid coordinates directly as indices and returns
    ///    without the 1/64 scaling. In other words, the x/z arguments of this overload are "detail
    ///    grid coordinates themselves", not world coordinates. Task53 passed world-coordinate x/z
    ///    straight into it, so (a) the coordinate conversion (/4+2160) was missing and a completely
    ///    different location was sampled, and (b) the 1/64 scale conversion was also missing, making
    ///    the return value up to 64x too large. This is the root cause of the "units start dogfighting
    ///    in mid-air" bug (snapping to a Y far above the ground). Because TrySampleHeight does not
    ///    throw (the indices are clamped), this bug left no error in the logs whatsoever, and the
    ///    user's output_log.txt contained no SurfaceHeightSampler-related errors either (confirmed
    ///    during the Task55 investigation).
    ///  - TerrainManager.instance: Singleton&lt;TerrainManager&gt;.instance (same
    ///    ColossalFramework.Singleton pattern as RoadGraphBuilder/CoverMapBuilder, unchanged since
    ///    Task53).
    ///
    /// Reason for adopting SampleDetailHeight(Vector3) above (Task53's original intent is preserved
    /// as-is): the detail heightmap reflects the terrain as actually flattened/deformed by road and
    /// building construction — the "as seen" height — and is distinct from the coarse (1081 steps,
    /// ~8m/cell) control heightmap (the raw pre-construction terrain) referenced by
    /// SampleRawHeight/SampleFinalHeight/SampleBlockHeight. It is SampleDetailHeight that reflects
    /// the ground surface actually changed by road embankments, bridges, building foundations, etc.
    ///
    /// Hardening (introduced in Task53, kept in Task55): the measured ground surface on this map is
    /// about 270, and with the old implementation (a float SampleHeight returning 0f on failure),
    /// the moment TerrainManager was momentarily uninitialized or threw, MovementStep adopted that
    /// 0f directly as the unit's Y, producing a visible glitch where the unit teleported ~270 below
    /// the surface for one tick. We use the TrySampleHeight form instead: on failure it returns
    /// false and delegates to MovementStep's Y-interpolation fallback (this class never writes a
    /// "plausible-looking" failure value into height).
    ///
    /// Defense in depth (added in Task55): to prevent recurrence of the very class of bug above
    /// ("throws no exception but returns an absurd value"), a deviation clamp via
    /// MaxSurfaceDeviation was also added on the Core.MovementStep side (insurance that mechanically
    /// limits the damage even if this class's contract (return the correct height on success) breaks
    /// again in the future).
    /// </summary>
    internal sealed class SurfaceHeightSampler : IHeightSampler
    {
        // Same throttling pattern as RoadGraphBuilder/CoverMapBuilder (Task23/Task44): MovementStep
        // is called massively every tick on the sim thread, so to avoid flooding the log while
        // failures persist we record only the first occurrence. After a success, the next failure is
        // logged once again (the suppression state is reset).
        // Sim-thread-only access, so no lock is needed.
        private static bool _failureAlreadyLogged;

        public bool TrySampleHeight(float x, float z, out float height)
        {
            // Same defensive policy as RoadGraphBuilder/CoverMapBuilder: if the Singleton is not yet
            // created (e.g. the very brief window right after level load), give up for this tick only.
            // Hardening: do not return a "plausible-looking" value (0f etc.) here; signal failure
            // explicitly with false and let the caller (MovementStep) keep its existing Y
            // interpolation result as-is (prevents recurrence of the bug where 0f was adopted
            // directly as Y and the unit teleported far below the surface).
            if (!Singleton<TerrainManager>.exists)
            {
                if (!_failureAlreadyLogged)
                {
                    ModConfig.LogError("SurfaceHeightSampler.TrySampleHeight: TerrainManager not ready; falling back to interpolation");
                    _failureAlreadyLogged = true;
                }
                height = default(float);
                return false;
            }

            try
            {
                // Task55: SampleDetailHeight(float, float) is an internal-facing overload that
                // expects detail grid coordinates, not world coordinates. To get a world-unit height
                // from world-coordinate x/z, use SampleDetailHeight(Vector3) (it performs both the
                // coordinate conversion and the 1/64 scale conversion internally; see the ILSpy
                // decompilation results in the class doc comment above).
                height = Singleton<TerrainManager>.instance.SampleDetailHeight(new Vector3(x, 0f, z));
                _failureAlreadyLogged = false; // Success, so the next failure will again be logged once
                return true;
            }
            catch (System.Exception e)
            {
                // Log only the first occurrence so continued exceptions do not flood the log
                // (emitting every tick without throttling would turn the "Nothing may throw into the
                // game loop" principle into log spam).
                if (!_failureAlreadyLogged)
                {
                    ModConfig.LogError("SurfaceHeightSampler.TrySampleHeight error: " + e);
                    _failureAlreadyLogged = true;
                }
                height = default(float);
                return false;
            }
        }
    }
}
