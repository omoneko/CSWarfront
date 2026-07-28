using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class CombatStepTests
{
    private static WarState TwoHostileTanks(float distance)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        s.Units.Add(new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(distance, 0f, 0f)));
        return s;
    }

    [Fact]
    public void Units_in_range_damage_each_other()
    {
        var s = TwoHostileTanks(50f); // range 60 内
        CombatStep.Advance(s, 1f);
        // Task28: Tank_T1.Armor is now 10 (was 5). DamagePerHit(40,10)=30.
        // Task38: Tank_T1.Accuracy=0.70 (no drone synergy applies to non-Artillery), matchup Tank->Tank=1.0.
        // dmg = 30 * dt(1) * matchup(1.0) * accuracy(0.70) = 21 -> 100-21=79
        // (old arithmetic, pre-accuracy: 100-30=70)
        Assert.Equal(79f, s.FindUnit(1).CurrentHP, 3);
        Assert.Equal(79f, s.FindUnit(2).CurrentHP, 3);
        Assert.Equal(UnitState.Engaging, s.FindUnit(1).State);
    }

    [Fact]
    public void Units_out_of_range_do_not_engage()
    {
        var s = TwoHostileTanks(100f); // range 60 外
        CombatStep.Advance(s, 1f);
        Assert.Equal(100f, s.FindUnit(1).CurrentHP, 3);
        Assert.NotEqual(UnitState.Engaging, s.FindUnit(1).State);
    }

    [Fact]
    public void Unit_dies_when_hp_reaches_zero()
    {
        var s = TwoHostileTanks(50f);
        // Task38: dmg = DamagePerHit(40,10)=30 * matchup(1.0) * accuracy(0.70) = 21 (dt=1) で死亡させる。
        s.FindUnit(2).CurrentHP = 15f;
        CombatStep.Advance(s, 1f);
        Assert.Equal(UnitState.Dead, s.FindUnit(2).State);
    }

    // --- Task35: 撃破報酬（Research.KillReward）の付与 ---

    [Fact]
    public void Killing_a_unit_awards_research_points_to_the_killers_faction()
    {
        var s = TwoHostileTanks(50f);
        // Task38: dies this tick (dmg = DamagePerHit(40,10)=30 * accuracy(0.70) = 21 >= 15)
        s.FindUnit(2).CurrentHP = 15f;
        CombatStep.Advance(s, 1f);

        // Tank_T1.Cost = 60, KillRewardRate = 0.5 -> 30
        Assert.Equal(30f, s.FindFaction(0).ResearchPoints, 3); // unit 1 (Red) got the kill
        Assert.Equal(0f, s.FindFaction(1).ResearchPoints, 3);  // Blue lost its unit, no reward
    }

    [Fact]
    public void Killing_a_unit_does_not_award_research_points_when_target_survives()
    {
        var s = TwoHostileTanks(50f); // both survive this tick
        CombatStep.Advance(s, 1f);

        Assert.Equal(0f, s.FindFaction(0).ResearchPoints, 3);
        Assert.Equal(0f, s.FindFaction(1).ResearchPoints, 3);
    }

    [Fact]
    public void Damage_scales_linearly_with_dt()
    {
        var full = TwoHostileTanks(50f);
        CombatStep.Advance(full, 1f);
        // Task38: DamagePerHit(40,10)=30 * matchup(1.0) * accuracy(0.70) = 21 (dt=1)
        // (old arithmetic, pre-accuracy: 30)
        float fullDmg = 100f - full.FindUnit(1).CurrentHP;

        var half = TwoHostileTanks(50f);
        CombatStep.Advance(half, 0.5f);
        float halfDmg = 100f - half.FindUnit(1).CurrentHP; // 10.5 (old arithmetic, pre-accuracy: 15)

        Assert.Equal(fullDmg / 2f, halfDmg, 3);
        // 100 - 30*0.70*0.5 = 89.5 (old arithmetic, pre-accuracy: 100 - 30*0.5 = 85)
        Assert.Equal(89.5f, half.FindUnit(1).CurrentHP, 3);
    }

    // --- Task29: CombatMatchup適用 ---

    [Fact]
    public void DroneInfantry_deals_double_damage_against_tank()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.DroneInfantry, 1)); // Attack=30, Range=90
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));          // Armor=10, Range=60
        // Drone(id1) attacks Tank(id2). distance=50: within Drone's range(90) and Tank's range(60),
        // so both engage each other this tick.
        s.Units.Add(new UnitInstance(1, "DroneInfantry_T1", 0, 200f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(50f, 0f, 0f)));

        CombatStep.Advance(s, 1f);

        // DamagePerHit(Drone.Attack=30, Tank.Armor=10) = 20, × CombatMatchup(Drone->Tank)=2.0 × dt(1)
        // × DroneInfantry.Accuracy(0.85, Task38; DroneInfantry is not Artillery so gets no synergy bonus,
        //   just its own base accuracy) = 34 -> 200-34=166 (old arithmetic, pre-accuracy: 200-40=160)
        Assert.Equal(166f, s.FindUnit(2).CurrentHP, 3);
    }

    [Fact]
    public void Infantry_deals_reduced_damage_against_tank()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Infantry, 1)); // Attack=20, Range=40
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));     // Armor=10, Range=60
        // Infantry(id1) attacks Tank(id2). distance=30: within Infantry's range(40) and Tank's range(60).
        s.Units.Add(new UnitInstance(1, "Infantry_T1", 0, 200f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(30f, 0f, 0f)));

        CombatStep.Advance(s, 1f);

        // DamagePerHit(Infantry.Attack=20, Tank.Armor=10) = 10, × CombatMatchup(Infantry->Tank)=0.4 × dt(1)
        // × Infantry.Accuracy(0.75, Task38) = 3 -> 200-3=197 (old arithmetic, pre-accuracy: 200-4=196)
        Assert.Equal(197f, s.FindUnit(2).CurrentHP, 3);
    }

    [Fact]
    public void Matchup_damage_scales_with_dt()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.DroneInfantry, 1));
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));
        s.Units.Add(new UnitInstance(1, "DroneInfantry_T1", 0, 200f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(50f, 0f, 0f)));

        CombatStep.Advance(s, 0.5f);

        // DamagePerHit(30,10)=20 × 2.0 × dt(0.5) × accuracy(0.85, Task38) = 17
        // (old arithmetic, pre-accuracy: 200-20=180)
        Assert.Equal(183f, s.FindUnit(2).CurrentHP, 3);
    }

    // --- Task38: 命中率(Accuracy)の適用、ドローン観測支援シナジー ---

    [Fact]
    public void Artillery_alone_deals_reduced_expected_damage_due_to_low_accuracy()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Artillery, 1)); // Attack=50, Range=120, Accuracy=0.35
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));     // Armor=10
        s.Units.Add(new UnitInstance(1, "Artillery_T1", 0, 200f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(100f, 0f, 0f))); // out of Tank's range(60), only Artillery fires

        CombatStep.Advance(s, 1f);

        // DamagePerHit(Artillery.Attack=50, Tank.Armor=10) = 40, × CombatMatchup(Artillery->Tank)=0.7
        // × dt(1) × Artillery.Accuracy(0.35, no drone nearby) = 9.8 -> 200-9.8=190.2
        Assert.Equal(190.2f, s.FindUnit(2).CurrentHP, 2);
    }

    [Fact]
    public void Artillery_with_nearby_friendly_drone_deals_much_more_expected_damage()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Artillery, 1));
        s.Types.Register(LandUnitRoster.Get(UnitCategory.Tank, 1));
        s.Types.Register(LandUnitRoster.Get(UnitCategory.DroneInfantry, 1));
        s.Units.Add(new UnitInstance(1, "Artillery_T1", 0, 200f, new WorldPos(0f, 0f, 0f)));
        s.Units.Add(new UnitInstance(2, "Tank_T1", 1, 200f, new WorldPos(100f, 0f, 0f)));
        // Friendly DroneInfantry within CombatSynergy.DroneSpotterRadius(150) of the Artillery (id1),
        // but placed at x=5 so it is itself out of its own attack range (90) of the Tank (distance 95)
        // and out of the Tank's range (60) too — it purely spots, it does not also join the fight
        // (which would otherwise add its own Drone->Tank damage on top and confound this assertion).
        s.Units.Add(new UnitInstance(3, "DroneInfantry_T1", 0, 100f, new WorldPos(5f, 0f, 0f)));

        CombatStep.Advance(s, 1f);

        // effective accuracy = min(0.95, 0.35 + DroneSpotterAccuracyBonus(0.5)) = 0.85
        // DamagePerHit(50,10)=40 × matchup(0.7) × dt(1) × 0.85 = 23.8 -> 200-23.8=176.2
        // (versus 190.2 without the drone above: the synergy visibly raises artillery damage)
        Assert.Equal(176.2f, s.FindUnit(2).CurrentHP, 2);
    }

    // --- Task42: 発砲エフェクト(ShotEvent)の間引き ---

    [Fact]
    public void Unit_with_no_target_emits_no_shots()
    {
        var s = TwoHostileTanks(100f); // range 60 外、どちらも交戦しない
        CombatStep.Advance(s, 1f);
        Assert.Empty(s.RecentShots);
    }

    [Fact]
    public void Firing_unit_emits_exactly_one_shot_event_on_the_first_tick_it_deals_damage()
    {
        var s = TwoHostileTanks(50f); // range 60 内、両者交戦
        CombatStep.Advance(s, 0.01f); // FireCooldown既定0なので、ダメージを与えた瞬間に必ず1発出る

        var fromUnit1 = s.RecentShots.FindAll(e => e.FactionId == 0);
        Assert.Single(fromUnit1);
        Assert.Equal(ShotKind.DirectFire, fromUnit1[0].Kind); // Tank
        Assert.Equal(0f, fromUnit1[0].From.X, 3);
        Assert.Equal(0f, fromUnit1[0].From.Z, 3);
        Assert.Equal(50f, fromUnit1[0].To.X, 3); // 標的(unit2)の位置
        Assert.Equal((byte)0, fromUnit1[0].FactionId);
    }

    [Fact]
    public void Firing_unit_emits_at_most_one_shot_per_its_own_fire_interval()
    {
        // Tank.FireIntervalHours = 0.25h（LandUnitRoster）。dt=0.1hずつ8回進める＝合計0.8h(=3.2間隔分)。
        // dt=0.1はFireIntervalHours(0.25)を割り切らない値を意図的に選んでいる：発火/非発火の判定が
        // 常にゼロから明確に離れた値(±0.05や±0.1)になり、浮動小数点の丸め誤差でゼロ境界の判定が
        // 揺れる心配がない（決定的シミュレーションのテストとして頑健にするため）。
        var s = TwoHostileTanks(50f);
        int shotsFromUnit1 = 0;
        for (int i = 0; i < 8; i++)
        {
            s.RecentShots.Clear();
            CombatStep.Advance(s, 0.1f);
            shotsFromUnit1 += s.RecentShots.FindAll(e => e.FactionId == 0).Count;
        }

        // FireCooldown推移（unit1、Tank_T1、FireIntervalHours=0.25）:
        //   step1: 0-0.1=-0.1<=0 → 発砲、reset 0.25
        //   step2: 0.25-0.1=0.15  step3: 0.15-0.1=0.05  step4: 0.05-0.1=-0.05<=0 → 発砲、reset 0.25
        //   step5: 0.15  step6: 0.05  step7: -0.05<=0 → 発砲、reset 0.25  step8: 0.15
        // 合計3発（乱数不使用・決定的：毎回同じ値になる）。
        Assert.Equal(3, shotsFromUnit1);
    }

    [Fact]
    public void RecentShots_is_capped_at_MaxRecentShotsPerTick_even_with_a_huge_battle()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions.Add(new Faction(1, "Blue"));
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Types.Register(MvpUnitTypes.Tank_T1());

        // WarState.MaxRecentShotsPerTick(200)を大きく超える数の交戦ペアを作る。各ペアは互いの射程内で、
        // 他ペアとは十分離れているためTargetSearchが自分のペア相手だけを選ぶ（干渉しない）。
        const int pairs = 130; // 130ペア×2ユニット=260ユニット、両方向で最大520発の"要求"が出る想定
        uint nextId = 1;
        for (int p = 0; p < pairs; p++)
        {
            float baseX = p * 1000f;
            s.Units.Add(new UnitInstance(nextId++, "Tank_T1", 0, 100f, new WorldPos(baseX, 0f, 0f)));
            s.Units.Add(new UnitInstance(nextId++, "Tank_T1", 1, 100f, new WorldPos(baseX + 50f, 0f, 0f)));
        }

        CombatStep.Advance(s, 0.01f);

        Assert.Equal(WarState.MaxRecentShotsPerTick, s.RecentShots.Count);
    }
}
