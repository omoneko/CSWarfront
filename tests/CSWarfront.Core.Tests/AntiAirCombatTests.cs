using CSWarfront.Core;
using Xunit;

/// <summary>
/// Tests for Task90 (user request: "AA guns and missiles get a per-attack hit chance configured per
/// Tier, so shots can miss. Use the gun against suicide drones and the AA missile against fighters
/// and bombers").
/// </summary>
public class AntiAirCombatTests
{
    // --- hit-chance table ---

    [Fact]
    public void Hit_chances_increase_with_tier_and_stay_within_bounds()
    {
        for (byte tier = 1; tier <= 5; tier++)
        {
            float gun = AntiAirCombat.GunHitChance(tier);
            float missile = AntiAirCombat.MissileHitChance(tier);
            Assert.InRange(gun, 0.01f, 0.95f);
            Assert.InRange(missile, 0.01f, 0.95f);
            if (tier > 1)
            {
                Assert.True(gun >= AntiAirCombat.GunHitChance((byte)(tier - 1)));
                Assert.True(missile >= AntiAirCombat.MissileHitChance((byte)(tier - 1)));
            }
        }
    }

    [Fact]
    public void Weapon_selection_is_gun_for_drones_and_missile_for_planes()
    {
        Assert.False(AntiAirCombat.UsesMissileAgainst(UnitCategory.SuicideDrone));
        Assert.True(AntiAirCombat.UsesMissileAgainst(UnitCategory.AirSuperiority));
        Assert.True(AntiAirCombat.UsesMissileAgainst(UnitCategory.TacticalBomber));
    }

    [Fact]
    public void RollHit_is_deterministic_for_identical_inputs()
    {
        bool a = AntiAirCombat.RollHit(7u, 13u, 42u, 0.5f);
        bool b = AntiAirCombat.RollHit(7u, 13u, 42u, 0.5f);
        Assert.Equal(a, b);
    }

    [Fact]
    public void RollHit_rate_approximates_the_requested_chance()
    {
        // Even with the deterministic hash, rolling many times while varying the tick should make the hit rate converge to the requested value.
        const float chance = 0.6f;
        int hits = 0;
        const int n = 2000;
        for (uint t = 0; t < n; t++)
            if (AntiAirCombat.RollHit(7u, 13u, t, chance)) hits++;

        float rate = hits / (float)n;
        Assert.InRange(rate, chance - 0.05f, chance + 0.05f);
    }

    // --- discrete firing in CombatStep ---

    private static WarState AaVsAir(string targetTypeKey, out UnitInstance aa, out UnitInstance target)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        LandUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);

        aa = new UnitInstance(1, "AntiAir_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(aa);
        target = new UnitInstance(2, targetTypeKey, 1, 100000f, new WorldPos(30, 0, 0)); // within range, extremely high HP
        s.Units.Add(target);
        return s;
    }

    [Fact]
    public void AA_fires_a_sam_missile_shot_event_at_a_fighter()
    {
        var s = AaVsAir("AirSuperiority_T1", out var aa, out var target);
        var aaType = s.Types.Get("AntiAir_T1");

        CombatStep.Advance(s, aaType.FireIntervalHours); // uses up the cooldown exactly

        Assert.Single(s.RecentShots);
        Assert.Equal(ShotKind.SamMissile, s.RecentShots[0].Kind);
        Assert.Equal(UnitCategory.AntiAir, s.RecentShots[0].Category);
    }

    [Fact]
    public void AA_fires_gunfire_at_a_suicide_drone()
    {
        var s = AaVsAir("SuicideDrone_T1", out var aa, out var target);
        var aaType = s.Types.Get("AntiAir_T1");

        CombatStep.Advance(s, aaType.FireIntervalHours);

        Assert.Single(s.RecentShots);
        Assert.Equal(ShotKind.Gunfire, s.RecentShots[0].Kind);
    }

    [Fact]
    public void Missed_shots_deal_no_damage_and_hit_shots_deal_burst_damage()
    {
        // Run several ticks to observe both hits and misses, and verify the damage rule for each.
        var s = AaVsAir("AirSuperiority_T1", out var aa, out var target);
        var aaType = s.Types.Get("AntiAir_T1");
        var targetType = s.Types.Get("AirSuperiority_T1");
        float burst = CombatMath.DamagePerHit(aaType.Attack, targetType.Armor) * aaType.FireIntervalHours *
            CombatMatchup.Multiplier(UnitCategory.AntiAir, UnitCategory.AirSuperiority);

        bool sawHit = false, sawMiss = false;
        for (int i = 0; i < 60 && !(sawHit && sawMiss); i++)
        {
            float hpBefore = target.CurrentHP;
            s.RecentShots.Clear();
            s.TickCounter++; // in the real game MissileStep.Advance increments this every tick (changing the roll seed)
            CombatStep.Advance(s, aaType.FireIntervalHours); // each Advance always fires exactly one shot

            Assert.Single(s.RecentShots);
            var shot = s.RecentShots[0];
            float dealt = hpBefore - target.CurrentHP;
            if (shot.Missed)
            {
                sawMiss = true;
                Assert.Equal(0f, dealt, 2); // a miss deals no damage at all
            }
            else
            {
                sawHit = true;
                Assert.Equal(burst, dealt, 1); // a hit deals one shot's worth of damage in a single burst
            }
        }
        Assert.True(sawHit, "expected at least one hit in 60 shots");
        Assert.True(sawMiss, "expected at least one miss in 60 shots (T1 missile chance 0.55)");
    }

    [Fact]
    public void No_shot_and_no_damage_while_cooldown_is_still_running()
    {
        var s = AaVsAir("AirSuperiority_T1", out var aa, out var target);
        var aaType = s.Types.Get("AntiAir_T1");

        CombatStep.Advance(s, aaType.FireIntervalHours); // first shot (resets the cooldown)
        s.RecentShots.Clear();
        float hpAfterFirst = target.CurrentHP;

        CombatStep.Advance(s, aaType.FireIntervalHours * 0.25f); // still on cooldown

        Assert.Empty(s.RecentShots);
        Assert.Equal(hpAfterFirst, target.CurrentHP, 3); // no damage is dealt either (discrete firing only)
    }

    [Fact]
    public void AA_against_land_target_still_uses_continuous_expected_value_damage()
    {
        // Ground targets keep the old continuous model (discretization applies only against air).
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        LandUnitRoster.RegisterAll(s.Types);
        var aa = new UnitInstance(1, "AntiAir_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(aa);
        var tank = new UnitInstance(2, "Tank_T1", 1, 100000f, new WorldPos(30, 0, 0));
        s.Units.Add(tank);

        float hpBefore = tank.CurrentHP;
        CombatStep.Advance(s, 0.01f); // a dt small enough that discrete firing could not shoot on the first tick

        Assert.True(tank.CurrentHP < hpBefore, "expected continuous dt-scaled damage against a land target");
    }

    [Fact]
    public void Tick_counter_variation_changes_roll_outcomes_across_shots()
    {
        // Even with the same attacker and target, different ticks can flip hit/miss (not every shot has the same outcome).
        int distinct = 0;
        bool first = AntiAirCombat.RollHit(1u, 2u, 0u, 0.55f);
        for (uint t = 1; t < 40; t++)
            if (AntiAirCombat.RollHit(1u, 2u, t, 0.55f) != first) { distinct++; }
        Assert.True(distinct > 0, "expected outcomes to vary across ticks");
    }
}
