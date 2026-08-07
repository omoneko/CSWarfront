using CSWarfront.Core;
using Xunit;

public class BaseCombatStepTests
{
    [Fact]
    public void Attacker_in_range_damages_hostile_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1()); // attack 40 (per in-game hour), range 60
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: disable regen (this test verifies a non-regen subject)
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 1f);
        // Task38: Tank_T1.Accuracy=0.70 < BaseCombatStep.SiegeAccuracyFloor(0.8), so the floor applies.
        // Task91: Tank Attack 40->42. dmg = DamagePerHit(42,0)=42 * dt(1) * siegeAccuracy(0.8) = 33.6 -> 100-33.6=66.4
        Assert.Equal(66.4f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Base_damage_scales_with_dt()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: disable regen (this test verifies a non-regen subject)
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 0.5f);
        // Task91: dmg = 42 * 0.5 * siegeAccuracy(0.8) = 16.8 -> 100-16.8=83.2
        Assert.Equal(83.2f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Does_not_damage_own_or_neutral_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue")); // 0-1 Neutral by default
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var neutralBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        neutralBase.OwnerFactionId = 1; neutralBase.CurrentHP = 100f; neutralBase.MaxHP = 100f; // Task89: disable regen
        s.Bases.Add(neutralBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Base_under_grace_takes_no_damage_and_grace_decreases_by_dt()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: disable regen (this test verifies a non-regen subject)
        enemyBase.CaptureGraceHours = 5f;
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.Equal(100f, s.Bases[0].CurrentHP, 3); // No damage while protected
        Assert.Equal(4f, s.Bases[0].CaptureGraceHours, 3); // Decreases by dt
    }

    [Fact]
    public void Base_takes_damage_normally_once_grace_hits_zero()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: disable regen (this test verifies a non-regen subject)
        enemyBase.CaptureGraceHours = 2f;
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f); // grace 2 -> 1 (still > 0 after the subtraction this tick, so protected)

        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
        Assert.Equal(1f, s.Bases[0].CaptureGraceHours, 3);

        BaseCombatStep.Advance(s, 1f); // grace 1 -> 0 (0 after the subtraction, so damage applies normally from this tick)

        // Task91: dmg = 42 * 1 * siegeAccuracy(0.8) = 33.6 -> 100-33.6=66.4
        Assert.Equal(66.4f, s.Bases[0].CurrentHP, 3);
    }

    // --- Task38: applying Accuracy to base sieges (0.8 floor) ---

    [Fact]
    public void Siege_accuracy_floor_boosts_artillerys_low_accuracy_against_a_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Artillery, 1)); // Attack=55 (Task91), Accuracy=0.35, Range=120
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: disable regen (this test verifies a non-regen subject)
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Artillery_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        // Artillery.Accuracy(0.35) < SiegeAccuracyFloor(0.8), so the floor of 0.8 is used instead of 0.35.
        // Task91: dmg = DamagePerHit(55,0)=55 * dt(1) * 0.8 = 44 -> 100-44=56
        // (without the floor, dmg would only have been 55*0.35=19.25: the floor keeps artillery
        // a strong siege weapon despite its low anti-unit accuracy)
        Assert.Equal(56f, s.Bases[0].CurrentHP, 3);
    }

    // --- Task42: muzzle-flash effects (ShotEvent) aim at the base position (To) ---

    [Fact]
    public void Attack_on_base_emits_shot_event_aimed_at_the_base_position()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: disable regen (this test verifies a non-regen subject)
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 0.01f); // FireCooldown defaults to 0, so exactly one shot fires the instant damage is dealt

        Assert.Single(s.RecentShots);
        var shot = s.RecentShots[0];
        Assert.Equal(ShotKind.DirectFire, shot.Kind); // Tank
        Assert.Equal(0f, shot.From.X, 3); // Attacking unit's position
        Assert.Equal(40f, shot.To.X, 3);  // The base's position (not the unit's position)
        Assert.Equal((byte)0, shot.FactionId);
        // Task43: a base is not a logical unit, so TargetId=0 (the Game layer treats this as a
        // "non-unit target" and uses the default impact height for bases). AttackerId is the
        // attacking unit's InstanceId.
        Assert.Equal(1u, shot.AttackerId);
        Assert.Equal(0u, shot.TargetId);
        // Task51: the firing unit's UnitCategory is carried on the ShotEvent so the Game layer can pick per-category firing sounds.
        Assert.Equal(UnitCategory.Tank, shot.Category);
    }

    [Fact]
    public void Base_under_grace_emits_no_shot_events()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: disable regen (this test verifies a non-regen subject)
        enemyBase.CaptureGraceHours = 5f;
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.Empty(s.RecentShots);
    }

    [Fact]
    public void Grace_never_goes_negative()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        b.OwnerFactionId = 1; b.CaptureGraceHours = 0.5f;
        s.Bases.Add(b);

        BaseCombatStep.Advance(s, 2f); // dt far exceeds the grace

        Assert.Equal(0f, s.Bases[0].CaptureGraceHours, 3);
    }

    // Task79: suicide drones perform no sustained in-range bombardment of bases (ramming attacks are
    // handled only by KamikazeStep against units/external threats; ramming bases was explicitly
    // deferred as out of scope for that task).
    [Fact]
    public void SuicideDrone_deals_no_damage_to_bases_and_emits_no_ShotEvent()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(AirUnitRoster.Get(UnitCategory.SuicideDrone, 1));
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(10, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f; enemyBase.MaxHP = 100f; // Task89: disable regen (this test verifies a non-regen subject)
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "SuicideDrone_T1", 0, 40f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
        Assert.Equal(40f, s.FindUnit(1).CurrentHP, 3);
        Assert.Empty(s.RecentShots);
    }
}
