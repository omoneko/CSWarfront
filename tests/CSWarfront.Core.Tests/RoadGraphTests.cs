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

    // Task88: 目的地側スナップ半径の独立指定（宿敵追撃の道路経路化）。
    [Fact]
    public void FindPath_with_unlimited_dest_snap_reaches_the_node_nearest_a_far_target()
    {
        var g = new RoadGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));
        g.AddNode(2, new WorldPos(100, 0, 0));
        g.AddNode(3, new WorldPos(200, 0, 0));
        g.AddEdge(1, 2);
        g.AddEdge(2, 3);

        // 目的地(600,0,0)は最寄りノード(200,0,0)から400離れている＝従来のsnapRadius(200)では
        // スナップ失敗でnullだったケース。
        var strict = g.FindPath(new WorldPos(10, 0, 0), new WorldPos(600, 0, 0), 200f, 0u, 0f);
        Assert.Null(strict);

        var relaxed = g.FindPath(new WorldPos(10, 0, 0), new WorldPos(600, 0, 0), 200f, 0u, 0f, float.MaxValue);
        Assert.NotNull(relaxed);
        // 経路の末端は目的地に最も近いノード(200,0,0)。残り区間は呼び出し側の直線フォールバックが担う。
        Assert.Equal(200f, relaxed[relaxed.Count - 1].X, 1);
    }

    [Fact]
    public void FindPath_unlimited_dest_snap_still_requires_origin_within_snap_radius()
    {
        var g = new RoadGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));
        g.AddNode(2, new WorldPos(100, 0, 0));
        g.AddEdge(1, 2);

        // 出発点が道路から遠い（>200）場合は従来どおりnull（直線移動が正しい）。
        var path = g.FindPath(new WorldPos(0, 0, 500), new WorldPos(100, 0, 0), 200f, 0u, 0f, float.MaxValue);
        Assert.Null(path);
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

    // --- Task108: 連結成分の解析とノード融合（軍用列車が走れない不具合の調査で追加）---

    [Fact]
    public void ComputeComponentIds_separates_disconnected_pieces()
    {
        var g = new RoadGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));
        g.AddNode(2, new WorldPos(100, 0, 0));
        g.AddEdge(1, 2);
        g.AddNode(3, new WorldPos(1000, 0, 0)); // 孤立

        var comps = g.ComputeComponentIds();
        Assert.Equal(comps[1], comps[2]);
        Assert.NotEqual(comps[1], comps[3]);

        int largest;
        Assert.True(g.TryGetLargestComponent(comps, out largest));
        Assert.Equal(comps[1], largest);
    }

    [Fact]
    public void WeldCoincidentNodes_joins_seams_but_not_overpasses()
    {
        var g = new RoadGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));
        g.AddNode(2, new WorldPos(100, 0, 0));
        g.AddEdge(1, 2);
        // 継ぎ目: ほぼ同じ位置にある別idのノード（別ネットワーク側の端点）。
        g.AddNode(3, new WorldPos(102, 0.5f, 0));
        g.AddNode(4, new WorldPos(300, 0, 0));
        g.AddEdge(3, 4);
        // 立体交差: 水平には重なるが20m上（繋いではいけない）。
        g.AddNode(5, new WorldPos(100, 20, 0));
        g.AddNode(6, new WorldPos(100, 20, 300));
        g.AddEdge(5, 6);

        int added = g.WeldCoincidentNodes(6f, 3f);

        Assert.Equal(1, added);
        var comps = g.ComputeComponentIds();
        Assert.Equal(comps[1], comps[4]);      // 継ぎ目が繋がった
        Assert.NotEqual(comps[1], comps[5]);   // 立体交差は繋がっていない
    }
}
