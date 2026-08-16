using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task127 (Workshop request): infantry take cover against a city building while fighting
/// instead of standing on the open road, and leave once the fight is over. Also covers Task128/129,
/// the two bugs reported alongside it.</summary>
public class BuildingGarrisonStepTests
{
    private static WarState StateWithBuilding(float buildingX, out UnitInstance infantry)
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);

        var cover = new CoverMap();
        cover.Add(new WorldPos(buildingX, 0, 0), 10f);
        s.Cover = cover;

        infantry = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        infantry.OrderTargetPos = new WorldPos(3000, 0, 0); // far away: no objective lock
        s.Units.Add(infantry);
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(300, 0, 0))); // the enemy
        return s;
    }

    [Fact]
    public void Infantry_in_contact_take_cover_against_a_nearby_building()
    {
        UnitInstance infantry;
        WarState s = StateWithBuilding(30f, out infantry);

        BuildingGarrisonStep.Advance(s, 0.1f);

        Assert.True(infantry.CoverHold);
        Assert.True(infantry.CoverDestination.HasValue);
        // Stands on the far side of the building from the enemy, not in the middle of the road.
        Assert.True(infantry.CoverDestination.Value.X < 30f);
    }

    [Fact]
    public void They_leave_once_the_fighting_stops()
    {
        UnitInstance infantry;
        WarState s = StateWithBuilding(30f, out infantry);
        BuildingGarrisonStep.Advance(s, 0.1f);
        Assert.True(infantry.CoverHold);

        s.Units.RemoveAt(1); // the enemy is gone
        BuildingGarrisonStep.Advance(s, 0.1f);

        Assert.Equal(0f, infantry.GarrisonHoldTimer, 3); // released; movement resumes from here
    }

    [Fact]
    public void No_building_within_reach_means_they_fight_where_they_stand()
    {
        UnitInstance infantry;
        WarState s = StateWithBuilding(BuildingGarrisonStep.GarrisonRadius + 50f, out infantry);

        BuildingGarrisonStep.Advance(s, 0.1f);

        Assert.False(infantry.CoverHold);
    }

    [Fact]
    public void Holding_a_building_is_time_boxed_like_entrenching()
    {
        UnitInstance infantry;
        WarState s = StateWithBuilding(30f, out infantry);
        BuildingGarrisonStep.Advance(s, 0.1f);
        infantry.Position = infantry.CoverDestination.Value; // arrived

        for (int h = 0; h < (int)BuildingGarrisonStep.MaxGarrisonHours + 2; h++)
            BuildingGarrisonStep.Advance(s, 1f);

        Assert.False(infantry.CoverHold);
        Assert.True(infantry.GarrisonCooldown > 0f);
    }

    [Fact]
    public void Garrisoned_infantry_take_less_damage()
    {
        UnitInstance infantry;
        WarState s = StateWithBuilding(30f, out infantry);
        BuildingGarrisonStep.Advance(s, 1f);
        infantry.Position = infantry.CoverDestination.Value;
        BuildingGarrisonStep.Advance(s, 1f); // arrived: the hold timer starts counting

        UnitType type = s.Types.Get("Infantry_T1");
        Assert.True(FortDefenseBonus.IsGarrisoned(s, infantry));
        Assert.Equal(1f / FortDefenseBonus.GarrisonDamageDivisor,
            FortDefenseBonus.Multiplier(s, infantry, type), 3);
    }

    // --- Task128: the auto-produce toggle must stop automatic fleet upkeep too ---

    private class NoWater : IWaterSampler
    {
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return false; }
        public bool IsWater(float x, float z) { return false; }
    }

    private static WarState StateWithArmyBase(bool autoProduce)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        LandUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        b.AutoProduce = autoProduce;
        s.Bases.Add(b);
        s.FindFaction(0).AddManpower(100000f);
        s.FindFaction(0).AddProduction(100000f);
        s.FindFaction(0).AddTreasury(100000f);
        return s;
    }

    [Fact]
    public void Auto_produce_off_stops_transport_helicopters_and_supply_trucks()
    {
        WarState off = StateWithArmyBase(false);
        TransportHeliStep.MaintainHelis(off);
        SupplyTruckStep.MaintainTrucks(off);
        Assert.Empty(off.Units);

        WarState on = StateWithArmyBase(true);
        TransportHeliStep.MaintainHelis(on);
        SupplyTruckStep.MaintainTrucks(on);
        Assert.NotEmpty(on.Units);
    }

    // --- Task129: land units must not drive into water ---

    private class WaterBeyond : IWaterSampler
    {
        private readonly float _x;
        public WaterBeyond(float x) { _x = x; }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return x > _x; }
        public bool IsWater(float x, float z) { return x > _x; }
    }

    [Fact]
    public void A_fast_unit_cannot_step_over_the_shoreline_into_the_water()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        LandUnitRoster.RegisterAll(s.Types);
        var tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        tank.State = UnitState.Moving;
        tank.OrderTargetPos = new WorldPos(1000, 0, 0); // straight out to sea
        s.Units.Add(tank);
        // Water starts just 1m ahead, far closer than one tick's step: the landing point alone would
        // have been dry land on the far shore in the old check.
        s.Water = new WaterBeyond(1f);

        for (int i = 0; i < 20; i++) MovementStep.Advance(s, 1f);

        Assert.True(s.Units[0].Position.X <= 1f, "the tank drove into the water");
    }

    /// <summary>Task139: a squad, a hostile within EnemyRadius, and a caller-supplied cover map.</summary>
    private static WarState StateWithEnemyNearby(out UnitInstance squad, out UnitInstance enemy)
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);

        squad = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        squad.OrderTargetPos = new WorldPos(3000, 0, 0); // far away: no objective lock
        s.Units.Add(squad);
        enemy = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(300, 0, 0));
        s.Units.Add(enemy);
        return s;
    }

    // --- Task139: naming the building a squad holds, so the engine side can show it abandoned ---

    /// <summary>The building is only claimed once the troops are actually in it. Flagging it the moment
    /// it was chosen would flicker buildings abandoned for squads that never arrive.</summary>
    [Fact]
    public void The_held_building_is_claimed_on_arrival_not_on_the_way()
    {
        var s = StateWithEnemyNearby(out UnitInstance squad, out UnitInstance enemy);
        var cover = new CoverMap();
        cover.Add(new WorldPos(40, 0, 0), 8f, 4242);
        s.Cover = cover;

        BuildingGarrisonStep.Advance(s, 0.1f);
        Assert.True(squad.CoverDestination.HasValue);
        Assert.Equal(0, squad.GarrisonBuildingId); // still crossing the street

        squad.Position = squad.CoverDestination.Value;
        BuildingGarrisonStep.Advance(s, 0.1f);
        Assert.Equal(4242, squad.GarrisonBuildingId);
    }

    [Fact]
    public void The_building_is_released_when_the_fight_is_over()
    {
        var s = StateWithEnemyNearby(out UnitInstance squad, out UnitInstance enemy);
        var cover = new CoverMap();
        cover.Add(new WorldPos(40, 0, 0), 8f, 4242);
        s.Cover = cover;

        BuildingGarrisonStep.Advance(s, 0.1f);
        squad.Position = squad.CoverDestination.Value;
        BuildingGarrisonStep.Advance(s, 0.1f);
        Assert.Equal(4242, squad.GarrisonBuildingId);

        enemy.State = UnitState.Dead;
        enemy.CurrentHP = 0f;
        BuildingGarrisonStep.Advance(s, 0.1f);

        Assert.Equal(0, squad.GarrisonBuildingId);
    }

    [Fact]
    public void The_building_is_released_when_the_hold_times_out()
    {
        var s = StateWithEnemyNearby(out UnitInstance squad, out UnitInstance enemy);
        var cover = new CoverMap();
        cover.Add(new WorldPos(40, 0, 0), 8f, 4242);
        s.Cover = cover;

        BuildingGarrisonStep.Advance(s, 0.1f);
        squad.Position = squad.CoverDestination.Value;
        for (float t = 0f; t <= BuildingGarrisonStep.MaxGarrisonHours + 1f; t += 0.5f)
            BuildingGarrisonStep.Advance(s, 0.5f);

        Assert.Equal(0, squad.GarrisonBuildingId);
        Assert.True(squad.GarrisonCooldown > 0f);
    }

    /// <summary>Cover the Game layer did not name (props, or the plain two-argument Add) must not claim
    /// building 0 - that is a real building id in the CS buffer.</summary>
    [Fact]
    public void Unnamed_cover_claims_no_building()
    {
        var s = StateWithEnemyNearby(out UnitInstance squad, out UnitInstance enemy);
        var cover = new CoverMap();
        cover.Add(new WorldPos(40, 0, 0), 8f);
        s.Cover = cover;

        BuildingGarrisonStep.Advance(s, 0.1f);
        squad.Position = squad.CoverDestination.Value;
        BuildingGarrisonStep.Advance(s, 0.1f);

        Assert.Equal(0, squad.GarrisonBuildingId);
    }
}
}
