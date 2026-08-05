namespace CSWarfront.Core
{
    /// <summary>The action AI auto-production takes in one decision (Task46).</summary>
    public enum AiSpendChoice { None, Research, Produce }

    /// <summary>Result of AiProductionPolicy.Decide. TypeKey is valid only when Choice==Produce.</summary>
    public struct AiDecision
    {
        public AiSpendChoice Choice;
        public string TypeKey;
    }

    /// <summary>
    /// The AI's auto-production policy (Task46; fully reworked on game-theoretic lines in Task80).
    ///
    /// The Task46 design was a static rule — "steer the force composition toward fixed target shares
    /// (Tank 30% / MechInf 20% / ...)". That cured the infantry spam caused by the even older rule
    /// ("buy the most expensive unit currently affordable"), but it kept building the same mix no matter
    /// what the opposing side fielded — creating the new problem the user reported as "unit production is
    /// monotonous".
    ///
    /// Task80 replaces this with three stages — observe → payoff → hedge (mixed strategy) — see
    /// AiThreatAssessment/AiCombatScoring; the detailed theory is documented in those classes' XML comments:
    ///   1. Observe: tally the composition of living hostile units and external threats per category and
    ///      normalize (AiThreatAssessment.Observe). With no hostile units at all it is "peacetime" and the
    ///      Task46 fixed target shares are used as-is (backward compatible; good for peacetime build-up).
    ///   2. Payoff: for each candidate category, compute the expected effectiveness E[c] against that enemy
    ///      mix (AiCombatScoring.ExpectedEffectiveness) — the textbook game-theory notion of a best response
    ///      to the opponent's observed mixed strategy.
    ///   3. Hedge: always picking the argmax of E[] (a pure strategy) lets the opponent read what comes
    ///      next — another form of "monotonous". Drawing from the top three candidates with weights
    ///      proportional to E[]² (AiCombatScoring.HedgeOrder) yields behavior equivalent to a mixed
    ///      strategy: usually the best response, but occasionally the runner-up or third pick.
    ///
    /// Tier choice applies the same hedging idea (ChooseTierHedged): "quality (one high tier)" vs
    /// "quantity (several low tiers across multiple decisions)" are compared by cost efficiency
    /// (AiCombatScoring.TierValue) and drawn with a bias toward higher tiers. The probability of routing
    /// funds into research also moves from the old fixed 35% to a formula driven by wealth headroom, tier
    /// lag (behind) and numerical inferiority (outnumbered) — see ResearchProbability.
    ///
    /// Deterministic, no RNG: all "random-looking" behavior derives from integer hashes of the seed
    /// (System.Random is never used; the same (state, faction, base, seed) always yields the same result).
    /// No UnityEngine dependency.
    /// </summary>
    public static class AiProductionPolicy
    {
        /// <summary>Funds floor below which nothing is routed into research (Task46). While
        /// UnlockedTier&lt;5, ProductionPlanning's unit selection also spends only the budget left above
        /// this amount (so production cannot consume the capital earmarked for research).</summary>
        public const float ResearchReserve = 150f;

        /// <summary>Fixed amount passed to Research.TryInvest per decision when research is chosen (Task46).</summary>
        public const float ResearchInvestPerDecision = 50f;

        // --- Task80: situation-adaptive research probability (was: fixed 35%) ---
        // pResearch = clamp(Base + BehindWeight*behindTiers + WealthWeight*wealthFactor
        //                        - OutnumberedWeight*outnumberedFactor, Min, Max)
        // See ResearchProbability() for the intent of each term.
        private const float ResearchBase = 0.15f;
        private const float ResearchBehindWeight = 0.25f;
        private const float ResearchWealthWeight = 0.15f;
        private const float ResearchOutnumberedWeight = 0.3f;
        private const float ResearchProbabilityMin = 0.05f;
        private const float ResearchProbabilityMax = 0.6f;

        /// <summary>Reserve-funds headroom at which wealthFactor reaches 1.0 (full contribution):
        /// Treasury - ResearchReserve reaching this amount counts as "comfortably wealthy".</summary>
        private const float WealthComfortRange = 500f;

        /// <summary>Tier gap at which behindTiers reaches 1.0 (full contribution). Tiers run 1..5, so the
        /// maximum gap is 4.</summary>
        private const float TierBehindRange = 4f;

        /// <summary>Target composition shares (peacetime fallback only; unchanged since Task46). The six
        /// land categories including drone infantry/infantry. AntiAir stays excluded in peacetime (no aerial
        /// targets exist) — AA is inherently reactive equipment against an air threat, and there is no
        /// reason to mass-produce it in peace; a deliberate design choice (the same exclusion rationale as
        /// Task46). Task61: used when the base is Army (can spawn Domain.Land).</summary>
        private static readonly UnitCategory[] LandCategories =
        {
            UnitCategory.Tank, UnitCategory.MechInfantry, UnitCategory.Infantry,
            UnitCategory.Apc, UnitCategory.Artillery, UnitCategory.DroneInfantry
        };

        private static readonly float[] LandTargets = { 0.30f, 0.20f, 0.20f, 0.15f, 0.10f, 0.05f };

        /// <summary>Task80: land candidate categories while at war (enemy mix observable). LandCategories
        /// plus AntiAir — the old rule hard-excluded AntiAir as "no aerial targets exist" (see the
        /// Category_choice_never_picks_AntiAir test), but Task61 implemented air units (AirSuperiority/
        /// TacticalBomber/SuicideDrone) and the matchup row AntiAir vs air categories = 2.5x already exists
        /// in CombatMatchup. When the enemy mix contains an air share, AiCombatScoring.ExpectedEffectiveness
        /// naturally boosts AntiAir's expected value — simply lifting the exclusion here makes AA production
        /// emergent, with no special-casing needed in the scoring.</summary>
        private static readonly UnitCategory[] LandWartimeCategories =
        {
            UnitCategory.Tank, UnitCategory.MechInfantry, UnitCategory.Infantry,
            UnitCategory.Apc, UnitCategory.Artillery, UnitCategory.DroneInfantry, UnitCategory.AntiAir
        };

        /// <summary>Task61: used when the base is Navy (can spawn Domain.Sea). Destroyers as the mainstay,
        /// carriers few (an expensive platform, not fielded in numbers). NavalUnitRoster defines only these
        /// two, so the candidates are the same in peace and war (no hidden exclusion like LandCategories).</summary>
        private static readonly UnitCategory[] SeaCategories = { UnitCategory.Destroyer, UnitCategory.Carrier };
        private static readonly float[] SeaTargets = { 0.7f, 0.3f };

        /// <summary>Task61: used when the base is AirForce (can spawn Domain.Air). Fighters for air
        /// superiority as the mainstay, bombers for ground strike, and a few expendable suicide drones.
        /// AirUnitRoster defines only these, so the candidates are the same in peace and war.</summary>
        private static readonly UnitCategory[] AirCategories =
        {
            UnitCategory.AirSuperiority, UnitCategory.TacticalBomber, UnitCategory.SuicideDrone,
            UnitCategory.AttackHelicopter // Task101: attack helicopters are also a regular air-base category
        };
        private static readonly float[] AirTargets = { 0.35f, 0.30f, 0.15f, 0.20f };

        /// <summary>Task61: picks the peacetime-fallback composition table from the base's SpawnableDomains.
        /// Sea → SeaCategories/SeaTargets, Air → AirCategories/AirTargets, anything else (including Army) →
        /// the traditional LandCategories/LandTargets (the default all existing tests assume).</summary>
        private static void CategoriesFor(DomainMask spawnableDomains, out UnitCategory[] categories, out float[] targets)
        {
            if (DomainMaskUtil.Contains(spawnableDomains, Domain.Sea))
            {
                categories = SeaCategories; targets = SeaTargets;
            }
            else if (DomainMaskUtil.Contains(spawnableDomains, Domain.Air))
            {
                categories = AirCategories; targets = AirTargets;
            }
            else
            {
                categories = LandCategories; targets = LandTargets;
            }
        }

        /// <summary>Task80: picks the wartime candidate list (enemy-mix-based scoring) from the base's
        /// SpawnableDomains. Everything except Land uses the exact same arrays as CategoriesFor() (they
        /// have no hidden exclusions).</summary>
        private static UnitCategory[] WartimeCandidatesFor(DomainMask spawnableDomains)
        {
            if (DomainMaskUtil.Contains(spawnableDomains, Domain.Sea)) return SeaCategories;
            if (DomainMaskUtil.Contains(spawnableDomains, Domain.Air)) return AirCategories;
            return LandWartimeCategories;
        }

        /// <summary>Jitter width applied to the deviation from target shares (Task46, peacetime fallback
        /// only). A small wobble so several bases of one faction in the same tick do not all pick the
        /// identical category. Kept well below the smallest gap between target shares (5%) so it never
        /// overturns the composition policy itself.</summary>
        private const float CompositionJitter = 0.02f;

        // Task80: fixed salts that decorrelate the wartime hedge draws (HedgeOrder/HedgePickIndex) from the
        // other draws inside the same decision (the research roll, subsequent fallback attempts). Each is
        // just "hash(seed ^ salt)" rather than the raw seed (see AiCombatScoring.Hash), but changing the
        // salt effectively yields an independent random sequence.
        private const uint CategoryHedgeSalt = 0xB5297A4Du;
        private const uint TierHedgeSalt = 0x68E31DA4u;

        public static AiDecision Decide(WarState state, Faction faction, MilitaryBase b, uint seed)
        {
            DomainMask domains = b.SpawnableDomains;
            AiThreatAssessment.EnemyMix mix = AiThreatAssessment.Observe(state, faction.Id);

            // Bootstrap rule (Task46 fix, unchanged by Task80): a faction with zero living units
            // (across ALL domains, not just this base's) needs troops before it needs research.
            bool hasUnits = CountTotalLivingUnits(state, faction.Id) > 0;

            if (faction.UnlockedTier < 5 && faction.Treasury >= ResearchReserve)
            {
                float pResearch = ResearchProbability(state, faction);
                uint researchRoll = AiCombatScoring.Hash(seed) % 1000;
                if (researchRoll < (uint)(pResearch * 1000f))
                    return new AiDecision { Choice = AiSpendChoice.Research, TypeKey = null };
            }

            float spendCap = (faction.UnlockedTier < 5 && hasUnits)
                ? faction.Treasury - ResearchReserve
                : faction.Treasury;
            if (spendCap < 0f) spendCap = 0f;

            UnitCategory[] preference;
            if (mix.HasHostiles)
            {
                // --- Task80: best-response-with-hedging (wartime) ---
                UnitCategory[] candidates = WartimeCandidatesFor(domains);
                float[] scores = new float[candidates.Length];
                for (int i = 0; i < candidates.Length; i++)
                    scores[i] = AiCombatScoring.ExpectedEffectiveness(candidates[i], mix.Shares);

                preference = AiCombatScoring.HedgeOrder(candidates, scores, AiCombatScoring.Hash(seed ^ CategoryHedgeSalt));
            }
            else
            {
                // --- Task46 fallback (peacetime, fixed target shares; the legacy logic kept unchanged) ---
                CategoriesFor(domains, out UnitCategory[] categories, out float[] targets);
                CountByCategory(state, faction.Id, categories, out int[] counts, out int totalUnits);
                preference = CategoryPreferenceOrder(categories, targets, counts, totalUnits, seed);
            }

            // Cross-category fallback (Task46 fix, preserved under Task80): walk the (now hedge-
            // ordered, in wartime) preference list and return the first category with an
            // affordable+unlocked tier, instead of stalling when the top pick is unaffordable.
            for (int i = 0; i < preference.Length; i++)
            {
                uint tierSeed = AiCombatScoring.Hash(seed ^ TierHedgeSalt ^ (uint)(i * 2654435761u));
                UnitType type = ChooseTierHedged(state, faction, preference[i], faction.UnlockedTier, spendCap, tierSeed);
                if (type != null)
                    return new AiDecision { Choice = AiSpendChoice.Produce, TypeKey = type.TypeKey };
            }

            return new AiDecision { Choice = AiSpendChoice.None, TypeKey = null };
        }

        /// <summary>Task80: probability of routing funds into research. Replaces the old fixed 35% with a
        /// formula that moves within 0.05..0.6:
        ///   pResearch = clamp(0.15 + 0.25×behindTiers + 0.15×wealthFactor - 0.3×outnumberedFactor,
        ///                      0.05, 0.6)
        ///   behindTiers     = how far the faction's average tier trails the most advanced hostile
        ///                      faction's (0..1, capped at a 4-tier gap). The further behind, the more
        ///                      research is prioritized "to catch up".
        ///   wealthFactor    = headroom of reserve funds (Treasury - ResearchReserve) (0..1, capped at
        ///                      500). The fuller the coffers, the more room for research.
        ///   outnumberedFactor = degree of numerical inferiority (0..1, capped at 2:1). The direr the
        ///                      inferiority, the more urgent production of immediate troops is preferred
        ///                      over research (the negative term).
        /// The reference amounts are design-tuning values chosen so the effect clearly kicks in given this
        /// game's balance (ResearchReserve=150, tiers 1..5).</summary>
        private static float ResearchProbability(WarState state, Faction faction)
        {
            float ownAvgTier = AiThreatAssessment.AverageFieldedTier(state, faction.Id);
            float strongestHostileAvgTier = AiThreatAssessment.StrongestHostileAverageTier(state, faction.Id);

            float behindTiers = 0f;
            if (strongestHostileAvgTier > ownAvgTier)
            {
                behindTiers = (strongestHostileAvgTier - ownAvgTier) / TierBehindRange;
                if (behindTiers > 1f) behindTiers = 1f;
            }

            float wealthFactor = (faction.Treasury - ResearchReserve) / WealthComfortRange;
            if (wealthFactor < 0f) wealthFactor = 0f;
            if (wealthFactor > 1f) wealthFactor = 1f;

            float outnumberedFactor = AiThreatAssessment.OutnumberedFactor(state, faction.Id);

            float p = ResearchBase
                + ResearchBehindWeight * behindTiers
                + ResearchWealthWeight * wealthFactor
                - ResearchOutnumberedWeight * outnumberedFactor;

            if (p < ResearchProbabilityMin) p = ResearchProbabilityMin;
            if (p > ResearchProbabilityMax) p = ResearchProbabilityMax;
            return p;
        }

        /// <summary>Total living units the faction owns (any domain). Used only by Decide's bootstrap check
        /// (whether to waive the research reserve). Unlike CountByCategory, it does not filter to a
        /// particular categories array — e.g. a navy-only faction making a decision at a land base must not
        /// be misjudged as "a brand-new faction with no units at all" (a generalization matching Task80,
        /// where AntiAir/Sea/Air can all actually be produced).</summary>
        private static int CountTotalLivingUnits(WarState state, byte factionId)
        {
            int total = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (u.IsAlive && u.FactionId == factionId) total++;
            }
            return total;
        }

        /// <summary>Counts the faction's living units per category (peacetime fallback only; unchanged
        /// since Task46). Used by Decide's target-share deviation computation (CategoryPreferenceOrder).</summary>
        private static void CountByCategory(WarState state, byte factionId, UnitCategory[] categories, out int[] counts, out int total)
        {
            counts = new int[categories.Length];
            total = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null) continue;
                int idx = IndexOf(categories, t.Category);
                if (idx < 0) continue;
                counts[idx]++;
                total++;
            }
        }

        /// <summary>Returns the categories ordered by how far below their target share they are (= most
        /// deficient first) (Task46: peacetime fallback only, logic unchanged). Ties keep the categories
        /// array order (deterministic, stable sort). A small seed-derived jitter is added to each
        /// category's deviation so several bases of the same faction do not all converge on the same
        /// category in near-tie situations.</summary>
        private static UnitCategory[] CategoryPreferenceOrder(UnitCategory[] categories, float[] targets, int[] counts, int total, uint seed)
        {
            float[] deficits = new float[categories.Length];
            uint jitterSeed = AiCombatScoring.Hash(seed ^ 0x9E3779B9u);
            for (int i = 0; i < categories.Length; i++)
            {
                float share = total > 0 ? (float)counts[i] / total : 0f;
                float deficit = targets[i] - share;

                uint categoryHash = AiCombatScoring.Hash(jitterSeed + (uint)i * 2654435761u);
                float jitter = ((categoryHash % 1000) / 1000f - 0.5f) * 2f * CompositionJitter;
                deficits[i] = deficit + jitter;
            }

            int[] order = new int[categories.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;

            // Stable descending insertion sort: only shifts on strictly-smaller deficit, so ties
            // keep their original categories-array order (same tie-break as the old single-winner
            // loop's strict `>` comparison, which favored the first-seen index).
            for (int i = 1; i < order.Length; i++)
            {
                int key = order[i];
                float keyDeficit = deficits[key];
                int j = i - 1;
                while (j >= 0 && deficits[order[j]] < keyDeficit)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = key;
            }

            UnitCategory[] result = new UnitCategory[categories.Length];
            for (int i = 0; i < order.Length; i++) result[i] = categories[order[i]];
            return result;
        }

        private static int IndexOf(UnitCategory[] categories, UnitCategory category)
        {
            for (int i = 0; i < categories.Length; i++)
                if (categories[i] == category) return i;
            return -1;
        }

        /// <summary>Maximum tier count (rosters always run tiers 1..5). Sizes ChooseTierHedged's stack arrays.</summary>
        private const int MaxTierCount = 5;

        /// <summary>Task80: for the given category, returns one of the currently buyable tiers (at or below
        /// unlockedTier and spendCap) drawn by hedge over AiCombatScoring.TierValue (the quality-vs-quantity
        /// cost efficiency). (Old: ChooseHighestAffordableTier always uniquely picked the highest tier.)
        /// Returns null when no tier is buyable (the caller's cross-category fallback tries the next
        /// category). All three rosters (Land/Sea/Air) share the TypeKey shape "&lt;Category&gt;_T&lt;tier&gt;"
        /// (see *UnitRoster.TypeKey), so keys are assembled directly instead of scanning Types.All().</summary>
        private static UnitType ChooseTierHedged(WarState state, Faction faction, UnitCategory category, byte unlockedTier, float spendCap, uint seed)
        {
            UnitType[] candidates = new UnitType[MaxTierCount];
            float[] scores = new float[MaxTierCount];
            int count = 0;

            byte maxTier = unlockedTier > MaxTierCount ? (byte)MaxTierCount : unlockedTier;
            for (byte tier = 1; tier <= maxTier; tier++)
            {
                UnitType t = state.Types.Get(category + "_T" + tier);
                if (t == null) continue; // category missing from this roster (defensive; should not happen)
                // Task99: three-resource economy. Affordability is judged on manpower + production (funds
                // substitution up to spendCap) — was the funds-only test t.Cost > spendCap.
                if (!UnitCosts.CanAfford(faction, t, spendCap)) continue;

                candidates[count] = t;
                scores[count] = AiCombatScoring.TierValue(t);
                count++;
            }

            if (count == 0) return null;
            if (count == 1) return candidates[0];

            int idx = AiCombatScoring.HedgePickIndex(scores, count, seed);
            return candidates[idx];
        }
    }
}
