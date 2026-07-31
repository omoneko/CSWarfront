namespace CSWarfront.Core
{
    /// <summary>
    /// 対空兵科（AntiAir）の対航空戦闘の規則（Task90、ユーザー要望）:
    ///  - 自爆ドローンに対しては機銃、戦闘機・爆撃機に対しては対空ミサイルを発射する。
    ///  - どちらも「1回の攻撃ごとの命中率」をTierごとに持ち、外れる場合もある。
    ///
    /// 通常の兵科（期待値方式: Accuracyをダメージ倍率として毎tick連続適用）と違い、対空の対航空攻撃は
    /// FireIntervalHoursごとの離散的な1発として解決する: 発射のたびに決定的ハッシュ（乱数不使用、
    /// state.TickCounter/攻撃側/目標のIDから導出）で命中/外れをロールし、命中時のみ
    /// 「Attack × FireIntervalHours × 相性」の一括ダメージを与える。外れは完全にノーダメージで、
    /// ShotEvent.Missed=trueの発砲イベントだけが積まれる（Game層が「逸れる対空ミサイル＋標的の
    /// フレア放出・回避機動」の演出に使う）。
    ///
    /// 期待DPSは「命中率 × Attack × 相性」で従来の期待値方式と同じ形になるため、命中率テーブルの
    /// 数値そのものがそのままバランス調整のツマミになる。
    /// </summary>
    public static class AntiAirCombat
    {
        /// <summary>対空機銃（vs 自爆ドローン）のTier別命中率。近距離の高速目標だが銃弾は即着弾する
        /// ため、ミサイルよりも安定して当たる。T1=0.70 → T5=0.90。</summary>
        public static float GunHitChance(byte tier)
        {
            float chance = 0.70f + 0.05f * (tier - 1);
            return chance > 0.90f ? 0.90f : chance;
        }

        /// <summary>対空ミサイル（vs 戦闘機・爆撃機）のTier別命中率。標的はフレア放出と回避機動で
        /// 逸らしてくる（外れの演出、Game層）ため機銃より低め。T1=0.55 → T5=0.83。</summary>
        public static float MissileHitChance(byte tier)
        {
            float chance = 0.55f + 0.07f * (tier - 1);
            return chance > 0.90f ? 0.90f : chance;
        }

        /// <summary>この航空目標に対して対空ミサイルを使うか（false=機銃）。自爆ドローンのような
        /// 小型・低空目標は機銃、それ以外の航空機（戦闘機・爆撃機）はミサイル。</summary>
        public static bool UsesMissileAgainst(UnitCategory targetCategory)
        {
            return !targetCategory.IsKamikaze();
        }

        /// <summary>Tierと目標に応じた1発ごとの命中率。</summary>
        public static float HitChanceFor(byte tier, UnitCategory targetCategory)
        {
            return UsesMissileAgainst(targetCategory) ? MissileHitChance(tier) : GunHitChance(tier);
        }

        /// <summary>1発ぶんの命中ロール（決定的、乱数不使用）。BallisticMissiles.HashSeedと同じ
        /// 素数近似定数の合成＋finalizerで、(攻撃側, 目標, tick)から[0,1)の一様値を導き
        /// chanceと比較する。</summary>
        public static bool RollHit(uint attackerId, uint targetId, uint tick, float chance)
        {
            unchecked
            {
                uint h = attackerId;
                h = h * 2654435761u + targetId;
                h = h * 2654435761u + tick;
                // finalizer（fmix32、MurmurHash3）
                h ^= h >> 16;
                h *= 0x85ebca6bu;
                h ^= h >> 13;
                h *= 0xc2b2ae35u;
                h ^= h >> 16;
                float roll = (h & 0xFFFFFF) / (float)0x1000000; // [0,1)
                return roll < chance;
            }
        }
    }
}
