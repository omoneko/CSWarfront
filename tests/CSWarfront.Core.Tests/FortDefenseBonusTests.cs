using CSWarfront.Core;
using Xunit;

/// <summary>Task101: 塹壕/掩蔽壕の守備ボーナス（+50%＝被ダメ÷1.5）と歩兵の陣地志向AI。</summary>
public class FortDefenseBonusTests
{
    [Fact]
    public void Infantry_on_a_trench_takes_reduced_damage_regardless_of_ownership()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var trench = new MilitaryBase(1, BaseType.Trench, new WorldPos(0, 0, 0));
        trench.OwnerFactionId = 1; // 敵所有の塹壕でも
        s.Bases.Add(trench);

        var infantry = new UnitInstance(1, "Infantry_T1", 0, 10000f, new WorldPos(5, 0, 0)); // 塹壕上
        var infantryOpen = new UnitInstance(2, "Infantry_T1", 0, 10000f, new WorldPos(200, 0, 0)); // 平地
        var enemyA = new UnitInstance(3, "Tank_T1", 1, 10000f, new WorldPos(30, 0, 0));
        var enemyB = new UnitInstance(4, "Tank_T1", 1, 10000f, new WorldPos(230, 0, 0));
        s.Units.Add(infantry); s.Units.Add(infantryOpen); s.Units.Add(enemyA); s.Units.Add(enemyB);

        CombatStep.Advance(s, 1f);

        float dmgOnTrench = 10000f - infantry.CurrentHP;
        float dmgOpen = 10000f - infantryOpen.CurrentHP;
        Assert.True(dmgOnTrench > 0f && dmgOpen > 0f);
        Assert.Equal(dmgOpen / FortDefenseBonus.DamageDivisor, dmgOnTrench, 2); // ÷1.5
    }

    [Fact]
    public void Tanks_get_no_fortification_bonus()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        LandUnitRoster.RegisterAll(s.Types);
        s.Bases.Add(new MilitaryBase(1, BaseType.Trench, new WorldPos(0, 0, 0)));
        var tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        UnitType tankType = s.Types.Get("Tank_T1");

        Assert.Equal(1f, FortDefenseBonus.Multiplier(s, tank, tankType), 3);
    }

    [Fact]
    public void Engaging_infantry_hides_behind_friendly_armor_not_buildings()
    {
        // Task104: 歩兵の建物遮蔽を廃止し、交戦中は味方装甲（Tank/Apc）の後ろに隠れる。
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var cover = new CoverMap();
        cover.Add(new WorldPos(0, 0, 50), 10f); // 建物（歩兵はもう使わない）
        s.Cover = cover;

        var infantry = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        var tank = new UnitInstance(2, "Tank_T1", 0, 100f, new WorldPos(20, 0, 0)); // 味方装甲
        var enemy = new UnitInstance(3, "Tank_T1", 1, 100f, new WorldPos(35, 0, 0));
        infantry.State = UnitState.Engaging;
        infantry.TargetId = enemy.InstanceId;
        s.Units.Add(infantry); s.Units.Add(tank); s.Units.Add(enemy);

        CoverSeekStep.Advance(s, 0.1f);

        Assert.True(infantry.CoverHold);
        Assert.True(infantry.CoverDestination.HasValue);
        // 立ち位置は「装甲の、敵と反対側」＝装甲(20)より敵(35)から遠い側。
        Assert.True(infantry.CoverDestination.Value.X < 20f,
            "expected a position behind the friendly tank (away from the enemy)");

        // 味方装甲がいなければ遮蔽なし（建物や高架下へは走らない）。
        var loneInf = new UnitInstance(4, "Infantry_T1", 0, 100f, new WorldPos(200, 0, 0));
        var loneEnemy = new UnitInstance(5, "Tank_T1", 1, 100f, new WorldPos(230, 0, 0));
        loneInf.State = UnitState.Engaging;
        loneInf.TargetId = loneEnemy.InstanceId;
        s.Units.Add(loneInf); s.Units.Add(loneEnemy);
        CoverSeekStep.Advance(s, 0.1f);
        Assert.False(loneInf.CoverHold);
    }

    [Fact]
    public void Infantry_near_enemies_moves_to_the_fort_closest_to_the_enemy()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var trenchNearEnemy = new MilitaryBase(1, BaseType.Trench, new WorldPos(200, 0, 0));
        var trenchFar = new MilitaryBase(2, BaseType.Trench, new WorldPos(-200, 0, 0));
        s.Bases.Add(trenchFar); s.Bases.Add(trenchNearEnemy);
        var infantry = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        var enemy = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(500, 0, 0));
        s.Units.Add(infantry); s.Units.Add(enemy);

        FortSeekStep.Advance(s, 0.1f);

        Assert.True(infantry.CoverHold);
        Assert.True(infantry.CoverDestination.HasValue);
        Assert.Equal(200f, infantry.CoverDestination.Value.X, 1); // 敵に近い側の塹壕
    }

    [Fact]
    public void Infantry_ignores_enemy_held_trenches_and_idles_without_enemies()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        var trench = new MilitaryBase(1, BaseType.Trench, new WorldPos(200, 0, 0));
        s.Bases.Add(trench);
        var infantry = new UnitInstance(1, "Infantry_T1", 0, 100f, new WorldPos(0, 0, 0));
        s.Units.Add(infantry);

        // 敵がいない: 何もしない。
        FortSeekStep.Advance(s, 0.1f);
        Assert.False(infantry.CoverHold);

        // 敵歩兵が塹壕を占有: その塹壕へは向かわない。
        var enemyInf = new UnitInstance(2, "Infantry_T1", 1, 100f, new WorldPos(205, 0, 0));
        s.Units.Add(enemyInf);
        FortSeekStep.Advance(s, 0.1f);
        Assert.False(infantry.CoverHold);

        // 戦車も撃てない歩兵の守り（FortSeekの対象外確認）: 戦車は陣地志向しない。
        var tank = new UnitInstance(3, "Tank_T1", 0, 100f, new WorldPos(0, 0, 50));
        s.Units.Add(tank);
        FortSeekStep.Advance(s, 0.1f);
        Assert.False(tank.CoverHold);
    }
}
