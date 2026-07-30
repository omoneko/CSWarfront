using CSWarfront.Core;
using Xunit;

/// <summary>
/// Task86（ユーザー要望「爆撃機は爆弾を落としてヒットアンドアウェイ、戦闘機は停止せずすれ違いながら
/// ドッグファイト」）: 航空ユニットの交戦パス移動（レーストラック航過）のテスト。
/// 接近→至近(PassTriggerDistance)で進行方向へ抜ける離脱点(PassEgressDistance)を設定→離脱点まで
/// 飛び切ってから反転して再進入、を繰り返す。ダメージは従来どおり射程内でのみ入るため、
/// 射程内滞在時間が減るぶんAirCombat.PassDamageCompensationで補正する。
/// </summary>
public class AirPassTests
{
    private static WarState FighterVsTargetState(out UnitInstance fighter, string targetTypeKey, float targetX)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        LandUnitRoster.RegisterAll(s.Types);
        NavalUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);

        fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Moving;
        fighter.OrderTargetPos = new WorldPos(1000, 0, 0);
        s.Units.Add(fighter);

        var target = new UnitInstance(2, targetTypeKey, 1, 10000f, new WorldPos(targetX, 0, 0));
        s.Units.Add(target);
        return s;
    }

    [Fact]
    public void Engaging_fighter_flies_through_its_locked_target_and_sets_egress_beyond_it()
    {
        UnitInstance fighter;
        var s = FighterVsTargetState(out fighter, "TacticalBomber_T1", 100f);
        fighter.TargetId = 2; // CombatStepがロック済みの想定
        fighter.State = UnitState.Engaging;

        // 至近距離まで接近するのに十分な回数進める（戦闘機は非常に速い）。
        for (int i = 0; i < 200 && !fighter.AirPassEgress.HasValue; i++)
            MovementStep.Advance(s, 0.05f);

        Assert.True(fighter.AirPassEgress.HasValue, "expected an egress point to be armed near the target");
        // 離脱点は標的(100,0)から進行方向(+X)へPassEgressDistanceぶん先。
        Assert.Equal(100f + AirCombat.PassEgressDistance, fighter.AirPassEgress.Value.X, 0);
        Assert.Equal(0f, fighter.AirPassEgress.Value.Z, 0);
    }

    [Fact]
    public void Fighter_completes_the_egress_leg_and_then_turns_back_for_another_pass()
    {
        UnitInstance fighter;
        var s = FighterVsTargetState(out fighter, "TacticalBomber_T1", 100f);
        fighter.TargetId = 2;
        fighter.State = UnitState.Engaging;

        // 離脱点が武装されるまで進める。
        for (int i = 0; i < 200 && !fighter.AirPassEgress.HasValue; i++)
            MovementStep.Advance(s, 0.05f);
        Assert.True(fighter.AirPassEgress.HasValue);

        // 離脱レグを飛び切る（標的の遥か向こう側まで抜ける＝ヒットアンドアウェイの「アウェイ」）。
        float maxX = fighter.Position.X;
        for (int i = 0; i < 400 && fighter.AirPassEgress.HasValue; i++)
        {
            MovementStep.Advance(s, 0.05f);
            if (fighter.Position.X > maxX) maxX = fighter.Position.X;
        }
        Assert.False(fighter.AirPassEgress.HasValue, "expected the egress leg to complete");
        Assert.True(maxX > 100f + AirCombat.PassEgressDistance * 0.8f,
            "expected the fighter to fly well past the target before turning (maxX=" + maxX + ")");

        // 反転して再び標的方向（-X側）へ向かう＝レーストラック。
        float xAfterEgress = fighter.Position.X;
        for (int i = 0; i < 40; i++) MovementStep.Advance(s, 0.05f);
        Assert.True(fighter.Position.X < xAfterEgress,
            "expected the fighter to turn back toward the target for another pass");
    }

    [Fact]
    public void Egress_leg_persists_even_if_the_target_dies_mid_leg()
    {
        UnitInstance fighter;
        var s = FighterVsTargetState(out fighter, "TacticalBomber_T1", 100f);
        fighter.TargetId = 2;
        fighter.State = UnitState.Engaging;

        for (int i = 0; i < 200 && !fighter.AirPassEgress.HasValue; i++)
            MovementStep.Advance(s, 0.05f);
        Assert.True(fighter.AirPassEgress.HasValue);

        // 標的が撃破されてロックも外れた（CombatStepがTargetId=nullにした想定）。
        s.FindUnit(2).CurrentHP = 0f;
        s.FindUnit(2).State = UnitState.Dead;
        fighter.TargetId = null;

        // それでも離脱レグは最後まで飛び切る（境界でのふらつき防止）。
        MovementStep.Advance(s, 0.05f);
        Assert.True(fighter.AirPassEgress.HasValue,
            "expected the egress leg to persist after the target died");
    }

    [Fact]
    public void Bomber_passes_over_a_hostile_base_in_range()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        AirUnitRoster.RegisterAll(s.Types);

        var bomber = new UnitInstance(1, "TacticalBomber_T1", 0, 100f, new WorldPos(0, 0, 0));
        bomber.State = UnitState.Moving;
        bomber.OrderTargetPos = new WorldPos(100, 0, 0); // 敵基地の位置が進撃目的地
        s.Units.Add(bomber);

        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(100, 0, 0));
        enemyBase.OwnerFactionId = 1;
        enemyBase.CurrentHP = 500f;
        s.Bases.Add(enemyBase);

        for (int i = 0; i < 300 && !bomber.AirPassEgress.HasValue; i++)
            MovementStep.Advance(s, 0.05f);

        Assert.True(bomber.AirPassEgress.HasValue,
            "expected the bomber to arm an egress point over the hostile base (hit and away)");
    }

    [Fact]
    public void Bomber_does_not_keep_passing_over_a_base_already_at_the_floor()
    {
        // Task88: HP1（航空の床）に達した拠点はもう航過アンカーにしない＝爆撃機は離脱して
        // 通常の目的地移動へ戻る（実機報告「HP1になっても攻撃をやめない」の移動面の対処）。
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        AirUnitRoster.RegisterAll(s.Types);

        var bomber = new UnitInstance(1, "TacticalBomber_T1", 0, 100f, new WorldPos(0, 0, 0));
        bomber.State = UnitState.Moving;
        bomber.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(bomber);

        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(100, 0, 0));
        enemyBase.OwnerFactionId = 1;
        enemyBase.CurrentHP = 1f; // 既に床
        s.Bases.Add(enemyBase);

        for (int i = 0; i < 300; i++) MovementStep.Advance(s, 0.05f);

        Assert.False(bomber.AirPassEgress.HasValue); // 航過は発生しない
        Assert.Equal(100f, bomber.Position.X, 0);    // 目的地でホバリング（従来の到着挙動）
    }

    [Fact]
    public void Fighter_does_not_pass_over_bases_it_cannot_attack()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        AirUnitRoster.RegisterAll(s.Types);

        var fighter = new UnitInstance(1, "AirSuperiority_T1", 0, 100f, new WorldPos(0, 0, 0));
        fighter.State = UnitState.Moving;
        fighter.OrderTargetPos = new WorldPos(100, 0, 0);
        s.Units.Add(fighter);

        var enemyBase = new MilitaryBase(200, BaseType.Army, new WorldPos(100, 0, 0));
        enemyBase.OwnerFactionId = 1;
        enemyBase.CurrentHP = 500f;
        s.Bases.Add(enemyBase);

        for (int i = 0; i < 300; i++) MovementStep.Advance(s, 0.05f);

        // 戦闘機は基地を攻撃できない（Task85）ので、基地上空でもパスは発生せず目的地でホバリングする。
        Assert.False(fighter.AirPassEgress.HasValue);
        Assert.Equal(100f, fighter.Position.X, 0);
    }

    [Fact]
    public void Plane_with_no_combat_anchor_advances_to_objective_as_before()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        AirUnitRoster.RegisterAll(s.Types);
        var bomber = new UnitInstance(1, "TacticalBomber_T1", 0, 100f, new WorldPos(0, 0, 0));
        bomber.State = UnitState.Moving;
        bomber.OrderTargetPos = new WorldPos(50, 0, 0);
        s.Units.Add(bomber);

        for (int i = 0; i < 100; i++) MovementStep.Advance(s, 0.05f);

        Assert.Equal(50f, bomber.Position.X, 1); // 従来どおり目的地へ到達して静止
        Assert.False(bomber.AirPassEgress.HasValue);
    }

    // --- ダメージ補正 ---

    [Fact]
    public void Damage_compensation_applies_to_air_but_not_land_or_kamikaze()
    {
        var types = new UnitTypeRegistry();
        LandUnitRoster.RegisterAll(types);
        AirUnitRoster.RegisterAll(types);

        Assert.Equal(AirCombat.PassDamageCompensation, AirCombat.DamageMultiplier(types.Get("AirSuperiority_T1")));
        Assert.Equal(AirCombat.PassDamageCompensation, AirCombat.DamageMultiplier(types.Get("TacticalBomber_T1")));
        Assert.Equal(1f, AirCombat.DamageMultiplier(types.Get("Tank_T1")));
        Assert.Equal(1f, AirCombat.DamageMultiplier(types.Get("SuicideDrone_T1"))); // 体当たりは1回フルダメージのまま
    }

    [Fact]
    public void CombatStep_applies_air_damage_compensation()
    {
        UnitInstance fighter;
        var s = FighterVsTargetState(out fighter, "TacticalBomber_T1", 50f); // 射程(90)内
        var target = s.FindUnit(2);
        float hpBefore = target.CurrentHP;

        CombatStep.Advance(s, 1f);

        var fighterType = s.Types.Get("AirSuperiority_T1");
        var targetType = s.Types.Get("TacticalBomber_T1");
        float expected = CombatMath.DamagePerHit(fighterType.Attack, targetType.Armor)
            * CombatMatchup.Multiplier(fighterType.Category, targetType.Category)
            * fighterType.Accuracy
            * AirCombat.PassDamageCompensation;
        Assert.Equal(expected, hpBefore - target.CurrentHP, 1);
    }
}
