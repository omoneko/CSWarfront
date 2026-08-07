using CSWarfront.Core;
using Xunit;

/// <summary>Task99: Faction pools for the 3-resource economy (manpower / production; funds = the existing Treasury).</summary>
public class FactionResourceTests
{
    [Fact]
    public void Manpower_and_production_pools_follow_the_treasury_conventions()
    {
        var f = new Faction(0, "Red");

        f.AddManpower(100f);
        f.AddProduction(50f);
        f.AddManpower(-10f);   // non-positive additions are ignored (same convention as AddTreasury)
        f.AddProduction(0f);

        Assert.Equal(100f, f.Manpower, 3);
        Assert.Equal(50f, f.Production, 3);

        Assert.True(f.TrySpendManpower(40f));
        Assert.Equal(60f, f.Manpower, 3);
        Assert.False(f.TrySpendManpower(61f)); // insufficient balance fails and leaves the balance unchanged
        Assert.Equal(60f, f.Manpower, 3);

        Assert.True(f.TrySpendProduction(50f));
        Assert.Equal(0f, f.Production, 3);
        Assert.False(f.TrySpendProduction(0.1f));
        Assert.False(f.TrySpendManpower(-1f)); // negative amounts fail (same convention as TrySpend)
    }
}
