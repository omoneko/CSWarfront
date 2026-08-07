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
    /// Task101: builds a Core.RoadGraph (the rail instance, WarState.Rails) from CS's rail network
    /// (NetManager) (sim thread only). The rail counterpart of RoadGraphBuilder — only the acceptance
    /// condition differs: only segments with ItemClass.Service.PublicTransport and
    /// SubService.PublicTransportTrain (no distinction between passenger and cargo lines; by design,
    /// military trains = our own visuals can run on either kind of track).
    /// Does not interfere with CS's train operations (CargoTrainAI etc.) in any way.
    /// </summary>
    internal static class RailGraphBuilder
    {
        /// <summary>Task108: radius (m) for welding nodes that have nearly identical positions but
        /// different ids into one. At junctions between station yard tracks and main lines, etc.,
        /// there are cases where the NetNodes are separate even though they are geometrically
        /// connected; left as-is, the rail network splits into disconnected components and no route
        /// can be found.</summary>
        private const float NodeWeldRadius = 6f;

        /// <summary>Height difference (m) allowed for welding. Upper bound to avoid mistakenly
        /// connecting grade separations (10m+ of clearance under a girder).</summary>
        private const float NodeWeldHeightTolerance = 3f;

        /// <summary>Task108: how many meters apart to sample curved segments. Finer is more faithful
        /// to the track but increases node count (ids are ushort, so there is a cap). 20m is enough
        /// to visually follow the track almost exactly.</summary>
        private const float SegmentSampleSpacing = 20f;

        /// <summary>Cap on intermediate nodes per segment (guards against blowup on extremely long
        /// segments).</summary>
        private const int MaxSamplesPerSegment = 12;

        /// <summary>Upper bound of our own id space (ushort). Once reached, no more intermediate
        /// nodes are inserted; segments are connected by their endpoints only.</summary>
        private const int MaxGraphNodes = 60000;

        private static bool _failureAlreadyLogged;

        /// <summary>Task109: number of segments whose curve was untrusted and fell back to a straight
        /// line (reset per build, for logging).</summary>
        private static int _curveRejections;

        /// <summary>Maps a CS NetNode id to our own graph id (on first appearance, assigns an id and
        /// creates the node).</summary>
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

        /// <summary>Inserts intermediate nodes along the segment's curve and connects them as a
        /// polyline startId→…→endId (if the curve is unavailable/too short/the node id space is
        /// exhausted, connects the endpoints directly).
        ///
        /// Task109 (user report "trains run where there are no rails / fly through the air"): stop
        /// assembling the curve ourselves via NetSegment.CalculateMiddlePoints; instead take it from
        /// the actual travel curve CS itself keeps per lane (NetLane.m_bezier = the very line vanilla
        /// trains run on). Additionally, add a safety valve that demotes a segment to a straight line
        /// if a sample point is unreasonably far from the endpoints (whatever the reason the curve is
        /// wrong, the route will never deviate far from the track).</summary>
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

            // Safety valve: if a sample point is farther from the midpoint between the endpoints than
            // "half the segment length + margin", the curve is untrusted (= would leave the track),
            // so fall back to a straight line.
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

        /// <summary>Returns the travel curve of this segment's "lane trains run on" (the one CS itself
        /// computes and stores). Returns false if the lanes cannot be traversed or there is no train
        /// lane.</summary>
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
                    // Do not use lanes with zero length (not yet computed).
                    return Vector3.Distance(bezier.a, bezier.d) > 0.01f || lanes[laneId].m_length > 0.01f;
                }
                laneId = lanes[laneId].m_nextLane;
            }
            return false;
        }

        /// <summary>Task108: is this NetInfo a track trains can run on? Previously judged only by
        /// ItemClass (PublicTransport / PublicTransportTrain), but that risks missing station yard
        /// tracks, cargo lines, Workshop track assets, and other items with different class settings,
        /// making the rail network appear fragmented. The primary test is now whether any lane allows
        /// trains (VehicleType.Train), with ItemClass kept as a fallback. Metro and Monorail remain
        /// excluded.</summary>
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
            Dictionary<string, int> rejected, int outsideExcluded)
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
              .Append(" straightenedSegments=").Append(_curveRejections) // Task109: count of segments straightened due to suspect curves
              .Append(" outsideExcluded=").Append(outsideExcluded);      // Task110: count of excluded outside-connection segments
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
                int outsideExcluded = 0; // Task110: count of segments excluded for touching outside connections (for logging)
                var graph = new RoadGraph();
                int segmentsAccepted = 0;
                var rejected = new Dictionary<string, int>(); // Task108: breakdown of what is being dropped
                // Task108: the graph's node ids are not the CS NetNode ids as-is; we assign them from
                // our own id space (so that ids do not collide with the intermediate nodes inserted by
                // curve sampling. This graph's ids are never fed back to the CS side = only positions
                // are used, so independent numbering is fine).
                var netNodeToGraph = new Dictionary<ushort, ushort>();
                ushort nextSyntheticId = 1;

                for (int i = 0; i < segments.Length; i++)
                {
                    if ((segments[i].m_flags & NetSegment.Flags.Created) == 0) continue;

                    NetInfo info = segments[i].Info;
                    if (info == null) continue;
                    if (!IsRailSegment(info))
                    {
                        // Record only items with rail-like names that were rejected (counting roads
                        // too would bloat this meaninglessly).
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

                    // Task110 (user request "run only on tracks inside the city"): do not accept
                    // segments that connect to outside connections at the map edge. This cuts off, at
                    // the root, military trains being routed out of the city via outside-connection
                    // nodes (does not interfere with vanilla train operations in any way).
                    if ((nodes[startNode].m_flags & NetNode.Flags.Outside) != 0
                        || (nodes[endNode].m_flags & NetNode.Flags.Outside) != 0)
                    {
                        outsideExcluded++;
                        continue;
                    }

                    Vector3 startPos = nodes[startNode].m_position;
                    Vector3 endPos = nodes[endNode].m_position;
                    ushort startId = MapNode(graph, netNodeToGraph, ref nextSyntheticId, startNode, startPos);
                    ushort endId = MapNode(graph, netNodeToGraph, ref nextSyntheticId, endNode, endPos);

                    // Task108 (user report "trains do not move along the tracks; they go in straight
                    // lines between stations, cutting through buildings"): CS segments are Bezier
                    // curves, not straight lines, and making an edge from just the two endpoints turns
                    // a curved section into its chord (a shortcut). Sample the curve roughly every
                    // SegmentSampleSpacing and insert intermediate nodes so trains follow the actual
                    // shape of the track.
                    AddCurvedSegment(graph, ref nextSyntheticId, (ushort)i, segments[i], info,
                        startPos, endPos, startId, endId);
                    segmentsAccepted++;
                }

                // Task108: on real hardware we hit a state where "all stations are operating yet there
                // are 0 lines = every station is in a separate connected component", so log every time
                // how many chunks the rail network is split into (first-line triage for why military
                // trains cannot run. If healthy, it should collapse into one large component).
                int welded = graph.WeldCoincidentNodes(NodeWeldRadius, NodeWeldHeightTolerance);
                LogComponentSummary(graph, segmentsAccepted, welded, rejected, outsideExcluded);
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
