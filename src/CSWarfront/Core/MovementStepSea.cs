namespace CSWarfront.Core
{
    /// <summary>Continuation of MovementStep (Task78: the movement model for sea units (Domain.Sea)).
    /// Only the AdvanceSea body and the detour logic were split into this file to stay within the
    /// 500-line-per-file limit (the same partial-class pattern as MilitaryBase.cs/MilitaryManager*.cs).
    ///
    /// The countermeasure for sea units blocked by land staying pinned at the shoreline forever (user
    /// report: "naval units never move on the enemy base and stay holed up at their own"). No true naval
    /// pathfinding (A* etc.) exists yet — this MVP does a simple "wall follow": when the straight step's
    /// landing point is not water, deterministic detour directions are tried in SeaDetourAnglesDeg order
    /// and the first direction landing on water is taken. It can round capes and the necks of peninsulas,
    /// but may never reach targets inside fully closed bays or inland — in that case SeaBlockedHours
    /// accumulates and SeaBlockedIdleHours safely gives up into Idle. At most len(SeaDetourAnglesDeg) (6)
    /// water tests run per tick, so the search cost is bounded and constant.</summary>
    public static partial class MovementStep
    {
        private static readonly float[] SeaDetourAnglesDeg = { 30f, -30f, 60f, -60f, 90f, -90f };

        /// <summary>Task78: once a sea unit has been unable to take a single step in the straight or any
        /// detour direction for this much in-game time, it transitions to State=Idle that tick and stops
        /// attempting to move until the objective (OrderTargetPos/RallyPoint) changes (prevents the
        /// endless per-tick detour searching that looks like spinning in place and wastes CPU). When the
        /// objective changes, UnitInstance.SeaBlockedHours resets to 0 immediately and detours are
        /// attempted for this long again.</summary>
        public const float SeaBlockedIdleHours = 6f;

        /// <summary>Threshold below which the objective counts as effectively unchanged (horizontal
        /// distance). Smaller differences are treated as "the same order continues" for the
        /// SeaBlockedHours reset decision.</summary>
        private const float SeaObjectiveChangeEpsilon = 0.5f;

        /// <summary>Task61/Task78: sea-unit movement. Attempts a straight line toward the objective with
        /// no RoadGraph/CoverMap at all. If the straight landing point is not water (blocked by land or a
        /// cape), deterministic detour directions are tried in SeaDetourAnglesDeg order and the first that
        /// lands on water is taken (simple wall-follow). If none lands on water, no movement happens this
        /// tick and dt accumulates into SeaBlockedHours (see the IWaterSampler class comment — a known MVP
        /// limitation while dedicated naval pathfinding does not exist: targets fully enclosed by land may
        /// be physically unreachable). Y adopts the water-level sampler's value verbatim (keeping the
        /// previous Y on sampling failure). With water==null, everywhere counts as water and movement is
        /// free (like Height, a safe fallback for testability without the Game layer — no detour/blocking
        /// logic ever runs, the straight step always succeeds).</summary>
        /// <summary>Task92: arrival distance for SeaGrid path waypoints. Slightly under the cell size
        /// (96m) so the unit rolls smoothly onto the next waypoint while passing through.</summary>
        public const float SeaWaypointArrivalDistance = 60f;

        private static void AdvanceSea(UnitInstance u, float stepLen, WorldPos objective, IWaterSampler water, float dt)
        {
            bool objectiveChanged = !u.SeaLastObjective.HasValue ||
                System.Math.Abs(u.SeaLastObjective.Value.X - objective.X) >= SeaObjectiveChangeEpsilon ||
                System.Math.Abs(u.SeaLastObjective.Value.Z - objective.Z) >= SeaObjectiveChangeEpsilon;
            if (objectiveChanged)
            {
                u.SeaLastObjective = objective;
                u.SeaBlockedHours = 0f;
            }
            else if (u.SeaBlockedHours >= SeaBlockedIdleHours)
            {
                // Task78: further searching against the same objective is known to be futile. Do not
                // attempt any movement until the orders change (from the next tick on,
                // ResolveDomainObjective rejects on State!=Moving, so this method stops being called).
                u.State = UnitState.Idle;
                return;
            }

            // Task92: with a SeaGrid path (laid by InvasionOrders/ApplyRally), follow its waypoints in
            // order. The per-step water check, wall-follow detours and the blocked counter all keep
            // working (the grid is coarse, so they remain the last line of defense). Once the path is
            // exhausted, revert to the straight line toward the true objective.
            WorldPos steer = objective;
            if (u.Path != null)
            {
                while (u.PathIndex < u.Path.Count &&
                       u.Position.HorizontalDistanceTo(u.Path[u.PathIndex]) <= SeaWaypointArrivalDistance)
                    u.PathIndex++;
                if (u.PathIndex < u.Path.Count) steer = u.Path[u.PathIndex];
            }
            objective = steer;

            float dist = u.Position.HorizontalDistanceTo(objective);
            if (dist <= 0.01f) { u.SeaBlockedHours = 0f; return; } // already there.

            bool arriving = dist <= stepLen;
            float nx, nz;
            if (arriving) { nx = objective.X; nz = objective.Z; }
            else
            {
                float t = stepLen / dist;
                nx = u.Position.X + (objective.X - u.Position.X) * t;
                nz = u.Position.Z + (objective.Z - u.Position.Z) * t;
            }

            if (water == null || water.IsWater(nx, nz))
            {
                CommitSeaStep(u, nx, nz, water);
                u.SeaBlockedHours = 0f;
                return;
            }

            // Task78: the straight step is blocked by land. Just before arrival (arriving) a detour would
            // overshoot, so it is excluded; otherwise try the deterministic detour directions in order.
            if (!arriving && TryFindSeaDetourStep(u, objective, stepLen, water, out nx, out nz))
            {
                CommitSeaStep(u, nx, nz, water);
                u.SeaBlockedHours = 0f;
                return;
            }

            // Neither the straight step nor any detour landed on water = fully blocked this tick.
            u.SeaBlockedHours += dt;
            if (u.SeaBlockedHours >= SeaBlockedIdleHours)
                u.State = UnitState.Idle;
        }

        /// <summary>In SeaDetourAnglesDeg order, tries landing points of the same step length (stepLen)
        /// with the direction to the objective rotated by each angle, returning the first judged to be
        /// water in nx/nz (true). False when none is water (nx/nz remain undefined). water is guaranteed
        /// non-null here (the caller AdvanceSea already finished on the straight-step side when
        /// water==null).</summary>
        private static bool TryFindSeaDetourStep(UnitInstance u, WorldPos objective, float stepLen, IWaterSampler water, out float nx, out float nz)
        {
            nx = 0f; nz = 0f;
            float dx = objective.X - u.Position.X;
            float dz = objective.Z - u.Position.Z;
            float mag = (float)System.Math.Sqrt(dx * dx + dz * dz);
            if (mag <= 0.0001f) return false;
            dx /= mag; dz /= mag; // unit direction vector

            for (int i = 0; i < SeaDetourAnglesDeg.Length; i++)
            {
                double rad = SeaDetourAnglesDeg[i] * System.Math.PI / 180.0;
                float cos = (float)System.Math.Cos(rad);
                float sin = (float)System.Math.Sin(rad);
                float rdx = dx * cos - dz * sin;
                float rdz = dx * sin + dz * cos;
                float tx = u.Position.X + rdx * stepLen;
                float tz = u.Position.Z + rdz * stepLen;
                if (water.IsWater(tx, tz))
                {
                    nx = tx; nz = tz;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Actually updates the position (Y is the water-level sampler's value; the previous Y is
        /// kept on failure). Shared by both straight and detour landings (see AdvanceSea).</summary>
        private static void CommitSeaStep(UnitInstance u, float nx, float nz, IWaterSampler water)
        {
            float ny = u.Position.Y;
            float level;
            if (water != null && water.TrySampleWaterLevel(nx, nz, out level))
                ny = level;

            u.Position = new WorldPos(nx, ny, nz);
        }
    }
}
