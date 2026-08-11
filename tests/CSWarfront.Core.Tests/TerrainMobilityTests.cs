using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task125: terrain effects on land movement (slope + off-road), per mobility class.</summary>
public class TerrainMobilityTests
{
    /// <summary>Ground that rises steeply east of x=0 and is flat everywhere else — a bank the unit
    /// meets head-on while marching east.</summary>
    private class BankSampler : IHeightSampler
    {
        private readonly float _risePerMetre;
        public BankSampler(float risePerMetre) { _risePerMetre = risePerMetre; }
        public bool TrySampleHeight(float x, float z, out float height)
        {
            height = x > 0f ? x * _risePerMetre : 0f;
            return true;
        }
    }

    private static WarState MarchingUnit(string typeKey, IHeightSampler ground)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        LandUnitRoster.RegisterAll(s.Types);
        var u = new UnitInstance(1, typeKey, 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(1000, 0, 0); // due east, straight into the bank
        s.Units.Add(u);
        s.Height = ground;
        return s;
    }

    [Fact]
    public void Categories_map_to_the_three_mobility_classes()
    {
        Assert.Equal(MobilityClass.Infantry, TerrainMobility.ClassOf(UnitCategory.Infantry));
        Assert.Equal(MobilityClass.Infantry, TerrainMobility.ClassOf(UnitCategory.DroneInfantry));
        Assert.Equal(MobilityClass.Tracked, TerrainMobility.ClassOf(UnitCategory.Tank));
        Assert.Equal(MobilityClass.Tracked, TerrainMobility.ClassOf(UnitCategory.Artillery));
        Assert.Equal(MobilityClass.Tracked, TerrainMobility.ClassOf(UnitCategory.AntiAir));
        Assert.Equal(MobilityClass.Tracked, TerrainMobility.ClassOf(UnitCategory.Apc));
        Assert.Equal(MobilityClass.Wheeled, TerrainMobility.ClassOf(UnitCategory.MechInfantry));
        Assert.Equal(MobilityClass.Wheeled, TerrainMobility.ClassOf(UnitCategory.SupplyTruck));
    }

    [Fact]
    public void Slope_limits_and_off_road_penalties_are_ordered_by_class()
    {
        // Infantry copes best with terrain, wheeled worst — in both dimensions.
        Assert.True(TerrainMobility.MaxSlopeDegrees(MobilityClass.Infantry)
                  > TerrainMobility.MaxSlopeDegrees(MobilityClass.Tracked));
        Assert.True(TerrainMobility.MaxSlopeDegrees(MobilityClass.Tracked)
                  > TerrainMobility.MaxSlopeDegrees(MobilityClass.Wheeled));
        Assert.True(TerrainMobility.OffRoadFactor(MobilityClass.Infantry)
                  > TerrainMobility.OffRoadFactor(MobilityClass.Tracked));
        Assert.True(TerrainMobility.OffRoadFactor(MobilityClass.Tracked)
                  > TerrainMobility.OffRoadFactor(MobilityClass.Wheeled));
    }

    [Fact]
    public void Roads_are_never_penalised_and_slopes_slow_movement_off_road()
    {
        Assert.Equal(1f, TerrainMobility.SpeedFactor(MobilityClass.Wheeled, 20f, true), 3);

        float flat = TerrainMobility.SpeedFactor(MobilityClass.Tracked, 0f, false);
        float gentle = TerrainMobility.SpeedFactor(MobilityClass.Tracked, 10f, false);
        float steep = TerrainMobility.SpeedFactor(MobilityClass.Tracked, 30f, false);

        Assert.Equal(TerrainMobility.OffRoadFactor(MobilityClass.Tracked), flat, 3);
        Assert.True(gentle < flat);
        Assert.True(steep < gentle);
        Assert.True(steep > 0f); // slowed, never frozen
    }

    [Fact]
    public void Traversability_uses_the_absolute_slope()
    {
        Assert.True(TerrainMobility.CanTraverse(MobilityClass.Wheeled, 20f));
        Assert.False(TerrainMobility.CanTraverse(MobilityClass.Wheeled, 30f));
        Assert.False(TerrainMobility.CanTraverse(MobilityClass.Wheeled, -30f)); // a cliff down is no better
        Assert.True(TerrainMobility.CanTraverse(MobilityClass.Infantry, 50f));
    }

    [Fact]
    public void Infantry_climbs_a_bank_that_stops_a_truck_head_on()
    {
        // ~45 degrees: within infantry's 55 limit, beyond the wheeled 22 limit.
        var infantryState = MarchingUnit("Infantry_T1", new BankSampler(1f));
        var truckState = MarchingUnit("SupplyTruck_T1", new BankSampler(1f));

        MovementStep.Advance(infantryState, 1f);
        MovementStep.Advance(truckState, 1f);

        // Infantry takes the bank head-on: straight up it, no sideways deviation.
        Assert.True(infantryState.Units[0].Position.X > 0f, "infantry should climb the bank");
        Assert.Equal(0f, infantryState.Units[0].Position.Z, 2);

        // The truck refuses to climb and skirts along it instead of driving up.
        Assert.True(System.Math.Abs(truckState.Units[0].Position.Z) > 0.01f,
            "wheeled unit should skirt the bank rather than climb it");
    }

    [Fact]
    public void Blocked_units_skirt_around_rather_than_freeze_when_a_way_exists()
    {
        // The bank only exists east of x=0, so deflecting north/south is viable.
        var s = MarchingUnit("SupplyTruck_T1", new BankSampler(1f));

        MovementStep.Advance(s, 1f);

        var pos = s.Units[0].Position;
        Assert.True(System.Math.Abs(pos.Z) > 0.01f, "expected a sideways deflection instead of a dead stop");
    }

    [Fact]
    public void Gentle_ground_is_traversable_by_everything_but_still_costs_speed_off_road()
    {
        // ~11 degrees: fine for every class.
        var truck = MarchingUnit("SupplyTruck_T1", new BankSampler(0.2f));
        var tank = MarchingUnit("Tank_T1", new BankSampler(0.2f));

        MovementStep.Advance(truck, 1f);
        MovementStep.Advance(tank, 1f);

        Assert.True(truck.Units[0].Position.X > 0f);
        Assert.True(tank.Units[0].Position.X > 0f);

        // Both moved straight ahead, so the distance ratio is purely the class difference.
        UnitType truckType = truck.Types.Get("SupplyTruck_T1");
        UnitType tankType = tank.Types.Get("Tank_T1");
        float truckShare = truck.Units[0].Position.X / truckType.Speed;
        float tankShare = tank.Units[0].Position.X / tankType.Speed;
        Assert.True(tankShare > truckShare, "tracked units should keep more of their speed off-road");
    }
}
}
