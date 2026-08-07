using CSWarfront.Core;
using Xunit;

/// <summary>Task99: three-resource breakdown of unit costs and payment (manpower + production, funds substitution).</summary>
public class UnitCostsTests
{
    private static UnitType Tank() { return LandUnitRoster.Get(UnitCategory.Tank, 1); }

    [Fact]
    public void Cost_splits_by_category_share()
    {
        UnitType tank = Tank(); // Tank = 30% manpower / 70% production
        Assert.Equal(tank.Cost * 0.3f, UnitCosts.ManpowerCost(tank), 3);
        Assert.Equal(tank.Cost * 0.7f, UnitCosts.ProductionCost(tank), 3);
        Assert.Equal(tank.Cost, UnitCosts.ManpowerCost(tank) + UnitCosts.ProductionCost(tank), 3);
    }

    [Fact]
    public void Pays_from_production_when_sufficient()
    {
        var f = new Faction(0, "Red");
        UnitType tank = Tank();
        f.AddManpower(UnitCosts.ManpowerCost(tank));
        f.AddProduction(UnitCosts.ProductionCost(tank));
        f.AddTreasury(100f);

        Assert.True(UnitCosts.TryPay(f, tank, f.Treasury));
        Assert.Equal(0f, f.Manpower, 3);
        Assert.Equal(0f, f.Production, 3);
        Assert.Equal(100f, f.Treasury, 3); // No funds are spent
    }

    [Fact]
    public void Production_shortfall_is_paid_with_funds_at_double_rate()
    {
        var f = new Faction(0, "Red");
        UnitType tank = Tank();
        float pc = UnitCosts.ProductionCost(tank);
        f.AddManpower(UnitCosts.ManpowerCost(tank));
        f.AddProduction(pc - 10f); // Production is short by 10
        f.AddTreasury(100f);

        Assert.True(UnitCosts.TryPay(f, tank, f.Treasury));
        Assert.Equal(0f, f.Production, 3);
        Assert.Equal(100f - 10f * UnitCosts.FundsPerProduction, f.Treasury, 3); // Shortfall 10 x 2 = 20 paid with funds
    }

    [Fact]
    public void Manpower_is_never_substitutable()
    {
        var f = new Faction(0, "Red");
        UnitType tank = Tank();
        f.AddProduction(1000f);
        f.AddTreasury(1000f); // Only manpower is missing

        Assert.False(UnitCosts.CanAfford(f, tank, f.Treasury));
        Assert.False(UnitCosts.TryPay(f, tank, f.Treasury));
        Assert.Equal(1000f, f.Production, 3); // all-or-nothing: nothing is consumed
        Assert.Equal(1000f, f.Treasury, 3);
    }

    [Fact]
    public void Funds_cap_limits_substitution()
    {
        var f = new Faction(0, "Red");
        UnitType tank = Tank();
        f.AddManpower(1000f);
        f.AddTreasury(1000f); // Zero production -> the full amount must be substituted with funds

        float fundsNeeded = UnitCosts.ProductionCost(tank) * UnitCosts.FundsPerProduction;
        Assert.False(UnitCosts.CanAfford(f, tank, fundsNeeded - 1f)); // Cap is insufficient
        Assert.True(UnitCosts.CanAfford(f, tank, fundsNeeded));

        Assert.True(UnitCosts.TryPay(f, tank, fundsNeeded));
        Assert.Equal(1000f - fundsNeeded, f.Treasury, 3);
    }
}
