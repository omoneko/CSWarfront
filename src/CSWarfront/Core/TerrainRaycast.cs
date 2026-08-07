using System;

namespace CSWarfront.Core
{
    /// <summary>
    /// Pure computation intersecting a camera ray with the terrain (IHeightSampler) (Task77).
    ///
    /// Background: CS1's terrain has no Unity physics collider, so a Physics.Raycast aimed at open
    /// ground always fails with "hit nothing" (the in-game output_log.txt recording many
    /// "rally click rejected - raycast hit nothing" lines is the root-cause evidence; units/buildings
    /// do hit because the mod gives them its own colliders). Right-click point designation (rally
    /// points, missile targets) falls back to this terrain intersection after Physics.Raycast misses.
    ///
    /// Algorithm: a coarse ray-march (CoarseStep spacing) finds the first interval where the ray height
    /// drops below the terrain height, then bisection refines that interval to pin the intersection.
    /// A single flat-plane intersection would punch through hillsides and ledges and hit far beyond
    /// (especially pronounced with shallow camera angles), hence the march. Deterministic, floats only.
    /// </summary>
    public static class TerrainRaycast
    {
        /// <summary>Coarse march step (meters). CS's detail heightmap is ~4m/cell, so 16m practically
        /// never steps clean over a hill (and if it did, the bisection interval just shifts one step
        /// later).</summary>
        private const float CoarseStep = 16f;

        private const int BisectIterations = 24; // 16m / 2^24 ≈ 1μm — converges plenty

        /// <summary>Searches for the terrain intersection from origin along (dirX,dirY,dirZ)
        /// (normalization not required) out to maxDistance. Returns true and stores the point in hit if
        /// found. False when the origin is already below the terrain (underground camera and similar
        /// anomalies), the ray points up, the sampler fails, or no intersection lies within
        /// maxDistance.</summary>
        public static bool TryFind(
            WorldPos origin, float dirX, float dirY, float dirZ,
            IHeightSampler sampler, float maxDistance,
            out WorldPos hit)
        {
            hit = default(WorldPos);
            if (sampler == null || maxDistance <= 0f) return false;

            float len = (float)Math.Sqrt(dirX * dirX + dirY * dirY + dirZ * dirZ);
            if (len < 1e-6f) return false;
            float nx = dirX / len, ny = dirY / len, nz = dirZ / len;

            // Confirm the origin is above the terrain (below would mean a backwards hit — treat as a miss)
            float h0;
            if (!sampler.TrySampleHeight(origin.X, origin.Z, out h0)) return false;
            if (origin.Y < h0) return false;

            // Coarse march: find the first interval [tPrev, t] flipping from "above terrain" to "at or below"
            float tPrev = 0f;
            bool found = false;
            float tLow = 0f, tHigh = 0f;
            for (float t = CoarseStep; t <= maxDistance; t += CoarseStep)
            {
                float x = origin.X + nx * t;
                float y = origin.Y + ny * t;
                float z = origin.Z + nz * t;
                float h;
                if (!sampler.TrySampleHeight(x, z, out h)) return false;
                if (y <= h)
                {
                    tLow = tPrev;
                    tHigh = t;
                    found = true;
                    break;
                }
                tPrev = t;
            }
            if (!found) return false;

            // Refine the interval by bisection (the terrain inside the interval need not be monotonic:
            // the invariant "low is above the terrain / high is at or below" holds while shrinking)
            for (int i = 0; i < BisectIterations; i++)
            {
                float tMid = (tLow + tHigh) * 0.5f;
                float x = origin.X + nx * tMid;
                float y = origin.Y + ny * tMid;
                float z = origin.Z + nz * tMid;
                float h;
                if (!sampler.TrySampleHeight(x, z, out h)) return false;
                if (y <= h) tHigh = tMid; else tLow = tMid;
            }

            float tf = tHigh;
            float hx = origin.X + nx * tf;
            float hz = origin.Z + nz * tf;
            float hy;
            if (!sampler.TrySampleHeight(hx, hz, out hy)) return false;
            hit = new WorldPos(hx, hy, hz);
            return true;
        }
    }
}
