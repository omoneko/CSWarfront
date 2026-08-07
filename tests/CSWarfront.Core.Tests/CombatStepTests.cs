using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class CombatStepTests
{
    private static WarState TwoHostileTanks(float distance)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(distance, 0f, 0f)));
        return s;
    }

    [Fact]
    public void Units_in_range_damage_each_other()
    {
        var s = TwoHostileTanks(50f); // within range 60
        CombatStep.Advance(s, 1f);
        // Task28: Tank_T1.Armor is now 10 (was 5). DamagePerHit(40,10)=30.
        // Task38: Tank_T1.Accuracy=0.70 (no drone synergy applies to non-Artillery), matchup Tank->Tank=1.0.
        // Task91: Tank Attack 40->42. DamagePerHit(42,10)=32, dmg = 32 * dt(1) * matchup(1.0) * accuracy(0.70) = 22.4 -> 100-22.4=77.6
        // (old arithmetic, pre-accuracy: 100-30=70)
        Assert.Equal(77.6f, s.FindUnit(1).CurrentHP, 3);
        Assert.Equal(77.6f, s.FindUnit(2).CurrentHP, 3);
        Assert.Equal(UnitState.Engaging, s.FindUnit(1).State);
    }

    [Fact]
    public void Units_out_of_range_do_not_engage()
    {
        var s = TwoHostileTanks(100f); // outside range 60
        CombatStep.Advance(s, 1f);
        Assert.Equal(100f, s.FindUnit(1).CurrentHP, 3);
        Assert.NotEqual(UnitState.Engaging, s.FindUnit(1).State);
    }

    [Fact]
    public void Unit_dies_when_hp_reaches_zero()
    {
        var s = TwoHostileTanks(50f);
        // Task38: dmg = DamagePerHit(40,10)=30 * matchup(1.0) * accuracy(0.70) = 21 (dt=1) kills it.
        s.FindUnit(2).CurrentHP = 15f;
        CombatStep.Advance(s, 1f);
        Assert.Equal(UnitState.Dead, s.FindUnit(2).State);
    }

    // --- Task35: awarding the kill reward (Research.KillReward) ---

    [Fact]
    public void Killing_a_unit_awards_research_points_to_the_killers_faction()
    {
        var s = TwoHostileTanks(50f);
        // Task38: dies this tick (dmg = DamagePerHit(40,10)=30 * accuracy(0.70) = 21 >= 15)
        s.FindUnit(2).CurrentHP = 15f;
        CombatStep.Advance(s, 1f);

        // Tank_T1.Cost = 60, KillRewardRate = 0.5 -> 30
        Assert.Equal(30f, s.FindFaction(0).ResearchPoints, 3); // unit 1 (Red) got the kill
        Assert.Equal(0f, s.FindFaction(1).ResearchPoints, 3);  // Blue lost its unit, no reward
    }

    [Fact]
    public void Killing_a_unit_does_not_award_research_points_when_target_survives()
    {
        var s = TwoHostileTanks(50f); // both survive this tick
        CombatStep.Advance(s, 1f);

        Assert.Equal(0f, s.FindFaction(0).ResearchPoints, 3);
        Assert.Equal(0f, s.FindFaction(1).ResearchPoints, 3);
    }

    [Fact]
    public void Damage_scales_linearly_with_dt()
    {
        var full = TwoHostileTanks(50f);
        CombatStep.Advance(full, 1f);
        // Task38: DamagePerHit(40,10)=30 * matchup(1.0) * accuracy(0.70) = 21 (dt=1)
        // (old arithmetic, pre-accuracy: 30)
        float fullDmg = 100f - full.FindUnit(1).CurrentHP;

        var half = TwoHostileTanks(50f);
        CombatStep.Advance(half, 0.5f);
        float halfDmg = 100f - half.FindUnit(1).CurrentHP; // 10.5 (old arithmetic, pre-accuracy: 15)

        Assert.Equal(fullDmg / 2f, halfDmg, 3);
        // Task91: 100 - 32*0.70*0.5 = 88.8
        Assert.Equal(88.8f, half.FindUnit(1).CurrentHP, 3);
    }

    // --- Task29: applying CombatMatchup ---

    [Fact]
    public void DroneInfantry_deals_double_damage_against_tank()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.DroneInfantry, 1)); // Attack=30, Range=90
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));          // Armor=10, Range=60
        // Drone(id1) attacks Tank(id2). distance=50: within Drone's range(90) and Tank's range(60),
        // so both engage each other this tick.
        s.Units.Add(new UnitInstance(1, "DroneInfantry_T1", 0, 200f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(50f, 0f, 0f)));

        CombatStep.Advance(s, 1f);

        // DamagePerHit(Drone.Attack=30, Tank.Armor=10) = 20, × CombatMatchup(Drone->Tank)=2.0 × dt(1)
        // × DroneInfantry.Accuracy(0.85, Task38; DroneInfantry is not Artillery so gets no synergy bonus,
        //   just its own base accuracy) = 34 -> 200-34=166 (old arithmetic, pre-accuracy: 200-40=160)
        Assert.Equal(166f, s.FindUnit(2).CurrentHP, 3);
    }

    [Fact]
    public void Infantry_deals_reduced_damage_against_tank()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Infantry, 1)); // Attack=20, Range=40
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));     // Armor=10, Range=60
        // Infantry(id1) attacks Tank(id2). distance=30: within Infantry's range(40) and Tank's range(60).
        s.Units.Add(new UnitInstance(1, "Infantry_T1", 0, 200f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(30f, 0f, 0f)));

        CombatStep.Advance(s, 1f);

        // Task91: Infantry Attack 20->18. DamagePerHit(18,10)=8, × CombatMatchup(Infantry->Tank)=0.4 × dt(1)
        // × Infantry.Accuracy(0.75) = 2.4 -> 200-2.4=197.6
        Assert.Equal(197.6f, s.FindUnit(2).CurrentHP, 3);
    }

    [Fact]
    public void Matchup_damage_scales_with_dt()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.DroneInfantry, 1));
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));
        s.Units.Add(new UnitInstance(1, "DroneInfantry_T1", 0, 200f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(50f, 0f, 0f)));

        CombatStep.Advance(s, 0.5f);

        // DamagePerHit(30,10)=20 × 2.0 × dt(0.5) × accuracy(0.85, Task38) = 17
        // (old arithmetic, pre-accuracy: 200-20=180)
        Assert.Equal(183f, s.FindUnit(2).CurrentHP, 3);
    }

    // --- Task38: applying hit chance (Accuracy), drone spotting-support synergy ---

    [Fact]
    public void Artillery_alone_deals_reduced_expected_damage_due_to_low_accuracy()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Artillery, 1)); // Attack=50, Range=120, Accuracy=0.35
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));     // Armor=10
        s.Units.Add(new UnitInstance(1, "Artillery_T1", 0, 200f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(100f, 0f, 0f))); // out of Tank's range(60), only Artillery fires

        CombatStep.Advance(s, 1f);

        // Task91: Artillery Attack 50->55. Derive the expected value from the formula (a hardcoded literal breaks every time the rates are retuned).
        float expectedAlone = 200f - CombatMath.DamagePerHit(55f, 10f) * 0.7f * 0.35f;
        Assert.Equal(expectedAlone, s.FindUnit(2).CurrentHP, 2);
    }

    [Fact]
    public void Artillery_with_nearby_friendly_drone_deals_much_more_expected_damage()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Artillery, 1));
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));
        s.Types.Register(LandUnitRoster.Get(UnitCategory.DroneInfantry, 1));
        s.Units.Add(new UnitInstance(1, "Artillery_T1", 0, 200f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(100f, 0f, 0f)));
        // Friendly DroneInfantry within CombatSynergy.DroneSpotterRadius(150) of the Artillery (id1),
        // but placed at x=5 so it is itself out of its own attack range (90) of the Tank (distance 95)
        // and out of the Tank's range (60) too — it purely spots, it does not also join the fight
        // (which would otherwise add its own Drone->Tank damage on top and confound this assertion).
        s.Units.Add(new UnitInstance(3, "DroneInfantry_T1", 0, 100f, new WorldPos(5f, 0f, 0f)));

        CombatStep.Advance(s, 1f);

        // effective accuracy = min(0.95, 0.35 + DroneSpotterAccuracyBonus(0.5)) = 0.85 (Task91: Attack 55)
        // (versus 190.2 without the drone above: the synergy visibly raises artillery damage)
        float expectedSpotted = 200f - CombatMath.DamagePerHit(55f, 10f) * 0.7f * 0.85f;
        Assert.Equal(expectedSpotted, s.FindUnit(2).CurrentHP, 2);
    }

    // --- Task42: throttling of muzzle-fire effects (ShotEvent) ---

    [Fact]
    public void Unit_with_no_target_emits_no_shots()
    {
        var s = TwoHostileTanks(100f); // outside range 60, neither engages
        CombatStep.Advance(s, 1f);
        Assert.Empty(s.RecentShots);
    }

    [Fact]
    public void Firing_unit_emits_exactly_one_shot_event_on_the_first_tick_it_deals_damage()
    {
        var s = TwoHostileTanks(50f); // within range 60, both engage
        CombatStep.Advance(s, 0.01f); // FireCooldown defaults to 0, so exactly one shot is emitted the instant damage is dealt

        var fromUnit1 = s.RecentShots.FindAll(e => e.FactionId == 0);
        Assert.Single(fromUnit1);
        Assert.Equal(ShotKind.DirectFire, fromUnit1[0].Kind); // Tank
        Assert.Equal(0f, fromUnit1[0].From.X, 3);
        Assert.Equal(0f, fromUnit1[0].From.Z, 3);
        Assert.Equal(50f, fromUnit1[0].To.X, 3); // position of the target (unit2)
        Assert.Equal((byte)0, fromUnit1[0].FactionId);
        // Task43: the attacker/target InstanceIds are carried on the ShotEvent so the Game layer can
        // compute the height correction of the firing position (model center height).
        Assert.Equal(1u, fromUnit1[0].AttackerId); // unit1 (self)
        Assert.Equal(2u, fromUnit1[0].TargetId);   // unit2 (target)
        // Task51: the firing unit's UnitCategory is carried on the ShotEvent so the Game layer can pick per-branch firing sounds.
        Assert.Equal(UnitCategory.Tank, fromUnit1[0].Category);
    }

    [Fact]
    public void Firing_unit_emits_at_most_one_shot_per_its_own_fire_interval()
    {
        // Task43: Tank.FireIntervalHours = 0.90h (LandUnitRoster, extended from the old 0.25h).
        // Advance by dt=0.31h nine times = 2.79h total (= 3.1 intervals).
        // dt=0.31 is deliberately chosen so it does not divide FireIntervalHours(0.90) evenly: the
        // fire/no-fire decision always lands on values clearly away from zero, so there is no worry
        // about floating-point rounding error wobbling the decision at the zero boundary
        // (keeping this robust as a test of a deterministic simulation).
        var s = TwoHostileTanks(50f);
        int shotsFromUnit1 = 0;
        for (int i = 0; i < 9; i++)
        {
            s.RecentShots.Clear();
            CombatStep.Advance(s, 0.31f);
            shotsFromUnit1 += s.RecentShots.FindAll(e => e.FactionId == 0).Count;
        }

        // FireCooldown progression (unit1, Tank_T1, FireIntervalHours=0.90):
        //   step1: 0-0.31=-0.31<=0 -> fires, reset 0.90
        //   step2: 0.90-0.31=0.59  step3: 0.59-0.31=0.28  step4: 0.28-0.31=-0.03<=0 -> fires, reset 0.90
        //   step5: 0.59  step6: 0.28  step7: -0.03<=0 -> fires, reset 0.90  step8: 0.59  step9: 0.28
        // 3 shots in total (no randomness, deterministic: the same values every run).
        Assert.Equal(3, shotsFromUnit1);
    }

    [Fact]
    public void RecentShots_is_capped_at_MaxRecentShotsPerTick_even_with_a_huge_battle()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());

        // Create far more engaging pairs than WarState.MaxRecentShotsPerTick(200). Each pair is within
        // range of each other and far enough from every other pair that TargetSearch only picks its
        // own pair partner (no interference).
        const int pairs = 130; // 130 pairs × 2 units = 260 units, expecting up to 520 shot "requests" across both directions
        uint nextId = 1;
        for (int p = 0; p < pairs; p++)
        {
            float baseX = p * 1000f;
            s.Units.Add(new UnitInstance(nextId++, "Tank_T1", 0, 100f, new WorldPos(baseX, 0f, 0f)));
            s.Units.Add(new UnitInstance(nextId++, "Tank_T1", 1, 100f, new WorldPos(baseX + 50f, 0f, 0f)));
        }

        CombatStep.Advance(s, 0.01f);

        Assert.Equal(WarState.MaxRecentShotsPerTick, s.RecentShots.Count);
    }

    // --- Task51: kill events (KillEvent) are queued in the same tick as the death determination ---

    [Fact]
    public void Killing_a_unit_emits_exactly_one_kill_event_at_the_victims_position()
    {
        var s = TwoHostileTanks(50f); // within range 60, both engage
        s.FindUnit(2).CurrentHP = 1f; // whittled down so a single hit this tick reliably kills it

        CombatStep.Advance(s, 1f);

        Assert.Equal(UnitState.Dead, s.FindUnit(2).State);
        Assert.Single(s.RecentKills);
        var kill = s.RecentKills[0];
        Assert.Equal(50f, kill.Position.X, 3); // position of the destroyed unit2
        Assert.Equal((byte)1, kill.FactionId); // faction of the destroyed unit2 (Blue)
        Assert.Equal(UnitCategory.Tank, kill.Category); // Task53: category of the destroyed unit2 (Tank_T1)
    }

    // Task53: verify that the UnitCategory used for the infantry kill-sound omission check is
    // correctly looked up from the destroyed unit's UnitType (the premise of the Infantry/DroneInfantry
    // check in the Game layer's CombatFx.SpawnKillSounds).
    [Fact]
    public void Killing_an_infantry_unit_emits_a_kill_event_with_Infantry_category()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Types.Register(MvpUnitTypes.Infantry_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0f, 0f, 0f)));
        var victim = new UnitInstance(2, "Infantry_T1", 1, 1f, new WorldPos(50f, 0f, 0f));
        s.Units.Add(victim);

        CombatStep.Advance(s, 1f);

        Assert.Equal(UnitState.Dead, s.FindUnit(2).State);
        Assert.Single(s.RecentKills);
        Assert.Equal(UnitCategory.Infantry, s.RecentKills[0].Category);
    }

    [Fact]
    public void Unit_that_survives_the_tick_emits_no_kill_event()
    {
        var s = TwoHostileTanks(50f); // within range 60, both survive with 100 HP
        CombatStep.Advance(s, 1f);
        Assert.Empty(s.RecentKills);
    }

    [Fact]
    public void RecentKills_is_capped_at_MaxRecentKillsPerTick_even_with_a_huge_battle()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());

        // Create far more engaging pairs than WarState.MaxRecentKillsPerTick(200) and set one side of
        // each pair to 1 HP, so that all pairs can die simultaneously in the same tick (the same layout
        // pattern as the RecentShots cap verification). Only the 1-HP unit of each pair dies (the other
        // survives at 100 HP and, due to attack ordering, takes no counterattack either), so actually
        // exceeding the cap of 200 requires more than 200 pairs.
        const int pairs = 250;
        uint nextId = 1;
        for (int p = 0; p < pairs; p++)
        {
            float baseX = p * 1000f;
            s.Units.Add(new UnitInstance(nextId++, "Tank_T1", 0, 100f, new WorldPos(baseX, 0f, 0f)));
            var victim = new UnitInstance(nextId++, "Tank_T1", 1, 1f, new WorldPos(baseX + 50f, 0f, 0f));
            s.Units.Add(victim);
        }

        CombatStep.Advance(s, 1f);

        Assert.Equal(WarState.MaxRecentKillsPerTick, s.RecentKills.Count);
    }

    // --- Task52: Core-level verification that faction-relation changes made in the Options screen take effect on the simulation immediately ---
    // TargetSearch/CombatStep/BaseCombatStep/AiTargeting are all gated on Relation.Hostile, so
    // switching a pair to Neutral/Allied should make even units that had been fighting each other
    // immediately start ignoring each other. MilitaryManager.TrySetRelation is a thin wrapper that
    // merely delegates to Core.RelationMatrix.Set, so here we call WarState.Relations.Set directly
    // and verify the same code path.

    [Fact]
    public void Flipping_a_hostile_pair_to_Neutral_makes_units_stop_engaging_each_other()
    {
        var s = TwoHostileTanks(50f); // within range 60, Hostile so they engage

        // First confirm that, while still hostile, they do engage and deal damage (precondition check).
        var warmup = TwoHostileTanks(50f);
        CombatStep.Advance(warmup, 1f);
        Assert.True(warmup.FindUnit(1).CurrentHP < 100f, "sanity check: hostile units should engage");

        // Mimic the operation of selecting "Neutral" in the Options screen by switching the two
        // factions' relation to Neutral (the same RelationMatrix.Set that MilitaryManager.TrySetRelation actually calls).
        s.Relations.Set(0, 1, Relation.Neutral);

        CombatStep.Advance(s, 1f);

        Assert.Equal(100f, s.FindUnit(1).CurrentHP, 3);
        Assert.Equal(100f, s.FindUnit(2).CurrentHP, 3);
        Assert.NotEqual(UnitState.Engaging, s.FindUnit(1).State);
        Assert.NotEqual(UnitState.Engaging, s.FindUnit(2).State);
    }

    [Fact]
    public void Flipping_a_hostile_pair_to_Allied_makes_units_stop_engaging_each_other()
    {
        var s = TwoHostileTanks(50f); // within range 60, Hostile so they engage

        s.Relations.Set(0, 1, Relation.Allied);

        CombatStep.Advance(s, 1f);

        Assert.Equal(100f, s.FindUnit(1).CurrentHP, 3);
        Assert.Equal(100f, s.FindUnit(2).CurrentHP, 3);
        Assert.NotEqual(UnitState.Engaging, s.FindUnit(1).State);
        Assert.NotEqual(UnitState.Engaging, s.FindUnit(2).State);
    }

    // Task52: even a pair that was already engaging (State==Engaging) when switched to Neutral
    // returns to Idle immediately on the next tick and deals no further damage to each other
    // (direct confirmation of the user requirement "stop fighting when changed from hostile to
    // neutral/allied"; this holds automatically because CombatStep re-evaluates via TargetSearch every tick).
    [Fact]
    public void Already_engaging_units_stop_fighting_the_tick_after_relation_becomes_Neutral()
    {
        var s = TwoHostileTanks(50f);
        CombatStep.Advance(s, 1f); // both start Engaging and take damage
        Assert.Equal(UnitState.Engaging, s.FindUnit(1).State);
        float hpAfterFirstHit = s.FindUnit(1).CurrentHP;
        Assert.True(hpAfterFirstHit < 100f);

        s.Relations.Set(0, 1, Relation.Neutral);
        CombatStep.Advance(s, 1f);

        Assert.Equal(hpAfterFirstHit, s.FindUnit(1).CurrentHP, 3); // no further damage
        Assert.Equal(UnitState.Idle, s.FindUnit(1).State);
    }

    // --- Task61: the domain filter actually takes effect in CombatStep ---

    [Fact]
    public void Tank_does_not_engage_hostile_fighter_in_range_even_though_in_range()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));           // Range=60, CanTargetDomains=Land
        s.Types.Register(AirUnitRoster.Get(UnitCategory.AirSuperiority, 1));  // Domain=Air
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "AirSuperiority_T1", 1, 100f, new WorldPos(30f, 0f, 0f)));

        CombatStep.Advance(s, 1f);

        Assert.Equal(100f, s.FindUnit(2).CurrentHP, 3); // Tank never targeted the fighter
        Assert.NotEqual(UnitState.Engaging, s.FindUnit(1).State);
    }

    [Fact]
    public void AntiAir_engages_hostile_fighter_in_range()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.AntiAir, 1));        // Range=120, CanTargetDomains=Land|Air
        s.Types.Register(AirUnitRoster.Get(UnitCategory.AirSuperiority, 1));
        s.Units.Add(new UnitInstance(1, "AntiAir_T1", 0, 100f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "AirSuperiority_T1", 1, 100f, new WorldPos(30f, 0f, 0f)));

        CombatStep.Advance(s, 1f);

        Assert.True(s.FindUnit(2).CurrentHP < 100f, "AntiAir should have damaged the fighter");
        Assert.Equal(UnitState.Engaging, s.FindUnit(1).State);
    }

    // --- Task79: suicide drones (formerly: Task61's "fire within range, then self-destruct" scheme via UnitType.IsOneShot) ---
    // The old tests SuicideDrone_dies_immediately_after_dealing_damage /
    // SuicideDrone_that_never_finds_a_target_does_not_die assumed that CombatStep handled the
    // entire suicide-drone engagement (target selection, damage application, self-destruction).
    // Task79 changed CombatStep to skip suicide drones entirely (which the test below verifies),
    // and the engagement logic itself was moved wholesale to KamikazeStepTests.cs (target lock,
    // dive, ramming detonation).

    [Fact]
    public void CombatStep_skips_SuicideDrone_entirely_no_ranged_damage_no_ShotEvent()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(AirUnitRoster.Get(UnitCategory.SuicideDrone, 1)); // Range=20
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));
        s.Units.Add(new UnitInstance(1, "SuicideDrone_T1", 0, 40f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(10f, 0f, 0f)));

        CombatStep.Advance(s, 1f);

        Assert.Equal(40f, s.FindUnit(1).CurrentHP, 3); // drone untouched by CombatStep
        Assert.Equal(200f, s.FindUnit(2).CurrentHP, 3); // CombatStep never applied ranged damage
        Assert.NotEqual(UnitState.Engaging, s.FindUnit(1).State); // CombatStep never locked a target
        Assert.Empty(s.RecentShots); // no gunfire/tracer ShotEvent ever emitted by CombatStep for it
    }
}
