using CSWarfront.Core;
using Xunit;

public class CoverSeekStepTests
{
    private static WarState BaseState()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        LandUnitRoster.RegisterAll(s.Types);
        return s;
    }

    private static UnitInstance AddUnit(WarState s, uint id, UnitCategory category, byte faction, WorldPos pos)
    {
        string key = LandUnitRoster.TypeKey(category, 1);
        var type = s.Types.Get(key);
        var u = new UnitInstance(id, key, faction, type.MaxHP, pos);
        s.Units.Add(u);
        return u;
    }

    [Fact]
    public void Advance_sets_CoverDestination_for_engaging_infantry_when_cover_available()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.True(self.CoverDestination.HasValue);
        // Task45: 交戦中の遮蔽は「保持」モード＝到着したらその場に留まって撃ち続ける。
        Assert.True(self.CoverHold);
    }

    // Task44の旧仕様（テスト名の "not_engaging" が前提としていたこと）は「交戦していなければ
    // 常にCoverDestinationを持たない」だった。Task45でCoverSeekStepは進軍中（交戦前）のユニットにも
    // CoverDestinationを与えるようになったが、それは「OrderTargetPos(進軍目的地)がある」場合に限る。
    // このテストはOrderTargetPosを設定しないため、Idle相当（進軍先も交戦相手も無い）＝Mode3の対象外
    // であり、新仕様でも変わらずCoverDestinationは付かない。
    [Fact]
    public void Advance_does_not_set_CoverDestination_when_not_engaging_and_no_advance_target()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving; // OrderTargetPosを設定しない＝進軍目的地なし

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_clears_CoverDestination_when_unit_stops_engaging()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.CoverDestination = new WorldPos(10, 0, 10); // pretend it was previously set
        self.State = UnitState.Idle;
        self.TargetId = null;

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_clears_CoverDestination_when_target_died()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.CoverDestination = new WorldPos(10, 0, 10);
        enemy.State = UnitState.Dead;
        enemy.CurrentHP = 0f;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_never_sets_CoverDestination_for_artillery()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Artillery, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f); // plenty of cover nearby, but artillery shouldn't care

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_sets_CoverDestination_for_engaging_vehicle_within_its_smaller_radius()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Tank, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Tank, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(30, 0, 0), 6f); // well within Apc/Tank/AntiAir's 45 radius

        CoverSeekStep.Advance(s, 0.01f);

        Assert.True(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_leaves_CoverDestination_unset_when_no_cover_map_supplied()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        // s.Cover left null

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_does_not_reevaluate_before_cooldown_elapses()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);
        Assert.True(self.CoverDestination.HasValue);
        var firstDestination = self.CoverDestination.Value;

        // 遮蔽物を全て取り除いても、クールダウン中なら既存のCoverDestinationは変わらない。
        s.Cover = new CoverMap();
        CoverSeekStep.Advance(s, 0.01f); // still well within CoverReevaluateHours (0.5h)

        Assert.True(self.CoverDestination.HasValue);
        Assert.Equal(firstDestination.X, self.CoverDestination.Value.X, 3);
    }

    [Fact]
    public void Advance_reevaluates_after_cooldown_elapses()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);
        Assert.True(self.CoverDestination.HasValue);

        // 遮蔽マップを空にして、クールダウン(0.5h)を超える時間を進める。
        s.Cover = new CoverMap();
        CoverSeekStep.Advance(s, CoverSeekStep.CoverReevaluateHours + 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    // --- Task45: IsInFriendlyTerritory ---

    [Fact]
    public void IsInFriendlyTerritory_true_when_inside_own_base_influence_radius()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var home = new MilitaryBase(1, BaseType.Army, new WorldPos(100, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(home); // InfluenceRadius既定500、距離100なので圏内

        Assert.True(CoverSeekStep.IsInFriendlyTerritory(s, self));
    }

    [Fact]
    public void IsInFriendlyTerritory_false_when_outside_own_base_influence_radius()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var home = new MilitaryBase(1, BaseType.Army, new WorldPos(1000, 0, 0)) { OwnerFactionId = 0, InfluenceRadius = 500f };
        s.Bases.Add(home); // 距離1000 > InfluenceRadius500

        Assert.False(CoverSeekStep.IsInFriendlyTerritory(s, self));
    }

    [Fact]
    public void IsInFriendlyTerritory_ignores_enemy_base_influence_radius()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemyBase = new MilitaryBase(2, BaseType.Army, new WorldPos(50, 0, 0)) { OwnerFactionId = 1, InfluenceRadius = 500f };
        s.Bases.Add(enemyBase); // 圏内に入っているが敵勢力の基地なので味方territoryにはならない

        Assert.False(CoverSeekStep.IsInFriendlyTerritory(s, self));
    }

    [Fact]
    public void IsInFriendlyTerritory_false_when_no_bases_exist()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));

        Assert.False(CoverSeekStep.IsInFriendlyTerritory(s, self));
    }

    // --- Task45: Advance の3モード分岐 ---

    [Fact]
    public void Advance_gives_no_CoverDestination_when_inside_friendly_territory_even_if_engaging()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        var home = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0)) { OwnerFactionId = 0 };
        s.Bases.Add(home); // selfは自分の基地の勢力圏内

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f); // 遮蔽自体はあっても、圏内なので無視されるはず

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
        Assert.False(self.CoverHold);
    }

    [Fact]
    public void Advance_gives_forward_progressing_CoverDestination_when_advancing_outside_territory()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(200, 0, 0); // 進軍先(敵基地)の方向

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f); // 進軍方向にある遮蔽物

        float distBefore = self.Position.HorizontalDistanceTo(self.OrderTargetPos.Value);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.True(self.CoverDestination.HasValue);
        float distAfter = self.CoverDestination.Value.HorizontalDistanceTo(self.OrderTargetPos.Value);
        Assert.True(distAfter < distBefore - CoverSeekStep.MinForwardProgress,
            "expected the chosen cover to make forward progress toward the objective");
        // Task45: 進軍中(bounding advance)の遮蔽は「保持」しない＝到着したら次の遮蔽へ進む。
        Assert.False(self.CoverHold);
    }

    [Fact]
    public void Advance_leaves_CoverDestination_null_when_no_forward_progressing_cover_exists()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(200, 0, 0); // 進軍先は+X方向

        s.Cover = new CoverMap();
        // 遮蔽物が進軍方向の逆側(-X)にしか無い＝どれを選んでも目的地から遠ざかる。
        s.Cover.Add(new WorldPos(-50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_engaging_outside_territory_uses_target_as_threat_and_MovementStep_halts_at_it()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Tank, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Tank, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        // s.Bases left empty -> outside friendly territory

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(30, 0, 0), 6f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.True(self.CoverDestination.HasValue);
        Assert.True(self.CoverHold);

        // MovementStepが同じ立ち位置まで運び、そこで停止することを確認する（統合的な確認）。
        MovementStep.Advance(s, 10f); // 十分大きなdtで確実に到達させる
        Assert.True(self.Position.HorizontalDistanceTo(self.CoverDestination.Value) <= MovementStep.CoverArrivalDistance);

        var posAfterArrival = self.Position;
        MovementStep.Advance(s, 1f); // さらに進めても、保持モードなので停止したまま
        Assert.Equal(posAfterArrival.X, self.Position.X, 2);
        Assert.Equal(posAfterArrival.Z, self.Position.Z, 2);
        Assert.True(self.CoverDestination.HasValue); // 保持モードなのでクリアされない
    }

    [Fact]
    public void Advance_never_sets_CoverDestination_for_artillery_in_bounding_mode_either()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Artillery, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(200, 0, 0);
        // s.Bases left empty -> outside friendly territory, so Mode3 would otherwise apply

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }
}
