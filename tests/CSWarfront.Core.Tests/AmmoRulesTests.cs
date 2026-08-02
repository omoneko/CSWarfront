using CSWarfront.Core;
using Xunit;

/// <summary>Task99: 弾薬ゲージ（消費・弾切れで射撃停止・Invader無限・帰還再武装の目標解除）。</summary>
public class AmmoRulesTests
{
    private static WarState TwoHostileTanks(out UnitInstance red, out UnitInstance blue)
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        red = new UnitInstance(1, "Tank_T1", 0, 1000f, new WorldPos(0, 0, 0));
        blue = new UnitInstance(2, "Tank_T1", 1, 1000f, new WorldPos(30, 0, 0));
        s.Units.Add(red); s.Units.Add(blue);
        return s;
    }

    [Fact]
    public void Firing_consumes_ammo_at_dt_over_combat_hours()
    {
        var s = TwoHostileTanks(out UnitInstance red, out UnitInstance blue);
        UnitType tank = s.Types.Get("Tank_T1");
        Assert.Equal(8f, tank.AmmoCombatHours, 3); // 戦車の既定=8h

        CombatStep.Advance(s, 2f); // 双方が2時間ぶん射撃

        Assert.Equal(1f - 2f / 8f, red.Ammo, 3);
        Assert.Equal(1f - 2f / 8f, blue.Ammo, 3);
    }

    [Fact]
    public void Out_of_ammo_units_stop_dealing_damage_and_disengage()
    {
        var s = TwoHostileTanks(out UnitInstance red, out UnitInstance blue);
        red.Ammo = 0f;
        blue.Ammo = 0f;
        float hpBefore = blue.CurrentHP;

        CombatStep.Advance(s, 1f);

        Assert.Equal(hpBefore, blue.CurrentHP, 3); // 弾切れはダメージを与えない
        Assert.Equal(UnitState.Idle, red.State);
        Assert.Null(red.TargetId);
    }

    [Fact]
    public void Idle_units_do_not_consume_ammo()
    {
        var s = TwoHostileTanks(out UnitInstance red, out UnitInstance blue);
        blue.Position = new WorldPos(5000, 0, 5000); // 射程外＝射撃しない

        CombatStep.Advance(s, 5f);

        Assert.Equal(1f, red.Ammo, 3);
    }

    [Fact]
    public void Invader_units_never_run_dry()
    {
        var s = TwoHostileTanks(out UnitInstance red, out UnitInstance blue);
        InvasionEvents.EnsureInvaderFaction(s);
        var invader = new UnitInstance(3, "Tank_T1", Faction.InvaderFactionId, 1000f, new WorldPos(-30, 0, 0));
        invader.Ammo = 0f; // 仮に0でも撃てる（HasAmmoが常にtrue）
        s.Units.Add(invader);
        float redHp = red.CurrentHP;

        CombatStep.Advance(s, 1f);

        Assert.True(red.CurrentHP < redHp, "expected the invader to keep firing with zero ammo");
        Assert.Equal(0f, invader.Ammo, 3); // 消費もしない（0のまま）
    }

    [Fact]
    public void Base_attack_stops_and_consumes_with_ammo()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var tank = new UnitInstance(1, "Tank_T1", 0, 1000f, new WorldPos(0, 0, 0));
        s.Units.Add(tank);
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(30, 0, 0));
        b.OwnerFactionId = 1;
        s.Bases.Add(b);

        BaseCombatStep.Advance(s, 1f);
        Assert.True(b.CurrentHP < b.MaxHP, "expected base damage");
        Assert.True(tank.Ammo < 1f, "expected ammo consumption for the siege");

        tank.Ammo = 0f;
        float hp = b.CurrentHP;
        BaseCombatStep.Advance(s, 0.001f); // 回復があるので極小dtで攻撃停止だけを確認
        Assert.True(b.CurrentHP >= hp, "expected no siege damage when out of ammo (only regen applies)");
    }

    [Fact]
    public void Empty_air_units_are_recalled_instead_of_advancing()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        AirUnitRoster.RegisterAll(s.Types);
        var fighter = new UnitInstance(1, "TacticalBomber_T1", 0, 1000f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Moving;
        s.Units.Add(fighter);
        var enemyBase = new MilitaryBase(1, BaseType.Army, new WorldPos(2000, 0, 0));
        enemyBase.OwnerFactionId = 1;
        s.Bases.Add(enemyBase);

        fighter.Ammo = 0f;
        InvasionOrders.AssignAdvance(s, 0, 0.1f);

        Assert.Equal(UnitState.Idle, fighter.State); // 進軍目標を与えられずIdle→帰還ロジックへ
        Assert.False(fighter.OrderTargetPos.HasValue);

        fighter.Ammo = 1f; // 再武装完了→次の呼び出しで再出撃
        InvasionOrders.AssignAdvance(s, 0, 0.1f);
        Assert.Equal(UnitState.Moving, fighter.State);
        Assert.True(fighter.OrderTargetPos.HasValue);
    }
}
