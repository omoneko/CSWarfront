using CSWarfront.Core;
using Xunit;

/// <summary>
/// Task87（ユーザー要望「航空機がターゲットを失った際に静止してしまう→付近の航空基地または空母に
/// 帰還。海上戦力は付近の海軍基地にもどる」): Idleの航空/海上ユニットの帰還移動テスト。
/// </summary>
public class ReturnHomeTests
{
    private static WarState BaseState()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        LandUnitRoster.RegisterAll(s.Types);
        NavalUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);
        return s;
    }

    private static MilitaryBase AddBase(WarState s, ushort id, BaseType type, byte owner, float x)
    {
        var b = new MilitaryBase(id, type, new WorldPos(x, 0, 0));
        b.OwnerFactionId = owner;
        b.CurrentHP = 500f;
        s.Bases.Add(b);
        return b;
    }

    [Fact]
    public void Idle_fighter_flies_back_toward_its_own_air_base()
    {
        var s = BaseState();
        AddBase(s, 200, BaseType.AirForce, 0, 1000f);
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Idle; // ターゲット喪失後の状態
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.True(fighter.Position.X > 0f, "expected the idle fighter to head home");
    }

    [Fact]
    public void Idle_fighter_stops_within_home_arrival_distance()
    {
        var s = BaseState();
        AddBase(s, 200, BaseType.AirForce, 0, MovementStep.HomeArrivalDistance - 10f);
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, fighter.Position.X, 3); // 既に到着圏内なので動かない
    }

    [Fact]
    public void Idle_fighter_prefers_a_closer_friendly_carrier_over_a_far_air_base()
    {
        var s = BaseState();
        AddBase(s, 200, BaseType.AirForce, 0, 2000f);
        var carrier = new UnitInstance(2, "Carrier_T1", 0, 100f, new WorldPos(0, 0, 500f));
        s.Units.Add(carrier);
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.True(fighter.Position.Z > 0f, "expected the fighter to head to the closer carrier");
        Assert.True(fighter.Position.X < 1f, "expected the fighter not to head to the far air base");
    }

    [Fact]
    public void Idle_fighter_ignores_enemy_air_bases()
    {
        var s = BaseState();
        AddBase(s, 200, BaseType.AirForce, 1, 100f);  // 敵の航空基地（近い）
        AddBase(s, 201, BaseType.AirForce, 0, -800f); // 自軍の航空基地（遠い）
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.True(fighter.Position.X < 0f, "expected the fighter to head to its OWN base, not the enemy's");
    }

    [Fact]
    public void Idle_destroyer_sails_back_toward_its_own_navy_base_not_an_army_base()
    {
        var s = BaseState();
        AddBase(s, 200, BaseType.Army, 0, 100f);   // 自軍陸軍基地（近いが対象外）
        AddBase(s, 201, BaseType.Navy, 0, -800f);  // 自軍海軍基地
        var destroyer = new UnitInstance(1, "Destroyer_T1", 0, 100f, new WorldPos(0, 0, 0));
        destroyer.State = UnitState.Idle;
        s.Units.Add(destroyer);

        MovementStep.Advance(s, 1f);

        Assert.True(destroyer.Position.X < 0f, "expected the destroyer to head to the navy base");
    }

    [Fact]
    public void Idle_land_unit_does_not_return_home()
    {
        // 帰還は航空/海上のみ（地上ユニットの挙動は従来どおりIdleで静止）。
        var s = BaseState();
        AddBase(s, 200, BaseType.Army, 0, 1000f);
        var tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        tank.State = UnitState.Idle;
        s.Units.Add(tank);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, tank.Position.X, 3);
    }

    [Fact]
    public void Moving_fighter_with_objective_is_unaffected()
    {
        var s = BaseState();
        AddBase(s, 200, BaseType.AirForce, 0, -1000f); // 反対方向の自軍基地
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Moving;
        fighter.OrderTargetPos = new WorldPos(1000, 0, 0);
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.True(fighter.Position.X > 0f, "expected the ordered objective to take priority over home");
    }
}
