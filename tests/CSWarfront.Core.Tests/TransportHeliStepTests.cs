using CSWarfront.Core;
using Xunit;

/// <summary>Task101: transport helicopters (maintenance, loading/boarding, delivery/disembarking, passengers lost on shoot-down).</summary>
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
        SupplyTruckStep.MaintainTrucks(s); // establish the premise that nothing spawns with zero resources
        TransportHeliStep.MaintainHelis(s);
        Assert.Empty(s.Units);

        f.AddManpower(10000f);
        f.AddProduction(10000f);
        TransportHeliStep.MaintainHelis(s);
        Assert.Single(s.Units);
        TransportHeliStep.MaintainHelis(s); // capped at one helicopter per base
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
        UnitInstance heli = AddHeli(s, new WorldPos(Far, 0, 0), load: 1f); // above the depot, fully loaded
        var inf = new UnitInstance(100, "Infantry_T1", 0, 60f, new WorldPos(Far, 0, 0));
        inf.CarriedByUnitId = heli.InstanceId;
        s.Units.Add(inf);

        TransportHeliStep.Advance(s, 0.1f);

        Assert.Equal(TransportHeliStep.CargoSupply, depot.StoredSupplies, 3); // unloaded 60
        Assert.Equal(0f, heli.SupplyLoad, 3);
        Assert.Null(inf.CarriedByUnitId); // disembarked
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
        Assert.Equal(500f, inf.Position.X, 1); // follows the helicopter's position

        heli.CurrentHP = 0f;
        heli.State = UnitState.Dead; // shot down
        TransportHeliStep.Advance(s, 0.1f);
        Assert.False(inf.IsAlive); // lost together with the helicopter
        Assert.Empty(s.RecentKills); // silent removal (no kill effect is shown)
    }

    [Fact]
    public void Empty_heli_returns_to_its_army_base()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        UnitInstance heli = AddHeli(s, new WorldPos(Far, 0, Far)); // empty and far away

        TransportHeliStep.Advance(s, 0.1f);

        Assert.Equal(UnitState.Moving, heli.State);
        Assert.Equal(0f, heli.OrderTargetPos.Value.X, 1); // toward its home base (0,0)
    }

    // --- Task111 (Workshop report: "all transport helicopters land and then never move again") ---

    [Fact]
    public void Loaded_heli_with_nowhere_to_deliver_returns_its_cargo_home()
    {
        var s = BaseState(out Faction f, out MilitaryBase b);
        var depot = new MilitaryBase(2, BaseType.SupplyDepot, new WorldPos(Far, 0, 0));
        depot.OwnerFactionId = 0;
        depot.StoredSupplies = FortificationRules.StoredSupplyCap(BaseType.SupplyDepot); // full
        s.Bases.Add(depot);
        UnitInstance heli = AddHeli(s, new WorldPos(Far - 100, 0, 0), load: 1f); // the situation where it was stranded just short of the full depot

        TransportHeliStep.Advance(s, 0.1f); // no delivery destination -> heads to its home base
        Assert.Equal(UnitState.Moving, heli.State);
        Assert.Equal(0f, heli.OrderTargetPos.Value.X, 1);

        heli.Position = new WorldPos(0, 0, 0); // arrived at its home base
        TransportHeliStep.Advance(s, 0.1f);

        Assert.Equal(0f, heli.SupplyLoad, 3);                              // cargo was returned to the faction pool
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
        UnitInstance heli = AddHeli(s, new WorldPos(Far - 10, 0, 0), load: 1f); // right next to the fortification

        TransportHeliStep.Advance(s, 0.1f);

        Assert.Equal(1f, artPost.FortAmmo, 3); // a full load of 60 = 6 reloads' worth -> refilled to full
        Assert.True(heli.SupplyLoad < 1f, "expected the helicopter to have spent part of its load");
    }
}
