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

    // --- Task61: Sea/Air units are excluded from cover-seeking movement ---

    [Fact]
    public void Advance_never_sets_CoverDestination_for_an_engaging_air_unit_even_when_cover_available()
    {
        var s = BaseState();
        AirUnitRoster.RegisterAll(s.Types);
        string key = AirUnitRoster.TypeKey(UnitCategory.AirSuperiority, 1);
        var type = s.Types.Get(key);
        var self = new UnitInstance(1, key, 0, type.MaxHP, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(30, 0, 0));
        s.Units.Add(self);
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(15, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
        Assert.False(self.CoverHold);
    }

    [Fact]
    public void Advance_never_sets_CoverDestination_for_a_sea_unit_advancing_toward_an_objective()
    {
        var s = BaseState();
        NavalUnitRoster.RegisterAll(s.Types);
        string key = NavalUnitRoster.TypeKey(UnitCategory.Destroyer, 1);
        var type = s.Types.Get(key);
        var self = new UnitInstance(1, key, 0, type.MaxHP, new WorldPos(0, 0, 0));
        s.Units.Add(self);
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(1000, 0, 0);

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(500, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_infantry_ignores_building_cover_without_friendly_armor()
    {
        // Task104: Infantry does not use building cover (eliminates the unnatural movement of
        // running under elevated roads and the like). Without friendly armor there is no cover.
        // The happy path for armor cover is verified in FortDefenseBonusTests.
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f); // even though building cover exists

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
        Assert.False(self.CoverHold);
    }

    // The old Task44 spec (what the "not_engaging" in the test name assumed) was "a unit that is not
    // engaging never holds a CoverDestination". With Task45, CoverSeekStep started giving a
    // CoverDestination to advancing (pre-engagement) units too, but only when an OrderTargetPos
    // (advance objective) exists. This test does not set OrderTargetPos, so the unit is effectively
    // Idle (no advance objective and no engagement target) = outside the scope of Mode3, and under
    // the new spec it still gets no CoverDestination.
    [Fact]
    public void Advance_does_not_set_CoverDestination_when_not_engaging_and_no_advance_target()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving; // OrderTargetPos left unset = no advance objective

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

    // Task50: re-evaluation while engaging (Mode2) is no longer driven by a cooldown; it depends
    // solely on whether the unit keeps fighting the same opponent (TargetId). No matter how far time
    // advances past any cooldown, as long as TargetId is unchanged the already-chosen CoverDestination
    // is never altered (the most important guard against frequent position changes).
    [Fact]
    public void Advance_does_not_reevaluate_while_engaging_the_same_target_even_long_after_cooldown_would_have_elapsed()
    {
        var s = BaseState(); // Task104: APC is the subject for building-cover verification (infantry only uses armor cover)
        var self = AddUnit(s, 1, UnitCategory.Apc, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(30, 0, 0), 5f); // within the APC's search radius of 45

        CoverSeekStep.Advance(s, 0.01f);
        Assert.True(self.CoverDestination.HasValue);
        var firstDestination = self.CoverDestination.Value;
        Assert.Equal(enemy.InstanceId, self.CoverTargetId);

        // Even after removing all cover and advancing time far beyond Task52's MaxEngageHoldHours (3h),
        // the CoverDestination stays unchanged as long as the unit keeps engaging the same opponent
        // (MaxEngageHoldHours only affects MovementStep's movement resumption; it does not release
        // CoverSeekStep's own re-evaluation lock).
        s.Cover = new CoverMap();
        CoverSeekStep.Advance(s, CoverSeekStep.MaxEngageHoldHours * 10f);

        Assert.True(self.CoverDestination.HasValue);
        Assert.Equal(firstDestination.X, self.CoverDestination.Value.X, 3);
    }

    // Task50: the decision that no cover was found is itself remembered while engaging the same
    // opponent, and the search is not redone every tick (CoverDestination stays stably null =
    // MovementStep does not move the unit during this time).
    [Fact]
    public void Advance_remembers_no_cover_found_for_the_same_target_and_does_not_keep_retrying()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        s.Cover = new CoverMap(); // no cover available at all

        CoverSeekStep.Advance(s, 0.01f);
        Assert.False(self.CoverDestination.HasValue);
        Assert.Equal(enemy.InstanceId, self.CoverTargetId); // decision recorded even though nothing was found

        // Cover appears later, but the target hasn't changed -> still must not re-pick.
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);
        CoverSeekStep.Advance(s, CoverSeekStep.MaxEngageHoldHours * 10f);

        Assert.False(self.CoverDestination.HasValue);
    }

    // Task50 TDD: "an engaging unit re-evaluates cover only when its target changes".
    // The moment the opponent changes, re-evaluation happens immediately (without waiting for any cooldown).
    [Fact]
    public void Advance_reevaluates_immediately_when_the_engaged_target_changes()
    {
        var s = BaseState(); // Task104: APC is the subject for building-cover verification
        var self = AddUnit(s, 1, UnitCategory.Apc, 0, new WorldPos(0, 0, 0));
        var enemy1 = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        var enemy2 = AddUnit(s, 3, UnitCategory.Infantry, 1, new WorldPos(-100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy1.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(30, 0, 0), 5f); // within the APC's search radius of 45

        CoverSeekStep.Advance(s, 0.01f);
        Assert.True(self.CoverDestination.HasValue);
        var firstDestination = self.CoverDestination.Value;
        Assert.Equal(enemy1.InstanceId, self.CoverTargetId);

        // Switch to a different target (still engaging) with a cover map that no longer has
        // anything near the old destination -> must re-pick right away, not wait for a cooldown.
        self.TargetId = enemy2.InstanceId;
        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(-40, 0, 0), 5f);
        CoverSeekStep.Advance(s, 0.01f); // tiny dt: a cooldown-based design would still be "in cooldown" here

        Assert.True(self.CoverDestination.HasValue);
        Assert.NotEqual(firstDestination.X, self.CoverDestination.Value.X, 3);
        Assert.Equal(enemy2.InstanceId, self.CoverTargetId);
    }

    // Task50/52: an engaging unit that has already arrived (or has no cover at all) does not move
    // at all until MaxEngageHoldHours is reached (integration check of the CoverSeekStep ->
    // MovementStep interplay).
    [Fact]
    public void Advance_engaging_unit_with_no_cover_does_not_move_within_MaxEngageHoldHours()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.OrderTargetPos = new WorldPos(1000, 0, 0); // stray advance target; must be ignored while engaging
        // s.Cover left null -> no cover map at all

        CoverSeekStep.Advance(s, 0.01f);
        MovementStep.Advance(s, 1f);
        Assert.Equal(0f, self.Position.X, 3);

        // Still well within MaxEngageHoldHours(3h) total.
        CoverSeekStep.Advance(s, 1f);
        MovementStep.Advance(s, 1f);
        Assert.Equal(0f, self.Position.X, 3);
        Assert.False(self.CoverDestination.HasValue);
    }

    // Task52 rule3 TDD: a unit that keeps engaging the same opponent while never finding any cover
    // (a search radius exists but there are no candidates / no CoverMap at all) resumes advancing
    // toward OrderTargetPos once MaxEngageHoldHours is exceeded, even while still Engaging (the
    // direct fix for the "cannot finish off the enemy and gets stuck forever" bug).
    [Fact]
    public void Advance_engaging_unit_with_no_cover_resumes_advancing_after_MaxEngageHoldHours()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.OrderTargetPos = new WorldPos(1000, 0, 0);
        // s.Cover left null -> no cover map at all, so this unit can never find cover to hide behind.

        CoverSeekStep.Advance(s, 0.01f);
        MovementStep.Advance(s, 1f);
        Assert.Equal(0f, self.Position.X, 3); // still pinned, well under MaxEngageHoldHours

        // Cross the MaxEngageHoldHours(3h) threshold while still engaging the same target.
        CoverSeekStep.Advance(s, CoverSeekStep.MaxEngageHoldHours);
        MovementStep.Advance(s, 1f);

        Assert.True(self.Position.X > 0f, "expected the unit to resume advancing once MaxEngageHoldHours elapsed");
        Assert.Equal(UnitState.Engaging, self.State); // Task52: may keep "fighting" (state-wise) while moving
    }

    // --- Task45: IsInFriendlyTerritory ---

    [Fact]
    public void IsInFriendlyTerritory_true_when_inside_own_base_influence_radius()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var home = new MilitaryBase(1, BaseType.Army, new WorldPos(100, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(home); // default InfluenceRadius is 500, distance is 100, so inside the radius

        Assert.True(CoverSeekStep.IsInFriendlyTerritory(s, self));
    }

    [Fact]
    public void IsInFriendlyTerritory_false_when_outside_own_base_influence_radius()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var home = new MilitaryBase(1, BaseType.Army, new WorldPos(1000, 0, 0)) { OwnerFactionId = 0, InfluenceRadius = 500f };
        s.Bases.Add(home); // distance 1000 > InfluenceRadius 500

        Assert.False(CoverSeekStep.IsInFriendlyTerritory(s, self));
    }

    [Fact]
    public void IsInFriendlyTerritory_ignores_enemy_base_influence_radius()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(50, 0, 0)) { OwnerFactionId = 1, InfluenceRadius = 500f };
        s.Bases.Add(enemyBase); // inside the radius, but it belongs to an enemy faction, so it does not count as friendly territory

        Assert.False(CoverSeekStep.IsInFriendlyTerritory(s, self));
    }

    [Fact]
    public void IsInFriendlyTerritory_false_when_no_bases_exist()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));

        Assert.False(CoverSeekStep.IsInFriendlyTerritory(s, self));
    }

    // --- Task45: the three-mode branching of Advance ---

    [Fact]
    public void Advance_gives_no_CoverDestination_when_inside_friendly_territory_even_if_engaging()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        var home = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(home); // self is inside its own base's influence radius

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f); // cover exists, but should be ignored because the unit is inside friendly territory

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
        Assert.False(self.CoverHold);
    }

    [Fact]
    public void Advance_gives_forward_progressing_CoverDestination_when_advancing_outside_territory()
    {
        var s = BaseState(); // Task104: APC is the subject for building-cover verification
        var self = AddUnit(s, 1, UnitCategory.Apc, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(200, 0, 0); // direction of the advance objective (enemy base)

        s.Cover = new CoverMap();
        // A candidate within MaxCoverDetour(40) that also satisfies MinForwardProgress(5).
        s.Cover.Add(new WorldPos(30, 0, 0), 5f); // cover located in the direction of advance

        float distBefore = self.Position.HorizontalDistanceTo(self.OrderTargetPos.Value);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.True(self.CoverDestination.HasValue);
        float distAfter = self.CoverDestination.Value.HorizontalDistanceTo(self.OrderTargetPos.Value);
        Assert.True(distAfter < distBefore - CoverSeekStep.MinForwardProgress,
            "expected the chosen cover to make forward progress toward the objective");
        // Task52: cover reached during a bounding advance is also "held" to hide for a short while
        // (advance resumes automatically via MovementStep.MaxCoverHoldHours; the unit never stops indefinitely).
        Assert.True(self.CoverHold);
    }

    // Task52 rule1: cover requiring a detour beyond MaxCoverDetour from the current position is rejected even when it satisfies the forward-progress condition.
    [Fact]
    public void Advance_rejects_forward_progressing_cover_beyond_MaxCoverDetour()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(200, 0, 0);

        s.Cover = new CoverMap();
        // Satisfies the forward-progress condition (MinForwardProgress), but its distance from the current position exceeds MaxCoverDetour(40).
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_leaves_CoverDestination_null_when_no_forward_progressing_cover_exists()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(200, 0, 0); // the advance objective is in the +X direction

        s.Cover = new CoverMap();
        // Cover only exists on the opposite side (-X) of the advance direction = choosing any of it moves away from the objective.
        s.Cover.Add(new WorldPos(-50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_engaging_outside_territory_uses_target_as_threat_and_MovementStep_halts_at_it()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Tank, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Tank, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        // s.Bases left empty -> outside friendly territory

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(30, 0, 0), 6f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.True(self.CoverDestination.HasValue);
        Assert.True(self.CoverHold);

        // Verify that MovementStep carries the unit to that same standing spot and halts there (integration check).
        MovementStep.Advance(s, 10f); // large enough dt to guarantee arrival
        Assert.True(self.Position.HorizontalDistanceTo(self.CoverDestination.Value) <= MovementStep.CoverArrivalDistance);

        var posAfterArrival = self.Position;
        // Task52: advancing by a time well short of MaxCoverHoldHours(1h) keeps the unit halted, since it is in hold mode.
        MovementStep.Advance(s, 0.2f);
        Assert.Equal(posAfterArrival.X, self.Position.X, 2);
        Assert.Equal(posAfterArrival.Z, self.Position.Z, 2);
        Assert.True(self.CoverDestination.HasValue); // not cleared because it is in hold mode
    }

    // Task52 rule2 TDD: a unit that has stayed stationary with CoverHold==true releases its
    // CoverDestination once MaxCoverHoldHours is exceeded and resumes moving even while still
    // Engaging (integration check with MovementStepTests).
    [Fact]
    public void Advance_engaging_unit_resumes_advancing_after_MaxCoverHoldHours_even_while_still_engaging()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Tank, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Tank, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.OrderTargetPos = new WorldPos(1000, 0, 0);

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(30, 0, 0), 6f);

        CoverSeekStep.Advance(s, 0.01f);
        MovementStep.Advance(s, 10f); // arrive at the cover point
        Assert.True(self.Position.HorizontalDistanceTo(self.CoverDestination.Value) <= MovementStep.CoverArrivalDistance);

        // Advance well past MaxCoverHoldHours(1h) while remaining locked onto the same target
        // (CoverSeekStep must not re-pick cover; MovementStep alone must force the release).
        CoverSeekStep.Advance(s, 0.01f);
        MovementStep.Advance(s, MovementStep.MaxCoverHoldHours + 0.5f);

        Assert.False(self.CoverDestination.HasValue);

        // One more tick: still Engaging, but now free to move toward its objective.
        var beforeX = self.Position.X;
        CoverSeekStep.Advance(s, 0.01f);
        MovementStep.Advance(s, 1f);
        Assert.True(self.Position.X > beforeX, "expected the unit to resume advancing after the cover-hold cap");
        Assert.Equal(UnitState.Engaging, self.State);
    }

    [Fact]
    public void Advance_never_sets_CoverDestination_for_artillery_in_bounding_mode_either()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Artillery, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(200, 0, 0);
        // s.Bases left empty -> outside friendly territory, so Mode3 would otherwise apply

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    // --- Task48: Hold/RallyHold units are excluded from cover-seeking movement ---

    [Fact]
    public void Advance_never_sets_CoverDestination_for_engaging_unit_with_Hold_order()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.Order = UnitOrder.Hold;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_never_sets_CoverDestination_for_engaging_unit_with_RallyHold_order()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.Order = UnitOrder.RallyHold;
        self.RallyPoint = new WorldPos(0, 0, 0);

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_clears_stray_CoverDestination_for_unit_switched_to_Hold_order()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.CoverDestination = new WorldPos(10, 0, 10); // leftover from before the Hold command
        self.CoverHold = true;
        self.Order = UnitOrder.Hold;

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
        Assert.False(self.CoverHold);
    }

    // --- Task52: fix for the "advance toward the enemy stronghold stalls partway" bug ---

    // rule1 TDD: Mode3 (advancing) performs no cover search at all until a CoverUseIntervalHours
    // interval has elapsed (even if a good new candidate appears, it is not picked up during the
    // cooldown = the unit simply keeps following its road path).
    [Fact]
    public void Advance_skips_cover_seeking_while_CoverUseIntervalHours_cooldown_is_unexpired()
    {
        var s = BaseState(); // Task104: APC is the subject for building-cover verification
        var self = AddUnit(s, 1, UnitCategory.Apc, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(200, 0, 0);
        s.Cover = new CoverMap(); // no candidates yet

        // First tick: cooldown was 0 (default), so this attempt happens immediately and starts a
        // fresh CoverUseIntervalHours cooldown. No candidate exists yet, so nothing is picked.
        CoverSeekStep.Advance(s, 0.01f);
        Assert.False(self.CoverDestination.HasValue);

        // A perfectly good candidate appears, but the per-unit cooldown is still running. Nudge the
        // unit a little closer each tick (as MovementStep would along the road path) so the rule4
        // stall watchdog stays satisfied and doesn't interfere with what this test checks; the nudge
        // is small enough to stay well short of the candidate so forward-progress math is unaffected.
        s.Cover.Add(new WorldPos(30, 0, 0), 5f);
        self.Position = new WorldPos(6, 0, 0);
        CoverSeekStep.Advance(s, 1f); // well under the remaining CoverUseIntervalHours(3f)
        Assert.False(self.CoverDestination.HasValue,
            "expected cover-seeking to be skipped entirely while the cooldown is unexpired");

        // Cross the remaining cooldown -> now it is allowed to evaluate again and picks the candidate.
        self.Position = new WorldPos(12, 0, 0);
        CoverSeekStep.Advance(s, CoverSeekStep.CoverUseIntervalHours);
        Assert.True(self.CoverDestination.HasValue);
    }

    // rule4 TDD: if the distance to OrderTargetPos does not shrink by at least StallEpsilon within
    // StallTimeoutHours, the unit is considered stalled and CoverSuppressionRemaining is set. For
    // that duration afterwards, cover searching itself is ignored completely even when good
    // candidates exist, leaving only the road path in charge. The suppression itself naturally
    // decays back to 0 over time ("re-enable"), after which cover can be evaluated normally again.
    [Fact]
    public void Advance_watchdog_suppresses_cover_after_a_stall_and_re_enables_it_later()
    {
        var s = BaseState(); // Task104: APC is the subject for building-cover verification
        var self = AddUnit(s, 1, UnitCategory.Apc, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(1000, 0, 0);
        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(30, 0, 0), 5f); // valid, forward-progressing, in-detour candidate

        // Tick 1: initializes the watchdog checkpoint (no MovementStep is invoked in this test, so
        // the unit's Position never actually advances -> it will look "stalled" from here on).
        CoverSeekStep.Advance(s, 0.01f);
        Assert.Equal(0f, self.CoverSuppressionRemaining);

        // No progress for exactly StallTimeoutHours -> the watchdog trips.
        CoverSeekStep.Advance(s, CoverSeekStep.StallTimeoutHours);
        Assert.True(self.CoverSuppressionRemaining > 0f, "expected the stall watchdog to trip");

        // While suppressed, cover is ignored entirely (even a stray pre-existing CoverDestination
        // gets cleared, and a perfectly good candidate is not picked up).
        self.CoverDestination = new WorldPos(999, 0, 0);
        CoverSeekStep.Advance(s, 0.5f); // small step: stays within the stall window, no re-trigger
        Assert.False(self.CoverDestination.HasValue,
            "expected cover to stay suppressed for the rest of the CoverSuppressedHours window");

        // Simulate the unit actually marching (as required: "ignores cover entirely and marches
        // straight along its path") by moving it closer to the objective, which both breaks the
        // stall and, combined with enough elapsed time, lets the suppression window fully elapse.
        self.Position = new WorldPos(500, 0, 0);
        CoverSeekStep.Advance(s, CoverSeekStep.CoverSuppressedHours);
        Assert.Equal(0f, self.CoverSuppressionRemaining); // expected suppression to re-enable itself over time
    }

    // rule4 TDD: as long as the distance keeps steadily shrinking, the watchdog never trips even
    // when a large dt worth of a full StallTimeoutHours is passed at once (because the checkpoint
    // and timer are reset every time progress is made).
    [Fact]
    public void Advance_steady_progress_never_trips_the_stall_watchdog()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(1000, 0, 0);
        s.Cover = new CoverMap(); // isolate the watchdog from cover-picking noise

        float x = 0f;
        for (int tick = 0; tick < 5; tick++)
        {
            x += 200f; // far more than StallEpsilon(5) of progress each window
            self.Position = new WorldPos(x, 0, 0);
            CoverSeekStep.Advance(s, CoverSeekStep.StallTimeoutHours); // a full stall-timeout window each time
            Assert.Equal(0f, self.CoverSuppressionRemaining);
        }
    }

    // rule2/3/5: a player's Hold order is completely unaffected by every timer and watchdog added
    // in Task52 (no matter how far time advances, neither cover movement nor timer accumulation
    // ever occurs, and the unit remains stationary at all times).
    [Fact]
    public void Advance_Hold_order_unit_is_unaffected_by_every_Task52_timer()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.Order = UnitOrder.Hold;
        self.OrderTargetPos = new WorldPos(1000, 0, 0); // stray; Hold must still never move

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, CoverSeekStep.MaxEngageHoldHours * 5f); // far beyond every Task52 threshold
        MovementStep.Advance(s, 10f);

        Assert.False(self.CoverDestination.HasValue);
        Assert.Equal(0f, self.EngageHoldTimer);
        Assert.Equal(0f, self.StallTimer);
        Assert.Equal(0f, self.CoverSuppressionRemaining);
        Assert.Equal(0f, self.Position.X, 3); // Hold: never moves, regardless of any Task52 timer
    }

    // --- Task77: countermeasure for the "ground units can walk into the sea" bug (never let land units pick an underwater standing spot) ---

    // Fake sampler that treats the entire map as water (in this coordinate space, any cover spot chosen anywhere is always treated as underwater).
    private class AlwaysWater : IWaterSampler
    {
        public bool IsWater(float x, float z) { return true; }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return true; }
    }

    // Mode2 (engaging): if the standing spot found by TryFindBestCover is underwater, treat it the
    // same as if no cover was found (no CoverDestination/CoverHold is set; however, CoverTargetId is
    // still recorded per the existing spec = no re-search every tick while engaging the same opponent).
    [Fact]
    public void Advance_rejects_engaging_cover_candidate_located_in_water()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f); // a candidate that would normally be chosen if it were on land
        s.Water = new AlwaysWater();

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
        Assert.False(self.CoverHold);
        Assert.Equal(enemy.InstanceId, self.CoverTargetId);
    }

    // Mode3 (bounding advance while moving): even a candidate that satisfies both the forward-progress
    // condition (MinForwardProgress) and the detour condition (MaxCoverDetour) is rejected if it is
    // underwater, falling back to preferring the road path (Path/OrderTargetPos).
    [Fact]
    public void Advance_rejects_bounding_cover_candidate_located_in_water()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(200, 0, 0);

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(30, 0, 0), 5f); // same as Advance_gives_forward_progressing_CoverDestination...: a candidate that would be chosen if on land
        s.Water = new AlwaysWater();

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
        Assert.False(self.CoverHold);
    }

    // Regression check: when state.Water is not supplied (null), no water check is performed at all
    // and candidates are chosen normally as before (the same "safe fallback when not supplied"
    // pattern as RoadGraph/Cover/Height).
    [Fact]
    public void Advance_still_picks_cover_when_WaterSampler_is_absent()
    {
        var s = BaseState(); // Task104: APC is the subject for building-cover verification
        var self = AddUnit(s, 1, UnitCategory.Apc, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(30, 0, 0), 5f); // within the APC's search radius of 45
        Assert.Null(s.Water);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.True(self.CoverDestination.HasValue);
    }
}
