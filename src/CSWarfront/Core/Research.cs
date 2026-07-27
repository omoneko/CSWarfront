namespace CSWarfront.Core
{
    /// <summary>
    /// 研究によるTier解禁（Task35）。撃破報酬・資金投資で貯まった Faction.ResearchPoints を消費して
    /// Faction.UnlockedTier を引き上げる、純ロジック・決定的・RNG不使用のクラス。
    /// UnityEngine非依存。ProductionPlanning / ManualProduction はUnlockedTierを参照してTierゲートする。
    /// </summary>
    public static class Research
    {
        /// <summary>撃破報酬レート。撃破したUnitTypeのCostに掛けて研究点を算出する（Task35）。</summary>
        public const float KillRewardRate = 0.5f;

        /// <summary>資金→研究点の変換効率。1.0fは等価交換（Task35）。</summary>
        public const float TreasuryToResearchRate = 1.0f;

        /// <summary>
        /// 指定Tierへ解禁するのに必要な研究点。T2=100, T3=250, T4=500, T5=1000。
        /// それ以外（1以下・6以上）は解禁不可の意味で0を返す。
        /// </summary>
        public static float CostToUnlock(byte nextTier)
        {
            switch (nextTier)
            {
                case 2: return 100f;
                case 3: return 250f;
                case 4: return 500f;
                case 5: return 1000f;
                default: return 0f;
            }
        }

        /// <summary>faction が次のTierへ解禁可能か（UnlockedTier&lt;5 かつ ResearchPoints が足りている）。</summary>
        public static bool CanUnlockNext(Faction f)
        {
            if (f == null || f.UnlockedTier >= 5) return false;
            byte next = (byte)(f.UnlockedTier + 1);
            float cost = CostToUnlock(next);
            return cost > 0f && f.ResearchPoints >= cost;
        }

        /// <summary>条件を満たせば研究点を消費してUnlockedTierを1上げる。満たさなければ何もせずfalse。</summary>
        public static bool TryUnlockNext(Faction f)
        {
            if (!CanUnlockNext(f)) return false;
            byte next = (byte)(f.UnlockedTier + 1);
            float cost = CostToUnlock(next);
            f.ResearchPoints -= cost;
            f.UnlockedTier = next;
            return true;
        }

        /// <summary>撃破報酬の研究点。撃破したユニットのCost × KillRewardRate。destroyedがnullなら0。</summary>
        public static float KillReward(UnitType destroyed)
        {
            if (destroyed == null) return 0f;
            return destroyed.Cost * KillRewardRate;
        }

        /// <summary>
        /// 資金を研究点へ変換する（Task35 Part3）。f.TrySpend(treasuryAmount) が成功した場合のみ
        /// treasuryAmount * TreasuryToResearchRate を研究点へ加算しtrueを返す。資金不足ならfalse
        /// （何も変更しない）。
        /// </summary>
        public static bool TryInvest(Faction f, float treasuryAmount)
        {
            if (f == null) return false;
            if (!f.TrySpend(treasuryAmount)) return false;
            f.AddResearchPoints(treasuryAmount * TreasuryToResearchRate);
            return true;
        }
    }
}
