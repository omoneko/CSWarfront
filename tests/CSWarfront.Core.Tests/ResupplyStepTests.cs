using CSWarfront.Core;
using Xunit;

/// <summary>Task99: automatic production of supplies and automatic resupply within base zones.</summary>
public class ResupplyStepTests
{
    private static WarState StateWithBaseAndTank(out Faction f, out UnitInstance tank, float tankDistance)
    {
        var s = new WarState();
        f = new Faction(0, "Red");
        s.Factions.Add(f);
        LandUnitRoster.RegisterAll(s.Types);
        var b = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);
        tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(tankDistance, 0, 0));
        s.Units.Add(tank);
        return s;
    }

    // --- ProduceSupplies ---

    [Fact]
    public void Supplies_are_produced_from_production_up_to_the_cap()
    {
        var f = new Faction(0, "Red");
        f.AddProduction(1000f);

        ResupplyStep.ProduceSupplies(f);

        Assert.Equal(ResupplyStep.SupplyPerEconomyTick, f.SupplyStock, 3);
        Assert.Equal(1000f - ResupplyStep.SupplyPerEconomyTick * ResupplyStep.ProductionPerSupply, f.Production, 3);

        // Never produces beyond the cap.
        for (int i = 0; i < 100; i++) ResupplyStep.ProduceSupplies(f);
        Assert.True(f.SupplyStock <= ResupplyStep.SupplyStockCap + 0.001f);
    }

    [Fact]
    public void Supply_production_falls_back_to_funds_when_production_is_short()
    {
        var f = new Faction(0, "Red");
        f.AddProduction(20f);  // wants 50 but only has 20
        f.AddTreasury(1000f);

        ResupplyStep.ProduceSupplies(f);

        Assert.Equal(50f, f.SupplyStock, 3);
        Assert.Equal(0f, f.Production, 3);
        // The shortfall of 30 x rate 2 = 60 comes out of funds.
        Assert.Equal(1000f - 30f * UnitCosts.FundsPerProduction, f.Treasury, 3);
    }

    [Fact]
    public void Supply_production_is_partial_when_both_pools_are_short()
    {
        var f = new Faction(0, "Red");
        f.AddProduction(10f);
        f.AddTreasury(20f); // funds of 20 -> substitutes for 10 production

        ResupplyStep.ProduceSupplies(f);

        Assert.Equal(20f, f.SupplyStock, 3); // 10 (production) + 10 (funds substitute)
        Assert.Equal(0f, f.Production, 3);
        Assert.Equal(0f, f.Treasury, 3);
    }

    // --- Advance (automatic resupply within base zones) ---

    [Fact]
    public void Units_near_their_own_base_refill_and_consume_supplies()
    {
        var s = StateWithBaseAndTank(out Faction f, out UnitInstance tank, tankDistance: 100f);
        f.AddSupply(100f);
        tank.Ammo = 0f;

        ResupplyStep.Advance(s, 1f);

        Assert.Equal(ResupplyStep.RefillPerHour, tank.Ammo, 3); // 25%/h
        Assert.Equal(100f - ResupplyStep.RefillPerHour * ResupplyStep.SupplyPerFullReload, f.SupplyStock, 3);
    }

    [Fact]
    public void Units_outside_the_radius_do_not_refill()
    {
        var s = StateWithBaseAndTank(out Faction f, out UnitInstance tank, tankDistance: 500f);
        f.AddSupply(100f);
        tank.Ammo = 0f;

        ResupplyStep.Advance(s, 1f);

        Assert.Equal(0f, tank.Ammo, 3);
        Assert.Equal(100f, f.SupplyStock, 3);
    }

    [Fact]
    public void Refill_stops_when_the_supply_stock_runs_dry()
    {
        var s = StateWithBaseAndTank(out Faction f, out UnitInstance tank, tankDistance: 100f);
        f.AddSupply(1f); // a full reload needs 10 -> can only restore 0.1
        tank.Ammo = 0f;

        ResupplyStep.Advance(s, 10f); // ample time

        Assert.Equal(0.1f, tank.Ammo, 3);
        Assert.Equal(0f, f.SupplyStock, 3);

        ResupplyStep.Advance(s, 10f); // no refill with a stock of 0
        Assert.Equal(0.1f, tank.Ammo, 3);
    }

    // --- Task101: 200m automatic resupply from supply depots (SupplyDepot) ---

    [Fact]
    public void Depot_zone_refills_from_stored_supplies_not_the_faction_pool()
    {
        var s = new WarState();
        var f = new Faction(0, "Red");
        f.AddSupply(500f); // faction pool (should remain untouched)
        s.Factions.Add(f);
        LandUnitRoster.RegisterAll(s.Types);
        var depot = new MilitaryBase(1, BaseType.SupplyDepot, new WorldPos(0, 0, 0));
        depot.OwnerFactionId = 0;
        depot.StoredSupplies = 100f;
        s.Bases.Add(depot);
        var tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(100, 0, 0));
        tank.Ammo = 0f;
        s.Units.Add(tank);

        ResupplyStep.Advance(s, 1f);

        Assert.Equal(ResupplyStep.RefillPerHour, tank.Ammo, 3);
        Assert.Equal(100f - ResupplyStep.RefillPerHour * ResupplyStep.SupplyPerFullReload, depot.StoredSupplies, 3);
        Assert.Equal(500f, f.SupplyStock, 3); // the pool is not reduced
    }

    [Fact]
    public void Empty_depot_and_non_depot_fortifications_do_not_refill()
    {
        var s = new WarState();
        var f = new Faction(0, "Red");
        f.AddSupply(500f);
        s.Factions.Add(f);
        LandUnitRoster.RegisterAll(s.Types);
        var emptyDepot = new MilitaryBase(1, BaseType.SupplyDepot, new WorldPos(0, 0, 0));
        emptyDepot.OwnerFactionId = 0; // stores 0
        var bunker = new MilitaryBase(2, BaseType.Bunker, new WorldPos(50, 0, 0));
        bunker.OwnerFactionId = 0;
        var station = new MilitaryBase(3, BaseType.CargoStation, new WorldPos(-50, 0, 0));
        station.OwnerFactionId = 0;
        station.StoredSupplies = 100f; // a station is not an automatic resupply point
        s.Bases.Add(emptyDepot); s.Bases.Add(bunker); s.Bases.Add(station);
        var tank = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(20, 0, 0));
        tank.Ammo = 0f;
        s.Units.Add(tank);

        ResupplyStep.Advance(s, 1f);

        Assert.Equal(0f, tank.Ammo, 3); // no refill from anywhere (the faction pool only applies within regular base zones)
    }

    [Fact]
    public void Carrier_resupplies_its_own_aircraft_but_not_land_units()
    {
        var s = new WarState();
        var f = new Faction(0, "Red");
        f.AddSupply(100f);
        s.Factions.Add(f);
        LandUnitRoster.RegisterAll(s.Types);
        NavalUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);

        var carrier = new UnitInstance(1, "Carrier_T1", 0, 500f, new WorldPos(0, 0, 0));
        var fighter = new UnitInstance(2, "AirSuperiority_T1", 0, 100f, new WorldPos(50, 0, 0));
        var tank = new UnitInstance(3, "Tank_T1", 0, 100f, new WorldPos(50, 0, 0));
        fighter.Ammo = 0f;
        tank.Ammo = 0f;
        s.Units.Add(carrier); s.Units.Add(fighter); s.Units.Add(tank);

        ResupplyStep.Advance(s, 1f);

        Assert.True(fighter.Ammo > 0f, "expected the carrier to rearm its aircraft");
        Assert.Equal(0f, tank.Ammo, 3); // a carrier is not a resupply point for land units
    }
}
