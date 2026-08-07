using CSWarfront.Core;
using Xunit;

public class AiTargetingTests
{
    private static WarState TwoEnemyBases()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        var near = new MilitaryBase(10, BaseType.Army, new WorldPos(100, 0, 0)); near.OwnerFactionId = 1;
        var far = new MilitaryBase(11, BaseType.Army, new WorldPos(500, 0, 0)); far.OwnerFactionId = 1;
        var own = new MilitaryBase(12, BaseType.Army, new WorldPos(50, 0, 0)); own.OwnerFactionId = 0;
        s.Bases.Add(near); s.Bases.Add(far); s.Bases.Add(own);
        return s;
    }

    [Fact]
    public void Chooses_nearest_hostile_owned_base()
    {
        var s = TwoEnemyBases();
        var t = AiTargeting.ChooseTargetBase(s, 0, new WorldPos(0, 0, 0));
        Assert.Equal((ushort)10, t.BaseId);
    }

    [Fact]
    public void Returns_null_when_no_hostile_base()
    {
        var s = TwoEnemyBases();
        s.Relations.Set(0, 1, Relation.Neutral); // Hostility lifted
        Assert.Null(AiTargeting.ChooseTargetBase(s, 0, new WorldPos(0, 0, 0)));
    }

    // --- Task59: Nemesis (arch-enemy) ---

    [Fact]
    public void ChooseTargetBase_counts_nemesis_owned_base_as_hostile()
    {
        var s = TwoEnemyBases();
        s.Relations.Set(0, 1, Relation.Nemesis);
        var t = AiTargeting.ChooseTargetBase(s, 0, new WorldPos(0, 0, 0));
        Assert.Equal((ushort)10, t.BaseId);
    }

    [Fact]
    public void ChooseTargetBase_prefers_a_farther_nemesis_base_over_a_closer_ordinary_hostile_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Factions.Add(new Faction(2, "Nemesis"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Relations.Set(0, 2, Relation.Nemesis);
        var closeHostile = new MilitaryBase(10, BaseType.Army, new WorldPos(50, 0, 0)); closeHostile.OwnerFactionId = 1;
        var fartherNemesis = new MilitaryBase(11, BaseType.Army, new WorldPos(200, 0, 0)); fartherNemesis.OwnerFactionId = 2;
        s.Bases.Add(closeHostile); s.Bases.Add(fartherNemesis);

        var t = AiTargeting.ChooseTargetBase(s, 0, new WorldPos(0, 0, 0));
        Assert.Equal((ushort)11, t.BaseId);
    }

    // --- Task64: Sea-domain units target the nearest hostile-owned BaseType.Navy base only, ignoring
    // hostile bases of other types (they used to beach themselves marching on inland Army/AirForce/
    // MissileBase targets). Land/Air callers keep the old "any hostile base type" behaviour by using
    // the default domain parameter (Domain.Land), exercised by every other test in this file. ---

    [Fact]
    public void ChooseTargetBase_for_sea_domain_ignores_a_nearer_non_navy_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        var nearArmy = new MilitaryBase(10, BaseType.Army, new WorldPos(50, 0, 0)); nearArmy.OwnerFactionId = 1;
        var farNavy = new MilitaryBase(11, BaseType.Navy, new WorldPos(500, 0, 0)); farNavy.OwnerFactionId = 1;
        s.Bases.Add(nearArmy); s.Bases.Add(farNavy);

        var t = AiTargeting.ChooseTargetBase(s, 0, new WorldPos(0, 0, 0), Domain.Sea);
        Assert.Equal((ushort)11, t.BaseId); // the farther Navy base wins over the nearer Army base
    }

    [Fact]
    public void ChooseTargetBase_for_sea_domain_returns_null_when_no_hostile_navy_base_exists()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        var army = new MilitaryBase(10, BaseType.Army, new WorldPos(50, 0, 0)); army.OwnerFactionId = 1;
        var airForce = new MilitaryBase(11, BaseType.AirForce, new WorldPos(60, 0, 0)); airForce.OwnerFactionId = 1;
        s.Bases.Add(army); s.Bases.Add(airForce);

        Assert.Null(AiTargeting.ChooseTargetBase(s, 0, new WorldPos(0, 0, 0), Domain.Sea));
    }

    [Fact]
    public void ChooseTargetBase_for_sea_domain_still_prefers_a_farther_nemesis_navy_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Factions.Add(new Faction(2, "Nemesis"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Relations.Set(0, 2, Relation.Nemesis);
        var closeHostileNavy = new MilitaryBase(10, BaseType.Navy, new WorldPos(50, 0, 0)); closeHostileNavy.OwnerFactionId = 1;
        var fartherNemesisNavy = new MilitaryBase(11, BaseType.Navy, new WorldPos(200, 0, 0)); fartherNemesisNavy.OwnerFactionId = 2;
        s.Bases.Add(closeHostileNavy); s.Bases.Add(fartherNemesisNavy);

        var t = AiTargeting.ChooseTargetBase(s, 0, new WorldPos(0, 0, 0), Domain.Sea);
        Assert.Equal((ushort)11, t.BaseId);
    }

    [Fact]
    public void ChooseTargetBase_default_domain_is_unaffected_and_still_ignores_base_type()
    {
        // Domain.Land (the default) must keep the pre-Task64 behaviour: any hostile-owned base type
        // is a valid target, regardless of BaseType.
        var s = TwoEnemyBases(); // all three bases are BaseType.Army
        var t = AiTargeting.ChooseTargetBase(s, 0, new WorldPos(0, 0, 0));
        Assert.Equal((ushort)10, t.BaseId);
    }

    [Fact]
    public void AssignAdvance_sea_unit_targets_the_nearest_hostile_navy_base_ignoring_a_nearer_army_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        NavalUnitRoster.RegisterAll(s.Types);
        var nearArmy = new MilitaryBase(10, BaseType.Army, new WorldPos(50, 0, 0)); nearArmy.OwnerFactionId = 1;
        var farNavy = new MilitaryBase(11, BaseType.Navy, new WorldPos(500, 0, 0)); farNavy.OwnerFactionId = 1;
        s.Bases.Add(nearArmy); s.Bases.Add(farNavy);
        s.Units.Add(new UnitInstance(1, NavalUnitRoster.TypeKey(UnitCategory.Destroyer, 1), 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(UnitState.Moving, u.State);
        Assert.Equal(500f, u.OrderTargetPos.Value.X, 3); // Navy base, not the nearer Army base
    }

    [Fact]
    public void AssignAdvance_sea_unit_gets_no_order_when_no_hostile_navy_base_exists()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        NavalUnitRoster.RegisterAll(s.Types);
        var army = new MilitaryBase(10, BaseType.Army, new WorldPos(50, 0, 0)); army.OwnerFactionId = 1;
        s.Bases.Add(army);
        s.Units.Add(new UnitInstance(1, NavalUnitRoster.TypeKey(UnitCategory.Destroyer, 1), 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1); // MVP patrol behaviour: idle, no advance order, still fights whatever comes in range
        Assert.False(u.OrderTargetPos.HasValue);
        Assert.Equal(UnitState.Idle, u.State);
    }

    [Fact]
    public void AssignAdvance_land_unit_targeting_is_unaffected_by_the_sea_navy_restriction()
    {
        var s = TwoEnemyBases(); // all BaseType.Army; a land unit must still be able to target them
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(UnitState.Moving, u.State);
        Assert.Equal(100f, u.OrderTargetPos.Value.X, 3); // near Army base, as before
    }

    [Fact]
    public void AssignAdvance_sets_moving_orders_for_faction_units()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        InvasionOrders.AssignAdvance(s, 0, 0f);
        Assert.Equal(UnitState.Moving, s.FindUnit(1).State);
        Assert.True(s.FindUnit(1).OrderTargetPos.HasValue);
        Assert.Equal(100f, s.FindUnit(1).OrderTargetPos.Value.X, 3); // toward the near base
    }

    private static RoadGraph SimpleRoadToNearBase()
    {
        // A(0,0,0) - B(100,0,0), matching "near" base position exactly.
        var g = new RoadGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));
        g.AddNode(2, new WorldPos(100, 0, 0));
        g.AddEdge(1, 2);
        return g;
    }

    [Fact]
    public void AssignAdvance_with_roads_computes_path_and_records_target()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase();
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.NotNull(u.Path);
        Assert.NotEmpty(u.Path);
        Assert.True(u.PathTarget.HasValue);
        Assert.Equal(u.OrderTargetPos.Value.X, u.PathTarget.Value.X, 3);
    }

    [Fact]
    public void AssignAdvance_without_roads_leaves_path_null()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = null;
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var u = s.FindUnit(1);
        Assert.Equal(UnitState.Moving, u.State);
        Assert.True(u.OrderTargetPos.HasValue);
        Assert.Null(u.Path);
    }

    [Fact]
    public void AssignAdvance_clears_stale_path_when_target_base_changes()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase();
        var unit = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(unit);

        InvasionOrders.AssignAdvance(s, 0, 0f);
        var firstPath = unit.Path;
        Assert.NotNull(firstPath);

        // Simulate the near base falling / no longer being the nearest target:
        // remove it so the unit must now aim at the far base instead.
        s.Bases.RemoveAll(b => b.BaseId == 10);
        // Extend the road graph so the far base (500,0,0) is reachable and would
        // differ from the stale PathTarget, forcing a recompute.
        s.Roads.AddNode(3, new WorldPos(500, 0, 0));
        s.Roads.AddEdge(2, 3);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(500f, unit.OrderTargetPos.Value.X, 3);
        Assert.Equal(500f, unit.PathTarget.Value.X, 3);
    }

    [Fact]
    public void AssignAdvance_maxPathComputations_throttles_new_path_computation()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase();
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        s.Units.Add(new UnitInstance(3, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, 0f, maxPathComputations: 1);

        int withPath = 0;
        foreach (var u in s.Units)
            if (u.Path != null) withPath++;

        Assert.Equal(1, withPath);
        // all units still receive orders/state even if path computation was throttled
        foreach (var u in s.Units)
        {
            Assert.Equal(UnitState.Moving, u.State);
            Assert.True(u.OrderTargetPos.HasValue);
        }
    }

    // --- PathRetryCooldown (Task23 review, Important) ---
    // Unreachable units (too far from any road, etc.) must not rerun a full A* on every FindPath
    // failure; they are not retried until PathRetryFailCooldownHours (2h) has elapsed.

    [Fact]
    public void AssignAdvance_unit_on_cooldown_is_not_retried_before_it_elapses()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase(); // nodes only at (0,0,0)/(100,0,0)
        // Position far away from any road = FindPath is guaranteed to fail (cannot snap the "from" side).
        var unit = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(1000, 0, 0));
        s.Units.Add(unit);

        // 1st call: attempted immediately, fails, and the cooldown is armed.
        InvasionOrders.AssignAdvance(s, 0, 0f);
        Assert.Null(unit.Path);
        Assert.Equal(InvasionOrders.PathRetryFailCooldownHours, unit.PathRetryCooldown, 3);

        // 2nd call: before the cooldown runs out (0.5h elapsed). If it had been retried, the failure
        // would reset the cooldown to exactly 2f; if it was not retried, it simply decays to 2 - 0.5 = 1.5.
        InvasionOrders.AssignAdvance(s, 0, 0.5f);
        Assert.Null(unit.Path);
        Assert.Equal(1.5f, unit.PathRetryCooldown, 3);
    }

    [Fact]
    public void AssignAdvance_unit_is_retried_once_cooldown_elapses()
    {
        var s = TwoEnemyBases(); // near(100,0,0) is the closest hostile base from Z-offset positions
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase();
        // Being offset along Z makes the near base (100,0,0) the closest target, but the unit is too
        // far from the road nodes (on the X axis) so FindPath's "from"-side snap fails.
        var unit = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 1000));
        s.Units.Add(unit);

        InvasionOrders.AssignAdvance(s, 0, 0f); // fails, cooldown = 2h
        Assert.Null(unit.Path);

        // Add a road node near the unit and connect it (so a retry can succeed).
        // Position and destination stay the same, so cooldown expiry is the only reason for the retry.
        s.Roads.AddNode(3, new WorldPos(0, 0, 1000));
        s.Roads.AddEdge(3, 1);

        // Advance exactly 2h so the cooldown is fully consumed.
        InvasionOrders.AssignAdvance(s, 0, InvasionOrders.PathRetryFailCooldownHours);

        Assert.NotNull(unit.Path);
        Assert.NotEmpty(unit.Path);
        Assert.Equal(0f, unit.PathRetryCooldown, 3);
    }

    [Fact]
    public void AssignAdvance_cooling_down_unit_does_not_consume_path_budget()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase();

        var stuck = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(1000, 0, 0)); // unreachable
        s.Units.Add(stuck);
        InvasionOrders.AssignAdvance(s, 0, 0f); // fails, cooldown = 2h
        Assert.Null(stuck.Path);

        var reachable = new UnitInstance(2, "Tank_T1", 0, 100f, new WorldPos(10, 0, 0)); // near the road
        s.Units.Add(reachable);

        // Call with a budget of 1 before the cooldown runs out (0.5h). "stuck" should not consume any
        // cost, so "reachable" gets to use the single budget slot and obtain a path.
        InvasionOrders.AssignAdvance(s, 0, 0.5f, maxPathComputations: 1);

        Assert.Null(stuck.Path); // still cooling down, not retried
        Assert.NotNull(reachable.Path); // path was computed without the budget being stolen
        Assert.NotEmpty(reachable.Path);
    }

    [Fact]
    public void AssignAdvance_destination_change_resets_cooldown_for_immediate_retry()
    {
        var s = TwoEnemyBases(); // near(100,0,0) owner1, far(500,0,0) owner1, own(50,0,0) owner0
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase();
        // From (1000,0,0), far(500,0,0) is closer, so far becomes the target first, and pathing fails.
        var unit = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(1000, 0, 0));
        s.Units.Add(unit);

        InvasionOrders.AssignAdvance(s, 0, 0f);
        Assert.Null(unit.Path);
        Assert.Equal(InvasionOrders.PathRetryFailCooldownHours, unit.PathRetryCooldown, 3);

        // Remove the far base -> the target switches to near(100,0,0), and the PathTarget mismatch triggers ClearPath().
        s.Bases.RemoveAll(b => b.BaseId == 11);
        // Connect the unit's position to the existing network so the near base becomes reachable.
        s.Roads.AddNode(3, new WorldPos(1000, 0, 0));
        s.Roads.AddEdge(3, 1);

        // Even with dt=0 (no cooldown decay), the destination change causes ClearPath(), which
        // immediately resets PathRetryCooldown to 0, so a retry happens within this same call.
        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.NotNull(unit.Path);
        Assert.NotEmpty(unit.Path);
        Assert.Equal(100f, unit.OrderTargetPos.Value.X, 3);
    }

    // --- Task48: AssignAdvance skips units based on UnitInstance.Order ---

    [Fact]
    public void AssignAdvance_skips_units_with_Hold_order()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.Hold;
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 1f);

        Assert.False(u.OrderTargetPos.HasValue);
        Assert.Equal(UnitState.Idle, u.State);
        Assert.Null(u.Path);
    }

    [Fact]
    public void AssignAdvance_skips_units_with_RallyHold_order()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(5, 0, 5);
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 1f);

        // The AI assigns no target base at all. RallyPoint stays unchanged with State==Moving (set by the caller).
        Assert.False(u.OrderTargetPos.HasValue);
        Assert.Equal(new WorldPos(5, 0, 5).X, u.RallyPoint.Value.X, 3);
        Assert.Null(u.Path);
    }

    [Fact]
    public void AssignAdvance_still_targets_units_with_FreeAdvance_order_like_AiControlled()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.FreeAdvance;
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(UnitState.Moving, u.State);
        Assert.True(u.OrderTargetPos.HasValue);
        Assert.Equal(100f, u.OrderTargetPos.Value.X, 3); // toward the near base
    }

    [Fact]
    public void ClearPath_resets_path_retry_cooldown()
    {
        var unit = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        unit.PathRetryCooldown = 2f;
        unit.Path = new System.Collections.Generic.List<WorldPos> { new WorldPos(1, 0, 0) };

        unit.ClearPath();

        Assert.Equal(0f, unit.PathRetryCooldown, 3);
        Assert.Null(unit.Path);
    }
}
