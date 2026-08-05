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
                    if (pairs.Count == 0 && operational >= 2)
                        sb.Append(" (stations too close or not on the same rail network)");
                    else if (pairs.Count == 0 && stations > operational)
                        sb.Append(" (station not within ")
                          .Append(CargoStationRules.RailSnapRadius.ToString("0"))
                          .Append("m of a rail line)");
                    ModConfig.Log(sb.ToString());
                }
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("LogRailRoutes error: " + e);
            }
        }
    }
}
