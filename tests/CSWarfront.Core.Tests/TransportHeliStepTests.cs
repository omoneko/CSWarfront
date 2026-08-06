using CSWarfront.Core;
using Xunit;

/// <summary>Task101: 輸送ヘリ（維持・積載/搭乗・配送/降機・撃墜道連れ）。</summary>
public class TransportHeliStepTests
{
    private const float Far = 2000f;

    private static WarState BaseState(out Faction f, out MilitaryBase armyBase)
    {
        var s = new WarState();
        f = new Faction(0, "Red");
        s.Factions.Add(f);
        LandUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);
        armyBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        armyBase.OwnerFactionId = 0;
        s.Bases.Add(armyBase);
        return s;
    }

    private static UnitInstance AddHeli(WarState s, WorldPos pos, float load = 0f)
    {
        var u = new UnitInstance(s.AllocInstanceId(),
            AirUnitRoster.TypeKey(UnitCategory.TransportHelicopter, 1), 0, 60f, pos);
        u.SupplyLoad = load;
        s.Units.Add(u);
        return u;
    }

    [Fact]
    public void Army_bases_maintain_transport_helis_with_resources()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        SupplyTruckStep.MaintainTrucks(s); // 資源ゼロでは何も湧かない前提を揃える
        TransportHeliStep.MaintainHelis(s);
        Assert.Empty(s.Units);

        f.AddManpower(10000f);
        f.AddProduction(10000f);
        TransportHeliStep.MaintainHelis(s);
        Assert.Single(s.Units);
        TransportHeliStep.MaintainHelis(s); // 1基地1機で頭打ち
        Assert.Single(s.Units);
    }

    [Fact]
    public void Heli_loads_supplies_and_boards_idle_infantry_at_base()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        f.AddSupply(1000f);
        UnitInstance heli = AddHeli(s, new WorldPos(50, 0, 0));
        var inf1 = new UnitInstance(100, "Infantry_T1", 0, 60f, new WorldPos(20, 0, 0));
        var inf2 = new UnitInstance(101, "Infantry_T1", 0, 60f, new WorldPos(30, 0, 0));
        s.Units.Add(inf1); s.Units.Add(inf2);

        TransportHeliStep.Advance(s, 0.1f);

        Assert.Equal(1f, heli.SupplyLoad, 3);
        Assert.Equal(1000f - TransportHeliStep.CargoSupply, f.SupplyStock, 3);
        Assert.Equal(heli.InstanceId, inf1.CarriedByUnitId);
        Assert.Equal(heli.InstanceId, inf2.CarriedByUnitId);
    }

    [Fact]
    public void Loaded_heli_delivers_to_a_depot_and_disembarks_passengers()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var depot = new MilitaryBase(2, BaseType.SupplyDepot, new WorldPos(Far, 0, 0));
        depot.OwnerFactionId = 0;
        s.Bases.Add(depot);
        UnitInstance heli = AddHeli(s, new WorldPos(Far, 0, 0), load: 1f); // 拠点上空・満載
        var inf = new UnitInstance(100, "Infantry_T1", 0, 60f, new WorldPos(Far, 0, 0));
        inf.CarriedByUnitId = heli.InstanceId;
        s.Units.Add(inf);

        TransportHeliStep.Advance(s, 0.1f);

        Assert.Equal(TransportHeliStep.CargoSupply, depot.StoredSupplies, 3); // 60を荷下ろし
        Assert.Equal(0f, heli.SupplyLoad, 3);
        Assert.Null(inf.CarriedByUnitId); // 降機
        Assert.Equal(UnitState.Idle, inf.State);
    }

    [Fact]
    public void Carried_units_follow_the_heli_and_die_with_it()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        UnitInstance heli = AddHeli(s, new WorldPos(500, 0, 500), load: 1f);
        var inf = new UnitInstance(100, "Infantry_T1", 0, 60f, new WorldPos(0, 0, 0));
        inf.CarriedByUnitId = heli.InstanceId;
        s.Units.Add(inf);

        TransportHeliStep.Advance(s, 0.1f);
        Assert.Equal(500f, inf.Position.X, 1); // 位置追従

        heli.CurrentHP = 0f;
        heli.State = UnitState.Dead; // 撃墜
        TransportHeliStep.Advance(s, 0.1f);
        Assert.False(inf.IsAlive); // 道連れ
        Assert.Empty(s.RecentKills); // 無音消滅（撃破演出は出さない）
    }

    [Fact]
    public void Empty_heli_returns_to_its_army_base()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        UnitInstance heli = AddHeli(s, new WorldPos(Far, 0, Far)); // 空荷で遠方

        TransportHeliStep.Advance(s, 0.1f);

        Assert.Equal(UnitState.Moving, heli.State);
        Assert.Equal(0f, heli.OrderTargetPos.Value.X, 1); // 母基地(0,0)へ
    }

    // --- Task111 (Workshop報告「輸送ヘリが全機着陸したまま永久に動かない」) ---

    [Fact]
    public void Loaded_heli_with_nowhere_to_deliver_returns_its_cargo_home()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var depot = new MilitaryBase(2, BaseType.SupplyDepot, new WorldPos(Far, 0, 0));
        depot.OwnerFactionId = 0;
        depot.StoredSupplies = FortificationRules.StoredSupplyCap(BaseType.SupplyDepot); // 満杯
        s.Bases.Add(depot);
        UnitInstance heli = AddHeli(s, new WorldPos(Far - 100, 0, 0), load: 1f); // 満杯Depotの手前で立ち往生していた状況

        TransportHeliStep.Advance(s, 0.1f); // 配送先なし → 母基地へ向かう
        Assert.Equal(UnitState.Moving, heli.State);
        Assert.Equal(0f, heli.OrderTargetPos.Value.X, 1);

        heli.Position = new WorldPos(0, 0, 0); // 母基地に到着
        TransportHeliStep.Advance(s, 0.1f);

        Assert.Equal(0f, heli.SupplyLoad, 3);                              // 荷は勢力プールへ返した
        Assert.Equal(TransportHeliStep.CargoSupply, f.SupplyStock, 3);
    }

    [Fact]
    public void Loaded_heli_delivers_ammo_directly_to_a_dry_fort()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var artPost = new MilitaryBase(2, BaseType.ArtilleryPost, new WorldPos(Far, 0, 0));
        artPost.OwnerFactionId = 0;
        artPost.FortAmmo = 0f;
        s.Bases.Add(artPost);
        UnitInstance heli = AddHeli(s, new WorldPos(Far - 10, 0, 0), load: 1f); // 陣地のすぐ側

        TransportHeliStep.Advance(s, 0.1f);

        Assert.Equal(1f, artPost.FortAmmo, 3); // 満載60 = 6リロードぶん → 満タンまで回復
        Assert.True(heli.SupplyLoad < 1f, "expected the helicopter to have spent part of its load");
    }
}
