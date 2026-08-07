using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

/// <summary>
/// Tests for the jittered overload of FindPath (detour-route selection).
/// RoadGraph itself does not expose its edge set, so the tests use a helper (TrackedGraph) that
/// records "which nodes connect to which" on its own alongside graph construction, and then
/// verifies that the returned route traverses only edges that actually exist.
/// </summary>
public class RoadGraphJitterTests
{
    /// <summary>A thin wrapper that records adjacency and coordinates alongside RoadGraph construction so the returned route can be validated.</summary>
    private class TrackedGraph
    {
        public readonly RoadGraph Graph = new RoadGraph();
        private readonly Dictionary<ushort, WorldPos> _positions = new Dictionary<ushort, WorldPos>();
        private readonly Dictionary<ushort, HashSet<ushort>> _adjacency = new Dictionary<ushort, HashSet<ushort>>();

        public void AddNode(ushort id, WorldPos pos)
        {
            Graph.AddNode(id, pos);
            _positions[id] = pos;
            if (!_adjacency.ContainsKey(id)) _adjacency[id] = new HashSet<ushort>();
        }

        public void AddEdge(ushort a, ushort b)
        {
            Graph.AddEdge(a, b);
            _adjacency[a].Add(b);
            _adjacency[b].Add(a);
        }

        private ushort NodeAt(WorldPos p)
        {
            foreach (var kv in _positions)
                if (kv.Value.HorizontalDistanceTo(p) < 0.01f) return kv.Key;
            Assert.Fail($"no tracked node at ({p.X},{p.Z})");
            return 0;
        }

        /// <summary>Verifies that every consecutive segment of the route is a real edge and that the route genuinely reaches the goal.</summary>
        public void AssertValidRoute(List<WorldPos> route, WorldPos start, WorldPos goal)
        {
            Assert.NotNull(route);
            Assert.NotEmpty(route);

            ushort current = NodeAt(start);
            foreach (var wp in route)
            {
                ushort next = NodeAt(wp);
                Assert.True(_adjacency[current].Contains(next),
                    $"expected an edge between node {current} and node {next}, but none was recorded");
                current = next;
            }

            ushort goalId = NodeAt(goal);
            Assert.Equal(goalId, current);
        }
    }

    private static TrackedGraph Chain()
    {
        var g = new TrackedGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));
        g.AddNode(2, new WorldPos(100, 0, 0));
        g.AddNode(3, new WorldPos(200, 0, 0));
        g.AddEdge(1, 2);
        g.AddEdge(2, 3);
        return g;
    }

    private static TrackedGraph Grid3x3()
    {
        var g = new TrackedGraph();
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                g.AddNode((ushort)(row * 3 + col + 1), new WorldPos(col * 100, 0, row * 100));
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                ushort id = (ushort)(row * 3 + col + 1);
                if (col < 2) g.AddEdge(id, (ushort)(id + 1));
                if (row < 2) g.AddEdge(id, (ushort)(id + 3));
            }
        return g;
    }

    /// <summary>
    /// From A(0,0,0) to D(100,0,0) there are two parallel routes, one via B(50,0,50) and one via
    /// C(50,0,-50); they are symmetric so their costs are exactly equal (without jitter the
    /// tie-break picks the lower id = B). With jitter, the per-edge factor varies with the seed, so
    /// different seeds alternate between the B route and the C route.
    /// </summary>
    private static TrackedGraph ParallelRoutes()
    {
        var g = new TrackedGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));     // A start
        g.AddNode(2, new WorldPos(50, 0, 50));   // B (lower id, detour "north")
        g.AddNode(3, new WorldPos(50, 0, -50));  // C (higher id, detour "south")
        g.AddNode(4, new WorldPos(100, 0, 0));   // D goal
        g.AddEdge(1, 2); g.AddEdge(2, 4);
        g.AddEdge(1, 3); g.AddEdge(3, 4);
        return g;
    }

    private static readonly WorldPos ParallelStart = new WorldPos(0, 0, 0);
    private static readonly WorldPos ParallelGoal = new WorldPos(100, 0, 0);

    // --- the 3-arg overload / seed=0,jitter=0 must be exactly identical to the current shortest-path search ---

    [Fact]
    public void ThreeArg_and_zero_seed_zero_jitter_produce_identical_path_on_chain()
    {
        var g = Chain();
        var a = g.Graph.FindPath(new WorldPos(0, 0, 0), new WorldPos(200, 0, 0), 10f);
        var b = g.Graph.FindPath(new WorldPos(0, 0, 0), new WorldPos(200, 0, 0), 10f, 0u, 0f);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].X, b[i].X, 3);
            Assert.Equal(a[i].Z, b[i].Z, 3);
        }
    }

    [Fact]
    public void ThreeArg_and_zero_seed_zero_jitter_produce_identical_path_on_parallel_routes()
    {
        var g = ParallelRoutes();
        var a = g.Graph.FindPath(ParallelStart, ParallelGoal, 10f);
        var b = g.Graph.FindPath(ParallelStart, ParallelGoal, 10f, 0u, 0f);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].X, b[i].X, 3);
            Assert.Equal(a[i].Z, b[i].Z, 3);
        }
        // tie-break: the lower id (node 2 = B, the north side) should be chosen
        Assert.Equal(50f, a[0].Z, 2);
    }

    [Fact]
    public void Nonzero_seed_but_zero_jitter_behaves_like_shortest_path()
    {
        var g = ParallelRoutes();
        var shortest = g.Graph.FindPath(ParallelStart, ParallelGoal, 10f);
        var withSeedNoJitter = g.Graph.FindPath(ParallelStart, ParallelGoal, 10f, 42u, 0f);
        Assert.Equal(shortest[0].Z, withSeedNoJitter[0].Z, 2);
    }

    [Fact]
    public void Zero_seed_but_positive_jitter_behaves_like_shortest_path()
    {
        var g = ParallelRoutes();
        var shortest = g.Graph.FindPath(ParallelStart, ParallelGoal, 10f);
        var withJitterNoSeed = g.Graph.FindPath(ParallelStart, ParallelGoal, 10f, 0u, 0.35f);
        Assert.Equal(shortest[0].Z, withJitterNoSeed[0].Z, 2);
    }

    // --- the actual "detour selection" behavior ---

    [Fact]
    public void Different_seeds_choose_different_routes_for_at_least_some_seeds()
    {
        var g = ParallelRoutes();
        var chosenBranches = new HashSet<float>(); // distinguished by the via-node's Z coordinate (+50 or -50)

        for (uint seed = 1; seed <= 40; seed++)
        {
            var path = g.Graph.FindPath(ParallelStart, ParallelGoal, 10f, seed, 0.35f);
            g.AssertValidRoute(path, ParallelStart, ParallelGoal);
            chosenBranches.Add(path[0].Z);
        }

        Assert.True(chosenBranches.Count > 1,
            "expected at least two different detour branches to be chosen across 40 seeds, got only: " +
            string.Join(",", chosenBranches));
    }

    [Fact]
    public void Same_seed_always_yields_the_same_route()
    {
        var g = ParallelRoutes();
        const uint seed = 1234u;
        var p1 = g.Graph.FindPath(ParallelStart, ParallelGoal, 10f, seed, 0.35f);
        var p2 = g.Graph.FindPath(ParallelStart, ParallelGoal, 10f, seed, 0.35f);
        var p3 = g.Graph.FindPath(ParallelStart, ParallelGoal, 10f, seed, 0.35f);

        Assert.Equal(p1.Count, p2.Count);
        Assert.Equal(p1.Count, p3.Count);
        for (int i = 0; i < p1.Count; i++)
        {
            Assert.Equal(p1[i].X, p2[i].X, 3); Assert.Equal(p1[i].Z, p2[i].Z, 3);
            Assert.Equal(p1[i].X, p3[i].X, 3); Assert.Equal(p1[i].Z, p3[i].Z, 3);
        }
    }

    [Fact]
    public void Different_units_ie_different_seeds_can_get_different_routes_on_a_grid()
    {
        // The 3x3 grid has multiple equal-length shortest paths from (0,0) to (200,200).
        var g = Grid3x3();
        var firstWaypoints = new HashSet<string>();

        for (uint seed = 1; seed <= 25; seed++)
        {
            var path = g.Graph.FindPath(new WorldPos(0, 0, 0), new WorldPos(200, 0, 200), 10f, seed, 0.35f);
            g.AssertValidRoute(path, new WorldPos(0, 0, 0), new WorldPos(200, 0, 200));
            firstWaypoints.Add($"{path[0].X}:{path[0].Z}");
        }

        Assert.True(firstWaypoints.Count > 1,
            "expected route variety on the grid across 25 seeds, got only: " + string.Join(",", firstWaypoints));
    }

    // --- connectivity/reachability must not change under jitter ---

    [Fact]
    public void Jittered_search_still_returns_null_for_disconnected_graph()
    {
        var g = new TrackedGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));
        g.AddNode(2, new WorldPos(500, 0, 0)); // no edges
        var path = g.Graph.FindPath(new WorldPos(0, 0, 0), new WorldPos(500, 0, 0), 10f, 7u, 0.35f);
        Assert.Null(path);
    }

    [Fact]
    public void Jittered_search_still_returns_empty_list_for_start_equals_goal()
    {
        var g = Chain();
        var path = g.Graph.FindPath(new WorldPos(0, 0, 0), new WorldPos(0, 0, 0), 10f, 7u, 0.35f);
        Assert.NotNull(path);
        Assert.Empty(path);
    }

    [Fact]
    public void Jitter_never_makes_a_findable_path_disappear()
    {
        var g = Grid3x3();
        var unjittered = g.Graph.FindPath(new WorldPos(0, 0, 0), new WorldPos(200, 0, 200), 10f);
        Assert.NotNull(unjittered);

        for (uint seed = 1; seed <= 30; seed++)
        {
            var jittered = g.Graph.FindPath(new WorldPos(0, 0, 0), new WorldPos(200, 0, 200), 10f, seed, 0.35f);
            Assert.NotNull(jittered);
            g.AssertValidRoute(jittered, new WorldPos(0, 0, 0), new WorldPos(200, 0, 200));
        }
    }
}
