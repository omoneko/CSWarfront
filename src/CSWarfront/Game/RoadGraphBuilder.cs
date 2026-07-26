using System;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// CSの道路網（NetManager）からCore.RoadGraphを構築する（simスレッド専用、Task23）。
    /// スレッド注記: MilitaryManager.OnSimTickから呼ばれる想定。NetManagerのセグメント/ノードバッファは
    /// sim側が所有するデータのため、simスレッドでの読み取りは安全（DevelopmentSamplerと同じ理由。
    /// メインスレッド専用API（描画・UI等）には一切触れない）。
    ///
    /// 検証済みシグネチャ（Assembly-CSharp.dll / ColossalFrameworkをリフレクションで確認、Task23）:
    ///  - NetManager.m_segments: Array16&lt;NetSegment&gt;（フィールドm_bufferはNetSegment[]）
    ///  - NetManager.m_nodes: Array16&lt;NetNode&gt;（フィールドm_bufferはNetNode[]）
    ///  - NetSegment.m_flags: NetSegment.Flags（[Flags]enum、Createdフラグあり）
    ///  - NetSegment.Info: NetInfoプロパティ（getter）
    ///  - NetSegment.m_startNode / m_endNode: System.UInt16
    ///  - NetInfo.m_class: ItemClassフィールド（NetInfo自身が宣言、PrefabInfo由来ではない）
    ///  - ItemClass.m_service: ItemClass.Service（[Flags]ではない単純enum、Road値あり）
    ///  - NetNode.m_flags: NetNode.Flags（[Flags]enum、Createdフラグあり）
    ///  - NetNode.m_position: UnityEngine.Vector3
    /// </summary>
    internal static class RoadGraphBuilder
    {
        /// <summary>
        /// NetManagerの道路網からRoadGraphを構築する。失敗時はnullを返す（呼び出し側は既存グラフを維持すること）。
        /// </summary>
        public static RoadGraph Build()
        {
            try
            {
                if (!Singleton<NetManager>.exists)
                {
                    ModConfig.LogError("RoadGraphBuilder.Build: NetManager not ready; skip");
                    return null;
                }

                NetManager nm = Singleton<NetManager>.instance;
                NetSegment[] segments = nm.m_segments.m_buffer;
                NetNode[] nodes = nm.m_nodes.m_buffer;

                var graph = new RoadGraph();
                int segmentsAccepted = 0;
                int segmentsSkippedNonRoad = 0;

                for (int i = 0; i < segments.Length; i++)
                {
                    if ((segments[i].m_flags & NetSegment.Flags.Created) == 0) continue;

                    NetInfo info = segments[i].Info;
                    if (info == null || info.m_class == null)
                    {
                        segmentsSkippedNonRoad++;
                        continue;
                    }
                    // 道路のみを採用する（線路・パイプライン・送電線等は対象外）。
                    if (info.m_class.m_service != ItemClass.Service.Road)
                    {
                        segmentsSkippedNonRoad++;
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

                ModConfig.Log("RoadGraphBuilder: built nodes=" + graph.NodeCount +
                    " segmentsAccepted=" + segmentsAccepted +
                    " segmentsSkippedNonRoad=" + segmentsSkippedNonRoad);
                return graph;
            }
            catch (Exception e)
            {
                ModConfig.LogError("RoadGraphBuilder.Build exception: " + e);
                return null;
            }
        }
    }
}
