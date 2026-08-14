using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CSWarfront.Core;
using Xunit;

public class LandUnitRosterTests
{
    [Fact]
    public void All_yields_exactly_45_types()
    {
        List<UnitType> all = LandUnitRoster.All().ToList();
        Assert.Equal(45, all.Count); // Task101: 7 combat branches + SupplyTruck + MilitaryTrain = 9 categories x 5 tiers
    }

    [Fact]
    public void All_keys_match_Category_T_tier_format()
    {
        var pattern = new Regex("^(Infantry|MechInfantry|Apc|Tank|Artillery|DroneInfantry|AntiAir|SupplyTruck|MilitaryTrain)_T[1-5]$");
        foreach (UnitType t in LandUnitRoster.All())
        {
            Assert.Matches(pattern, t.TypeKey);
            Assert.Equal(Domain.Land, t.Domain);
        }
    }

    [Fact]
    public void All_covers_9_categories_times_5_tiers_with_no_duplicates()
    {
        var keys = new HashSet<string>();
        foreach (UnitType t in LandUnitRoster.All())
            Assert.True(keys.Add(t.TypeKey), "duplicate key: " + t.TypeKey);
        Assert.Equal(45, keys.Count); // Task101: 9 categories x 5 tiers
    }

    [Fact]
    public void Tank_T1_has_table_stats_and_calibrated_speed()
    {
        UnitType t = LandUnitRoster.Get(UnitCategory.Tank, 1);
        Assert.Equal("Tank_T1", t.TypeKey);
        Assert.Equal(140f, t.MaxHP, 3);
        Assert.Equal(42f, t.Attack, 3); // Task91: 40->42 (recalibrated toward realism)
        Assert.Equal(75f, t.Range, 3); // Task131: 60->75 (gunfights at a slightly longer distance)
        Assert.Equal(10f, t.Armor, 3);
        Assert.Equal(0f, t.SplashRadius, 3);
        Assert.Equal(60f, t.Cost, 3);
        Assert.Equal(8f, t.BuildTime, 3);
        Assert.Equal(0.70f, t.Accuracy, 3); // Task38: Tank Tier1 base accuracy
        Assert.Equal(SpeedCalibration.UnitsPerGameHourFromKmh(40f), t.Speed, 5);
    }

    // --- Task38: base value table for hit chance (Accuracy) ---

    [Theory]
    [InlineData(UnitCategory.Infantry, 0.75f)]
    [InlineData(UnitCategory.MechInfantry, 0.75f)]
    [InlineData(UnitCategory.Apc, 0.70f)]
    [InlineData(UnitCategory.Tank, 0.70f)]
    [InlineData(UnitCategory.Artillery, 0.35f)]
    [InlineData(UnitCategory.DroneInfantry, 0.85f)]
    [InlineData(UnitCategory.AntiAir, 0.60f)]
    public void Tier1_accuracy_matches_design_table(UnitCategory category, float expectedAccuracy)
    {
        UnitType t = LandUnitRoster.Get(category, 1);
        Assert.Equal(expectedAccuracy, t.Accuracy, 3);
    }

    [Fact]
    public void Artillery_T1_rebalanced_range_and_attack_are_lower_than_the_old_values()
    {
        // Task38: Artillery Tier1 Range 160->120, Attack 55->50 (nerf; accuracy 0.35 is the compensating buff).
        // Task131: 120->145, moving with the tank so armour and guns engage a little further out; still
        // below the 160 the Task38 nerf came down from.
        UnitType t = LandUnitRoster.Get(UnitCategory.Artillery, 1);
        Assert.Equal(145f, t.Range, 3);
        Assert.Equal(55f, t.Attack, 3); // Task91: 50->55
        // HP/Armor/Speed/Splash/Cost/BuildTime stay as before (unaffected by the rebalance).
        Assert.Equal(70f, t.MaxHP, 3);
        Assert.Equal(2f, t.Armor, 3);
        Assert.Equal(30f, t.SplashRadius, 3);
        Assert.Equal(70f, t.Cost, 3);
        Assert.Equal(9f, t.BuildTime, 3);
    }

    [Fact]
    public void Tank_T5_stats_equal_TierScaling_of_Tank_T1_base_values()
    {
        UnitType t5 = LandUnitRoster.Get(UnitCategory.Tank, 5);
        Assert.Equal("Tank_T5", t5.TypeKey);
        Assert.Equal(TierScaling.Hp(140f, 5), t5.MaxHP, 3);
        Assert.Equal(TierScaling.Attack(42f, 5), t5.Attack, 3);
        Assert.Equal(TierScaling.Cost(60f, 5), t5.Cost, 3);
        // Documented Tier5 multipliers applied to the Tank_T1 base table values.
        Assert.Equal(336f, t5.MaxHP, 3);   // 140 * 2.4
        Assert.Equal(109.2f, t5.Attack, 3);  // 42 * 2.6 (Task91)
        Assert.Equal(204f, t5.Cost, 3);    // 60 * 3.4
    }

    // --- Task42/Task43: base value tables for muzzle-effect interval (FireIntervalHours) and kind (ShotKind) ---
    // Task43: all intervals were lengthened across the board based on user feedback (firing intervals were too short)
    // (for Gunfire this is the interval between 3-round bursts; for DirectFire/IndirectFire it is the interval between single shots).
    [Theory]
    [InlineData(UnitCategory.Infantry, ShotKind.Gunfire, 0.30f)]
    [InlineData(UnitCategory.MechInfantry, ShotKind.Gunfire, 0.30f)]
    [InlineData(UnitCategory.Apc, ShotKind.Gunfire, 0.35f)]
    [InlineData(UnitCategory.DroneInfantry, ShotKind.Gunfire, 0.60f)]
    [InlineData(UnitCategory.AntiAir, ShotKind.Gunfire, 0.60f)]
    [InlineData(UnitCategory.Tank, ShotKind.DirectFire, 1.20f)]
    [InlineData(UnitCategory.Artillery, ShotKind.IndirectFire, 2.50f)]
    public void Tier1_shot_kind_and_fire_interval_match_design_table(UnitCategory category, ShotKind expectedKind,
        float expectedIntervalHours)
    {
        UnitType t = LandUnitRoster.Get(category, 1);
        Assert.Equal(expectedKind, t.ShotKind);
        Assert.Equal(expectedIntervalHours, t.FireIntervalHours, 3);
    }

    [Fact]
    public void Fire_interval_does_not_change_with_tier()
    {
        // Task42: FireIntervalHours is exempt from TierScaling (an intentional simplification). Same value at Tier1 and Tier5.
        UnitType t1 = LandUnitRoster.Get(UnitCategory.Tank, 1);
        UnitType t5 = LandUnitRoster.Get(UnitCategory.Tank, 5);
        Assert.Equal(t1.FireIntervalHours, t5.FireIntervalHours, 5);

        UnitType artT1 = LandUnitRoster.Get(UnitCategory.Artillery, 1);
        UnitType artT5 = LandUnitRoster.Get(UnitCategory.Artillery, 5);
        Assert.Equal(artT1.FireIntervalHours, artT5.FireIntervalHours, 5);
    }

    [Fact]
    public void Artillerys_fire_interval_is_longer_than_infantrys()
    {
        // Task42/43: artillery's indirect fire has a longer throttling interval than infantry's gunfire (2.00h vs 0.40h).
        UnitType artillery = LandUnitRoster.Get(UnitCategory.Artillery, 1);
        UnitType infantry = LandUnitRoster.Get(UnitCategory.Infantry, 1);
        Assert.True(artillery.FireIntervalHours > infantry.FireIntervalHours);
    }

    [Fact]
    public void RegisterAll_makes_all_35_types_resolvable_by_key()
    {
        var registry = new UnitTypeRegistry();
        LandUnitRoster.RegisterAll(registry);

        Assert.NotNull(registry.Get("Artillery_T3"));
        Assert.NotNull(registry.Get("Tank_T1"));
        Assert.NotNull(registry.Get("Infantry_T5"));
        Assert.Null(registry.Get("NoSuchUnit"));
    }
}


