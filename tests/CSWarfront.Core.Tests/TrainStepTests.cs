using CSWarfront.Core;
using Xunit;

/// <summary>Task101: 軍用貨物列車（駅ペア列挙・維持・積載/搭乗・輸送/降車・撃破全損）。</summary>
public class TrainStepTests
{
    private const float Span = 3000f; // 駅間距離（MinStationDistance=2000より大きい）

    /// <summary>駅2つ（(0,0)と(Span,0)）を直線レールで結んだ状態。陸軍基地は(0,0)側。</summary>
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
        Assert.Single(s.Units); // ペア1組=列車1編成
        TrainStep.MaintainTrains(s);
        Assert.Single(s.Units); // 増えない
    }

    [Fact]
    public void Disconnected_or_close_stations_form_no_pair()
    {
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        b.Position = new WorldPos(500, 0, 0); // 2km未満
        Assert.Empty(TrainStep.FindStationPairs(s, 0));

        b.Position = new WorldPos(Span, 0, 0);
        s.Rails = new RoadGraph(); // レール消失
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
            new WorldPos(0, 1, 0)); // 基地側駅Aに停車中
        s.Units.Add(train);

        TrainStep.Advance(s, 0.1f); // 積載+出発
        Assert.Equal(1f, train.SupplyLoad, 3);
        Assert.Equal(1000f - TrainStep.CargoSupply, f.SupplyStock, 3);
        Assert.Equal(UnitState.Moving, train.State);

        // 走行（MovementStepのレール移動）→ 到着まで回す。
        for (int i = 0; i < 200 && train.SupplyLoad > 0f; i++)
        {
            MovementStep.Advance(s, 1f);
            TrainStep.Advance(s, 1f);
        }

        Assert.Equal(0f, train.SupplyLoad, 3);
        Assert.Equal(TrainStep.CargoSupply, b.StoredSupplies, 3); // 前線側駅の備蓄へ
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
        tank.OrderTargetPos = new WorldPos(Span + 500f, 0, 0); // 前線=駅Bのさらに先（Bの方が1km以上近い）
        s.Units.Add(tank);

        var nearTank = new UnitInstance(102, "Tank_T1", 0, 100f, new WorldPos(60, 0, 0));
        nearTank.State = UnitState.Moving;
        nearTank.OrderTargetPos = new WorldPos(300, 0, 0); // 近距離目標=乗らない
        s.Units.Add(nearTank);

        TrainStep.Advance(s, 0.1f);

        Assert.Equal(train.InstanceId, tank.CarriedByUnitId);
        Assert.Null(nearTank.CarriedByUnitId);

        // 輸送→到着→降車→自走再開。
        for (int i = 0; i < 200 && tank.IsCarried; i++)
        {
            MovementStep.Advance(s, 1f);
            TransportHeliStep.Advance(s, 1f); // 搭乗中の位置追従（共通機構）
            TrainStep.Advance(s, 1f);
        }

        Assert.False(tank.IsCarried);
        Assert.True(tank.Position.X > Span - 200f, "expected to disembark near station B");
        Assert.Equal(Span + 500f, tank.OrderTargetPos.Value.X, 1); // 目的地は保持
        Assert.Equal(UnitState.Moving, tank.State);
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
        TransportHeliStep.Advance(s, 0.1f); // 道連れ処理（共通機構）

        Assert.False(tank.IsAlive);
    }
}
