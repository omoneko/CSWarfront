namespace CSWarfront.Core
{
    /// <summary>
    /// Centralizes how tiers (1–5) grow unit performance (Task28).
    /// Not just the land roster (LandUnitRoster) — future sea/air rosters use the same formulas, so
    /// the growth curves stay consistent across categories and domains.
    ///
    /// [The growth formula] For each parameter, linear growth over the tier-1 base value (baseValue):
    ///     value(tier) = baseValue * (1 + perTierIncrement * (tier - 1))
    /// (tier=1 returns baseValue unchanged).
    /// tier is clamped to 1..5 (out-of-range values would extrapolate unintentionally).
    ///
    /// [Per-tier increments (HoI-flavored: higher tiers are stronger, but build costs surge — an
    /// intentional trade-off)]
    ///   HP        +35%
    ///   Attack    +40%
    ///   Range     +10%
    ///   Armor     +45%
    ///   Speed     +8%
    ///   Cost      +60%
    ///   BuildTime +30%
    ///
    /// [Reference tier-5 multipliers] (1 + increment * 4)
    ///   HP x2.4, Attack x2.6, Range x1.4, Armor x2.8, Speed x1.32, Cost x3.4, BuildTime x2.2
    /// e.g. a tier-5 tank has 2.4× HP and 2.6× attack over tier 1, but costs 3.4× as much
    ///     (the trade-off between tier-1 numbers and tier-5 quality).
    /// </summary>
    public static class TierScaling
    {
        private const float HpPerTier = 0.35f;
        private const float AttackPerTier = 0.40f;
        private const float RangePerTier = 0.10f;
        private const float ArmorPerTier = 0.45f;
        private const float SpeedPerTier = 0.08f;
        private const float CostPerTier = 0.60f;
        private const float BuildTimePerTier = 0.30f;

        /// <summary>Accuracy's per-tier increment (Task38: +6% of base per tier). Uses the same linear
        /// growth formula value(tier) = baseValue * (1 + 0.06 * (tier-1)) as everything else, but
        /// accuracy alone is capped at 0.95 (higher tiers aim better, yet never reach a perfect
        /// never-miss accuracy — a deliberate ceiling).</summary>
        private const float AccuracyPerTier = 0.06f;

        /// <summary>The absolute accuracy ceiling (Task38). Values after the drone-spotting buff are
        /// clamped to this ceiling too (see CombatSynergy.AccuracyFor).</summary>
        public const float AccuracyMax = 0.95f;

        /// <summary>Clamps tier to 1..5 (below 1 becomes 1, above 5 becomes 5).</summary>
        private static byte ClampTier(byte tier)
        {
            if (tier < 1) return 1;
            if (tier > 5) return 5;
            return tier;
        }

        private static float Scale(float baseValue, byte tier, float perTierIncrement)
        {
            byte t = ClampTier(tier);
            return baseValue * (1f + perTierIncrement * (t - 1));
        }

        public static float Hp(float baseValue, byte tier) { return Scale(baseValue, tier, HpPerTier); }
        public static float Attack(float baseValue, byte tier) { return Scale(baseValue, tier, AttackPerTier); }
        public static float Range(float baseValue, byte tier) { return Scale(baseValue, tier, RangePerTier); }
        public static float Armor(float baseValue, byte tier) { return Scale(baseValue, tier, ArmorPerTier); }
        public static float SpeedKmh(float baseValue, byte tier) { return Scale(baseValue, tier, SpeedPerTier); }
        public static float Cost(float baseValue, byte tier) { return Scale(baseValue, tier, CostPerTier); }
        public static float BuildTime(float baseValue, byte tier) { return Scale(baseValue, tier, BuildTimePerTier); }

        /// <summary>Accuracy's tier growth. Tier 1 returns baseValue unchanged; later tiers grow by
        /// +6%/tier but never exceed AccuracyMax (0.95). It clamps even when baseValue itself exceeds
        /// AccuracyMax (avoiding tier=1 rounding surprises).</summary>
        public static float Accuracy(float baseValue, byte tier)
        {
            float scaled = Scale(baseValue, tier, AccuracyPerTier);
            return scaled > AccuracyMax ? AccuracyMax : scaled;
        }
    }
}
