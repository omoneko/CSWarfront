using System;
using System.Collections.Generic;
using System.Text;
using ColossalFramework;
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

        private static bool _failureAlreadyLogged;

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
              .Append(" largestComponent=").Append(largest);
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

                var graph = new RoadGraph();
                int segmentsAccepted = 0;
                var rejected = new Dictionary<string, int>(); // Task108: 何を落としているかの内訳

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
                    graph.AddNode(startNode, new WorldPos(startPos.x, startPos.y, startPos.z));
                    graph.AddNode(endNode, new WorldPos(endPos.x, endPos.y, endPos.z));
                    graph.AddEdge(startNode, endNode);
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
