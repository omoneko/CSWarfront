using CSWarfront.Core;
using Xunit;

// Task78: 「進撃先(KAIJU等の外部脅威/敵拠点)が消えたら、ユニットは自拠点へ撤収する」不具合の修正。
// 従来のInvasionOrders.AssignAdvanceは、脅威(FindNearbyThreatToOwnTerritory)も敵基地
// (AiTargeting.ChooseTargetBase)もどちらもnullを返すケースで単に continue しており、
// OrderTargetPos/State/Pathが前回呼び出し時の値のまま取り残されていた（脅威が自然消滅した後も
// 消えた地点へ進み続ける、敵拠点を陥落させた後もその場所へ進み続ける、として報告された不具合）。
// 本テストは「対象なし」の際に自勢力の最寄り所有基地へ撤収する新しいルールを検証する。
public class InvasionOrdersWithdrawTests
{
    [Fact]
    public void Unit_withdraws_to_own_base_once_the_threat_it_was_chasing_is_gone()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        // Threatsは空＝KAIJU等が自然消滅した後の状態。敵勢力/敵基地も存在しない。
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 500f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(9999f, 0f, 9999f); // 消滅した脅威を追い続けていた古い目的地
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(UnitState.Moving, u.State); // まだ自拠点から遠いので撤収移動中
        Assert.True(u.OrderTargetPos.HasValue);
        Assert.Equal(0f, u.OrderTargetPos.Value.X, 3);
        Assert.Equal(0f, u.OrderTargetPos.Value.Z, 3);
    }

    [Fact]
    public void Stale_Path_toward_the_old_target_is_cleared_so_a_new_path_can_be_computed()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 500f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(9999f, 0f, 9999f);
        u.Path = new System.Collections.Generic.List<WorldPos> { new WorldPos(9000f, 0f, 9000f) };
        u.PathTarget = new WorldPos(9999f, 0f, 9999f);
        s.Units.Add(u);
        // Roadsを供給しない: このテストは「古い経路が捨てられるか」だけを見る（新しい経路計算の成否は問わない）。

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Null(u.Path);
        Assert.Null(u.PathTarget);
    }

    [Fact]
    public void Unit_withdraws_after_the_enemy_base_it_was_marching_on_falls_and_becomes_friendly()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(300, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(ownBase); s.Bases.Add(enemyBase);
        // 意図的にownBase寄りに置く: 陥落前の目的地(300, 敵基地)と陥落後の撤収先(0, 自拠点)が
        // 異なる値になるようにし、「単に古い値が残っているだけ」では通らないテストにする。
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(50f, 0f, 0f));
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);
        Assert.Equal(300f, s.FindUnit(1).OrderTargetPos.Value.X, 3); // marching on the enemy base, as before

        // 陥落: 基地が占領されて自勢力の所有になった（Occupation相当の状態変化を直接シミュレート）。
        enemyBase.OwnerFactionId = 0;

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var after = s.FindUnit(1);
        Assert.Equal(UnitState.Moving, after.State);
        // 敵はもういない。最寄りの自拠点(=ownBase、距離50 < capturedBaseの距離250)へ撤収する。
        Assert.Equal(0f, after.OrderTargetPos.Value.X, 3);
    }

    [Fact]
    public void A_new_hostile_appearing_later_re_targets_instead_of_continuing_to_withdraw()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 0f));
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);
        Assert.Equal(0f, s.FindUnit(1).OrderTargetPos.Value.X, 3); // withdrawing home, no target yet

        // 新たな敵が出現する。
        s.Relations.Set(0, 1, Relation.Hostile);
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(700, 0, 0)) { OwnerFactionId = 1 };
        s.Bases.Add(enemyBase);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(700f, s.FindUnit(1).OrderTargetPos.Value.X, 3); // 撤収をやめ、新しい敵基地へ向き直す
        Assert.Equal(UnitState.Moving, s.FindUnit(1).State);
    }

    [Fact]
    public void Withdraw_targets_the_nearest_owned_base_not_just_the_first_one()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var farBase = new MilitaryBase(1, BaseType.Army, new WorldPos(1000, 0, 0)) { OwnerFactionId = 0 };
        var nearBase = new MilitaryBase(2, BaseType.Army, new WorldPos(50, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(farBase); s.Bases.Add(nearBase); // 登録順は「近い方が後」＝先頭バイアスでは通らない
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(50f, s.FindUnit(1).OrderTargetPos.Value.X, 3);
    }

    [Fact]
    public void Withdraw_prefers_the_headquarters_base_on_an_exact_distance_tie()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var plainBase = new MilitaryBase(1, BaseType.Army, new WorldPos(100, 0, 0)) { OwnerFactionId = 0 };
        var hqBase = new MilitaryBase(2, BaseType.Army, new WorldPos(-100, 0, 0)) { OwnerFactionId = 0, IsHeadquarters = true };
        s.Bases.Add(plainBase); s.Bases.Add(hqBase); // 両方とも原点からちょうど100離れている
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(-100f, s.FindUnit(1).OrderTargetPos.Value.X, 3);
    }

    [Fact]
    public void Faction_with_no_bases_at_all_idles_in_place_instead_of_chasing_a_stale_target()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        // 基地を1つも登録しない: 撤収先が存在しない勢力。
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 0f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(9999f, 0f, 0f);
        u.Path = new System.Collections.Generic.List<WorldPos> { new WorldPos(600f, 0f, 0f) };
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var after = s.FindUnit(1);
        Assert.Equal(UnitState.Idle, after.State);
        Assert.False(after.OrderTargetPos.HasValue);
        Assert.Null(after.Path);
    }

    [Fact]
    public void Unit_that_has_arrived_near_its_own_base_goes_Idle_instead_of_stopping_short()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        // 既にMovementStep.CoverArrivalDistance(3f)以内まで撤収し終えている。
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(1f, 0f, 0f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(0f, 0f, 0f);
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        var after = s.FindUnit(1);
        Assert.Equal(UnitState.Idle, after.State);
        Assert.False(after.OrderTargetPos.HasValue);
    }

    [Fact]
    public void FreeAdvance_units_withdraw_too_without_their_Order_being_changed()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 0f)) { Order = UnitOrder.FreeAdvance };
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.Equal(0f, s.FindUnit(1).OrderTargetPos.Value.X, 3);
        Assert.Equal(UnitOrder.FreeAdvance, s.FindUnit(1).Order);
    }

    [Fact]
    public void Hold_units_are_never_pulled_into_a_withdraw()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var ownBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(ownBase);
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(500f, 0f, 0f)) { Order = UnitOrder.Hold };
        s.Units.Add(u);

        InvasionOrders.AssignAdvance(s, 0, 0f);

        Assert.False(s.FindUnit(1).OrderTargetPos.HasValue);
        Assert.Equal(UnitState.Idle, s.FindUnit(1).State);
    }
}
