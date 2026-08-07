using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Shared helper that resolves the world-space "clicked point" from the mouse's screen
    /// coordinates (Task77). Used by both UnitCommandInput (rally points) and
    /// MissileLaunchTargeting (missile targets).
    ///
    /// Procedure:
    ///  1. Physics.Raycast — precise hits against the mod's own colliders such as units/buildings
    ///     (the legacy path).
    ///  2. If that misses, Core.TerrainRaycast — intersection computed against TerrainManager
    ///     height sampling. CS1 terrain has no Unity physics collider, so clicks on open ground
    ///     are always resolved by this path (previously this path did not exist and everything was
    ///     rejected with "raycast hit nothing" = the root cause of Task77).
    ///
    /// Threading note: main thread only (touches Camera/Input/Physics). SurfaceHeightSampler's
    /// TerrainManager.SampleDetailHeight(Vector3) is a read-only API and is safe to read from the
    /// main thread concurrently with the sim thread (races on the log-suppression flag are
    /// harmless).
    /// </summary>
    internal static class GroundClickRaycast
    {
        private const float MaxRaycastDistance = 10000f; // same value as the legacy UnitCommandInput/MissileLaunchTargeting

        private static readonly SurfaceHeightSampler _sampler = new SurfaceHeightSampler();

        /// <summary>Attempts to get the world position of the clicked point from the current mouse
        /// position. Returns false if the camera is not ready and the terrain intersection also
        /// fails, storing the rejection reason (a short English phrase for logging) in reason.</summary>
        public static bool TryGetPoint(out Vector3 point, out string reason)
        {
            point = default(Vector3);

            Camera cam = Camera.main;
            if (cam == null)
            {
                reason = "camera not ready";
                return false;
            }

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);

            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, MaxRaycastDistance))
            {
                point = hit.point;
                reason = null;
                return true;
            }

            WorldPos terrainHit;
            if (TerrainRaycast.TryFind(
                new WorldPos(ray.origin.x, ray.origin.y, ray.origin.z),
                ray.direction.x, ray.direction.y, ray.direction.z,
                _sampler, MaxRaycastDistance, out terrainHit))
            {
                point = new Vector3(terrainHit.X, terrainHit.Y, terrainHit.Z);
                reason = null;
                return true;
            }

            reason = "no collider hit and terrain intersection failed";
            return false;
        }
    }
}
