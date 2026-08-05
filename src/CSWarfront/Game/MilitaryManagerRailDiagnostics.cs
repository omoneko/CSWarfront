using System.Collections.Generic;
using System.Text;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task107: 鉄道輸送の計装（MilitaryManager partial、500行制限のため分離）。
    ///
    /// ユーザー報告「軍用貨物列車がスポーンしても身動きできず文鎮化する」の原因は、実機でしか
    /// 分からない条件（駅がレール網に届いているか／路線が成立しているか／列車が担当路線を
    /// 持っているか）に依存する。レール網の再構築のたびに、勢力ごとの
    ///   貨物駅の総数 / うち稼働（所有＋レール接続＋HP残）/ 成立した路線ペア数 / 列車数
    /// をログへ出す。列車が動かないときに、どの段階で切れているのかがこれ1行で分かる。
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>simスレッド（OnSimTick、_stateLock内）。</summary>
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

                    if (stations == 0 && trains == 0) continue; // 鉄道要素を持たない勢力は黙る

                    List<TrainStep.StationPair> pairs = TrainStep.FindStationPairs(State, f.Id);
                    StringBuilder sb = new StringBuilder();
                    sb.Append("RailRoutes: f").Append(f.Id)
                      .Append(" stations=").Append(stations)
                      .Append(" operational=").Append(operational)
                      .Append(" routes=").Append(pairs.Count)
                      .Append(" trains=").Append(trains)
                      .Append(" supplyStock=").Append(f.SupplyStock.ToString("0"));
                    ModConfig.Log(sb.ToString());

                    // 路線が1本も成立しないときだけ、駅ごと/ペアごとの内訳を出す（原因の切り分け用）。
                    if (pairs.Count == 0 && operational >= 2) LogRouteFailureDetail(f.Id);
                }
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("LogRailRoutes error: " + e);
            }
        }

        /// <summary>路線が1本も成立しないときの内訳。駅ごとに「スナップ先のレールノード」「レール網の
        /// どの連結成分か」を、駅ペアごとに「距離」「経路探索の結果」を出す。これで
        ///   - 別々の線路網に建っている（component が違う）
        ///   - 近すぎる（distance < MinStationDistance）
        ///   - 同じノードにスナップしている（区間長ゼロ）
        /// のどれなのかが一意に分かる。</summary>
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

            var snapped = new Dictionary<ushort, int>();   // BaseId -> component (-1: スナップ不可)
            var snappedNode = new Dictionary<ushort, ushort>();
            for (int i = 0; i < stations.Count; i++)
            {
                MilitaryBase b = stations[i];
                // 実際に列車が発着する地点（RailEntry、未解決なら駅の位置）で判定する。
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
