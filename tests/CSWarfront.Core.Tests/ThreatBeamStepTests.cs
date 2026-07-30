using CSWarfront.Core;
using Xunit;

/// <summary>
/// ThreatBeamStep（Task83: ゴジラ光線/トライポッドレーザーのユニットダメージ判定）のテスト。
/// 他MODが発射した一回性のビーム（線分）に対し、線分から一定幅以内の全ユニットへ
/// 装甲無視・勢力無差別の一撃ダメージを与える。
/// </summary>
public class ThreatBeamStepTests
{
    private static WarState StateWithTank(float x, float z, float hp = 100f)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, hp, new WorldPos(x, 0, z));
        s.Units.Add(u);
        return s;
    }

    [Fact]
    public void Unit_on_beam_path_takes_kaiju_damage_and_dies()
    {
        var s = StateWithTank(50f, 0f); // 線分(0,0)-(100,0)の真上
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);

        Assert.Equal(1, hit);
        Assert.True(s.Units[0].CurrentHP <= 0f);
        Assert.Equal(UnitState.Dead, s.Units[0].State);
        Assert.Single(s.RecentKills);
    }

    [Fact]
    public void Unit_within_lateral_width_is_hit()
    {
        var s = StateWithTank(50f, ThreatBeamStep.KaijuBeamWidth - 1f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);
        Assert.Equal(1, hit);
    }

    [Fact]
    public void Unit_outside_lateral_width_is_untouched()
    {
        var s = StateWithTank(50f, ThreatBeamStep.KaijuBeamWidth + 1f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);

        Assert.Equal(0, hit);
        Assert.Equal(100f, s.Units[0].CurrentHP, 1);
        Assert.Empty(s.RecentKills);
    }

    [Fact]
    public void Unit_beyond_segment_end_is_untouched()
    {
        var s = StateWithTank(100f + ThreatBeamStep.KaijuBeamWidth + 1f, 0f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);
        Assert.Equal(0, hit);
    }

    [Fact]
    public void Unit_within_endpoint_circle_is_hit()
    {
        // 線分の端点から幅以内（端点の丸いキャップ）もヒット扱い
        var s = StateWithTank(100f + ThreatBeamStep.KaijuBeamWidth - 1f, 0f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);
        Assert.Equal(1, hit);
    }

    [Fact]
    public void Alien_strike_uses_alien_damage()
    {
        // AlienBeamDamageを上回るHPを持たせ、「死なずにきっちりAlienBeamDamageぶん削れる」ことを確認
        var s = StateWithTank(50f, 0f, ThreatBeamStep.AlienBeamDamage + 100f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Alien, 0f, 0f, 100f, 0f);

        Assert.Equal(1, hit);
        Assert.Equal(100f, s.Units[0].CurrentHP, 1);
        Assert.NotEqual(UnitState.Dead, s.Units[0].State);
        Assert.Empty(s.RecentKills);
    }

    [Fact]
    public void Dead_unit_is_skipped_and_no_duplicate_kill_event()
    {
        var s = StateWithTank(50f, 0f);
        s.Units[0].State = UnitState.Dead;
        s.Units[0].CurrentHP = 0f;

        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);

        Assert.Equal(0, hit);
        Assert.Empty(s.RecentKills);
    }

    [Fact]
    public void Damage_ignores_faction_relations()
    {
        // ThreatAuraStepと同じ方針: ThreatRelationsを一切参照せず、全勢力のユニットへ等しく当たる。
        // （関係設定なしのままでもダメージが入ることをもって「参照していない」ことを確認する）
        var s = StateWithTank(50f, 0f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);
        Assert.Equal(1, hit);
    }

    [Fact]
    public void Degenerate_zero_length_segment_acts_as_circle()
    {
        // トライポッドの真下撃ち等、始点=終点の退化ケースは点周りの円として扱う
        var s = StateWithTank(5f, 5f, ThreatBeamStep.AlienBeamDamage + 50f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Alien, 0f, 0f, 0f, 0f);
        Assert.Equal(1, hit); // 距離√50≈7.1 < AlienBeamWidth(15)
    }
}
