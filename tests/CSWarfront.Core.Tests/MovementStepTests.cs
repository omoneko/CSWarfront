using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class MovementStepTests
{
    private static WarState OneMovingUnit()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(1000, 0, 0);
        s.Units.Add(u);
        return s;
    }

    // Tank_T1のSpeed（Task26でkm/h基準に較正済み、SpeedCalibration参照）:
    // 40km/h -> (40*1000/3600) / 2.05078125(InGameHoursPerRealSecond) ≈ 5.418 map units / ゲーム内時間。
    private const float TankSpeedPerHour = 5.418f;

    [Fact]
    public void Advance_moves_unit_toward_target()
    {
        var s = OneMovingUnit();
        MovementStep.Advance(s, 1f);
        // dt=1h, distance 1000 -> TankSpeedPerHour(≈5.418)ぶんの部分移動
        Assert.Equal(TankSpeedPerHour, s.Units[0].Position.X, 2);
        Assert.Equal(0f, s.Units[0].Position.Z, 1);
    }

    [Fact]
    public void Advance_stops_at_target_without_overshoot()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(5, 0, 0);
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(5f, s.Units[0].Position.X, 1);
        Assert.Equal(0f, s.Units[0].Position.Z, 1);
    }

    [Fact]
    public void Advance_does_not_move_idle_unit()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Idle;
        u.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 1);
    }

    [Fact]
    public void Advance_does_not_move_when_no_target()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 1);
    }

    // Task37: 旧仕様は「移動中もYは常に維持する」だった（テスト名 Advance_preserves_y_coordinate、
    // 期待値は開始Yの42のまま）。これは道路の勾配を無視して水平飛行する「路面から浮く」バグの原因だった
    // ため、新仕様では X/Z と同じ補間係数でYも目標へ向けて補間する。以下はその新仕様を検証する
    // （start Y=42, target Y=0, dist=100, stepLen≈TankSpeedPerHour(≈5.418) -> t≈0.05418,
    //  Y = 42 + (0-42)*t ≈ 39.72）。
    [Fact]
    public void Advance_interpolates_y_toward_target_in_straight_line_fallback()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 42, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(39.7f, s.Units[0].Position.Y, 1);
    }

    // Task37: 直線フォールバック（Path無し/消化済み）でも、到達時はtargetのYへ完全収束すること
    // （オーバーシュートせず、丸め誤差もなく厳密にtargetのYになる）を確認する。
    [Fact]
    public void Advance_converges_exactly_to_target_y_on_arrival_in_straight_line_fallback()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(5, 20, 0); // dist=5 << stepLen(≈5.418) -> 1ステップで到達
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(5f, s.Units[0].Position.X, 1);
        Assert.Equal(20f, s.Units[0].Position.Y, 4); // targetのYへ厳密に収束（スナップ）
        Assert.Equal(0f, s.Units[0].Position.Z, 1);
    }

    private static WarState UnitWithPath()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1()); // Speed ≈5.418 map units / in-game hour（Task26較正後、TankSpeedPerHour参照）
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(100, 0, 100);
        u.Path = new List<WorldPos> { new WorldPos(100, 0, 0), new WorldPos(100, 0, 100) };
        u.PathIndex = 0;
        u.PathTarget = u.OrderTargetPos;
        s.Units.Add(u);
        return s;
    }

    [Fact]
    public void Advance_small_step_moves_toward_first_waypoint_only()
    {
        var s = UnitWithPath();
        // dt small enough that stepLen (TankSpeedPerHour*dt ≈ 5.418*0.1 ≈ 0.542) doesn't reach waypoint 1 (100 away)
        MovementStep.Advance(s, 0.1f); // stepLen ≈ 0.542
        var u = s.Units[0];
        Assert.Equal(0.542f, u.Position.X, 2);
        Assert.Equal(0f, u.Position.Z, 1);
        Assert.Equal(0, u.PathIndex); // still heading to waypoint 0
    }

    [Fact]
    public void Advance_large_step_crosses_first_waypoint_and_continues_toward_second()
    {
        var s = UnitWithPath();
        // dt chosen so that stepLen = TankSpeedPerHour*dt = 150 exactly (150/5.418... ≈ 27.6855469h):
        // covers 100 to first waypoint, then 50 more toward second (同じ幾何、旧テストのSpeed=250, dt=0.6と同じ結果になるよう選定)
        MovementStep.Advance(s, 27.6855469f);
        var u = s.Units[0];
        Assert.Equal(100f, u.Position.X, 1);
        Assert.Equal(50f, u.Position.Z, 1);
        Assert.Equal(1, u.PathIndex); // now heading to waypoint 1 (index 1)
    }

    [Fact]
    public void Advance_falls_back_to_straight_line_when_path_exhausted()
    {
        var s = UnitWithPath();
        var u = s.Units[0];
        u.PathIndex = 2; // path exhausted (Path.Count == 2)
        u.Position = new WorldPos(100, 0, 100); // arrived at last waypoint already
        u.OrderTargetPos = new WorldPos(200, 0, 100);

        // stepLen = TankSpeedPerHour*20 ≈ 108.4、100離れた目標に到達するには十分（オーバーシュートせず目標でクランプされる）
        MovementStep.Advance(s, 20f);

        Assert.Equal(200f, u.Position.X, 1);
        Assert.Equal(100f, u.Position.Z, 1);
    }

    [Fact]
    public void Advance_with_null_path_uses_straight_line_fallback()
    {
        var s = OneMovingUnit();
        s.Units[0].Path = null;
        MovementStep.Advance(s, 1f);
        Assert.Equal(TankSpeedPerHour, s.Units[0].Position.X, 2);
    }

    // Task37: 旧仕様は「Pathに沿って移動中もYは常に維持する」だった（テスト名
    // Advance_preserves_y_while_following_path、期待値は開始Yの42のまま）。これは道路の勾配（橋・坂）を
    // 無視して水平飛行してしまう「路面から浮く」バグの原因だったため、新仕様ではウェイポイントへ向かう
    // 間もX/Zと同じ補間係数でYを補間する。UnitWithPath()のウェイポイントはY=0なので、42から0へ向けて
    // 補間されるはず（dist=100, stepLen≈3.2508 -> t≈0.032508, Y=42+(0-42)*t≈40.63）。
    [Fact]
    public void Advance_interpolates_y_toward_waypoint_while_following_path()
    {
        var s = UnitWithPath();
        s.Units[0].Position = new WorldPos(0, 42, 0);
        MovementStep.Advance(s, 0.6f);
        Assert.Equal(40.6f, s.Units[0].Position.Y, 1);
    }

    // Task37: ウェイポイントへ到達した瞬間は、丸め誤差なくそのウェイポイントのYへ厳密にスナップすること
    // （坂の上/橋の上で「ちょうど乗った」状態を保証する）。
    [Fact]
    public void Advance_snaps_exactly_to_waypoint_y_on_arrival()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(10, 7, 0);
        u.Path = new List<WorldPos> { new WorldPos(10, 7, 0) }; // 坂の上、Y=7
        u.PathIndex = 0;
        u.PathTarget = u.OrderTargetPos;
        s.Units.Add(u);

        // stepLen = TankSpeedPerHour*2 ≈ 10.84 >= dist(10) -> 1ステップで到達しウェイポイントのYへスナップ
        MovementStep.Advance(s, 2f);

        Assert.Equal(10f, s.Units[0].Position.X, 1);
        Assert.Equal(7f, s.Units[0].Position.Y, 4); // 厳密にウェイポイントのYへスナップ
        Assert.Equal(1, s.Units[0].PathIndex);
    }

    // Task37: 複数ウェイポイントを跨ぐ大きなステップでも、最後に到達したウェイポイントのYを基準に
    // 次のウェイポイントへ向けて正しく補間されること（橋を渡るように0->10->20と高さが上がる想定）。
    [Fact]
    public void Advance_large_step_crosses_waypoint_and_interpolates_y_from_last_reached_waypoint()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(100, 20, 100);
        u.Path = new List<WorldPos> { new WorldPos(100, 10, 0), new WorldPos(100, 20, 100) };
        u.PathIndex = 0;
        u.PathTarget = u.OrderTargetPos;
        s.Units.Add(u);

        // stepLen≈150（Advance_large_step_crosses_first_waypoint_and_continues_toward_secondと同じdt）:
        // 最初のウェイポイントまで100ぶん到達（Y=10へスナップ）→残り50を2番目(dist100)へ向けて進む
        // (t=0.5) -> Y = 10 + (20-10)*0.5 = 15。
        MovementStep.Advance(s, 27.6855469f);
        var pos = s.Units[0].Position;

        Assert.Equal(100f, pos.X, 1);
        Assert.Equal(50f, pos.Z, 1);
        Assert.Equal(15f, pos.Y, 1);
        Assert.Equal(1, s.Units[0].PathIndex);
    }

    // --- Task44: CoverDestination優先の移動 ---

    [Fact]
    public void Advance_moves_engaging_unit_toward_CoverDestination_instead_of_OrderTargetPos()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Engaging;
        u.OrderTargetPos = new WorldPos(1000, 0, 0); // 進軍目的地（無視されるはず）
        u.CoverDestination = new WorldPos(0, 0, 1000); // 遮蔽位置（南北方向、こちらへ動くはず）
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        // OrderTargetPos(X方向)ではなくCoverDestination(Z方向)へ動いたことを確認する。
        Assert.Equal(0f, s.Units[0].Position.X, 1);
        Assert.True(s.Units[0].Position.Z > 0f, "expected unit to move toward CoverDestination (positive Z)");
    }

    [Fact]
    public void Advance_stops_within_CoverArrivalDistance_of_CoverDestination()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Engaging;
        u.CoverDestination = new WorldPos(1f, 0, 0); // 探索半径(arrival distance=3)より近い

        s.Units.Add(u);

        MovementStep.Advance(s, 1f); // ステップ長は十分大きい(TankSpeedPerHour≈5.4)

        // すでにCoverArrivalDistance(3)以内なので動かない。
        Assert.Equal(0f, s.Units[0].Position.X, 3);
    }

    // Task44の旧仕様（テスト名 Advance_does_not_use_CoverDestination_when_not_engaging）は
    // 「State==EngagingでなければCoverDestinationは無視する」だった。Task45でCoverSeekStepが
    // 進軍中（交戦前、自勢力圏の外）のユニットにもCoverDestinationを設定するようになったため、
    // MovementStepはState(Engaging/Moving)に関わらずCoverDestinationが設定されていれば必ず
    // honorするよう変更した。以下はその新仕様を検証する（State=MovingでもCoverDestinationへ
    // 向かい、OrderTargetPosは無視される＝bounding advance中の移動）。
    [Fact]
    public void Advance_uses_CoverDestination_even_when_not_engaging_since_bounding_advance_sets_it_while_moving()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving; // Engagingではないが、bounding advance中はCoverDestinationを持ちうる
        u.OrderTargetPos = new WorldPos(1000, 0, 0); // 無視されるはず
        u.CoverDestination = new WorldPos(0, 0, 1000); // こちらへ動くはず
        u.CoverHold = false;

        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        // OrderTargetPos(X方向)ではなくCoverDestination(Z方向)へ動いたことを確認する。
        Assert.Equal(0f, s.Units[0].Position.X, 1);
        Assert.True(s.Units[0].Position.Z > 0f, "expected unit to move toward CoverDestination even while not Engaging");
    }

    // Task45: bounding advance（CoverHold==false）でCoverArrivalDistance以内に到達したら、
    // その場に留まるのではなくCoverDestinationをクリアし、CoverReevaluateCooldownも0へリセットして
    // 次のCoverSeekStep評価がすぐ次の遮蔽を選べるようにする（遮蔽から遮蔽への「跳び」を実現する）。
    [Fact]
    public void Advance_clears_CoverDestination_and_resets_cooldown_on_arrival_when_not_holding()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.CoverDestination = new WorldPos(1f, 0, 0); // CoverArrivalDistance(3)より近い
        u.CoverHold = false;
        u.CoverReevaluateCooldown = 0.3f; // まだクールダウン中のふり

        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.False(s.Units[0].CoverDestination.HasValue);
        Assert.Equal(0f, s.Units[0].CoverReevaluateCooldown);
    }

    // Task45: 対して、CoverHold==true（交戦中）ならCoverArrivalDistance以内でも
    // CoverDestinationを保持したまま、その場に留まり続ける（従来のTask44挙動）。
    [Fact]
    public void Advance_keeps_CoverDestination_on_arrival_when_holding()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Engaging;
        u.CoverDestination = new WorldPos(1f, 0, 0);
        u.CoverHold = true;

        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.True(s.Units[0].CoverDestination.HasValue);
        Assert.Equal(0f, s.Units[0].Position.X, 3); // 動いていない
    }

    [Fact]
    public void Advance_resumes_path_following_once_CoverDestination_is_cleared()
    {
        var s = OneMovingUnit();
        var u = s.Units[0];
        u.State = UnitState.Engaging;
        u.CoverDestination = new WorldPos(0, 0, 1000);
        MovementStep.Advance(s, 0.1f);
        Assert.True(s.Units[0].Position.Z > 0f); // 遮蔽位置へ向かって動いた

        // 交戦が終わりCoverDestinationがクリアされ、通常の移動に戻ったと仮定する。
        u.CoverDestination = null;
        u.State = UnitState.Moving;
        var beforeX = s.Units[0].Position.X;
        MovementStep.Advance(s, 0.1f);

        Assert.True(s.Units[0].Position.X > beforeX, "expected unit to resume advancing toward OrderTargetPos");
    }

    // --- Task48: Order (Hold/RallyHold) ---

    [Fact]
    public void Advance_never_moves_unit_with_Hold_order_even_if_State_and_OrderTargetPos_are_set()
    {
        var s = OneMovingUnit();
        s.Units[0].Order = UnitOrder.Hold;

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 3);
        Assert.Equal(0f, s.Units[0].Position.Z, 3);
    }

    [Fact]
    public void Advance_never_moves_unit_with_Hold_order_even_with_stray_CoverDestination()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.Hold;
        u.CoverDestination = new WorldPos(0, 0, 1000); // stale/inconsistent, should still be ignored
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 3);
        Assert.Equal(0f, s.Units[0].Position.Z, 3);
    }

    [Fact]
    public void Advance_moves_RallyHold_unit_toward_RallyPoint_via_straight_line_when_no_path()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(1000, 0, 0);
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(TankSpeedPerHour, s.Units[0].Position.X, 2);
        Assert.Equal(0f, s.Units[0].Position.Z, 1);
    }

    [Fact]
    public void Advance_RallyHold_unit_stops_within_CoverArrivalDistance_of_RallyPoint()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(1f, 0, 0); // < CoverArrivalDistance(3)

        s.Units.Add(u);

        MovementStep.Advance(s, 1f); // stepLen (~5.4) easily covers the remaining distance

        Assert.Equal(0f, s.Units[0].Position.X, 3); // already within arrival distance, does not move
    }

    [Fact]
    public void Advance_RallyHold_unit_consumes_Path_toward_RallyPoint_before_falling_back_to_straight_line()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(100, 0, 100);
        u.Path = new List<WorldPos> { new WorldPos(100, 0, 0), new WorldPos(100, 0, 100) };
        u.PathIndex = 0;
        u.PathTarget = u.RallyPoint;
        s.Units.Add(u);

        // Same geometry as Advance_large_step_crosses_first_waypoint_and_continues_toward_second:
        // clears the first waypoint (100 away) then advances 50 more toward the second.
        MovementStep.Advance(s, 27.6855469f);

        var pos = s.Units[0].Position;
        Assert.Equal(100f, pos.X, 1);
        Assert.Equal(50f, pos.Z, 1);
        Assert.Equal(1, u.PathIndex);
    }

    [Fact]
    public void Advance_RallyHold_unit_with_no_RallyPoint_does_not_move()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold; // RallyPoint intentionally left null
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 3);
    }

    // --- Task50: 「建物の陰に隠れながら戦闘するときは停車する」 ---

    // 遮蔽位置(CoverDestination)が無い交戦中ユニットは、OrderTargetPos/Pathが残っていても
    // 一切動かない（複数tickにわたって停止し続ける）。RallyHoldでない通常のAiControlled/FreeAdvance
    // ユニットを想定（RallyHoldは移動しながら応戦する別仕様、下のテスト参照）。
    [Fact]
    public void Advance_engaging_unit_without_CoverDestination_never_moves_toward_OrderTargetPos()
    {
        var s = OneMovingUnit();
        var u = s.Units[0];
        u.State = UnitState.Engaging; // no CoverDestination assigned (CoverSeekStep found none, e.g.)

        MovementStep.Advance(s, 1f);
        Assert.Equal(0f, u.Position.X, 3);

        MovementStep.Advance(s, 10f); // several more ticks, still nothing to move toward
        Assert.Equal(0f, u.Position.X, 3);
    }

    // RallyHold + Engaging は例外: 「移動中・停止後を問わず射程内の敵にしか応戦しない」という
    // Task48の意図的な仕様どおり、持ち場(RallyPoint)へ向かいながら応戦し続ける（Task50では変更しない）。
    [Fact]
    public void Advance_RallyHold_unit_still_advances_toward_RallyPoint_while_engaging()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.Order = UnitOrder.RallyHold;
        u.RallyPoint = new WorldPos(1000, 0, 0);
        u.State = UnitState.Engaging; // fighting off something along the way, no CoverDestination
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.Equal(TankSpeedPerHour, s.Units[0].Position.X, 2);
    }

    // 非交戦(進軍中)のbounding advanceは従来通り機能する（Task50でモード2のみを変更したことの回帰確認）。
    // CoverHold==false（互換維持のフォールバック経路。現行のCoverSeekStepはもう作らない値だが、
    // 手動で設定した呼び出し元のために挙動を維持する、Task52）。
    [Fact]
    public void Advance_non_engaging_unit_still_bounds_from_cover_to_cover()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.CoverDestination = new WorldPos(1f, 0, 0); // within CoverArrivalDistance(3)
        u.CoverHold = false; // bounding advance, not holding
        s.Units.Add(u);

        MovementStep.Advance(s, 1f);

        Assert.False(s.Units[0].CoverDestination.HasValue); // cleared on arrival, ready for the next cover
        Assert.Equal(0f, s.Units[0].CoverReevaluateCooldown);
    }

    // --- Task52: 遮蔽保持(CoverHold==true)の時間上限（MaxCoverHoldHours） ---

    // rule2 TDD: Task52でモード3(進軍中のbounding advance)もCoverHold=trueで「保持」するようになった
    // （実際に隠れて一時停止する演出のため）。MaxCoverHoldHoursを超えて静止し続けたら、
    // CoverDestinationを解放し、通常のOrderTargetPosへの前進を再開する（無期限には止まらない）。
    [Fact]
    public void Advance_releases_bounding_CoverHold_after_MaxCoverHoldHours_and_resumes_toward_OrderTargetPos()
    {
        var s = OneMovingUnit(); // State=Moving, OrderTargetPos=(1000,0,0)
        var u = s.Units[0];
        u.CoverDestination = new WorldPos(1f, 0, 0); // within CoverArrivalDistance(3)
        u.CoverHold = true; // Task52: bounding cover now also holds briefly

        // Arrival tick: within MaxCoverHoldHours(1h), the hold timer only just started.
        MovementStep.Advance(s, 0.5f);
        Assert.True(s.Units[0].CoverDestination.HasValue);
        Assert.Equal(0f, s.Units[0].Position.X, 3);

        // Exceed the cap while still sitting at the cover point.
        MovementStep.Advance(s, MovementStep.MaxCoverHoldHours + 0.1f);
        Assert.False(s.Units[0].CoverDestination.HasValue);

        // Now free to resume toward OrderTargetPos.
        var beforeX = s.Units[0].Position.X;
        MovementStep.Advance(s, 1f);
        Assert.True(s.Units[0].Position.X > beforeX,
            "expected the unit to resume advancing once MaxCoverHoldHours elapsed");
    }
}
