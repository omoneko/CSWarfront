using CSWarfront.Core;
using Xunit;

public class CoverMapTests
{
    [Fact]
    public void TryFindBestCover_returns_false_when_map_empty()
    {
        var map = new CoverMap();
        bool found = map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), 60f, 1u, out WorldPos cover);
        Assert.False(found);
    }

    [Fact]
    public void TryFindBestCover_returns_false_when_nothing_within_search_radius()
    {
        var map = new CoverMap();
        map.Add(new WorldPos(1000, 0, 0), 8f); // far away
        bool found = map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), 60f, 1u, out WorldPos cover);
        Assert.False(found);
    }

    [Fact]
    public void TryFindBestCover_returns_false_when_search_radius_is_zero_or_negative()
    {
        var map = new CoverMap();
        map.Add(new WorldPos(10, 0, 0), 8f);
        Assert.False(map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), 0f, 1u, out _));
        Assert.False(map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), -5f, 1u, out _));
    }

    /// <summary>
    /// Unit at (0,0,0), threat at (100,0,0) (due east). A single cover object at (50,0,0) (exactly midway, due east).
    /// The returned standing position should be offset from the cover's centre in the "direction opposite the threat",
    /// i.e. (radius+StandoffMargin) to the west.
    /// </summary>
    [Fact]
    public void TryFindBestCover_places_standing_position_on_far_side_from_threat()
    {
        var map = new CoverMap();
        map.Add(new WorldPos(50, 0, 0), 8f);

        bool found = map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), 60f, 1u, out WorldPos cover);

        Assert.True(found);
        // As seen from the threat (100,0,0), it should be beyond the cover centre (50,0,0), i.e. on the smaller-X side.
        Assert.True(cover.X < 50f, $"expected standing position west of cover centre, got X={cover.X}");
        // Distance is radius(8) + StandoffMargin(4) = 12 -> X = 50 - 12 = 38
        Assert.Equal(38f, cover.X, 2);
        Assert.Equal(0f, cover.Z, 2);
    }

    [Fact]
    public void TryFindBestCover_prefers_cover_between_unit_and_threat_over_cover_behind_unit()
    {
        var map = new CoverMap();
        // Cover behind the unit (opposite direction from the threat) that provides no actual cover: close but a bad option
        map.Add(new WorldPos(-20, 0, 0), 5f);
        // Cover between the unit and the threat: farther but a good option
        map.Add(new WorldPos(50, 0, 0), 5f);

        bool found = map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), 60f, 1u, out WorldPos cover);

        Assert.True(found);
        // Should be offset (5+4)=9 to the west of the good option (50,0,0) -> X=41 (the behind-the-unit (-20,0,0) would yield a negative X)
        Assert.Equal(41f, cover.X, 1);
    }

    [Fact]
    public void TryFindBestCover_is_deterministic_for_the_same_seed()
    {
        var map = new CoverMap();
        map.Add(new WorldPos(10, 0, 10), 5f);
        map.Add(new WorldPos(15, 0, -10), 5f);
        map.Add(new WorldPos(30, 0, 5), 5f);

        map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), 60f, 777u, out WorldPos first);
        map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), 60f, 777u, out WorldPos second);
        map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), 60f, 777u, out WorldPos third);

        Assert.Equal(first.X, second.X, 4);
        Assert.Equal(first.Z, second.Z, 4);
        Assert.Equal(first.X, third.X, 4);
        Assert.Equal(first.Z, third.Z, 4);
    }

    /// <summary>When there are several roughly equal candidates, changing the seed should make different units
    /// pick different points so that everyone does not cluster at the same spot (same intent as
    /// Different_seeds_choose_different_routes in RoadGraphJitterTests).</summary>
    [Fact]
    public void TryFindBestCover_spreads_different_seeds_across_different_points_when_several_available()
    {
        var map = new CoverMap();
        // Set up several cover objects roughly equidistant from the unit and roughly symmetric relative to the threat.
        map.Add(new WorldPos(30, 0, 20), 5f);
        map.Add(new WorldPos(30, 0, -20), 5f);
        map.Add(new WorldPos(35, 0, 15), 5f);
        map.Add(new WorldPos(35, 0, -15), 5f);

        var chosen = new System.Collections.Generic.HashSet<string>();
        for (uint seed = 1; seed <= 30; seed++)
        {
            bool found = map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), 60f, seed, out WorldPos cover);
            Assert.True(found);
            // Using X alone, the Z-symmetric candidates (35,15) and (35,-15) could yield the same standing X, so distinguish by the X:Z pair.
            chosen.Add(cover.X.ToString("0.0") + ":" + cover.Z.ToString("0.0"));
        }

        Assert.True(chosen.Count > 1,
            "expected at least two different cover points to be chosen across 30 seeds, got only: " +
            string.Join(",", chosen));
    }

    [Fact]
    public void TryFindBestCover_ignores_cover_outside_search_radius_even_if_it_would_score_better()
    {
        var map = new CoverMap();
        // Perfectly midway but too far (outside the search radius).
        map.Add(new WorldPos(200, 0, 0), 5f);
        // Inside the search radius but off the midline; still the only valid candidate.
        map.Add(new WorldPos(20, 0, 30), 5f);

        bool found = map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(1000, 0, 0), 60f, 1u, out WorldPos cover);

        Assert.True(found);
        // If (200,0,0) had been chosen, cover.X would be around 200, so verify that is not the case.
        Assert.True(cover.X < 60f, $"expected the near candidate to win, got X={cover.X}");
    }

    [Fact]
    public void Count_reflects_number_of_added_points()
    {
        var map = new CoverMap();
        Assert.Equal(0, map.Count);
        map.Add(new WorldPos(1, 0, 1), 3f);
        map.Add(new WorldPos(2, 0, 2), 3f);
        Assert.Equal(2, map.Count);
    }
}
