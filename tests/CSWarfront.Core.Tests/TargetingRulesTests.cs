using CSWarfront.Core;
using Xunit;

/// <summary>
/// Task85（ユーザー要望「敵拠点を占領できるのは地上戦力だけ」「戦闘機は戦闘機・爆撃機・KAIJUのみ」
/// 「爆撃機は地上目標・KAIJUのみ」「駆逐艦は占領不可」「空母は発着艦プラットフォームのみ」）の
/// 標的規則とBaseCombatStep/ThreatCombatStepへの適用テスト。
/// ユニット間の標的制限（CanTargetDomains）はTargetSearchTestsの方でカバーする。
/// </summary>
public class TargetingRulesTests
{
    // --- TargetingRules 単体 ---

    [Fact]
    public void Fighter_and_carrier_cannot_attack_bases_others_can()
    {
        Assert.False(TargetingRules.CanAttackBase(UnitCategory.AirSuperiority));
        Assert.False(TargetingRules.CanAttackBase(UnitCategory.Carrier));
        Assert.True(TargetingRules.CanAttackBase(UnitCategory.Tank));
        Assert.True(TargetingRules.CanAttackBase(UnitCategory.TacticalBomber));
        Assert.True(TargetingRules.CanAttackBase(UnitCategory.Destroyer));
    }

    [Fact]
    public void Only_land_attackers_can_reduce_base_hp_to_zero()
    {
        Assert.Equal(0f, TargetingRules.BaseHpFloor(Domain.Land));
        Assert.Equal(1f, TargetingRules.BaseHpFloor(Domain.Air));
        Assert.Equal(1f, TargetingRules.BaseHpFloor(Domain.Sea));
    }

    [Fact]
    public void Carrier_cannot_attack_threats_others_can()
    {
        Assert.False(TargetingRules.CanAttackThreat(UnitCategory.Carrier));
        Assert.True(TargetingRules.CanAttackThreat(UnitCategory.AirSuperiority));
        Assert.True(TargetingRules.CanAttackThreat(UnitCategory.TacticalBomber));
        Assert.True(TargetingRules.CanAttackThreat(UnitCategory.Destroyer));
        Assert.True(TargetingRules.CanAttackThreat(UnitCategory.Tank));
    }

    // --- BaseCombatStep への適用 ---

    /// <summary>maxHpを省略するとMaxHP=開始HPになり自然回復（Task89）は発生しない
    /// （回復と無関係なテストの期待値を単純に保つため）。回復を検証するテストは明示的に
    /// maxHpを渡す。</summary>
    private static WarState StateWithHostileBase(float baseHp, float maxHp = -1f)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(40, 0, 0));
        enemyBase.OwnerFactionId = 1;
        enemyBase.CurrentHP = baseHp;
        enemyBase.MaxHP = maxHp > 0f ? maxHp : baseHp;
        s.Bases.Add(enemyBase);
        return s;
    }

    [Fact]
    public void Bomber_reduces_base_hp_only_down_to_one()
    {
        var s = StateWithHostileBase(10f); // 爆撃機の1tickダメージで余裕で0を割る低HP
        s.Types.Register(AirUnitRoster.Get(UnitCategory.TacticalBomber, 5));
        s.Units.Add(new UnitInstance(1, "TacticalBomber_T5", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 5f);

        Assert.Equal(1f, s.Bases[0].CurrentHP, 3); // 0ではなく1で止まる（占領は陸上戦力のみ）
    }

    [Fact]
    public void Destroyer_reduces_base_hp_only_down_to_one()
    {
        var s = StateWithHostileBase(10f);
        s.Types.Register(NavalUnitRoster.Get(UnitCategory.Destroyer, 5));
        s.Units.Add(new UnitInstance(1, "Destroyer_T5", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 5f);

        Assert.Equal(1f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Land_unit_can_reduce_base_hp_to_zero()
    {
        var s = StateWithHostileBase(10f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 5f);

        Assert.Equal(0f, s.Bases[0].CurrentHP, 3); // 陸上戦力は0まで削れる＝占領できる
    }

    [Fact]
    public void Fighter_does_not_damage_bases_at_all()
    {
        var s = StateWithHostileBase(100f);
        s.Types.Register(AirUnitRoster.Get(UnitCategory.AirSuperiority, 1));
        s.Units.Add(new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Carrier_does_not_damage_bases_at_all()
    {
        var s = StateWithHostileBase(100f);
        s.Types.Register(NavalUnitRoster.Get(UnitCategory.Carrier, 1));
        s.Units.Add(new UnitInstance(1, "Carrier_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.Equal(100f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Air_then_land_can_finish_a_base_the_air_left_at_one_hp()
    {
        // 航空で1まで削った後、陸上が最後の1を削って0にできる（協同攻略の想定フロー）。
        var s = StateWithHostileBase(10f);
        s.Types.Register(AirUnitRoster.Get(UnitCategory.TacticalBomber, 5));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "TacticalBomber_T5", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 5f);
        Assert.Equal(1f, s.Bases[0].CurrentHP, 3);

        s.Units.Add(new UnitInstance(2, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(0f, s.Bases[0].CurrentHP, 3);
    }

    // --- Task88: HP床に達した拠点への攻撃停止 ---

    [Fact]
    public void Bomber_stops_shooting_a_base_already_at_the_floor()
    {
        // 実機報告「爆撃機が敵拠点HPが1になっても攻撃をやめない」の修正。床(1)に達した拠点へは
        // ダメージも発砲イベントも一切発生させない（無意味な爆撃を延々と続けない）。
        var s = StateWithHostileBase(1f);
        s.Types.Register(AirUnitRoster.Get(UnitCategory.TacticalBomber, 1));
        s.Units.Add(new UnitInstance(1, "TacticalBomber_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 5f);

        Assert.Equal(1f, s.Bases[0].CurrentHP, 3);
        Assert.Empty(s.RecentShots); // 発砲の見た目も出さない
    }

    [Fact]
    public void Land_unit_still_finishes_a_base_at_one_hp()
    {
        // 陸上の床は0なので、HP1の拠点は引き続き攻撃・占領対象。
        var s = StateWithHostileBase(1f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.Equal(0f, s.Bases[0].CurrentHP, 3);
    }

    // --- Task89: 基地HPの自然回復 ---

    [Fact]
    public void Damaged_base_slowly_regenerates_hp()
    {
        var s = StateWithHostileBase(250f, 500f); // 攻撃者なし
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(250f + BaseCombatStep.BaseRegenPerHour, s.Bases[0].CurrentHP, 2);
    }

    [Fact]
    public void Regeneration_caps_at_max_hp()
    {
        var s = StateWithHostileBase(499f, 500f); // MaxHP=500の直下
        BaseCombatStep.Advance(s, 5f);
        Assert.Equal(500f, s.Bases[0].CurrentHP, 2);
    }

    [Fact]
    public void Captured_base_at_zero_hp_does_not_regenerate()
    {
        // HP0（占領処理待ち）の基地は回復しない——回復させると占領が二度と成立しなくなる。
        var s = StateWithHostileBase(0f, 500f);
        BaseCombatStep.Advance(s, 1f);
        Assert.Equal(0f, s.Bases[0].CurrentHP, 3);
    }

    [Fact]
    public void Land_attack_exceeding_regen_still_grinds_the_base_down()
    {
        // Tank_T1の攻城DPS 40*0.8=32/h > 回復20/h → 正味12/hで削れ続ける＝
        // 「回復速度を上回る攻撃が地上兵力から加えられた場合のみ占領される」の要件を満たす。
        var s = StateWithHostileBase(100f, 500f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        float expected = 100f + BaseCombatStep.BaseRegenPerHour - 42f * 0.8f; // 回復→攻撃の順（Task91: Tank Attack 42）
        Assert.Equal(expected, s.Bases[0].CurrentHP, 2);
    }

    [Fact]
    public void Weak_land_attack_below_regen_cannot_capture()
    {
        // 回復を下回る攻撃（歩兵1体: DamagePerHit(20,0)=20 × siege accuracy 0.8 = 16/h < 20/h）では
        // 正味プラスで、基地は削り切れない。
        var s = StateWithHostileBase(100f, 500f);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Infantry, 1));
        s.Units.Add(new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0)));

        BaseCombatStep.Advance(s, 1f);

        Assert.True(s.Bases[0].CurrentHP >= 100f,
            "expected regen to outpace a sub-regen attack (hp=" + s.Bases[0].CurrentHP + ")");
    }

    // --- ThreatCombatStep への適用 ---

    [Fact]
    public void Carrier_does_not_damage_threats()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(NavalUnitRoster.Get(UnitCategory.Carrier, 5));
        s.Units.Add(new UnitInstance(1, "Carrier_T5", 0, 100f, new WorldPos(0, 0, 0)));
        var threat = new ExternalThreat
        {
            Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(50, 0, 0),
            Radius = 45f, MaxHP = 65000f, CurrentHP = 65000f
        };
        s.Threats.Add(threat);

        ThreatCombatStep.Advance(s, 1f);

        Assert.Equal(65000f, threat.CurrentHP, 1);
    }

    [Fact]
    public void Fighter_still_damages_threats()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(AirUnitRoster.Get(UnitCategory.AirSuperiority, 5));
        s.Units.Add(new UnitInstance(1, "AirSuperiority_T5", 0, 100f, new WorldPos(0, 0, 0)));
        var threat = new ExternalThreat
        {
            Id = 1, Kind = ThreatKind.Kaiju, Position = new WorldPos(50, 0, 0),
            Radius = 45f, MaxHP = 65000f, CurrentHP = 65000f
        };
        s.Threats.Add(threat);

        ThreatCombatStep.Advance(s, 1f);

        Assert.True(threat.CurrentHP < 65000f); // 戦闘機はKAIJUを攻撃できる
    }
}
