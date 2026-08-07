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
    public void Land_attack_exceeding_regen_still_grinds_the_base_down()
    {
        // Tank_T1 siege DPS 40*0.8=32/h > regen 20/h -> keeps grinding at a net 12/h, satisfying
        // the requirement "a base is captured only when ground forces attack faster than it regenerates".
        var s = StateWithHostileBase(100f, 500f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        float expected = 100f + BaseCombatStep.BaseRegenPerHour - 42f * 0.8f; // Regen first, then attack (Task91: Tank Attack 42)
        Assert.Equal(expected, s.Bases[0].CurrentHP, 2);
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
