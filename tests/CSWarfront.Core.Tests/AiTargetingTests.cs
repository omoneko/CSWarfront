using CSWarfront.Core;
using Xunit;

public class AiTargetingTests
{
    private static WarState TwoEnemyBases()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        var near = new MilitaryBase(10, BaseType.Army, new WorldPos(100, 0, 0)); near.OwnerFactionId = 1;
        var far = new MilitaryBase(11, BaseType.Army, new WorldPos(500, 0, 0)); far.OwnerFactionId = 1;
        var own = new MilitaryBase(12, BaseType.Army, new WorldPos(50, 0, 0)); own.OwnerFactionId = 0;
        s.Bases.Add(near); s.Bases.Add(far); s.Bases.Add(own);
        return s;
    }

    [Fact]
    public void Chooses_nearest_hostile_owned_base()
    {
        var s = TwoEnemyBases();
        var t = AiTargeting.ChooseTargetBase(s, 0, new WorldPos(0, 0, 0));
        Assert.Equal((ushort)10, t.BaseId);
    }

    [Fact]
    public void Returns_null_when_no_hostile_base()
    {
        var s = TwoEnemyBases();
        s.Relations.Set(0, 1, Relation.Neutral); // 敵対解除
        Assert.Null(AiTargeting.ChooseTargetBase(s, 0, new WorldPos(0, 0, 0)));
    }

    [Fact]
    public void AssignAdvance_sets_moving_orders_for_faction_units()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        InvasionOrders.AssignAdvance(s, 0);
        Assert.Equal(UnitState.Moving, s.FindUnit(1).State);
        Assert.True(s.FindUnit(1).OrderTargetPos.HasValue);
        Assert.Equal(100f, s.FindUnit(1).OrderTargetPos.Value.X, 3); // near基地へ
    }
}
