using CSWarfront.Core;
using Xunit;

/// <summary>
/// Task85 (user requests: "only ground forces can capture enemy bases", "fighters may target only
/// fighters, bombers, and KAIJU", "bombers may target only ground targets and KAIJU", "destroyers
/// cannot capture", "carriers are launch/landing platforms only") — tests for the targeting rules
/// and their application to BaseCombatStep/ThreatCombatStep.
/// Unit-vs-unit targeting restrictions (CanTargetDomains) are covered by TargetSearchTests instead.
/// </summary>
public class TargetingRulesTests
{
    // --- TargetingRules in isolation ---

    [Fact]
    public void Fighter_and_carrier_cannot_attack_bases_others_can()
    {
        Assert.False(TargetingRules.CanAttackBase(UnitCategory.AirSuperiority));
        Assert.False(TargetingRules.CanAttackBase(UnitCategory.Carrier));
        Assert.True(TargetingRules.CanAttackBase(UnitCategory.Tank));
        Assert.True(TargetingRules.CanAttackBase(UnitCategory.TacticalBomber));
        Assert.True(TargetingRules.CanAttackBase(UnitCategory.Destroyer));
    }

    [Fact]
    public void Only_land_attackers_can_reduce_base_hp_to_zero()
    {
        Assert.Equal(0f, TargetingRules.BaseHpFloor(Domain.Land));
        Assert.Equal(1f, TargetingRules.BaseHpFloor(Domain.Air));
        Assert.Equal(1f, TargetingRules.BaseHpFloor(Domain.Sea));
    }

    [Fact]
    public void Carrier_cannot_attack_threats_others_can()
    {
        Assert.False(TargetingRules.CanAttackThreat(UnitCategory.Carrier));
        Assert.True(TargetingRules.CanAttackThreat(UnitCategory.AirSuperiority));
        Assert.True(TargetingRules.CanAttackThreat(UnitCategory.TacticalBomber));
        Assert.True(TargetingRules.CanAttackThreat(UnitCategory.Destroyer));
        Assert.True(TargetingRules.CanAttackThreat(UnitCategory.Tank));
    }

    // --- Application to BaseCombatStep ---

    /// <summary>Omitting maxHp makes MaxHP = starting HP, so natural regeneration (Task89) never
    /// kicks in (keeps the expected values simple in tests unrelated to regen). Tests that verify
    /// regeneration pass maxHp explicitly.</summary>
    private static WarState StateWithHostileBase(float baseHp, float maxHp = -1f)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1;
        enemyBase.CurrentHP = baseHp;
        enemyBase.MaxHP = maxHp > 0f ? maxHp : baseHp;
        s.Bases.Add(enemyBase);
        return s;
    }

    [Fact]
    public void Bomber_reduces_base_hp_only_down_to_one()
    {
        var s = StateWithHostileBase(10f); // HP low enough that one tick of bomber damage would easily cross 0
        s.Types.Register(AirUnitRoster.Get(UnitCategory.TacticalBomber, 5));
        s.Units.Add(new UnitInstance(1, "TacticalBomber_T5", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 5f);

        Assert.Equal(1f, s.Bases[0].CurrentHP, 3); // Stops at 1, not 0 (capture is land-forces-only)
    }

    [Fact]
    public void Destroyer_reduces_base_hp_only_down_to_one()
    {
        var s = StateWithHostileBase(10f);
        s.Types.Register(NavalUnitRoster.Get(UnitCategory.Destroyer, 5));
        s.Units.Add(new UnitInstance(1, "Destroyer_T5", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 5f);

        Assert.Equal(1f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Land_unit_can_reduce_base_hp_to_zero()
    {
        var s = StateWithHostileBase(10f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 5f);

        Assert.Equal(0f, s.Bases[0].CurrentHP, 3); // Land forces can grind HP down to 0 = can capture
    }

    [Fact]
    public void Fighter_does_not_damage_bases_at_all()
    {
        var s = StateWithHostileBase(100f);
        s.Types.Register(AirUnitRoster.Get(UnitCategory.AirSuperiority, 1));
        s.Units.Add(new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Carrier_does_not_damage_bases_at_all()
    {
        var s = StateWithHostileBase(100f);
        s.Types.Register(NavalUnitRoster.Get(UnitCategory.Carrier, 1));
        s.Units.Add(new UnitInstance(1, "Carrier_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Air_then_land_can_finish_a_base_the_air_left_at_one_hp()
    {
        // After air grinds it down to 1, land can remove the last 1 HP and take it to 0
        // (the intended combined-arms capture flow).
        var s = StateWithHostileBase(10f);
        s.Types.Register(AirUnitRoster.Get(UnitCategory.TacticalBomber, 5));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "TacticalBomber_T5", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 5f);
        Assert.Equal(1f, s.Bases[0].CurrentHP, 3);

        s.Units.Add(new UnitInstance(2, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(0f, s.Bases[0].CurrentHP, 3);
    }

    // --- Task88: stop attacking bases that have reached the HP floor ---

    [Fact]
    public void Bomber_stops_shooting_a_base_already_at_the_floor()
    {
        // Fix for the field report "bombers keep attacking even after an enemy base's HP hits 1".
        // Against a base at the floor (1), neither damage nor firing events are produced at all
        // (no endlessly continuing pointless bombardment).
        var s = StateWithHostileBase(1f);
        s.Types.Register(AirUnitRoster.Get(UnitCategory.TacticalBomber, 1));
        s.Units.Add(new UnitInstance(1, "TacticalBomber_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 5f);

        Assert.Equal(1f, s.Bases[0].CurrentHP, 3);
        Assert.Empty(s.RecentShots); // No muzzle-flash visuals either
    }

    [Fact]
    public void Land_unit_still_finishes_a_base_at_one_hp()
    {
        // The land floor is 0, so a base at 1 HP remains a valid attack/capture target.
        var s = StateWithHostileBase(1f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.Equal(0f, s.Bases[0].CurrentHP, 3);
    }

    // --- Task89: natural regeneration of base HP ---

    [Fact]
    public void Damaged_base_slowly_regenerates_hp()
    {
        var s = StateWithHostileBase(250f, 500f); // No attackers
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(250f + BaseCombatStep.BaseRegenPerHour, s.Bases[0].CurrentHP, 2);
    }

    [Fact]
    public void Regeneration_caps_at_max_hp()
    {
        var s = StateWithHostileBase(499f, 500f); // Just below MaxHP=500
        BaseCombatStep.Advance(s, 5f);
        Assert.Equal(500f, s.Bases[0].CurrentHP, 2);
    }

    [Fact]
    public void Captured_base_at_zero_hp_does_not_regenerate()
    {
        // A base at 0 HP (awaiting capture processing) does not regenerate — if it did, the
        // capture could never complete.
        var s = StateWithHostileBase(0f, 500f);
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(0f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Land_attack_grinds_the_base_down_without_regen_healing_it_mid_siege()
    {
        // Task124: a base under fire does not repair, so the siege nets the full attack value.
        // (Before, regen was applied unconditionally in the same tick as the attack.)
        var s = StateWithHostileBase(100f, 500f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        // First tick: the base has not been hit yet, so it still repairs, then takes the hit.
        BaseCombatStep.Advance(s, 1f);
        float afterFirst = 100f + BaseCombatStep.BaseRegenPerHour - 42f * 0.8f; // Task91: Tank Attack 42
        Assert.Equal(afterFirst, s.Bases[0].CurrentHP, 2);
        Assert.True(s.Bases[0].RegenSuppressedHours > 0f);

        // From the second tick on the siege nets the full attack value - no more free repairs.
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(afterFirst - 42f * 0.8f, s.Bases[0].CurrentHP, 2);
    }

    [Fact]
    public void Lone_infantry_can_eventually_take_an_undefended_supply_depot()
    {
        // Task124 (bug report): infantry siege DPS is 18*0.8 = 14.4/h, below the 20/h regen, so with
        // unconditional regen a faction holding only a supply depot could never be beaten - the HP
        // never moved and the attackers were eventually removed as "stuck".
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var depot = new MilitaryBase(1, BaseType.SupplyDepot, new WorldPos(0, 0, 0));
        depot.OwnerFactionId = 1;
        depot.MaxHP = depot.CurrentHP = FortificationRules.DefaultMaxHP(BaseType.SupplyDepot);
        s.Bases.Add(depot);
        var infantry = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(10, 0, 0));
        infantry.State = UnitState.Moving;
        s.Units.Add(infantry);

        // 400 HP at 14.4/h needs ~28 hours of firing, which outlasts an infantry ammo load (12h), so
        // the squad is kept supplied - as ResupplyStep would inside a supply zone or via a truck.
        for (int h = 0; h < 40; h++)
        {
            infantry.Ammo = 1f;
            BaseCombatStep.Advance(s, 1f);
            Occupation.ResolveCaptures(s); // the real tick resolves captures right after combat
            StuckCleanupStep.Advance(s, 1f);
        }

        Assert.Equal((byte)0, depot.OwnerFactionId.Value);  // captured, not merely damaged
        Assert.True(infantry.IsAlive);          // and the besieger was not removed as "stuck"
    }

    [Fact]
    public void Besieging_units_are_not_despawned_as_stuck()
    {
        // Task124: base sieges never set State=Engaging, so the stall watchdog used to delete the
        // attackers standing at the enemy base.
        var s = StateWithHostileBase(100000f, 100000f); // never falls, so the siege runs long
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        tank.State = UnitState.Moving;
        s.Units.Add(tank);

        for (int h = 0; h < (int)StuckCleanupStep.DespawnAfterHours + 5; h++)
        {
            BaseCombatStep.Advance(s, 1f);
            StuckCleanupStep.Advance(s, 1f);
        }

        Assert.True(tank.IsAlive);
    }

    [Fact]
    public void Regeneration_resumes_once_the_shooting_stops()
    {
        var s = StateWithHostileBase(100f, 500f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 1f);
        float afterHit = s.Bases[0].CurrentHP;

        s.Units.Clear(); // attackers gone
        for (int h = 0; h < (int)BaseCombatStep.RegenSuppressedAfterHitHours; h++)
            BaseCombatStep.Advance(s, 1f);
        Assert.Equal(afterHit, s.Bases[0].CurrentHP, 2); // still suppressed

        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(afterHit + BaseCombatStep.BaseRegenPerHour, s.Bases[0].CurrentHP, 2);
    }

    [Fact]
    public void Weak_land_attack_below_regen_cannot_capture()
    {
        // An attack below the regen rate (one infantry: DamagePerHit(20,0)=20 x siege accuracy 0.8
        // = 16/h < 20/h) is a net gain, so the base can never be ground down.
        var s = StateWithHostileBase(100f, 500f);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Infantry, 1));
        s.Units.Add(new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.True(s.Bases[0].CurrentHP >= 100f,
            "expected regen to outpace a sub-regen attack (hp=" + s.Bases[0].CurrentHP + ")");
    }

    // --- Application to ThreatCombatStep ---

    [Fact]
    public void Carrier_does_not_damage_threats()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(NavalUnitRoster.Get(UnitCategory.Carrier, 5));
        s.Units.Add(new UnitInstance(1, "Carrier_T5", 0, 100f, new WorldPos(0, 0, 0)));
        var threat = new ExternalThreat
        {
            Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(50, 0, 0),
            Radius = 45f, MaxHP = 65000f, CurrentHP = 65000f
        };
        s.Threats.Add(threat);

        ThreatCombatStep.Advance(s, 1f);

        Assert.Equal(65000f, threat.CurrentHP, 1);
    }

    [Fact]
    public void Fighter_still_damages_threats()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(AirUnitRoster.Get(UnitCategory.AirSuperiority, 5));
        s.Units.Add(new UnitInstance(1, "AirSuperiority_T5", 0, 100f, new WorldPos(0, 0, 0)));
        var threat = new ExternalThreat
        {
            Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(50, 0, 0),
            Radius = 45f, MaxHP = 65000f, CurrentHP = 65000f
        };
        s.Threats.Add(threat);

        ThreatCombatStep.Advance(s, 1f);

        Assert.True(threat.CurrentHP < 65000f); // Fighters can attack KAIJU
    }
}
