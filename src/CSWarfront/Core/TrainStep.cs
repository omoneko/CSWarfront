using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// Task101: military freight train operations (design §3).
    ///
    /// For each operational station pair (two friendly CargoStations, rail-connected and at least
    /// MinStationDistance apart) one train is automatically maintained. It shuttles:
    ///   Supplies: loads from the faction pool at the "home station" (the one nearest a friendly army base),
    ///         unloads into the far station's StoredSupplies (front-side stock = the pickup source for
    ///         trucks/helicopters)
    ///   Units: boards land units within BoardRadius of a station that are "headed for the front"
    ///         (whose advance objective is at least BoardDetourAdvantage closer to the opposite station),
    ///         and disembarks them at the opposite station (objective and orders retained; they resume
    ///         marching on their own)
    /// Train-to-pair assignment is stateless (trains in ascending InstanceId order are assigned to pairs in
    /// ascending (BaseId,BaseId) order = deterministically re-derived every tick; nothing saved).
    /// The carrying mechanism (CarriedByUnitId, position sync, shared fate) is common with TransportHeliStep.
    /// </summary>
    public static class TrainStep
    {
        public const int MaxTrainsPerFaction = 6; // Task105: 4→6 (aggressive rail usage)

        /// <summary>Supply amount a full load (SupplyLoad=1) carries.</summary>
        public const float CargoSupply = 200f;

        public const float BoardRadius = 250f;          // Task105: 150→250 (wider station catchment)

        /// <summary>Minimum distance between stations for a pair to qualify. Task107: 1500→400 (in-game the
        /// main cause of "built stations but no train ever moves" was that no pair formed unless two
        /// stations were 1.5km+ apart, leaving trains with no assigned route permanently parked; relaxed so
        /// city-scale lines qualify).</summary>
        public const float MinStationDistance = 400f;

        /// <summary>Boarding condition: the opposite station must be at least this much closer to the unit's
        /// advance objective ("the front is far away" test).
        /// Task105: 1000→300 (aggressive rail usage — board whenever it is even slightly worthwhile).</summary>
        public const float BoardDetourAdvantage = 300f;

        /// <summary>Task110 (user request "trains need time to unload, so pause at stations"):
        /// dwell time at a station (in-game hours). The sequence becomes arrive → cargo work → dwell for
        /// this long → depart. 6 hours ≈ 3 real seconds at 1x speed.</summary>
        public const float StationDwellHours = 6f;

        /// <summary>Task110: re-evaluation interval while waiting with nothing to do (e.g. no supplies to
        /// load at the home station) (in-game hours). Previously the station processing (which includes
        /// whole-unit-list scans for boarding checks and passenger counting) ran every tick, so a few parked
        /// trains alone were a pointless load.</summary>
        public const float IdleRecheckHours = 2f;

        /// <summary>Station arrival radius. Task107: 60→150. A station may sit up to
        /// CargoStationRules.RailSnapRadius (100m) from the rails while the train can only travel on rails
        /// (= it can never come within 100m of the building), so at 60m "arrived" was never satisfied and
        /// the train deadlocked re-issuing DepartTo just short of the station (the main cause of the user
        /// report "trains turn into paperweights"). Kept safely above the snap radius so stopping on the
        /// rail next to the station counts as arrival.</summary>
        public const float StationArriveRadius = 150f;

        public struct StationPair
        {
            public MilitaryBase A;
            public MilitaryBase B;
        }

        /// <summary>Per economy tick: maintains trains up to the number of pairs (capped at
        /// MaxTrainsPerFaction). Spawn at each pair's home station (paid via UnitCosts; Invaders excluded).</summary>
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
                    // Task108: spawn at the rail entry point, not the station building (= on the track from the start).
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

                List<StationPair> pairs = null;   // not built until needed
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
                        // No route exists at all (only one station / rails disconnected / stations captured
                        // etc.): Task107: previously the train parked forever on the spot (the main cause of
                        // the user report "spawns but cannot move — a paperweight"). Instead it travels along
                        // the rails to the nearest friendly operational station and waits there (with no
                        // station at all there is genuinely nowhere to go, so it waits in place).
                        ParkAtNearestStation(state, f, train);
                        continue;
                    }

                    // Task107: with a fixed one-train-per-pair mapping, trains beyond the pair count
                    // (manually produced ones) all parked forever. Round-robin ensures every train gets a
                    // route.
                    // Task110: but prefer "a route reachable from the rail network this train stands on" —
                    // a train assigned a route on a different network can never lay a path anywhere and
                    // parks forever (observed in-game).
                    StationPair pair = ChoosePair(state, pairs, train, trainIndex);
                    trainIndex++;
                    AdvanceTrainCycle(state, f, train, pair, dt);
                }
            }
        }

        /// <summary>Task110: route selection. Starting from the round-robin position, picks the first route
        /// "reachable from the rail network this train currently stands on" (falling back to the plain
        /// round-robin assignment when none is reachable). The test is component-id equality only (cached,
        /// hence cheap).</summary>
        private static StationPair ChoosePair(WarState state, List<StationPair> pairs, UnitInstance train, int trainIndex)
        {
            ushort trainNode;
            int trainComponent;
            if (state.Rails.TryFindNearestNode(train.Position, CargoStationRules.RailEntryRadius, out trainNode)
                && state.Rails.Components.TryGetValue(trainNode, out trainComponent))
            {
                for (int offset = 0; offset < pairs.Count; offset++)
                {
                    StationPair candidate = pairs[(trainIndex + offset) % pairs.Count];
                    ushort node;
                    int comp;
                    if (!state.Rails.TryFindNearestNode(CargoStationRules.RailPointOf(candidate.A),
                            CargoStationRules.RailSnapRadius * 2f, out node)) continue;
                    if (!state.Rails.Components.TryGetValue(node, out comp) || comp != trainComponent) continue;
                    return candidate;
                }
            }
            return pairs[trainIndex % pairs.Count];
        }

        private static void AdvanceTrainCycle(WarState state, Faction f, UnitInstance train, StationPair pair, float dt)
        {
            // Task110: while dwelling (cargo-work time / idle recheck delay) do nothing. Station processing
            // includes whole-unit-list scans, so bailing out here is itself a load reduction.
            if (train.StationDwell > 0f)
            {
                train.StationDwell -= dt;
                return;
            }

            MilitaryBase home = HomeStation(state, pair, f.Id);

            // Task108 (user report "train vibrates in place and gets stuck"): never run station processing
            // while travelling (consuming a path). Previously DepartTo was called every tick while inside
            // the 150m arrival radius, re-laying the path from the current position each time — when the
            // start snap picked the node just behind the train it stepped backwards, then re-pathed next
            // tick, and so on (= vibrating in place, never moving forward).
            if (train.Path != null && train.PathIndex < train.Path.Count) return;

            // Task108: arrival checks and path goals are measured against the "rail entry point", not the
            // station building (the train can only travel on rails and can never reach the building's own
            // coordinates).
            MilitaryBase atStation = null;
            if (train.Position.HorizontalDistanceTo(CargoStationRules.RailPointOf(pair.A)) <= StationArriveRadius)
                atStation = pair.A;
            else if (train.Position.HorizontalDistanceTo(CargoStationRules.RailPointOf(pair.B)) <= StationArriveRadius)
                atStation = pair.B;

            if (atStation == null)
            {
                // Outside both stations without a path (just loaded / just built / pair changed / path
                // consumed short of a station): lay a path to the nearest assigned station.
                MilitaryBase nearest =
                    train.Position.HorizontalDistanceTo(CargoStationRules.RailPointOf(pair.A))
                    <= train.Position.HorizontalDistanceTo(CargoStationRules.RailPointOf(pair.B)) ? pair.A : pair.B;
                DepartTo(state, train, nearest);
                return;
            }

            MilitaryBase other = atStation.BaseId == pair.A.BaseId ? pair.B : pair.A;

            // Task110: on arrival, do the cargo work exactly once, then dwell for its duration (departure is
            // decided at the next evaluation). If already serviced (= the dwell has just finished), skip
            // this block and go on to the departure decision.
            if (!train.StationServiced)
            {
                ServiceAtStation(state, f, train, atStation, home, other);
                train.StationServiced = true;
                train.StationDwell = StationDwellHours;
                train.OrderTargetPos = null;
                train.State = UnitState.Idle; // dwelling
                return;
            }

            // 5) Departure: with cargo/passengers head for the opposite station; empty, return to the home
            // station (or wait if already there).
            train.StationServiced = false;
            bool hasCargo = train.SupplyLoad > 0f || CountPassengers(state, train.InstanceId) > 0;
            if (hasCargo) { DepartTo(state, train, other); return; }
            if (atStation.BaseId != home.BaseId) { DepartTo(state, train, home); return; }

            // Nothing to load at the home station: wait a while before re-evaluating (no per-tick station
            // processing).
            train.OrderTargetPos = null;
            train.State = UnitState.Idle;
            train.StationDwell = IdleRecheckHours;
        }

        /// <summary>Task110: performs the station cargo work (unload → disembark → load → board) in one go.</summary>
        private static void ServiceAtStation(WarState state, Faction f, UnitInstance train,
            MilitaryBase atStation, MilitaryBase home, MilitaryBase other)
        {
            // 1) Unload (only at non-home = front-side stations; only as much as the stock has room for).
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

            // 2) Disembark (all passengers get off at this station — the boarding condition was "this
            // station is closer to their objective").
            DisembarkAll(state, train);

            // 3) Load (supplies are loaded at the home station only).
            if (atStation.BaseId == home.BaseId && f.SupplyStock > 0f && train.SupplyLoad < 1f)
            {
                float loadable = f.SupplyStock / CargoSupply;
                float room = 1f - train.SupplyLoad;
                float load = loadable < room ? loadable : room;
                f.TrySpendSupply(load * CargoSupply);
                train.SupplyLoad += load;
            }

            // 4) Board (allowed at either station: only units whose objective is much closer to the opposite
            // station get on).
            BoardEligibleUnits(state, train, atStation, other);
        }

        /// <summary>Task107: sends a train with no assigned route along the rails to the nearest friendly
        /// operational station to wait there (waits in place when already inside a station's radius or when
        /// no operational station exists at all).</summary>
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
                Wait(train); // Task110: do not re-run every tick; re-evaluate after a pause
                return;
            }

            if (train.Path != null && train.PathIndex < train.Path.Count) return; // already deadheading
            DepartTo(state, train, best);
        }

        /// <summary>Tolerance for counting as "on the rails" (m). Farther off the track than this and the
        /// train is placed back on.</summary>
        public const float RailSnapTolerance = 15f;

        private static void DepartTo(WarState state, UnitInstance train, MilitaryBase station)
        {
            WorldPos dest = CargoStationRules.RailPointOf(station); // Task108: the goal is the rail entry point

            // Task110 (user report "the train looks stuck"): the path's start node must be one that can
            // reach the goal. A plain nearest-node snap can grab a node of a different network (a siding)
            // right next to the station, from which the destination can never be reached — every path search
            // failed and fully loaded trains sat at the station forever (confirmed in the live logs).
            // Resolve the destination node first, then pick the start node from its connected component.
            ushort destNode;
            if (!state.Rails.TryFindNearestNode(dest, CargoStationRules.RailSnapRadius * 2f, out destNode))
            {
                Wait(train);
                return;
            }

            ushort startNode;
            if (!state.Rails.TryFindNearestNodeInSameComponent(train.Position,
                    CargoStationRules.RailEntryRadius, destNode, out startNode))
            {
                Wait(train); // physically cannot enter that rail network (route assignment is reconsidered next tick)
                return;
            }

            // Task109: if the train is away from the start node, place it back on the track (prevents flying
            // in a straight line through the air where there are no rails).
            WorldPos onRail;
            if (state.Rails.TryGetNodePosition(startNode, out onRail)
                && train.Position.HorizontalDistanceTo(onRail) > RailSnapTolerance)
            {
                train.Position = onRail;
            }

            var path = state.Rails.FindPathBetweenNodes(startNode, destNode);
            if (path == null || path.Count == 0)
            {
                // Already on the destination node / no route: do not move (re-evaluate next tick).
                Wait(train);
                return;
            }
            train.Path = path;
            train.PathIndex = 0;
            train.PathTarget = dest;
            train.OrderTargetPos = dest;
            train.State = UnitState.Moving;
            train.StationServiced = false; // Task110: the next station reached always starts with cargo work
        }

        /// <summary>Task110: waiting when departure is impossible. Pauses until the next evaluation (no
        /// per-tick path-search retries). Route assignment is reconsidered every tick, so the train starts
        /// moving as soon as a reachable route appears.</summary>
        private static void Wait(UnitInstance train)
        {
            train.OrderTargetPos = null;
            train.State = UnitState.Idle;
            train.StationDwell = IdleRecheckHours;
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
                    >= dest.HorizontalDistanceTo(here.Position)) continue; // not worth boarding (front is near / opposite direction)

                u.CarriedByUnitId = train.InstanceId;
                u.ClearPath(); // after alighting, the path is re-laid from OrderTargetPos
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
                u.State = UnitState.Moving; // resumes marching with its retained objective (OrderTargetPos)
                n++;
            }
        }

        /// <summary>
        /// Task109 (user report "trains stopped moving"): cached route-list lookup.
        ///
        /// <see cref="FindStationPairs"/> runs one A* per station pair. After curve sampling grew the rail
        /// graph from 309 to 1347 nodes, and six stations meant 15 pairs, calling it every tick — from two
        /// places no less (TrainStep.Advance and InvasionOrders.AssignAdvance) — saturated the sim thread
        /// with pathfinding and froze not just the trains but everything.
        ///
        /// Routes only change when the rail network or the station set changes, so a naive cache that is
        /// discarded via <see cref="InvalidateRoutes"/> on rail rebuild suffices (a miss computes once and
        /// remembers — callers need not care).
        /// </summary>
        public static List<StationPair> RoutesOf(WarState state, byte factionId)
        {
            List<StationPair> cached;
            if (state.RailRoutes.TryGetValue(factionId, out cached) && cached != null
                && IsStillValid(cached, factionId))
                return cached;

            cached = FindStationPairs(state, factionId);
            state.RailRoutes[factionId] = cached;
            return cached;
        }

        /// <summary>Task136 (playtest report "military freight trains stop at enemy stations"): the cache
        /// holds direct references to the station objects, and it was only ever discarded when the *rail
        /// network* was rebuilt. Capturing a cargo station changes its owner without touching a single
        /// rail, so a captured station stayed on the timetable and the train kept shuttling into enemy
        /// hands. Re-checking ownership is a handful of field reads — cheap enough to do on every lookup,
        /// and it needs no cooperation from whatever code changes an owner (capture, demolition, HP
        /// reaching zero), which is what makes it reliable.</summary>
        private static bool IsStillValid(List<StationPair> pairs, byte factionId)
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                if (!IsStillOurs(pairs[i].A, factionId)) return false;
                if (!IsStillOurs(pairs[i].B, factionId)) return false;
            }
            return true;
        }

        private static bool IsStillOurs(MilitaryBase station, byte factionId)
        {
            return CargoStationRules.IsOperational(station)
                && station.OwnerFactionId.Value == factionId;
        }

        /// <summary>Discards the route cache (called by the Game layer when the rail network is rebuilt).</summary>
        public static void InvalidateRoutes(WarState state)
        {
            state.RailRoutes.Clear();
        }

        /// <summary>Enumerates pairs of the faction's operational stations (rail-connected, at least
        /// MinStationDistance apart) in ascending BaseId order. Reachability costs one graph pass; heavy
        /// enough that callers should use <see cref="RoutesOf"/> rather than calling this directly.</summary>
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

            // Task109: reachability is decided by connected components, not A* (equivalent on an undirected
            // graph). Previously an A* ran per pair — six stations = 15 pairs over a 1300+ node graph, called
            // every tick from two places — and the sim thread drowned in pathfinding (the direct cause of
            // "trains stopped moving"). Components take a single pass over the whole graph.
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
                    if (stationComponent[b] != stationComponent[a]) continue; // different rail networks
                    // Task108: both snapped to the same rail node = there is no stretch to run (this used to
                    // qualify as a route, and the assigned train endlessly "departed" with a zero-length
                    // path, never moving).
                    if (stationNode[a] == stationNode[b]) continue;
                    if (stations[a].Position.HorizontalDistanceTo(stations[b].Position) < MinStationDistance) continue;
                    pairs.Add(new StationPair { A = stations[a], B = stations[b] });
                }
            }
            return pairs;
        }

        /// <summary>Task105 (aggressive rail usage): when travelling from from to dest, if some station pair
        /// makes the rail detour (march to the boarding station → train → march from the alighting station)
        /// at least BoardDetourAdvantage cheaper, returns that boarding station's position. Used by the AI
        /// advance (InvasionOrders.AssignAdvance) to redirect the road path's goal to the boarding station,
        /// creating the flow "head for the station first → get picked up within BoardRadius".
        /// Returns false when already right by a boarding station (keep waiting to board).</summary>
        public static bool TryFindBoardingStation(List<StationPair> pairs, WorldPos from, WorldPos dest,
            out WorldPos boardingStation)
        {
            boardingStation = default(WorldPos);
            float direct = from.HorizontalDistanceTo(dest);
            float bestVia = float.MaxValue;
            bool found = false;

            for (int i = 0; i < pairs.Count; i++)
            {
                // Try both directions (board A → alight B, board B → alight A).
                for (int dir = 0; dir < 2; dir++)
                {
                    MilitaryBase board = dir == 0 ? pairs[i].A : pairs[i].B;
                    MilitaryBase alight = dir == 0 ? pairs[i].B : pairs[i].A;
                    float toBoard = from.HorizontalDistanceTo(board.Position);
                    // Already inside the station's catchment: no goal redirection needed (the train will
                    // pick the unit up as-is).
                    // Task107: the test uses BoardRadius rather than StationArriveRadius×2 (the arrival
                    // radius varies for the train's own reasons; what matters here is "within boarding
                    // range").
                    if (toBoard <= BoardRadius) continue;
                    float via = toBoard + alight.Position.HorizontalDistanceTo(dest);
                    if (via + BoardDetourAdvantage >= direct) continue; // rail is not clearly worthwhile
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

        /// <summary>The "home" station of a pair = the one nearer a friendly army base (ties go to A).</summary>
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
