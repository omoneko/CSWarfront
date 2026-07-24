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
}
