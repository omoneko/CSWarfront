using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>Game層から供給される道路網の単純な無向グラフ表現＋A*経路探索。UnityEngine非依存。</summary>
    public class RoadGraph
    {
        private class Node
        {
            public ushort Id;
            public WorldPos Position;
            public List<ushort> Neighbors = new List<ushort>();
        }

        private readonly Dictionary<ushort, Node> _nodes = new Dictionary<ushort, Node>();

        public int NodeCount => _nodes.Count;

        /// <summary>ノードを追加する。同じidが既にあれば無視（idempotent）。</summary>
        public void AddNode(ushort id, WorldPos position)
        {
            if (_nodes.ContainsKey(id)) return;
            _nodes[id] = new Node { Id = id, Position = position };
        }

        /// <summary>無向辺を追加する。未知のid・自己ループは無視。重複辺は追加しない。</summary>
        public void AddEdge(ushort a, ushort b)
        {
            if (a == b) return;
            if (!_nodes.TryGetValue(a, out var na)) return;
            if (!_nodes.TryGetValue(b, out var nb)) return;
            if (!na.Neighbors.Contains(b)) na.Neighbors.Add(b);
            if (!nb.Neighbors.Contains(a)) nb.Neighbors.Add(a);
        }

        /// <summary>水平距離でmaxDistance以内の最近傍ノードを探す。</summary>
        public bool TryFindNearestNode(WorldPos p, float maxDistance, out ushort nodeId)
        {
            ushort best = 0;
            float bestDist = float.MaxValue;
            bool found = false;
            foreach (var kv in _nodes)
            {
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

        /// <summary>
        /// from/to を snapRadius 以内の最近傍ノードへスナップし、A*で経路を求める。
        /// 戻り値は開始ノードの「次」から目的ノードまでのWorldPosの並び。
        /// スナップ失敗・経路なしはnull。開始ノード==目的ノードは空リスト。
        /// </summary>
        public List<WorldPos> FindPath(WorldPos from, WorldPos to, float snapRadius)
        {
            if (!TryFindNearestNode(from, snapRadius, out ushort startId)) return null;
            if (!TryFindNearestNode(to, snapRadius, out ushort goalId)) return null;

            if (startId == goalId) return new List<WorldPos>();

            List<ushort> route = AStar(startId, goalId);
            if (route == null) return null;

            var result = new List<WorldPos>(route.Count - 1);
            for (int i = 1; i < route.Count; i++)
                result.Add(_nodes[route[i]].Position);
            return result;
        }

        private List<ushort> AStar(ushort startId, ushort goalId)
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

                    float tentativeG = currentG + currentNode.Position.HorizontalDistanceTo(_nodes[neighborId].Position);

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

            return null; // 到達不能
        }

        private float Heuristic(ushort nodeId, WorldPos goalPos)
        {
            return _nodes[nodeId].Position.HorizontalDistanceTo(goalPos);
        }

        /// <summary>fScore最小のノードを選ぶ。同点は低いidを優先し、決定的な結果にする。</summary>
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
