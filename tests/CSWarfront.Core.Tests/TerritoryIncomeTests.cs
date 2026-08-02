using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class TerritoryIncomeTests
{
    [Fact]
    public void Sums_development_within_radius_times_rate()
    {
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        b.InfluenceRadius = 100f;
        var samples = new List<DevelopmentSample>
        {
            new DevelopmentSample { Position = new WorldPos(50, 0, 0), Development = 10f },  // 圏内
            new DevelopmentSample { Position = new WorldPos(80, 0, 0), Development = 5f },   // 圏内
            new DevelopmentSample { Position = new WorldPos(200, 0, 0), Development = 100f },// 圏外
        };
        Assert.Equal(1.5f, TerritoryIncome.ForBase(b, samples, 0.1f), 3); // (10+5)*0.1
    }

    [Fact]
    public void Unowned_base_yields_zero()
    {
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = null;
        var samples = new List<DevelopmentSample>
        { new DevelopmentSample { Position = new WorldPos(0, 0, 0), Development = 10f } };
        Assert.Equal(0f, TerritoryIncome.ForBase(b, samples, 1f), 3);
    }

    // --- Task99: 3資源経済（住宅→人的資源、商業/オフィス→資金、工業→生産力） ---

    private static MilitaryBase OwnedBase()
    {
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        return b;
    }

    [Fact]
    public void Zoned_income_routes_each_zone_to_its_resource()
    {
        var samples = new List<DevelopmentSample>
        {
            new DevelopmentSample { Position = new WorldPos(100, 0, 0), Development = 10f, Zone = ZoneKind.Residential },
            new DevelopmentSample { Position = new WorldPos(200, 0, 0), Development = 20f, Zone = ZoneKind.CommercialOffice },
            new DevelopmentSample { Position = new WorldPos(300, 0, 0), Development = 30f, Zone = ZoneKind.Industrial },
            new DevelopmentSample { Position = new WorldPos(400, 0, 0), Development = 99f, Zone = ZoneKind.Other }, // 対象外ゾーン
        };

        ZonedIncome inc = TerritoryIncome.ZonedForBase(OwnedBase(), samples, 0.1f);

        Assert.Equal(1f, inc.Manpower, 3);   // 10*0.1
        Assert.Equal(2f, inc.Funds, 3);      // 20*0.1
        Assert.Equal(3f, inc.Production, 3); // 30*0.1
    }

    [Fact]
    public void Zoned_income_uses_the_1km_economy_radius()
    {
        Assert.Equal(1000f, TerritoryIncome.EconomyRadius, 3);
        var samples = new List<DevelopmentSample>
        {
            new DevelopmentSample { Position = new WorldPos(999, 0, 0), Development = 10f, Zone = ZoneKind.Residential },  // 圏内
            new DevelopmentSample { Position = new WorldPos(1001, 0, 0), Development = 10f, Zone = ZoneKind.Residential }, // 圏外
        };

        ZonedIncome inc = TerritoryIncome.ZonedForBase(OwnedBase(), samples, 1f);

        Assert.Equal(10f, inc.Manpower, 3);
    }

    [Fact]
    public void Zoned_income_for_unowned_base_is_zero()
    {
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = null;
        var samples = new List<DevelopmentSample>
        { new DevelopmentSample { Position = new WorldPos(0, 0, 0), Development = 10f, Zone = ZoneKind.Industrial } };

        ZonedIncome inc = TerritoryIncome.ZonedForBase(b, samples, 1f);

        Assert.Equal(0f, inc.Manpower + inc.Funds + inc.Production, 3);
    }
}
