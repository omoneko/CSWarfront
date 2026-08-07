using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Game-layer implementation of Core.IWaterSampler (Task61: adding naval forces). Queries CS's
    /// TerrainManager and reports whether a coordinate is on water and the height of that water
    /// surface. Same supply pattern as SurfaceHeightSampler (sim thread only, intended to be called
    /// from MovementStep.Advance; on failure, never throws and signals via false).
    ///
    /// Verified signatures (confirmed by decompiling Assembly-CSharp.dll with ilspycmd, Task61):
    ///  - TerrainManager.HasWater(Vector2 position): bool
    ///      position.x = world X, position.y = world Z (the convention is to pack (x,z) into the
    ///      Vector2. TerrainManager's coordinate handling differs from the SampleDetailHeight(Vector3)
    ///      used by SurfaceHeightSampler: HasWater/WaterLevel take world coordinates directly as a
    ///      Vector2 — internally they perform grid conversions such as
    ///      Mathf.FloorToInt((position.x + 8640f) * 16f)).
    ///      Returns true if the water-simulation cell has at least the threshold water depth
    ///      (difference from the adjacent block height >= MIN_WATER_AMOUNT(8), the
    ///      TerrainManager.MIN_WATER_AMOUNT constant).
    ///  - TerrainManager.WaterLevel(Vector2 position): float
    ///      Performs exactly the same grid conversion and threshold test internally as HasWater;
    ///      returns 0f if there is no water surface, otherwise returns the height of the water
    ///      surface (world units, already converted from the internal raw height value * 1/64f).
    ///  - TerrainManager.instance: Singleton&lt;TerrainManager&gt;.instance (same
    ///    ColossalFramework.Singleton pattern as SurfaceHeightSampler).
    ///
    /// Hardening: same policy as SurfaceHeightSampler — when TerrainManager is uninitialized or an
    /// exception occurs, return false and delegate the safe-side fallback to the caller
    /// (Core.MovementStep) (the same behavior as when water==null: "allow the movement" / "keep Y
    /// as before"). Both the missing-Singleton and exception cases are logged only on the first
    /// occurrence; after a success the next failure is again logged once (called at high frequency
    /// from the sim thread, so this prevents log spam).
    /// </summary>
    internal sealed class WaterSampler : IWaterSampler
    {
        /// <summary>Task88 (user request "for naval forces, make the operating area draft-aware"):
        /// the minimum water depth (meters) at which vessels are considered able to navigate. In
        /// addition to HasWater (is there water at all; its threshold is a tiny ~0.125m), IsWater
        /// requires "water surface height − seabed terrain height >= this value". This keeps vessels
        /// out of the surf line and shallows, restricting them to reasonably deep water (prevents the
        /// visual of ships hugging the shore). A calibration value matched to the coastal slopes on
        /// real maps (set conservatively, because too deep a value would leave no spawn points found
        /// off existing naval bases. To be tuned in playtesting).</summary>
        internal const float MinNavigableDepth = 2f;

        private static bool _failureAlreadyLogged;

        public bool IsWater(float x, float z)
        {
            if (!Singleton<TerrainManager>.exists)
            {
                LogFailureOnce("TerrainManager not ready");
                return false;
            }

            try
            {
                TerrainManager tm = Singleton<TerrainManager>.instance;
                Vector2 pos = new Vector2(x, z);
                if (!tm.HasWater(pos))
                {
                    _failureAlreadyLogged = false;
                    return false;
                }

                // Task88: draft check. Water depth = water surface height − seabed (terrain) height.
                // SampleDetailHeight(Vector3) returns the terrain height even on the seabed (not the
                // water surface; the same verified API SurfaceHeightSampler uses).
                float waterLevel = tm.WaterLevel(pos);
                float seabed = tm.SampleDetailHeight(new Vector3(x, 0f, z));
                bool navigable = (waterLevel - seabed) >= MinNavigableDepth;
                _failureAlreadyLogged = false;
                return navigable;
            }
            catch (System.Exception e)
            {
                LogFailureOnce("IsWater error: " + e);
                return false;
            }
        }

        public bool TrySampleWaterLevel(float x, float z, out float level)
        {
            level = 0f;
            if (!Singleton<TerrainManager>.exists)
            {
                LogFailureOnce("TerrainManager not ready");
                return false;
            }

            try
            {
                TerrainManager tm = Singleton<TerrainManager>.instance;
                Vector2 pos = new Vector2(x, z);
                if (!tm.HasWater(pos)) return false; // Land. By contract, level is not written (the caller does not use it).

                level = tm.WaterLevel(pos);
                _failureAlreadyLogged = false;
                return true;
            }
            catch (System.Exception e)
            {
                LogFailureOnce("TrySampleWaterLevel error: " + e);
                level = 0f;
                return false;
            }
        }

        private static void LogFailureOnce(string message)
        {
            if (_failureAlreadyLogged) return;
            _failureAlreadyLogged = true;
            ModConfig.LogError("WaterSampler." + message);
        }
    }
}
