using CSWarfront.Core;
using Xunit;

public class CombatZoneTrackerTests
{
    [Fact]
    public void ReportCombat_creates_a_new_zone_when_none_exists()
    {
        var tracker = new CombatZoneTracker();
        tracker.ReportCombat(new WorldPos(10, 0, 20));

        Assert.Single(tracker.Zones);
        Assert.Equal(10f, tracker.Zones[0].Center.X);
        Assert.Equal(20f, tracker.Zones[0].Center.Z);
        Assert.Equal(CombatZoneTracker.ZoneRadius, tracker.Zones[0].Radius);
        Assert.Equal(CombatZoneTracker.ZoneLingerHours, tracker.Zones[0].RemainingHours);
    }

    [Fact]
    public void ReportCombat_merges_nearby_reports_into_the_same_zone_instead_of_duplicating()
    {
        var tracker = new CombatZoneTracker();
        tracker.ReportCombat(new WorldPos(0, 0, 0));
        tracker.ReportCombat(new WorldPos(10, 0, 0)); // 十分近い（ZoneRadius=120以内）

        Assert.Single(tracker.Zones);
    }

    [Fact]
    public void ReportCombat_averages_the_centre_when_merging()
    {
        var tracker = new CombatZoneTracker();
        tracker.ReportCombat(new WorldPos(0, 0, 0));
        tracker.ReportCombat(new WorldPos(20, 0, 0));

        Assert.Single(tracker.Zones);
        Assert.Equal(10f, tracker.Zones[0].Center.X, 3);
    }

    [Fact]
    public void ReportCombat_extends_remaining_hours_back_to_full_linger_on_merge()
    {
        var tracker = new CombatZoneTracker();
        tracker.ReportCombat(new WorldPos(0, 0, 0));
        tracker.Advance(1f); // 2h -> 1h残り

        tracker.ReportCombat(new WorldPos(5, 0, 0)); // 近傍への再報告で延長されるはず
        Assert.Equal(CombatZoneTracker.ZoneLingerHours, tracker.Zones[0].RemainingHours);
    }

    [Fact]
    public void ReportCombat_creates_a_separate_zone_when_far_from_existing_ones()
    {
        var tracker = new CombatZoneTracker();
        tracker.ReportCombat(new WorldPos(0, 0, 0));
        tracker.ReportCombat(new WorldPos(10000, 0, 10000)); // 十分遠い

        Assert.Equal(2, tracker.Zones.Count);
    }

    [Fact]
    public void Advance_reduces_remaining_hours()
    {
        var tracker = new CombatZoneTracker();
        tracker.ReportCombat(new WorldPos(0, 0, 0));
        tracker.Advance(0.5f);

        Assert.Equal(CombatZoneTracker.ZoneLingerHours - 0.5f, tracker.Zones[0].RemainingHours, 4);
    }

    [Fact]
    public void Advance_removes_zones_once_remaining_hours_reaches_zero()
    {
        var tracker = new CombatZoneTracker();
        tracker.ReportCombat(new WorldPos(0, 0, 0));
        tracker.Advance(CombatZoneTracker.ZoneLingerHours); // ちょうど期限

        Assert.Empty(tracker.Zones);
    }

    [Fact]
    public void Advance_removes_only_expired_zones_and_keeps_others()
    {
        var tracker = new CombatZoneTracker();
        tracker.ReportCombat(new WorldPos(0, 0, 0));
        tracker.Advance(CombatZoneTracker.ZoneLingerHours - 0.1f);
        tracker.ReportCombat(new WorldPos(10000, 0, 0)); // 新しいゾーン（フル残り時間）

        tracker.Advance(0.2f); // 最初のゾーンだけ失効するはず

        Assert.Single(tracker.Zones);
        Assert.Equal(10000f, tracker.Zones[0].Center.X);
    }

    [Fact]
    public void MaxZones_is_respected_by_dropping_the_zone_closest_to_expiry()
    {
        var tracker = new CombatZoneTracker();
        for (int i = 0; i < CombatZoneTracker.MaxZones; i++)
        {
            tracker.ReportCombat(new WorldPos(i * 10000, 0, 0)); // 全て互いに遠い
        }
        Assert.Equal(CombatZoneTracker.MaxZones, tracker.Zones.Count);

        // 最初のゾーンだけ残り時間を減らし、「最も期限が近い」ものにする。
        // Advanceは全ゾーンへ一律dtを適用するため、代わりに追加報告で個別に延長して差をつける。
        for (int i = 1; i < CombatZoneTracker.MaxZones; i++)
        {
            tracker.ReportCombat(new WorldPos(i * 10000, 0, 0)); // フルへ延長し直す
        }
        tracker.Advance(0.1f); // 全ゾーンへ一律適用（相対順序は変わらない）

        // 新しいゾーンを追加すると、上限に達しているため最も残り時間が少ない(index 0)が落ちるはず。
        tracker.ReportCombat(new WorldPos(999999, 0, 0));

        Assert.Equal(CombatZoneTracker.MaxZones, tracker.Zones.Count);
        for (int i = 0; i < tracker.Zones.Count; i++)
        {
            Assert.NotEqual(0f, tracker.Zones[i].Center.X);
        }
    }

    [Fact]
    public void ReportCombat_is_deterministic_for_the_same_sequence_of_reports()
    {
        var trackerA = new CombatZoneTracker();
        var trackerB = new CombatZoneTracker();

        WorldPos[] reports =
        {
            new WorldPos(0, 0, 0),
            new WorldPos(15, 0, 5),
            new WorldPos(500, 0, 500),
            new WorldPos(20, 0, -10),
        };

        foreach (var p in reports) trackerA.ReportCombat(p);
        foreach (var p in reports) trackerB.ReportCombat(p);

        Assert.Equal(trackerA.Zones.Count, trackerB.Zones.Count);
        for (int i = 0; i < trackerA.Zones.Count; i++)
        {
            Assert.Equal(trackerA.Zones[i].Center.X, trackerB.Zones[i].Center.X, 4);
            Assert.Equal(trackerA.Zones[i].Center.Z, trackerB.Zones[i].Center.Z, 4);
        }
    }
}
