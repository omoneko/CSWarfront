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
        InvasionOrders.AssignAdvance(s, 0, 0f);
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

        InvasionOrders.AssignAdvance(s, 0, 0f);

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

        InvasionOrders.AssignAdvance(s, 0, 0f);

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

        InvasionOrders.AssignAdvance(s, 0, 0f);
        var firstPath = unit.Path;
        Assert.NotNull(firstPath);

        // Simulate the near base falling / no longer being the nearest target:
        // remove it so the unit must now aim at the far base instead.
        s.Bases.RemoveAll(b => b.BaseId == 10);
        // Extend the road graph so the far base (500,0,0) is reachable and would
        // differ from the stale PathTarget, forcing a recompute.
        s.Roads.AddNode(3, new WorldPos(500, 0, 0));
        s.Roads.AddEdge(2, 3);

        InvasionOrders.AssignAdvance(s, 0, 0f);

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

        InvasionOrders.AssignAdvance(s, 0, 0f, maxPathComputations: 1);

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

    // --- PathRetryCooldown (Task23レビューImportant) ---
    // 到達不能なユニット（道路から遠すぎる等）はFindPath失敗のたびにフルA*を再実行せず、
    // PathRetryFailCooldownHours(2h)が尽きるまで再試行しない。

    [Fact]
    public void AssignAdvance_unit_on_cooldown_is_not_retried_before_it_elapses()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase(); // nodes only at (0,0,0)/(100,0,0)
        // 道路から遠く離れた位置＝FindPathが必ず失敗する（from側のスナップ不可）。
        var unit = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(1000, 0, 0));
        s.Units.Add(unit);

        // 1回目：即座に試行され、失敗してクールダウンが立つ。
        InvasionOrders.AssignAdvance(s, 0, 0f);
        Assert.Null(unit.Path);
        Assert.Equal(InvasionOrders.PathRetryFailCooldownHours, unit.PathRetryCooldown, 3);

        // 2回目：クールダウンが尽きる前（0.5h経過）。再試行されていれば失敗により
        // ちょうど2fへリセットされるはずだが、再試行されなければ 2 - 0.5 = 1.5 のまま減衰する。
        InvasionOrders.AssignAdvance(s, 0, 0.5f);
        Assert.Null(unit.Path);
        Assert.Equal(1.5f, unit.PathRetryCooldown, 3);
    }

    [Fact]
    public void AssignAdvance_unit_is_retried_once_cooldown_elapses()
    {
        var s = TwoEnemyBases(); // near(100,0,0) is the closest hostile base from Z-offset positions
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase();
        // Z方向に離れているためnear基地(100,0,0)が最寄りの目標になるが、道路ノード(X軸上)から
        // 遠すぎてFindPathのfrom側スナップは失敗する。
        var unit = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 1000));
        s.Units.Add(unit);

        InvasionOrders.AssignAdvance(s, 0, 0f); // 失敗、クールダウン=2h
        Assert.Null(unit.Path);

        // ユニットの近くに道路ノードを追加し接続する（再試行時に成功できるようにする）。
        // 位置・目的地は変えないため、再試行はクールダウン経過のみが理由になる。
        s.Roads.AddNode(3, new WorldPos(0, 0, 1000));
        s.Roads.AddEdge(3, 1);

        // クールダウンをちょうど使い切る2h経過させる。
        InvasionOrders.AssignAdvance(s, 0, InvasionOrders.PathRetryFailCooldownHours);

        Assert.NotNull(unit.Path);
        Assert.NotEmpty(unit.Path);
        Assert.Equal(0f, unit.PathRetryCooldown, 3);
    }

    [Fact]
    public void AssignAdvance_cooling_down_unit_does_not_consume_path_budget()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase();

        var stuck = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(1000, 0, 0)); // 到達不能
        s.Units.Add(stuck);
        InvasionOrders.AssignAdvance(s, 0, 0f); // 失敗、クールダウン=2h
        Assert.Null(stuck.Path);

        var reachable = new UnitInstance(2, "Tank_T1", 0, 100f, new WorldPos(10, 0, 0)); // 道路近傍
        s.Units.Add(reachable);

        // クールダウンが尽きる前（0.5h）に予算1で呼び出す。stuckはコストを消費しないはずなので、
        // reachableが唯一の予算枠を使ってパスを得られる。
        InvasionOrders.AssignAdvance(s, 0, 0.5f, maxPathComputations: 1);

        Assert.Null(stuck.Path); // まだクールダウン中で再試行されない
        Assert.NotNull(reachable.Path); // 予算を奪われず経路計算された
        Assert.NotEmpty(reachable.Path);
    }

    [Fact]
    public void AssignAdvance_destination_change_resets_cooldown_for_immediate_retry()
    {
        var s = TwoEnemyBases(); // near(100,0,0) owner1, far(500,0,0) owner1, own(50,0,0) owner0
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Roads = SimpleRoadToNearBase();
        // 位置(1000,0,0)からは far(500,0,0) の方が近いのでまず far が目標になり、失敗する。
        var unit = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(1000, 0, 0));
        s.Units.Add(unit);

        InvasionOrders.AssignAdvance(s, 0, 0f);
        Assert.Null(unit.Path);
        Assert.Equal(InvasionOrders.PathRetryFailCooldownHours, unit.PathRetryCooldown, 3);

        // far基地を除去 → 目標が near(100,0,0) に切り替わり、PathTarget不一致でClearPath()される。
        s.Bases.RemoveAll(b => b.BaseId == 11);
        // ユニット位置を既存ネットワークへ接続し、near基地まで到達可能にする。
        s.Roads.AddNode(3, new WorldPos(1000, 0, 0));
        s.Roads.AddEdge(3, 1);

        // dt=0（クールダウン経過なし）でも、目的地変更によるClearPath()が
        // PathRetryCooldownを即座に0へリセットするため、この同じ呼び出しで再試行される。
        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.NotNull(unit.Path);
        Assert.NotEmpty(unit.Path);
        Assert.Equal(100f, unit.OrderTargetPos.Value.X, 3);
    }

    // --- Task48: UnitInstance.Order によるAssignAdvanceのスキップ ---

    [Fact]
    public void AssignAdvance_skips_units_with_Hold_order()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.Hold;
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 1f);

        Assert.False(u.OrderTargetPos.HasValue);
        Assert.Equal(UnitState.Idle, u.State);
        Assert.Null(u.Path);
    }

    [Fact]
    public void AssignAdvance_skips_units_with_RallyHold_order()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(5, 0, 5);
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 1f);

        // AIは目標基地を一切割り当てない。RallyPointもState==Movingのまま(呼び出し元が設定)不変。
        Assert.False(u.OrderTargetPos.HasValue);
        Assert.Equal(new WorldPos(5, 0, 5).X, u.RallyPoint.Value.X, 3);
        Assert.Null(u.Path);
    }

    [Fact]
    public void AssignAdvance_still_targets_units_with_FreeAdvance_order_like_AiControlled()
    {
        var s = TwoEnemyBases();
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.FreeAdvance;
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(UnitState.Moving, u.State);
        Assert.True(u.OrderTargetPos.HasValue);
        Assert.Equal(100f, u.OrderTargetPos.Value.X, 3); // near基地へ
    }

    [Fact]
    public void ClearPath_resets_path_retry_cooldown()
    {
        var unit = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        unit.PathRetryCooldown = 2f;
        unit.Path = new System.Collections.Generic.List<WorldPos> { new WorldPos(1, 0, 0) };

        unit.ClearPath();

        Assert.Equal(0f, unit.PathRetryCooldown, 3);
        Assert.Null(unit.Path);
    }
}
