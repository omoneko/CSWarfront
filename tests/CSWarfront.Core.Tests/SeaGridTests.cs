using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

/// <summary>
/// SeaGrid（Task92: 海上ユニットの本格的な経路探索）のテスト。
/// 10x10・セル10mのグリッド（原点0,0）を合成して検証する。
/// </summary>
public class SeaGridTests
{
    /// <summary>全セル航行可能な10x10グリッド。</summary>
    private static SeaGrid OpenGrid()
    {
        var g = new SeaGrid(0f, 0f, 10f, 10, 10);
        for (int z = 0; z < 10; z++)
            for (int x = 0; x < 10; x++)
                g.SetNavigable(x, z, true);
        return g;
    }

    [Fact]
    public void FindPath_on_open_water_reaches_the_goal_cell()
    {
        var g = OpenGrid();
        List<WorldPos> path = g.FindPath(new WorldPos(5, 0, 5), new WorldPos(95, 0, 5), 50f);

        Assert.NotNull(path);
        Assert.True(path.Count > 0);
        // 経路の末端は目的地セル(9,0)の中心(95,5)。
        Assert.Equal(95f, path[path.Count - 1].X, 1);
        Assert.Equal(5f, path[path.Count - 1].Z, 1);
    }

    [Fact]
    public void FindPath_routes_around_a_peninsula_through_the_gap()
    {
        // x=5の列を z=0..7 まで陸地にする（下側だけ開いた壁＝岬）。直線では渡れない。
        var g = OpenGrid();
        for (int z = 0; z <= 7; z++) g.SetNavigable(5, z, false);

        List<WorldPos> path = g.FindPath(new WorldPos(15, 0, 15), new WorldPos(85, 0, 15), 50f);

        Assert.NotNull(path);
        // 経路のどこかで z>=80（開いている z=8..9 の帯）を通って回り込むはず。
        bool wentAround = false;
        for (int i = 0; i < path.Count; i++)
            if (path[i].Z >= 80f) wentAround = true;
        Assert.True(wentAround, "expected the path to detour around the peninsula through the southern gap");
        Assert.Equal(85f, path[path.Count - 1].X, 1);
    }

    [Fact]
    public void FindPath_returns_null_when_goal_is_landlocked()
    {
        // 目的地セルの周囲を全て陸地で囲む。
        var g = OpenGrid();
        for (int z = 4; z <= 6; z++)
            for (int x = 4; x <= 6; x++)
                g.SetNavigable(x, z, false);

        List<WorldPos> path = g.FindPath(new WorldPos(5, 0, 5), new WorldPos(55, 0, 55), 8f);
        Assert.Null(path); // スナップ半径8では航行可能セルに届かない
    }

    [Fact]
    public void FindPath_snaps_an_inland_start_to_the_nearest_navigable_cell()
    {
        var g = OpenGrid();
        g.SetNavigable(0, 0, false); // 出発セルだけ陸地

        List<WorldPos> path = g.FindPath(new WorldPos(5, 0, 5), new WorldPos(95, 0, 95), 50f);
        Assert.NotNull(path); // 隣のセルへスナップして探索が成立する
    }

    [Fact]
    public void FindPath_does_not_cut_diagonal_corners_across_land()
    {
        // (1,0)と(0,1)が陸地: (0,0)から(1,1)へは斜め一歩では行けない（角の陸をかすめるため）。
        var g = OpenGrid();
        g.SetNavigable(1, 0, false);
        g.SetNavigable(0, 1, false);

        List<WorldPos> path = g.FindPath(new WorldPos(5, 0, 5), new WorldPos(15, 0, 15), 5f);
        Assert.Null(path); // 2x2の角に完全に塞がれている（唯一の抜け道が斜めカットのみ→禁止）
    }

    [Fact]
    public void FindPath_is_deterministic()
    {
        var g = OpenGrid();
        for (int z = 0; z <= 7; z++) g.SetNavigable(5, z, false);

        var a = g.FindPath(new WorldPos(15, 0, 15), new WorldPos(85, 0, 15), 50f);
        var b = g.FindPath(new WorldPos(15, 0, 15), new WorldPos(85, 0, 15), 50f);

        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].X, b[i].X, 3);
            Assert.Equal(a[i].Z, b[i].Z, 3);
        }
    }

    [Fact]
    public void Same_start_and_goal_cell_returns_empty_path()
    {
        var g = OpenGrid();
        List<WorldPos> path = g.FindPath(new WorldPos(5, 0, 5), new WorldPos(7, 0, 7), 50f);
        Assert.NotNull(path);
        Assert.Empty(path);
    }
}
