using CSWarfront.Core;
using Xunit;

/// <summary>
/// DisasterImpactStep（Task94: MissileDisasterの着弾によるユニットダメージ）のテスト。
/// </summary>
public class DisasterImpactStepTests
{
    private static WarState StateWithTank(float x, float z, float hp = 100f)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, hp, new WorldPos(x, 0, z)));
        return s;
    }

    [Fact]
    public void Unit_in_destruction_radius_takes_heavy_damage_and_dies()
    {
        var s = StateWithTank(30f, 0f);
        int hit = DisasterImpactStep.ApplyImpact(s, 0f, 0f, 80f, 40f, false);

        Assert.Equal(1, hit);
        Assert.Equal(UnitState.Dead, s.Units[0].State);
        Assert.Single(s.RecentKills);
    }

    [Fact]
    public void Unit_in_burn_ring_takes_lighter_damage()
    {
        // 破壊半径80の外・延焼半径120の内側 → 延焼ダメージのみ
        var s = StateWithTank(100f, 0f, DisasterImpactStep.ConventionalBurnDamage + 50f);
        int hit = DisasterImpactStep.ApplyImpact(s, 0f, 0f, 80f, 120f, false);

        Assert.Equal(1, hit);
        Assert.Equal(50f, s.Units[0].CurrentHP, 1);
        Assert.NotEqual(UnitState.Dead, s.Units[0].State);
    }

    [Fact]
    public void Unit_outside_both_radii_is_untouched()
    {
        var s = StateWithTank(200f, 0f);
        int hit = DisasterImpactStep.ApplyImpact(s, 0f, 0f, 80f, 120f, false);

        Assert.Equal(0, hit);
        Assert.Equal(100f, s.Units[0].CurrentHP, 1);
    }

    [Fact]
    public void Nuclear_destruction_zone_kills_even_the_toughest_unit()
    {
        var s = StateWithTank(1000f, 0f, 100000f);
        DisasterImpactStep.ApplyImpact(s, 0f, 0f, 3720f, 5850f, true); // 150kt核の実半径

        Assert.Equal(UnitState.Dead, s.Units[0].State);
    }

    [Fact]
    public void Nuclear_burn_ring_deals_heavy_but_survivable_damage_to_high_hp_units()
    {
        var s = StateWithTank(5000f, 0f, DisasterImpactStep.NuclearBurnDamage + 100f);
        DisasterImpactStep.ApplyImpact(s, 0f, 0f, 3720f, 5850f, true);

        Assert.Equal(100f, s.Units[0].CurrentHP, 1);
        Assert.NotEqual(UnitState.Dead, s.Units[0].State);
    }

    [Fact]
    public void Damage_ignores_faction_and_dead_units_are_skipped()
    {
        var s = StateWithTank(30f, 0f);
        s.Units[0].State = UnitState.Dead;
        s.Units[0].CurrentHP = 0f;

        int hit = DisasterImpactStep.ApplyImpact(s, 0f, 0f, 80f, 40f, false);

        Assert.Equal(0, hit);
        Assert.Empty(s.RecentKills);
    }
}
