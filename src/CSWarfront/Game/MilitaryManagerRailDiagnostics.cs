using System.Collections.Generic;
using System.Text;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task107: rail transport instrumentation (MilitaryManager partial, split off because of the
    /// 500-line limit).
    ///
    /// The user-reported issue "military cargo trains spawn but cannot move and just sit there like a
    /// paperweight" depends on conditions that can only be observed on a real machine (whether the
    /// station reaches the rail network / whether a route can be formed / whether the train has a route
    /// assigned). Every time the rail network is rebuilt, log per faction:
    ///   total cargo stations / how many are operational (owned + rail-connected + HP remaining) /
    ///   number of formed route pairs / train count
    /// When trains do not move, this single line shows at which stage the chain is broken.
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>Sim thread (OnSimTick, inside _stateLock).</summary>
        private static void LogRailRoutes()
        {
            try
            {
                if (State == null) return;

                for (int fi = 0; fi < State.Factions.Count; fi++)
                {
                    Faction f = State.Factions[fi];
                    int stations = 0, operational = 0, trains = 0;

                    for (int i = 0; i < State.Bases.Count; i++)
                    {
                        MilitaryBase b = State.Bases[i];
                        if (b.Type != BaseType.CargoStation) continue;
                        if (b.OwnerFactionId == null || b.OwnerFactionId.Value != f.Id) continue;
                        stations++;
                        if (CargoStationRules.IsOperational(b)) operational++;
                    }

                    for (int i = 0; i < State.Units.Count; i++)
                    {
                        UnitInstance u = State.Units[i];
                        if (!u.IsAlive || u.FactionId != f.Id) continue;
                        UnitType t = State.Types.Get(u.TypeKey);
                        if (t != null && t.Category == UnitCategory.MilitaryTrain) trains++;
                    }

                    if (stations == 0 && trains == 0) continue; // factions with no rail assets stay silent

                    List<TrainStep.StationPair> pairs = TrainStep.RoutesOf(State, f.Id);
                    StringBuilder sb = new StringBuilder();
                    sb.Append("RailRoutes: f").Append(f.Id)
                      .Append(" stations=").Append(stations)
                      .Append(" operational=").Append(operational)
                      .Append(" routes=").Append(pairs.Count)
                      .Append(" trains=").Append(trains)
                      .Append(" supplyStock=").Append(f.SupplyStock.ToString("0"));
                    ModConfig.Log(sb.ToString());

                    // Only when no route forms at all, log the per-station/per-pair breakdown (for
                    // isolating the cause).
                    if (pairs.Count == 0 && operational >= 2) LogRouteFailureDetail(f.Id);

                    // Task109: log per-train state, for isolating why trains do not move.
                    if (trains > 0) LogTrainStates(f.Id, pairs);
                }
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("LogRailRoutes error: " + e);
            }
        }

        /// <summary>Task109: per-train state (assigned route, position, distance to both stations, path
        /// consumption progress, cargo). Used to tell in one line where a train is stuck when "routes
        /// exist but the train does not move". The route assignment is reproduced with the same rule as
        /// TrainStep.Advance (round-robin in enumeration order).</summary>
        private static void LogTrainStates(byte factionId, List<TrainStep.StationPair> pairs)
        {
            int trainIndex = 0;
            for (int i = 0; i < State.Units.Count; i++)
            {
                UnitInstance u = State.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = State.Types.Get(u.TypeKey);
                if (t == null || t.Category != UnitCategory.MilitaryTrain) continue;

                var sb = new StringBuilder();
                sb.Append("RailRoutes:   train").Append(u.InstanceId)
                  .Append(" state=").Append(u.State)
                  .Append(" order=").Append(u.Order)
                  .Append(" pos=").Append(u.Position.X.ToString("0")).Append(",").Append(u.Position.Z.ToString("0"))
                  .Append(" path=").Append(u.Path == null ? "none" : u.PathIndex + "/" + u.Path.Count)
                  .Append(" load=").Append(u.SupplyLoad.ToString("0.00"));

                if (pairs.Count > 0)
                {
                    TrainStep.StationPair pair = pairs[trainIndex % pairs.Count];
                    WorldPos a = CargoStationRules.RailPointOf(pair.A);
                    WorldPos b = CargoStationRules.RailPointOf(pair.B);
                    sb.Append(" route=").Append(pair.A.BaseId).Append("-").Append(pair.B.BaseId)
                      .Append(" distA=").Append(u.Position.HorizontalDistanceTo(a).ToString("0"))
                      .Append(" distB=").Append(u.Position.HorizontalDistanceTo(b).ToString("0"))
                      .Append(" arriveRadius=").Append(TrainStep.StationArriveRadius.ToString("0"));
                }
                else
                {
                    sb.Append(" route=none");
                }

                ModConfig.Log(sb.ToString());
                trainIndex++;
            }
        }

        /// <summary>Breakdown for when no route forms at all. Logs, per station, "which rail node it
        /// snapped to" and "which connected component of the rail network it is in"; and per station
        /// pair, "the distance" and "the pathfinding result". This uniquely distinguishes which of the
        /// following is the case:
        ///   - built on separate rail networks (different component)
        ///   - too close (distance &lt; MinStationDistance)
        ///   - snapped to the same node (zero segment length)</summary>
        private static void LogRouteFailureDetail(byte factionId)
        {
            if (State.Rails == null)
            {
                ModConfig.Log("RailRoutes:   rail graph is not built yet");
                return;
            }

            Dictionary<ushort, int> components = State.Rails.ComputeComponentIds();
            var stations = new List<MilitaryBase>();
            for (int i = 0; i < State.Bases.Count; i++)
            {
                MilitaryBase b = State.Bases[i];
                if (b.Type != BaseType.CargoStation) continue;
                if (b.OwnerFactionId == null || b.OwnerFactionId.Value != factionId) continue;
                stations.Add(b);
            }

            var snapped = new Dictionary<ushort, int>();   // BaseId -> component (-1: could not snap)
            var snappedNode = new Dictionary<ushort, ushort>();
            for (int i = 0; i < stations.Count; i++)
            {
                MilitaryBase b = stations[i];
                // Judge at the point where trains actually depart/arrive (RailEntry; the station's
                // position if unresolved).
                WorldPos entry = CargoStationRules.RailPointOf(b);
                ushort nodeId;
                bool ok = State.Rails.TryFindNearestNode(entry, CargoStationRules.RailEntryRadius, out nodeId);
                int comp;
                if (!ok || !components.TryGetValue(nodeId, out comp)) comp = -1;
                snapped[b.BaseId] = comp;
                snappedNode[b.BaseId] = ok ? nodeId : (ushort)0;
                ModConfig.Log("RailRoutes:   station" + b.BaseId +
                    " pos=" + b.Position.X.ToString("0") + "," + b.Position.Z.ToString("0") +
                    " railEntry=" + (b.RailEntry.HasValue
                        ? entry.X.ToString("0") + "," + entry.Z.ToString("0") +
                          " (" + b.Position.HorizontalDistanceTo(entry).ToString("0") + "m away)"
                        : "unresolved") +
                    " railNode=" + (ok ? nodeId.ToString() : "none") + " component=" + comp);
            }

            for (int a = 0; a < stations.Count; a++)
            {
                for (int b2 = a + 1; b2 < stations.Count; b2++)
                {
                    MilitaryBase sa = stations[a], sb2 = stations[b2];
                    float dist = sa.Position.HorizontalDistanceTo(sb2.Position);
                    string why;
                    if (dist < TrainStep.MinStationDistance)
                    {
                        why = "too close (min " + TrainStep.MinStationDistance.ToString("0") + "m)";
                    }
                    else if (snapped[sa.BaseId] < 0 || snapped[sb2.BaseId] < 0)
                    {
                        why = "not snapped to any rail node";
                    }
                    else if (snapped[sa.BaseId] != snapped[sb2.BaseId])
                    {
                        why = "different rail networks (" + snapped[sa.BaseId] + " vs " + snapped[sb2.BaseId] + ")";
                    }
                    else
                    {
                        var path = State.Rails.FindPath(
                            CargoStationRules.RailPointOf(sa), CargoStationRules.RailPointOf(sb2),
                            CargoStationRules.RailSnapRadius * 2f);
                        why = path == null ? "no path (A* failed)"
                            : path.Count == 0 ? "both stations snap to the same node (" + snappedNode[sa.BaseId] + ")"
                            : "OK (" + path.Count + " waypoints)";
                    }
                    ModConfig.Log("RailRoutes:   pair " + sa.BaseId + "-" + sb2.BaseId +
                        " dist=" + dist.ToString("0") + "m -> " + why);
                }
            }
        }
    }
}
