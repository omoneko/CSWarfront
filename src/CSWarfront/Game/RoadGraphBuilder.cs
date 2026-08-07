using System;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// Builds a Core.RoadGraph from CS's road network (NetManager) (sim thread only, Task23).
    /// Threading note: intended to be called from MilitaryManager.OnSimTick. NetManager's
    /// segment/node buffers are data owned by the sim side, so reading them on the sim thread is safe
    /// (same reasoning as DevelopmentSampler; we never touch main-thread-only APIs (rendering, UI,
    /// etc.)).
    ///
    /// Verified signatures (confirmed via reflection on Assembly-CSharp.dll / ColossalFramework,
    /// Task23):
    ///  - NetManager.m_segments: Array16&lt;NetSegment&gt; (field m_buffer is NetSegment[])
    ///  - NetManager.m_nodes: Array16&lt;NetNode&gt; (field m_buffer is NetNode[])
    ///  - NetSegment.m_flags: NetSegment.Flags ([Flags] enum, has Created flag)
    ///  - NetSegment.Info: NetInfo property (getter)
    ///  - NetSegment.m_startNode / m_endNode: System.UInt16
    ///  - NetInfo.m_class: ItemClass field (declared by NetInfo itself, not inherited from PrefabInfo)
    ///  - ItemClass.m_service: ItemClass.Service (a plain enum, not [Flags]; has a Road value)
    ///  - NetNode.m_flags: NetNode.Flags ([Flags] enum, has Created flag)
    ///  - NetNode.m_position: UnityEngine.Vector3
    /// </summary>
    internal static class RoadGraphBuilder
    {
        // For throttling failure logs (Task23 review, Important). The caller
        // (MilitaryManager.OnSimTick) keeps calling RoadGraphBuilder.Build repeatedly with
        // State.Roads == null while failures persist, so without suppression a log line would be
        // emitted on every failed attempt. Record only the first occurrence and do not emit again
        // until a success. After a success, the next failure is logged once again (the suppression
        // state is reset).
        // Sim-thread-only access, so no lock is needed.
        private static bool _failureAlreadyLogged;

        /// <summary>
        /// Builds a RoadGraph from NetManager's road network. Returns null on failure (the caller
        /// must keep the existing graph).
        /// </summary>
        public static RoadGraph Build()
        {
            try
            {
                if (!Singleton<NetManager>.exists)
                {
                    if (!_failureAlreadyLogged)
                    {
                        ModConfig.LogError("RoadGraphBuilder.Build: NetManager not ready; skip");
                        _failureAlreadyLogged = true;
                    }
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
                    // Accept roads only (rails, pipelines, power lines, etc. are out of scope).
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
                _failureAlreadyLogged = false; // Success, so the next failure will again be logged once
                return graph;
            }
            catch (Exception e)
            {
                if (!_failureAlreadyLogged)
                {
                    ModConfig.LogError("RoadGraphBuilder.Build exception: " + e);
                    _failureAlreadyLogged = true;
                }
                return null;
            }
        }
    }
}
