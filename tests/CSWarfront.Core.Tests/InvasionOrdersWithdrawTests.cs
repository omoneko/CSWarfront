using CSWarfront.Core;
using Xunit;

// Task78: fix for the bug "when the advance objective (an external threat such as a KAIJU, or an
// enemy base) disappears, units should withdraw to their own base".
// Previously, InvasionOrders.AssignAdvance simply continued when both the threat
// (FindNearbyThreatToOwnTerritory) and the enemy base (AiTargeting.ChooseTargetBase) returned null,
// leaving OrderTargetPos/State/Path stuck at the values from the previous call (the reported bugs:
// units kept marching to the point where a threat had despawned, and kept marching to an enemy base
// even after it had already fallen).
// These tests verify the new rule: when there is no target, units withdraw to the nearest base
// owned by their own faction.
public class InvasionOrdersWithdrawTests
{
    [Fact]
    public void Unit_withdraws_to_own_base_once_the_threat_it_was_chasing_is_gone()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        // Threats is empty = the state after a KAIJU or similar has despawned. No enemy factions/bases exist either.
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 500f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(9999f, 0f, 9999f); // stale destination from chasing the now-gone threat
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(UnitState.Moving, u.State); // still far from its own base, so it is moving in withdrawal
        Assert.True(u.OrderTargetPos.HasValue);
        Assert.Equal(0f, u.OrderTargetPos.Value.X, 3);
        Assert.Equal(0f, u.OrderTargetPos.Value.Z, 3);
    }

    [Fact]
    public void Stale_Path_toward_the_old_target_is_cleared_so_a_new_path_can_be_computed()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 500f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(9999f, 0f, 9999f);
        u.Path = new System.Collections.Generic.List<WorldPos> { new WorldPos(9000f, 0f, 9000f) };
        u.PathTarget = new WorldPos(9999f, 0f, 9999f);
        s.Units.Add(u);
        // No Roads supplied: this test only checks that the stale path is discarded (whether a new path can be computed is out of scope).

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Null(u.Path);
        Assert.Null(u.PathTarget);
    }

    [Fact]
    public void Unit_withdraws_after_the_enemy_base_it_was_marching_on_falls_and_becomes_friendly()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(300, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(ownBase); s.Bases.Add(enemyBase);
        // Deliberately placed closer to ownBase: this makes the pre-fall destination (300, the enemy
        // base) and the post-fall withdrawal destination (0, own base) different values, so the test
        // cannot pass merely because "the old value is still there".
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(50f, 0f, 0f));
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);
        Assert.Equal(300f, s.FindUnit(1).OrderTargetPos.Value.X, 3); // marching on the enemy base, as before

        // The fall: the base was captured and is now owned by our faction (directly simulates the
        // state change equivalent to Occupation).
        enemyBase.OwnerFactionId = 0;

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var after = s.FindUnit(1);
        Assert.Equal(UnitState.Moving, after.State);
        // No enemies remain. Withdraw to the nearest own base (= ownBase, distance 50 < the captured base's distance 250).
        Assert.Equal(0f, after.OrderTargetPos.Value.X, 3);
    }

    [Fact]
    public void A_new_hostile_appearing_later_re_targets_instead_of_continuing_to_withdraw()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 0f));
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);
        Assert.Equal(0f, s.FindUnit(1).OrderTargetPos.Value.X, 3); // withdrawing home, no target yet

        // A new enemy appears.
        s.Relations.Set(0, 1, Relation.Hostile);
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(700, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(enemyBase);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(700f, s.FindUnit(1).OrderTargetPos.Value.X, 3); // stops withdrawing and turns toward the new enemy base
        Assert.Equal(UnitState.Moving, s.FindUnit(1).State);
    }

    [Fact]
    public void Withdraw_targets_the_nearest_owned_base_not_just_the_first_one()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var farBase = new MilitaryBase(1, BaseType.Army, new WorldPos(1000, 0, 0)) { OwnerFactionId = 0 };
        var nearBase = new MilitaryBase(2, BaseType.Army, new WorldPos(50, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(farBase); s.Bases.Add(nearBase); // registered with the nearer base LAST = a first-entry bias would fail this
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(50f, s.FindUnit(1).OrderTargetPos.Value.X, 3);
    }

    [Fact]
    public void Withdraw_prefers_the_headquarters_base_on_an_exact_distance_tie()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var plainBase = new MilitaryBase(1, BaseType.Army, new WorldPos(100, 0, 0)) { OwnerFactionId = 0 };
        var hqBase = new MilitaryBase(2, BaseType.Army, new WorldPos(-100, 0, 0)) { OwnerFactionId = 0, IsHeadquarters = true };
        s.Bases.Add(plainBase); s.Bases.Add(hqBase); // both are exactly 100 away from the origin
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(-100f, s.FindUnit(1).OrderTargetPos.Value.X, 3);
    }

    [Fact]
    public void Faction_with_no_bases_at_all_idles_in_place_instead_of_chasing_a_stale_target()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        // Register no bases at all: a faction with no withdrawal destination.
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 0f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(9999f, 0f, 0f);
        u.Path = new System.Collections.Generic.List<WorldPos> { new WorldPos(600f, 0f, 0f) };
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var after = s.FindUnit(1);
        Assert.Equal(UnitState.Idle, after.State);
        Assert.False(after.OrderTargetPos.HasValue);
        Assert.Null(after.Path);
    }

    [Fact]
    public void Unit_that_has_arrived_near_its_own_base_goes_Idle_instead_of_stopping_short()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        // Has already finished withdrawing to within MovementStep.CoverArrivalDistance (3f).
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(1f, 0f, 0f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(0f, 0f, 0f);
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var after = s.FindUnit(1);
        Assert.Equal(UnitState.Idle, after.State);
        Assert.False(after.OrderTargetPos.HasValue);
    }

    [Fact]
    public void FreeAdvance_units_withdraw_too_without_their_Order_being_changed()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 0f)) { Order = UnitOrder.FreeAdvance };
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(0f, s.FindUnit(1).OrderTargetPos.Value.X, 3);
        Assert.Equal(UnitOrder.FreeAdvance, s.FindUnit(1).Order);
    }

    /// <summary>Task136 (playtest report "with no objective left, crowds pile onto our own bunkers and
    /// artillery positions and then disappear"): a fortification is an emplacement for three men, not
    /// somewhere an army falls back to — and being small and numerous, one was almost always the nearest
    /// owned thing on the map.</summary>
    [Fact]
    public void Withdraw_ignores_fortifications_even_when_one_is_much_closer()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var realBase = new MilitaryBase(1, BaseType.Army, new WorldPos(1000, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(realBase);
        foreach (BaseType fortType in new[]
        {
            BaseType.Bunker, BaseType.ArtilleryPost, BaseType.AtPillbox,
            BaseType.AaPosition, BaseType.SupplyDepot, BaseType.CargoStation
        })
        {
            s.Bases.Add(new MilitaryBase((ushort)(10 + (int)fortType), fortType, new WorldPos(20, 0, 0))
            { OwnerFactionId = 0 });
        }
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(1000f, s.FindUnit(1).OrderTargetPos.Value.X, 3);
    }

    /// <summary>Task136: a faction reduced to emplacements alone has nowhere to fall back to. Standing
    /// still is right; converging on a pillbox is what produced the reported pile-up.</summary>
    [Fact]
    public void A_faction_holding_only_fortifications_stands_down_where_it_is()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Bases.Add(new MilitaryBase(1, BaseType.Bunker, new WorldPos(20, 0, 0)) { OwnerFactionId = 0 });
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 0f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(9999f, 0f, 0f);
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var after = s.FindUnit(1);
        Assert.Equal(UnitState.Idle, after.State);
        Assert.False(after.OrderTargetPos.HasValue);
    }

    [Fact]
    public void Hold_units_are_never_pulled_into_a_withdraw()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 0f)) { Order = UnitOrder.Hold };
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.False(s.FindUnit(1).OrderTargetPos.HasValue);
        Assert.Equal(UnitState.Idle, s.FindUnit(1).State);
    }
}
