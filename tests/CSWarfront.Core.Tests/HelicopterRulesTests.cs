using CSWarfront.Core;
using Xunit;

/// <summary>Task101: ヘリコプターの兵科規則（対ヘリ=戦車/対空/戦闘機のみ、攻撃ヘリ=地上専任、
/// ホバリング型=パス補正なし、搭乗中ユニットは非標的）。</summary>
public class HelicopterRulesTests
{
    private static WarState TwoFactions()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);
        return s;
    }

    private static UnitInstance Add(WarState s, string typeKey, byte faction, float x, float z)
    {
        var u = new UnitInstance(s.AllocInstanceId(), typeKey, faction, 1000f, new WorldPos(x, 0, z));
        s.Units.Add(u);
        return u;
    }

    [Fact]
    public void Only_tanks_antiair_and_fighters_can_target_helicopters()
    {
        Assert.True(TargetingRules.CanTargetHelicopter(UnitCategory.Tank));
        Assert.True(TargetingRules.CanTargetHelicopter(UnitCategory.AntiAir));
        Assert.True(TargetingRules.CanTargetHelicopter(UnitCategory.AirSuperiority));
        Assert.False(TargetingRules.CanTargetHelicopter(UnitCategory.Infantry));
        Assert.False(TargetingRules.CanTargetHelicopter(UnitCategory.Artillery));
        Assert.False(TargetingRules.CanTargetHelicopter(UnitCategory.TacticalBomber));
        Assert.False(TargetingRules.CanTargetHelicopter(UnitCategory.AttackHelicopter));
    }

    [Fact]
    public void Tank_finds_a_helicopter_but_infantry_does_not()
    {
        var s = TwoFactions();
        UnitInstance tank = Add(s, "Tank_T1", 0, 0, 0);
        UnitInstance infantry = Add(s, "Infantry_T1", 0, 0, 10);
        Add(s, "AttackHelicopter_T1", 1, 30, 0);
        s.UnitGrid.Build(s.Units);

        UnitType tankType = s.Types.Get("Tank_T1");
        UnitType infType = s.Types.Get("Infantry_T1");
        Assert.NotNull(TargetSearch.FindNearestHostile(tank, s.UnitGrid, s.Relations, 60f,
            tankType.CanTargetDomains, s.Types)); // 戦車は対ヘリ例外で標的にできる
        Assert.Null(TargetSearch.FindNearestHostile(infantry, s.UnitGrid, s.Relations, 60f,
            infType.CanTargetDomains, s.Types));  // 歩兵はヘリを狙えない
    }

    [Fact]
    public void Attack_helicopter_targets_ground_but_not_aircraft()
    {
        var s = TwoFactions();
        UnitInstance heli = Add(s, "AttackHelicopter_T1", 0, 0, 0);
        UnitInstance enemyFighter = Add(s, "AirSuperiority_T1", 1, 30, 0);
        UnitInstance enemyHeli = Add(s, "AttackHelicopter_T1", 1, 40, 0);
        s.UnitGrid.Build(s.Units);

        UnitType heliType = s.Types.Get("AttackHelicopter_T1");
        Assert.Null(TargetSearch.FindNearestHostile(heli, s.UnitGrid, s.Relations, 100f,
            heliType.CanTargetDomains, s.Types)); // 空中目標は一切狙えない

        UnitInstance enemyTank = Add(s, "Tank_T1", 1, 50, 0);
        s.UnitGrid.Build(s.Units);
        Assert.Same(enemyTank, TargetSearch.FindNearestHostile(heli, s.UnitGrid, s.Relations, 100f,
            heliType.CanTargetDomains, s.Types)); // 地上は狙える
    }

    [Fact]
    public void Helicopters_hover_and_get_no_pass_damage_compensation()
    {
        var s = TwoFactions();
        UnitType heli = s.Types.Get("AttackHelicopter_T1");
        UnitType bomber = s.Types.Get("TacticalBomber_T1");
        Assert.Equal(1f, AirCombat.DamageMultiplier(heli), 3);
        Assert.Equal(AirCombat.PassDamageCompensation, AirCombat.DamageMultiplier(bomber), 3);
        Assert.Equal(3f, heli.AmmoCombatHours, 3); // 弾薬制の対象（再武装ループ）
    }

    [Fact]
    public void Carried_units_are_not_targetable()
    {
        var s = TwoFactions();
        UnitInstance tank = Add(s, "Tank_T1", 0, 0, 0);
        UnitInstance enemy = Add(s, "Infantry_T1", 1, 30, 0);
        enemy.CarriedByUnitId = 999; // 輸送ヘリ搭乗中
        s.UnitGrid.Build(s.Units);

        UnitType tankType = s.Types.Get("Tank_T1");
        Assert.Null(TargetSearch.FindNearestHostile(tank, s.UnitGrid, s.Relations, 60f,
            tankType.CanTargetDomains, s.Types));
    }

    [Fact]
    public void AntiAir_engages_helicopters_with_sam_rolls()
    {
        var s = TwoFactions();
        UnitInstance aa = Add(s, "AntiAir_T1", 0, 0, 0);
        UnitInstance heli = Add(s, "AttackHelicopter_T1", 1, 50, 0);
        heli.Ammo = 0f; // ヘリの反撃を止め、AAの射撃だけを観測する

        Assert.True(AntiAirCombat.UsesMissileAgainst(UnitCategory.AttackHelicopter)); // SAM（機銃ではない）

        float before = heli.CurrentHP;
        for (int i = 0; i < 50; i++) { s.TickCounter++; CombatStep.Advance(s, 0.6f); }
        Assert.True(heli.CurrentHP < before, "expected the SAM battery to eventually hit the helicopter");
    }
}
