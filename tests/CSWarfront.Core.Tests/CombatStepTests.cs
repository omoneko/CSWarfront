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
        var s = TwoHostileTanks(50f); // range 60 内
        CombatStep.Advance(s, 1f);
        // Task28: Tank_T1.Armor is now 10 (was 5). DamagePerHit(40,10)=30 に dt(1時間分)を乗じて
        // 相互に適用 → 100-30=70 (old arithmetic was DamagePerHit(40,5)=35 -> 100-35=65)
        Assert.Equal(70f, s.FindUnit(1).CurrentHP, 3);
        Assert.Equal(70f, s.FindUnit(2).CurrentHP, 3);
        Assert.Equal(UnitState.Engaging, s.FindUnit(1).State);
    }

    [Fact]
    public void Units_out_of_range_do_not_engage()
    {
        var s = TwoHostileTanks(100f); // range 60 外
        CombatStep.Advance(s, 1f);
        Assert.Equal(100f, s.FindUnit(1).CurrentHP, 3);
        Assert.NotEqual(UnitState.Engaging, s.FindUnit(1).State);
    }

    [Fact]
    public void Unit_dies_when_hp_reaches_zero()
    {
        var s = TwoHostileTanks(50f);
        s.FindUnit(2).CurrentHP = 15f; // 35ダメージ（dt=1）で死亡
        CombatStep.Advance(s, 1f);
        Assert.Equal(UnitState.Dead, s.FindUnit(2).State);
    }

    [Fact]
    public void Damage_scales_linearly_with_dt()
    {
        var full = TwoHostileTanks(50f);
        CombatStep.Advance(full, 1f);
        float fullDmg = 100f - full.FindUnit(1).CurrentHP; // Task28: DamagePerHit(40,10)=30 (was 35)

        var half = TwoHostileTanks(50f);
        CombatStep.Advance(half, 0.5f);
        float halfDmg = 100f - half.FindUnit(1).CurrentHP; // 15 (was 17.5)

        Assert.Equal(fullDmg / 2f, halfDmg, 3);
        // 100 - 30*0.5 = 85 (old arithmetic was 100 - 35*0.5 = 82.5, before Tank_T1.Armor changed 5->10)
        Assert.Equal(85f, half.FindUnit(1).CurrentHP, 3);
    }
}
