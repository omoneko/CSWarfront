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

    // --- Task38: 命中率(Accuracy)のTier成長 ---

    [Fact]
    public void Accuracy_tier1_returns_base_value_unchanged()
    {
        Assert.Equal(0.35f, TierScaling.Accuracy(0.35f, 1), 4);
        Assert.Equal(0.75f, TierScaling.Accuracy(0.75f, 1), 4);
    }

    [Fact]
    public void Accuracy_grows_6_percent_of_base_per_tier()
    {
        // value(tier) = base * (1 + 0.06*(tier-1)). base=0.35, tier=3 -> 0.35*(1+0.12)=0.392
        Assert.Equal(0.392f, TierScaling.Accuracy(0.35f, 3), 4);
        // base=0.60, tier=5 -> 0.60*(1+0.24)=0.744
        Assert.Equal(0.744f, TierScaling.Accuracy(0.60f, 5), 4);
    }

    [Fact]
    public void Accuracy_clamps_at_0_95_even_when_growth_would_exceed_it()
    {
        // base=0.85 (DroneInfantry), tier=5 -> 0.85*(1+0.24)=1.054 -> clamped to 0.95
        Assert.Equal(0.95f, TierScaling.Accuracy(0.85f, 5), 4);
        // an already-over-cap base value must also clamp, even at tier 1
        Assert.Equal(0.95f, TierScaling.Accuracy(0.99f, 1), 4);
    }
}
