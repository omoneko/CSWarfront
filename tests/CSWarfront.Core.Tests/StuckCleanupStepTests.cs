using CSWarfront.Core;
using Xunit;

/// <summary>
/// Tests for StuckCleanupStep (Task98: automatic despawning of units stuck at the water's edge and similar spots).
/// </summary>
public class StuckCleanupStepTests
{
    private static WarState StateWithUnit(UnitState unitState, WorldPos pos)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, pos);
        u.State = unitState;
        s.Units.Add(u);
        return s;
    }

    /// <summary>Advances the given number of hours in dt=1h steps (position stays fixed = stuck).</summary>
    private static int AdvanceStuckHours(WarState s, float hours)
    {
        int despawned = 0;
        for (float t = 0; t < hours; t += 1f) despawned += StuckCleanupStep.Advance(s, 1f);
        return despawned;
    }

    [Fact]
    public void Unit_deliberately_holding_at_cover_is_never_despawned()
    {
        // Task120: holding at a cover/fortification position is intentional stillness, not being stuck.
        var s = StateWithUnit(UnitState.Moving, new WorldPos(1000, 0, 1000));
        s.Units[0].CoverDestination = new WorldPos(1000, 0, 1000);
        s.Units[0].CoverHold = true;

        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours + 5f);

        Assert.Equal(0, despawned);
        Assert.Equal(UnitState.Moving, s.Units[0].State);
    }

    [Fact]
    public void Stuck_moving_unit_despawns_silently_after_the_threshold()
    {
        var s = StateWithUnit(UnitState.Moving, new WorldPos(1000, 0, 1000));

        // The first Advance sets the anchor -> then DespawnAfterHours elapse from there.
        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours + 2f);

        Assert.Equal(1, despawned);
        Assert.Equal(UnitState.Dead, s.Units[0].State);
        Assert.Empty(s.RecentKills); // silent, no explosion (no KillEvent is queued)
    }

    [Fact]
    public void Progressing_unit_never_despawns()
    {
        var s = StateWithUnit(UnitState.Moving, new WorldPos(0, 0, 0));

        for (int i = 0; i < 40; i++)
        {
            // Advances at least MinProgressDistance every hour (a normal march).
            var u = s.Units[0];
            u.Position = new WorldPos(u.Position.X + StuckCleanupStep.MinProgressDistance + 1f, 0, 0);
            StuckCleanupStep.Advance(s, 1f);
        }

        Assert.True(s.Units[0].IsAlive);
    }

    [Fact]
    public void Slow_infantry_marching_at_its_own_speed_never_despawns()
    {
        // Task98 addendum (in-game bug): infantry (7.5km/h) covers only about 1.0m per game hour,
        // so with a fixed 20m threshold a perfectly normal march was flagged as stuck
        // ("12m in 12h < 20m") and despawned. The threshold must scale with speed.
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        LandUnitRoster.RegisterAll(s.Types);
        UnitType infantry = s.Types.Get(LandUnitRoster.TypeKey(UnitCategory.Infantry, 1));
        var u = new UnitInstance(1, infantry.TypeKey, 0, infantry.MaxHP, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        s.Units.Add(u);

        // Keeps advancing by Speed x 1h every hour, exactly as its true speed allows (a normal march).
        for (int i = 0; i < 48; i++)
        {
            u.Position = new WorldPos(u.Position.X + infantry.Speed * 1f, 0, 0);
            StuckCleanupStep.Advance(s, 1f);
        }

        Assert.True(u.IsAlive);
    }

    [Fact]
    public void Fully_stuck_infantry_still_despawns()
    {
        // Even with the speed-proportional threshold, infantry that truly cannot move is still removed.
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        LandUnitRoster.RegisterAll(s.Types);
        UnitType infantry = s.Types.Get(LandUnitRoster.TypeKey(UnitCategory.Infantry, 1));
        var u = new UnitInstance(1, infantry.TypeKey, 0, infantry.MaxHP, new WorldPos(1000, 0, 1000));
        u.State = UnitState.Moving;
        s.Units.Add(u);

        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours + 2f);

        Assert.Equal(1, despawned);
        Assert.Equal(UnitState.Dead, u.State);
    }

    [Fact]
    public void Stuck_unit_near_its_own_base_is_preserved()
    {
        var s = StateWithUnit(UnitState.Moving, new WorldPos(1000, 0, 1000));
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(1050, 0, 1000)); // 50m away = within the exclusion radius
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours * 3f);

        Assert.Equal(0, despawned);
        Assert.True(s.Units[0].IsAlive);
    }

    [Fact]
    public void Stuck_unit_near_an_enemy_base_still_despawns()
    {
        // A nearby base owned by an enemy (another faction) grants no exclusion.
        var s = StateWithUnit(UnitState.Moving, new WorldPos(1000, 0, 1000));
        s.Factions.Add(new Faction(1, "Blue"));
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(1050, 0, 1000));
        b.OwnerFactionId = 1;
        s.Bases.Add(b);

        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours + 2f);

        Assert.Equal(1, despawned);
    }

    [Fact]
    public void Idle_and_engaging_units_are_never_considered_stuck()
    {
        var idle = StateWithUnit(UnitState.Idle, new WorldPos(0, 0, 0));
        var engaging = StateWithUnit(UnitState.Engaging, new WorldPos(0, 0, 0));

        Assert.Equal(0, AdvanceStuckHours(idle, StuckCleanupStep.DespawnAfterHours * 3f));
        Assert.Equal(0, AdvanceStuckHours(engaging, StuckCleanupStep.DespawnAfterHours * 3f));
        Assert.True(idle.Units[0].IsAlive);
        Assert.True(engaging.Units[0].IsAlive);
    }

    [Fact]
    public void Progress_resets_the_stuck_timer()
    {
        var s = StateWithUnit(UnitState.Moving, new WorldPos(0, 0, 0));

        // Stuck until just short of the threshold -> advance once -> stuck again. The total time
        // exceeds the threshold but is not continuous, so the unit survives.
        AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours - 2f);
        var u = s.Units[0];
        u.Position = new WorldPos(StuckCleanupStep.MinProgressDistance + 1f, 0, 0);
        StuckCleanupStep.Advance(s, 1f); // the anchor and timer are reset here
        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours - 2f);

        Assert.Equal(0, despawned);
        Assert.True(u.IsAlive);
    }

    [Fact]
    public void State_transition_out_of_moving_resets_the_timer()
    {
        var s = StateWithUnit(UnitState.Moving, new WorldPos(0, 0, 0));

        AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours - 2f);
        s.Units[0].State = UnitState.Engaging; // entered combat (standing still is normal)
        StuckCleanupStep.Advance(s, 1f);
        s.Units[0].State = UnitState.Moving;   // combat over, movement resumes
        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours - 2f);

        Assert.Equal(0, despawned);
        Assert.True(s.Units[0].IsAlive);
    }
}
