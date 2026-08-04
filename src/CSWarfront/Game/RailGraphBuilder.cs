using System;
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
        private static bool _failureAlreadyLogged;

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

                for (int i = 0; i < segments.Length; i++)
                {
                    if ((segments[i].m_flags & NetSegment.Flags.Created) == 0) continue;

                    NetInfo info = segments[i].Info;
                    if (info == null || info.m_class == null) continue;
                    // 線路のみを採用する（Train系のSubServiceで判定。地下鉄・モノレール等は対象外）。
                    if (info.m_class.m_service != ItemClass.Service.PublicTransport) continue;
                    if (info.m_class.m_subService != ItemClass.SubService.PublicTransportTrain) continue;

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

                ModConfig.Log("RailGraphBuilder: built nodes=" + graph.NodeCount +
                    " segmentsAccepted=" + segmentsAccepted);
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
