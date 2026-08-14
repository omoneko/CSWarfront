using CSWarfront.Core;
using Xunit;

/// <summary>
/// Tests for UnitStatOverrides (Task92: external override of unit-class base values via unit-stats.xml).
/// It holds static state, so every test must clean up with Clear().
/// </summary>
public class UnitStatOverridesTests
{
    [Fact]
    public void Override_changes_roster_base_values_and_tier_scaling_applies_on_top()
    {
        try
        {
            UnitStatOverrides.Set(UnitCategory.Tank, new UnitStatOverride { Attack = 100f, Hp = 200f });

            UnitType t1 = LandUnitRoster.Get(UnitCategory.Tank, 1);
            Assert.Equal(100f, t1.Attack, 3);
            Assert.Equal(200f, t1.MaxHP, 3);
            Assert.Equal(75f, t1.Range, 3); // unspecified fields keep their defaults (Task131: tank range 75)

            UnitType t5 = LandUnitRoster.Get(UnitCategory.Tank, 5);
            Assert.Equal(TierScaling.Attack(100f, 5), t5.Attack, 3); // TierScaling is applied on top of the overridden value
        }
        finally
        {
            UnitStatOverrides.Clear();
        }
    }

    [Fact]
    public void Clear_restores_roster_defaults()
    {
        UnitStatOverrides.Set(UnitCategory.Tank, new UnitStatOverride { Attack = 999f });
        UnitStatOverrides.Clear();

        Assert.Equal(42f, LandUnitRoster.Get(UnitCategory.Tank, 1).Attack, 3);
    }

    [Fact]
    public void Overrides_apply_to_naval_and_air_rosters_too()
    {
        try
        {
            UnitStatOverrides.Set(UnitCategory.Destroyer, new UnitStatOverride { Range = 300f });
            UnitStatOverrides.Set(UnitCategory.TacticalBomber, new UnitStatOverride { FireIntervalHours = 2.0f });

            Assert.Equal(300f, NavalUnitRoster.Get(UnitCategory.Destroyer, 1).Range, 3);
            Assert.Equal(2.0f, AirUnitRoster.Get(UnitCategory.TacticalBomber, 1).FireIntervalHours, 3);
        }
        finally
        {
            UnitStatOverrides.Clear();
        }
    }

    [Fact]
    public void SpeedKmh_override_goes_through_the_kmh_calibration()
    {
        try
        {
            UnitStatOverrides.Set(UnitCategory.Tank, new UnitStatOverride { SpeedKmh = 80f });
            UnitType t = LandUnitRoster.Get(UnitCategory.Tank, 1);
            Assert.Equal(SpeedCalibration.UnitsPerGameHourFromKmh(80f), t.Speed, 4);
        }
        finally
        {
            UnitStatOverrides.Clear();
        }
    }
}
