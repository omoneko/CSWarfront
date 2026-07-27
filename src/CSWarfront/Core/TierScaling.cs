namespace CSWarfront.Core
{
    /// <summary>
    /// Tier（1〜5）がユニット性能をどう伸ばすかを一元管理する（Task28）。
    /// 陸上ロスター（LandUnitRoster）に限らず、将来のsea/airロスターも同じ式を使うことで
    /// カテゴリ間・ドメイン間で成長カーブの一貫性を保つ。
    ///
    /// 【成長式】各パラメータについて、Tier1の基礎値(baseValue)に対して
    ///     value(tier) = baseValue * (1 + perTierIncrement * (tier - 1))
    /// という線形成長を適用する（tier=1のときは baseValue そのまま）。
    /// tierは1..5にクランプする（範囲外の値は意図しない外挿になるため）。
    ///
    /// 【1Tierあたりの増分（HoI風：上位Tierほど強いが建造コストが急増するトレードオフを意図）】
    ///   HP        +35%
    ///   Attack    +40%
    ///   Range     +10%
    ///   Armor     +45%
    ///   Speed     +8%
    ///   Cost      +60%
    ///   BuildTime +30%
    ///
    /// 【Tier5の目安倍率】(1 + increment * 4)
    ///   HP x2.4, Attack x2.6, Range x1.4, Armor x2.8, Speed x1.32, Cost x3.4, BuildTime x2.2
    /// 例: Tier5戦車はTier1に対しHP2.4倍・攻撃2.6倍だが、コストは3.4倍かかる
    ///     （数を揃えるTier1と、質で押すTier5のトレードオフ）。
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

        /// <summary>tierを1..5へクランプする（1未満は1、5超は5）。</summary>
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
    }
}
