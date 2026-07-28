using CSWarfront.Core;
using Xunit;

public class AiProductionPolicyTests
{
    private static WarState WithFullRoster(float treasury, byte unlockedTier)
    {
        var s = new WarState();
        LandUnitRoster.RegisterAll(s.Types);
        var f = new Faction(0, "Red");
        f.AddTreasury(treasury);
        f.UnlockedTier = unlockedTier;
        s.Factions.Add(f);
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);
        return s;
    }

    private static void AddLivingUnits(WarState s, UnitCategory category, byte tier, int count)
    {
        string key = LandUnitRoster.TypeKey(category, tier);
        for (int i = 0; i < count; i++)
        {
            s.Units.Add(new UnitInstance(s.AllocInstanceId(), key, 0, 100f, new WorldPos(0, 0, 0)));
        }
    }

    // --- Composition: largest deficit wins ---

    [Fact]
    public void Fresh_faction_with_no_units_and_plenty_of_money_produces_a_tank_not_infantry()
    {
        // UnlockedTier=5 removes the research reserve/consideration entirely so this test isolates
        // the composition rule (Tank has the highest target share, 30%, of any tracked category).
        var s = WithFullRoster(10000f, unlockedTier: 5);
        AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed: 12345u);

        Assert.Equal(AiSpendChoice.Produce, d.Choice);
        UnitType chosen = s.Types.Get(d.TypeKey);
        Assert.NotNull(chosen);
        Assert.Equal(UnitCategory.Tank, chosen.Category);
    }

    [Fact]
    public void After_several_tanks_the_ai_switches_to_the_most_deficient_remaining_category()
    {
        var s = WithFullRoster(10000f, unlockedTier: 5);
        // Tank share is already far above its 30% target; Infantry is close to its target too,
        // leaving MechInfantry (0 units, 20% target) as the clear largest remaining deficit.
        AddLivingUnits(s, UnitCategory.Tank, 1, 10);
        AddLivingUnits(s, UnitCategory.Infantry, 1, 3);

        AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed: 777u);

        Assert.Equal(AiSpendChoice.Produce, d.Choice);
        UnitType chosen = s.Types.Get(d.TypeKey);
        Assert.NotNull(chosen);
        Assert.Equal(UnitCategory.MechInfantry, chosen.Category);
    }

    [Fact]
    public void Category_choice_never_picks_AntiAir()
    {
        var s = WithFullRoster(10000f, unlockedTier: 5);
        for (uint seed = 0; seed < 200; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            UnitType chosen = s.Types.Get(d.TypeKey);
            Assert.NotEqual(UnitCategory.AntiAir, chosen.Category);
        }
    }

    // --- Research ---

    [Fact]
    public void Research_is_chosen_sometimes_but_not_always_when_behind_and_affordable()
    {
        var s = WithFullRoster(200f, unlockedTier: 1); // Treasury >= ResearchReserve(150)
        int researchCount = 0;
        int total = 1000;
        for (uint seed = 0; seed < total; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice == AiSpendChoice.Research) researchCount++;
        }

        Assert.True(researchCount > 0, "expected research to be chosen for at least some seeds");
        Assert.True(researchCount < total, "expected research to NOT be chosen for at least some seeds");
    }

    [Fact]
    public void Research_is_never_chosen_when_already_at_max_tier()
    {
        var s = WithFullRoster(10000f, unlockedTier: 5);
        for (uint seed = 0; seed < 500; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            Assert.NotEqual(AiSpendChoice.Research, d.Choice);
        }
    }

    [Fact]
    public void Research_is_never_chosen_when_treasury_below_reserve()
    {
        var s = WithFullRoster(149f, unlockedTier: 1); // just under ResearchReserve(150)
        for (uint seed = 0; seed < 500; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            Assert.NotEqual(AiSpendChoice.Research, d.Choice);
        }
    }

    // --- Tier choice respects UnlockedTier + research reserve floor ---

    [Fact]
    public void Tier_choice_never_exceeds_UnlockedTier()
    {
        var s = WithFullRoster(10000f, unlockedTier: 3);
        for (uint seed = 0; seed < 200; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            UnitType chosen = s.Types.Get(d.TypeKey);
            Assert.True(chosen.Tier <= 3);
        }
    }

    [Fact]
    public void Tier_choice_respects_research_reserve_floor_below_max_tier()
    {
        // Tank_T3 costs 60*(1+0.6*2) = 132. Reserve is 150. Treasury=282 -> spendCap=132 -> exactly
        // affords Tank_T3.
        var s = WithFullRoster(282f, unlockedTier: 3);
        AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed: 999u);
        // With plenty of empty army and Tank as the clear composition target, and treasury far above
        // the research floor is not the case here (282 >= 150) so research is possible for this seed;
        // filter to the produce branch by scanning seeds until we find one that produces.
        UnitType chosen = FindFirstProducedType(s, maxSeed: 2000);
        Assert.NotNull(chosen);
        Assert.Equal(UnitCategory.Tank, chosen.Category);
        Assert.Equal((byte)3, chosen.Tier);
    }

    [Fact]
    public void Tier_choice_falls_back_to_cheaper_tier_when_top_tier_just_out_of_reach()
    {
        // Tank_T3 costs 132, Tank_T2 costs 96. Treasury=281 -> spendCap=131 (< 132, so T3 is NOT
        // affordable) but >= 96, so T2 should be chosen instead.
        var s = WithFullRoster(281f, unlockedTier: 3);
        UnitType chosen = FindFirstProducedType(s, maxSeed: 2000);
        Assert.NotNull(chosen);
        Assert.Equal(UnitCategory.Tank, chosen.Category);
        Assert.Equal((byte)2, chosen.Tier);
    }

    [Fact]
    public void At_max_tier_the_whole_treasury_is_available_no_reserve_held_back()
    {
        // Tank_T5 costs 60*3.4 = 204. UnlockedTier=5 -> no reserve withheld, so exactly 204 affords it.
        var s = WithFullRoster(204f, unlockedTier: 5);
        UnitType chosen = FindFirstProducedType(s, maxSeed: 2000);
        Assert.NotNull(chosen);
        Assert.Equal(UnitCategory.Tank, chosen.Category);
        Assert.Equal((byte)5, chosen.Tier);
    }

    private static UnitType FindFirstProducedType(WarState s, uint maxSeed)
    {
        for (uint seed = 0; seed < maxSeed; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice == AiSpendChoice.Produce) return s.Types.Get(d.TypeKey);
        }
        return null;
    }

    // --- Determinism / affordability edge cases ---

    [Fact]
    public void Decide_is_reproducible_for_the_same_seed()
    {
        var s = WithFullRoster(10000f, unlockedTier: 3);
        AiDecision first = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed: 42u);
        for (int i = 0; i < 10; i++)
        {
            AiDecision again = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed: 42u);
            Assert.Equal(first.Choice, again.Choice);
            Assert.Equal(first.TypeKey, again.TypeKey);
        }
    }

    [Fact]
    public void Nothing_is_chosen_when_treasury_is_zero()
    {
        var s = WithFullRoster(0f, unlockedTier: 3);
        for (uint seed = 0; seed < 50; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            Assert.Equal(AiSpendChoice.None, d.Choice);
        }
    }
}
