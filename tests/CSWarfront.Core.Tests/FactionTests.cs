using CSWarfront.Core;
using Xunit;

public class FactionTests
{
    [Fact]
    public void TrySpend_succeeds_when_enough_and_deducts()
    {
        var f = new Faction(0, "Red");
        f.AddTreasury(100f);
        Assert.True(f.TrySpend(30f));
        Assert.Equal(70f, f.Treasury, 3);
    }

    [Fact]
    public void TrySpend_fails_when_insufficient_and_leaves_treasury()
    {
        var f = new Faction(0, "Red");
        f.AddTreasury(20f);
        Assert.False(f.TrySpend(30f));
        Assert.Equal(20f, f.Treasury, 3);
    }

    // --- Task35: ResearchPoints / UnlockedTier ---

    [Fact]
    public void New_faction_starts_at_UnlockedTier_1_with_zero_research()
    {
        var f = new Faction(0, "Red");
        Assert.Equal((byte)1, f.UnlockedTier);
        Assert.Equal(0f, f.ResearchPoints, 3);
    }

    [Fact]
    public void AddResearchPoints_accumulates_positive_amounts()
    {
        var f = new Faction(0, "Red");
        f.AddResearchPoints(10f);
        f.AddResearchPoints(5.5f);
        Assert.Equal(15.5f, f.ResearchPoints, 3);
    }

    [Fact]
    public void AddResearchPoints_ignores_non_positive_amounts()
    {
        var f = new Faction(0, "Red");
        f.AddResearchPoints(10f);
        f.AddResearchPoints(0f);
        f.AddResearchPoints(-5f);
        Assert.Equal(10f, f.ResearchPoints, 3);
    }
}
