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
        // DamagePerHit(25,5)=20 を相互に適用
        Assert.Equal(80f, s.FindUnit(1).CurrentHP, 3);
        Assert.Equal(80f, s.FindUnit(2).CurrentHP, 3);
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
        s.FindUnit(2).CurrentHP = 15f; // 20ダメージで死亡
        CombatStep.Advance(s, 1f);
        Assert.Equal(UnitState.Dead, s.FindUnit(2).State);
    }
}
