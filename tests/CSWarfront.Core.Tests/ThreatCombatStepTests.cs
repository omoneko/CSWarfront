using CSWarfront.Core;
using Xunit;

// Task58: ThreatCombatStep — units fighting an ExternalThreat (Godzilla/Alien, owned by another mod).
// Task64: ThreatArmor was raised from 20 to 45 (Tier5 rebalance — see ThreatCombatStep.ThreatArmor's
// doc comment for the full worked arithmetic against a Tier5 army). At Tier1 this means Tank_T1's
// Attack(40) no longer even exceeds the new armor(45), so most of the tests below (which use Tank_T1
// via OneTankOneThreat purely as a "some unit is in range" fixture) now hit the DamagePerHit floor of 1:
// Arithmetic reference (Tank_T1: Attack=40, Range=60, Accuracy=0.70; ThreatCombatStep.ThreatArmor=45;
// CombatMath.DamagePerHit=max(1, attack-armor)):
//   Tank_T1: DamagePerHit(40,45)=max(1,-5)=1 -> dmg/dt=1*accuracy(0.70)=0.7/h
// The dedicated Tier5 tank-vs-infantry effectiveness comparison lives in
// ThreatArmor_makes_infantry_nearly_ineffective_but_tanks_effective below (that is where the
// "tanks stay effective, small arms stay useless" claim is actually exercised at the tier the
// rebalance targets).
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

        // dmg = DamagePerHit(40,45)=max(1,-5)=1 * dt(1) * accuracy(0.70) = 0.7 (Task64: ThreatArmor 20->45)
        Assert.Equal(999.3f, s.Threats[0].CurrentHP, 3);
    }

    [Fact]
    public void Damage_scales_linearly_with_dt()
    {
        var full = OneTankOneThreat(65f);
        ThreatCombatStep.Advance(full, 1f);
        float fullDmg = 1000f - full.Threats[0].CurrentHP; // 0.7

        var half = OneTankOneThreat(65f);
        ThreatCombatStep.Advance(half, 0.5f);
        float halfDmg = 1000f - half.Threats[0].CurrentHP; // 0.35

        Assert.Equal(fullDmg / 2f, halfDmg, 3);
    }

    [Fact]
    public void Unit_out_of_range_does_not_damage_the_threat()
    {
        var s = OneTankOneThreat(200f); // way beyond Range(60)+Radius(10)=70
        ThreatCombatStep.Advance(s, 1f);
        Assert.Equal(1000f, s.Threats[0].CurrentHP, 3);
    }

    // Task64: rebalanced for Tier5 armies (see ThreatCombatStep.ThreatArmor doc comment for the full
    // derivation). Tier5 stats via TierScaling: Attack x2.6, Accuracy x1.24 over the Tier1 base.
    //   Tank_T5:     Attack=40*2.6=104, Accuracy=0.70*1.24=0.868
    //                DamagePerHit(104,45)=59 -> dmg/h = 59*0.868 = 51.212
    //   Infantry_T5: Attack=20*2.6=52, Accuracy=0.75*1.24=0.93
    //                DamagePerHit(52,45)=max(1,7)=7 -> dmg/h = 7*0.93 = 6.51
    // Tanks remain clearly effective (~51/h) while small arms stay comparatively "trivial" (~6.5/h,
    // about 1/8th of a tank) even at the top tier — the design intent survives the rebalance.
    [Fact]
    public void ThreatArmor_makes_infantry_nearly_ineffective_but_tanks_effective()
    {
        var infantryState = new WarState();
        infantryState.Factions.Add(new Faction(0, "Red"));
        infantryState.Types.Register(LandUnitRoster.Get(UnitCategory.Infantry, 5));
        infantryState.Units.Add(new UnitInstance(1, "Infantry_T5", 0, 60f, new WorldPos(0f, 0f, 0f)));
        infantryState.Threats.Add(new ExternalThreat
        {
            Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(45f, 0f, 0f), // Range(40*1.4=56)+Radius(10) >= 45
            Radius = 10f, MaxHP = 1000f, CurrentHP = 1000f
        });
        ThreatCombatStep.Advance(infantryState, 1f);
        float infantryDmg = 1000f - infantryState.Threats[0].CurrentHP;
        Assert.Equal(1.674f, infantryDmg, 2); // Task91: Infantry 18*2.6=46.8, DamagePerHit(46.8,45)=1.8 x 0.93

        var tankState = new WarState();
        tankState.Factions.Add(new Faction(0, "Red"));
        tankState.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 5));
        tankState.Units.Add(new UnitInstance(1, "Tank_T5", 0, 100f, new WorldPos(0f, 0f, 0f)));
        tankState.Threats.Add(new ExternalThreat
        {
            Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(65f, 0f, 0f), // Range(60*1.4=84)+Radius(10) >= 65
            Radius = 10f, MaxHP = 1000f, CurrentHP = 1000f
        });
        ThreatCombatStep.Advance(tankState, 1f);
        float tankDmg = 1000f - tankState.Threats[0].CurrentHP;
        Assert.Equal(55.726f, tankDmg, 2); // Task91: Tank 42*2.6=109.2, DamagePerHit(109.2,45)=64.2 x 0.868

        // Tanks remain drastically more effective than infantry against an armored threat (~33x here, Task91).
        Assert.True(tankDmg > infantryDmg * 5f);
    }

    [Fact]
    public void CurrentHP_clamps_at_zero_and_IsDefeated_flips()
    {
        var s = OneTankOneThreat(65f, threatHp: 0.5f);
        // dmg would be 0.7 (> 0.5, Task64: ThreatArmor 20->45), must clamp instead of going negative.
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
        Assert.Equal(999.3f, s.Threats[0].CurrentHP, 3); // same 0.7 dmg as the default Hostile case (Task64)
    }

    [Fact]
    public void Default_relation_is_hostile_when_untouched()
    {
        var s = OneTankOneThreat(65f); // ThreatRelations left at its default (all Hostile)
        ThreatCombatStep.Advance(s, 1f);
        Assert.Equal(999.3f, s.Threats[0].CurrentHP, 3);
    }

    // Task79: suicide drones do not perform continuous dt-scaled fire against threats either (KamikazeStep
    // handles the single ramming strike under the same effective-range / ThreatArmor / ThreatRelations rules;
    // see KamikazeStepTests.cs).
    [Fact]
    public void SuicideDrone_deals_no_continuous_damage_to_threats_and_emits_no_ShotEvent()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(AirUnitRoster.Get(UnitCategory.SuicideDrone, 1)); // Range=20
        s.Units.Add(new UnitInstance(1, "SuicideDrone_T1", 0, 40f, new WorldPos(0f, 0f, 0f)));
        s.Threats.Add(new ExternalThreat
        {
            Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(15f, 0f, 0f),
            Radius = 10f, MaxHP = 1000f, CurrentHP = 1000f
        });

        ThreatCombatStep.Advance(s, 1f);

        Assert.Equal(1000f, s.Threats[0].CurrentHP, 3);
        Assert.Empty(s.RecentShots);
    }
}
