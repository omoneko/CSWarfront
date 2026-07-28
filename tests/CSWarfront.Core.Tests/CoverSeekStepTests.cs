using CSWarfront.Core;
using Xunit;

public class CoverSeekStepTests
{
    private static WarState BaseState()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        LandUnitRoster.RegisterAll(s.Types);
        return s;
    }

    private static UnitInstance AddUnit(WarState s, uint id, UnitCategory category, byte faction, WorldPos pos)
    {
        string key = LandUnitRoster.TypeKey(category, 1);
        var type = s.Types.Get(key);
        var u = new UnitInstance(id, key, faction, type.MaxHP, pos);
        s.Units.Add(u);
        return u;
    }

    [Fact]
    public void Advance_sets_CoverDestination_for_engaging_infantry_when_cover_available()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.True(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_does_not_set_CoverDestination_when_not_engaging()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_clears_CoverDestination_when_unit_stops_engaging()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.CoverDestination = new WorldPos(10, 0, 10); // pretend it was previously set
        self.State = UnitState.Idle;
        self.TargetId = null;

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_clears_CoverDestination_when_target_died()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.CoverDestination = new WorldPos(10, 0, 10);
        enemy.State = UnitState.Dead;
        enemy.CurrentHP = 0f;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_never_sets_CoverDestination_for_artillery()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Artillery, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f); // plenty of cover nearby, but artillery shouldn't care

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_sets_CoverDestination_for_engaging_vehicle_within_its_smaller_radius()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Tank, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Tank, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(30, 0, 0), 6f); // well within Apc/Tank/AntiAir's 45 radius

        CoverSeekStep.Advance(s, 0.01f);

        Assert.True(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_leaves_CoverDestination_unset_when_no_cover_map_supplied()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        // s.Cover left null

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_does_not_reevaluate_before_cooldown_elapses()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);
        Assert.True(self.CoverDestination.HasValue);
        var firstDestination = self.CoverDestination.Value;

        // 遮蔽物を全て取り除いても、クールダウン中なら既存のCoverDestinationは変わらない。
        s.Cover = new CoverMap();
        CoverSeekStep.Advance(s, 0.01f); // still well within CoverReevaluateHours (0.5h)

        Assert.True(self.CoverDestination.HasValue);
        Assert.Equal(firstDestination.X, self.CoverDestination.Value.X, 3);
    }

    [Fact]
    public void Advance_reevaluates_after_cooldown_elapses()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);
        Assert.True(self.CoverDestination.HasValue);

        // 遮蔽マップを空にして、クールダウン(0.5h)を超える時間を進める。
        s.Cover = new CoverMap();
        CoverSeekStep.Advance(s, CoverSeekStep.CoverReevaluateHours + 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }
}
