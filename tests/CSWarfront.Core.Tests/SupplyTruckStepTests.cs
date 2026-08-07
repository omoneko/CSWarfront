using CSWarfront.Core;
using Xunit;

/// <summary>Task99: supply trucks (auto-maintenance, loading, delivery, transfer, return, 30-truck cap separate from combat cap).</summary>
public class SupplyTruckStepTests
{
    private const float Far = 2000f; // Frontline distance well outside a base's resupply zone (200m)

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

        SupplyTruckStep.MaintainTrucks(s); // One truck per base per tick
        Assert.Single(s.Units);
        SupplyTruckStep.MaintainTrucks(s);
        Assert.Equal(SupplyTruckStep.TrucksPerArmyBase, s.Units.Count);
        SupplyTruckStep.MaintainTrucks(s); // Quota is full: no further increase
        Assert.Equal(SupplyTruckStep.TrucksPerArmyBase, s.Units.Count);
    }

    [Fact]
    public void Truck_spawn_is_blocked_without_resources()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        // Zero resources
        SupplyTruckStep.MaintainTrucks(s);
        Assert.Empty(s.Units);
    }

    [Fact]
    public void Faction_truck_cap_is_separate_from_the_combat_unit_cap()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        f.AddManpower(100000f);
        f.AddProduction(100000f);
        // Trucks are still maintained even when the 150-combat-unit cap has been reached (separate quota).
        for (uint i = 0; i < ProductionPlanning.MaxUnitsPerFaction; i++)
            s.Units.Add(new UnitInstance(10000 + i, "Tank_T1", 0, 100f, new WorldPos(500, 0, 500)));

        SupplyTruckStep.MaintainTrucks(s);

        int trucks = 0;
        foreach (var u in s.Units)
            if (s.Types.Get(u.TypeKey).Category == UnitCategory.SupplyTruck) trucks++;
        Assert.Equal(1, trucks);

        // Conversely, even with nothing but trucks, automatic combat-unit production keeps running
        // (they are excluded on the ProductionPlanning side).
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

    // --- Logistics loop ---

    [Fact]
    public void Empty_truck_returns_to_base_and_loads_from_the_supply_stock()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        f.AddSupply(1000f);
        var truck = AddTruck(s, new WorldPos(50, 0, 0)); // Inside the base zone, empty

        SupplyTruckStep.Advance(s, 1f);

        Assert.Equal(1f, truck.SupplyLoad, 3); // Fully loaded
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
        tankWorse.Ammo = 0.1f; // This one has the least ammo
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
        // Load consumed = 2 units x 0.5 restored x 0.2 = 0.2
        Assert.Equal(1f - 2f * SupplyTruckStep.TransferPerHour * SupplyTruckStep.LoadPerFullReload,
            truck.SupplyLoad, 3);

        // Once its load is spent, the truck returns to base.
        truck.SupplyLoad = 0f;
        SupplyTruckStep.Advance(s, 0.1f);
        Assert.Equal(UnitState.Moving, truck.State);
        Assert.Equal(0f, truck.OrderTargetPos.Value.X, 1); // Toward the base at (0,0)
    }

    [Fact]
    public void Units_inside_a_base_resupply_zone_are_not_truck_targets()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var truck = AddTruck(s, new WorldPos(50, 0, 0), load: 1f);
        var tankAtBase = new UnitInstance(100, "Tank_T1", 0, 100f, new WorldPos(100, 0, 0)); // Inside the base zone
        tankAtBase.Ammo = 0f;
        s.Units.Add(tankAtBase);

        SupplyTruckStep.Advance(s, 0.1f);

        Assert.Equal(UnitState.Idle, truck.State); // No delivery target = waits near the base
    }

    // --- Task101: interaction with supply depots ---

    [Fact]
    public void Idle_loaded_truck_hauls_pool_supplies_into_a_depot()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var depot = new MilitaryBase(2, BaseType.SupplyDepot, new WorldPos(Far, 0, 0));
        depot.OwnerFactionId = 0;
        s.Bases.Add(depot);
        var truck = AddTruck(s, new WorldPos(Far, 0, 0), load: 1f); // Right next to the depot, cargo came from the pool

        SupplyTruckStep.Advance(s, 0.1f);

        Assert.Equal(1f * SupplyTruckStep.SupplyPerTruckLoad, depot.StoredSupplies, 3); // Unloads the 30
        Assert.Equal(0f, truck.SupplyLoad, 3);
    }

    [Fact]
    public void Depot_loaded_cargo_is_never_hauled_back_into_depots()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var depot = new MilitaryBase(2, BaseType.SupplyDepot, new WorldPos(Far, 0, 0));
        depot.OwnerFactionId = 0;
        s.Bases.Add(depot);
        var truck = AddTruck(s, new WorldPos(Far, 0, 0), load: 1f);
        truck.SupplyLoadFromDepot = true; // Cargo that was loaded at a depot

        SupplyTruckStep.Advance(s, 0.1f);

        Assert.Equal(0f, depot.StoredSupplies, 3); // Not re-deposited (prevents shuffling)
        Assert.Equal(1f, truck.SupplyLoad, 3);
    }

    [Fact]
    public void Empty_truck_reloads_at_the_nearest_stocked_depot()
    {
        var s = BaseState(out Faction f, out MilitaryBase b); // Faction pool is empty
        var depot = new MilitaryBase(2, BaseType.SupplyDepot, new WorldPos(Far, 0, 0));
        depot.OwnerFactionId = 0;
        depot.StoredSupplies = 100f;
        s.Bases.Add(depot);
        var truck = AddTruck(s, new WorldPos(Far + 10, 0, 0)); // Right beside the depot, empty

        SupplyTruckStep.Advance(s, 0.1f);

        Assert.Equal(1f, truck.SupplyLoad, 3); // Fully loaded from the depot's stockpile
        Assert.True(truck.SupplyLoadFromDepot);
        Assert.Equal(100f - SupplyTruckStep.SupplyPerTruckLoad, depot.StoredSupplies, 3);
    }

    // --- Task111 (Workshop report: "artillery positions never receive supplies") ---

    [Fact]
    public void Loaded_truck_drives_to_a_dry_fort_far_from_any_base()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var artPost = new MilitaryBase(2, BaseType.ArtilleryPost, new WorldPos(Far, 0, 0));
        artPost.OwnerFactionId = 0;
        artPost.FortAmmo = 0f; // Out of ammo, outside every base/depot 200m zone
        s.Bases.Add(artPost);
        var truck = AddTruck(s, new WorldPos(100, 0, 0), load: 1f);

        SupplyTruckStep.Advance(s, 0.1f);

        Assert.Equal(UnitState.Moving, truck.State);
        Assert.Equal(Far, truck.OrderTargetPos.Value.X, 1); // Heads for the artillery position
    }

    [Fact]
    public void Truck_transfers_ammo_into_the_fort_on_arrival()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var artPost = new MilitaryBase(2, BaseType.ArtilleryPost, new WorldPos(Far, 0, 0));
        artPost.OwnerFactionId = 0;
        artPost.FortAmmo = 0f;
        s.Bases.Add(artPost);
        var truck = AddTruck(s, new WorldPos(Far + 10, 0, 0), load: 1f); // Right beside the position

        SupplyTruckStep.Advance(s, 1f);

        Assert.True(artPost.FortAmmo > 0f, "expected the truck to refill the artillery position's ammo");
        Assert.True(truck.SupplyLoad < 1f, "expected the truck to have spent some of its load");
    }

    [Fact]
    public void Forts_inside_a_base_supply_zone_are_left_to_auto_resupply()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var bunker = new MilitaryBase(2, BaseType.Bunker, new WorldPos(100, 0, 0)); // Inside the base's 200m zone
        bunker.OwnerFactionId = 0;
        bunker.FortAmmo = 0f;
        s.Bases.Add(bunker);
        var truck = AddTruck(s, new WorldPos(50, 0, 0), load: 1f);

        SupplyTruckStep.Advance(s, 0.1f);

        // Fortifications inside a base zone are restored by ResupplyStep, so they are not truck delivery targets.
        Assert.NotEqual(UnitState.Moving, truck.State);
    }

    [Fact]
    public void Player_held_trucks_are_left_alone()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        f.AddSupply(1000f);
        var truck = AddTruck(s, new WorldPos(50, 0, 0));
        truck.Order = UnitOrder.Hold;

        SupplyTruckStep.Advance(s, 1f);

        Assert.Equal(0f, truck.SupplyLoad, 3); // Not touched at all
    }
}
