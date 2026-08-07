using System;

namespace CSWarfront.Core
{
    /// <summary>
    /// Computes synergy effects between unit combinations (Task38).
    /// Only one synergy is defined so far — "Artillery + DroneInfantry spotting support" — but with
    /// AccuracyFor as the entry point, future synergies for other categories (e.g. MechInfantry escort
    /// boosting tank armor, AntiAir overwatch boosting APC survival) can keep stacking their checks
    /// into this class.
    ///
    /// Deterministic (no RNG), O(units): a single linear scan over the friendly unit list suffices.
    /// </summary>
    public static class CombatSynergy
    {
        /// <summary>A friendly drone-infantry unit within this horizontal distance establishes
        /// artillery spotting support.</summary>
        public const float DroneSpotterRadius = 150f;

        /// <summary>The bonus added to accuracy when spotting support holds (additive, not
        /// multiplicative). The post-addition value is clamped at TierScaling.AccuracyMax
        /// (0.95).</summary>
        public const float DroneSpotterAccuracyBonus = 0.5f;

        /// <summary>
        /// Returns the "effective accuracy" when attacker attacks as attackerType.
        /// The rule defined so far: if attackerType.Category is Artillery and at least one living
        /// DroneInfantry unit of the same faction lies within DroneSpotterRadius (horizontal
        /// distance), returns min(TierScaling.AccuracyMax, attackerType.Accuracy +
        /// DroneSpotterAccuracyBonus). Otherwise (non-Artillery, or no spotting support),
        /// attackerType.Accuracy is returned unchanged.
        /// </summary>
        public static float AccuracyFor(WarState state, UnitInstance attacker, UnitType attackerType)
        {
            if (attackerType == null) return 0f;
            if (attackerType.Category != UnitCategory.Artillery) return attackerType.Accuracy;
            if (!HasFriendlyDroneSpotterNearby(state, attacker)) return attackerType.Accuracy;
            return Math.Min(TierScaling.AccuracyMax, attackerType.Accuracy + DroneSpotterAccuracyBonus);
        }

        /// <summary>True when at least one living DroneInfantry of the attacker's faction (FactionId)
        /// lies within DroneSpotterRadius (horizontal distance).</summary>
        private static bool HasFriendlyDroneSpotterNearby(WarState state, UnitInstance attacker)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (u.FactionId != attacker.FactionId) continue;
                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null || type.Category != UnitCategory.DroneInfantry) continue;
                if (attacker.Position.HorizontalDistanceTo(u.Position) <= DroneSpotterRadius) return true;
            }
            return false;
        }
    }
}
