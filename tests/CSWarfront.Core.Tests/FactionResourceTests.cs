using CSWarfront.Core;
using Xunit;

/// <summary>Task99: 3資源経済のFactionプール（人的資源/生産力。資金=Treasuryは既存）。</summary>
public class FactionResourceTests
{
    [Fact]
    public void Manpower_and_production_pools_follow_the_treasury_conventions()
    {
        var f = new Faction(0, "Red");

        f.AddManpower(100f);
        f.AddProduction(50f);
        f.AddManpower(-10f);   // 非正の加算は無視（AddTreasuryと同じ規約）
        f.AddProduction(0f);

        Assert.Equal(100f, f.Manpower, 3);
        Assert.Equal(50f, f.Production, 3);

        Assert.True(f.TrySpendManpower(40f));
        Assert.Equal(60f, f.Manpower, 3);
        Assert.False(f.TrySpendManpower(61f)); // 残高不足は失敗し、残高は変わらない
        Assert.Equal(60f, f.Manpower, 3);

        Assert.True(f.TrySpendProduction(50f));
        Assert.Equal(0f, f.Production, 3);
        Assert.False(f.TrySpendProduction(0.1f));
        Assert.False(f.TrySpendManpower(-1f)); // 負額は失敗（TrySpendと同じ規約）
    }
}
