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
}
