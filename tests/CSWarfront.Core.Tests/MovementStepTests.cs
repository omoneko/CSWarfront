using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class MovementStepTests
{
    private static WarState OneMovingUnit()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(1000, 0, 0);
        s.Units.Add(u);
        return s;
    }

    [Fact]
    public void Advance_moves_unit_toward_target()
    {
        var s = OneMovingUnit();
        MovementStep.Advance(s, 1f);
        // Speed 250 map units / in-game hour, dt=1h, distance 1000 -> 250 の部分移動
        Assert.Equal(250f, s.Units[0].Position.X, 1);
        Assert.Equal(0f, s.Units[0].Position.Z, 1);
    }

    [Fact]
    public void Advance_stops_at_target_without_overshoot()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(5, 0, 0);
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(5f, s.Units[0].Position.X, 1);
        Assert.Equal(0f, s.Units[0].Position.Z, 1);
    }

    [Fact]
    public void Advance_does_not_move_idle_unit()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Idle;
        u.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 1);
    }

    [Fact]
    public void Advance_does_not_move_when_no_target()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 1);
    }

    [Fact]
    public void Advance_preserves_y_coordinate()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 42, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(42f, s.Units[0].Position.Y, 1);
    }

    private static WarState UnitWithPath()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1()); // Speed 250 map units / in-game hour
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(100, 0, 100);
        u.Path = new List<WorldPos> { new WorldPos(100, 0, 0), new WorldPos(100, 0, 100) };
        u.PathIndex = 0;
        u.PathTarget = u.OrderTargetPos;
        s.Units.Add(u);
        return s;
    }

    [Fact]
    public void Advance_small_step_moves_toward_first_waypoint_only()
    {
        var s = UnitWithPath();
        // dt small enough that stepLen (250*dt) doesn't reach waypoint 1 (100 away)
        MovementStep.Advance(s, 0.1f); // stepLen = 25
        var u = s.Units[0];
        Assert.Equal(25f, u.Position.X, 1);
        Assert.Equal(0f, u.Position.Z, 1);
        Assert.Equal(0, u.PathIndex); // still heading to waypoint 0
    }

    [Fact]
    public void Advance_large_step_crosses_first_waypoint_and_continues_toward_second()
    {
        var s = UnitWithPath();
        // stepLen = 250*0.6 = 150 : covers 100 to first waypoint, then 50 more toward second
        MovementStep.Advance(s, 0.6f);
        var u = s.Units[0];
        Assert.Equal(100f, u.Position.X, 1);
        Assert.Equal(50f, u.Position.Z, 1);
        Assert.Equal(1, u.PathIndex); // now heading to waypoint 1 (index 1)
    }

    [Fact]
    public void Advance_falls_back_to_straight_line_when_path_exhausted()
    {
        var s = UnitWithPath();
        var u = s.Units[0];
        u.PathIndex = 2; // path exhausted (Path.Count == 2)
        u.Position = new WorldPos(100, 0, 100); // arrived at last waypoint already
        u.OrderTargetPos = new WorldPos(200, 0, 100);

        MovementStep.Advance(s, 1f); // stepLen = 250, plenty to reach 100 away

        Assert.Equal(200f, u.Position.X, 1);
        Assert.Equal(100f, u.Position.Z, 1);
    }

    [Fact]
    public void Advance_with_null_path_uses_straight_line_fallback()
    {
        var s = OneMovingUnit();
        s.Units[0].Path = null;
        MovementStep.Advance(s, 1f);
        Assert.Equal(250f, s.Units[0].Position.X, 1);
    }

    [Fact]
    public void Advance_preserves_y_while_following_path()
    {
        var s = UnitWithPath();
        s.Units[0].Position = new WorldPos(0, 42, 0);
        MovementStep.Advance(s, 0.6f);
        Assert.Equal(42f, s.Units[0].Position.Y, 1);
    }
}
