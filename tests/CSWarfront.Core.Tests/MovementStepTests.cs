using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class MovementStepTests
{
    // Task53: fake implementation of state.Height (IHeightSampler). It returns the simple deterministic
    // function x+z, so "Position.Y after movement always equals the result of TrySampleHeight(X, Z)"
    // can be verified with assertions alone (the value normally matches neither the waypoint Y nor the
    // straight-line Y, so this simultaneously detects the legacy Y-interpolation path being used by
    // mistake).
    private class FakeHeightSampler : IHeightSampler
    {
        public bool TrySampleHeight(float x, float z, out float height)
        {
            height = x + z;
            return true;
        }
    }

    // Task53 hardening: a fake whose TrySampleHeight always returns false (simulating a TerrainManager
    // outage/exception). It deliberately writes a value "far away from the ground surface" (-9999f) to
    // the out parameter. If MovementStep overlooked the false return value and adopted this height as-is,
    // the test assertions would catch it immediately; this is the trap that reproduces and detects the
    // same class of "failure value gets adopted as-is" bug where the real production
    // SurfaceHeightSampler returned 0f on failure.
    private class FailingHeightSampler : IHeightSampler
    {
        public bool TrySampleHeight(float x, float z, out float height)
        {
            height = -9999f;
            return false;
        }
    }

    // Task55: regression prevention (defense in depth) for the "units start dogfighting in midair" bug.
    // Even if SurfaceHeightSampler again returns an absurd height in the future (via a wrong overload,
    // etc.), MovementStep must not take it at face value and launch the unit into the sky.
    // TrySampleHeight itself returns success (true), but if the value deviates from the interpolated Y
    // (the result of the conventional waypoint/straight-line interpolation) by more than
    // MaxSurfaceDeviation, it is rejected and the interpolated Y is used instead. offset always acts as
    // the absolute deviation from the interpolated Y (because the interpolated Y is 0 in every case
    // used by these tests).
    private class OffsetHeightSampler : IHeightSampler
    {
        private readonly float _offset;
        public OffsetHeightSampler(float offset) { _offset = offset; }

        public bool TrySampleHeight(float x, float z, out float height)
        {
            height = _offset;
            return true;
        }
    }


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

    // Tank_T1's effective speed (calibrated to a km/h basis in Task26: 40km/h ≈ 5.418 map units per
    // in-game hour. Since Task83 applies GlobalSpeedMultiplier (1.25) uniformly to movement, this
    // constant represents the multiplier-inclusive "distance actually traveled per in-game hour".
    // All distance/time expectations are derived from it.)
    private const float TankSpeedPerHour = 5.418f * MovementStep.GlobalSpeedMultiplier;

    [Fact]
    public void Advance_moves_unit_toward_target()
    {
        var s = OneMovingUnit();
        MovementStep.Advance(s, 1f);
        // dt=1h, distance 1000 -> partial move of TankSpeedPerHour (effective speed, multiplier included)
        Assert.Equal(TankSpeedPerHour, s.Units[0].Position.X, 2);
        Assert.Equal(0f, s.Units[0].Position.Z, 1);
    }

    [Fact]
    public void GlobalSpeedMultiplier_is_1_25()
    {
        // Task83 (user request "1.25x the current overall speed"): a global multiplier applied at the
        // single consumption point of movement (the stepLen computation). The value itself is the spec,
        // so it is pinned as a constant.
        Assert.Equal(1.25f, MovementStep.GlobalSpeedMultiplier, 3);
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

    // Task37: the old spec was "Y is always preserved while moving" (test name
    // Advance_preserves_y_coordinate, expected value stayed at the starting Y of 42). That was the cause
    // of the "floating above the road surface" bug where units flew horizontally, ignoring road grades,
    // so the new spec interpolates Y toward the target using the same interpolation factor as X/Z.
    // The following verifies that new spec
    // (start Y=42, target Y=0, dist=100, stepLen≈TankSpeedPerHour(≈5.418) -> t≈0.05418,
    //  Y = 42 + (0-42)*t ≈ 39.72).
    [Fact]
    public void Advance_interpolates_y_toward_target_in_straight_line_fallback()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 42, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(42f - 42f * (TankSpeedPerHour / 100f), s.Units[0].Position.Y, 1);
    }

    // Task37: verify that even in the straight-line fallback (no Path / Path exhausted), Y converges
    // fully to the target's Y on arrival (no overshoot, and exactly the target's Y with no rounding
    // error).
    [Fact]
    public void Advance_converges_exactly_to_target_y_on_arrival_in_straight_line_fallback()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(5, 20, 0); // dist=5 << stepLen(≈5.418) -> arrives in a single step
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(5f, s.Units[0].Position.X, 1);
        Assert.Equal(20f, s.Units[0].Position.Y, 4); // converges exactly to the target's Y (snap)
        Assert.Equal(0f, s.Units[0].Position.Z, 1);
    }

    private static WarState UnitWithPath()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1()); // Speed ≈5.418 map units / in-game hour (after Task26 calibration, see TankSpeedPerHour)
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
        // dt small enough that stepLen (TankSpeedPerHour*dt ≈ 5.418*0.1 ≈ 0.542) doesn't reach waypoint 1 (100 away)
        MovementStep.Advance(s, 0.1f); // stepLen ≈ 0.542
        var u = s.Units[0];
        Assert.Equal(TankSpeedPerHour * 0.1f, u.Position.X, 2);
        Assert.Equal(0f, u.Position.Z, 1);
        Assert.Equal(0, u.PathIndex); // still heading to waypoint 0
    }

    [Fact]
    public void Advance_large_step_crosses_first_waypoint_and_continues_toward_second()
    {
        var s = UnitWithPath();
        // dt chosen so that stepLen = TankSpeedPerHour*dt = 150 exactly (150/5.418... ≈ 27.6855469h):
        // covers 100 to first waypoint, then 50 more toward second (same geometry, chosen so the result matches the old test's Speed=250, dt=0.6)
        MovementStep.Advance(s, 150f / TankSpeedPerHour);
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

        // stepLen = TankSpeedPerHour*20 ≈ 108.4, plenty to reach the target 100 away (clamped at the target without overshoot)
        MovementStep.Advance(s, 20f);

        Assert.Equal(200f, u.Position.X, 1);
        Assert.Equal(100f, u.Position.Z, 1);
    }

    [Fact]
    public void Advance_with_null_path_uses_straight_line_fallback()
    {
        var s = OneMovingUnit();
        s.Units[0].Path = null;
        MovementStep.Advance(s, 1f);
        Assert.Equal(TankSpeedPerHour, s.Units[0].Position.X, 2);
    }

    // Task37: the old spec was "Y is always preserved while moving along a Path too" (test name
    // Advance_preserves_y_while_following_path, expected value stayed at the starting Y of 42). That was
    // the cause of the "floating above the road surface" bug where units flew horizontally, ignoring
    // road grades (bridges/slopes), so the new spec interpolates Y with the same interpolation factor as
    // X/Z while heading toward a waypoint too. The waypoints of UnitWithPath() have Y=0, so Y should be
    // interpolated from 42 toward 0 (dist=100, stepLen≈3.2508 -> t≈0.032508, Y=42+(0-42)*t≈40.63).
    [Fact]
    public void Advance_interpolates_y_toward_waypoint_while_following_path()
    {
        var s = UnitWithPath();
        s.Units[0].Position = new WorldPos(0, 42, 0);
        MovementStep.Advance(s, 0.6f);
        Assert.Equal(42f - 42f * (TankSpeedPerHour * 0.6f / 100f), s.Units[0].Position.Y, 1);
    }

    // Task37: at the moment a waypoint is reached, Y must snap exactly to that waypoint's Y with no
    // rounding error (guaranteeing the unit sits "exactly on top" at the top of a slope / on a bridge).
    [Fact]
    public void Advance_snaps_exactly_to_waypoint_y_on_arrival()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(10, 7, 0);
        u.Path = new List<WorldPos> { new WorldPos(10, 7, 0) }; // top of a slope, Y=7
        u.PathIndex = 0;
        u.PathTarget = u.OrderTargetPos;
        s.Units.Add(u);

        // stepLen = TankSpeedPerHour*2 ≈ 10.84 >= dist(10) -> arrives in one step and snaps to the waypoint's Y
        MovementStep.Advance(s, 2f);

        Assert.Equal(10f, s.Units[0].Position.X, 1);
        Assert.Equal(7f, s.Units[0].Position.Y, 4); // snaps exactly to the waypoint's Y
        Assert.Equal(1, s.Units[0].PathIndex);
    }

    // Task37: even with a large step that crosses multiple waypoints, Y must be interpolated correctly
    // toward the next waypoint based on the last waypoint reached (imagine heights rising 0->10->20 as
    // if crossing a bridge).
    [Fact]
    public void Advance_large_step_crosses_waypoint_and_interpolates_y_from_last_reached_waypoint()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(100, 20, 100);
        u.Path = new List<WorldPos> { new WorldPos(100, 10, 0), new WorldPos(100, 20, 100) };
        u.PathIndex = 0;
        u.PathTarget = u.OrderTargetPos;
        s.Units.Add(u);

        // stepLen≈150 (same dt as Advance_large_step_crosses_first_waypoint_and_continues_toward_second):
        // travels 100 to reach the first waypoint (snaps to Y=10), then advances the remaining 50 toward
        // the second (dist 100) (t=0.5) -> Y = 10 + (20-10)*0.5 = 15.
        MovementStep.Advance(s, 150f / TankSpeedPerHour);
        var pos = s.Units[0].Position;

        Assert.Equal(100f, pos.X, 1);
        Assert.Equal(50f, pos.Z, 1);
        Assert.Equal(15f, pos.Y, 1);
        Assert.Equal(1, s.Units[0].PathIndex);
    }

    // --- Task44: CoverDestination-priority movement ---

    [Fact]
    public void Advance_moves_engaging_unit_toward_CoverDestination_instead_of_OrderTargetPos()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Engaging;
        u.OrderTargetPos = new WorldPos(1000, 0, 0); // advance objective (should be ignored)
        u.CoverDestination = new WorldPos(0, 0, 1000); // cover position (north-south direction, should move toward this)
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        // Confirm it moved toward CoverDestination (Z direction), not OrderTargetPos (X direction).
        Assert.Equal(0f, s.Units[0].Position.X, 1);
        Assert.True(s.Units[0].Position.Z > 0f, "expected unit to move toward CoverDestination (positive Z)");
    }

    [Fact]
    public void Advance_stops_within_CoverArrivalDistance_of_CoverDestination()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Engaging;
        u.CoverDestination = new WorldPos(1f, 0, 0); // closer than the search radius (arrival distance=3)

        s.Units.Add(u);

        MovementStep.Advance(s, 1f); // step length is large enough (TankSpeedPerHour≈5.4)

        // Already within CoverArrivalDistance(3), so it does not move.
        Assert.Equal(0f, s.Units[0].Position.X, 3);
    }

    // Task44's old spec (test name Advance_does_not_use_CoverDestination_when_not_engaging) was
    // "ignore CoverDestination unless State==Engaging". Since Task45 made CoverSeekStep also set
    // CoverDestination on units that are advancing (pre-engagement, outside their own faction's
    // territory), MovementStep was changed to always honor CoverDestination whenever it is set,
    // regardless of State (Engaging/Moving). The following verifies that new spec (even with
    // State=Moving, the unit heads toward CoverDestination and OrderTargetPos is ignored = movement
    // during a bounding advance).
    [Fact]
    public void Advance_uses_CoverDestination_even_when_not_engaging_since_bounding_advance_sets_it_while_moving()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving; // not Engaging, but may carry a CoverDestination during a bounding advance
        u.OrderTargetPos = new WorldPos(1000, 0, 0); // should be ignored
        u.CoverDestination = new WorldPos(0, 0, 1000); // should move toward this
        u.CoverHold = false;

        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        // Confirm it moved toward CoverDestination (Z direction), not OrderTargetPos (X direction).
        Assert.Equal(0f, s.Units[0].Position.X, 1);
        Assert.True(s.Units[0].Position.Z > 0f, "expected unit to move toward CoverDestination even while not Engaging");
    }

    // Task45: on a bounding advance (CoverHold==false), when the unit gets within CoverArrivalDistance,
    // instead of staying put it clears CoverDestination and also resets CoverReevaluateCooldown to 0 so
    // the next CoverSeekStep evaluation can immediately pick the next cover (realizing the
    // cover-to-cover "leapfrog").
    [Fact]
    public void Advance_clears_CoverDestination_and_resets_cooldown_on_arrival_when_not_holding()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.CoverDestination = new WorldPos(1f, 0, 0); // closer than CoverArrivalDistance(3)
        u.CoverHold = false;
        u.CoverReevaluateCooldown = 0.3f; // pretend the cooldown is still running

        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.False(s.Units[0].CoverDestination.HasValue);
        Assert.Equal(0f, s.Units[0].CoverReevaluateCooldown);
    }

    // Task45: in contrast, with CoverHold==true (in combat), the unit keeps CoverDestination even
    // within CoverArrivalDistance and stays put (the pre-existing Task44 behavior).
    [Fact]
    public void Advance_keeps_CoverDestination_on_arrival_when_holding()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Engaging;
        u.CoverDestination = new WorldPos(1f, 0, 0);
        u.CoverHold = true;

        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.True(s.Units[0].CoverDestination.HasValue);
        Assert.Equal(0f, s.Units[0].Position.X, 3); // has not moved
    }

    [Fact]
    public void Advance_resumes_path_following_once_CoverDestination_is_cleared()
    {
        var s = OneMovingUnit();
        var u = s.Units[0];
        u.State = UnitState.Engaging;
        u.CoverDestination = new WorldPos(0, 0, 1000);
        MovementStep.Advance(s, 0.1f);
        Assert.True(s.Units[0].Position.Z > 0f); // moved toward the cover position

        // Assume the engagement ended, CoverDestination was cleared, and normal movement resumed.
        u.CoverDestination = null;
        u.State = UnitState.Moving;
        var beforeX = s.Units[0].Position.X;
        MovementStep.Advance(s, 0.1f);

        Assert.True(s.Units[0].Position.X > beforeX, "expected unit to resume advancing toward OrderTargetPos");
    }

    // --- Task48: Order (Hold/RallyHold) ---

    [Fact]
    public void Advance_never_moves_unit_with_Hold_order_even_if_State_and_OrderTargetPos_are_set()
    {
        var s = OneMovingUnit();
        s.Units[0].Order = UnitOrder.Hold;

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 3);
        Assert.Equal(0f, s.Units[0].Position.Z, 3);
    }

    [Fact]
    public void Advance_never_moves_unit_with_Hold_order_even_with_stray_CoverDestination()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.Hold;
        u.CoverDestination = new WorldPos(0, 0, 1000); // stale/inconsistent, should still be ignored
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 3);
        Assert.Equal(0f, s.Units[0].Position.Z, 3);
    }

    [Fact]
    public void Advance_moves_RallyHold_unit_toward_RallyPoint_via_straight_line_when_no_path()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(1000, 0, 0);
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(TankSpeedPerHour, s.Units[0].Position.X, 2);
        Assert.Equal(0f, s.Units[0].Position.Z, 1);
    }

    [Fact]
    public void Advance_RallyHold_unit_stops_within_CoverArrivalDistance_of_RallyPoint()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(1f, 0, 0); // < CoverArrivalDistance(3)

        s.Units.Add(u);

        MovementStep.Advance(s, 1f); // stepLen (~5.4) easily covers the remaining distance

        Assert.Equal(0f, s.Units[0].Position.X, 3); // already within arrival distance, does not move
    }

    [Fact]
    public void Advance_RallyHold_unit_consumes_Path_toward_RallyPoint_before_falling_back_to_straight_line()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(100, 0, 100);
        u.Path = new List<WorldPos> { new WorldPos(100, 0, 0), new WorldPos(100, 0, 100) };
        u.PathIndex = 0;
        u.PathTarget = u.RallyPoint;
        s.Units.Add(u);

        // Same geometry as Advance_large_step_crosses_first_waypoint_and_continues_toward_second:
        // clears the first waypoint (100 away) then advances 50 more toward the second.
        MovementStep.Advance(s, 150f / TankSpeedPerHour);

        var pos = s.Units[0].Position;
        Assert.Equal(100f, pos.X, 1);
        Assert.Equal(50f, pos.Z, 1);
        Assert.Equal(1, u.PathIndex);
    }

    [Fact]
    public void Advance_RallyHold_unit_with_no_RallyPoint_does_not_move()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold; // RallyPoint intentionally left null
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 3);
    }

    // --- Task50: "stop the vehicle when fighting from behind building cover" ---

    // An engaging unit without a cover position (CoverDestination) does not move at all, even if
    // OrderTargetPos/Path remain (it stays stopped across multiple ticks). This assumes a normal
    // AiControlled/FreeAdvance unit that is not RallyHold (RallyHold is a separate spec that returns
    // fire while moving, see the test below).
    [Fact]
    public void Advance_engaging_unit_without_CoverDestination_never_moves_toward_OrderTargetPos()
    {
        var s = OneMovingUnit();
        var u = s.Units[0];
        u.State = UnitState.Engaging; // no CoverDestination assigned (CoverSeekStep found none, e.g.)

        MovementStep.Advance(s, 1f);
        Assert.Equal(0f, u.Position.X, 3);

        MovementStep.Advance(s, 10f); // several more ticks, still nothing to move toward
        Assert.Equal(0f, u.Position.X, 3);
    }

    // RallyHold + Engaging is the exception: per Task48's intentional spec of "only return fire at
    // enemies within range, whether moving or stopped", the unit keeps returning fire while heading to
    // its post (RallyPoint) (unchanged by Task50).
    [Fact]
    public void Advance_RallyHold_unit_still_advances_toward_RallyPoint_while_engaging()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(1000, 0, 0);
        u.State = UnitState.Engaging; // fighting off something along the way, no CoverDestination
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(TankSpeedPerHour, s.Units[0].Position.X, 2);
    }

    // The non-engaging (advancing) bounding advance still works as before (regression check that Task50
    // only changed mode 2). CoverHold==false (compatibility fallback path: the current CoverSeekStep no
    // longer produces this value, but the behavior is kept for callers that set it manually, Task52).
    [Fact]
    public void Advance_non_engaging_unit_still_bounds_from_cover_to_cover()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.CoverDestination = new WorldPos(1f, 0, 0); // within CoverArrivalDistance(3)
        u.CoverHold = false; // bounding advance, not holding
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.False(s.Units[0].CoverDestination.HasValue); // cleared on arrival, ready for the next cover
        Assert.Equal(0f, s.Units[0].CoverReevaluateCooldown);
    }

    // --- Task52: time cap on holding cover (CoverHold==true) (MaxCoverHoldHours) ---

    // rule2 TDD: with Task52, mode 3 (bounding advance while advancing) now also "holds" with
    // CoverHold=true (to actually depict hiding and pausing). If the unit keeps sitting still beyond
    // MaxCoverHoldHours, it releases CoverDestination and resumes the normal advance toward
    // OrderTargetPos (it never stays stopped indefinitely).
    [Fact]
    public void Advance_releases_bounding_CoverHold_after_MaxCoverHoldHours_and_resumes_toward_OrderTargetPos()
    {
        var s = OneMovingUnit(); // State=Moving, OrderTargetPos=(1000,0,0)
        var u = s.Units[0];
        u.CoverDestination = new WorldPos(1f, 0, 0); // within CoverArrivalDistance(3)
        u.CoverHold = true; // Task52: bounding cover now also holds briefly

        // Arrival tick: within MaxCoverHoldHours(1h), the hold timer only just started.
        MovementStep.Advance(s, 0.5f);
        Assert.True(s.Units[0].CoverDestination.HasValue);
        Assert.Equal(0f, s.Units[0].Position.X, 3);

        // Exceed the cap while still sitting at the cover point.
        MovementStep.Advance(s, MovementStep.MaxCoverHoldHours + 0.1f);
        Assert.False(s.Units[0].CoverDestination.HasValue);

        // Now free to resume toward OrderTargetPos.
        var beforeX = s.Units[0].Position.X;
        MovementStep.Advance(s, 1f);
        Assert.True(s.Units[0].Position.X > beforeX,
            "expected the unit to resume advancing once MaxCoverHoldHours elapsed");
    }

    // --- Task53/Task77: where Y comes from when state.Height is supplied ---

    // Task77 (fix for the "ground units won't cross bridges" bug): while on a path (following
    // waypoints), the terrain sampler is never consulted; the waypoint's own Y (the road-network
    // node's Y = the deck height in the case of a bridge) is adopted as-is. The old spec (Task53-76)
    // overwrote this with the result of TrySampleHeight even here, so when the height directly under
    // the bridge (water surface/riverbed) returned a "plausible-looking" value like FakeHeightSampler
    // does, units crossing the bridge appeared to sink underwater. The deviation (3) is deliberately
    // kept within MaxSurfaceDeviation(15), so this verifies the spec change itself — that the sampler
    // is not consulted at all while on a path — rather than the value merely being rejected by the
    // deviation clamp.
    [Fact]
    public void Advance_uses_waypoint_y_not_sampled_height_on_arrival_while_following_path()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(10, 7, 0);
        u.Path = new List<WorldPos> { new WorldPos(10, 7, 0) }; // the waypoint's own Y=7 (imagine the bridge deck height)
        u.PathIndex = 0;
        u.PathTarget = u.OrderTargetPos;
        s.Units.Add(u);
        s.Height = new FakeHeightSampler(); // TrySampleHeight(10, 0) -> true, 10 (imagine the water surface/terrain under the bridge)

        // stepLen = TankSpeedPerHour*2 ≈ 10.84 >= dist(10) -> arrives in one step (reaches the waypoint)
        MovementStep.Advance(s, 2f);

        Assert.Equal(10f, s.Units[0].Position.X, 1);
        // Task77: on a path, the sampler's value (10) is ignored and Y snaps exactly to the waypoint's own Y (7).
        Assert.Equal(7f, s.Units[0].Position.Y, 3);
    }

    // Task77: the sampler is likewise ignored during a partial move toward a waypoint, and the
    // interpolation toward the waypoint's Y is preserved (a regression test confirming that adding a
    // HeightSampler to the same geometry/expected values as
    // Advance_interpolates_y_toward_waypoint_while_following_path does not change the result).
    [Fact]
    public void Advance_interpolates_toward_waypoint_y_ignoring_HeightSampler_during_partial_path_move()
    {
        var s = UnitWithPath();
        s.Units[0].Position = new WorldPos(0, 42, 0);
        s.Height = new FakeHeightSampler(); // TrySampleHeight(x,z) = x+z, should be ignored while on a path

        MovementStep.Advance(s, 0.6f);

        Assert.Equal(42f - 42f * (TankSpeedPerHour * 0.6f / 100f), s.Units[0].Position.Y, 1);
    }

    // Task77 (integration-style regression test for the "ground units won't cross bridges" bug): with a
    // Path modeling a bridge (Y=0 shore -> Y=20 bridge crest -> Y=0 far shore, and FakeHeightSampler
    // always returning low values directly beneath the bridge (x+z, near the shore's Y=0)), confirm
    // that Y rises and falls exactly per the waypoint heights until the bridge is fully crossed, never
    // once dropping to the sampler's value.
    [Fact]
    public void Advance_crosses_a_bridge_path_following_waypoint_heights_without_dropping_to_sampled_terrain()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(200, 0, 0);
        u.Path = new List<WorldPos>
        {
            new WorldPos(100, 20, 0), // bridge crest
            new WorldPos(200, 0, 0),  // far shore
        };
        u.PathIndex = 0;
        u.PathTarget = u.OrderTargetPos;
        s.Units.Add(u);
        s.Height = new FakeHeightSampler(); // TrySampleHeight(x,z) = x+z (imagine the terrain/water surface under the bridge)

        // Step length that reaches the bridge crest exactly.
        MovementStep.Advance(s, 100f / TankSpeedPerHour);
        Assert.Equal(100f, s.Units[0].Position.X, 1);
        Assert.Equal(20f, s.Units[0].Position.Y, 2); // the deck's Y (20), not the sampler's (100)

        // Continue to the far shore.
        MovementStep.Advance(s, 100f / TankSpeedPerHour);
        Assert.Equal(200f, s.Units[0].Position.X, 1);
        Assert.Equal(0f, s.Units[0].Position.Y, 2); // the far shore's Y (0), not the sampler's (200)
    }

    [Fact]
    public void Advance_uses_sampled_height_during_partial_straight_line_move_when_HeightSampler_supplied()
    {
        var s = OneMovingUnit(); // start (0,0,0) -> target (1000,0,0); Y would always be 0 on a straight-line move (old spec)
        s.Height = new FakeHeightSampler();

        MovementStep.Advance(s, 1f); // stepLen ≈ TankSpeedPerHour(5.418), a partial move that does not yet reach the target

        var pos = s.Units[0].Position;
        Assert.True(pos.X > 0f, "expected partial movement toward the target");
        // Under the legacy interpolation Y would stay 0, but TrySampleHeight(X, Z) = X + Z is adopted.
        // Asserted as a relation, not a fixed landing spot: this test is about the height seam, and the
        // exact step length now also depends on terrain (Task125 slope/off-road factors).
        Assert.Equal(pos.X + pos.Z, pos.Y, 3);
    }

    [Fact]
    public void Advance_uses_sampled_height_during_partial_cover_move_when_HeightSampler_supplied()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Engaging;
        u.CoverDestination = new WorldPos(0, 0, 1000); // north-south direction, makes a partial move along Z
        s.Units.Add(u);
        s.Height = new FakeHeightSampler();

        MovementStep.Advance(s, 1f); // partial move that does not yet reach CoverArrivalDistance

        var pos = s.Units[0].Position;
        Assert.True(pos.Z > 0f, "expected partial movement toward CoverDestination");
        // TrySampleHeight(X, Z) = X + Z is adopted (under the legacy interpolation Y would stay 0).
        // Relational for the same reason as the straight-line case above (Task125).
        Assert.Equal(pos.X + pos.Z, pos.Y, 3);
    }

    [Fact]
    public void Advance_preserves_old_y_interpolation_when_HeightSampler_is_null()
    {
        // Regression check: when state.Height is never set (null by default), explicitly confirm that
        // Task37's conventional Y interpolation is preserved as-is (a premise most other tests
        // implicitly rely on).
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 42, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(u);
        Assert.Null(s.Height);

        MovementStep.Advance(s, 1f);

        Assert.Equal(42f - 42f * (TankSpeedPerHour / 100f), s.Units[0].Position.Y, 1); // same expected value as Advance_interpolates_y_toward_target_in_straight_line_fallback
    }

    // Task53 hardening: when TrySampleHeight returns false (simulating a TerrainManager
    // outage/exception), MovementStep must never adopt the out parameter's value (which depending on
    // the implementation may be 0f etc., unrelated to the ground surface) as Y, and the Y interpolation
    // result must be exactly the same as when state.Height == null (i.e. the failure fallback goes
    // through the identical code path as the null-sampler path). This is precisely the regression guard
    // against the "units teleport far below the ground surface for an instant during a TerrainManager
    // outage" bug.
    [Fact]
    public void Advance_falls_back_to_old_y_interpolation_when_TrySampleHeight_fails()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 42, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(u);
        s.Height = new FailingHeightSampler(); // always returns false. The out parameter (-9999f) must never be adopted.

        MovementStep.Advance(s, 1f);

        // Exactly the same expected value as Advance_preserves_old_y_interpolation_when_HeightSampler_is_null
        // (= confirming the interpolation result is identical to when state.Height == null).
        Assert.Equal(42f - 42f * (TankSpeedPerHour / 100f), s.Units[0].Position.Y, 1);
        Assert.NotEqual(-9999f, s.Units[0].Position.Y);
    }

    // --- Task55: deviation clamp on the sampled height (MaxSurfaceDeviation) ---

    // Even when TrySampleHeight returns true, if the value deviates from the interpolated Y by well
    // over MaxSurfaceDeviation(15f), it is ignored and the conventional Y interpolation result is used
    // (the defense that "even if the sampler returns an absurd value, units are not launched into the
    // sky"). OneMovingUnit() has Y=0 at both the start and the target, so the interpolated Y is always
    // 0 (it converges even during partial moves).
    [Fact]
    public void Advance_ignores_wildly_high_sampled_height_and_uses_interpolated_y_instead()
    {
        var s = OneMovingUnit(); // start (0,0,0) -> target (1000,0,0), interpolated Y is always 0
        s.Height = new OffsetHeightSampler(9999f); // deviation far beyond MaxSurfaceDeviation(15)

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.Y, 3);
    }

    // If the deviation is within MaxSurfaceDeviation(15f), the sampled value is adopted as before
    // (not breaking the "reflect small surface changes such as embankments" behavior Task53 introduced).
    [Fact]
    public void Advance_applies_sampled_height_when_deviation_is_within_MaxSurfaceDeviation()
    {
        var s = OneMovingUnit(); // interpolated Y is always 0
        s.Height = new OffsetHeightSampler(10f); // deviation within 15

        MovementStep.Advance(s, 1f);

        Assert.Equal(10f, s.Units[0].Position.Y, 3);
    }

    // Boundary value: exactly MaxSurfaceDeviation(15f) does not "exceed" it, so it is adopted
    // (the spec rejects "when it exceeds 15f" = 15f itself is inside the accepted range).
    [Fact]
    public void Advance_applies_sampled_height_when_deviation_exactly_equals_MaxSurfaceDeviation()
    {
        var s = OneMovingUnit();
        s.Height = new OffsetHeightSampler(MovementStep.MaxSurfaceDeviation);

        MovementStep.Advance(s, 1f);

        Assert.Equal(MovementStep.MaxSurfaceDeviation, s.Units[0].Position.Y, 3);
    }

    // Boundary value: slightly exceeding MaxSurfaceDeviation(15f) is rejected, reverting to the interpolated Y.
    [Fact]
    public void Advance_ignores_sampled_height_when_deviation_slightly_exceeds_MaxSurfaceDeviation()
    {
        var s = OneMovingUnit();
        s.Height = new OffsetHeightSampler(MovementStep.MaxSurfaceDeviation + 0.01f);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.Y, 3);
    }

    // --- Task61: movement rules for the Air/Sea domains ---

    private class FakeWaterSampler : IWaterSampler
    {
        private readonly float _waterBoundaryX;
        private readonly float _level;
        public FakeWaterSampler(float waterBoundaryX, float level = 0f) { _waterBoundaryX = waterBoundaryX; _level = level; }
        public bool IsWater(float x, float z) { return x <= _waterBoundaryX; }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = _level; return IsWater(x, z); }
    }

    private static WarState OneAirUnit(float startX, float startY, float targetX)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(AirUnitRoster.Get(UnitCategory.AirSuperiority, 1)); // Domain=Air, fast
        var u = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(startX, startY, 0f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(targetX, 0f, 0f);
        s.Units.Add(u);
        return s;
    }

    private static WarState OneSeaUnit(float startX, float targetX)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(NavalUnitRoster.Get(UnitCategory.Destroyer, 1)); // Domain=Sea
        var u = new UnitInstance(1, "Destroyer_T1", 0, 100f, new WorldPos(startX, 0f, 0f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(targetX, 0f, 0f);
        s.Units.Add(u);
        return s;
    }

    [Fact]
    public void Air_unit_ignores_Path_and_moves_straight_toward_OrderTargetPos()
    {
        var s = OneAirUnit(0f, 0f, 1000f);
        s.Units[0].Path = new List<WorldPos> { new WorldPos(0f, 0f, 500f) }; // would divert a land unit; must be ignored

        MovementStep.Advance(s, 1f);

        Assert.True(s.Units[0].Position.X > 0f, "expected the fighter to have advanced toward X");
        Assert.Equal(0f, s.Units[0].Position.Z, 2); // did NOT follow the waypoint's Z=500 detour
    }

    [Fact]
    public void Air_unit_holds_cruise_altitude_above_sampled_ground()
    {
        var s = OneAirUnit(0f, 999f, 1000f);
        s.Height = new FakeHeightSampler(); // TrySampleHeight(x,z) = x+z

        MovementStep.Advance(s, 1f);

        var pos = s.Units[0].Position;
        // ground at (pos.X, 0) == pos.X (FakeHeightSampler returns x+z), so Y must be groundY + CruiseAltitude.
        Assert.Equal(pos.X + MovementStep.CruiseAltitude, pos.Y, 2);
    }

    [Fact]
    public void Air_unit_keeps_previous_Y_when_height_sampling_fails()
    {
        var s = OneAirUnit(0f, 500f, 1000f);
        s.Height = new FailingHeightSampler();

        MovementStep.Advance(s, 1f);

        Assert.Equal(500f, s.Units[0].Position.Y, 3);
    }

    [Fact]
    public void Air_unit_snaps_exactly_to_objective_on_arrival()
    {
        var s = OneAirUnit(0f, 0f, 3f); // well within one tick's travel distance for a fast fighter
        MovementStep.Advance(s, 1f);
        Assert.Equal(3f, s.Units[0].Position.X, 2);
        Assert.Equal(0f, s.Units[0].Position.Z, 2);
    }

    [Fact]
    public void Air_unit_does_not_move_when_Idle_with_no_order()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(AirUnitRoster.Get(UnitCategory.AirSuperiority, 1));
        var u = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        s.Units.Add(u); // State stays Idle (default), no OrderTargetPos

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 3);
    }

    [Fact]
    public void Sea_unit_moves_straight_toward_OrderTargetPos_when_water_sampler_absent()
    {
        var s = OneSeaUnit(0f, 1000f);
        // s.Water intentionally left null: treated as "always water" (fallback for domain-less tests).
        MovementStep.Advance(s, 1f);
        Assert.True(s.Units[0].Position.X > 0f);
    }

    [Fact]
    public void Sea_unit_follows_the_sampled_water_level()
    {
        var s = OneSeaUnit(0f, 5f); // within one tick's travel distance
        s.Water = new FakeWaterSampler(waterBoundaryX: 1000f, level: 12f);

        MovementStep.Advance(s, 1f);

        Assert.Equal(5f, s.Units[0].Position.X, 2);
        Assert.Equal(12f, s.Units[0].Position.Y, 3);
    }

    [Fact]
    public void Sea_unit_refuses_to_step_onto_land_and_stops_at_the_waters_edge()
    {
        var s = OneSeaUnit(0f, 1000f); // objective is far inland, past the water boundary
        s.Water = new FakeWaterSampler(waterBoundaryX: 3f); // only x<=3 is water

        var beforeX = s.Units[0].Position.X;
        MovementStep.Advance(s, 1f); // stepLen for a destroyer easily exceeds 3 map units

        // The straight-line step would have landed past x=3 (on land), so the unit must not move at all
        // this tick rather than teleport onto land.
        Assert.Equal(beforeX, s.Units[0].Position.X, 3);
    }

    // --- Task78: fix for the "sea units stay holed up at their own base instead of moving to the enemy base" bug ---
    // When the next straight-line step is blocked by land, deterministic detour directions of
    // ±30/60/90 degrees are tried in order, and the unit advances in the first direction that lands
    // in water (a simple wall-follow). If every direction is blocked, that fact is accumulated into
    // SeaBlockedHours, and once it exceeds MovementStep.SeaBlockedIdleHours the unit transitions to
    // Idle to prevent searching forever (receiving a new objective resets it).

    // A fake where only the rectangle boxMinX<=x<=boxMaxX and |z|<=boxHalfZ is land and everything
    // else is water (models the base of a peninsula/cape: going straight hits this rectangle, but a
    // wide detour in the z direction can get around it).
    private class FakeWaterExceptBox : IWaterSampler
    {
        private readonly float _minX, _maxX, _halfZ;
        public FakeWaterExceptBox(float minX, float maxX, float halfZ) { _minX = minX; _maxX = maxX; _halfZ = halfZ; }
        public bool IsWater(float x, float z) { return !(x >= _minX && x <= _maxX && System.Math.Abs(z) <= _halfZ); }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return IsWater(x, z); }
    }

    // A fake where only the area within a radius of the origin is water (if the radius is made
    // sufficiently smaller than the unit's per-tick travel distance, one step in any detour direction —
    // let alone straight ahead — always lands on land = models an objective completely surrounded by
    // land).
    private class FakeWaterOnlyNearOrigin : IWaterSampler
    {
        private readonly float _radius;
        public FakeWaterOnlyNearOrigin(float radius) { _radius = radius; }
        public bool IsWater(float x, float z) { return (x * x + z * z) <= _radius * _radius; }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return IsWater(x, z); }
    }

    [Fact]
    public void Sea_unit_takes_a_deterministic_detour_step_when_the_direct_step_lands_on_a_peninsula()
    {
        var s = OneSeaUnit(0f, 1000f); // heading is pure +X
        var type = s.Types.Get("Destroyer_T1");
        float stepLen = type.Speed * MovementStep.GlobalSpeedMultiplier * 1f;
        // A narrow rectangle of land covering only the straight-ahead landing point (stepLen, 0). The
        // landing points rotated by ±30 degrees should both fall outside this rectangle, geometrically
        // guaranteeing that the detour logic really does try the candidate directions in order.
        s.Water = new FakeWaterExceptBox(stepLen - 1f, stepLen + 1f, 1f);

        MovementStep.Advance(s, 1f);

        var pos = s.Units[0].Position;
        // The algorithm is expected to try 0 degrees (straight) -> +30 -> -30 -> ... in order. Straight
        // ahead is blocked, so +30 degrees is chosen and the unit should land at the point rotated +30
        // degrees with the same step length (stepLen).
        double rad = 30.0 * System.Math.PI / 180.0;
        float expectedX = (float)(stepLen * System.Math.Cos(rad));
        float expectedZ = (float)(stepLen * System.Math.Sin(rad));
        Assert.Equal(expectedX, pos.X, 1);
        Assert.Equal(expectedZ, pos.Z, 1);
        // The landing point after the detour must be outside the rectangle = in water (re-confirming it
        // did not teleport onto land).
        Assert.True(s.Water.IsWater(pos.X, pos.Z));
    }

    [Fact]
    public void Sea_unit_makes_net_progress_working_its_way_around_a_peninsula_over_several_ticks()
    {
        var s = OneSeaUnit(0f, 30f);
        // Only (10<=x<=20, |z|<=5) is land: it stands in the way on the straight line to the objective,
        // but has finite width, so a wide detour can get around it (a "peninsula" in the sense of not
        // being an endless wall).
        s.Water = new FakeWaterExceptBox(10f, 20f, 5f);

        float initialDist = s.Units[0].Position.HorizontalDistanceTo(new WorldPos(30f, 0f, 0f));
        for (int i = 0; i < 30; i++)
            MovementStep.Advance(s, 1f);
        float finalDist = s.Units[0].Position.HorizontalDistanceTo(new WorldPos(30f, 0f, 0f));

        Assert.True(finalDist < initialDist, $"expected progress toward the target; initial={initialDist} final={finalDist}");
        // Also verify the invariant that it never landed on land during the detour (its current
        // position is always in water).
        Assert.True(s.Water.IsWater(s.Units[0].Position.X, s.Units[0].Position.Z));
    }

    [Fact]
    public void Sea_unit_gives_up_and_goes_Idle_after_SeaBlockedIdleHours_of_being_fully_landlocked()
    {
        var s = OneSeaUnit(0f, 1000f);
        // Only the circle of radius 2 is water: much smaller than a destroyer's per-tick travel
        // distance, so straight ahead and all 6 detour directions always land on land = models an
        // objective completely surrounded by land.
        s.Water = new FakeWaterOnlyNearOrigin(2f);
        var u = s.Units[0];

        for (int hour = 1; hour < (int)MovementStep.SeaBlockedIdleHours; hour++)
        {
            MovementStep.Advance(s, 1f);
            Assert.Equal(UnitState.Moving, u.State); // still below the threshold: keeps searching in the advancing state
            Assert.Equal(0f, u.Position.X, 3); // has not been able to advance in any direction
        }

        MovementStep.Advance(s, 1f); // the call that reaches the threshold (SeaBlockedIdleHours) exactly
        Assert.Equal(UnitState.Idle, u.State);
        Assert.Equal(0f, u.Position.X, 3);

        // Once Idle, ResolveDomainObjective no longer consults OrderTargetPos at all, so no matter how
        // many times it is called the unit stays still in the same place forever (no visible spinning).
        MovementStep.Advance(s, 100f);
        Assert.Equal(UnitState.Idle, u.State);
        Assert.Equal(0f, u.Position.X, 3);
    }

    [Fact]
    public void Sea_unit_blocked_counter_resets_when_a_new_order_gives_a_different_OrderTargetPos()
    {
        var s = OneSeaUnit(0f, 1000f);
        s.Water = new FakeWaterOnlyNearOrigin(2f); // completely landlocked toward either objective
        var u = s.Units[0];

        for (int i = 0; i < (int)MovementStep.SeaBlockedIdleHours; i++)
            MovementStep.Advance(s, 1f);
        Assert.Equal(UnitState.Idle, u.State); // gave up on the first objective

        // Assume a new order (equivalent to InvasionOrders) changed OrderTargetPos and returned State to Moving.
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(2000f, 0f, 0f);

        MovementStep.Advance(s, 1f);

        // The objective changed, so SeaBlockedHours should restart from 0: a single tick does not yet
        // reach the threshold (SeaBlockedIdleHours), so it must not fall back to Idle immediately.
        Assert.Equal(UnitState.Moving, u.State);
    }

    [Fact]
    public void Land_unit_behaviour_is_unchanged_by_the_Sea_Air_domain_split()
    {
        // Regression: a plain Land unit (Tank) must still use the pre-existing Path/road logic,
        // completely untouched by the new Domain != Land branch introduced above.
        var s = UnitWithPath();
        MovementStep.Advance(s, 0.1f);
        var u = s.Units[0];
        Assert.Equal(TankSpeedPerHour * 0.1f, u.Position.X, 2);
        Assert.Equal(0f, u.Position.Z, 1);
        Assert.Equal(0, u.PathIndex);
    }

    // --- Task77: fix for the "ground units can walk into the sea" bug (no off-road water entry for land units) ---

    // The opposite of AdvanceSea's FakeWaterSampler (water where x<=boundary): a fake where "water is
    // at and beyond the boundary" (oriented for the scenario of a land unit advancing from land toward
    // the sea).
    private class FakeWaterBeyondX : IWaterSampler
    {
        private readonly float _boundaryX;
        public FakeWaterBeyondX(float boundaryX) { _boundaryX = boundaryX; }
        public bool IsWater(float x, float z) { return x >= _boundaryX; }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return IsWater(x, z); }
    }

    private class FakeWaterBeyondZ : IWaterSampler
    {
        private readonly float _boundaryZ;
        public FakeWaterBeyondZ(float boundaryZ) { _boundaryZ = boundaryZ; }
        public bool IsWater(float x, float z) { return z >= _boundaryZ; }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return IsWater(x, z); }
    }

    // In the off-road straight-line fallback (no Path), if the next step would enter water, the unit
    // does not move at all that tick (stopped at the water's edge). Also confirm it stays in the same
    // place no matter how many ticks pass (a simple, deterministic rule symmetric to AdvanceSea's land
    // version).
    [Fact]
    public void Advance_land_unit_off_road_step_into_water_is_cancelled_and_unit_stays_at_the_shoreline()
    {
        var s = OneMovingUnit(); // (0,0,0) -> OrderTargetPos (1000,0,0), no Path
        s.Water = new FakeWaterBeyondX(3f); // x>=3 is water (stepLen(≈5.4) crosses it in one step)

        MovementStep.Advance(s, 1f);
        Assert.Equal(0f, s.Units[0].Position.X, 3);

        // Even after several more ticks, it stays outside the water (no automatic transition to Idle
        // etc., just a simple standstill).
        MovementStep.Advance(s, 5f);
        Assert.Equal(0f, s.Units[0].Position.X, 3);
    }

    // Water entry is likewise forbidden in the rally (RallyHold) straight-line fallback.
    [Fact]
    public void Advance_RallyHold_unit_off_road_step_into_water_is_cancelled()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(1000, 0, 0);
        s.Units.Add(u);
        s.Water = new FakeWaterBeyondX(3f);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 3);
    }

    // Water entry is likewise forbidden in the straight-line segment of cover movement (AdvanceTowardCover).
    [Fact]
    public void Advance_toward_CoverDestination_off_road_step_into_water_is_cancelled()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Engaging;
        u.CoverDestination = new WorldPos(0, 0, 1000); // north-south direction, tries to move along Z
        s.Units.Add(u);
        s.Water = new FakeWaterBeyondZ(3f); // z>=3 is water

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.Z, 3);
    }

    // The heart of Task77 (bridge regression guard): while on a path (Path/ConsumePath), the water
    // check does not apply. Directly under a bridge is "water" in the HasWater sense, but as long as
    // the unit is traversing road-network nodes it must remain passable. Confirm that even with a
    // water sampler supplied, the Path-following result is exactly the same as
    // Advance_large_step_crosses_first_waypoint_and_continues_toward_second (i.e. it was not stopped
    // by the water check).
    [Fact]
    public void Advance_water_sampler_does_not_block_movement_while_following_a_road_Path_bridge_regression()
    {
        var s = UnitWithPath(); // waypoints (100,0,0), (100,0,100)
        s.Water = new FakeWaterBeyondX(0f); // everything at x>=0 is "water" (an extreme case including under the bridge)

        MovementStep.Advance(s, 150f / TankSpeedPerHour); // same dt as Advance_large_step_crosses_first_waypoint_and_continues_toward_second
        var u = s.Units[0];

        Assert.Equal(100f, u.Position.X, 1);
        Assert.Equal(50f, u.Position.Z, 1);
        Assert.Equal(1, u.PathIndex);
    }

    // Confirm that only the off-road remainder after the Path is consumed (the "final leg when the
    // path ran out short of the destination") is subject to the water check, and the portion already
    // consumed on the path is unaffected.
    [Fact]
    public void Advance_blocks_only_the_off_road_remainder_after_Path_is_exhausted_when_it_enters_water()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(1000, 0, 0);
        u.Path = new List<WorldPos> { new WorldPos(50, 0, 0) }; // end of the on-land road
        u.PathIndex = 0;
        u.PathTarget = u.OrderTargetPos;
        s.Units.Add(u);
        s.Water = new FakeWaterBeyondX(55f); // water is x>=55, beyond the road's end (50)

        // stepLen=60: ConsumePath consumes 50 (10 remaining); trying to advance the off-road remainder
        // of 10 toward (1000,0,0) gives nx = 50 + 950*(10/950) ≈ 59.95 >= 55 -> water, so the off-road
        // portion does not move at all.
        MovementStep.Advance(s, 60f / TankSpeedPerHour);

        Assert.Equal(50f, u.Position.X, 1); // stopped at the road's end (the already-consumed 50f is not lost)
        Assert.Equal(1, u.PathIndex); // the waypoint has been consumed
    }
}
