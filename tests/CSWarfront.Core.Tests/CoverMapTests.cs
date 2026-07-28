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
    /// ユニットは(0,0,0)、脅威は(100,0,0)（真東）。遮蔽物は(50,0,0)（ちょうど中間、真東）に1つだけ。
    /// 返る立ち位置は遮蔽物の中心から「脅威と反対方向」＝西側に(radius+StandoffMargin)ぶん離れた点になるはず。
    /// </summary>
    [Fact]
    public void TryFindBestCover_places_standing_position_on_far_side_from_threat()
    {
        var map = new CoverMap();
        map.Add(new WorldPos(50, 0, 0), 8f);

        bool found = map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), 60f, 1u, out WorldPos cover);

        Assert.True(found);
        // 脅威(100,0,0)から見て遮蔽物中心(50,0,0)より奥＝Xがより小さい側にあるはず。
        Assert.True(cover.X < 50f, $"expected standing position west of cover centre, got X={cover.X}");
        // 遠さは radius(8) + StandoffMargin(4) = 12 → X = 50 - 12 = 38
        Assert.Equal(38f, cover.X, 2);
        Assert.Equal(0f, cover.Z, 2);
    }

    [Fact]
    public void TryFindBestCover_prefers_cover_between_unit_and_threat_over_cover_behind_unit()
    {
        var map = new CoverMap();
        // ユニットの背後（脅威と逆方向）にあり、遮蔽としては機能しない遮蔽物: 近いが悪い選択肢
        map.Add(new WorldPos(-20, 0, 0), 5f);
        // ユニットと脅威の間にある遮蔽物: 遠いが良い選択肢
        map.Add(new WorldPos(50, 0, 0), 5f);

        bool found = map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), 60f, 1u, out WorldPos cover);

        Assert.True(found);
        // 良い選択肢(50,0,0)から西へ (5+4)=9 ずれた位置になるはず -> X=41（背後の(-20,0,0)なら負のXになる）
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

    /// <summary>複数のほぼ同格な候補があるとき、seedを変えると異なるユニットが異なる点を選び、
    /// 全員が同一点へ密集しないこと（RoadGraphJitterTestsのDifferent_seeds_choose_different_routesと同じ意図）。</summary>
    [Fact]
    public void TryFindBestCover_spreads_different_seeds_across_different_points_when_several_available()
    {
        var map = new CoverMap();
        // ユニットからほぼ等距離・脅威との位置関係もほぼ対称な複数の遮蔽物を用意する。
        map.Add(new WorldPos(30, 0, 20), 5f);
        map.Add(new WorldPos(30, 0, -20), 5f);
        map.Add(new WorldPos(35, 0, 15), 5f);
        map.Add(new WorldPos(35, 0, -15), 5f);

        var chosen = new System.Collections.Generic.HashSet<string>();
        for (uint seed = 1; seed <= 30; seed++)
        {
            bool found = map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(100, 0, 0), 60f, seed, out WorldPos cover);
            Assert.True(found);
            // XだけだとZ対称な2候補(35,15)と(35,-15)が同じXの立ち位置になり得るため、X:Zの組で判別する。
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
        // 完璧に中間にあるが遠すぎる（探索半径外）。
        map.Add(new WorldPos(200, 0, 0), 5f);
        // 探索半径内だが中間からは外れている、それでも唯一の有効候補。
        map.Add(new WorldPos(20, 0, 30), 5f);

        bool found = map.TryFindBestCover(new WorldPos(0, 0, 0), new WorldPos(1000, 0, 0), 60f, 1u, out WorldPos cover);

        Assert.True(found);
        // (200,0,0)が選ばれていたらcover.Xは200付近になるはずなので、そうならないことを確認する。
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
