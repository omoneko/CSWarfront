using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>Simple undirected-graph representation of the road network supplied by the Game layer,
    /// plus A* pathfinding. No UnityEngine dependency.</summary>
    public class RoadGraph
    {
        private class Node
        {
            public ushort Id;
            public WorldPos Position;
            public List<ushort> Neighbors = new List<ushort>();
        }

        private readonly Dictionary<ushort, Node> _nodes = new Dictionary<ushort, Node>();

        /// <summary>Task110: lazy cache of the connected components (the graph is read-only after
        /// construction, so discarding it in AddNode/AddEdge suffices). Used for repeated reachability
        /// checks.</summary>
        private Dictionary<ushort, int> _components;

        public int NodeCount => _nodes.Count;

        /// <summary>Node id → component id (computed and cached on first access).</summary>
        public Dictionary<ushort, int> Components
        {
            get
            {
                if (_components == null) _components = ComputeComponentIds();
                return _components;
            }
        }

        /// <summary>Adds a node. Ignored if the id already exists (idempotent).</summary>
        public void AddNode(ushort id, WorldPos position)
        {
            if (_nodes.ContainsKey(id)) return;
            _nodes[id] = new Node { Id = id, Position = position };
            _components = null;
        }

        /// <summary>Adds an undirected edge. Unknown ids and self-loops are ignored. Duplicate edges are
        /// not added.</summary>
        public void AddEdge(ushort a, ushort b)
        {
            if (a == b) return;
            if (!_nodes.TryGetValue(a, out var na)) return;
            if (!_nodes.TryGetValue(b, out var nb)) return;
            if (!na.Neighbors.Contains(b)) na.Neighbors.Add(b);
            if (!nb.Neighbors.Contains(a)) nb.Neighbors.Add(a);
            _components = null;
        }

        /// <summary>Task110: finds the nearest node considering only nodes in the same connected component
        /// as the given reference node. Used to "start the path from a node that can reach the goal" — a
        /// plain nearest-node search may grab a node of a different network right next door (a siding),
        /// from which the destination can never be reached and pathfinding always fails (the cause of the
        /// in-game bug where trains sat at the station and never moved).</summary>
        public bool TryFindNearestNodeInSameComponent(WorldPos p, float maxDistance, ushort referenceNode,
            out ushort nodeId)
        {
            nodeId = 0;
            int component;
            if (!Components.TryGetValue(referenceNode, out component)) return false;
            return TryFindNearestNode(p, maxDistance, Components, component, out nodeId);
        }

        /// <summary>Task110: pathfinding between explicit node ids (no snapping). For callers that have
        /// already chosen reachable nodes, so a re-snap cannot grab a different node.
        /// Same node: empty list. Unreachable: null.</summary>
        public List<WorldPos> FindPathBetweenNodes(ushort startId, ushort goalId)
        {
            if (!_nodes.ContainsKey(startId) || !_nodes.ContainsKey(goalId)) return null;
            if (startId == goalId) return new List<WorldPos>();

            List<ushort> route = AStar(startId, goalId, 0u, 0f);
            if (route == null) return null;

            var result = new List<WorldPos>(route.Count - 1);
            for (int i = 1; i < route.Count; i++) result.Add(_nodes[route[i]].Position);
            return result;
        }

        /// <summary>Task108: connects nodes that nearly coincide in position yet carry different ids
        /// ("welding" = making them mutually traversable; ids are kept, only adjacency is added).
        /// In CS, junctions such as where a station's yard track meets the main line can be geometrically
        /// connected while their NetNodes remain distinct, splitting the network into separate components
        /// and making pathfinding fail (the countermeasure for the in-game bug "all stations operational
        /// yet zero routes"). radius is horizontal; heightTolerance caps the height difference — the guard
        /// that keeps grade separations (under a viaduct) from being wrongly joined. Returns the number of
        /// edges added. Scanning in ascending node-id order keeps the result deterministic.</summary>
        public int WeldCoincidentNodes(float radius, float heightTolerance)
        {
            if (radius <= 0f) return 0;

            // Avoid the O(N²) sweep: bucket into a radius-sized grid, then compare only the 9 neighboring cells.
            var buckets = new Dictionary<long, List<ushort>>();
            var ordered = new List<ushort>(_nodes.Keys);
            ordered.Sort();
            for (int i = 0; i < ordered.Count; i++)
            {
                Node n = _nodes[ordered[i]];
                long key = BucketKey(n.Position, radius);
                List<ushort> bucket;
                if (!buckets.TryGetValue(key, out bucket)) { bucket = new List<ushort>(); buckets[key] = bucket; }
                bucket.Add(n.Id);
            }

            int added = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                Node a = _nodes[ordered[i]];
                int cx = (int)System.Math.Floor(a.Position.X / radius);
                int cz = (int)System.Math.Floor(a.Position.Z / radius);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        List<ushort> bucket;
                        if (!buckets.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out bucket)) continue;
                        for (int k = 0; k < bucket.Count; k++)
                        {
                            ushort otherId = bucket[k];
                            if (otherId <= a.Id) continue; // visit each pair once (deterministic)
                            Node b = _nodes[otherId];
                            if (a.Position.HorizontalDistanceTo(b.Position) > radius) continue;
                            float dy = a.Position.Y - b.Position.Y;
                            if (dy < 0f) dy = -dy;
                            if (dy > heightTolerance) continue;
                            if (a.Neighbors.Contains(b.Id)) continue;
                            AddEdge(a.Id, b.Id);
                            added++;
                        }
                    }
                }
            }
            return added;
        }

        // --- Task133: making a bridge that is not wired into the road network usable ---
        //
        // Playtest question: "can a standalone bridge, not connected to any road, be crossed?" It could not.
        // Such a bridge forms its own connected component, so A* from a bank road to the far side always
        // returned null (unreachable), the unit fell back to the straight line and stopped at the water
        // (Task129/132). Land units, however, drive off-road perfectly well - the only thing they cannot do
        // is enter water. So the gap between a stranded fragment and the rest of the network is passable
        // whenever the ground between them is dry.
        //
        // LinkStrandedComponents adds exactly those "drive across the gap" edges: from nodes of a stranded
        // component to the nearest node of another component, provided the straight line between them stays
        // out of the water and is not a cliff. A* then treats the bridge as an ordinary part of the network
        // and routes over it. The main network itself is never given shortcuts (only non-largest components
        // are used as a source), so no route through the city changes.

        /// <summary>Task133: how far a stranded road fragment may reach for the rest of the network.
        /// Roughly "how far off-road a column will detour to get onto a bridge".</summary>
        public const float StrandedLinkRadius = 150f;

        /// <summary>Task133: a link steeper than this is rejected. On-path movement skips the terrain checks
        /// (Task77/125), so a connector must not be allowed to run up a cliff face.</summary>
        public const float StrandedLinkMaxSlopeDegrees = 25f;

        /// <summary>Task133: spacing of the water samples along a candidate link. Any wet sample rejects it.</summary>
        private const float StrandedLinkWaterSampleSpacing = 8f;

        /// <summary>Task133: edges added per stranded component. One or two are enough to join it to the
        /// network; the rest would only be redundant parallel gaps.</summary>
        private const int MaxLinksPerComponent = 3;

        /// <summary>Task133: nodes examined per stranded component before giving up on it. Bounds the work of
        /// a component that simply has no dry neighbour (an island), which would otherwise be re-scanned in
        /// full on every road-graph rebuild.</summary>
        private const int MaxAttemptsPerComponent = 64;

        /// <summary>Task133: connects road fragments that are separated from the main network but reachable
        /// across dry land - most importantly a bridge the player built without wiring roads to its ends.
        /// A null water sampler means "no water data", in which case nothing is linked (a connector may only
        /// be added when it can be *proven* dry; guessing would hand units a route straight through a river).
        /// Returns the number of edges added. Scanning in ascending node-id order keeps the result
        /// deterministic.</summary>
        public int LinkStrandedComponents(IWaterSampler water)
        {
            return LinkStrandedComponents(StrandedLinkRadius, StrandedLinkMaxSlopeDegrees, water);
        }

        public int LinkStrandedComponents(float radius, float maxSlopeDegrees, IWaterSampler water)
        {
            if (water == null || radius <= 0f) return 0;

            // Snapshot the component ids up front: AddEdge invalidates the cache, and re-deriving them mid-pass
            // would make the outcome depend on the order edges happened to be added.
            Dictionary<ushort, int> components = ComputeComponentIds();
            int largest;
            if (!TryGetLargestComponent(components, out largest)) return 0;

            var ordered = new List<ushort>(_nodes.Keys);
            ordered.Sort();

            var buckets = new Dictionary<long, List<ushort>>();
            for (int i = 0; i < ordered.Count; i++)
            {
                Node n = _nodes[ordered[i]];
                long key = BucketKey(n.Position, radius);
                List<ushort> bucket;
                if (!buckets.TryGetValue(key, out bucket)) { bucket = new List<ushort>(); buckets[key] = bucket; }
                bucket.Add(n.Id);
            }

            var linksPerComponent = new Dictionary<int, int>();
            var attemptsPerComponent = new Dictionary<int, int>();
            int added = 0;

            for (int i = 0; i < ordered.Count; i++)
            {
                Node a = _nodes[ordered[i]];
                int component;
                if (!components.TryGetValue(a.Id, out component)) continue;
                if (component == largest) continue; // the main network is never given shortcuts

                int links;
                linksPerComponent.TryGetValue(component, out links);
                if (links >= MaxLinksPerComponent) continue;
                int attempts;
                attemptsPerComponent.TryGetValue(component, out attempts);
                if (attempts >= MaxAttemptsPerComponent) continue;
                attemptsPerComponent[component] = attempts + 1;

                ushort partner;
                if (!TryFindLinkPartner(a, component, components, buckets, radius, maxSlopeDegrees, water, out partner))
                    continue;

                AddEdge(a.Id, partner);
                linksPerComponent[component] = links + 1;
                added++;
            }

            return added;
        }

        /// <summary>Task133: nearest node of a different component that this node can reach across dry,
        /// climbable ground. Candidates are tried nearest-first, so a blocked closest neighbour does not hide
        /// a usable one slightly further away.</summary>
        private bool TryFindLinkPartner(Node a, int ownComponent, Dictionary<ushort, int> components,
            Dictionary<long, List<ushort>> buckets, float radius, float maxSlopeDegrees, IWaterSampler water,
            out ushort partner)
        {
            partner = 0;
            var candidates = new List<ushort>();
            int cx = (int)System.Math.Floor(a.Position.X / radius);
            int cz = (int)System.Math.Floor(a.Position.Z / radius);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    List<ushort> bucket;
                    if (!buckets.TryGetValue(((long)(cx + dx) << 32) ^ (uint)(cz + dz), out bucket)) continue;
                    for (int k = 0; k < bucket.Count; k++)
                    {
                        ushort otherId = bucket[k];
                        int otherComponent;
                        if (!components.TryGetValue(otherId, out otherComponent)) continue;
                        if (otherComponent == ownComponent) continue; // already the same fragment
                        // The component ids are a snapshot taken before any edge was added, so two nodes an
                        // earlier iteration already joined still look separate here.
                        if (a.Neighbors.Contains(otherId)) continue;
                        if (a.Position.HorizontalDistanceTo(_nodes[otherId].Position) > radius) continue;
                        candidates.Add(otherId);
                    }
                }
            }
            if (candidates.Count == 0) return false;

            // Nearest first; ties resolve to the lower id so the pass stays deterministic.
            candidates.Sort((x, y) =>
            {
                float dx2 = a.Position.HorizontalDistanceTo(_nodes[x].Position);
                float dy2 = a.Position.HorizontalDistanceTo(_nodes[y].Position);
                if (dx2 < dy2) return -1;
                if (dx2 > dy2) return 1;
                return x.CompareTo(y);
            });

            for (int i = 0; i < candidates.Count; i++)
            {
                if (!IsLinkPassable(a.Position, _nodes[candidates[i]].Position, maxSlopeDegrees, water)) continue;
                partner = candidates[i];
                return true;
            }
            return false;
        }

        /// <summary>Task133: whether a land unit could actually drive the gap between two nodes - dry the
        /// whole way, and not a cliff. Both endpoints are sampled as well as the interior, because a node can
        /// sit right at the waterline.</summary>
        private static bool IsLinkPassable(WorldPos from, WorldPos to, float maxSlopeDegrees, IWaterSampler water)
        {
            float dist = from.HorizontalDistanceTo(to);
            if (dist <= 0.01f) return true; // coincident nodes: nothing to drive across

            float dy = to.Y - from.Y;
            if (dy < 0f) dy = -dy;
            double slope = System.Math.Atan(dy / dist) * 180.0 / System.Math.PI;
            if (slope > maxSlopeDegrees) return false;

            int steps = (int)(dist / StrandedLinkWaterSampleSpacing);
            for (int i = 0; i <= steps + 1; i++)
            {
                float t = i / (float)(steps + 1);
                if (water.IsWater(from.X + (to.X - from.X) * t, from.Z + (to.Z - from.Z) * t)) return false;
            }
            return true;
        }

        private static long BucketKey(WorldPos p, float cellSize)
        {
            int cx = (int)System.Math.Floor(p.X / cellSize);
            int cz = (int)System.Math.Floor(p.Z / cellSize);
            return ((long)cx << 32) ^ (uint)cz;
        }

        /// <summary>Task108: returns node id → component id, with components numbered 0,1,2… (numbering
        /// follows the ascending node-id traversal = deterministic). Used to decide/diagnose "are two
        /// locations on the same network" without running a path search (the triage for the bug where no
        /// military-train route ever formed — distinguishing a shattered rail network from stations merely
        /// too close).</summary>
        public Dictionary<ushort, int> ComputeComponentIds()
        {
            var result = new Dictionary<ushort, int>(_nodes.Count);
            var ordered = new List<ushort>(_nodes.Keys);
            ordered.Sort();

            int component = 0;
            var queue = new Queue<ushort>();
            for (int i = 0; i < ordered.Count; i++)
            {
                ushort seed = ordered[i];
                if (result.ContainsKey(seed)) continue;

                result[seed] = component;
                queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    ushort current = queue.Dequeue();
                    List<ushort> neighbors = _nodes[current].Neighbors;
                    for (int n = 0; n < neighbors.Count; n++)
                    {
                        ushort next = neighbors[n];
                        if (result.ContainsKey(next)) continue;
                        result[next] = component;
                        queue.Enqueue(next);
                    }
                }
                component++;
            }
            return result;
        }

        /// <summary>Finds the nearest node within maxDistance (horizontal).</summary>
        public bool TryFindNearestNode(WorldPos p, float maxDistance, out ushort nodeId)
        {
            return TryFindNearestNode(p, maxDistance, null, 0, out nodeId);
        }

        /// <summary>Task108: component-restricted variant. Pass ComputeComponentIds' result as components
        /// and a component number as requiredComponent to consider only that component's nodes
        /// (components==null considers every node = same as the overload above).
        /// Used to avoid "the station snapped onto its own siding (a small component split from the main
        /// line) and no path can be laid to the other stations", grabbing a main-line node instead.</summary>
        public bool TryFindNearestNode(WorldPos p, float maxDistance, Dictionary<ushort, int> components,
            int requiredComponent, out ushort nodeId)
        {
            ushort best = 0;
            float bestDist = float.MaxValue;
            bool found = false;
            foreach (var kv in _nodes)
            {
                if (components != null)
                {
                    int c;
                    if (!components.TryGetValue(kv.Key, out c) || c != requiredComponent) continue;
                }
                float d = p.HorizontalDistanceTo(kv.Value.Position);
                if (d <= maxDistance && (d < bestDist || (d == bestDist && kv.Key < best)))
                {
                    bestDist = d;
                    best = kv.Key;
                    found = true;
                }
            }
            nodeId = best;
            return found;
        }

        /// <summary>Task108: returns a node id's coordinates (used to cache a station's rail entry point).</summary>
        public bool TryGetNodePosition(ushort nodeId, out WorldPos position)
        {
            Node node;
            if (_nodes.TryGetValue(nodeId, out node)) { position = node.Position; return true; }
            position = default(WorldPos);
            return false;
        }

        /// <summary>Task110: returns, for each connected component, its nearest node and distance within
        /// maxDistance of the given point (component id → (node id, horizontal distance)). Used by the
        /// "which rail network do the stations share" vote (CargoStationRules.RefreshConnectivity). Ties
        /// resolve to the lower node id — deterministic.</summary>
        public Dictionary<int, KeyValuePair<ushort, float>> FindNearestNodePerComponent(WorldPos p, float maxDistance)
        {
            var result = new Dictionary<int, KeyValuePair<ushort, float>>();
            Dictionary<ushort, int> components = Components;
            foreach (var kv in _nodes)
            {
                float d = p.HorizontalDistanceTo(kv.Value.Position);
                if (d > maxDistance) continue;
                int component;
                if (!components.TryGetValue(kv.Key, out component)) continue;

                KeyValuePair<ushort, float> best;
                if (!result.TryGetValue(component, out best)
                    || d < best.Value || (d == best.Value && kv.Key < best.Key))
                {
                    result[component] = new KeyValuePair<ushort, float>(kv.Key, d);
                }
            }
            return result;
        }

        /// <summary>Task108: returns the component containing the most nodes (= the de-facto "main-line
        /// network"). False when there are no nodes. Ties resolve to the lower component id
        /// (deterministic).</summary>
        public bool TryGetLargestComponent(Dictionary<ushort, int> components, out int largestComponent)
        {
            largestComponent = 0;
            if (components == null || components.Count == 0) return false;

            var counts = new Dictionary<int, int>();
            foreach (var kv in components)
            {
                int c;
                counts.TryGetValue(kv.Value, out c);
                counts[kv.Value] = c + 1;
            }

            int bestCount = -1;
            foreach (var kv in counts)
            {
                if (kv.Value > bestCount || (kv.Value == bestCount && kv.Key < largestComponent))
                {
                    bestCount = kv.Value;
                    largestComponent = kv.Key;
                }
            }
            return true;
        }

        /// <summary>
        /// Snaps from/to to their nearest nodes within snapRadius and finds an A* path.
        /// The return value is the sequence of WorldPos from the node AFTER the start node through the goal
        /// node. Snap failure / no route: null. Start node == goal node: empty list.
        /// Delegates to the overload below without jitter (seed=0, jitter=0).
        /// </summary>
        public List<WorldPos> FindPath(WorldPos from, WorldPos to, float snapRadius)
        {
            return FindPath(from, to, snapRadius, 0u, 0f);
        }

        /// <summary>
        /// Jittered variant of FindPath. Gives each unit its own deterministic "preferred detour", fixing
        /// the problem of every unit always taking the identical shortest route (parallel roads never
        /// chosen). With seed==0 or jitter&lt;=0 the behavior is exactly that of the 3-argument overload
        /// (pure shortest path). Otherwise each edge cost is raised by a factor derived deterministically
        /// from (seed, nodeA, nodeB), up to the jitter fraction: cost * (1 + jitter * f), f∈[0,1). Edge
        /// costs only ever increase. The heuristic stays the unweighted horizontal distance (not
        /// jittered): by the triangle inequality the heuristic (straight-line distance) remains ≤ the sum
        /// of the increased true costs, so A* admissibility (the heuristic never exceeds the true cost)
        /// holds. For the same reason, reachability itself is unaffected by jitter (edge existence is
        /// unchanged; only the relative ordering of costs moves).
        /// </summary>
        public List<WorldPos> FindPath(WorldPos from, WorldPos to, float snapRadius, uint seed, float jitter)
        {
            return FindPath(from, to, snapRadius, seed, jitter, snapRadius);
        }

        /// <summary>Task88 (user request "land routes to the nemesis should stay on roads as much as
        /// possible"): variant with an independent destination-side snap radius. Roaming threats (Godzilla
        /// etc.) are often more than snapRadius (200) from any road; previously the snap failed → null path
        /// → the whole leg became off-road straight-line movement. Passing float.MaxValue as destSnapRadius
        /// yields "take roads to the node nearest the destination, then go straight for the rest" (the
        /// straight-line fallback after the path is consumed is MovementStep's existing behavior).
        /// The origin-side snap keeps using snapRadius (when the unit itself is far from any road,
        /// straight-line movement is correct).</summary>
        public List<WorldPos> FindPath(WorldPos from, WorldPos to, float snapRadius, uint seed, float jitter,
            float destSnapRadius)
        {
            if (!TryFindNearestNode(from, snapRadius, out ushort startId)) return null;
            if (!TryFindNearestNode(to, destSnapRadius, out ushort goalId)) return null;

            if (startId == goalId) return new List<WorldPos>();

            bool useJitter = seed != 0 && jitter > 0f;
            List<ushort> route = AStar(startId, goalId, useJitter ? seed : 0u, useJitter ? jitter : 0f);
            if (route == null) return null;

            var result = new List<WorldPos>(route.Count - 1);
            for (int i = 1; i < route.Count; i++)
                result.Add(_nodes[route[i]].Position);
            return result;
        }

        private List<ushort> AStar(ushort startId, ushort goalId, uint seed, float jitter)
        {
            var goalPos = _nodes[goalId].Position;

            var openSet = new List<ushort> { startId };
            var cameFrom = new Dictionary<ushort, ushort>();
            var gScore = new Dictionary<ushort, float> { { startId, 0f } };
            var fScore = new Dictionary<ushort, float> { { startId, Heuristic(startId, goalPos) } };
            var closed = new HashSet<ushort>();

            while (openSet.Count > 0)
            {
                ushort current = PickLowestFScore(openSet, fScore);

                if (current == goalId)
                    return ReconstructPath(cameFrom, current);

                openSet.Remove(current);
                closed.Add(current);

                var currentNode = _nodes[current];
                float currentG = gScore[current];
                foreach (var neighborId in currentNode.Neighbors)
                {
                    if (closed.Contains(neighborId)) continue;

                    float edgeCost = currentNode.Position.HorizontalDistanceTo(_nodes[neighborId].Position);
                    if (jitter > 0f)
                        edgeCost *= 1f + jitter * EdgeJitterFactor(seed, current, neighborId);
                    float tentativeG = currentG + edgeCost;

                    bool inOpen = gScore.TryGetValue(neighborId, out float existingG);
                    if (!inOpen || tentativeG < existingG)
                    {
                        cameFrom[neighborId] = current;
                        gScore[neighborId] = tentativeG;
                        fScore[neighborId] = tentativeG + Heuristic(neighborId, goalPos);
                        if (!openSet.Contains(neighborId))
                            openSet.Add(neighborId);
                    }
                }
            }

            return null; // unreachable
        }

        /// <summary>
        /// Derives a deterministic [0,1) factor from (seed, nodeA, nodeB). Normalized via the smaller/larger
        /// of a/b, so traversing the edge in either direction yields the same factor (order-independent).
        /// No System.Random — pure integer arithmetic only.
        /// </summary>
        private static float EdgeJitterFactor(uint seed, ushort nodeA, ushort nodeB)
        {
            uint lo = nodeA < nodeB ? nodeA : nodeB;
            uint hi = nodeA < nodeB ? nodeB : nodeA;
            uint h = Mix(seed ^ lo);
            h = Mix(h ^ hi);
            // Use the top 24 bits, normalized to [0, 1).
            return (h >> 8) / (float)(1u << 24);
        }

        /// <summary>xorshift-style 32-bit integer avalanche mix. A simple deterministic hash — the same
        /// input always yields the same output. No System.Random or other non-deterministic/seeded RNG.</summary>
        private static uint Mix(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352dU;
            x ^= x >> 15;
            x *= 0x846ca68bU;
            x ^= x >> 16;
            return x;
        }

        private float Heuristic(ushort nodeId, WorldPos goalPos)
        {
            return _nodes[nodeId].Position.HorizontalDistanceTo(goalPos);
        }

        /// <summary>Picks the node with the lowest fScore. Ties prefer the lower id, keeping the result
        /// deterministic.</summary>
        private static ushort PickLowestFScore(List<ushort> openSet, Dictionary<ushort, float> fScore)
        {
            ushort best = openSet[0];
            float bestScore = fScore.TryGetValue(best, out float bs) ? bs : float.MaxValue;
            for (int i = 1; i < openSet.Count; i++)
            {
                ushort candidate = openSet[i];
                float score = fScore.TryGetValue(candidate, out float cs) ? cs : float.MaxValue;
                if (score < bestScore || (score == bestScore && candidate < best))
                {
                    best = candidate;
                    bestScore = score;
                }
            }
            return best;
        }

        private static List<ushort> ReconstructPath(Dictionary<ushort, ushort> cameFrom, ushort current)
        {
            var path = new List<ushort> { current };
            while (cameFrom.TryGetValue(current, out ushort prev))
            {
                current = prev;
                path.Add(current);
            }
            path.Reverse();
            return path;
        }
    }
}
