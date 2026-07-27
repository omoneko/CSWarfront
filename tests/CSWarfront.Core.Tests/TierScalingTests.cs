using CSWarfront.Core;
using Xunit;

public class TierScalingTests
{
    [Fact]
    public void Tier1_returns_base_value_unchanged()
    {
        Assert.Equal(100f, TierScaling.Hp(100f, 1), 3);
        Assert.Equal(100f, TierScaling.Attack(100f, 1), 3);
        Assert.Equal(100f, TierScaling.Range(100f, 1), 3);
        Assert.Equal(100f, TierScaling.Armor(100f, 1), 3);
        Assert.Equal(100f, TierScaling.SpeedKmh(100f, 1), 3);
        Assert.Equal(100f, TierScaling.Cost(100f, 1), 3);
        Assert.Equal(100f, TierScaling.BuildTime(100f, 1), 3);
    }

    [Fact]
    public void Tier5_matches_documented_multipliers()
    {
        // (1 + increment * 4): HP x2.4, Attack x2.6, Range x1.4, Armor x2.8, Speed x1.32,
        // Cost x3.4, BuildTime x2.2
        Assert.Equal(240f, TierScaling.Hp(100f, 5), 3);
        Assert.Equal(260f, TierScaling.Attack(100f, 5), 3);
        Assert.Equal(140f, TierScaling.Range(100f, 5), 3);
        Assert.Equal(280f, TierScaling.Armor(100f, 5), 3);
        Assert.Equal(132f, TierScaling.SpeedKmh(100f, 5), 3);
        Assert.Equal(340f, TierScaling.Cost(100f, 5), 3);
        Assert.Equal(220f, TierScaling.BuildTime(100f, 5), 3);
    }

    [Fact]
    public void Tier_below_1_clamps_to_1()
    {
        Assert.Equal(TierScaling.Hp(100f, 1), TierScaling.Hp(100f, 0), 3);
        Assert.Equal(TierScaling.Cost(100f, 1), TierScaling.Cost(100f, 0), 3);
    }

    [Fact]
    public void Tier_above_5_clamps_to_5()
    {
        Assert.Equal(TierScaling.Hp(100f, 5), TierScaling.Hp(100f, 6), 3);
        Assert.Equal(TierScaling.Hp(100f, 5), TierScaling.Hp(100f, 255), 3);
        Assert.Equal(TierScaling.Cost(100f, 5), TierScaling.Cost(100f, 200), 3);
    }
}
