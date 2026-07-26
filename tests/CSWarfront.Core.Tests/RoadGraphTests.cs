using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class RoadGraphTests
{
    private static RoadGraph Chain()
    {
        // A(0,0,0) - B(100,0,0) - C(200,0,0)
        var g = new RoadGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));      // A
        g.AddNode(2, new WorldPos(100, 0, 0));    // B
        g.AddNode(3, new WorldPos(200, 0, 0));    // C
        g.AddEdge(1, 2);
        g.AddEdge(2, 3);
        return g;
    }

    private static RoadGraph Grid3x3()
    {
        // node id = row*3 + col + 1, spacing 100
        var g = new RoadGraph();
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                g.AddNode((ushort)(row * 3 + col + 1), new WorldPos(col * 100, 0, row * 100));
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
            {
                ushort id = (ushort)(row * 3 + col + 1);
                if (col < 2) g.AddEdge(id, (ushort)(id + 1));       // horizontal
                if (row < 2) g.AddEdge(id, (ushort)(id + 3));       // vertical
            }
        return g;
    }

    [Fact]
    public void AddNode_is_idempotent_for_repeated_id()
    {
        var g = new RoadGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));
        g.AddNode(1, new WorldPos(999, 0, 999)); // ignored, first wins
        Assert.Equal(1, g.NodeCount);
    }

    [Fact]
    public void AddEdge_ignores_unknown_ids_and_self_loops()
    {
        var g = new RoadGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));
        g.AddEdge(1, 1);   // self loop
        g.AddEdge(1, 99);  // unknown
        g.AddEdge(99, 1);  // unknown
        // no exception; graph still has just node 1, no path anywhere else
        Assert.Equal(1, g.NodeCount);
    }

    [Fact]
    public void TryFindNearestNode_finds_node_within_maxDistance()
    {
        var g = Chain();
        Assert.True(g.TryFindNearestNode(new WorldPos(10, 0, 0), 50f, out var id));
        Assert.Equal((ushort)1, id);
    }

    [Fact]
    public void TryFindNearestNode_fails_outside_maxDistance()
    {
        var g = Chain();
        Assert.False(g.TryFindNearestNode(new WorldPos(10, 0, 0), 5f, out _));
    }

    [Fact]
    public void FindPath_chain_returns_nodes_after_start()
    {
        var g = Chain();
        var path = g.FindPath(new WorldPos(0, 0, 0), new WorldPos(200, 0, 0), 10f);
        Assert.NotNull(path);
        Assert.Equal(2, path.Count);
        Assert.Equal(100f, path[0].X, 2);
        Assert.Equal(200f, path[1].X, 2);
    }

    [Fact]
    public void FindPath_grid_consecutive_points_are_connected_by_real_edges()
    {
        var g = Grid3x3();
        var path = g.FindPath(new WorldPos(0, 0, 0), new WorldPos(200, 0, 200), 10f);
        Assert.NotNull(path);
        Assert.True(path.Count > 0);
        Assert.Equal(200f, path[path.Count - 1].X, 2);
        Assert.Equal(200f, path[path.Count - 1].Z, 2);
        // every consecutive pair (including from start) must be one grid-step apart (100 units, axis-aligned)
        var full = new List<WorldPos> { new WorldPos(0, 0, 0) };
        full.AddRange(path);
        for (int i = 0; i < full.Count - 1; i++)
        {
            float d = full[i].HorizontalDistanceTo(full[i + 1]);
            Assert.Equal(100f, d, 2);
        }
    }

    [Fact]
    public void FindPath_returns_null_when_disconnected()
    {
        var g = new RoadGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));
        g.AddNode(2, new WorldPos(500, 0, 0)); // no edge between them
        var path = g.FindPath(new WorldPos(0, 0, 0), new WorldPos(500, 0, 0), 10f);
        Assert.Null(path);
    }

    [Fact]
    public void FindPath_returns_null_when_snap_fails()
    {
        var g = Chain();
        var path = g.FindPath(new WorldPos(0, 0, 0), new WorldPos(5000, 0, 5000), 10f);
        Assert.Null(path);
    }

    [Fact]
    public void FindPath_start_equals_goal_returns_empty_list()
    {
        var g = Chain();
        var path = g.FindPath(new WorldPos(0, 0, 0), new WorldPos(0, 0, 0), 10f);
        Assert.NotNull(path);
        Assert.Empty(path);
    }

    [Fact]
    public void FindPath_is_deterministic_across_repeated_calls()
    {
        var g = Grid3x3();
        var p1 = g.FindPath(new WorldPos(0, 0, 0), new WorldPos(200, 0, 200), 10f);
        var p2 = g.FindPath(new WorldPos(0, 0, 0), new WorldPos(200, 0, 200), 10f);
        Assert.Equal(p1.Count, p2.Count);
        for (int i = 0; i < p1.Count; i++)
        {
            Assert.Equal(p1[i].X, p2[i].X, 3);
            Assert.Equal(p1[i].Z, p2[i].Z, 3);
        }
    }

    [Fact]
    public void FindPath_tie_break_prefers_lower_node_id()
    {
        // Two equally short routes from A to D: A-B-D and A-C-D, both cost 200.
        // B has lower id than C, so the deterministic tie-break should choose A-B-D.
        var g = new RoadGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));     // A
        g.AddNode(2, new WorldPos(100, 0, 0));   // B (lower id)
        g.AddNode(3, new WorldPos(0, 0, 100));   // C (higher id)
        g.AddNode(4, new WorldPos(100, 0, 100)); // D
        g.AddEdge(1, 2); g.AddEdge(2, 4);
        g.AddEdge(1, 3); g.AddEdge(3, 4);

        var path = g.FindPath(new WorldPos(0, 0, 0), new WorldPos(100, 0, 100), 10f);
        Assert.NotNull(path);
        Assert.Equal(2, path.Count);
        Assert.Equal(100f, path[0].X, 2); // via B, not C
        Assert.Equal(0f, path[0].Z, 2);
    }
}
