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
}
