using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// Task101: 軍用貨物列車の運行（設計§3）。
    ///
    /// 稼働駅ペア（自軍のCargoStation同士、レールで接続・MinStationDistance以上離れている）ごとに
    /// 列車1編成を自動維持し、
    ///   物資: 「基地側駅」（自軍陸軍基地に最も近い方）で勢力プールから積載 → 反対側の駅の
    ///         StoredSuppliesへ荷下ろし（前線側の備蓄＝トラック/ヘリの積出元になる）
    ///   ユニット: 駅BoardRadius内の「前線へ向かう」陸上ユニット（進軍目的地が反対側の駅の方が
    ///         BoardDetourAdvantage以上近いもの）を搭乗させ、反対側の駅で降車（目的地・命令は
    ///         保持したまま自走再開）
    /// を往復する。列車→ペアの割り当てはステートレス（InstanceId昇順の列車を、(BaseId,BaseId)
    /// 昇順のペアへ順に割り当てる＝毎tick決定的に再導出。セーブに割り当てを持たない）。
    /// 搭乗の仕組み（CarriedByUnitId、位置追従・道連れ）はTransportHeliStepと共通。
    /// </summary>
    public static class TrainStep
    {
        public const int MaxTrainsPerFaction = 6; // Task105: 4→6（鉄道の積極利用）

        /// <summary>満載（SupplyLoad=1）が運ぶ補給物資量。</summary>
        public const float CargoSupply = 200f;

        public const float BoardRadius = 250f;          // Task105: 150→250（駅の集客範囲を拡大）

        /// <summary>駅ペアとして成立する最小の駅間距離。Task107: 1500→400（実機で「駅を建てたのに
        /// 列車が一切動かない」の主因が『2駅が1.5km以上離れていないとペアが1つも成立せず、
        /// 担当路線の無い列車がその場で永久停止する』だったため、市内規模の路線でも成立するよう緩めた）。</summary>
        public const float MinStationDistance = 400f;

        /// <summary>搭乗条件: 反対側の駅が進軍目的地へこの距離以上近いこと（「前線が遠方にある」判定）。
        /// Task105: 1000→300（鉄道の積極利用。少しでも得なら乗る）。</summary>
        public const float BoardDetourAdvantage = 300f;

        /// <summary>Task110（ユーザー要望「荷下ろしのための時間が必要なので駅に着いたら一時停車」）:
        /// 駅での停車時間（ゲーム内時間）。到着→荷役→この時間だけ停車→発車、という流れになる。
        /// 6時間＝1倍速で実時間およそ3秒。</summary>
        public const float StationDwellHours = 6f;

        /// <summary>Task110: 基地側駅で積む物資が無い等、やることが無くて待機するときの再評価間隔
        /// （ゲーム内時間）。従来は毎tick駅処理（全ユニット走査を含む搭乗判定・乗客数え上げ）を
        /// 走らせ続けていたため、停車中の列車が数両いるだけで無駄な負荷になっていた。</summary>
        public const float IdleRecheckHours = 2f;

        /// <summary>駅への到着判定半径。Task107: 60→150。駅はレールから最大CargoStationRules.
        /// RailSnapRadius(100m)離れていてよいのに対し、列車はレール上しか走れない（＝駅建物まで
        /// 最大100m届かない）ため、60mでは永久に「到着」と判定されず、駅の手前でDepartToを
        /// 繰り返すだけのデッドロックになっていた（ユーザー報告「列車が文鎮化する」の主因）。
        /// スナップ半径より確実に大きい値にして、レール上に停まった時点で到着とみなす。</summary>
        public const float StationArriveRadius = 150f;

        public struct StationPair
        {
            public MilitaryBase A;
            public MilitaryBase B;
        }

        /// <summary>経済tickごと: ペア数（上限MaxTrainsPerFaction）まで列車を自動維持する。
        /// スポーンは各ペアの基地側駅（UnitCosts支払い。Invader除外）。</summary>
        public static void MaintainTrains(WarState state)
        {
            UnitType trainType = state.Types.Get(LandUnitRoster.TypeKey(UnitCategory.MilitaryTrain, 1));
            if (trainType == null || state.Rails == null) return;

            for (int fi = 0; fi < state.Factions.Count; fi++)
            {
                Faction f = state.Factions[fi];
                if (f.Eliminated || f.Id == Faction.InvaderFactionId) continue;

                List<StationPair> pairs = RoutesOf(state, f.Id);
                if (pairs.Count == 0) continue;

                int want = pairs.Count < MaxTrainsPerFaction ? pairs.Count : MaxTrainsPerFaction;
                int have = CountTrains(state, f.Id);
                for (int p = have; p < want; p++)
                {
                    if (!UnitCosts.TryPay(f, trainType, f.Treasury)) break;
                    MilitaryBase home = HomeStation(state, pairs[p], f.Id);
                    // Task108: 駅建物ではなくレール進入点に出現させる（＝最初から線路の上に居る）。
                    WorldPos spawn = CargoStationRules.RailPointOf(home);
                    var u = new UnitInstance(state.AllocInstanceId(), trainType.TypeKey, f.Id, trainType.MaxHP,
                        new WorldPos(spawn.X, spawn.Y, spawn.Z));
                    u.State = UnitState.Idle;
                    state.Units.Add(u);
                }
            }
        }

        public static void Advance(WarState state, float dt)
        {
            if (state.Rails == null) return;

            for (int fi = 0; fi < state.Factions.Count; fi++)
            {
                Faction f = state.Factions[fi];
                if (f.Id == Faction.InvaderFactionId) continue;

                List<StationPair> pairs = null;   // 必要になるまで作らない
                int trainIndex = 0;

                for (int i = 0; i < state.Units.Count; i++)
                {
                    UnitInstance train = state.Units[i];
                    if (!train.IsAlive || train.FactionId != f.Id) continue;
                    UnitType type = state.Types.Get(train.TypeKey);
                    if (type == null || type.Category != UnitCategory.MilitaryTrain) continue;
                    if (train.Order == UnitOrder.Hold || train.Order == UnitOrder.RallyHold) continue;

                    if (pairs == null) pairs = RoutesOf(state, f.Id);
                    if (pairs.Count == 0)
                    {
                        // 路線が1本も成立していない（駅が1つだけ／レール未接続／占領された等）:
                        // Task107: 従来はその場で永久停止していた（ユーザー報告「スポーンしても
                        // 身動きできず文鎮化」の主因）。最寄りの自軍稼働駅までレール上を移動して
                        // そこで待機する（駅が皆無なら本当に行き場が無いのでその場で待機）。
                        ParkAtNearestStation(state, f, train);
                        continue;
                    }

                    // Task107: 列車1編成＝1ペア固定だと、ペア数より多い列車（手動生産ぶん）が全て
                    // 永久停止していた。ラウンドロビンで必ずどれかの路線を担当させる。
                    StationPair pair = pairs[trainIndex % pairs.Count];
                    trainIndex++;
                    AdvanceTrainCycle(state, f, train, pair, dt);
                }
            }
        }

        private static void AdvanceTrainCycle(WarState state, Faction f, UnitInstance train, StationPair pair, float dt)
        {
            // Task110: 停車中（荷役の所要時間・待機の再評価待ち）は何もしない。駅処理は全ユニット走査を
            // 含むので、ここで抜けること自体が負荷削減にもなっている。
            if (train.StationDwell > 0f)
            {
                train.StationDwell -= dt;
                return;
            }

            MilitaryBase home = HomeStation(state, pair, f.Id);

            // Task108（ユーザー報告「列車が振動しながらスタックする」）: 走行中（経路を消化中）は
            // 駅処理を一切走らせない。従来は到着判定半径(150m)の内側にいる限り毎tick DepartToが
            // 呼ばれ、そのたびに現在位置から経路を引き直していた——引き直しの起点スナップが
            // 進行方向の1つ手前のノードになると後戻りし、次のtickでまた引き直す、という往復
            // （＝その場で振動して前へ進めない）に陥っていた。
            if (train.Path != null && train.PathIndex < train.Path.Count) return;

            // Task108: 到着判定・経路の行き先は駅建物ではなく「レール進入点」で測る（列車はレール上
            // しか走れないため、駅建物の座標そのものには到達しえない）。
            MilitaryBase atStation = null;
            if (train.Position.HorizontalDistanceTo(CargoStationRules.RailPointOf(pair.A)) <= StationArriveRadius)
                atStation = pair.A;
            else if (train.Position.HorizontalDistanceTo(CargoStationRules.RailPointOf(pair.B)) <= StationArriveRadius)
                atStation = pair.B;

            if (atStation == null)
            {
                // 経路を持たずに駅の外にいる（ロード直後・新造直後・ペア変更後・経路を走り切ったが
                // 駅の圏内ではない）: 最寄りの担当駅へ経路を張り直す。
                MilitaryBase nearest =
                    train.Position.HorizontalDistanceTo(CargoStationRules.RailPointOf(pair.A))
                    <= train.Position.HorizontalDistanceTo(CargoStationRules.RailPointOf(pair.B)) ? pair.A : pair.B;
                DepartTo(state, train, nearest);
                return;
            }

            MilitaryBase other = atStation.BaseId == pair.A.BaseId ? pair.B : pair.A;

            // Task110: 到着したらまず荷役を1回だけ行い、そのぶんの時間だけ停車する（発車は次の評価）。
            // 荷役済み（＝停車時間を終えて戻ってきた）ならこのブロックを飛ばして発車判定へ進む。
            if (!train.StationServiced)
            {
                ServiceAtStation(state, f, train, atStation, home, other);
                train.StationServiced = true;
                train.StationDwell = StationDwellHours;
                train.OrderTargetPos = null;
                train.State = UnitState.Idle; // 停車中
                return;
            }

            // 5) 出発判定: 積荷/乗客があれば反対駅へ。空なら基地側駅へ戻る（既に基地側なら待機）。
            train.StationServiced = false;
            bool hasCargo = train.SupplyLoad > 0f || CountPassengers(state, train.InstanceId) > 0;
            if (hasCargo) { DepartTo(state, train, other); return; }
            if (atStation.BaseId != home.BaseId) { DepartTo(state, train, home); return; }

            // 基地側駅で積むものが無い: しばらく待ってから再評価する（毎tick駅処理を回さない）。
            train.OrderTargetPos = null;
            train.State = UnitState.Idle;
            train.StationDwell = IdleRecheckHours;
        }

        /// <summary>Task110: 駅での荷役（荷下ろし→降車→積載→搭乗）をまとめて1回行う。</summary>
        private static void ServiceAtStation(WarState state, Faction f, UnitInstance train,
            MilitaryBase atStation, MilitaryBase home, MilitaryBase other)
        {
            // 1) 荷下ろし（基地側駅以外＝前線側の駅でのみ。備蓄の空きぶんだけ）。
            if (train.SupplyLoad > 0f && atStation.BaseId != home.BaseId)
            {
                float cap = FortificationRules.StoredSupplyCap(atStation.Type);
                float room = cap - atStation.StoredSupplies;
                float carried = train.SupplyLoad * CargoSupply;
                float transfer = carried < room ? carried : room;
                atStation.StoredSupplies += transfer;
                train.SupplyLoad -= transfer / CargoSupply;
                if (train.SupplyLoad < 0.001f) train.SupplyLoad = 0f;
            }

            // 2) 降車（搭乗兵は全員この駅で降りる。搭乗条件が「この駅の方が目的地に近い」だったため）。
            DisembarkAll(state, train);

            // 3) 積載（基地側駅でのみ物資を積む）。
            if (atStation.BaseId == home.BaseId && f.SupplyStock > 0f && train.SupplyLoad < 1f)
            {
                float loadable = f.SupplyStock / CargoSupply;
                float room = 1f - train.SupplyLoad;
                float load = loadable < room ? loadable : room;
                f.TrySpendSupply(load * CargoSupply);
                train.SupplyLoad += load;
            }

            // 4) 搭乗（どちらの駅でも可: 反対側の駅の方が目的地へ大きく近いユニットだけが乗る）。
            BoardEligibleUnits(state, train, atStation, other);
        }

        /// <summary>Task107: 担当路線が無い列車を、最寄りの自軍稼働駅までレール上で移動させて待機させる
        /// （駅の圏内に既に居る／稼働駅が1つも無い場合はその場で待機）。</summary>
        private static void ParkAtNearestStation(WarState state, Faction f, UnitInstance train)
        {
            MilitaryBase best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.OwnerFactionId == null || b.OwnerFactionId.Value != f.Id) continue;
                if (!CargoStationRules.IsOperational(b)) continue;
                float d = train.Position.HorizontalDistanceTo(CargoStationRules.RailPointOf(b));
                if (d < bestDist) { bestDist = d; best = b; }
            }

            if (best == null || bestDist <= StationArriveRadius)
            {
                train.OrderTargetPos = null;
                train.State = UnitState.Idle;
                return;
            }

            if (train.Path != null && train.PathIndex < train.Path.Count) return; // 既に回送中
            DepartTo(state, train, best);
        }

        /// <summary>レール上に居るとみなす許容距離（m）。これを超えて線路から離れていたら載せ直す。</summary>
        public const float RailSnapTolerance = 15f;

        private static void DepartTo(WarState state, UnitInstance train, MilitaryBase station)
        {
            // Task109（ユーザー報告「列車がレールの無いところを走る／宙を飛ぶ」）: 経路は必ずレール上の
            // ノードから始まる。列車がそこから離れた位置に居ると（駅建物の位置に手動生産された直後、
            // 担当路線の変更後など）、最初のウェイポイントまで直線＝線路の無い空中を進んでしまう。
            // 出発前に最寄りのレールノードへ載せ直して、必ず線路の上から走り出すようにする。
            ushort nodeId;
            WorldPos onRail;
            if (state.Rails.TryFindNearestNode(train.Position, CargoStationRules.RailEntryRadius, out nodeId)
                && state.Rails.TryGetNodePosition(nodeId, out onRail)
                && train.Position.HorizontalDistanceTo(onRail) > RailSnapTolerance)
            {
                train.Position = onRail;
            }

            WorldPos dest = CargoStationRules.RailPointOf(station); // Task108: 行き先はレール進入点
            var path = state.Rails.FindPath(train.Position, dest, CargoStationRules.RailSnapRadius * 2f);
            if (path == null || path.Count == 0)
            {
                // レールが分断された／既に同じノード上にいる: 経路が張れない間は動かない（次tickで再試行）。
                train.OrderTargetPos = null;
                train.State = UnitState.Idle;
                return;
            }
            train.Path = path;
            train.PathIndex = 0;
            train.PathTarget = dest;
            train.OrderTargetPos = dest;
            train.State = UnitState.Moving;
            train.StationServiced = false; // Task110: 次に着いた駅では必ず荷役から始める
        }

        private static void BoardEligibleUnits(WarState state, UnitInstance train, MilitaryBase here, MilitaryBase other)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.IsCarried || u.InstanceId == train.InstanceId) continue;
                if (u.FactionId != train.FactionId) continue;
                if (u.State != UnitState.Moving || !u.OrderTargetPos.HasValue) continue;
                if (u.Order != UnitOrder.AiControlled && u.Order != UnitOrder.FreeAdvance) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null || t.Domain != Domain.Land) continue;
                if (t.Category == UnitCategory.MilitaryTrain) continue;
                if (u.Position.HorizontalDistanceTo(here.Position) > BoardRadius) continue;

                WorldPos dest = u.OrderTargetPos.Value;
                if (dest.HorizontalDistanceTo(other.Position) + BoardDetourAdvantage
                    >= dest.HorizontalDistanceTo(here.Position)) continue; // 乗る価値なし（前線が近い/逆方向）

                u.CarriedByUnitId = train.InstanceId;
                u.ClearPath(); // 降車後はOrderTargetPosから経路を引き直す
            }
        }

        private static void DisembarkAll(WarState state, UnitInstance train)
        {
            int n = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.CarriedByUnitId.HasValue || u.CarriedByUnitId.Value != train.InstanceId) continue;
                float ox = (n % 2 == 0 ? 1f : -1f) * 15f * ((n / 2) + 1);
                u.CarriedByUnitId = null;
                u.Position = new WorldPos(train.Position.X + ox, train.Position.Y, train.Position.Z + 15f);
                u.State = UnitState.Moving; // 目的地(OrderTargetPos)は保持したまま自走再開
                n++;
            }
        }

        /// <summary>
        /// Task109（ユーザー報告「列車が移動しなくなった」）: 路線一覧のキャッシュ付き取得。
        ///
        /// <see cref="FindStationPairs"/>は駅ペアごとにA*を1回走らせる。曲線サンプリングでレール網の
        /// ノード数が309→1347へ増え、かつ駅6つ＝15ペアになった結果、これを毎tick・しかも2箇所
        /// （TrainStep.AdvanceとInvasionOrders.AssignAdvance）から呼んでいた従来のままでは
        /// simスレッドが経路探索で埋まり、列車どころか全体の進行が止まっていた。
        ///
        /// 路線はレール網か駅の増減でしか変わらないので、レール網の再構築時に
        /// <see cref="InvalidateRoutes"/>で捨てるだけの素朴なキャッシュで足りる（未キャッシュなら
        /// その場で1回だけ計算して覚える＝呼び出し側は何も気にしなくてよい）。
        /// </summary>
        public static List<StationPair> RoutesOf(WarState state, byte factionId)
        {
            List<StationPair> cached;
            if (state.RailRoutes.TryGetValue(factionId, out cached) && cached != null) return cached;

            cached = FindStationPairs(state, factionId);
            state.RailRoutes[factionId] = cached;
            return cached;
        }

        /// <summary>路線キャッシュを捨てる（レール網の再構築時にGame層が呼ぶ）。</summary>
        public static void InvalidateRoutes(WarState state)
        {
            state.RailRoutes.Clear();
        }

        /// <summary>自軍の稼働駅からなるペア（レールで接続・MinStationDistance以上）をBaseId昇順で列挙する。
        /// 経路存在チェックはA*1回/ペア。重いので直接呼ばず<see cref="RoutesOf"/>を使うこと。</summary>
        public static List<StationPair> FindStationPairs(WarState state, byte factionId)
        {
            var stations = new List<MilitaryBase>();
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.OwnerFactionId == null || b.OwnerFactionId.Value != factionId) continue;
                if (CargoStationRules.IsOperational(b)) stations.Add(b);
            }
            stations.Sort((x, y) => x.BaseId.CompareTo(y.BaseId));

            var pairs = new List<StationPair>();
            if (state.Rails == null || stations.Count < 2) return pairs;

            // Task109: 到達可能かどうかはA*ではなく連結成分で判定する（無向グラフなので同値）。
            // 従来はペアごとにA*を走らせており、駅6つ＝15ペア×ノード1300超の探索を毎tick2箇所から
            // 呼んでいたためsimスレッドが経路探索で埋まっていた（列車が動かなくなった直接の原因）。
            // 連結成分はグラフ全体を1回なめるだけで求まる。
            var components = state.Rails.ComputeComponentIds();
            var stationComponent = new int[stations.Count];
            var stationNode = new ushort[stations.Count];
            for (int i = 0; i < stations.Count; i++)
            {
                ushort nodeId;
                int comp;
                stationComponent[i] =
                    state.Rails.TryFindNearestNode(CargoStationRules.RailPointOf(stations[i]),
                        CargoStationRules.RailSnapRadius * 2f, out nodeId)
                    && components.TryGetValue(nodeId, out comp) ? comp : -1;
                stationNode[i] = nodeId;
            }

            for (int a = 0; a < stations.Count; a++)
            {
                if (stationComponent[a] < 0) continue;
                for (int b = a + 1; b < stations.Count; b++)
                {
                    if (stationComponent[b] != stationComponent[a]) continue; // 別々の線路網
                    // Task108: 同じレールノードにスナップした＝走る区間が無い（従来はこれが路線として
                    // 成立してしまい、担当列車が「経路ゼロで出発」を繰り返して一切動かなかった）。
                    if (stationNode[a] == stationNode[b]) continue;
                    if (stations[a].Position.HorizontalDistanceTo(stations[b].Position) < MinStationDistance) continue;
                    pairs.Add(new StationPair { A = stations[a], B = stations[b] });
                }
            }
            return pairs;
        }

        /// <summary>Task105（鉄道の積極利用）: fromからdestへ向かうとき、鉄道経由（乗車駅まで自走→
        /// 列車→降車駅から自走）の方がBoardDetourAdvantage以上得になる駅ペアがあれば、その乗車駅の
        /// 位置を返す。AI進軍（InvasionOrders.AssignAdvance）が道路経路の目的地を乗車駅へ差し替えて
        /// 「まず駅へ向かう→BoardRadius内で列車に拾われる」流れを作るために使う。
        /// 既に乗車駅のすぐ側（StationArriveRadius×2以内）にいる場合はfalse（そのまま搭乗を待つ）。</summary>
        public static bool TryFindBoardingStation(List<StationPair> pairs, WorldPos from, WorldPos dest,
            out WorldPos boardingStation)
        {
            boardingStation = default(WorldPos);
            float direct = from.HorizontalDistanceTo(dest);
            float bestVia = float.MaxValue;
            bool found = false;

            for (int i = 0; i < pairs.Count; i++)
            {
                // 両方向（A乗車→B降車、B乗車→A降車）を試す。
                for (int dir = 0; dir < 2; dir++)
                {
                    MilitaryBase board = dir == 0 ? pairs[i].A : pairs[i].B;
                    MilitaryBase alight = dir == 0 ? pairs[i].B : pairs[i].A;
                    float toBoard = from.HorizontalDistanceTo(board.Position);
                    // 既に駅の集客範囲内なら目的地の差し替えは不要（そのまま列車に拾われる）。
                    // Task107: 判定をStationArriveRadius×2ではなくBoardRadiusにした（到着判定半径は
                    // 列車側の都合で変わるが、ここで意味を持つのは「乗車できる範囲かどうか」のため）。
                    if (toBoard <= BoardRadius) continue;
                    float via = toBoard + alight.Position.HorizontalDistanceTo(dest);
                    if (via + BoardDetourAdvantage >= direct) continue; // 鉄道経由が十分に得ではない
                    if (via < bestVia)
                    {
                        bestVia = via;
                        boardingStation = board.Position;
                        found = true;
                    }
                }
            }
            return found;
        }

        /// <summary>ペアのうち「基地側」の駅＝自軍の陸軍基地への最短距離が小さい方（同点はA）。</summary>
        private static MilitaryBase HomeStation(WarState state, StationPair pair, byte factionId)
        {
            float aDist = NearestArmyBaseDistance(state, pair.A.Position, factionId);
            float bDist = NearestArmyBaseDistance(state, pair.B.Position, factionId);
            return bDist < aDist ? pair.B : pair.A;
        }

        private static float NearestArmyBaseDistance(WarState state, WorldPos pos, byte factionId)
        {
            float best = float.MaxValue;
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.OwnerFactionId == null || b.OwnerFactionId.Value != factionId) continue;
                if (b.Type != BaseType.Army) continue;
                float d = pos.HorizontalDistanceTo(b.Position);
                if (d < best) best = d;
            }
            return best;
        }

        private static int CountPassengers(WarState state, uint trainId)
        {
            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
                if (state.Units[i].CarriedByUnitId.HasValue && state.Units[i].CarriedByUnitId.Value == trainId) count++;
            return count;
        }

        private static int CountTrains(WarState state, byte factionId)
        {
            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t != null && t.Category == UnitCategory.MilitaryTrain) count++;
            }
            return count;
        }
    }
}
