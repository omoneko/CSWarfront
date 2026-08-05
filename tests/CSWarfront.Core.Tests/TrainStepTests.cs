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
        b.Position = new WorldPos(200, 0, 0); // MinStationDistance(400)未満
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
    public void Boarding_station_detour_is_chosen_when_rail_is_clearly_shorter()
    {
        // Task105: 鉄道経由が得なら乗車駅を返す（AssignAdvanceが道路経路の行き先を差し替える）。
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        var pairs = TrainStep.FindStationPairs(s, 0);
        Assert.Single(pairs);

        // 駅Aの近く(300,0)から駅Bの先(Span+500)へ: 直行 vs 駅A乗車で大幅に得。
        WorldPos station;
        Assert.True(TrainStep.TryFindBoardingStation(pairs, new WorldPos(300, 0, 0),
            new WorldPos(Span + 500f, 0, 0), out station));
        Assert.Equal(0f, station.X, 1); // 乗車駅=A

        // 目的地が近距離なら使わない。
        Assert.False(TrainStep.TryFindBoardingStation(pairs, new WorldPos(300, 0, 0),
            new WorldPos(600, 0, 0), out station));

        // 既に駅前にいる場合は差し替え不要（そのまま搭乗待ち）。
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
        TransportHeliStep.Advance(s, 0.1f); // 道連れ処理（共通機構）

        Assert.False(tank.IsAlive);
    }

    // --- Task107（ユーザー報告「列車がスポーンしても身動きできず文鎮化する」）---

    [Fact]
    public void Train_stopped_on_the_rail_beside_an_offset_station_counts_as_arrived()
    {
        // 駅はレールから最大RailSnapRadius(100m)離れてよい＝列車は駅建物まで届かない。
        // 到着判定がその距離より小さいと、駅の手前で出発をやり直し続けるデッドロックになる。
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        a.Position = new WorldPos(0, 0, 90); // レール(z=0)から90m離れた駅
        CargoStationRules.RefreshConnectivity(s);
        f.AddSupply(1000f);

        var train = new UnitInstance(100, LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1), 0, 500f,
            new WorldPos(0, 1, 0)); // レール上、駅の真横
        s.Units.Add(train);

        TrainStep.Advance(s, 0.1f);

        Assert.Equal(1f, train.SupplyLoad, 3);                 // 駅に着いた扱いで積載できた
        Assert.Equal(UnitState.Moving, train.State);           // 反対の駅へ出発した
        Assert.NotNull(train.Path);
    }

    [Fact]
    public void Extra_trains_share_the_available_routes_instead_of_freezing()
    {
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        f.AddSupply(1000f);
        for (uint id = 100; id < 103; id++) // 路線1本に対して列車3編成
            s.Units.Add(new UnitInstance(id, LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1), 0, 500f,
                new WorldPos(0, 1, 0)));

        TrainStep.Advance(s, 0.1f);

        foreach (var t in s.Units)
            Assert.Equal(UnitState.Moving, t.State); // 1編成だけ動いて残りが永久停止、にならない
    }

    [Fact]
    public void Station_beside_an_isolated_siding_still_enters_from_the_main_line()
    {
        // 実機で「駅は4つとも稼働なのに路線が0本」だったケース: 駅の真横のレールノードが本線から
        // 分断された引き込み線だと、そこへスナップした駅どうしは経路が引けない。進入点は
        // 本線網（最大の連結成分）から選ぶ。
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);

        // 本線から独立した引き込み線（2ノード）を駅Bのすぐ横に置く。
        var rails = s.Rails;
        rails.AddNode(100, new WorldPos(Span, 1, 40));
        rails.AddNode(101, new WorldPos(Span + 20f, 1, 40));
        rails.AddEdge(100, 101);

        b.Position = new WorldPos(Span, 0, 30); // 引き込み線まで10m、本線まで30m
        CargoStationRules.RefreshConnectivity(s);

        Assert.True(b.RailConnected);
        Assert.True(b.RailEntry.HasValue);
        Assert.Equal(0f, b.RailEntry.Value.Z, 1); // 本線(z=0)側を掴んでいる（引き込み線のz=40ではない）
        Assert.Single(TrainStep.FindStationPairs(s, 0));
    }

    [Fact]
    public void Train_off_the_rails_is_put_back_on_them_before_departing()
    {
        // 線路から離れた場所に居る列車（駅建物の位置に手動生産された等）は、最初のウェイポイントまで
        // 直線で「宙を飛ぶ」のではなく、レール上へ載せ直してから走り出す。
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        f.AddSupply(1000f);
        var train = new UnitInstance(100, LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1), 0, 500f,
            new WorldPos(0, 40, 120)); // レール(z=0,y=1)から120m離れた上空
        s.Units.Add(train);

        TrainStep.Advance(s, 0.1f);

        Assert.True(train.Position.HorizontalDistanceTo(new WorldPos(0, 1, 0)) <= TrainStep.RailSnapTolerance,
            "expected the train to be snapped onto the rail network before departing");
        Assert.Equal(UnitState.Moving, train.State);
    }

    [Fact]
    public void Train_without_any_route_runs_to_the_nearest_station_instead_of_freezing()
    {
        var s = RailState(out Faction f, out MilitaryBase a, out MilitaryBase b);
        s.Bases.Remove(b); // 駅が1つだけ＝路線が成立しない
        Assert.Empty(TrainStep.FindStationPairs(s, 0));

        var train = new UnitInstance(100, LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1), 0, 500f,
            new WorldPos(Span, 1, 0)); // 線路の反対端に取り残された列車
        s.Units.Add(train);

        TrainStep.Advance(s, 0.1f);

        Assert.Equal(UnitState.Moving, train.State);
        Assert.NotNull(train.Path); // レール上を回送して駅で待機する
    }
}
