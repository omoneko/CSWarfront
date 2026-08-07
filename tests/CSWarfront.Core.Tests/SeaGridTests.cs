using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

/// <summary>
/// Tests for SeaGrid (Task92: full pathfinding for naval units).
/// Verified against a synthesized 10x10 grid with 10 m cells (origin 0,0).
/// </summary>
public class SeaGridTests
{
    /// <summary>A 10x10 grid where every cell is navigable.</summary>
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
        // The end of the path is the centre (95,5) of the destination cell (9,0).
        Assert.Equal(95f, path[path.Count - 1].X, 1);
        Assert.Equal(5f, path[path.Count - 1].Z, 1);
    }

    [Fact]
    public void FindPath_routes_around_a_peninsula_through_the_gap()
    {
        // Turn column x=5 into land for z=0..7 (a wall open only at the bottom = a peninsula). Cannot be crossed in a straight line.
        var g = OpenGrid();
        for (int z = 0; z <= 7; z++) g.SetNavigable(5, z, false);

        List<WorldPos> path = g.FindPath(new WorldPos(15, 0, 15), new WorldPos(85, 0, 15), 50f);

        Assert.NotNull(path);
        // Somewhere along the path it should go around via z>=80 (the open z=8..9 band).
        bool wentAround = false;
        for (int i = 0; i < path.Count; i++)
            if (path[i].Z >= 80f) wentAround = true;
        Assert.True(wentAround, "expected the path to detour around the peninsula through the southern gap");
        Assert.Equal(85f, path[path.Count - 1].X, 1);
    }

    [Fact]
    public void FindPath_returns_null_when_goal_is_landlocked()
    {
        // Surround the destination cell entirely with land.
        var g = OpenGrid();
        for (int z = 4; z <= 6; z++)
            for (int x = 4; x <= 6; x++)
                g.SetNavigable(x, z, false);

        List<WorldPos> path = g.FindPath(new WorldPos(5, 0, 5), new WorldPos(55, 0, 55), 8f);
        Assert.Null(path); // A snap radius of 8 cannot reach any navigable cell
    }

    [Fact]
    public void FindPath_snaps_an_inland_start_to_the_nearest_navigable_cell()
    {
        var g = OpenGrid();
        g.SetNavigable(0, 0, false); // Only the starting cell is land

        List<WorldPos> path = g.FindPath(new WorldPos(5, 0, 5), new WorldPos(95, 0, 95), 50f);
        Assert.NotNull(path); // Snaps to the adjacent cell so the search succeeds
    }

    [Fact]
    public void FindPath_does_not_cut_diagonal_corners_across_land()
    {
        // (1,0) and (0,1) are land: cannot go from (0,0) to (1,1) in a single diagonal step (it would clip the land at the corner).
        var g = OpenGrid();
        g.SetNavigable(1, 0, false);
        g.SetNavigable(0, 1, false);

        List<WorldPos> path = g.FindPath(new WorldPos(5, 0, 5), new WorldPos(15, 0, 15), 5f);
        Assert.Null(path); // Completely blocked by the 2x2 corner (the only way through would be a diagonal cut -> forbidden)
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
