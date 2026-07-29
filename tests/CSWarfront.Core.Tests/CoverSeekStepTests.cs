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

    // --- Task61: Sea/Airは遮蔽移動の対象外 ---

    [Fact]
    public void Advance_never_sets_CoverDestination_for_an_engaging_air_unit_even_when_cover_available()
    {
        var s = BaseState();
        AirUnitRoster.RegisterAll(s.Types);
        string key = AirUnitRoster.TypeKey(UnitCategory.AirSuperiority, 1);
        var type = s.Types.Get(key);
        var self = new UnitInstance(1, key, 0, type.MaxHP, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(30, 0, 0));
        s.Units.Add(self);
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(15, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
        Assert.False(self.CoverHold);
    }

    [Fact]
    public void Advance_never_sets_CoverDestination_for_a_sea_unit_advancing_toward_an_objective()
    {
        var s = BaseState();
        NavalUnitRoster.RegisterAll(s.Types);
        string key = NavalUnitRoster.TypeKey(UnitCategory.Destroyer, 1);
        var type = s.Types.Get(key);
        var self = new UnitInstance(1, key, 0, type.MaxHP, new WorldPos(0, 0, 0));
        s.Units.Add(self);
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(1000, 0, 0);

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(500, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
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

    // Task50: 交戦中(Mode2)の再評価は、もはやクールダウンではなく「同じ相手(TargetId)と戦い続けて
    // いるか」だけで決まる。クールダウンをどれだけ超えて時間を進めても、TargetIdが変わらない限り
    // 既に決めたCoverDestinationは一切変更しない（頻繁な位置変更を防ぐための最重要ガード）。
    [Fact]
    public void Advance_does_not_reevaluate_while_engaging_the_same_target_even_long_after_cooldown_would_have_elapsed()
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
        Assert.Equal(enemy.InstanceId, self.CoverTargetId);

        // 遮蔽物を全て取り除き、Task52のMaxEngageHoldHours(3h)を大きく超える時間を進めても、
        // 同じ相手と交戦し続けている限りCoverDestinationは変わらない（MaxEngageHoldHoursは
        // MovementStepの移動再開にのみ影響し、CoverSeekStep自体の再評価ロックは解かない）。
        s.Cover = new CoverMap();
        CoverSeekStep.Advance(s, CoverSeekStep.MaxEngageHoldHours * 10f);

        Assert.True(self.CoverDestination.HasValue);
        Assert.Equal(firstDestination.X, self.CoverDestination.Value.X, 3);
    }

    // Task50: 遮蔽が見つからなかった、という判断そのものも「同じ相手との交戦中は」記憶され、
    // 毎tick探索し直されない（CoverDestinationがnullのまま安定する＝この間MovementStepは動かさない）。
    [Fact]
    public void Advance_remembers_no_cover_found_for_the_same_target_and_does_not_keep_retrying()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        s.Cover = new CoverMap(); // no cover available at all

        CoverSeekStep.Advance(s, 0.01f);
        Assert.False(self.CoverDestination.HasValue);
        Assert.Equal(enemy.InstanceId, self.CoverTargetId); // decision recorded even though nothing was found

        // Cover appears later, but the target hasn't changed -> still must not re-pick.
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);
        CoverSeekStep.Advance(s, CoverSeekStep.MaxEngageHoldHours * 10f);

        Assert.False(self.CoverDestination.HasValue);
    }

    // Task50 TDD: 「an engaging unit re-evaluates cover only when its target changes」。
    // 相手が変わった瞬間は（クールダウンを待たず）即座に再評価される。
    [Fact]
    public void Advance_reevaluates_immediately_when_the_engaged_target_changes()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy1 = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        var enemy2 = AddUnit(s, 3, UnitCategory.Infantry, 1, new WorldPos(-100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy1.InstanceId;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);
        Assert.True(self.CoverDestination.HasValue);
        var firstDestination = self.CoverDestination.Value;
        Assert.Equal(enemy1.InstanceId, self.CoverTargetId);

        // Switch to a different target (still engaging) with a cover map that no longer has
        // anything near the old destination -> must re-pick right away, not wait for a cooldown.
        self.TargetId = enemy2.InstanceId;
        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(-40, 0, 0), 5f);
        CoverSeekStep.Advance(s, 0.01f); // tiny dt: a cooldown-based design would still be "in cooldown" here

        Assert.True(self.CoverDestination.HasValue);
        Assert.NotEqual(firstDestination.X, self.CoverDestination.Value.X, 3);
        Assert.Equal(enemy2.InstanceId, self.CoverTargetId);
    }

    // Task50/52: 到達済み（またはそもそも遮蔽が無い）の交戦ユニットは、MaxEngageHoldHoursに
    // 達するまでは一切動かない（CoverSeekStep→MovementStepの連携を統合的に確認する）。
    [Fact]
    public void Advance_engaging_unit_with_no_cover_does_not_move_within_MaxEngageHoldHours()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.OrderTargetPos = new WorldPos(1000, 0, 0); // stray advance target; must be ignored while engaging
        // s.Cover left null -> no cover map at all

        CoverSeekStep.Advance(s, 0.01f);
        MovementStep.Advance(s, 1f);
        Assert.Equal(0f, self.Position.X, 3);

        // Still well within MaxEngageHoldHours(3h) total.
        CoverSeekStep.Advance(s, 1f);
        MovementStep.Advance(s, 1f);
        Assert.Equal(0f, self.Position.X, 3);
        Assert.False(self.CoverDestination.HasValue);
    }

    // Task52 rule3 TDD: 遮蔽が全く見つからない（探索半径はあるが候補が無い/CoverMap無し）まま
    // 同じ相手と交戦し続けるユニットは、MaxEngageHoldHoursを超えた時点でEngagingのままでも
    // OrderTargetPosへの進軍を再開する（「敵を倒しきれず永久にスタックする」不具合の直接の修正）。
    [Fact]
    public void Advance_engaging_unit_with_no_cover_resumes_advancing_after_MaxEngageHoldHours()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.OrderTargetPos = new WorldPos(1000, 0, 0);
        // s.Cover left null -> no cover map at all, so this unit can never find cover to hide behind.

        CoverSeekStep.Advance(s, 0.01f);
        MovementStep.Advance(s, 1f);
        Assert.Equal(0f, self.Position.X, 3); // still pinned, well under MaxEngageHoldHours

        // Cross the MaxEngageHoldHours(3h) threshold while still engaging the same target.
        CoverSeekStep.Advance(s, CoverSeekStep.MaxEngageHoldHours);
        MovementStep.Advance(s, 1f);

        Assert.True(self.Position.X > 0f, "expected the unit to resume advancing once MaxEngageHoldHours elapsed");
        Assert.Equal(UnitState.Engaging, self.State); // Task52: may keep "fighting" (state-wise) while moving
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
        // MaxCoverDetour(40)以内、かつMinForwardProgress(5)を満たす候補。
        s.Cover.Add(new WorldPos(30, 0, 0), 5f); // 進軍方向にある遮蔽物

        float distBefore = self.Position.HorizontalDistanceTo(self.OrderTargetPos.Value);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.True(self.CoverDestination.HasValue);
        float distAfter = self.CoverDestination.Value.HorizontalDistanceTo(self.OrderTargetPos.Value);
        Assert.True(distAfter < distBefore - CoverSeekStep.MinForwardProgress,
            "expected the chosen cover to make forward progress toward the objective");
        // Task52: 進軍中(bounding advance)の遮蔽も、到着したら「保持」して少しだけ隠れる
        // （MovementStep.MaxCoverHoldHoursで自動的に前進を再開する、無期限には止まらない）。
        Assert.True(self.CoverHold);
    }

    // Task52 rule1: 現在地からMaxCoverDetourを超える遠回りな遮蔽は、前進条件を満たしていても却下される。
    [Fact]
    public void Advance_rejects_forward_progressing_cover_beyond_MaxCoverDetour()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(200, 0, 0);

        s.Cover = new CoverMap();
        // 前進条件(MinForwardProgress)は満たすが、現在地からの距離がMaxCoverDetour(40)を超える。
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
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
        // Task52: MaxCoverHoldHours(1h)より十分短い時間だけ進めれば、保持モードなので停止したまま。
        MovementStep.Advance(s, 0.2f);
        Assert.Equal(posAfterArrival.X, self.Position.X, 2);
        Assert.Equal(posAfterArrival.Z, self.Position.Z, 2);
        Assert.True(self.CoverDestination.HasValue); // 保持モードなのでクリアされない
    }

    // Task52 rule2 TDD: CoverHold==trueで静止し続けたユニットは、MaxCoverHoldHoursを超えたら
    // CoverDestinationを解放し、Engagingのままでも移動を再開する（MovementStepTestsとの統合確認）。
    [Fact]
    public void Advance_engaging_unit_resumes_advancing_after_MaxCoverHoldHours_even_while_still_engaging()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Tank, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Tank, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.OrderTargetPos = new WorldPos(1000, 0, 0);

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(30, 0, 0), 6f);

        CoverSeekStep.Advance(s, 0.01f);
        MovementStep.Advance(s, 10f); // arrive at the cover point
        Assert.True(self.Position.HorizontalDistanceTo(self.CoverDestination.Value) <= MovementStep.CoverArrivalDistance);

        // Advance well past MaxCoverHoldHours(1h) while remaining locked onto the same target
        // (CoverSeekStep must not re-pick cover; MovementStep alone must force the release).
        CoverSeekStep.Advance(s, 0.01f);
        MovementStep.Advance(s, MovementStep.MaxCoverHoldHours + 0.5f);

        Assert.False(self.CoverDestination.HasValue);

        // One more tick: still Engaging, but now free to move toward its objective.
        var beforeX = self.Position.X;
        CoverSeekStep.Advance(s, 0.01f);
        MovementStep.Advance(s, 1f);
        Assert.True(self.Position.X > beforeX, "expected the unit to resume advancing after the cover-hold cap");
        Assert.Equal(UnitState.Engaging, self.State);
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

    // --- Task48: Hold/RallyHold は遮蔽移動の対象外 ---

    [Fact]
    public void Advance_never_sets_CoverDestination_for_engaging_unit_with_Hold_order()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.Order = UnitOrder.Hold;

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_never_sets_CoverDestination_for_engaging_unit_with_RallyHold_order()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.Order = UnitOrder.RallyHold;
        self.RallyPoint = new WorldPos(0, 0, 0);

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
    }

    [Fact]
    public void Advance_clears_stray_CoverDestination_for_unit_switched_to_Hold_order()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.CoverDestination = new WorldPos(10, 0, 10); // leftover from before the Hold command
        self.CoverHold = true;
        self.Order = UnitOrder.Hold;

        CoverSeekStep.Advance(s, 0.01f);

        Assert.False(self.CoverDestination.HasValue);
        Assert.False(self.CoverHold);
    }

    // --- Task52: 「敵拠点への進軍が途中でスタックする」不具合の修正 ---

    // rule1 TDD: モード3(進軍中)は、CoverUseIntervalHoursの間隔が空くまで遮蔽探索そのものをしない
    // （良い候補が新たに現れても、クールダウン中は拾わない＝道路経路をそのまま進む）。
    [Fact]
    public void Advance_skips_cover_seeking_while_CoverUseIntervalHours_cooldown_is_unexpired()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(200, 0, 0);
        s.Cover = new CoverMap(); // no candidates yet

        // First tick: cooldown was 0 (default), so this attempt happens immediately and starts a
        // fresh CoverUseIntervalHours cooldown. No candidate exists yet, so nothing is picked.
        CoverSeekStep.Advance(s, 0.01f);
        Assert.False(self.CoverDestination.HasValue);

        // A perfectly good candidate appears, but the per-unit cooldown is still running. Nudge the
        // unit a little closer each tick (as MovementStep would along the road path) so the rule4
        // stall watchdog stays satisfied and doesn't interfere with what this test checks; the nudge
        // is small enough to stay well short of the candidate so forward-progress math is unaffected.
        s.Cover.Add(new WorldPos(30, 0, 0), 5f);
        self.Position = new WorldPos(6, 0, 0);
        CoverSeekStep.Advance(s, 1f); // well under the remaining CoverUseIntervalHours(3f)
        Assert.False(self.CoverDestination.HasValue,
            "expected cover-seeking to be skipped entirely while the cooldown is unexpired");

        // Cross the remaining cooldown -> now it is allowed to evaluate again and picks the candidate.
        self.Position = new WorldPos(12, 0, 0);
        CoverSeekStep.Advance(s, CoverSeekStep.CoverUseIntervalHours);
        Assert.True(self.CoverDestination.HasValue);
    }

    // rule4 TDD: OrderTargetPosまでの距離がStallTimeoutHoursの間StallEpsilon以上縮まらなければ、
    // 膠着とみなしCoverSuppressionRemainingをセットする。以後その時間は、良い候補があっても
    // 遮蔽探索そのものを完全に無視して道路経路のみに任せる。suppression自体は時間経過で自然に
    // 0へ戻り（「再有効化」）、その後は通常どおり遮蔽を評価できる。
    [Fact]
    public void Advance_watchdog_suppresses_cover_after_a_stall_and_re_enables_it_later()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(1000, 0, 0);
        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(30, 0, 0), 5f); // valid, forward-progressing, in-detour candidate

        // Tick 1: initializes the watchdog checkpoint (no MovementStep is invoked in this test, so
        // the unit's Position never actually advances -> it will look "stalled" from here on).
        CoverSeekStep.Advance(s, 0.01f);
        Assert.Equal(0f, self.CoverSuppressionRemaining);

        // No progress for exactly StallTimeoutHours -> the watchdog trips.
        CoverSeekStep.Advance(s, CoverSeekStep.StallTimeoutHours);
        Assert.True(self.CoverSuppressionRemaining > 0f, "expected the stall watchdog to trip");

        // While suppressed, cover is ignored entirely (even a stray pre-existing CoverDestination
        // gets cleared, and a perfectly good candidate is not picked up).
        self.CoverDestination = new WorldPos(999, 0, 0);
        CoverSeekStep.Advance(s, 0.5f); // small step: stays within the stall window, no re-trigger
        Assert.False(self.CoverDestination.HasValue,
            "expected cover to stay suppressed for the rest of the CoverSuppressedHours window");

        // Simulate the unit actually marching (as required: "ignores cover entirely and marches
        // straight along its path") by moving it closer to the objective, which both breaks the
        // stall and, combined with enough elapsed time, lets the suppression window fully elapse.
        self.Position = new WorldPos(500, 0, 0);
        CoverSeekStep.Advance(s, CoverSeekStep.CoverSuppressedHours);
        Assert.Equal(0f, self.CoverSuppressionRemaining); // expected suppression to re-enable itself over time
    }

    // rule4 TDD: 距離が着実に縮まり続けている限り、StallTimeoutHours分の大きなdtを一度に渡しても
    // ウォッチドッグは絶対に発動しない（進捗があるたびにチェックポイントとタイマーがリセットされるため）。
    [Fact]
    public void Advance_steady_progress_never_trips_the_stall_watchdog()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        self.State = UnitState.Moving;
        self.OrderTargetPos = new WorldPos(1000, 0, 0);
        s.Cover = new CoverMap(); // isolate the watchdog from cover-picking noise

        float x = 0f;
        for (int tick = 0; tick < 5; tick++)
        {
            x += 200f; // far more than StallEpsilon(5) of progress each window
            self.Position = new WorldPos(x, 0, 0);
            CoverSeekStep.Advance(s, CoverSeekStep.StallTimeoutHours); // a full stall-timeout window each time
            Assert.Equal(0f, self.CoverSuppressionRemaining);
        }
    }

    // rule2/3/5: プレイヤーのHold命令は、Task52で追加した各種タイマー・ウォッチドッグの影響を
    // 一切受けない（どれだけ時間を進めても遮蔽移動もタイマー蓄積も一切起きず、常に不動のまま）。
    [Fact]
    public void Advance_Hold_order_unit_is_unaffected_by_every_Task52_timer()
    {
        var s = BaseState();
        var self = AddUnit(s, 1, UnitCategory.Infantry, 0, new WorldPos(0, 0, 0));
        var enemy = AddUnit(s, 2, UnitCategory.Infantry, 1, new WorldPos(100, 0, 0));
        self.State = UnitState.Engaging;
        self.TargetId = enemy.InstanceId;
        self.Order = UnitOrder.Hold;
        self.OrderTargetPos = new WorldPos(1000, 0, 0); // stray; Hold must still never move

        s.Cover = new CoverMap();
        s.Cover.Add(new WorldPos(50, 0, 0), 5f);

        CoverSeekStep.Advance(s, CoverSeekStep.MaxEngageHoldHours * 5f); // far beyond every Task52 threshold
        MovementStep.Advance(s, 10f);

        Assert.False(self.CoverDestination.HasValue);
        Assert.Equal(0f, self.EngageHoldTimer);
        Assert.Equal(0f, self.StallTimer);
        Assert.Equal(0f, self.CoverSuppressionRemaining);
        Assert.Equal(0f, self.Position.X, 3); // Hold: never moves, regardless of any Task52 timer
    }
}
