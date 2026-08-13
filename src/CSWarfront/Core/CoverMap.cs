using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>One piece of cover (building/prop). Radius is its approximate footprint radius.</summary>
    public struct CoverPoint
    {
        public readonly WorldPos Position;
        public readonly float Radius;

        public CoverPoint(WorldPos position, float radius)
        {
            Position = position;
            Radius = radius;
        }
    }

    /// <summary>
    /// The set of cover objects (buildings/props) supplied by the Game layer (CoverMapBuilder), plus the
    /// "seek cover" logic (Task44). UnityEngine-free, deterministic. The same supply pattern as
    /// RoadGraph: the Game layer reads the CS building/prop buffers on the sim thread, repacks them into
    /// this simple POCO and hands it to WarState.Cover.
    /// </summary>
    public class CoverMap
    {
        /// <summary>Margin between the cover's edge and where the unit actually stands (map units ≈
        /// meters). At 0 the unit would hug the wall, which looks unnatural, so it stands off a little.</summary>
        public const float StandoffMargin = 4f;

        // Scoring weights (tuning values). Lower scores are better candidates.
        //  - DistanceWeight: how much closeness to the unit is favored.
        //  - ExposureWeight: how much "sitting between the threat and the unit" is favored
        //    (ExposureScore spans a rougher range than distance, hence the slightly heavier weight).
        //  - JitterMagnitude: a small deterministic preference preventing several units from packing onto
        //    the identical best spot (kept small enough to only decide between tied/near-tied candidates).
        //  - OutOfSegmentPenalty: heavily penalizes candidates outside the unit→threat segment (positions
        //    that do not function as cover at all, e.g. behind the unit). Kept well above the distance
        //    weight so mere closeness can never let "cover behind us" beat "cover in between".
        private const float DistanceWeight = 1f;
        private const float ExposureWeight = 1.5f;
        private const float OutOfSegmentPenalty = 120f;
        private const float JitterMagnitude = 3f;

        private readonly List<CoverPoint> _points = new List<CoverPoint>();

        public int Count => _points.Count;

        public void Add(WorldPos position, float radius)
        {
            _points.Add(new CoverPoint(position, radius));
        }

        /// <summary>Task101: whether the line of fire from from to to (a horizontal segment) is blocked
        /// by cover (building circles). Used for the bunker's "no shooting through buildings" test.
        /// Cover within ignoreNearFromRadius of from counts as the shooter's own building and is ignored.
        /// Cover on the target side legitimately blocks the line (an enemy pressed against a building
        /// cannot be shot = cover keeps its meaning).</summary>
        public bool BlocksLine(WorldPos from, WorldPos to, float ignoreNearFromRadius)
        {
            float abx = to.X - from.X, abz = to.Z - from.Z;
            float lenSq = abx * abx + abz * abz;

            for (int i = 0; i < _points.Count; i++)
            {
                CoverPoint cp = _points[i];

                float fx = cp.Position.X - from.X, fz = cp.Position.Z - from.Z;
                float distFromSq = fx * fx + fz * fz;
                float ignore = ignoreNearFromRadius + cp.Radius;
                if (distFromSq <= ignore * ignore) continue; // the shooter's own building

                // Shortest distance from a point to a segment (2D).
                float t = lenSq > 0f ? (fx * abx + fz * abz) / lenSq : 0f;
                if (t < 0f) t = 0f;
                if (t > 1f) t = 1f;
                float cx = from.X + abx * t - cp.Position.X;
                float cz = from.Z + abz * t - cp.Position.Z;
                if (cx * cx + cz * cz < cp.Radius * cp.Radius) return true;
            }
            return false;
        }

        /// <summary>
        /// Among the cover within searchRadius of unitPos, finds the position that best hides from
        /// threatPos. On success the returned WorldPos is the standing spot on the cover's far side as
        /// seen from the threat (coverCentre + normalize(coverCentre - threatPos) * (radius +
        /// StandoffMargin)). False when nothing is found (the caller must fall back to its existing
        /// movement logic).
        /// </summary>
        public bool TryFindBestCover(WorldPos unitPos, WorldPos threatPos, float searchRadius, uint seed, out WorldPos coverPos)
        {
            coverPos = default(WorldPos);
            if (searchRadius <= 0f || _points.Count == 0) return false;

            int bestIndex = -1;
            float bestScore = float.MaxValue;

            for (int i = 0; i < _points.Count; i++)
            {
                CoverPoint cp = _points[i];

                // Spatial shortcut: reject with per-axis bounding boxes before the full horizontal
                // distance (sqrt).
                float dx = unitPos.X - cp.Position.X;
                if (dx > searchRadius || dx < -searchRadius) continue;
                float dz = unitPos.Z - cp.Position.Z;
                if (dz > searchRadius || dz < -searchRadius) continue;

                float distToUnit = unitPos.HorizontalDistanceTo(cp.Position);
                if (distToUnit > searchRadius) continue;

                float exposureScore = ExposureScore(cp.Position, unitPos, threatPos);
                float jitter = JitterFactor(seed, i) * JitterMagnitude;
                float score = distToUnit * DistanceWeight + exposureScore * ExposureWeight + jitter;

                // Updating only on a strict "<" means exact ties are automatically won by the candidate
                // found first (= the lower index). That is the deterministic tie-break.
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0) return false;

            CoverPoint best = _points[bestIndex];
            coverPos = StandingPosition(best, threatPos);
            return true;
        }

        /// <summary>Scores how well the cover sits on the unit→threat segment (= how well it interposes).
        /// Closer to 0 is better: the perpendicular distance off the segment plus a penalty for lying
        /// outside it (t&lt;0 or t&gt;1). The degenerate case of the unit and threat sharing a position
        /// treats all candidates as equal (0).</summary>
        private static float ExposureScore(WorldPos coverPos, WorldPos unitPos, WorldPos threatPos)
        {
            float ux = threatPos.X - unitPos.X;
            float uz = threatPos.Z - unitPos.Z;
            float lenSq = ux * ux + uz * uz;
            if (lenSq < 0.0001f) return 0f;

            float cx = coverPos.X - unitPos.X;
            float cz = coverPos.Z - unitPos.Z;
            float t = (cx * ux + cz * uz) / lenSq;

            float projX = unitPos.X + ux * t;
            float projZ = unitPos.Z + uz * t;
            float pdx = coverPos.X - projX;
            float pdz = coverPos.Z - projZ;
            float perpDist = (float)System.Math.Sqrt(pdx * pdx + pdz * pdz);

            float outOfSegment = 0f;
            if (t < 0f) outOfSegment = -t * OutOfSegmentPenalty;
            else if (t > 1f) outOfSegment = (t - 1f) * OutOfSegmentPenalty;

            return perpDist + outOfSegment;
        }

        /// <summary>Computes the spot where the unit actually stands — the cover's far side as seen from
        /// the threat. When coverCentre and threatPos nearly coincide (degenerate case), escapes in the
        /// deterministic default direction (+Z).</summary>
        /// <summary>Task127: the standing position against the building closest to from, within
        /// maxDistance, on the side away from threatPos. Unlike TryFindBestCover this does not score
        /// candidates for how well they screen the unit — a garrison is "get off the road into the
        /// nearest doorway", so plain proximity is the rule. Deterministic: ties go to the earlier
        /// entry, which is the CoverMapBuilder's stable building order.</summary>
        public bool TryFindNearestStandingPosition(WorldPos from, WorldPos threatPos, float maxDistance,
            out WorldPos standingPosition)
        {
            standingPosition = from;
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _points.Count; i++)
            {
                float d = from.HorizontalDistanceTo(_points[i].Position);
                if (d > maxDistance) continue;
                if (d < bestDist) { bestDist = d; best = i; }
            }
            if (best < 0) return false;

            standingPosition = StandingPosition(_points[best], threatPos);
            return true;
        }

        private static WorldPos StandingPosition(CoverPoint cover, WorldPos threatPos)
        {
            float ax = cover.Position.X - threatPos.X;
            float az = cover.Position.Z - threatPos.Z;
            float len = (float)System.Math.Sqrt(ax * ax + az * az);

            float nx, nz;
            if (len < 0.0001f)
            {
                nx = 0f; nz = 1f;
            }
            else
            {
                nx = ax / len; nz = az / len;
            }

            float standoff = cover.Radius + StandoffMargin;
            return new WorldPos(
                cover.Position.X + nx * standoff,
                cover.Position.Y,
                cover.Position.Z + nz * standoff);
        }

        /// <summary>Derives a deterministic [0,1) factor from (seed, candidateIndex). The same technique
        /// as RoadGraph.EdgeJitterFactor (pure-integer avalanche mix, no System.Random).</summary>
        private static float JitterFactor(uint seed, int candidateIndex)
        {
            uint h = Mix(seed ^ (uint)candidateIndex);
            h = Mix(h ^ 0x9e3779b9U);
            return (h >> 8) / (float)(1u << 24);
        }

        private static uint Mix(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352dU;
            x ^= x >> 15;
            x *= 0x846ca68bU;
            x ^= x >> 16;
            return x;
        }
    }
}
