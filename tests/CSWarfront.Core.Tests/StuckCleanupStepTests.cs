using CSWarfront.Core;
using Xunit;

/// <summary>
/// StuckCleanupStep（Task98: 水際等でスタックしたユニットの自動消滅）のテスト。
/// </summary>
public class StuckCleanupStepTests
{
    private static WarState StateWithUnit(UnitState unitState, WorldPos pos)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, 100f, pos);
        u.State = unitState;
        s.Units.Add(u);
        return s;
    }

    /// <summary>DespawnAfterHoursぶんの時間をdt=1hで刻んで進める（位置は動かさない＝スタック）。</summary>
    private static int AdvanceStuckHours(WarState s, float hours)
    {
        int despawned = 0;
        for (float t = 0; t < hours; t += 1f) despawned += StuckCleanupStep.Advance(s, 1f);
        return despawned;
    }

    [Fact]
    public void Stuck_moving_unit_despawns_silently_after_the_threshold()
    {
        var s = StateWithUnit(UnitState.Moving, new WorldPos(1000, 0, 1000));

        // 初回Advanceでアンカー設定→そこからDespawnAfterHours経過。
        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours + 2f);

        Assert.Equal(1, despawned);
        Assert.Equal(UnitState.Dead, s.Units[0].State);
        Assert.Empty(s.RecentKills); // 無音・無爆発（KillEventを積まない）
    }

    [Fact]
    public void Progressing_unit_never_despawns()
    {
        var s = StateWithUnit(UnitState.Moving, new WorldPos(0, 0, 0));

        for (int i = 0; i < 40; i++)
        {
            // 毎時間MinProgressDistance以上前進している（正常な行軍）。
            var u = s.Units[0];
            u.Position = new WorldPos(u.Position.X + StuckCleanupStep.MinProgressDistance + 1f, 0, 0);
            StuckCleanupStep.Advance(s, 1f);
        }

        Assert.True(s.Units[0].IsAlive);
    }

    [Fact]
    public void Slow_infantry_marching_at_its_own_speed_never_despawns()
    {
        // Task98追補（実機バグ）: 歩兵(7.5km/h)は約1.0m/ゲーム時しか進まないため、固定20m閾値だと
        // 正常行軍のまま「12hで12m＜20m」でスタック扱いになり消滅していた。閾値は速度比例で
        // なければならない。
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        LandUnitRoster.RegisterAll(s.Types);
        UnitType infantry = s.Types.Get(LandUnitRoster.TypeKey(UnitCategory.Infantry, 1));
        var u = new UnitInstance(1, infantry.TypeKey, 0, infantry.MaxHP, new WorldPos(0, 0, 0));
        u.State = UnitState.Moving;
        s.Units.Add(u);

        // 本来の速度どおり毎時間 Speed×1h だけ前進し続ける（正常な行軍）。
        for (int i = 0; i < 48; i++)
        {
            u.Position = new WorldPos(u.Position.X + infantry.Speed * 1f, 0, 0);
            StuckCleanupStep.Advance(s, 1f);
        }

        Assert.True(u.IsAlive);
    }

    [Fact]
    public void Fully_stuck_infantry_still_despawns()
    {
        // 速度比例閾値でも「本当に動けていない」歩兵はきちんと消える。
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        LandUnitRoster.RegisterAll(s.Types);
        UnitType infantry = s.Types.Get(LandUnitRoster.TypeKey(UnitCategory.Infantry, 1));
        var u = new UnitInstance(1, infantry.TypeKey, 0, infantry.MaxHP, new WorldPos(1000, 0, 1000));
        u.State = UnitState.Moving;
        s.Units.Add(u);

        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours + 2f);

        Assert.Equal(1, despawned);
        Assert.Equal(UnitState.Dead, u.State);
    }

    [Fact]
    public void Stuck_unit_near_its_own_base_is_preserved()
    {
        var s = StateWithUnit(UnitState.Moving, new WorldPos(1000, 0, 1000));
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(1050, 0, 1000)); // 50m先＝除外半径内
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours * 3f);

        Assert.Equal(0, despawned);
        Assert.True(s.Units[0].IsAlive);
    }

    [Fact]
    public void Stuck_unit_near_an_enemy_base_still_despawns()
    {
        // 近くの基地が敵（他勢力）所有なら除外されない。
        var s = StateWithUnit(UnitState.Moving, new WorldPos(1000, 0, 1000));
        s.Factions.Add(new Faction(1, "Blue"));
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(1050, 0, 1000));
        b.OwnerFactionId = 1;
        s.Bases.Add(b);

        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours + 2f);

        Assert.Equal(1, despawned);
    }

    [Fact]
    public void Idle_and_engaging_units_are_never_considered_stuck()
    {
        var idle = StateWithUnit(UnitState.Idle, new WorldPos(0, 0, 0));
        var engaging = StateWithUnit(UnitState.Engaging, new WorldPos(0, 0, 0));

        Assert.Equal(0, AdvanceStuckHours(idle, StuckCleanupStep.DespawnAfterHours * 3f));
        Assert.Equal(0, AdvanceStuckHours(engaging, StuckCleanupStep.DespawnAfterHours * 3f));
        Assert.True(idle.Units[0].IsAlive);
        Assert.True(engaging.Units[0].IsAlive);
    }

    [Fact]
    public void Progress_resets_the_stuck_timer()
    {
        var s = StateWithUnit(UnitState.Moving, new WorldPos(0, 0, 0));

        // 閾値ぎりぎりまでスタック→一度前進→再びスタック。合計時間は閾値超だが連続ではないので生存。
        AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours - 2f);
        var u = s.Units[0];
        u.Position = new WorldPos(StuckCleanupStep.MinProgressDistance + 1f, 0, 0);
        StuckCleanupStep.Advance(s, 1f); // ここでアンカー・タイマーがリセットされる
        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours - 2f);

        Assert.Equal(0, despawned);
        Assert.True(u.IsAlive);
    }

    [Fact]
    public void State_transition_out_of_moving_resets_the_timer()
    {
        var s = StateWithUnit(UnitState.Moving, new WorldPos(0, 0, 0));

        AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours - 2f);
        s.Units[0].State = UnitState.Engaging; // 交戦に入った（立ち止まりは正常）
        StuckCleanupStep.Advance(s, 1f);
        s.Units[0].State = UnitState.Moving;   // 交戦終了、移動再開
        int despawned = AdvanceStuckHours(s, StuckCleanupStep.DespawnAfterHours - 2f);

        Assert.Equal(0, despawned);
        Assert.True(s.Units[0].IsAlive);
    }
}
