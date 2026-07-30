using CSWarfront.Core;
using Xunit;

// Task79: MovementStep's dive movement for kamikaze units (UnitCategoryFlags.IsKamikaze). Kept in a
// separate file from the already-large MovementStepTests.cs (single-responsibility per test file,
// mirrors how ThreatAuraStepTests.cs/ThreatCombatStepTests.cs are split even though both exercise
// combat-adjacent Advance() steps). KamikazeStep.cs is what writes the lock (TargetId/TargetThreatId)
// that these tests set up directly on the UnitInstance, since here we only test MovementStep's own
// reaction to that lock, not the acquisition logic itself (see KamikazeStepTests.cs for that).
public class MovementStepKamikazeTests
{
    private class FakeHeightSampler : IHeightSampler
    {
        // A cruise-altitude Y that would be very different from a direct 3D dive interpolation,
        // so any test that finds this value being used has caught a regression to normal AdvanceAir.
        public bool TrySampleHeight(float x, float z, out float height) { height = 9999f; return true; }
    }

    private static WarState DroneLockedOnUnit(WorldPos dronePos, WorldPos targetPos)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Types.Register(AirUnitRoster.Get(UnitCategory.SuicideDrone, 1));
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));

        var drone = new UnitInstance(1, "SuicideDrone_T1", 0, 40f, dronePos);
        drone.State = UnitState.Engaging;
        drone.TargetId = 2;
        s.Units.Add(drone);

        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, targetPos));
        return s;
    }

    [Fact]
    public void Dives_directly_toward_the_locked_targets_current_3D_position()
    {
        // Target is both off to the side (X) and above (Y) — a real 3D dive, not a horizontal-only move.
        var s = DroneLockedOnUnit(new WorldPos(0f, 200f, 0f), new WorldPos(100f, 0f, 0f));

        MovementStep.Advance(s, 0.001f); // tiny dt: assert direction, not arrival

        WorldPos pos = s.FindUnit(1).Position;
        Assert.True(pos.X > 0f, "should move toward the target's X");
        Assert.True(pos.Y < 200f, "should descend toward the target's (lower) Y, not hold cruise altitude");
    }

    [Fact]
    public void Dive_ignores_the_height_sampler_and_CruiseAltitude()
    {
        var s = DroneLockedOnUnit(new WorldPos(0f, 50f, 0f), new WorldPos(1000f, 50f, 0f));
        s.Height = new FakeHeightSampler(); // would push Y to 9999+CruiseAltitude if (wrongly) used

        MovementStep.Advance(s, 1f);

        Assert.True(s.FindUnit(1).Position.Y < 100f, "dive must not snap to groundHeight+CruiseAltitude");
    }

    [Fact]
    public void Dive_speed_is_faster_than_normal_cruise_speed_by_DiveSpeedMultiplier()
    {
        var diving = DroneLockedOnUnit(new WorldPos(0f, 0f, 0f), new WorldPos(100000f, 0f, 0f));
        MovementStep.Advance(diving, 1f);
        float diveDist = diving.FindUnit(1).Position.DistanceTo(new WorldPos(0f, 0f, 0f));

        // Same unit/dt but no lock: falls back to normal AdvanceAir cruise speed (type.Speed*dt).
        var cruising = DroneLockedOnUnit(new WorldPos(0f, 0f, 0f), new WorldPos(100000f, 0f, 0f));
        cruising.FindUnit(1).TargetId = null;
        cruising.FindUnit(1).State = UnitState.Moving;
        cruising.FindUnit(1).OrderTargetPos = new WorldPos(100000f, 0f, 0f);
        MovementStep.Advance(cruising, 1f);
        float cruiseDist = cruising.FindUnit(1).Position.DistanceTo(new WorldPos(0f, 0f, 0f));

        Assert.Equal(MovementStep.DiveSpeedMultiplier, diveDist / cruiseDist, 2);
    }

    [Fact]
    public void Snaps_exactly_to_the_target_on_arrival()
    {
        var s = DroneLockedOnUnit(new WorldPos(0f, 0f, 0f), new WorldPos(3f, 0f, 0f)); // well within one tick

        MovementStep.Advance(s, 1f);

        WorldPos pos = s.FindUnit(1).Position;
        Assert.Equal(3f, pos.X, 2);
        Assert.Equal(0f, pos.Y, 2);
        Assert.Equal(0f, pos.Z, 2);
    }

    [Fact]
    public void Follows_the_targets_updated_position_across_ticks_a_moving_target()
    {
        var s = DroneLockedOnUnit(new WorldPos(0f, 0f, 0f), new WorldPos(1000f, 0f, 0f));
        MovementStep.Advance(s, 1f);
        float xAfterFirstTick = s.FindUnit(1).Position.X;

        // Target moved further away between ticks (e.g. it kept advancing).
        s.FindUnit(2).Position = new WorldPos(2000f, 0f, 0f);
        MovementStep.Advance(s, 1f);
        float xAfterSecondTick = s.FindUnit(1).Position.X;

        Assert.True(xAfterSecondTick > xAfterFirstTick, "should keep steering toward the target's latest position");
    }

    [Fact]
    public void Falls_back_to_normal_cruise_when_the_locked_unit_target_no_longer_exists()
    {
        var s = DroneLockedOnUnit(new WorldPos(0f, 0f, 0f), new WorldPos(10f, 0f, 0f));
        s.FindUnit(1).TargetId = 999; // stale/non-existent id (e.g. target already removed this tick)
        s.FindUnit(1).State = UnitState.Moving;
        s.FindUnit(1).OrderTargetPos = new WorldPos(50f, 0f, 0f);

        MovementStep.Advance(s, 1f);

        // Falls through to ResolveDomainObjective/AdvanceAir: heads toward OrderTargetPos, not the stale target.
        WorldPos pos = s.FindUnit(1).Position;
        Assert.True(pos.X > 0f);
        Assert.Equal(0f, pos.Z, 2);
    }

    [Fact]
    public void Dives_toward_a_locked_external_threat()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(AirUnitRoster.Get(UnitCategory.SuicideDrone, 1));
        var drone = new UnitInstance(1, "SuicideDrone_T1", 0, 40f, new WorldPos(0f, 0f, 0f));
        drone.State = UnitState.Engaging;
        drone.TargetThreatId = 7;
        s.Units.Add(drone);
        s.Threats.Add(new ExternalThreat
        {
            Id = 7, Kind = ThreatKind.Kaiju, Position = new WorldPos(50f, 0f, 0f),
            Radius = 10f, MaxHP = 1000f, CurrentHP = 1000f
        });

        MovementStep.Advance(s, 1f);

        Assert.True(s.FindUnit(1).Position.X > 0f, "should dive toward the locked threat");
    }
}
