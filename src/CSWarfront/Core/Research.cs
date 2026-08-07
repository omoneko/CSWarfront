namespace CSWarfront.Core
{
    /// <summary>
    /// Tier unlocking through research (Task35). A pure-logic, deterministic, RNG-free class that
    /// spends the Faction.ResearchPoints accumulated from kill rewards and cash investment to raise
    /// Faction.UnlockedTier. No UnityEngine dependency. ProductionPlanning / ManualProduction read
    /// UnlockedTier to gate tiers.
    /// </summary>
    public static class Research
    {
        /// <summary>The kill-reward rate. Multiplied by the destroyed UnitType's Cost to yield
        /// research points (Task35).</summary>
        public const float KillRewardRate = 0.5f;

        /// <summary>The cash → research-point conversion efficiency. 1.0f is an even trade
        /// (Task35).</summary>
        public const float TreasuryToResearchRate = 1.0f;

        /// <summary>
        /// Research points required to unlock the given tier. T2=100, T3=250, T4=500, T5=1000.
        /// Anything else (1 or below, 6 or above) returns 0, meaning unlockable-by-nothing.
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

        /// <summary>Whether the faction can unlock the next tier (UnlockedTier&lt;5 and enough
        /// ResearchPoints).</summary>
        public static bool CanUnlockNext(Faction f)
        {
            if (f == null || f.UnlockedTier >= 5) return false;
            byte next = (byte)(f.UnlockedTier + 1);
            float cost = CostToUnlock(next);
            return cost > 0f && f.ResearchPoints >= cost;
        }

        /// <summary>Spends research points and raises UnlockedTier by one when the conditions hold.
        /// Otherwise does nothing and returns false.</summary>
        public static bool TryUnlockNext(Faction f)
        {
            if (!CanUnlockNext(f)) return false;
            byte next = (byte)(f.UnlockedTier + 1);
            float cost = CostToUnlock(next);
            f.ResearchPoints -= cost;
            f.UnlockedTier = next;
            return true;
        }

        /// <summary>The kill reward in research points: the destroyed unit's Cost × KillRewardRate.
        /// 0 when destroyed is null.</summary>
        public static float KillReward(UnitType destroyed)
        {
            if (destroyed == null) return 0f;
            return destroyed.Cost * KillRewardRate;
        }

        /// <summary>
        /// Converts cash to research points (Task35 Part3). Only when f.TrySpend(treasuryAmount)
        /// succeeds does it add treasuryAmount * TreasuryToResearchRate to research points and return
        /// true. Returns false on insufficient funds (changing nothing).
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
