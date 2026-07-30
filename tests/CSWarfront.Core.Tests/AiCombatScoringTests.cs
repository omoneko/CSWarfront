using CSWarfront.Core;
using Xunit;

// Task80: AiCombatScoring is the "payoff" (ExpectedEffectiveness/Survivability/TierValue) and
// "hedge" (HedgeOrder/HedgePickIndex) phase of the game-theoretic AI production policy. These
// tests exercise it directly, independent of AiProductionPolicy.Decide, including the worked
// example from the task report (enemy 60% Tank / 30% Infantry / 10% Air).
public class AiCombatScoringTests
{
    // --- ExpectedEffectiveness / Survivability: the worked example ---

    [Fact]
    public void Worked_example_60pct_tank_30pct_infantry_10pct_air_ranks_DroneInfantry_highest()
    {
        float[] shares = NewShares();
        shares[(int)UnitCategory.Tank] = 0.6f;
        shares[(int)UnitCategory.Infantry] = 0.3f;
        shares[(int)UnitCategory.AirSuperiority] = 0.1f;

        // Hand-computed in the task report: DroneInfantry(~0.970) > Tank(~0.749) > Artillery(~0.602)
        // > Apc(~0.538) > MechInfantry(~0.533) > AntiAir(~0.442) > Infantry(~0.428).
        float drone = AiCombatScoring.ExpectedEffectiveness(UnitCategory.DroneInfantry, shares);
        float tank = AiCombatScoring.ExpectedEffectiveness(UnitCategory.Tank, shares);
        float artillery = AiCombatScoring.ExpectedEffectiveness(UnitCategory.Artillery, shares);
        float apc = AiCombatScoring.ExpectedEffectiveness(UnitCategory.Apc, shares);
        float mech = AiCombatScoring.ExpectedEffectiveness(UnitCategory.MechInfantry, shares);
        float antiAir = AiCombatScoring.ExpectedEffectiveness(UnitCategory.AntiAir, shares);
        float infantry = AiCombatScoring.ExpectedEffectiveness(UnitCategory.Infantry, shares);

        Assert.Equal(0.970f, drone, 2);
        Assert.Equal(0.749f, tank, 2);
        Assert.Equal(0.602f, artillery, 2);
        Assert.Equal(0.538f, apc, 2);
        Assert.Equal(0.533f, mech, 2);
        Assert.Equal(0.442f, antiAir, 2);
        Assert.Equal(0.428f, infantry, 2);

        Assert.True(drone > tank);
        Assert.True(tank > artillery);
        Assert.True(artillery > apc);
        Assert.True(apc > mech);
        Assert.True(mech > antiAir);
        Assert.True(antiAir > infantry);
    }

    [Fact]
    public void ExpectedEffectiveness_is_zero_against_an_empty_enemy_mix()
    {
        float[] shares = NewShares(); // all zero
        Assert.Equal(0f, AiCombatScoring.ExpectedEffectiveness(UnitCategory.Tank, shares));
    }

    [Fact]
    public void Survivability_is_lower_when_the_enemy_mix_counters_the_category_hard()
    {
        // DroneInfantry counters Tank hard (2.0x), so a pure-Tank enemy mix hits back at DroneInfantry
        // relatively softly (Tank->DroneInfantry = 1.1x) compared to how hard a pure-DroneInfantry-style
        // enemy would (not modeled here) - instead we directly compare survivability of Tank (which the
        // enemy Tank mix hits at Tank->Tank=1.0x default) vs Apc (Tank->Apc=1.4x, hit harder).
        float[] pureTankMix = NewShares();
        pureTankMix[(int)UnitCategory.Tank] = 1f;

        float survTank = AiCombatScoring.Survivability(UnitCategory.Tank, pureTankMix);
        float survApc = AiCombatScoring.Survivability(UnitCategory.Apc, pureTankMix);
        Assert.True(survTank > survApc, "Tank (hit at 1.0x by enemy Tanks) should survive better than Apc (hit at 1.4x)");
    }

    private static float[] NewShares()
    {
        return new float[AiThreatAssessment.CategoryCount];
    }

    // --- TierValue: cost-effectiveness responds to the actual stats, not just "always low tier" ---

    [Fact]
    public void TierValue_favours_the_unit_whose_stats_outpace_its_cost()
    {
        // This roster's real TierScaling curve (HP+35%/Attack+40%/Cost+60% per Tier) makes
        // TierValue decrease with Tier - but the formula itself, sqrt(HP*Attack)/Cost, is a pure
        // function of stats: if a "Tier" happened to scale stats faster than cost, TierValue must
        // say so. This proves the formula is genuinely data-driven, not hard-coded to prefer cheap
        // units.
        var cheapButInefficient = new UnitType("A", Domain.Land, UnitCategory.Tank, 1,
            maxHp: 200f, attack: 20f, range: 10f, armor: 1f, speed: 1f, splashRadius: 0f,
            cost: 40f, buildTime: 1f, assetPrefabName: "", accuracy: 0.5f, fireIntervalHours: 1f,
            shotKind: ShotKind.DirectFire, canTargetDomains: DomainMask.Land);
        var expensiveButEfficient = new UnitType("B", Domain.Land, UnitCategory.Tank, 2,
            maxHp: 1000f, attack: 1000f, range: 10f, armor: 1f, speed: 1f, splashRadius: 0f,
            cost: 50f, buildTime: 1f, assetPrefabName: "", accuracy: 0.5f, fireIntervalHours: 1f,
            shotKind: ShotKind.DirectFire, canTargetDomains: DomainMask.Land);

        float cheapValue = AiCombatScoring.TierValue(cheapButInefficient);   // sqrt(4000)/40 ~= 1.58
        float efficientValue = AiCombatScoring.TierValue(expensiveButEfficient); // sqrt(1,000,000)/50 = 20

        Assert.True(efficientValue > cheapValue,
            "a unit whose HP*Attack vastly outpaces its cost must score higher regardless of being pricier");
    }

    [Fact]
    public void TierValue_matches_the_documented_formula()
    {
        var t = new UnitType("C", Domain.Land, UnitCategory.Infantry, 1,
            maxHp: 100f, attack: 25f, range: 10f, armor: 1f, speed: 1f, splashRadius: 0f,
            cost: 10f, buildTime: 1f, assetPrefabName: "", accuracy: 0.5f, fireIntervalHours: 1f,
            shotKind: ShotKind.Gunfire, canTargetDomains: DomainMask.Land);

        // sqrt(100*25)/10 = sqrt(2500)/10 = 50/10 = 5
        Assert.Equal(5f, AiCombatScoring.TierValue(t), 3);
    }

    // --- Hedge: top-3 by score^2, deterministic, never wanders outside the top-3 ---

    [Fact]
    public void HedgeOrder_only_ever_places_a_top_three_scorer_first()
    {
        var candidates = new[]
        {
            UnitCategory.Tank, UnitCategory.Infantry, UnitCategory.Apc,
            UnitCategory.Artillery, UnitCategory.MechInfantry
        };
        // Descending scores by construction: Tank(5) > Infantry(4) > Apc(3) > Artillery(2) > MechInfantry(1).
        float[] scores = { 5f, 4f, 3f, 2f, 1f };

        var seen = new System.Collections.Generic.HashSet<UnitCategory>();
        for (uint seed = 0; seed < 500; seed++)
        {
            UnitCategory[] order = AiCombatScoring.HedgeOrder(candidates, scores, seed);
            seen.Add(order[0]);
            // full order must always be a permutation of all candidates
            Assert.Equal(candidates.Length, order.Length);
        }

        Assert.Contains(UnitCategory.Tank, seen);
        Assert.Contains(UnitCategory.Infantry, seen);
        Assert.Contains(UnitCategory.Apc, seen);
        Assert.DoesNotContain(UnitCategory.Artillery, seen);   // rank 4, outside top-3
        Assert.DoesNotContain(UnitCategory.MechInfantry, seen); // rank 5, outside top-3
    }

    [Fact]
    public void HedgeOrder_is_deterministic_for_the_same_seed()
    {
        var candidates = new[] { UnitCategory.Tank, UnitCategory.Infantry, UnitCategory.Apc };
        float[] scores = { 3f, 2f, 1f };

        UnitCategory[] first = AiCombatScoring.HedgeOrder(candidates, scores, 42u);
        for (int i = 0; i < 5; i++)
        {
            UnitCategory[] again = AiCombatScoring.HedgeOrder(candidates, scores, 42u);
            Assert.Equal(first[0], again[0]);
        }
    }

    [Fact]
    public void HedgeOrder_always_picks_the_only_candidate_when_there_is_just_one()
    {
        var candidates = new[] { UnitCategory.Tank };
        float[] scores = { 1f };
        for (uint seed = 0; seed < 20; seed++)
        {
            UnitCategory[] order = AiCombatScoring.HedgeOrder(candidates, scores, seed);
            Assert.Equal(UnitCategory.Tank, order[0]);
        }
    }

    [Fact]
    public void HedgePickIndex_only_ever_returns_indices_within_the_requested_length()
    {
        float[] scores = { 1f, 5f, 3f, 2f };
        for (uint seed = 0; seed < 300; seed++)
        {
            int idx = AiCombatScoring.HedgePickIndex(scores, 3, seed); // only consider the first 3
            Assert.True(idx >= 0 && idx < 3, $"index {idx} out of the requested range [0,3)");
        }
    }
}
