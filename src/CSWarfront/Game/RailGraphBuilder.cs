using System;
using System.Collections.Generic;
using System.Text;
using ColossalFramework;
using ColossalFramework.Math;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// Task101: CSの線路網（NetManager）からCore.RoadGraph（レール用インスタンス、WarState.Rails）を
    /// 構築する（simスレッド専用）。RoadGraphBuilderの線路版——採用条件だけが違う:
    /// ItemClass.Service.PublicTransport かつ SubService.PublicTransportTrain のセグメントのみ
    /// （旅客線・貨物線の区別はしない。軍用列車=自前ビジュアルはどちらの線路でも走れる仕様）。
    /// CSの列車運行（CargoTrainAI等）には一切干渉しない。
    /// </summary>
    internal static class RailGraphBuilder
    {
        /// <summary>Task108: 位置がほぼ同じなのに別idになっているノードを1つに融合する半径（m）。
        /// 駅の構内線と本線の接合部などで、幾何的には繋がっているのにNetNodeが別々になっている
        /// ケースがあり、そのままだと線路網がバラバラの連結成分に割れて経路が引けない。</summary>
        private const float NodeWeldRadius = 6f;

        /// <summary>融合を許す高低差（m）。立体交差（10m以上の桁下）を誤って繋げないための上限。</summary>
        private const float NodeWeldHeightTolerance = 3f;

        /// <summary>Task108: 曲線セグメントを何mごとにサンプリングするか。細かいほど線路に忠実だが
        /// ノード数が増える（idはushortのため上限あり）。20mあれば見た目上ほぼ線路どおりに走る。</summary>
        private const float SegmentSampleSpacing = 20f;

        /// <summary>1セグメントあたりの中間ノード数の上限（極端に長いセグメントでの暴発防止）。</summary>
        private const int MaxSamplesPerSegment = 12;

        /// <summary>自前id空間の上限（ushort）。ここに達したら以後は中間ノードを挿さず端点のみで繋ぐ。</summary>
        private const int MaxGraphNodes = 60000;

        private static bool _failureAlreadyLogged;

        /// <summary>Task109: 曲線が信用できず直線に落としたセグメント数（ビルドごとにリセット、ログ用）。</summary>
        private static int _curveRejections;

        /// <summary>CSのNetNode idをグラフの自前idへ写す（初出なら採番してノードを作る）。</summary>
        private static ushort MapNode(RoadGraph graph, Dictionary<ushort, ushort> map, ref ushort nextId,
            ushort netNodeId, Vector3 pos)
        {
            ushort graphId;
            if (map.TryGetValue(netNodeId, out graphId)) return graphId;

            graphId = nextId;
            if (nextId < MaxGraphNodes) nextId++;
            map[netNodeId] = graphId;
            graph.AddNode(graphId, new WorldPos(pos.x, pos.y, pos.z));
            return graphId;
        }

        /// <summary>セグメントの曲線に沿って中間ノードを挿し、startId→…→endIdの折れ線として繋ぐ
        /// （曲線が取れない/短い/ノードid空間が尽きた場合は端点どうしを直接繋ぐ）。
        ///
        /// Task109（ユーザー報告「列車がレールの無いところを走る／宙を飛ぶ」）: 曲線は
        /// NetSegment.CalculateMiddlePointsで自前に組み立てるのをやめ、CS自身がレーンごとに保持している
        /// 実際の走行曲線（NetLane.m_bezier＝バニラの列車が走る線そのもの）から取る。あわせて、
        /// サンプル点が端点から常識外に離れていたらそのセグメントは直線扱いに落とす安全弁を入れる
        /// （どんな理由で曲線がおかしくても、線路から大きく外れた経路にはならない）。</summary>
        private static void AddCurvedSegment(RoadGraph graph, ref ushort nextId, ushort segmentId,
            NetSegment segment, NetInfo info, Vector3 startPos, Vector3 endPos, ushort startId, ushort endId)
        {
            float length = segment.m_averageLength;
            if (length <= 0f) length = Vector3.Distance(startPos, endPos);
            int samples = Mathf.Clamp(Mathf.CeilToInt(length / SegmentSampleSpacing) - 1, 0, MaxSamplesPerSegment);

            Bezier3 curve;
            if (samples <= 0 || nextId >= MaxGraphNodes - samples || !TryGetRailLaneBezier(segment, info, out curve))
            {
                graph.AddEdge(startId, endId);
                return;
            }

            // 安全弁: 端点間の中点から見て、サンプル点が「セグメント長の半分＋余裕」より遠ければ、
            // 曲線が信用できない（＝線路から外れる）ので直線に落とす。
            Vector3 chordMid = (startPos + endPos) * 0.5f;
            float sanityRadius = length * 0.5f + SegmentSampleSpacing;

            var points = new Vector3[samples];
            for (int s = 1; s <= samples; s++)
            {
                Vector3 p = curve.Position((float)s / (samples + 1));
                if (Vector3.Distance(p, chordMid) > sanityRadius)
                {
                    _curveRejections++;
                    graph.AddEdge(startId, endId);
                    return;
                }
                points[s - 1] = p;
            }

            ushort previous = startId;
            for (int s = 0; s < points.Length; s++)
            {
                ushort id = nextId;
                nextId++;
                graph.AddNode(id, new WorldPos(points[s].x, points[s].y, points[s].z));
                graph.AddEdge(previous, id);
                previous = id;
            }
            graph.AddEdge(previous, endId);
        }

        /// <summary>このセグメントの「列車が走るレーン」の走行曲線を返す（CS自身が計算・保持しているもの）。
        /// レーンを辿れない/列車レーンが無い場合はfalse。</summary>
        private static bool TryGetRailLaneBezier(NetSegment segment, NetInfo info, out Bezier3 bezier)
        {
            bezier = default(Bezier3);
            if (info == null || info.m_lanes == null) return false;

            NetLane[] lanes = Singleton<NetManager>.instance.m_lanes.m_buffer;
            uint laneId = segment.m_lanes;
            for (int i = 0; i < info.m_lanes.Length && laneId != 0u && laneId < lanes.Length; i++)
            {
                NetInfo.Lane lane = info.m_lanes[i];
                if (lane != null && (lane.m_vehicleType & VehicleInfo.VehicleType.Train) != 0)
                {
                    bezier = lanes[laneId].m_bezier;
                    // 長さ0（未計算）のレーンは使わない。
                    return Vector3.Distance(bezier.a, bezier.d) > 0.01f || lanes[laneId].m_length > 0.01f;
                }
                laneId = lanes[laneId].m_nextLane;
            }
            return false;
        }

        /// <summary>Task108: このNetInfoは列車が走れる線路か。従来はItemClass（PublicTransport /
        /// PublicTransportTrain）だけで判定していたが、それだと駅の構内線・貨物線・Workshopの線路
        /// アセットなど、クラス指定が異なるものを取りこぼし、線路網が分断されて見える恐れがある。
        /// レーンに列車が通れるものがあるか（VehicleType.Train）を主判定にし、ItemClassは
        /// フォールバックとして残す。地下鉄(Metro)・モノレール(Monorail)は対象外のまま。</summary>
        private static bool IsRailSegment(NetInfo info)
        {
            if (info.m_lanes != null)
            {
                for (int i = 0; i < info.m_lanes.Length; i++)
                {
                    NetInfo.Lane lane = info.m_lanes[i];
                    if (lane == null) continue;
                    if ((lane.m_vehicleType & VehicleInfo.VehicleType.Train) != 0) return true;
                }
            }

            return info.m_class != null
                && info.m_class.m_service == ItemClass.Service.PublicTransport
                && info.m_class.m_subService == ItemClass.SubService.PublicTransportTrain;
        }

        private static void LogComponentSummary(RoadGraph graph, int segmentsAccepted, int welded,
            Dictionary<string, int> rejected)
        {
            var components = graph.ComputeComponentIds();
            var sizes = new Dictionary<int, int>();
            foreach (var kv in components)
            {
                int c;
                sizes.TryGetValue(kv.Value, out c);
                sizes[kv.Value] = c + 1;
            }

            int largest = 0;
            foreach (var kv in sizes) if (kv.Value > largest) largest = kv.Value;

            var sb = new StringBuilder();
            sb.Append("RailGraphBuilder: built nodes=").Append(graph.NodeCount)
              .Append(" segmentsAccepted=").Append(segmentsAccepted)
              .Append(" weldedNodes=").Append(welded)
              .Append(" components=").Append(sizes.Count)
              .Append(" largestComponent=").Append(largest)
              .Append(" straightenedSegments=").Append(_curveRejections); // Task109: 曲線が怪しく直線化した数
            if (rejected.Count > 0)
            {
                sb.Append(" rejectedRailLikeInfos=");
                foreach (var kv in rejected) sb.Append(kv.Key).Append("(").Append(kv.Value).Append(") ");
            }
            ModConfig.Log(sb.ToString());
        }

        public static RoadGraph Build()
        {
            try
            {
                if (!Singleton<NetManager>.exists)
                {
                    if (!_failureAlreadyLogged)
                    {
                        ModConfig.LogError("RailGraphBuilder.Build: NetManager not ready; skip");
                        _failureAlreadyLogged = true;
                    }
                    return null;
                }

                NetManager nm = Singleton<NetManager>.instance;
                NetSegment[] segments = nm.m_segments.m_buffer;
                NetNode[] nodes = nm.m_nodes.m_buffer;

                _curveRejections = 0;
                var graph = new RoadGraph();
                int segmentsAccepted = 0;
                var rejected = new Dictionary<string, int>(); // Task108: 何を落としているかの内訳
                // Task108: グラフのノードidはCSのNetNode idをそのまま使わず、自前のid空間で採番する
                // （曲線サンプリングで挿す中間ノードとidが衝突しないようにするため。このグラフのidを
                // CS側へ戻す用途は無い＝位置しか使わないので、独自採番で問題ない）。
                var netNodeToGraph = new Dictionary<ushort, ushort>();
                ushort nextSyntheticId = 1;

                for (int i = 0; i < segments.Length; i++)
                {
                    if ((segments[i].m_flags & NetSegment.Flags.Created) == 0) continue;

                    NetInfo info = segments[i].Info;
                    if (info == null) continue;
                    if (!IsRailSegment(info))
                    {
                        // 鉄道っぽい名前なのに落ちているものだけ記録する（道路まで数えると無意味に膨らむ）。
                        if (info.name != null && (info.name.Contains("Track") || info.name.Contains("Rail")))
                        {
                            int c;
                            rejected.TryGetValue(info.name, out c);
                            rejected[info.name] = c + 1;
                        }
                        continue;
                    }

                    ushort startNode = segments[i].m_startNode;
                    ushort endNode = segments[i].m_endNode;
                    if (startNode >= nodes.Length || endNode >= nodes.Length) continue;
                    if ((nodes[startNode].m_flags & NetNode.Flags.Created) == 0) continue;
                    if ((nodes[endNode].m_flags & NetNode.Flags.Created) == 0) continue;

                    Vector3 startPos = nodes[startNode].m_position;
                    Vector3 endPos = nodes[endNode].m_position;
                    ushort startId = MapNode(graph, netNodeToGraph, ref nextSyntheticId, startNode, startPos);
                    ushort endId = MapNode(graph, netNodeToGraph, ref nextSyntheticId, endNode, endPos);

                    // Task108（ユーザー報告「列車が線路上を移動せず、駅間を直線的に建物を貫通して進む」）:
                    // CSのセグメントは直線ではなくベジエ曲線であり、端点2つだけを辺にすると曲線区間が
                    // 弦（ショートカット）に化ける。曲線を約SegmentSampleSpacingごとにサンプリングして
                    // 中間ノードを挿し、線路の形そのものを辿らせる。
                    AddCurvedSegment(graph, ref nextSyntheticId, (ushort)i, segments[i], info,
                        startPos, endPos, startId, endId);
                    segmentsAccepted++;
                }

                // Task108: 実機で「駅は全て稼働なのに路線が0本＝どの駅も別々の連結成分」という
                // 状態になったため、線路網が何個の塊に割れているかを毎回ログする（軍用列車が
                // 走れない原因の一次切り分け。健全なら大きな成分1つに集約されるはず）。
                int welded = graph.WeldCoincidentNodes(NodeWeldRadius, NodeWeldHeightTolerance);
                LogComponentSummary(graph, segmentsAccepted, welded, rejected);
                _failureAlreadyLogged = false;
                return graph;
            }
            catch (Exception e)
            {
                if (!_failureAlreadyLogged)
                {
                    ModConfig.LogError("RailGraphBuilder.Build exception: " + e);
                    _failureAlreadyLogged = true;
                }
                return null;
            }
        }
    }
}
