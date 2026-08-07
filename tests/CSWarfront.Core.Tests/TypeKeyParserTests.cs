using CSWarfront.Core;
using Xunit;

// Task50: tests for the pure-logic part used by the tier fallback in UnitAssetBindings (Game layer).
// UnitAssetBindings itself is not part of the Core.Tests compilation set (Core\**\*.cs), so
// only the parsing / search-order construction is verified here (the UnitAssetBindings side is covered by visual inspection + build check).
public class TypeKeyParserTests
{
    [Theory]
    [InlineData("Tank_T1", UnitCategory.Tank, (byte)1)]
    [InlineData("Tank_T4", UnitCategory.Tank, (byte)4)]
    [InlineData("MechInfantry_T3", UnitCategory.MechInfantry, (byte)3)]
    [InlineData("AntiAir_T5", UnitCategory.AntiAir, (byte)5)]
    public void TryParse_splits_known_category_and_tier(string typeKey, UnitCategory expectedCategory, byte expectedTier)
    {
        bool ok = TypeKeyParser.TryParse(typeKey, out UnitCategory category, out byte tier);

        Assert.True(ok);
        Assert.Equal(expectedCategory, category);
        Assert.Equal(expectedTier, tier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Tank")]           // no _T segment
    [InlineData("Tank_T")]         // no digits after _T
    [InlineData("Tank_TX")]        // non-numeric tier
    [InlineData("NoSuchCategory_T1")] // unknown category
    [InlineData("_T1")]            // empty category part
    public void TryParse_returns_false_for_unparseable_input(string typeKey)
    {
        bool ok = TypeKeyParser.TryParse(typeKey, out UnitCategory category, out byte tier);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_round_trips_with_LandUnitRoster_TypeKey_for_every_category_and_tier()
    {
        foreach (UnitType t in LandUnitRoster.All())
        {
            bool ok = TypeKeyParser.TryParse(t.TypeKey, out UnitCategory category, out byte tier);

            Assert.True(ok, "failed to parse " + t.TypeKey);
            Assert.Equal(t.Category, category);
            Assert.Equal(t.Tier, tier);
            Assert.Equal(t.TypeKey, LandUnitRoster.TypeKey(category, tier));
        }
    }

    [Theory]
    [InlineData((byte)4, new byte[] { 3, 2, 1, 5 })]
    [InlineData((byte)1, new byte[] { 2, 3, 4, 5 })]
    [InlineData((byte)5, new byte[] { 4, 3, 2, 1 })]
    [InlineData((byte)3, new byte[] { 2, 1, 4, 5 })]
    public void FallbackTierOrder_prefers_nearest_lower_tier_then_higher_tiers(byte tier, byte[] expected)
    {
        byte[] order = TypeKeyParser.FallbackTierOrder(tier);

        Assert.Equal(expected, order);
    }

    [Fact]
    public void FallbackTierOrder_never_includes_the_tier_itself()
    {
        for (byte tier = 1; tier <= 5; tier++)
        {
            byte[] order = TypeKeyParser.FallbackTierOrder(tier);
            Assert.DoesNotContain(tier, order);
        }
    }

    // "Worked example": rebuilding, via LandUnitRoster.TypeKey, the sequence of fallback keys used
    // when resolving the binding for Tank_T4 matches the order UnitAssetBindings.TryGet actually tries
    // (the report's example: with a binding only for Tank_T1, T4/T3/T2 all fall through and it is found at T1).
    [Fact]
    public void FallbackTierOrder_worked_example_for_Tank_T4_reaches_Tank_T1_before_Tank_T5()
    {
        TypeKeyParser.TryParse("Tank_T4", out UnitCategory category, out byte tier);
        byte[] order = TypeKeyParser.FallbackTierOrder(tier);

        string[] candidateKeys = new string[order.Length];
        for (int i = 0; i < order.Length; i++)
            candidateKeys[i] = LandUnitRoster.TypeKey(category, order[i]);

        Assert.Equal(new[] { "Tank_T3", "Tank_T2", "Tank_T1", "Tank_T5" }, candidateKeys);
    }
}
