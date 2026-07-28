using CSWarfront.Core;
using Xunit;

// Task50: UnitAssetBindings（Game層）のTierフォールバックが使う純ロジック部分のテスト。
// UnitAssetBindings自体はCore.Testsのコンパイル対象（Core\**\*.cs）に含まれないため、
// ここではパース/探索順序の組み立てのみを検証する（UnitAssetBindings側は目視確認+ビルド確認）。
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

    // 「worked example」: Tank_T4の割り当てを解決する際に使われるフォールバックキーの並びを
    // LandUnitRoster.TypeKeyで組み立て直すと、UnitAssetBindings.TryGetが実際に試す順序と一致する
    // （Tank_T1にだけ割り当てがある場合、T4/T3/T2は全て素通りしてT1で見つかる、という報告書の例）。
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
