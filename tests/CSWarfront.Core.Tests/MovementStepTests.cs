using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class MovementStepTests
{
    // Task53: state.Height（IHeightSampler）のフェイク実装。x+zという単純な決定的関数を返すため、
    // 「移動後のPosition.Yが常にTrySampleHeight(X, Z)の結果と一致する」ことをアサーションだけで
    // 検証できる（ウェイポイント/直線移動どちらのYとも通常一致しない値なので、旧来のY補間経路が
    // 誤って使われていないことも同時に検出できる）。
    private class FakeHeightSampler : IHeightSampler
    {
        public bool TrySampleHeight(float x, float z, out float height)
        {
            height = x + z;
            return true;
        }
    }

    // Task53ハードニング: TrySampleHeightが常にfalseを返す（TerrainManager瞬断/例外を模した）フェイク。
    // out引数には意図的に「地表からかけ離れた値」(-9999f)を書き込む。もしMovementStep側がfalseの
    // 戻り値を見落としてこのheightをそのまま採用してしまえば、テストのアサーションで即座に
    // 検出できるようにするための仕込み（実プロダクションのSurfaceHeightSamplerが失敗時に0fを
    // 返していたのと同種の「失敗値がそのまま採用される」不具合を再現・検出する）。
    private class FailingHeightSampler : IHeightSampler
    {
        public bool TrySampleHeight(float x, float z, out float height)
        {
            height = -9999f;
            return false;
        }
    }

    // Task55: 「ユニットが空中戦を始める」不具合の再発防止（防御的多層化）。SurfaceHeightSamplerが
    // 誤ったオーバーロード等で将来また荒唐無稽な高さを返しても、MovementStepがそれを鵜呑みにして
    // ユニットを空へ打ち上げないようにする。TrySampleHeight自体は成功(true)を返すが、値が
    // 補間済みY（従来のウェイポイント/直線補間の結果）からMaxSurfaceDeviationを超えて乖離していれば、
    // 採用せず補間済みYを使う。offsetは常に補間済みYからの絶対乖離量として機能する
    // （このテストで使う全ケースの補間済みYが0のため）。
    private class OffsetHeightSampler : IHeightSampler
    {
        private readonly float _offset;
        public OffsetHeightSampler(float offset) { _offset = offset; }

        public bool TrySampleHeight(float x, float z, out float height)
        {
            height = _offset;
            return true;
        }
    }


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

    // --- Task53: state.Heightが供給されている場合、Yは常にサンプリング値へ置き換わる ---

    [Fact]
    public void Advance_uses_sampled_height_instead_of_waypoint_y_on_arrival_when_HeightSampler_supplied()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(10, 7, 0);
        u.Path = new List<WorldPos> { new WorldPos(10, 7, 0) }; // ウェイポイント自体のY=7
        u.PathIndex = 0;
        u.PathTarget = u.OrderTargetPos;
        s.Units.Add(u);
        s.Height = new FakeHeightSampler(); // TrySampleHeight(10, 0) -> true, 10

        // stepLen = TankSpeedPerHour*2 ≈ 10.84 >= dist(10) -> 1ステップで到達（ウェイポイントに到達）
        MovementStep.Advance(s, 2f);

        Assert.Equal(10f, s.Units[0].Position.X, 1);
        // Task37の旧仕様なら Y はウェイポイントの7になるはずだが、Heightサンプラーがあれば
        // それを無視して TrySampleHeight(10, 0) = 10 が採用される。
        Assert.Equal(10f, s.Units[0].Position.Y, 3);
    }

    [Fact]
    public void Advance_uses_sampled_height_during_partial_straight_line_move_when_HeightSampler_supplied()
    {
        var s = OneMovingUnit(); // start (0,0,0) -> target (1000,0,0)、Yは直線移動では常に0のはず（旧仕様）
        s.Height = new FakeHeightSampler();

        MovementStep.Advance(s, 1f); // stepLen ≈ TankSpeedPerHour(5.418), まだ目標に届かない部分移動

        var pos = s.Units[0].Position;
        Assert.Equal(TankSpeedPerHour, pos.X, 2);
        Assert.Equal(0f, pos.Z, 1);
        // 旧来の補間ならYは0のまま維持されるはずだが、TrySampleHeight(X, Z) = X + 0 = X が採用される。
        Assert.Equal(pos.X, pos.Y, 3);
    }

    [Fact]
    public void Advance_uses_sampled_height_during_partial_cover_move_when_HeightSampler_supplied()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        u.State = UnitState.Engaging;
        u.CoverDestination = new WorldPos(0, 0, 1000); // 南北方向、Z方向へ部分移動する
        s.Units.Add(u);
        s.Height = new FakeHeightSampler();

        MovementStep.Advance(s, 1f); // まだCoverArrivalDistanceに届かない部分移動

        var pos = s.Units[0].Position;
        Assert.Equal(0f, pos.X, 1);
        Assert.True(pos.Z > 0f, "expected partial movement toward CoverDestination");
        // TrySampleHeight(X, Z) = X + Z = 0 + Z = Z が採用される（旧来の補間なら0のまま）。
        Assert.Equal(pos.Z, pos.Y, 3);
    }

    [Fact]
    public void Advance_preserves_old_y_interpolation_when_HeightSampler_is_null()
    {
        // 回帰確認: state.Heightを一切設定しない（既定でnull）場合、Task37の従来のY補間が
        // そのまま維持されることを明示的に確認する（他の大多数のテストが暗黙に依存している前提）。
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 42, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(u);
        Assert.Null(s.Height);

        MovementStep.Advance(s, 1f);

        Assert.Equal(39.7f, s.Units[0].Position.Y, 1); // Advance_interpolates_y_toward_target_in_straight_line_fallbackと同じ期待値
    }

    // Task53ハードニング: TrySampleHeightがfalseを返す（TerrainManager瞬断/例外を模した）場合、
    // MovementStepはout引数の値（実装によっては0f等、地表と無関係な値）を絶対にYへ採用してはならず、
    // state.Height == nullのときと全く同じY補間結果になること（＝失敗時のフォールバックが
    // null-samplerパスと同一の経路を通っていること）を確認する。これが「TerrainManager瞬断で
    // ユニットが地表の遥か下へ一瞬テレポートする」不具合の再発防止そのもの。
    [Fact]
    public void Advance_falls_back_to_old_y_interpolation_when_TrySampleHeight_fails()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 42, 0));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(u);
        s.Height = new FailingHeightSampler(); // 常にfalseを返す。out引数(-9999f)は絶対に採用されないはず。

        MovementStep.Advance(s, 1f);

        // Advance_preserves_old_y_interpolation_when_HeightSampler_is_nullと全く同じ期待値
        // （= state.Height == nullのときと同一の補間結果になっていることの確認）。
        Assert.Equal(39.7f, s.Units[0].Position.Y, 1);
        Assert.NotEqual(-9999f, s.Units[0].Position.Y);
    }

    // --- Task55: サンプリング高さの逸脱クランプ（MaxSurfaceDeviation） ---

    // TrySampleHeightがtrueを返していても、値が補間済みYからMaxSurfaceDeviation(15f)を大幅に超えて
    // 乖離していれば無視し、従来のY補間結果を採用すること（「サンプラーが荒唐無稽な値を返しても
    // ユニットを空へ打ち上げない」防御）。OneMovingUnit()は始点・終点ともY=0のため、補間済みYは
    // 常に0になる（部分移動中も収束）。
    [Fact]
    public void Advance_ignores_wildly_high_sampled_height_and_uses_interpolated_y_instead()
    {
        var s = OneMovingUnit(); // start (0,0,0) -> target (1000,0,0), 補間済みYは常に0
        s.Height = new OffsetHeightSampler(9999f); // MaxSurfaceDeviation(15)を大幅に超える乖離

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.Y, 3);
    }

    // 乖離がMaxSurfaceDeviation(15f)以内であれば、従来どおりサンプリング値を採用する
    // （Task53が導入した「盛土などの小さな地表変化を反映する」挙動を壊さない）。
    [Fact]
    public void Advance_applies_sampled_height_when_deviation_is_within_MaxSurfaceDeviation()
    {
        var s = OneMovingUnit(); // 補間済みYは常に0
        s.Height = new OffsetHeightSampler(10f); // 15以内の乖離

        MovementStep.Advance(s, 1f);

        Assert.Equal(10f, s.Units[0].Position.Y, 3);
    }

    // 境界値: ちょうどMaxSurfaceDeviation(15f)は「超えて」いないので採用される
    // （仕様は「15fを超えたら」棄却＝15f自体は許容範囲の内側）。
    [Fact]
    public void Advance_applies_sampled_height_when_deviation_exactly_equals_MaxSurfaceDeviation()
    {
        var s = OneMovingUnit();
        s.Height = new OffsetHeightSampler(MovementStep.MaxSurfaceDeviation);

        MovementStep.Advance(s, 1f);

        Assert.Equal(MovementStep.MaxSurfaceDeviation, s.Units[0].Position.Y, 3);
    }

    // 境界値: MaxSurfaceDeviation(15f)をわずかに超えたら棄却され、補間済みYに戻る。
    [Fact]
    public void Advance_ignores_sampled_height_when_deviation_slightly_exceeds_MaxSurfaceDeviation()
    {
        var s = OneMovingUnit();
        s.Height = new OffsetHeightSampler(MovementStep.MaxSurfaceDeviation + 0.01f);

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.Y, 3);
    }

    // --- Task61: Air/Seaドメインの移動則 ---

    private class FakeWaterSampler : IWaterSampler
    {
        private readonly float _waterBoundaryX;
        private readonly float _level;
        public FakeWaterSampler(float waterBoundaryX, float level = 0f) { _waterBoundaryX = waterBoundaryX; _level = level; }
        public bool IsWater(float x, float z) { return x <= _waterBoundaryX; }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = _level; return IsWater(x, z); }
    }

    private static WarState OneAirUnit(float startX, float startY, float targetX)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(AirUnitRoster.Get(UnitCategory.AirSuperiority, 1)); // Domain=Air, fast
        var u = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(startX, startY, 0f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(targetX, 0f, 0f);
        s.Units.Add(u);
        return s;
    }

    private static WarState OneSeaUnit(float startX, float targetX)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(NavalUnitRoster.Get(UnitCategory.Destroyer, 1)); // Domain=Sea
        var u = new UnitInstance(1, "Destroyer_T1", 0, 100f, new WorldPos(startX, 0f, 0f));
        u.State = UnitState.Moving;
        u.OrderTargetPos = new WorldPos(targetX, 0f, 0f);
        s.Units.Add(u);
        return s;
    }

    [Fact]
    public void Air_unit_ignores_Path_and_moves_straight_toward_OrderTargetPos()
    {
        var s = OneAirUnit(0f, 0f, 1000f);
        s.Units[0].Path = new List<WorldPos> { new WorldPos(0f, 0f, 500f) }; // would divert a land unit; must be ignored

        MovementStep.Advance(s, 1f);

        Assert.True(s.Units[0].Position.X > 0f, "expected the fighter to have advanced toward X");
        Assert.Equal(0f, s.Units[0].Position.Z, 2); // did NOT follow the waypoint's Z=500 detour
    }

    [Fact]
    public void Air_unit_holds_cruise_altitude_above_sampled_ground()
    {
        var s = OneAirUnit(0f, 999f, 1000f);
        s.Height = new FakeHeightSampler(); // TrySampleHeight(x,z) = x+z

        MovementStep.Advance(s, 1f);

        var pos = s.Units[0].Position;
        // ground at (pos.X, 0) == pos.X (FakeHeightSampler returns x+z), so Y must be groundY + CruiseAltitude.
        Assert.Equal(pos.X + MovementStep.CruiseAltitude, pos.Y, 2);
    }

    [Fact]
    public void Air_unit_keeps_previous_Y_when_height_sampling_fails()
    {
        var s = OneAirUnit(0f, 500f, 1000f);
        s.Height = new FailingHeightSampler();

        MovementStep.Advance(s, 1f);

        Assert.Equal(500f, s.Units[0].Position.Y, 3);
    }

    [Fact]
    public void Air_unit_snaps_exactly_to_objective_on_arrival()
    {
        var s = OneAirUnit(0f, 0f, 3f); // well within one tick's travel distance for a fast fighter
        MovementStep.Advance(s, 1f);
        Assert.Equal(3f, s.Units[0].Position.X, 2);
        Assert.Equal(0f, s.Units[0].Position.Z, 2);
    }

    [Fact]
    public void Air_unit_does_not_move_when_Idle_with_no_order()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(AirUnitRoster.Get(UnitCategory.AirSuperiority, 1));
        var u = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0f, 0f, 0f));
        s.Units.Add(u); // State stays Idle (default), no OrderTargetPos

        MovementStep.Advance(s, 1f);

        Assert.Equal(0f, s.Units[0].Position.X, 3);
    }

    [Fact]
    public void Sea_unit_moves_straight_toward_OrderTargetPos_when_water_sampler_absent()
    {
        var s = OneSeaUnit(0f, 1000f);
        // s.Water intentionally left null: treated as "always water" (fallback for domain-less tests).
        MovementStep.Advance(s, 1f);
        Assert.True(s.Units[0].Position.X > 0f);
    }

    [Fact]
    public void Sea_unit_follows_the_sampled_water_level()
    {
        var s = OneSeaUnit(0f, 5f); // within one tick's travel distance
        s.Water = new FakeWaterSampler(waterBoundaryX: 1000f, level: 12f);

        MovementStep.Advance(s, 1f);

        Assert.Equal(5f, s.Units[0].Position.X, 2);
        Assert.Equal(12f, s.Units[0].Position.Y, 3);
    }

    [Fact]
    public void Sea_unit_refuses_to_step_onto_land_and_stops_at_the_waters_edge()
    {
        var s = OneSeaUnit(0f, 1000f); // objective is far inland, past the water boundary
        s.Water = new FakeWaterSampler(waterBoundaryX: 3f); // only x<=3 is water

        var beforeX = s.Units[0].Position.X;
        MovementStep.Advance(s, 1f); // stepLen for a destroyer easily exceeds 3 map units

        // The straight-line step would have landed past x=3 (on land), so the unit must not move at all
        // this tick rather than teleport onto land.
        Assert.Equal(beforeX, s.Units[0].Position.X, 3);
    }

    [Fact]
    public void Land_unit_behaviour_is_unchanged_by_the_Sea_Air_domain_split()
    {
        // Regression: a plain Land unit (Tank) must still use the pre-existing Path/road logic,
        // completely untouched by the new Domain != Land branch introduced above.
        var s = UnitWithPath();
        MovementStep.Advance(s, 0.1f);
        var u = s.Units[0];
        Assert.Equal(0.542f, u.Position.X, 2);
        Assert.Equal(0f, u.Position.Z, 1);
        Assert.Equal(0, u.PathIndex);
    }
}
