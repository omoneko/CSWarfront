using CSWarfront.Core;
using Xunit;

/// <summary>Task101: military cargo trains (station-pair enumeration, maintenance, loading/boarding, transport/disembarking, total loss on destruction).</summary>
public class TrainStepTests
{
    private const float Span = 3000f; // distance between stations (greater than MinStationDistance=2000)

    /// <summary>Task110: at a station the sequence is "cargo handling -> StationDwellHours stop -> departure",
    /// so use this when a test wants to run a single arrival all the way through
    /// (cargo handling -> consume the dwell time -> departure check).</summary>
    private static void ServiceAndDepart(WarState s)
    {
        TrainStep.Advance(s, 0.1f);                          // handle cargo and enter the dwell stop
        TrainStep.Advance(s, TrainStep.StationDwellHours);   // consume the dwell time
        TrainStep.Advance(s, 0.1f);                          // departure check
    }

    /// <summary>Two stations ((0,0) and (Span,0)) connected by a straight rail line. The army base is on the (0,0) side.</summary>
    private static WarState RailState(out Faction f, out MilitaryBase stationA, out MilitaryBase stationB)
    {
        var s = new WarState();
        f = new Faction(0, "Red");
        s.Factions.Add(f);
        LandUnitRoster.RegisterAll(s.Types);

        var rails = new RoadGraph();
        for (ushort n = 0; n <= 10; n++)
            rails.AddNode(n, new WorldPos(n * (Span / 10f), 1, 0));
        for (ushort n = 0; n < 10; n++)
            rails.AddEdge(n, (ushort)(n + 1));
        s.Rails = rails;

        var army = new MilitaryBase(1, BaseType.Army, new WorldPos(0, 0, 100));
        army.OwnerFactionId = 0;
        s.Bases.Add(army);

        stationA = new MilitaryBase(10, BaseType.CargoStation, new WorldPos(0, 0, 0));
        stationA.OwnerFactionId = 0;
        stationB = new MilitaryBase(11, BaseType.CargoStation, new WorldPos(Span, 0, 0));
        stationB.OwnerFactionId = 0;
        s.Bases.Add(stationA); s.Bases.Add(stationB);
        CargoStationRules.RefreshConnectivity(s);
        return s;
    }

    [Fact]
    public void Connected_stations_form_a_pair_and_maintain_one_train()
    {
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        Assert.True(a.RailConnected);
        Assert.True(b.RailConnected);
        Assert.Single(TrainStep.FindStationPairs(s, 0));

        f.AddManpower(10000f);
        f.AddProduction(10000f);
        TrainStep.MaintainTrains(s);
        Assert.Single(s.Units); // 1 pair = 1 train
        TrainStep.MaintainTrains(s);
        Assert.Single(s.Units); // does not grow
    }

    [Fact]
    public void Disconnected_or_close_stations_form_no_pair()
    {
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        b.Position = new WorldPos(200, 0, 0); // under MinStationDistance(400)
        Assert.Empty(TrainStep.FindStationPairs(s, 0));

        b.Position = new WorldPos(Span, 0, 0);
        s.Rails = new RoadGraph(); // rails removed
        CargoStationRules.RefreshConnectivity(s);
        Assert.False(a.RailConnected);
        Assert.Empty(TrainStep.FindStationPairs(s, 0));
    }

    [Fact]
    public void Train_hauls_supplies_from_home_station_to_the_front_station()
    {
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        f.AddSupply(1000f);
        var train = new UnitInstance(100, LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1), 0, 500f,
            new WorldPos(0, 1, 0)); // stopped at station A, the base-side station
        s.Units.Add(train);

        TrainStep.Advance(s, 0.1f); // handle cargo (load) and stay stopped in place
        Assert.Equal(1f, train.SupplyLoad, 3);
        Assert.Equal(1000f - TrainStep.CargoSupply, f.SupplyStock, 3);
        Assert.Equal(UnitState.Idle, train.State); // Task110: does not depart yet while dwelling

        TrainStep.Advance(s, TrainStep.StationDwellHours);
        TrainStep.Advance(s, 0.1f);
        Assert.Equal(UnitState.Moving, train.State); // departs after the dwell time is over

        // Run the trip (MovementStep rail travel) until arrival.
        for (int i = 0; i < 200 && train.SupplyLoad > 0f; i++)
        {
            MovementStep.Advance(s, 1f);
            TrainStep.Advance(s, 1f);
        }

        Assert.Equal(0f, train.SupplyLoad, 3);
        Assert.Equal(TrainStep.CargoSupply, b.StoredSupplies, 3); // into the front-side station's stockpile
    }

    [Fact]
    public void Units_heading_far_ride_the_train_and_resume_marching()
    {
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        var train = new UnitInstance(100, LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1), 0, 500f,
            new WorldPos(0, 1, 0));
        s.Units.Add(train);

        var tank = new UnitInstance(101, "Tank_T1", 0, 100f, new WorldPos(50, 0, 0));
        tank.State = UnitState.Moving;
        tank.OrderTargetPos = new WorldPos(Span + 500f, 0, 0); // the front = beyond station B (B is more than 1 km closer)
        s.Units.Add(tank);

        var nearTank = new UnitInstance(102, "Tank_T1", 0, 100f, new WorldPos(60, 0, 0));
        nearTank.State = UnitState.Moving;
        nearTank.OrderTargetPos = new WorldPos(300, 0, 0); // short-range objective = does not board
        s.Units.Add(nearTank);

        TrainStep.Advance(s, 0.1f);

        Assert.Equal(train.InstanceId, tank.CarriedByUnitId);
        Assert.Null(nearTank.CarriedByUnitId);

        // Transport -> arrival -> disembark -> resume marching on its own.
        for (int i = 0; i < 200 && tank.IsCarried; i++)
        {
            MovementStep.Advance(s, 1f);
            TransportHeliStep.Advance(s, 1f); // position tracking while aboard (shared mechanism)
            TrainStep.Advance(s, 1f);
        }

        Assert.False(tank.IsCarried);
        Assert.True(tank.Position.X > Span - 200f, "expected to disembark near station B");
        Assert.Equal(Span + 500f, tank.OrderTargetPos.Value.X, 1); // the objective is preserved
        Assert.Equal(UnitState.Moving, tank.State);
    }

    [Fact]
    public void Boarding_station_detour_is_chosen_when_rail_is_clearly_shorter()
    {
        // Task105: return a boarding station when going by rail is a win (AssignAdvance swaps out the road route's destination).
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        var pairs = TrainStep.FindStationPairs(s, 0);
        Assert.Single(pairs);

        // From near station A (300,0) to beyond station B (Span+500): direct march vs boarding at station A is a big win.
        WorldPos station;
        Assert.True(TrainStep.TryFindBoardingStation(pairs, new WorldPos(300, 0, 0),
            new WorldPos(Span + 500f, 0, 0), out station));
        Assert.Equal(0f, station.X, 1); // boarding station = A

        // Not used when the destination is short-range.
        Assert.False(TrainStep.TryFindBoardingStation(pairs, new WorldPos(300, 0, 0),
            new WorldPos(600, 0, 0), out station));

        // No swap needed when already in front of the station (just wait there to board).
        Assert.False(TrainStep.TryFindBoardingStation(pairs, new WorldPos(50, 0, 0),
            new WorldPos(Span + 500f, 0, 0), out station));
    }

    [Fact]
    public void Destroyed_train_takes_cargo_and_passengers_with_it()
    {
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        var train = new UnitInstance(100, LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1), 0, 500f,
            new WorldPos(1500, 1, 0));
        train.SupplyLoad = 1f;
        s.Units.Add(train);
        var tank = new UnitInstance(101, "Tank_T1", 0, 100f, new WorldPos(1500, 1, 0));
        tank.CarriedByUnitId = train.InstanceId;
        s.Units.Add(tank);

        train.CurrentHP = 0f;
        train.State = UnitState.Dead;
        TransportHeliStep.Advance(s, 0.1f); // taking passengers down with it (shared mechanism)

        Assert.False(tank.IsAlive);
    }

    // --- Task107 (user report: "trains spawn but cannot move and become paperweights") ---

    [Fact]
    public void Train_stopped_on_the_rail_beside_an_offset_station_counts_as_arrived()
    {
        // A station may be up to RailSnapRadius (100m) away from the rail = the train cannot reach
        // the station building itself. If the arrival check is smaller than that distance, the train
        // deadlocks by endlessly re-attempting departure just short of the station.
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        a.Position = new WorldPos(0, 0, 90); // station 90m away from the rail (z=0)
        CargoStationRules.RefreshConnectivity(s);
        f.AddSupply(1000f);

        var train = new UnitInstance(100, LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1), 0, 500f,
            new WorldPos(0, 1, 0)); // on the rail, right beside the station
        s.Units.Add(train);

        ServiceAndDepart(s);

        Assert.Equal(1f, train.SupplyLoad, 3);                 // counted as arrived at the station and could load
        Assert.Equal(UnitState.Moving, train.State);           // departed for the opposite station after the dwell time
        Assert.NotNull(train.Path);
    }

    [Fact]
    public void Extra_trains_share_the_available_routes_instead_of_freezing()
    {
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        f.AddSupply(1000f);
        for (uint id = 100; id < 103; id++) // 3 trains for a single line
            s.Units.Add(new UnitInstance(id, LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1), 0, 500f,
                new WorldPos(0, 1, 0)));

        ServiceAndDepart(s);

        foreach (var t in s.Units)
            Assert.Equal(UnitState.Moving, t.State); // must not end up with one train moving and the rest frozen forever
    }

    [Fact]
    public void Station_beside_an_isolated_siding_still_enters_from_the_main_line()
    {
        // The in-game case where "all 4 stations are operational yet there are 0 routes": if the rail
        // node right beside a station belongs to a siding cut off from the main line, stations that
        // snapped to it cannot be routed between. Pick the entry point from the main-line network
        // (the largest connected component).
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);

        // Place a siding (2 nodes) independent of the main line right next to station B.
        var rails = s.Rails;
        rails.AddNode(100, new WorldPos(Span, 1, 40));
        rails.AddNode(101, new WorldPos(Span + 20f, 1, 40));
        rails.AddEdge(100, 101);

        b.Position = new WorldPos(Span, 0, 30); // 10m to the siding, 30m to the main line
        CargoStationRules.RefreshConnectivity(s);

        Assert.True(b.RailConnected);
        Assert.True(b.RailEntry.HasValue);
        Assert.Equal(0f, b.RailEntry.Value.Z, 1); // grabbed the main-line side (z=0), not the siding's z=40
        Assert.Single(TrainStep.FindStationPairs(s, 0));
    }

    [Fact]
    public void Train_off_the_rails_is_put_back_on_them_before_departing()
    {
        // A train sitting away from the tracks (e.g. manually produced at the station building's
        // position) is put back onto the rail before running, instead of "flying through the air"
        // in a straight line to its first waypoint.
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        f.AddSupply(1000f);
        // In the air, outside the station's arrival range (150m) and 200m away from the rail.
        // It is re-snapped onto the tracks rather than flying to them in a straight line.
        var train = new UnitInstance(100, LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1), 0, 500f,
            new WorldPos(600, 40, 200));
        s.Units.Add(train);

        TrainStep.Advance(s, 0.1f);

        Assert.True(train.Position.HorizontalDistanceTo(new WorldPos(600, 1, 0)) <= TrainStep.RailSnapTolerance,
            "expected the train to be snapped onto the rail network before departing");
        Assert.Equal(UnitState.Moving, train.State);
    }

    [Fact]
    public void Stations_prefer_their_shared_line_over_a_bigger_nearby_mainline()
    {
        // The cause of the in-game "trains shuttle along a pre-existing main line heading out of the
        // area": the entry point was picked from the "largest connected component", so the map's
        // north-south trunk line (largest node count) beat the military line right beside the
        // stations. Pick the component the stations share, and break ties by distance from the station.
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);

        // A "huge pre-existing main line" running parallel 200m from the stations (wins by node count by a landslide, within 300m of both stations).
        var rails = s.Rails;
        for (ushort n = 100; n < 160; n++)
            rails.AddNode(n, new WorldPos((n - 100) * (Span / 59f), 1, 200));
        for (ushort n = 100; n < 159; n++)
            rails.AddEdge(n, (ushort)(n + 1));

        CargoStationRules.RefreshConnectivity(s);

        Assert.True(a.RailEntry.HasValue && b.RailEntry.HasValue);
        Assert.Equal(0f, a.RailEntry.Value.Z, 1); // on the military line (z=0), not the main line (z=200)
        Assert.Equal(0f, b.RailEntry.Value.Z, 1);
    }

    [Fact]
    public void Train_beside_a_disconnected_siding_still_departs()
    {
        // The in-game case where a fully loaded train got stuck at a station: with a siding cut off
        // from the main line right next to the station, a naive nearest-neighbor snap grabbed that
        // siding's node, from which the destination is unreachable, so pathfinding failed every time.
        // Pick the origin from the same connected component as the destination.
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        f.AddSupply(1000f);

        // Place a siding not connected to the main line right next to station A (on the main line z=0, x=0).
        s.Rails.AddNode(200, new WorldPos(2, 1, 3));
        s.Rails.AddNode(201, new WorldPos(2, 1, 30));
        s.Rails.AddEdge(200, 201);

        var train = new UnitInstance(100, LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1), 0, 500f,
            new WorldPos(1, 1, 2)); // a position where the siding node (2,3) is closer than the main line (0,0)
        s.Units.Add(train);

        ServiceAndDepart(s);

        Assert.Equal(UnitState.Moving, train.State);
        Assert.NotNull(train.Path);
    }

    [Fact]
    public void Train_without_any_route_runs_to_the_nearest_station_instead_of_freezing()
    {
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        s.Bases.Remove(b); // only one station = no route can be formed
        Assert.Empty(TrainStep.FindStationPairs(s, 0));

        var train = new UnitInstance(100, LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1), 0, 500f,
            new WorldPos(Span, 1, 0)); // a train stranded at the far end of the tracks
        s.Units.Add(train);

        TrainStep.Advance(s, 0.1f);

        Assert.Equal(UnitState.Moving, train.State);
        Assert.NotNull(train.Path); // deadheads along the rails and waits at the station
    }
}
