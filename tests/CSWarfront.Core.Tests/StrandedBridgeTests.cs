using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task133 (playtest question: "can a bridge that is not connected to the road network be
/// crossed?"). It could not: a standalone bridge is its own connected component, so A* declared the far
/// bank unreachable and units fell back to the straight line and stopped at the water. Land units do drive
/// off-road, so the gap between a stranded fragment and the network is passable whenever the ground between
/// them is dry — which is what LinkStrandedComponents adds.</summary>
public class StrandedBridgeTests
{
    /// <summary>A river running north-south across x in [100, 140].</summary>
    private class River : IWaterSampler
    {
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return IsWater(x, z); }
        public bool IsWater(float x, float z) { return x > 100f && x < 140f; }
    }

    private static void AddRun(RoadGraph g, ushort firstId, params WorldPos[] nodes)
    {
        for (ushort i = 0; i < nodes.Length; i++) g.AddNode((ushort)(firstId + i), nodes[i]);
        for (ushort i = 0; i < nodes.Length - 1; i++) g.AddEdge((ushort)(firstId + i), (ushort)(firstId + i + 1));
    }

    /// <summary>Near-bank road, a bridge wired to nothing, and a far-bank road: three separate networks with
    /// 35 m of dry ground in each gap.</summary>
    private static RoadGraph BanksAndStrandedBridge()
    {
        var g = new RoadGraph();
        AddRun(g, 0, new WorldPos(0, 0, 200), new WorldPos(60, 0, 200));    // near bank
        AddRun(g, 10, new WorldPos(95, 0, 200), new WorldPos(145, 0, 200)); // the standalone bridge
        AddRun(g, 20, new WorldPos(180, 0, 200), new WorldPos(300, 0, 200)); // far bank
        return g;
    }

    [Fact]
    public void A_bridge_wired_to_nothing_is_joined_to_both_banks()
    {
        RoadGraph g = BanksAndStrandedBridge();
        Assert.Equal(3, DistinctComponents(g)); // near bank, bridge, far bank — all separate

        int linked = g.LinkStrandedComponents(new River());

        Assert.True(linked > 0, "expected the gaps either side of the bridge to be linked");
        Assert.Equal(1, DistinctComponents(g)); // all one network now
    }

    private static int DistinctComponents(RoadGraph g)
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        foreach (var kv in g.ComputeComponentIds()) seen.Add(kv.Value);
        return seen.Count;
    }

    [Fact]
    public void The_route_it_produces_runs_over_the_bridge()
    {
        RoadGraph g = BanksAndStrandedBridge();
        g.LinkStrandedComponents(new River());

        var path = g.FindPath(new WorldPos(60, 0, 200), new WorldPos(300, 0, 200), InvasionOrders.PathSnapRadius);

        Assert.NotNull(path);
        Assert.Contains(path, p => p.X == 95f);  // onto the bridge...
        Assert.Contains(path, p => p.X == 145f); // ...and off the far end
    }

    [Fact]
    public void Banks_with_no_bridge_at_all_are_never_linked()
    {
        // Same two banks, 120 m apart — inside the link radius, but the ground between them is the river.
        var g = new RoadGraph();
        AddRun(g, 0, new WorldPos(0, 0, 200), new WorldPos(60, 0, 200));
        AddRun(g, 20, new WorldPos(180, 0, 200), new WorldPos(300, 0, 200));

        Assert.Equal(0, g.LinkStrandedComponents(new River()));
        Assert.Null(g.FindPath(new WorldPos(60, 0, 200), new WorldPos(300, 0, 200), InvasionOrders.PathSnapRadius));
    }

    [Fact]
    public void A_fragment_up_a_cliff_is_not_linked()
    {
        var g = new RoadGraph();
        AddRun(g, 0, new WorldPos(0, 0, 200), new WorldPos(60, 0, 200));
        AddRun(g, 20, new WorldPos(80, 80, 200), new WorldPos(100, 80, 200)); // 20 m away, 80 m up

        Assert.Equal(0, g.LinkStrandedComponents(new River()));
    }

    [Fact]
    public void A_healthy_network_is_left_exactly_as_it_was()
    {
        var g = new RoadGraph();
        AddRun(g, 0, new WorldPos(0, 0, 0), new WorldPos(60, 0, 0), new WorldPos(60, 0, 60), new WorldPos(0, 0, 60));

        Assert.Equal(0, g.LinkStrandedComponents(new River()));
    }

    [Fact]
    public void A_tank_actually_drives_over_the_stranded_bridge()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        s.Water = new River();
        s.Roads = BanksAndStrandedBridge();
        s.Roads.LinkStrandedComponents(new River());

        var target = new MilitaryBase(1, BaseType.Army, new WorldPos(300, 0, 200));
        target.OwnerFactionId = 1;
        s.Bases.Add(target);

        var tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(60, 0, 200));
        tank.State = UnitState.Moving;
        s.Units.Add(tank);

        for (int i = 0; i < 400; i++)
        {
            InvasionOrders.AssignAdvance(s, 0, 1f);
            MovementStep.Advance(s, 1f);
        }

        Assert.True(tank.Position.X > 145f, "the tank never made it over the stranded bridge");
    }
}
}
