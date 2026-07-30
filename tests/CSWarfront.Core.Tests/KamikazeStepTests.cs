using CSWarfront.Core;
using Xunit;

// Task79: KamikazeStep — the suicide drone's dedicated "acquire -> dive -> detonate" combat flow.
// Replaces the old Task61 behaviour (CombatStep ranged fire + UnitType.IsOneShot self-destruct,
// see the migrated/removed tests in CombatStepTests.cs) after the user reported the drone looked
// like it was strafing with gunfire instead of diving into its target and exploding.
//
// Arithmetic reference (Tier1, TierScaling is identity at tier1):
//   SuicideDrone_T1: Attack=260, Range=20, Armor=0.
//   Tank_T1: Armor=10 -> DamagePerHit(260,10) = 250 (impact damage vs a unit target).
//   ThreatCombatStep.ThreatArmor=45 -> DamagePerHit(260,45) = 215 (impact damage vs an external threat).
public class KamikazeStepTests
{
    private static WarState DroneAndTank(float distance, float droneHp = 40f, float tankHp = 200f)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(AirUnitRoster.Get(UnitCategory.SuicideDrone, 1)); // Range=20, Attack=260
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));        // Armor=10
        s.Units.Add(new UnitInstance(1, "SuicideDrone_T1", 0, droneHp, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, tankHp, new WorldPos(distance, 0f, 0f)));
        return s;
    }

    [Fact]
    public void Acquires_and_locks_a_hostile_target_within_Range_without_detonating_yet()
    {
        var s = DroneAndTank(15f); // within Range(20), outside DetonateDistance(6)

        KamikazeStep.Advance(s, 1f);

        UnitInstance drone = s.FindUnit(1);
        Assert.Equal(UnitState.Engaging, drone.State);
        Assert.Equal(2u, drone.TargetId);
        Assert.Equal(40f, drone.CurrentHP, 3); // not detonated yet, still full HP
        Assert.Equal(200f, s.FindUnit(2).CurrentHP, 3); // target untouched
    }

    [Fact]
    public void Does_not_acquire_a_target_outside_Range()
    {
        var s = DroneAndTank(25f); // Range=20

        KamikazeStep.Advance(s, 1f);

        UnitInstance drone = s.FindUnit(1);
        Assert.NotEqual(UnitState.Engaging, drone.State);
        Assert.Null(drone.TargetId);
    }

    [Fact]
    public void Detonates_within_DetonateDistance_applying_full_Attack_once_and_self_destructing()
    {
        var s = DroneAndTank(5f); // within DetonateDistance(6)

        KamikazeStep.Advance(s, 1f);

        Assert.Equal(0f, s.FindUnit(1).CurrentHP, 3); // drone marked for death (single source of truth: CombatStep 2nd pass)
        Assert.Equal(200f - 250f, s.FindUnit(2).CurrentHP, 3); // DamagePerHit(260,10)=250, applied once
    }

    [Fact]
    public void Detonation_damage_is_not_scaled_by_dt()
    {
        var quick = DroneAndTank(5f);
        KamikazeStep.Advance(quick, 0.1f);

        var slow = DroneAndTank(5f);
        KamikazeStep.Advance(slow, 5f);

        // Same impact damage regardless of dt (no dt-scaling, unlike CombatStep's ranged pipeline).
        Assert.Equal(quick.FindUnit(2).CurrentHP, slow.FindUnit(2).CurrentHP, 3);
        Assert.Equal(200f - 250f, quick.FindUnit(2).CurrentHP, 3);
    }

    [Fact]
    public void Detonation_snaps_the_drone_to_the_impact_point_before_dying()
    {
        var s = DroneAndTank(5f);

        KamikazeStep.Advance(s, 1f);

        // The drone's own KillEvent.Position (produced by CombatStep's shared death-scan pass) uses
        // UnitInstance.Position, so snapping it to the target's position makes the explosion FX/sound
        // play at the impact point rather than up to DetonateDistance away.
        Assert.Equal(s.FindUnit(2).Position.X, s.FindUnit(1).Position.X, 3);
        Assert.Equal(s.FindUnit(2).Position.Z, s.FindUnit(1).Position.Z, 3);
    }

    [Fact]
    public void Never_emits_a_ShotEvent_whether_acquiring_or_detonating()
    {
        var acquiring = DroneAndTank(15f);
        KamikazeStep.Advance(acquiring, 1f);
        Assert.Empty(acquiring.RecentShots);

        var detonating = DroneAndTank(5f);
        KamikazeStep.Advance(detonating, 1f);
        Assert.Empty(detonating.RecentShots);
    }

    [Fact]
    public void Prioritizes_Nemesis_over_a_closer_ordinary_Hostile_like_TargetSearch()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Factions.Add(new Faction(2, "Green"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Relations.Set(0, 2, Relation.Nemesis);
        s.Types.Register(AirUnitRoster.Get(UnitCategory.SuicideDrone, 1));
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));
        s.Units.Add(new UnitInstance(1, "SuicideDrone_T1", 0, 40f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(8f, 0f, 0f)));  // closer, ordinary hostile
        s.Units.Add(new UnitInstance(3, "Tank_T1", 2, 200f, new WorldPos(18f, 0f, 0f))); // farther, nemesis

        KamikazeStep.Advance(s, 1f);

        Assert.Equal(3u, s.FindUnit(1).TargetId); // nemesis wins despite being farther
    }

    [Fact]
    public void Reverts_to_no_lock_when_the_target_dies_between_ticks()
    {
        var s = DroneAndTank(15f);
        KamikazeStep.Advance(s, 1f);
        Assert.Equal(UnitState.Engaging, s.FindUnit(1).State);

        // Simulate the target dying from some other source between ticks.
        s.FindUnit(2).CurrentHP = 0f;
        s.FindUnit(2).State = UnitState.Dead;

        KamikazeStep.Advance(s, 1f);

        UnitInstance drone = s.FindUnit(1);
        Assert.Equal(UnitState.Idle, drone.State); // reverted (climbs back / follows orders via MovementStep)
        Assert.Null(drone.TargetId);
    }

    // --- External threats (Godzilla/Alien) ---

    private static WarState DroneAndThreat(float distance, float radius = 5f, float threatHp = 1000f)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(AirUnitRoster.Get(UnitCategory.SuicideDrone, 1));
        s.Units.Add(new UnitInstance(1, "SuicideDrone_T1", 0, 40f, new WorldPos(0f, 0f, 0f)));
        s.Threats.Add(new ExternalThreat
        {
            Id = 7,
            Kind = ThreatKind.Kaiju,
            Position = new WorldPos(distance, 0f, 0f),
            Radius = radius,
            MaxHP = threatHp,
            CurrentHP = threatHp
        });
        return s;
    }

    [Fact]
    public void Locks_an_external_threat_when_no_unit_target_is_available()
    {
        var s = DroneAndThreat(15f); // within Range(20)+Radius(5)=25, outside DetonateDistance

        KamikazeStep.Advance(s, 1f);

        UnitInstance drone = s.FindUnit(1);
        Assert.Equal(UnitState.Engaging, drone.State);
        Assert.Equal(7u, drone.TargetThreatId);
        Assert.Null(drone.TargetId);
    }

    [Fact]
    public void Detonates_against_an_external_threat_using_ThreatArmor_once()
    {
        var s = DroneAndThreat(4f); // within DetonateDistance(6)

        KamikazeStep.Advance(s, 1f);

        Assert.Equal(0f, s.FindUnit(1).CurrentHP, 3); // drone self-destructs
        Assert.Equal(1000f - 215f, s.Threats[0].CurrentHP, 3); // DamagePerHit(260,45)=215
        Assert.Equal(s.Threats[0].Position.X, s.FindUnit(1).Position.X, 3); // snapped to impact point
    }

    [Fact]
    public void Ignores_a_threat_the_faction_has_set_to_non_hostile()
    {
        var s = DroneAndThreat(4f);
        s.ThreatRelations.Set(0, ThreatKind.Kaiju, Relation.Neutral);

        KamikazeStep.Advance(s, 1f);

        Assert.Equal(40f, s.FindUnit(1).CurrentHP, 3); // never engaged, never detonated
        Assert.Equal(1000f, s.Threats[0].CurrentHP, 3);
    }

    [Fact]
    public void Prefers_a_unit_target_over_an_external_threat_when_both_are_in_range()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(AirUnitRoster.Get(UnitCategory.SuicideDrone, 1));
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));
        s.Units.Add(new UnitInstance(1, "SuicideDrone_T1", 0, 40f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(15f, 0f, 0f)));
        s.Threats.Add(new ExternalThreat
        {
            Id = 7, Kind = ThreatKind.Kaiju, Position = new WorldPos(10f, 0f, 0f),
            Radius = 5f, MaxHP = 1000f, CurrentHP = 1000f
        });

        KamikazeStep.Advance(s, 1f);

        UnitInstance drone = s.FindUnit(1);
        Assert.Equal(2u, drone.TargetId);
        Assert.Null(drone.TargetThreatId);
    }

    // --- Integration with CombatStep's shared death-scan pass (single source of truth for Dead/KillEvent) ---

    [Fact]
    public void Detonation_kill_is_finalized_by_CombatStep_second_pass_with_KillEvent_at_impact_point()
    {
        var s = DroneAndTank(5f, droneHp: 40f, tankHp: 100f); // 100 - 250 <= 0: target dies too

        KamikazeStep.Advance(s, 1f);
        CombatStep.Advance(s, 1f); // CombatStep skips kamikaze units for ranged fire but still runs its death-scan pass

        UnitInstance drone = s.FindUnit(1);
        UnitInstance tank = s.FindUnit(2);
        Assert.Equal(UnitState.Dead, drone.State);
        Assert.Equal(UnitState.Dead, tank.State);

        // Both deaths produced a KillEvent (drone at the impact point it snapped to, target at its own position).
        Assert.Contains(s.RecentKills, k => k.FactionId == 0 && k.Category == UnitCategory.SuicideDrone);
        Assert.Contains(s.RecentKills, k => k.FactionId == 1 && k.Category == UnitCategory.Tank);

        KillEvent droneKill = s.RecentKills.Find(k => k.FactionId == 0);
        Assert.Equal(5f, droneKill.Position.X, 2); // snapped to the tank's position (the impact point)
    }
}
