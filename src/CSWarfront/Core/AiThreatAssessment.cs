using System;

namespace CSWarfront.Core
{
    /// <summary>
    /// The "observe" phase of the game-theoretic AI production rework (Task80). Vectorizes the enemy
    /// composition and provides the aggregates used for the research/production situation call (own
    /// average tier, strongest hostile faction's average tier, degree of numerical inferiority).
    /// Pure logic called only from AiProductionPolicy.Decide; deterministic (no RNG); no UnityEngine
    /// dependency.
    ///
    /// Design intent: the old policy always aimed at fixed shares (Tank 30% / MechInf 20% / ...), building
    /// the same mix no matter what the opposing side fielded (user report: "unit production is
    /// monotonous"). This policy instead counts "what the enemies we are actually fighting (or facing as
    /// hostiles) are fielding right now" and switches to expected effectiveness against that mix
    /// (AiCombatScoring.ExpectedEffectiveness) — in standard game-theory terms, a switch from a fixed
    /// strategy to a best response against the opponent's observed mixed strategy.
    /// </summary>
    public static class AiThreatAssessment
    {
        /// <summary>Total member count of UnitCategory; the length of the enemy-mix vector (the Shares
        /// array). The same derivation as CombatMatchup.CategoryCount, computed independently here to
        /// avoid depending on the other core class (the values necessarily agree — both reference the
        /// UnitCategory enum itself).</summary>
        public static readonly int CategoryCount = Enum.GetValues(typeof(UnitCategory)).Length;

        /// <summary>Weight per living unit of a Nemesis (Relation.Nemesis) faction. Counted at twice an
        /// ordinary Hostile (design requirement: "Nemesis counts double weight — priority enemy").</summary>
        public const float NemesisWeight = 2f;

        /// <summary>Weight per living unit of an ordinary Hostile faction.</summary>
        public const float HostileWeight = 1f;

        /// <summary>Weight per living external threat (KAIJU/Alien, Task58/59). External threats have no
        /// UnitCategory (they are not faction units), but per the design requirement they count as a
        /// "heavy armor (Tank-equivalent)" pseudo-category ("Kaiju/Alien ≈ heavy armor for countering
        /// purposes"). This constant is that weight itself (one threat = one ordinary Hostile unit's
        /// weight).</summary>
        public const float ThreatWeight = 1f;

        /// <summary>The pseudo-category assigned when converting external threats to a UnitCategory
        /// (Tank-like).</summary>
        public const UnitCategory ThreatPseudoCategory = UnitCategory.Tank;

        /// <summary>The observed enemy mix. Shares is the normalized composition indexed by
        /// (int)UnitCategory (sums to 1.0). While HasHostiles is false, Shares is all zeros (= not a
        /// single living hostile unit/threat = peacetime).</summary>
        public struct EnemyMix
        {
            public float[] Shares;
            public bool HasHostiles;
        }

        /// <summary>Counts living units and external threats hostile from factionId's view
        /// (Hostile/Nemesis in the RelationMatrix, or Hostile/Nemesis in ThreatRelations) per
        /// UnitCategory and returns the normalized shares. Units of a Nemesis faction count at double
        /// weight (the Task80 design). With no qualifying enemy at all, HasHostiles=false (the caller
        /// treats that as "peacetime" and falls back to the legacy fixed target shares).</summary>
        public static EnemyMix Observe(WarState state, byte factionId)
        {
            float[] weights = new float[CategoryCount];
            float total = 0f;

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId == factionId) continue;

                Relation rel = state.Relations.Get(factionId, u.FactionId);
                if (!rel.IsHostile()) continue;

                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null) continue;

                float w = rel == Relation.Nemesis ? NemesisWeight : HostileWeight;
                weights[(int)t.Category] += w;
                total += w;
            }

            for (int i = 0; i < state.Threats.Count; i++)
            {
                ExternalThreat threat = state.Threats[i];
                if (threat.IsDefeated) continue;

                Relation rel = state.ThreatRelations.Get(factionId, threat.Kind);
                if (!rel.IsHostile()) continue;

                float w = rel == Relation.Nemesis ? NemesisWeight * ThreatWeight : ThreatWeight;
                weights[(int)ThreatPseudoCategory] += w;
                total += w;
            }

            EnemyMix mix;
            mix.HasHostiles = total > 0f;
            mix.Shares = new float[CategoryCount];
            if (mix.HasHostiles)
            {
                for (int i = 0; i < CategoryCount; i++)
                    mix.Shares[i] = weights[i] / total;
            }
            return mix;
        }

        /// <summary>Average tier (0..5) of the living units factionId currently fields; 0 with no units.
        /// Used by the research probability's "are we behind the enemy" check
        /// (AiProductionPolicy.ResearchProbability).</summary>
        public static float AverageFieldedTier(WarState state, byte factionId)
        {
            int count = 0;
            int tierSum = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null) continue;
                tierSum += t.Tier;
                count++;
            }
            return count > 0 ? (float)tierSum / count : 0f;
        }

        /// <summary>Returns the highest average tier among the non-Eliminated factions hostile to
        /// factionId. 0 when no hostile faction exists or none of them fields any units.</summary>
        public static float StrongestHostileAverageTier(WarState state, byte factionId)
        {
            float best = 0f;
            for (int i = 0; i < state.Factions.Count; i++)
            {
                Faction other = state.Factions[i];
                if (other.Id == factionId || other.Eliminated) continue;

                Relation rel = state.Relations.Get(factionId, other.Id);
                if (!rel.IsHostile()) continue;

                float avg = AverageFieldedTier(state, other.Id);
                if (avg > best) best = avg;
            }
            return best;
        }

        /// <summary>Returns the degree of numerical inferiority from 0 (even or better) to 1 (fully
        /// outnumbered at 2:1 or worse). With zero own units and at least one hostile unit, returns 1 as
        /// the worst possible inferiority. With no hostile units at all, returns 0 (not outnumbered).
        /// Used by the research probability formula (AiProductionPolicy.ResearchProbability) to shift
        /// funds from research toward emergency production the direr the inferiority (the formula's
        /// negative term).</summary>
        public static float OutnumberedFactor(WarState state, byte factionId)
        {
            int own = 0;
            int hostile = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (u.FactionId == factionId) { own++; continue; }

                Relation rel = state.Relations.Get(factionId, u.FactionId);
                if (rel.IsHostile()) hostile++;
            }

            if (own == 0) return hostile > 0 ? 1f : 0f;

            float factor = (float)hostile / own - 1f; // 0 at 1:1, 1 at 2:1
            if (factor < 0f) factor = 0f;
            if (factor > 1f) factor = 1f;
            return factor;
        }
    }
}
