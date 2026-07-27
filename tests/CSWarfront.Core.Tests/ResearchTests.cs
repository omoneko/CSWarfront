using CSWarfront.Core;
using Xunit;

public class ResearchTests
{
    // --- CostToUnlock ---

    [Theory]
    [InlineData((byte)2, 100f)]
    [InlineData((byte)3, 250f)]
    [InlineData((byte)4, 500f)]
    [InlineData((byte)5, 1000f)]
    public void CostToUnlock_matches_cost_table(byte nextTier, float expectedCost)
    {
        Assert.Equal(expectedCost, Research.CostToUnlock(nextTier), 3);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)6)]
    public void CostToUnlock_returns_zero_for_tiers_outside_2_to_5(byte nextTier)
    {
        Assert.Equal(0f, Research.CostToUnlock(nextTier), 3);
    }

    // --- CanUnlockNext ---

    [Fact]
    public void CanUnlockNext_true_when_enough_points_and_not_maxed()
    {
        var f = new Faction(0, "Red");
        f.AddResearchPoints(100f); // exactly CostToUnlock(2)
        Assert.True(Research.CanUnlockNext(f));
    }

    [Fact]
    public void CanUnlockNext_false_when_insufficient_points()
    {
        var f = new Faction(0, "Red");
        f.AddResearchPoints(99.9f);
        Assert.False(Research.CanUnlockNext(f));
    }

    [Fact]
    public void CanUnlockNext_false_when_already_max_tier()
    {
        var f = new Faction(0, "Red");
        f.UnlockedTier = 5;
        f.AddResearchPoints(100000f);
        Assert.False(Research.CanUnlockNext(f));
    }

    // --- TryUnlockNext ---

    [Fact]
    public void TryUnlockNext_succeeds_and_deducts_exact_cost()
    {
        var f = new Faction(0, "Red");
        f.AddResearchPoints(150f);

        bool ok = Research.TryUnlockNext(f);

        Assert.True(ok);
        Assert.Equal((byte)2, f.UnlockedTier);
        Assert.Equal(50f, f.ResearchPoints, 3);
    }

    [Fact]
    public void TryUnlockNext_fails_when_insufficient_points_and_state_unchanged()
    {
        var f = new Faction(0, "Red");
        f.AddResearchPoints(50f);

        bool ok = Research.TryUnlockNext(f);

        Assert.False(ok);
        Assert.Equal((byte)1, f.UnlockedTier);
        Assert.Equal(50f, f.ResearchPoints, 3);
    }

    [Fact]
    public void TryUnlockNext_fails_when_already_max_tier_and_state_unchanged()
    {
        var f = new Faction(0, "Red");
        f.UnlockedTier = 5;
        f.AddResearchPoints(100000f);

        bool ok = Research.TryUnlockNext(f);

        Assert.False(ok);
        Assert.Equal((byte)5, f.UnlockedTier);
        Assert.Equal(100000f, f.ResearchPoints, 3);
    }

    [Fact]
    public void TryUnlockNext_can_be_chained_through_all_tiers()
    {
        var f = new Faction(0, "Red");
        f.AddResearchPoints(100f + 250f + 500f + 1000f);

        Assert.True(Research.TryUnlockNext(f)); // -> T2
        Assert.True(Research.TryUnlockNext(f)); // -> T3
        Assert.True(Research.TryUnlockNext(f)); // -> T4
        Assert.True(Research.TryUnlockNext(f)); // -> T5

        Assert.Equal((byte)5, f.UnlockedTier);
        Assert.Equal(0f, f.ResearchPoints, 3);
        Assert.False(Research.TryUnlockNext(f)); // already max
    }

    // --- KillReward ---

    [Fact]
    public void KillReward_is_half_of_unit_cost()
    {
        UnitType tank = LandUnitRoster.Get(UnitCategory.Tank, 1); // Cost 60
        Assert.Equal(30f, Research.KillReward(tank), 3);
    }

    [Fact]
    public void KillReward_returns_zero_for_null_type()
    {
        Assert.Equal(0f, Research.KillReward(null), 3);
    }

    // --- TryInvest ---

    [Fact]
    public void TryInvest_spends_treasury_and_adds_research_points_at_equal_rate()
    {
        var f = new Faction(0, "Red");
        f.AddTreasury(100f);

        bool ok = Research.TryInvest(f, 50f);

        Assert.True(ok);
        Assert.Equal(50f, f.Treasury, 3);
        Assert.Equal(50f, f.ResearchPoints, 3);
    }

    [Fact]
    public void TryInvest_fails_when_treasury_insufficient_and_does_not_change_state()
    {
        var f = new Faction(0, "Red");
        f.AddTreasury(10f);

        bool ok = Research.TryInvest(f, 50f);

        Assert.False(ok);
        Assert.Equal(10f, f.Treasury, 3);
        Assert.Equal(0f, f.ResearchPoints, 3);
    }
}
