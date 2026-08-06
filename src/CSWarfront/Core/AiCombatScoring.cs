namespace CSWarfront.Core
{
    /// <summary>
    /// The "payoff" and "hedge" (mixed-strategy) phases of the game-theoretic AI production rework
    /// (Task80). Scores each candidate category's expected effectiveness against the enemy mix counted by
    /// AiThreatAssessment.Observe, then picks probabilistically among the top candidates via a seed hash
    /// (= a mixed-strategy hedge avoiding the "always pick the argmax" strategy an opponent can read).
    /// Pure logic called only from AiProductionPolicy.Decide; deterministic; no UnityEngine dependency
    /// (System.Random is never used).
    /// </summary>
    public static class AiCombatScoring
    {
        /// <summary>Expected effectiveness E[c] of producing category c against the enemy mix (the design
        /// requirement's formula verbatim):
        ///   E[c] = Σ_target share[target] × Multiplier(c, target) × survivability(c, enemyMix)
        /// The first term (Σ) is the expected firepower from the "attacker = c" view of CombatMatchup (the
        /// expectation of the matchup multiplier weighted by the opponent's composition). The
        /// survivability factor is described at Survivability() below — a cheap approximation collapsing
        /// "how vulnerable c is to being hunted given the opponent's composition" into one coefficient
        /// (a counter-counter estimate).</summary>
        public static float ExpectedEffectiveness(UnitCategory c, float[] enemyShares)
        {
            float expectedAttack = 0f;
            for (int t = 0; t < enemyShares.Length; t++)
            {
                float share = enemyShares[t];
                if (share <= 0f) continue;
                expectedAttack += share * CombatMatchup.Multiplier(c, (UnitCategory)t);
            }
            return expectedAttack * Survivability(c, enemyShares);
        }

        /// <summary>survivability(c, mix) = 1 / (1 + Σ share[t] × Multiplier(t, c) / 2).
        /// The Σ term is "the expected incoming-damage multiplier from the enemy against c, weighted by
        /// their composition" — larger the more of the enemy's mix counters c. The /2 is a damper that
        /// weakens the influence of matchup differences (limits the denominator's movement at a 1.0
        /// matchup so an extreme matchup (2.0× etc.) alone cannot blow the score up). The 1/(1+x) shape
        /// keeps the value in (0,1] and smoothly approaches 0 as matchups worsen (the counter-counter
        /// term: a cheap approximation of the second-order read "if the opponent fields many hunters of
        /// this category, make it less attractive").</summary>
        public static float Survivability(UnitCategory c, float[] enemyShares)
        {
            float expectedIncomingMultiplier = 0f;
            for (int t = 0; t < enemyShares.Length; t++)
            {
                float share = enemyShares[t];
                if (share <= 0f) continue;
                expectedIncomingMultiplier += share * CombatMatchup.Multiplier((UnitCategory)t, c);
            }
            return 1f / (1f + expectedIncomingMultiplier / 2f);
        }

        /// <summary>The tier "quality vs quantity" valuation: value(tier) = sqrt(HP × Attack) / Cost.
        /// sqrt(HP×Attack) crudely approximates "sustained damage output × time it can survive"
        /// (Lanchester's square law — "combat power scales as quality × quantity squared" — reduced to a
        /// per-unit effective-strength index; treating HP and Attack symmetrically avoids over/under-rating
        /// units extreme in only one of the two). Dividing by Cost yields "effective strength per unit
        /// cost" = cost efficiency. TierScaling is HP+35% / Attack+40% / Cost+60% per tier, so
        /// sqrt(HP×Attack)'s growth (≈+37.5%/tier) never catches Cost's (+60%/tier): by this index alone,
        /// tier 1 is always the most cost-efficient (i.e. massing is the better "raw efficiency" — which
        /// is precisely this game's intended tier cost curve). AiProductionPolicy feeds this value through
        /// HedgePick over the candidate tiers, so instead of pinning to tier 1 it sometimes invests in the
        /// less-efficient higher tiers (deeper roster, survivability, groundwork for research leads).</summary>
        public static float TierValue(UnitType t)
        {
            return (float)System.Math.Sqrt((double)t.MaxHP * t.Attack) / t.Cost;
        }

        /// <summary>Deterministic integer hash (MurmurHash3 finalizer equivalent). Same technique as
        /// AiProductionPolicy.Hash (no System.Random; same input, same output always). Serves as the
        /// randomness source for the HedgePick draws.</summary>
        public static uint Hash(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352dU;
                x ^= x >> 15;
                x *= 0x846ca68bU;
                x ^= x >> 16;
                return x;
            }
        }

        /// <summary>Returns the index array of scores[0..length) stably sorted descending (ties keep the
        /// scores array's original order). Shared by HedgeOrder/HedgePickIndex.</summary>
        private static int[] SortDescendingIndices(float[] scores, int length)
        {
            int[] order = new int[length];
            for (int i = 0; i < length; i++) order[i] = i;

            for (int i = 1; i < length; i++)
            {
                int key = order[i];
                float keyScore = scores[key];
                int j = i - 1;
                while (j >= 0 && scores[order[j]] < keyScore)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = key;
            }
            return order;
        }

        /// <summary>From the first topCount entries of the descending-sorted order array, draws one via
        /// the seed hash with weights proportional to score², returning its position (0..topCount-1)
        /// within the order array. This is the heart of the design requirement's "hedge (mixed strategy)":
        /// always taking the argmax (position 0) is completely readable by the opponent (= the old
        /// "monotonous production" itself), so the runner-up and third place always retain some
        /// probability. Why score² (sharper than linear weights): it keeps the bias toward the front
        /// runner while giving a closely-scored 2nd/3rd enough frequency, approximating the target
        /// distribution "usually the best response, occasionally the runner-up or third". When the top
        /// scores sum to 0 or less (degenerate cases: wiped out, no matchup data), the front runner
        /// (position 0) is always returned.</summary>
        private static int PickHedgedPosition(float[] scores, int[] order, int topCount, uint seed)
        {
            if (topCount <= 1) return 0;

            float totalWeight = 0f;
            for (int i = 0; i < topCount; i++)
            {
                float s = scores[order[i]];
                float w = s > 0f ? s * s : 0f;
                totalWeight += w;
            }
            if (totalWeight <= 0f) return 0;

            uint h = Hash(seed);
            float roll = (h % 1000000u) / 1000000f * totalWeight;

            float cumulative = 0f;
            for (int i = 0; i < topCount; i++)
            {
                float s = scores[order[i]];
                float w = s > 0f ? s * s : 0f;
                cumulative += w;
                if (roll < cumulative) return i;
            }
            return topCount - 1;
        }

        /// <summary>Given candidates[i] scored by scores[i], hedge-draws one entry from the top
        /// min(3,length) and returns a "preference order" array with it in front (the rest follow in
        /// descending score order). Used by AiProductionPolicy.Decide's category selection: starting from
        /// the front (the hedge draw), each is tried for "can a tier of this actually be bought right
        /// now", falling back to the next on failure (the same safety net as the old cross-category
        /// fallback, preserved on top of the hedged ordering).</summary>
        public static UnitCategory[] HedgeOrder(UnitCategory[] candidates, float[] scores, uint seed)
        {
            int n = candidates.Length;
            int[] order = SortDescendingIndices(scores, n);
            int topCount = n < 3 ? n : 3;
            int hedgePos = PickHedgedPosition(scores, order, topCount, seed);

            UnitCategory[] result = new UnitCategory[n];
            result[0] = candidates[order[hedgePos]];
            int w2 = 1;
            for (int i = 0; i < n; i++)
            {
                if (i == hedgePos) continue;
                result[w2++] = candidates[order[i]];
            }
            return result;
        }

        /// <summary>Hedge-draws one entry from the top min(3,length) of scores[0..length) and returns its
        /// index within the scores array. Used by AiProductionPolicy.ChooseTierHedged's tier selection
        /// (the quality-vs-quantity hedge).</summary>
        public static int HedgePickIndex(float[] scores, int length, uint seed)
        {
            int[] order = SortDescendingIndices(scores, length);
            int topCount = length < 3 ? length : 3;
            int hedgePos = PickHedgedPosition(scores, order, topCount, seed);
            return order[hedgePos];
        }
    }
}
