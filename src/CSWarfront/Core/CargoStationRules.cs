using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>Task101: 軍用貨物駅の稼働判定（設計§3）。駅はレール網（WarState.Rails）の
    /// ノードがRailSnapRadius以内にあるときだけ「接続済み」となり、鉄道輸送（TrainStep）に使われる。
    /// 未接続でも備蓄（StoredSupplies）・占領は機能する。</summary>
    public static class CargoStationRules
    {
        /// <summary>駅とみなすレールノードまでの最大距離（m）。</summary>
        public const float RailSnapRadius = 100f;

        /// <summary>Task108: 本線網（最大の連結成分）上の進入点を探すときの最大距離（m）。
        /// RailSnapRadiusより広いのは、駅の真横が「その駅の引き込み線＝本線から分断された小さな成分」
        /// である場合に、少し離れた本線のノードを掴ませるため（実機で路線が1本も成立しなかった
        /// ケースの対策）。</summary>
        public const float RailEntryRadius = 300f;

        /// <summary>全貨物駅のRailConnected／RailEntry（列車が実際に発着するレール上の地点）を
        /// 引き直す（レール網の構築/再構築のたびにGame層が呼ぶ）。
        ///
        /// Task110（ユーザー報告「列車が線路以外を通って域外まで往復する」）: 進入点の選び方を
        /// 「最大の連結成分」から「駅たちが共有している連結成分」へ変更した。曲線サンプリングで
        /// ノード数が線路の長さに比例するようになった結果、マップに元から敷かれている長大な既設線
        /// （域外接続へ続く縦断本線）が常に最大成分になり、全駅の進入点がそちらへ吸われて、列車が
        /// 都市の軍用線ではなく既設線を域外方向へ走っていた。
        ///
        /// 選定規則（決定的）:
        ///  1. 各駅からRailEntryRadius以内にある成分ごとの最寄りノードを集める。
        ///  2. 「届く駅の数」が最多の成分を選ぶ（＝駅たちを実際に結んでいる網）。
        ///  3. 同数なら、駅からの距離の合計が最小の成分（＝駅のすぐ横を走っている網）。
        /// 勝った成分に届かない駅は、従来どおりRailSnapRadius以内の最寄りノードへフォールバックする
        /// （別勢力が別の網に駅を建てているケースはこちらで機能する）。</summary>
        public static void RefreshConnectivity(WarState state)
        {
            var stations = new List<MilitaryBase>();
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.Type != BaseType.CargoStation) continue;
                b.RailEntry = null;
                b.RailConnected = false;
                stations.Add(b);
            }
            if (state.Rails == null || stations.Count == 0) return;

            // 1. 駅ごとの「成分→(最寄りノード, 距離)」と、成分ごとの得票・距離合計を集計する。
            var perStation = new Dictionary<int, KeyValuePair<ushort, float>>[stations.Count];
            var votes = new Dictionary<int, int>();
            var distanceSum = new Dictionary<int, float>();
            for (int i = 0; i < stations.Count; i++)
            {
                perStation[i] = state.Rails.FindNearestNodePerComponent(stations[i].Position, RailEntryRadius);
                foreach (var kv in perStation[i])
                {
                    int c;
                    votes.TryGetValue(kv.Key, out c);
                    votes[kv.Key] = c + 1;
                    float sum;
                    distanceSum.TryGetValue(kv.Key, out sum);
                    distanceSum[kv.Key] = sum + kv.Value.Value;
                }
            }

            // 2-3. 得票最多 → 距離合計最小 → 成分番号最小、の順で決定的に選ぶ。
            int winner = -1;
            foreach (var kv in votes)
            {
                if (winner < 0) { winner = kv.Key; continue; }
                int cmp = kv.Value.CompareTo(votes[winner]);
                if (cmp > 0 || (cmp == 0 && distanceSum[kv.Key] < distanceSum[winner])
                    || (cmp == 0 && distanceSum[kv.Key] == distanceSum[winner] && kv.Key < winner))
                {
                    winner = kv.Key;
                }
            }

            for (int i = 0; i < stations.Count; i++)
            {
                MilitaryBase b = stations[i];
                ushort nodeId;
                KeyValuePair<ushort, float> hit;
                if (winner >= 0 && perStation[i].TryGetValue(winner, out hit))
                {
                    nodeId = hit.Key;
                }
                else if (!state.Rails.TryFindNearestNode(b.Position, RailSnapRadius, out nodeId))
                {
                    continue; // どの網にも届かない: 未接続のまま
                }

                WorldPos entry;
                if (state.Rails.TryGetNodePosition(nodeId, out entry))
                {
                    b.RailEntry = entry;
                    b.RailConnected = true;
                }
            }
        }

        /// <summary>Task108: この駅で列車が発着するレール上の地点（進入点。未解決なら駅そのものの位置）。</summary>
        public static WorldPos RailPointOf(MilitaryBase b)
        {
            return b.RailEntry.HasValue ? b.RailEntry.Value : b.Position;
        }

        /// <summary>この駅は鉄道輸送に使えるか（所有・レール接続・HP残存）。</summary>
        public static bool IsOperational(MilitaryBase b)
        {
            return b != null && b.Type == BaseType.CargoStation
                && b.OwnerFactionId != null && b.RailConnected && b.CurrentHP > 0f;
        }
    }
}
