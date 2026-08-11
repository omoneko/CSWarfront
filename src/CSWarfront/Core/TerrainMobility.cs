namespace CSWarfront.Core
{
    /// <summary>How a land unit copes with terrain (Task125). Derived from UnitCategory — three classes
    /// keep the tuning legible and the behavioural difference obvious in play.</summary>
    public enum MobilityClass
    {
        /// <summary>Foot troops: barely care about roads, climb almost anything.</summary>
        Infantry,
        /// <summary>Tracked vehicles (tanks, SPGs, SPAAGs, APCs): fair cross-country ability.</summary>
        Tracked,
        /// <summary>Wheeled vehicles (jeeps, supply trucks): fast on roads, poor off them.</summary>
        Wheeled
    }

    /// <summary>
    /// Task125 (user request "movement should be restricted and slowed by terrain, depending on the unit
    /// class"): terrain effects on land movement — a road/off-road factor and a slope factor, plus a
    /// per-class maximum traversable slope.
    ///
    /// Applies to off-road movement only. Units following a road path move on the road network's own
    /// geometry (bridges and embankments included), whose height must never be second-guessed from the
    /// terrain sampler — see MovementStep's Task77 notes. So a unit on its path just gets the (absent)
    /// road penalty, and slope is evaluated only once it is cutting across country.
    ///
    /// Pure logic: no UnityEngine, no CS API, no RNG. The caller supplies the slope it measured through
    /// IHeightSampler.
    /// </summary>
    public static class TerrainMobility
    {
        /// <summary>Distance (m) over which the caller samples height to measure the local slope. Short
        /// enough to notice a bank or a cutting, long enough that detail-map noise (~4m/cell) does not
        /// read as a cliff.</summary>
        public const float SlopeSampleDistance = 12f;

        /// <summary>Speed retained at the class's maximum traversable slope (the rest of the curve is
        /// linear from 1.0 on flat ground). Even at the limit a unit still crawls rather than freezing.</summary>
        public const float SlopeSpeedFloor = 0.4f;

        public static MobilityClass ClassOf(UnitCategory category)
        {
            switch (category)
            {
                case UnitCategory.Infantry:
                case UnitCategory.DroneInfantry:
                    return MobilityClass.Infantry;

                case UnitCategory.Tank:
                case UnitCategory.Artillery:
                case UnitCategory.AntiAir:
                case UnitCategory.Apc:
                    return MobilityClass.Tracked;

                // MechInfantry rides the jeep model, and supply trucks are trucks: both are wheeled.
                case UnitCategory.MechInfantry:
                case UnitCategory.SupplyTruck:
                    return MobilityClass.Wheeled;

                default:
                    // Anything else that somehow reaches land movement (never in practice: sea, air and
                    // rail units have their own movement models) is treated as infantry = least
                    // restricted, so an unforeseen category can never be immobilised by terrain.
                    return MobilityClass.Infantry;
            }
        }

        /// <summary>The steepest slope (degrees) this class can move across. Above it the step is
        /// refused and the caller deflects or waits.</summary>
        public static float MaxSlopeDegrees(MobilityClass mobility)
        {
            switch (mobility)
            {
                case MobilityClass.Tracked: return 35f;
                case MobilityClass.Wheeled: return 22f;
                default: return 55f; // infantry: scrambles up almost anything short of a cliff
            }
        }

        /// <summary>Speed multiplier for leaving the road network. Roads are graded and metalled, so
        /// nothing is penalised while on them.</summary>
        public static float OffRoadFactor(MobilityClass mobility)
        {
            switch (mobility)
            {
                case MobilityClass.Tracked: return 0.75f;
                case MobilityClass.Wheeled: return 0.45f;
                default: return 0.85f; // infantry
            }
        }

        /// <summary>Whether this class may traverse ground of the given slope (absolute degrees — a
        /// descent too steep to drive down is refused just like the climb).</summary>
        public static bool CanTraverse(MobilityClass mobility, float slopeDegrees)
        {
            float s = slopeDegrees < 0f ? -slopeDegrees : slopeDegrees;
            return s <= MaxSlopeDegrees(mobility);
        }

        /// <summary>
        /// Combined terrain speed multiplier. onRoad units are unaffected (factor 1); off-road units get
        /// the class's off-road factor, scaled down further by slope — linearly from 1.0 on the flat to
        /// SlopeSpeedFloor at the class's maximum traversable slope. Slopes beyond the maximum return the
        /// floor value; the caller is expected to have refused the step via CanTraverse first.
        /// </summary>
        public static float SpeedFactor(MobilityClass mobility, float slopeDegrees, bool onRoad)
        {
            if (onRoad) return 1f;

            float s = slopeDegrees < 0f ? -slopeDegrees : slopeDegrees;
            float max = MaxSlopeDegrees(mobility);
            float slopeRatio = max <= 0f ? 1f : s / max;
            if (slopeRatio > 1f) slopeRatio = 1f;

            float slopeFactor = 1f - (1f - SlopeSpeedFloor) * slopeRatio;
            return OffRoadFactor(mobility) * slopeFactor;
        }

        /// <summary>Slope (degrees) of the straight line between two sampled ground heights, given the
        /// horizontal distance between them. Returns 0 for a degenerate distance.</summary>
        public static float SlopeDegrees(float heightFrom, float heightTo, float horizontalDistance)
        {
            if (horizontalDistance <= 0.001f) return 0f;
            double rise = heightTo - heightFrom;
            return (float)(System.Math.Atan2(rise, horizontalDistance) * 180.0 / System.Math.PI);
        }
    }
}
