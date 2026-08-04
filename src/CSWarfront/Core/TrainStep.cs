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
        public const int MaxTrainsPerFaction = 4;

        /// <summary>満載（SupplyLoad=1）が運ぶ補給物資量。</summary>
        public const float CargoSupply = 200f;

        public const float BoardRadius = 150f;
        public const float MinStationDistance = 2000f;

        /// <summary>搭乗条件: 反対側の駅が進軍目的地へこの距離以上近いこと（「前線が遠方にある」判定）。</summary>
        public const float BoardDetourAdvantage = 1000f;

        /// <summary>駅への到着判定半径。</summary>
        public const float StationArriveRadius = 60f;

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

                List<StationPair> pairs = FindStationPairs(state, f.Id);
                if (pairs.Count == 0) continue;

                int want = pairs.Count < MaxTrainsPerFaction ? pairs.Count : MaxTrainsPerFaction;
                int have = CountTrains(state, f.Id);
                for (int p = have; p < want; p++)
                {
                    if (!UnitCosts.TryPay(f, trainType, f.Treasury)) break;
                    MilitaryBase home = HomeStation(state, pairs[p], f.Id);
                    var u = new UnitInstance(state.AllocInstanceId(), trainType.TypeKey, f.Id, trainType.MaxHP,
                        new WorldPos(home.Position.X, home.Position.Y, home.Position.Z));
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
                int pairCursor = 0;

                for (int i = 0; i < state.Units.Count; i++)
                {
                    UnitInstance train = state.Units[i];
                    if (!train.IsAlive || train.FactionId != f.Id) continue;
                    UnitType type = state.Types.Get(train.TypeKey);
                    if (type == null || type.Category != UnitCategory.MilitaryTrain) continue;
                    if (train.Order == UnitOrder.Hold || train.Order == UnitOrder.RallyHold) continue;

                    if (pairs == null) pairs = FindStationPairs(state, f.Id);
                    if (pairCursor >= pairs.Count)
                    {
                        // 担当ペアが無い（駅が壊れた/占領された等）: その場で待機。
                        train.OrderTargetPos = null;
                        train.State = UnitState.Idle;
                        continue;
                    }

                    StationPair pair = pairs[pairCursor];
                    pairCursor++;
                    AdvanceTrainCycle(state, f, train, pair);
                }
            }
        }

        private static void AdvanceTrainCycle(WarState state, Faction f, UnitInstance train, StationPair pair)
        {
            MilitaryBase home = HomeStation(state, pair, f.Id);
            MilitaryBase away = home.BaseId == pair.A.BaseId ? pair.B : pair.A;

            MilitaryBase atStation = null;
            if (train.Position.HorizontalDistanceTo(pair.A.Position) <= StationArriveRadius) atStation = pair.A;
            else if (train.Position.HorizontalDistanceTo(pair.B.Position) <= StationArriveRadius) atStation = pair.B;

            if (atStation == null)
            {
                // 走行中: Pathがあれば走り続ける（MovementStep.AdvanceTrain）。無ければ（ロード直後・
                // 新造直後・ペア変更後）最寄りの担当駅へ経路を張り直す。
                if (train.Path != null && train.PathIndex < train.Path.Count) return;
                MilitaryBase nearest = train.Position.HorizontalDistanceTo(pair.A.Position)
                    <= train.Position.HorizontalDistanceTo(pair.B.Position) ? pair.A : pair.B;
                DepartTo(state, train, nearest);
                return;
            }

            MilitaryBase other = atStation.BaseId == pair.A.BaseId ? pair.B : pair.A;

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

            // 5) 出発判定: 積荷/乗客があれば反対駅へ。空なら基地側駅へ戻る（既に基地側なら待機）。
            bool hasCargo = train.SupplyLoad > 0f || CountPassengers(state, train.InstanceId) > 0;
            if (hasCargo) DepartTo(state, train, other);
            else if (atStation.BaseId != home.BaseId) DepartTo(state, train, home);
            else { train.OrderTargetPos = null; train.State = UnitState.Idle; }
        }

        private static void DepartTo(WarState state, UnitInstance train, MilitaryBase station)
        {
            var path = state.Rails.FindPath(train.Position, station.Position, CargoStationRules.RailSnapRadius * 2f);
            if (path == null)
            {
                // レールが分断された等: 経路が張れない間は動かない（次tickで再試行）。
                train.OrderTargetPos = null;
                train.State = UnitState.Idle;
                return;
            }
            train.Path = path;
            train.PathIndex = 0;
            train.PathTarget = station.Position;
            train.OrderTargetPos = station.Position;
            train.State = UnitState.Moving;
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

        /// <summary>自軍の稼働駅からなるペア（レールで接続・MinStationDistance以上）をBaseId昇順で列挙する。
        /// 経路存在チェックはA*1回/ペア（駅数は少ない想定。呼び出しは勢力ごとに1回/tickまで）。</summary>
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
            for (int a = 0; a < stations.Count; a++)
            {
                for (int b = a + 1; b < stations.Count; b++)
                {
                    if (stations[a].Position.HorizontalDistanceTo(stations[b].Position) < MinStationDistance) continue;
                    if (state.Rails == null) continue;
                    var path = state.Rails.FindPath(stations[a].Position, stations[b].Position,
                        CargoStationRules.RailSnapRadius * 2f);
                    if (path == null) continue;
                    pairs.Add(new StationPair { A = stations[a], B = stations[b] });
                }
            }
            return pairs;
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
