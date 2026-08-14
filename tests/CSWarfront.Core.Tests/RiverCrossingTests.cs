using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task132 (playtest: "vehicles drive into the river instead of taking the road across it"):
/// a unit halted by water asks for a road route over it, instead of waiting out the pathfinding
/// cooldown at the bank.</summary>
public class RiverCrossingTests
{
    /// <summary>A river running north-south across x in [100, 140], bridged at z = 200.</summary>
    private class River : IWaterSampler
    {
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return IsWater(x, z); }
        public bool IsWater(float x, float z) { return x > 100f && x < 140f; }
    }

    private static WarState StateAtTheBank(out UnitInstance tank)
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        s.Water = new River();

        // The objective is straight across the water.
        var target = new MilitaryBase(1, BaseType.Army, new WorldPos(300, 0, 0));
        target.OwnerFactionId = 1;
        s.Bases.Add(target);

        tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(60, 0, 0));
        tank.State = UnitState.Moving;
        tank.OrderTargetPos = new WorldPos(300, 0, 0);
        s.Units.Add(tank);
        return s;
    }

    /// <summary>A road running to a bridge at z=200 and back down the far bank.</summary>
    private static RoadGraph BridgeRoad()
    {
        var g = new RoadGraph();
        var nodes = new[]
        {
            new WorldPos(60, 0, 0), new WorldPos(60, 0, 200),
            new WorldPos(120, 0, 200), // on the bridge
            new WorldPos(300, 0, 200), new WorldPos(300, 0, 0)
        };
        for (ushort i = 0; i < nodes.Length; i++) g.AddNode(i, nodes[i]);
        for (ushort i = 0; i < nodes.Length - 1; i++) g.AddEdge(i, (ushort)(i + 1));
        return g;
    }

    [Fact]
    public void A_unit_stopped_by_water_is_flagged_as_needing_a_crossing()
    {
        UnitInstance tank;
        WarState s = StateAtTheBank(out tank);

        for (int i = 0; i < 30; i++) MovementStep.Advance(s, 1f);

        Assert.True(tank.Position.X <= 100f, "the tank should stop at the bank");
        Assert.True(tank.WaterBlocked, "and report that water is what stopped it");
    }

    [Fact]
    public void It_then_gets_a_road_route_over_the_bridge_without_waiting_out_the_cooldown()
    {
        UnitInstance tank;
        WarState s = StateAtTheBank(out tank);
        s.Roads = BridgeRoad();

        for (int i = 0; i < 30; i++) MovementStep.Advance(s, 1f);
        Assert.True(tank.WaterBlocked);

        // A failed attempt would normally park the unit for PathRetryFailCooldownHours; being blocked by
        // water overrides that, so the very next assignment produces a route.
        tank.PathRetryCooldown = InvasionOrders.PathRetryFailCooldownHours;
        InvasionOrders.AssignAdvance(s, 0, 0.1f);

        Assert.NotNull(tank.Path);
        Assert.NotEmpty(tank.Path);
    }

    [Fact]
    public void Following_that_route_actually_gets_it_across()
    {
        UnitInstance tank;
        WarState s = StateAtTheBank(out tank);
        s.Roads = BridgeRoad();

        for (int i = 0; i < 400; i++)
        {
            InvasionOrders.AssignAdvance(s, 0, 1f);
            MovementStep.Advance(s, 1f);
        }

        Assert.True(tank.Position.X > 140f, "the tank never made it over the river");
    }
}
}
