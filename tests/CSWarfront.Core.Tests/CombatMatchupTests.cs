using CSWarfront.Core;
using Xunit;

public class CombatMatchupTests
{
    [Theory]
    // Infantry
    [InlineData(UnitCategory.Infantry, UnitCategory.Apc, 0.6f)]
    [InlineData(UnitCategory.Infantry, UnitCategory.Tank, 0.4f)]
    [InlineData(UnitCategory.Infantry, UnitCategory.MechInfantry, 0.9f)]
    [InlineData(UnitCategory.Infantry, UnitCategory.Artillery, 1.3f)]
    [InlineData(UnitCategory.Infantry, UnitCategory.DroneInfantry, 1.2f)]
    [InlineData(UnitCategory.Infantry, UnitCategory.AntiAir, 1.2f)]
    // MechInfantry
    [InlineData(UnitCategory.MechInfantry, UnitCategory.Infantry, 1.2f)]
    [InlineData(UnitCategory.MechInfantry, UnitCategory.Apc, 0.8f)]
    [InlineData(UnitCategory.MechInfantry, UnitCategory.Tank, 0.6f)]
    [InlineData(UnitCategory.MechInfantry, UnitCategory.Artillery, 1.3f)]
    // Apc
    [InlineData(UnitCategory.Apc, UnitCategory.Infantry, 1.4f)]
    [InlineData(UnitCategory.Apc, UnitCategory.Tank, 0.5f)]
    [InlineData(UnitCategory.Apc, UnitCategory.Artillery, 1.2f)]
    // Tank
    [InlineData(UnitCategory.Tank, UnitCategory.Infantry, 1.1f)]
    [InlineData(UnitCategory.Tank, UnitCategory.MechInfantry, 1.3f)]
    [InlineData(UnitCategory.Tank, UnitCategory.Apc, 1.4f)]
    [InlineData(UnitCategory.Tank, UnitCategory.Artillery, 1.5f)]
    [InlineData(UnitCategory.Tank, UnitCategory.DroneInfantry, 1.1f)]
    [InlineData(UnitCategory.Tank, UnitCategory.AntiAir, 1.3f)]
    // Artillery
    [InlineData(UnitCategory.Artillery, UnitCategory.Infantry, 1.6f)]
    [InlineData(UnitCategory.Artillery, UnitCategory.MechInfantry, 1.4f)]
    [InlineData(UnitCategory.Artillery, UnitCategory.Apc, 1.1f)]
    [InlineData(UnitCategory.Artillery, UnitCategory.Tank, 0.7f)]
    [InlineData(UnitCategory.Artillery, UnitCategory.Artillery, 1.2f)]
    // DroneInfantry (対戦車ドローン)
    [InlineData(UnitCategory.DroneInfantry, UnitCategory.Tank, 2.0f)]
    [InlineData(UnitCategory.DroneInfantry, UnitCategory.Apc, 1.7f)]
    [InlineData(UnitCategory.DroneInfantry, UnitCategory.MechInfantry, 1.2f)]
    [InlineData(UnitCategory.DroneInfantry, UnitCategory.Infantry, 0.6f)]
    // AntiAir
    [InlineData(UnitCategory.AntiAir, UnitCategory.Infantry, 0.5f)]
    [InlineData(UnitCategory.AntiAir, UnitCategory.MechInfantry, 0.5f)]
    [InlineData(UnitCategory.AntiAir, UnitCategory.Apc, 0.5f)]
    [InlineData(UnitCategory.AntiAir, UnitCategory.Tank, 0.5f)]
    [InlineData(UnitCategory.AntiAir, UnitCategory.Artillery, 0.5f)]
    [InlineData(UnitCategory.AntiAir, UnitCategory.DroneInfantry, 0.5f)]
    public void Multiplier_matches_design_table(UnitCategory attacker, UnitCategory target, float expected)
    {
        Assert.Equal(expected, CombatMatchup.Multiplier(attacker, target), 3);
    }

    [Theory]
    [InlineData(UnitCategory.Tank, UnitCategory.Tank)]
    [InlineData(UnitCategory.Infantry, UnitCategory.Infantry)]
    [InlineData(UnitCategory.MechInfantry, UnitCategory.DroneInfantry)] // 表に無い組み合わせ
    [InlineData(UnitCategory.AntiAir, UnitCategory.AntiAir)]
    [InlineData(UnitCategory.Carrier, UnitCategory.Tank)] // 未実装カテゴリとの組み合わせ
    public void Undefined_pairs_default_to_one(UnitCategory attacker, UnitCategory target)
    {
        Assert.Equal(1.0f, CombatMatchup.Multiplier(attacker, target), 3);
    }

    [Fact]
    public void Matchups_are_not_assumed_symmetric()
    {
        // Tank -> Infantry: 戦車は歩兵に強い (1.1)。Infantry -> Tank: 歩兵は素で戦車に弱い (0.4)。
        Assert.Equal(1.1f, CombatMatchup.Multiplier(UnitCategory.Tank, UnitCategory.Infantry), 3);
        Assert.Equal(0.4f, CombatMatchup.Multiplier(UnitCategory.Infantry, UnitCategory.Tank), 3);
        Assert.NotEqual(
            CombatMatchup.Multiplier(UnitCategory.Tank, UnitCategory.Infantry),
            CombatMatchup.Multiplier(UnitCategory.Infantry, UnitCategory.Tank));
    }
}
