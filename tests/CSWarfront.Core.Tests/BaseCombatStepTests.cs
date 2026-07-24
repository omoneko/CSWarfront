using CSWarfront.Core;
using Xunit;

public class BaseCombatStepTests
{
    [Fact]
    public void Attacker_in_range_damages_hostile_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1()); // attack 25, range 60
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1; enemyBase.CurrentHP = 100f;
        s.Bases.Add(enemyBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(75f, s.Bases[0].CurrentHP, 3); // 100-25
    }

    [Fact]
    public void Does_not_damage_own_or_neutral_base()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue")); // 0-1 Neutral 既定
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var neutralBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        neutralBase.OwnerFactionId = 1; neutralBase.CurrentHP = 100f;
        s.Bases.Add(neutralBase);
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
    }
}
