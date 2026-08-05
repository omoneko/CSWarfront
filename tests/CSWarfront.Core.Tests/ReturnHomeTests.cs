using CSWarfront.Core;
using Xunit;

/// <summary>
/// Task87（ユーザー要望「航空機がターゲットを失った際に静止してしまう→付近の航空基地または空母に
/// 帰還。海上戦力は付近の海軍基地にもどる」): Idleの航空/海上ユニットの帰還移動テスト。
/// </summary>
public class ReturnHomeTests
{
    /// <summary>地表を一定高さ(0)で返すサンプラー（Task107の着陸テスト用）。</summary>
    private class FlatGroundSampler : IHeightSampler
    {
        private readonly float _ground;
        public FlatGroundSampler(float ground = 0f) { _ground = ground; }
        public bool TrySampleHeight(float x, float z, out float height) { height = _ground; return true; }
    }

    /// <summary>全面が水（洋上）のサンプラー（Task107: 空母以外への着水をしないことの検証用）。</summary>
    private class AllWater : IWaterSampler
    {
        public bool IsWater(float x, float z) { return true; }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return true; }
    }

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

    // --- Task107（ユーザー報告「目標がなくなった航空戦力が空中でホバリングしてしまう」）---

    [Fact]
    public void Idle_fighter_over_its_home_base_lands_instead_of_hovering()
    {
        var s = BaseState();
        s.Height = new FlatGroundSampler();
        AddBase(s, 200, BaseType.AirForce, 0, 0f); // 真上（＝帰還先へ到着済み）
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f,
            new WorldPos(0, MovementStep.CruiseAltitude, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.True(fighter.Position.Y < MovementStep.CruiseAltitude,
            "expected the idle fighter to start descending onto its base");
        Assert.Equal(0f, fighter.Position.X, 3); // 水平位置は変えない
    }

    [Fact]
    public void Landing_fighter_settles_at_parked_altitude_and_stays_there()
    {
        var s = BaseState();
        s.Height = new FlatGroundSampler();
        AddBase(s, 200, BaseType.AirForce, 0, 0f);
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f,
            new WorldPos(0, MovementStep.CruiseAltitude, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        for (int i = 0; i < 20; i++) MovementStep.Advance(s, 1f);

        Assert.Equal(MovementStep.ParkedAltitude, fighter.Position.Y, 2); // 接地して静止（地面貫通なし）
    }

    [Fact]
    public void Idle_transport_helicopter_lands_at_its_base()
    {
        // 輸送ヘリはTransportHeliStepが移動を管理する（帰還先解決の対象外）が、待機中に空中で
        // 止まったままにはせず着陸する。
        var s = BaseState();
        s.Height = new FlatGroundSampler();
        AddBase(s, 200, BaseType.Army, 0, 0f);
        var heli = new UnitInstance(1, "TransportHelicopter_T1", 0, 100f,
            new WorldPos(0, MovementStep.HeliCruiseAltitude, 0));
        heli.State = UnitState.Idle;
        s.Units.Add(heli);

        MovementStep.Advance(s, 1f);

        Assert.True(heli.Position.Y < MovementStep.HeliCruiseAltitude,
            "expected the waiting transport helicopter to land rather than hover");
    }

    [Fact]
    public void Idle_fighter_over_a_carrier_lands_on_the_deck_not_in_the_sea()
    {
        var s = BaseState();
        s.Height = new FlatGroundSampler(-30f); // 海底
        s.Water = new AllWater();
        var carrier = new UnitInstance(2, "Carrier_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(carrier);
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f,
            new WorldPos(0, MovementStep.CruiseAltitude, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        for (int i = 0; i < 20; i++) MovementStep.Advance(s, 1f);

        Assert.Equal(MovementStep.CarrierDeckAltitude, fighter.Position.Y, 2);
    }

    [Fact]
    public void Idle_fighter_over_open_water_keeps_hovering_rather_than_ditching()
    {
        var s = BaseState();
        s.Height = new FlatGroundSampler(-30f);
        s.Water = new AllWater();
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f,
            new WorldPos(0, MovementStep.CruiseAltitude, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter); // 帰還先も空母も無い＝着陸できる場所が無い

        MovementStep.Advance(s, 1f);

        Assert.Equal(MovementStep.CruiseAltitude, fighter.Position.Y, 3);
    }

    [Fact]
    public void Returning_fighter_descends_below_cruise_altitude_on_final_approach()
    {
        var s = BaseState();
        s.Height = new FlatGroundSampler();
        AddBase(s, 200, BaseType.AirForce, 0, 120f); // DescentStartDistance(500)より内側
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f,
            new WorldPos(0, MovementStep.CruiseAltitude, 0));
        fighter.State = UnitState.Idle;
        s.Units.Add(fighter);

        MovementStep.Advance(s, 1f);

        Assert.True(fighter.Position.Y < MovementStep.CruiseAltitude,
            "expected a gliding approach, not a level fly-over at cruise altitude");
    }

    [Fact]
    public void Parked_fighter_climbs_gradually_when_ordered_out_again()
    {
        var s = BaseState();
        s.Height = new FlatGroundSampler();
        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f,
            new WorldPos(0, MovementStep.ParkedAltitude, 0));
        fighter.State = UnitState.Moving;
        fighter.OrderTargetPos = new WorldPos(100000f, 0, 0); // 遠方へ出撃（1tickでは着かない）
        s.Units.Add(fighter);

        MovementStep.Advance(s, 0.1f); // 実機に近い小さなtick（1tickの上昇量はstepLenが上限）

        Assert.True(fighter.Position.Y < MovementStep.CruiseAltitude,
            "expected a climb, not a teleport to cruise altitude");
        Assert.True(fighter.Position.Y > MovementStep.ParkedAltitude, "expected the fighter to be climbing");
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
