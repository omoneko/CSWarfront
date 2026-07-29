using CSWarfront.Core;
using Xunit;

// Task58: ThreatCombatStep — units fighting an ExternalThreat (Godzilla/Alien, owned by another mod).
// Arithmetic reference (Tank_T1: Attack=40, Range=60, Accuracy=0.70; Infantry_T1: Attack=20, Range=40,
// Accuracy=0.75; ThreatCombatStep.ThreatArmor=20; CombatMath.DamagePerHit=max(1, attack-armor)):
//   Tank:     DamagePerHit(40,20)=20 -> dmg/dt=20*accuracy(0.70)=14/h
//   Infantry: DamagePerHit(20,20)=max(1,0)=1  -> dmg/dt=1*accuracy(0.75)=0.75/h
// i.e. infantry deals ~1/19th of a tank's DPS against a threat — "small arms nearly useless".
public class ThreatCombatStepTests
{
    private static WarState OneTankOneThreat(float distance, float radius = 10f, float threatHp = 1000f)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0f, 0f, 0f)));
        s.Threats.Add(new ExternalThreat
        {
            Id = 1,
            Kind = ThreatKind.Kaiju,
            Position = new WorldPos(distance, 0f, 0f),
            Radius = radius,
            MaxHP = threatHp,
            CurrentHP = threatHp
        });
        return s;
    }

    [Fact]
    public void Unit_in_range_damages_the_threat_scaled_by_dt_and_accuracy()
    {
        var s = OneTankOneThreat(65f); // Tank.Range(60) + Radius(10) = 70 >= 65
        ThreatCombatStep.Advance(s, 1f);

        // dmg = DamagePerHit(40,20)=20 * dt(1) * accuracy(0.70) = 14
        Assert.Equal(986f, s.Threats[0].CurrentHP, 3);
    }

    [Fact]
    public void Damage_scales_linearly_with_dt()
    {
        var full = OneTankOneThreat(65f);
        ThreatCombatStep.Advance(full, 1f);
        float fullDmg = 1000f - full.Threats[0].CurrentHP; // 14

        var half = OneTankOneThreat(65f);
        ThreatCombatStep.Advance(half, 0.5f);
        float halfDmg = 1000f - half.Threats[0].CurrentHP; // 7

        Assert.Equal(fullDmg / 2f, halfDmg, 3);
    }

    [Fact]
    public void Unit_out_of_range_does_not_damage_the_threat()
    {
        var s = OneTankOneThreat(200f); // way beyond Range(60)+Radius(10)=70
        ThreatCombatStep.Advance(s, 1f);
        Assert.Equal(1000f, s.Threats[0].CurrentHP, 3);
    }

    [Fact]
    public void ThreatArmor_makes_infantry_nearly_ineffective_but_tanks_effective()
    {
        var infantryState = new WarState();
        infantryState.Factions.Add(new Faction(0, "Red"));
        infantryState.Types.Register(MvpUnitTypes.Infantry_T1());
        infantryState.Units.Add(new UnitInstance(1, "Infantry_T1", 0, 60f, new WorldPos(0f, 0f, 0f)));
        infantryState.Threats.Add(new ExternalThreat
        {
            Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(45f, 0f, 0f), // Range(40)+Radius(10)=50 >= 45
            Radius = 10f, MaxHP = 1000f, CurrentHP = 1000f
        });
        ThreatCombatStep.Advance(infantryState, 1f);
        float infantryDmg = 1000f - infantryState.Threats[0].CurrentHP;
        Assert.Equal(0.75f, infantryDmg, 3);

        var tankState = OneTankOneThreat(65f);
        ThreatCombatStep.Advance(tankState, 1f);
        float tankDmg = 1000f - tankState.Threats[0].CurrentHP;
        Assert.Equal(14f, tankDmg, 3);

        // Tanks are drastically more effective than infantry against an armored threat (~19x here).
        Assert.True(tankDmg > infantryDmg * 15f);
    }

    [Fact]
    public void CurrentHP_clamps_at_zero_and_IsDefeated_flips()
    {
        var s = OneTankOneThreat(65f, threatHp: 5f);
        // dmg would be 14 (> 5), must clamp instead of going negative.
        ThreatCombatStep.Advance(s, 1f);

        Assert.Equal(0f, s.Threats[0].CurrentHP, 3);
        Assert.True(s.Threats[0].IsDefeated);
    }

    [Fact]
    public void Defeated_threats_stay_in_the_list_and_take_no_further_damage()
    {
        var s = OneTankOneThreat(65f, threatHp: 0f);
        ThreatCombatStep.Advance(s, 1f);

        Assert.Single(s.Threats); // Core does not remove it; Game layer observes/removes it
        Assert.Equal(0f, s.Threats[0].CurrentHP, 3);
    }

    [Fact]
    public void Threats_never_damage_units()
    {
        var s = OneTankOneThreat(65f);
        for (int i = 0; i < 10; i++)
            ThreatCombatStep.Advance(s, 1f);

        Assert.Equal(100f, s.FindUnit(1).CurrentHP, 3);
    }

    [Fact]
    public void Firing_unit_emits_a_shot_event_aimed_at_the_threat_position_with_TargetId_zero()
    {
        var s = OneTankOneThreat(65f);
        ThreatCombatStep.Advance(s, 0.01f); // FireCooldown starts at 0, so damage on the first tick always fires

        Assert.Single(s.RecentShots);
        var shot = s.RecentShots[0];
        Assert.Equal(65f, shot.To.X, 3);
        Assert.Equal(1u, shot.AttackerId);
        Assert.Equal(0u, shot.TargetId); // threats are not logical units, same convention as base attacks
        Assert.Equal(ShotKind.DirectFire, shot.Kind); // Tank
    }

    // --- Task59: WarState.ThreatRelations gating ---

    [Fact]
    public void Neutral_relation_to_the_threat_kind_prevents_damage()
    {
        var s = OneTankOneThreat(65f);
        s.ThreatRelations.Set(0, ThreatKind.Kaiju, Relation.Neutral);
        ThreatCombatStep.Advance(s, 1f);
        Assert.Equal(1000f, s.Threats[0].CurrentHP, 3);
    }

    [Fact]
    public void Allied_relation_to_the_threat_kind_prevents_damage()
    {
        var s = OneTankOneThreat(65f);
        s.ThreatRelations.Set(0, ThreatKind.Kaiju, Relation.Allied);
        ThreatCombatStep.Advance(s, 1f);
        Assert.Equal(1000f, s.Threats[0].CurrentHP, 3);
    }

    [Fact]
    public void Nemesis_relation_to_the_threat_kind_still_damages_it()
    {
        var s = OneTankOneThreat(65f);
        s.ThreatRelations.Set(0, ThreatKind.Kaiju, Relation.Nemesis);
        ThreatCombatStep.Advance(s, 1f);
        Assert.Equal(986f, s.Threats[0].CurrentHP, 3); // same 14 dmg as the default Hostile case
    }

    [Fact]
    public void Default_relation_is_hostile_when_untouched()
    {
        var s = OneTankOneThreat(65f); // ThreatRelations left at its default (all Hostile)
        ThreatCombatStep.Advance(s, 1f);
        Assert.Equal(986f, s.Threats[0].CurrentHP, 3);
    }
}
