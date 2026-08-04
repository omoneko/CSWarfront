using CSWarfront.Core;
using Xunit;

/// <summary>Task101: 築城の自動射撃（掩蔽壕=射線判定つき単体射撃、砲兵陣地=範囲射撃、弾薬）。</summary>
public class FortCombatStepTests
{
    private static WarState StateWithFort(BaseType fortType, out MilitaryBase fort)
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        LandUnitRoster.RegisterAll(s.Types);
        fort = new MilitaryBase(1, fortType, new WorldPos(0, 0, 0));
        fort.OwnerFactionId = 0;
        fort.MaxHP = fort.CurrentHP = FortificationRules.DefaultMaxHP(fortType);
        s.Bases.Add(fort);
        return s;
    }

    private static UnitInstance AddEnemyTank(WarState s, float x, float z, uint id = 100)
    {
        var u = new UnitInstance(id, "Tank_T1", 1, 1000f, new WorldPos(x, 0, z));
        s.Units.Add(u);
        return u;
    }

    [Fact]
    public void Bunker_fires_at_hostiles_in_range_and_consumes_ammo()
    {
        var s = StateWithFort(BaseType.Bunker, out MilitaryBase bunker);
        UnitInstance enemy = AddEnemyTank(s, 100, 0);
        UnitInstance farEnemy = AddEnemyTank(s, 500, 0, 101); // 射程外

        FortCombatStep.Advance(s, 1f);

        Assert.True(enemy.CurrentHP < 1000f, "expected bunker damage");
        Assert.Equal(1000f, farEnemy.CurrentHP, 3);
        Assert.Equal(1f - 1f / FortCombatStep.BunkerAmmoHours, bunker.FortAmmo, 3);
    }

    [Fact]
    public void Bunker_does_not_shoot_through_buildings()
    {
        var s = StateWithFort(BaseType.Bunker, out MilitaryBase bunker);
        UnitInstance enemy = AddEnemyTank(s, 100, 0);
        var cover = new CoverMap();
        cover.Add(new WorldPos(50, 0, 0), 10f); // 射線のど真ん中に建物
        s.Cover = cover;

        FortCombatStep.Advance(s, 1f);

        Assert.Equal(1000f, enemy.CurrentHP, 3); // 建物非貫通
        Assert.Equal(1f, bunker.FortAmmo, 3);    // 撃っていないので弾薬も減らない
    }

    [Fact]
    public void Artillery_post_splashes_all_hostiles_near_the_target()
    {
        var s = StateWithFort(BaseType.ArtilleryPost, out MilitaryBase post);
        UnitInstance a = AddEnemyTank(s, 100, 0, 100);
        UnitInstance b = AddEnemyTank(s, 100, 20, 101); // 目標から20m=Splash(30)内
        UnitInstance c = AddEnemyTank(s, 100, 90, 102); // Splash外（射程内でも巻き込まれない）

        FortCombatStep.Advance(s, 1f);

        Assert.True(a.CurrentHP < 1000f);
        Assert.True(b.CurrentHP < 1000f);
        Assert.Equal(1000f, c.CurrentHP, 3);
    }

    [Fact]
    public void Dry_or_neutralized_forts_do_not_fire()
    {
        var s = StateWithFort(BaseType.Bunker, out MilitaryBase bunker);
        UnitInstance enemy = AddEnemyTank(s, 100, 0);

        bunker.FortAmmo = 0f; // 弾切れ
        FortCombatStep.Advance(s, 1f);
        Assert.Equal(1000f, enemy.CurrentHP, 3);

        bunker.FortAmmo = 1f;
        bunker.OwnerFactionId = null; // 機能停止（中立化）
        FortCombatStep.Advance(s, 1f);
        Assert.Equal(1000f, enemy.CurrentHP, 3);
    }

    [Fact]
    public void Forts_refill_ammo_next_to_a_stocked_depot()
    {
        var s = StateWithFort(BaseType.Bunker, out MilitaryBase bunker);
        bunker.FortAmmo = 0f;
        var depot = new MilitaryBase(2, BaseType.SupplyDepot, new WorldPos(100, 0, 0));
        depot.OwnerFactionId = 0;
        depot.StoredSupplies = 100f;
        s.Bases.Add(depot);

        ResupplyStep.Advance(s, 1f);

        Assert.Equal(ResupplyStep.RefillPerHour, bunker.FortAmmo, 3);
        Assert.True(depot.StoredSupplies < 100f);
    }
}
