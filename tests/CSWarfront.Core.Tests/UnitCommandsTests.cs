using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

/// <summary>Task48: tests for UnitCommands (the API that applies player unit commands to UnitInstance).
/// Verifies the returned affected counts, the clearing of state left over from older orders, and that
/// missing/dead IDs are ignored. At the end, integration tests combining CombatStep/CoverSeekStep/MovementStep
/// confirm that Hold/RallyHold are "passive defense" (fire while in range, but no chasing / cover movement / base advance).</summary>
public class UnitCommandsTests
{
    private static WarState OneUnitState()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        return s;
    }

    // --- ApplyFreeAdvance ---

    [Fact]
    public void ApplyFreeAdvance_sets_order_and_returns_affected_count()
    {
        var s = OneUnitState();
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(u);

        int count = UnitCommands.ApplyFreeAdvance(s, new List<uint> { 1 });

        Assert.Equal(1, count);
        Assert.Equal(UnitOrder.FreeAdvance, u.Order);
    }

    [Fact]
    public void ApplyFreeAdvance_clears_stale_RallyHold_state()
    {
        var s = OneUnitState();
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(10, 0, 10);
        u.Path = new List<WorldPos> { new WorldPos(10, 0, 10) };
        u.PathTarget = new WorldPos(10, 0, 10);
        u.CoverDestination = new WorldPos(5, 0, 5);
        u.CoverHold = true;
        s.Units.Add(u);

        UnitCommands.ApplyFreeAdvance(s, new List<uint> { 1 });

        Assert.Null(u.RallyPoint);
        Assert.Null(u.Path);
        Assert.Null(u.OrderTargetPos);
        Assert.False(u.CoverDestination.HasValue);
        Assert.False(u.CoverHold);
    }

    // --- ApplyHold ---

    [Fact]
    public void ApplyHold_sets_order_and_clears_movement_state()
    {
        var s = OneUnitState();
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(500, 0, 0);
        u.Path = new List<WorldPos> { new WorldPos(100, 0, 0) };
        u.PathTarget = new WorldPos(500, 0, 0);
        u.CoverDestination = new WorldPos(50, 0, 0);
        u.CoverHold = false;
        s.Units.Add(u);

        int count = UnitCommands.ApplyHold(s, new List<uint> { 1 });

        Assert.Equal(1, count);
        Assert.Equal(UnitOrder.Hold, u.Order);
        Assert.Null(u.OrderTargetPos);
        Assert.Null(u.Path);
        Assert.False(u.CoverDestination.HasValue);
        Assert.Equal(UnitState.Idle, u.State);
    }

    [Fact]
    public void ApplyHold_preserves_Engaging_state()
    {
        var s = OneUnitState();
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Engaging;
        u.TargetId = 99;
        s.Units.Add(u);

        UnitCommands.ApplyHold(s, new List<uint> { 1 });

        Assert.Equal(UnitState.Engaging, u.State);
    }

    [Fact]
    public void ApplyHold_ignores_missing_and_dead_unit_ids()
    {
        var s = OneUnitState();
        var dead = new UnitInstance(1, "Tank_T1", 0, 0f, new WorldPos(0, 0, 0));
        dead.State = UnitState.Dead;
        s.Units.Add(dead);

        int count = UnitCommands.ApplyHold(s, new List<uint> { 1, 999 });

        Assert.Equal(0, count);
        Assert.Equal(UnitOrder.AiControlled, dead.Order); // untouched
    }

    // --- ApplyRally ---

    [Fact]
    public void ApplyRally_sets_order_and_RallyPoint_and_returns_count()
    {
        var s = OneUnitState();
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(u);

        int count = UnitCommands.ApplyRally(s, new List<uint> { 1 }, new WorldPos(42, 0, 7));

        Assert.Equal(1, count);
        Assert.Equal(UnitOrder.RallyHold, u.Order);
        Assert.True(u.RallyPoint.HasValue);
        Assert.Equal(42f, u.RallyPoint.Value.X, 3);
        Assert.Equal(UnitState.Moving, u.State);
    }

    [Fact]
    public void ApplyRally_leaves_Path_null_when_no_roads_supplied()
    {
        var s = OneUnitState();
        s.Roads = null;
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(u);

        UnitCommands.ApplyRally(s, new List<uint> { 1 }, new WorldPos(1000, 0, 0));

        Assert.Null(u.Path);
    }

    [Fact]
    public void ApplyRally_computes_road_path_toward_rally_point_when_roads_supplied()
    {
        var s = OneUnitState();
        var g = new RoadGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));
        g.AddNode(2, new WorldPos(100, 0, 0));
        g.AddEdge(1, 2);
        s.Roads = g;
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(u);

        UnitCommands.ApplyRally(s, new List<uint> { 1 }, new WorldPos(100, 0, 0));

        Assert.NotNull(u.Path);
        Assert.NotEmpty(u.Path);
        Assert.True(u.PathTarget.HasValue);
        Assert.Equal(100f, u.PathTarget.Value.X, 3);
    }

    [Fact]
    public void ApplyRally_clears_stale_Hold_and_cover_state()
    {
        var s = OneUnitState();
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.Hold;
        u.CoverDestination = new WorldPos(5, 0, 5);
        u.CoverHold = true;
        s.Units.Add(u);

        UnitCommands.ApplyRally(s, new List<uint> { 1 }, new WorldPos(10, 0, 10));

        Assert.False(u.CoverDestination.HasValue);
        Assert.False(u.CoverHold);
    }

    // --- ClearOrders ---

    [Fact]
    public void ClearOrders_resets_to_AiControlled_and_clears_movement_state()
    {
        var s = OneUnitState();
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(10, 0, 10);
        u.OrderTargetPos = new WorldPos(10, 0, 10);
        s.Units.Add(u);

        int count = UnitCommands.ClearOrders(s, new List<uint> { 1 });

        Assert.Equal(1, count);
        Assert.Equal(UnitOrder.AiControlled, u.Order);
        Assert.Null(u.RallyPoint);
        Assert.Null(u.OrderTargetPos);
    }

    [Fact]
    public void ApplyFreeAdvance_ApplyHold_ApplyRally_ClearOrders_ignore_unknown_ids_and_return_zero()
    {
        var s = OneUnitState();

        Assert.Equal(0, UnitCommands.ApplyFreeAdvance(s, new List<uint> { 12345 }));
        Assert.Equal(0, UnitCommands.ApplyHold(s, new List<uint> { 12345 }));
        Assert.Equal(0, UnitCommands.ApplyRally(s, new List<uint> { 12345 }, new WorldPos(0, 0, 0)));
        Assert.Equal(0, UnitCommands.ClearOrders(s, new List<uint> { 12345 }));
    }

    // --- Task48 integration: Hold/RallyHold are "passive defense" (fire while in range, but no chasing / cover movement) ---

    private static WarState HostilePair(out UnitInstance self, out UnitInstance enemy)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        LandUnitRoster.RegisterAll(s.Types);

        var selfType = LandUnitRoster.Get(UnitCategory.Tank, 1); // Range=60
        self = new UnitInstance(1, selfType.TypeKey, 0, selfType.MaxHP, new WorldPos(0, 0, 0));
        enemy = new UnitInstance(2, selfType.TypeKey, 1, selfType.MaxHP, new WorldPos(30, 0, 0)); // within range(60)
        s.Units.Add(self);
        s.Units.Add(enemy);
        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(15, 0, 0), 5f); // cover exists between them, should be ignored for Hold/RallyHold
        return s;
    }

    [Fact]
    public void Hold_unit_fires_at_hostile_in_range_but_never_moves_or_gains_CoverDestination()
    {
        UnitInstance self, enemy;
        var s = HostilePair(out self, out enemy);
        UnitCommands.ApplyHold(s, new List<uint> { self.InstanceId });

        for (int tick = 0; tick < 5; tick++)
        {
            CoverSeekStep.Advance(s, 0.01f);
            MovementStep.Advance(s, 0.01f);
            CombatStep.Advance(s, 0.01f);
        }

        Assert.Equal(UnitState.Engaging, self.State);
        Assert.Equal(enemy.InstanceId, self.TargetId);
        Assert.False(self.CoverDestination.HasValue);
        Assert.Equal(0f, self.Position.X, 3);
        Assert.Equal(0f, self.Position.Z, 3);
        Assert.True(enemy.CurrentHP < LandUnitRoster.Get(UnitCategory.Tank, 1).MaxHP); // took damage
    }

    [Fact]
    public void RallyHold_unit_fires_at_hostile_in_range_while_marching_to_rally_but_never_chases_or_seeks_cover()
    {
        UnitInstance self, enemy;
        var s = HostilePair(out self, out enemy);
        var rallyPoint = new WorldPos(0, 0, 500); // away from the enemy, in the opposite direction
        UnitCommands.ApplyRally(s, new List<uint> { self.InstanceId }, rallyPoint);

        for (int tick = 0; tick < 5; tick++)
        {
            CoverSeekStep.Advance(s, 0.1f);
            MovementStep.Advance(s, 0.1f);
            CombatStep.Advance(s, 0.1f);
        }

        // Still engaging the enemy that is within range, but never diverted toward it (no cover-seeking/chasing):
        Assert.Equal(UnitState.Engaging, self.State);
        Assert.Equal(enemy.InstanceId, self.TargetId);
        Assert.False(self.CoverDestination.HasValue);
        Assert.True(enemy.CurrentHP < LandUnitRoster.Get(UnitCategory.Tank, 1).MaxHP); // took damage despite marching away

        // Moved toward the rally point (positive Z), not toward the enemy (positive X):
        Assert.True(self.Position.Z > 0f, "expected unit to keep marching toward RallyPoint");
        Assert.Equal(0f, self.Position.X, 1);
    }

    [Fact]
    public void RallyHold_unit_stops_at_RallyPoint_and_does_not_advance_past_it()
    {
        UnitInstance self, enemy;
        var s = HostilePair(out self, out enemy);
        var rallyPoint = new WorldPos(0, 0, 5); // close by, reached quickly
        UnitCommands.ApplyRally(s, new List<uint> { self.InstanceId }, rallyPoint);

        for (int tick = 0; tick < 20; tick++)
        {
            CoverSeekStep.Advance(s, 0.5f);
            MovementStep.Advance(s, 0.5f);
            CombatStep.Advance(s, 0.5f);
        }

        Assert.True(self.Position.HorizontalDistanceTo(rallyPoint) <= MovementStep.CoverArrivalDistance);
        Assert.False(self.CoverDestination.HasValue);
    }
}
