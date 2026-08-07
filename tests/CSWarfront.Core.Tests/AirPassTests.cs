using CSWarfront.Core;
using Xunit;

/// <summary>
/// Task86 (user requests: "bombers drop their bombs and hit-and-run; fighters dogfight in flybys
/// without stopping"): tests for air units' engagement pass movement (racetrack flyover).
/// Approach -> at close range (PassTriggerDistance) arm an egress point ahead along the direction
/// of travel (PassEgressDistance) -> fly all the way to the egress point, then turn around and
/// re-enter, repeating. Damage still applies only within weapon range, so the reduced time spent
/// in range is compensated by AirCombat.PassDamageCompensation.
/// </summary>
public class AirPassTests
{
    private static WarState FighterVsTargetState(out UnitInstance fighter, string targetTypeKey, float targetX)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        LandUnitRoster.RegisterAll(s.Types);
        NavalUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);

        fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Moving;
        fighter.OrderTargetPos = new WorldPos(1000, 0, 0);
        s.Units.Add(fighter);

        var target = new UnitInstance(2, targetTypeKey, 1, 10000f, new WorldPos(targetX, 0, 0));
        s.Units.Add(target);
        return s;
    }

    [Fact]
    public void Engaging_fighter_flies_through_its_locked_target_and_sets_egress_beyond_it()
    {
        UnitInstance fighter;
        var s = FighterVsTargetState(out fighter, "TacticalBomber_T1", 100f);
        fighter.TargetId = 2; // Assumes CombatStep has already locked the target
        fighter.State = UnitState.Engaging;

        // Advance enough ticks to close to point-blank range (fighters are very fast).
        for (int i = 0; i < 200 && !fighter.AirPassEgress.HasValue; i++)
            MovementStep.Advance(s, 0.05f);

        Assert.True(fighter.AirPassEgress.HasValue, "expected an egress point to be armed near the target");
        // The egress point is PassEgressDistance beyond the target (100,0) along the direction of travel (+X).
        Assert.Equal(100f + AirCombat.PassEgressDistance, fighter.AirPassEgress.Value.X, 0);
        Assert.Equal(0f, fighter.AirPassEgress.Value.Z, 0);
    }

    [Fact]
    public void Fighter_completes_the_egress_leg_and_then_turns_back_for_another_pass()
    {
        UnitInstance fighter;
        var s = FighterVsTargetState(out fighter, "TacticalBomber_T1", 100f);
        fighter.TargetId = 2;
        fighter.State = UnitState.Engaging;

        // Advance until the egress point is armed.
        for (int i = 0; i < 200 && !fighter.AirPassEgress.HasValue; i++)
            MovementStep.Advance(s, 0.05f);
        Assert.True(fighter.AirPassEgress.HasValue);

        // Fly out the egress leg (well past the far side of the target = the "away" of hit-and-away).
        float maxX = fighter.Position.X;
        for (int i = 0; i < 400 && fighter.AirPassEgress.HasValue; i++)
        {
            MovementStep.Advance(s, 0.05f);
            if (fighter.Position.X > maxX) maxX = fighter.Position.X;
        }
        Assert.False(fighter.AirPassEgress.HasValue, "expected the egress leg to complete");
        Assert.True(maxX > 100f + AirCombat.PassEgressDistance * 0.8f,
            "expected the fighter to fly well past the target before turning (maxX=" + maxX + ")");

        // Turns around and heads back toward the target (the -X side) = the racetrack.
        float xAfterEgress = fighter.Position.X;
        for (int i = 0; i < 40; i++) MovementStep.Advance(s, 0.05f);
        Assert.True(fighter.Position.X < xAfterEgress,
            "expected the fighter to turn back toward the target for another pass");
    }

    [Fact]
    public void Egress_leg_persists_even_if_the_target_dies_mid_leg()
    {
        UnitInstance fighter;
        var s = FighterVsTargetState(out fighter, "TacticalBomber_T1", 100f);
        fighter.TargetId = 2;
        fighter.State = UnitState.Engaging;

        for (int i = 0; i < 200 && !fighter.AirPassEgress.HasValue; i++)
            MovementStep.Advance(s, 0.05f);
        Assert.True(fighter.AirPassEgress.HasValue);

        // The target was destroyed and the lock released (assumes CombatStep set TargetId=null).
        s.FindUnit(2).CurrentHP = 0f;
        s.FindUnit(2).State = UnitState.Dead;
        fighter.TargetId = null;

        // The egress leg is still flown to the end anyway (prevents jitter at the boundary).
        MovementStep.Advance(s, 0.05f);
        Assert.True(fighter.AirPassEgress.HasValue,
            "expected the egress leg to persist after the target died");
    }

    [Fact]
    public void Bomber_passes_over_a_hostile_base_in_range()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        AirUnitRoster.RegisterAll(s.Types);

        var bomber = new UnitInstance(1, "TacticalBomber_T1", 0, 100f, new WorldPos(0, 0, 0));
        bomber.State = UnitState.Moving;
        bomber.OrderTargetPos = new WorldPos(100, 0, 0); // The enemy base's position is the advance objective
        s.Units.Add(bomber);

        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(100, 0, 0));
        enemyBase.OwnerFactionId = 1;
        enemyBase.CurrentHP = 500f;
        s.Bases.Add(enemyBase);

        for (int i = 0; i < 300 && !bomber.AirPassEgress.HasValue; i++)
            MovementStep.Advance(s, 0.05f);

        Assert.True(bomber.AirPassEgress.HasValue,
            "expected the bomber to arm an egress point over the hostile base (hit and away)");
    }

    [Fact]
    public void Bomber_does_not_keep_passing_over_a_base_already_at_the_floor()
    {
        // Task88: a base that has reached HP 1 (the air floor) is no longer used as a pass anchor =
        // the bomber disengages and returns to normal objective movement (the movement-side fix for
        // the field report "keeps attacking even at HP 1").
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        AirUnitRoster.RegisterAll(s.Types);

        var bomber = new UnitInstance(1, "TacticalBomber_T1", 0, 100f, new WorldPos(0, 0, 0));
        bomber.State = UnitState.Moving;
        bomber.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(bomber);

        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(100, 0, 0));
        enemyBase.OwnerFactionId = 1;
        enemyBase.CurrentHP = 1f; // Already at the floor
        s.Bases.Add(enemyBase);

        for (int i = 0; i < 300; i++) MovementStep.Advance(s, 0.05f);

        Assert.False(bomber.AirPassEgress.HasValue); // No pass occurs
        Assert.Equal(100f, bomber.Position.X, 0);    // Hovers at the objective (the previous arrival behaviour)
    }

    [Fact]
    public void Fighter_does_not_pass_over_bases_it_cannot_attack()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        AirUnitRoster.RegisterAll(s.Types);

        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Moving;
        fighter.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(fighter);

        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(100, 0, 0));
        enemyBase.OwnerFactionId = 1;
        enemyBase.CurrentHP = 500f;
        s.Bases.Add(enemyBase);

        for (int i = 0; i < 300; i++) MovementStep.Advance(s, 0.05f);

        // Fighters cannot attack bases (Task85), so no pass occurs even over the base; the fighter hovers at the objective.
        Assert.False(fighter.AirPassEgress.HasValue);
        Assert.Equal(100f, fighter.Position.X, 0);
    }

    [Fact]
    public void Plane_with_no_combat_anchor_advances_to_objective_as_before()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        AirUnitRoster.RegisterAll(s.Types);
        var bomber = new UnitInstance(1, "TacticalBomber_T1", 0, 100f, new WorldPos(0, 0, 0));
        bomber.State = UnitState.Moving;
        bomber.OrderTargetPos = new WorldPos(50, 0, 0);
        s.Units.Add(bomber);

        for (int i = 0; i < 100; i++) MovementStep.Advance(s, 0.05f);

        Assert.Equal(50f, bomber.Position.X, 1); // Reaches the objective and stops, as before
        Assert.False(bomber.AirPassEgress.HasValue);
    }

    // --- Damage compensation ---

    [Fact]
    public void Damage_compensation_applies_to_air_but_not_land_or_kamikaze()
    {
        var types = new UnitTypeRegistry();
        LandUnitRoster.RegisterAll(types);
        AirUnitRoster.RegisterAll(types);

        Assert.Equal(AirCombat.PassDamageCompensation, AirCombat.DamageMultiplier(types.Get("AirSuperiority_T1")));
        Assert.Equal(AirCombat.PassDamageCompensation, AirCombat.DamageMultiplier(types.Get("TacticalBomber_T1")));
        Assert.Equal(1f, AirCombat.DamageMultiplier(types.Get("Tank_T1")));
        Assert.Equal(1f, AirCombat.DamageMultiplier(types.Get("SuicideDrone_T1"))); // Ramming stays a single full-damage hit
    }

    [Fact]
    public void CombatStep_applies_air_damage_compensation()
    {
        UnitInstance fighter;
        var s = FighterVsTargetState(out fighter, "TacticalBomber_T1", 50f); // Within weapon range (90)
        var target = s.FindUnit(2);
        float hpBefore = target.CurrentHP;

        CombatStep.Advance(s, 1f);

        var fighterType = s.Types.Get("AirSuperiority_T1");
        var targetType = s.Types.Get("TacticalBomber_T1");
        float expected = CombatMath.DamagePerHit(fighterType.Attack, targetType.Armor)
            * CombatMatchup.Multiplier(fighterType.Category, targetType.Category)
            * fighterType.Accuracy
            * AirCombat.PassDamageCompensation;
        Assert.Equal(expected, hpBefore - target.CurrentHP, 1);
    }
}
