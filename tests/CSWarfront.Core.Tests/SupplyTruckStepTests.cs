using CSWarfront.Core;
using Xunit;

/// <summary>Task99: 補給トラック（自動維持・積載・配送・転送・帰還・上限30台別枠）。</summary>
public class SupplyTruckStepTests
{
    private const float Far = 2000f; // 基地の補給圏(200m)から十分離れた前線距離

    private static WarState BaseState(out Faction f, out MilitaryBase armyBase)
    {
        var s = new WarState();
        f = new Faction(0, "Red");
        s.Factions.Add(f);
        LandUnitRoster.RegisterAll(s.Types);
        armyBase = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        armyBase.OwnerFactionId = 0;
        s.Bases.Add(armyBase);
        return s;
    }

    private static UnitInstance AddTruck(WarState s, WorldPos pos, float load = 0f)
    {
        var u = new UnitInstance(s.AllocInstanceId(), LandUnitRoster.TypeKey(UnitCategory.SupplyTruck, 1),
            0, 40f, pos);
        u.SupplyLoad = load;
        s.Units.Add(u);
        return u;
    }

    // --- MaintainTrucks ---

    [Fact]
    public void Army_bases_spawn_trucks_up_to_the_per_base_quota()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        f.AddManpower(10000f);
        f.AddProduction(10000f);

        SupplyTruckStep.MaintainTrucks(s); // 1回のtickで1基地1台
        Assert.Single(s.Units);
        SupplyTruckStep.MaintainTrucks(s);
        Assert.Equal(SupplyTruckStep.TrucksPerArmyBase, s.Units.Count);
        SupplyTruckStep.MaintainTrucks(s); // 枠いっぱい: これ以上増えない
        Assert.Equal(SupplyTruckStep.TrucksPerArmyBase, s.Units.Count);
    }

    [Fact]
    public void Truck_spawn_is_blocked_without_resources()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        // 資源ゼロ
        SupplyTruckStep.MaintainTrucks(s);
        Assert.Empty(s.Units);
    }

    [Fact]
    public void Faction_truck_cap_is_separate_from_the_combat_unit_cap()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        f.AddManpower(100000f);
        f.AddProduction(100000f);
        // 戦闘150体上限に達している状態でもトラックは維持される（別枠）。
        for (uint i = 0; i < ProductionPlanning.MaxUnitsPerFaction; i++)
            s.Units.Add(new UnitInstance(10000 + i, "Tank_T1", 0, 100f, new WorldPos(500, 0, 500)));

        SupplyTruckStep.MaintainTrucks(s);

        int trucks = 0;
        foreach (var u in s.Units)
            if (s.Types.Get(u.TypeKey).Category == UnitCategory.SupplyTruck) trucks++;
        Assert.Equal(1, trucks);

        // 逆に、トラックだらけでも戦闘ユニットの自動生産は止まらない（ProductionPlanning側の除外）。
        f.UnlockedTier = 5;
        s.Units.RemoveAll(u => s.Types.Get(u.TypeKey).Category != UnitCategory.SupplyTruck);
        ProductionPlanning.Advance(s);
        Assert.NotEmpty(s.Bases[0].Queue);
    }

    [Fact]
    public void Invader_faction_never_maintains_trucks()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        InvasionEvents.EnsureInvaderFaction(s);
        var invaderBase = new MilitaryBase(2, BaseType.Army, new WorldPos(100, 0, 100));
        invaderBase.OwnerFactionId = Faction.InvaderFactionId;
        s.Bases.Add(invaderBase);
        s.FindFaction(Faction.InvaderFactionId).AddManpower(10000f);
        s.FindFaction(Faction.InvaderFactionId).AddProduction(10000f);

        SupplyTruckStep.MaintainTrucks(s);

        foreach (var u in s.Units)
            Assert.NotEqual(Faction.InvaderFactionId, u.FactionId);
    }

    // --- 兵站ループ ---

    [Fact]
    public void Empty_truck_returns_to_base_and_loads_from_the_supply_stock()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        f.AddSupply(1000f);
        var truck = AddTruck(s, new WorldPos(50, 0, 0)); // 基地圏内・空荷

        SupplyTruckStep.Advance(s, 1f);

        Assert.Equal(1f, truck.SupplyLoad, 3); // 満載
        Assert.Equal(1000f - SupplyTruckStep.SupplyPerTruckLoad, f.SupplyStock, 3);
    }

    [Fact]
    public void Loaded_truck_moves_toward_the_neediest_frontline_unit()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var truck = AddTruck(s, new WorldPos(50, 0, 0), load: 1f);
        var tankNear = new UnitInstance(100, "Tank_T1", 0, 100f, new WorldPos(Far, 0, 0));
        tankNear.Ammo = 0.4f;
        var tankWorse = new UnitInstance(101, "Tank_T1", 0, 100f, new WorldPos(0, 0, Far));
        tankWorse.Ammo = 0.1f; // こちらが最も弾薬が少ない
        s.Units.Add(tankNear); s.Units.Add(tankWorse);

        SupplyTruckStep.Advance(s, 0.1f);

        Assert.Equal(UnitState.Moving, truck.State);
        Assert.True(truck.OrderTargetPos.HasValue);
        Assert.Equal(0f, truck.OrderTargetPos.Value.X, 1);
        Assert.Equal(Far, truck.OrderTargetPos.Value.Z, 1);
    }

    [Fact]
    public void Truck_transfers_ammo_to_all_nearby_allies_and_returns_when_empty()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var truck = AddTruck(s, new WorldPos(Far, 0, 0), load: 1f);
        var tank1 = new UnitInstance(100, "Tank_T1", 0, 100f, new WorldPos(Far + 20, 0, 0));
        var tank2 = new UnitInstance(101, "Tank_T1", 0, 100f, new WorldPos(Far - 20, 0, 0));
        tank1.Ammo = 0f; tank2.Ammo = 0f;
        s.Units.Add(tank1); s.Units.Add(tank2);

        SupplyTruckStep.Advance(s, 1f);

        Assert.Equal(SupplyTruckStep.TransferPerHour, tank1.Ammo, 3); // 0.5/h
        Assert.Equal(SupplyTruckStep.TransferPerHour, tank2.Ammo, 3);
        // 積載消費 = 2ユニット × 0.5回復 × 0.2 = 0.2
        Assert.Equal(1f - 2f * SupplyTruckStep.TransferPerHour * SupplyTruckStep.LoadPerFullReload,
            truck.SupplyLoad, 3);

        // 積載を使い切ると基地へ帰還する。
        truck.SupplyLoad = 0f;
        SupplyTruckStep.Advance(s, 0.1f);
        Assert.Equal(UnitState.Moving, truck.State);
        Assert.Equal(0f, truck.OrderTargetPos.Value.X, 1); // 基地(0,0)へ
    }

    [Fact]
    public void Units_inside_a_base_resupply_zone_are_not_truck_targets()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var truck = AddTruck(s, new WorldPos(50, 0, 0), load: 1f);
        var tankAtBase = new UnitInstance(100, "Tank_T1", 0, 100f, new WorldPos(100, 0, 0)); // 基地圏内
        tankAtBase.Ammo = 0f;
        s.Units.Add(tankAtBase);

        SupplyTruckStep.Advance(s, 0.1f);

        Assert.Equal(UnitState.Idle, truck.State); // 配送対象なし＝基地付近で待機
    }

    [Fact]
    public void Player_held_trucks_are_left_alone()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        f.AddSupply(1000f);
        var truck = AddTruck(s, new WorldPos(50, 0, 0));
        truck.Order = UnitOrder.Hold;

        SupplyTruckStep.Advance(s, 1f);

        Assert.Equal(0f, truck.SupplyLoad, 3); // 一切触られない
    }
}
