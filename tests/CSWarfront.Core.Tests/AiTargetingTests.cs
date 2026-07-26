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

    private static RoadGraph SimpleRoadToNearBase()
    {
        // A(0,0,0) - B(100,0,0), matching "near" base position exactly.
        var g = new RoadGraph();
        g.AddNode(1, new WorldPos(0, 0, 0));
        g.AddNode(2, new WorldPos(100, 0, 0));
        g.AddEdge(1, 2);
        return g;
    }

    [Fact]
    public void AssignAdvance_with_roads_computes_path_and_records_target()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase();
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0);

        var u = s.FindUnit(1);
        Assert.NotNull(u.Path);
        Assert.NotEmpty(u.Path);
        Assert.True(u.PathTarget.HasValue);
        Assert.Equal(u.OrderTargetPos.Value.X, u.PathTarget.Value.X, 3);
    }

    [Fact]
    public void AssignAdvance_without_roads_leaves_path_null()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = null;
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0);

        var u = s.FindUnit(1);
        Assert.Equal(UnitState.Moving, u.State);
        Assert.True(u.OrderTargetPos.HasValue);
        Assert.Null(u.Path);
    }

    [Fact]
    public void AssignAdvance_clears_stale_path_when_target_base_changes()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase();
        var unit = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(unit);

        InvasionOrders.AssignAdvance(s, 0);
        var firstPath = unit.Path;
        Assert.NotNull(firstPath);

        // Simulate the near base falling / no longer being the nearest target:
        // remove it so the unit must now aim at the far base instead.
        s.Bases.RemoveAll(b => b.BaseId == 10);
        // Extend the road graph so the far base (500,0,0) is reachable and would
        // differ from the stale PathTarget, forcing a recompute.
        s.Roads.AddNode(3, new WorldPos(500, 0, 0));
        s.Roads.AddEdge(2, 3);

        InvasionOrders.AssignAdvance(s, 0);

        Assert.Equal(500f, unit.OrderTargetPos.Value.X, 3);
        Assert.Equal(500f, unit.PathTarget.Value.X, 3);
    }

    [Fact]
    public void AssignAdvance_maxPathComputations_throttles_new_path_computation()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase();
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        s.Units.Add(new UnitInstance(3, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        InvasionOrders.AssignAdvance(s, 0, maxPathComputations: 1);

        int withPath = 0;
        foreach (var u in s.Units)
            if (u.Path != null) withPath++;

        Assert.Equal(1, withPath);
        // all units still receive orders/state even if path computation was throttled
        foreach (var u in s.Units)
        {
            Assert.Equal(UnitState.Moving, u.State);
            Assert.True(u.OrderTargetPos.HasValue);
        }
    }
}
