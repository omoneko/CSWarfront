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
        /// ジッタなし（seed=0, jitter=0）で下のオーバーロードへ委譲する。
        /// </summary>
        public List<WorldPos> FindPath(WorldPos from, WorldPos to, float snapRadius)
        {
            return FindPath(from, to, snapRadius, 0u, 0f);
        }

        /// <summary>
        /// FindPathのジッタ付き版。ユニットごとに異なる「決定的な好みの遠回り」を選ばせ、
        /// 全ユニットが常に同一の最短経路を通る問題（う回路が選ばれない）を解消する。
        /// seed==0 または jitter&lt;=0 のときは上の3引数オーバーロードと完全に同じ挙動（最短経路のみ）になる。
        /// それ以外は各辺のコストを (seed, nodeA, nodeB) から決定的に導いた係数分だけ最大jitter割合まで
        /// 引き上げる：cost * (1 + jitter * f)、f∈[0,1)。辺コストは常に増加方向のみに動く。
        /// ヒューリスティックは常に無加重の水平距離のまま（jitterしない）：三角不等式により
        /// heuristic（直線距離）は増加後の実コストの総和以下であり続けるため、A*のadmissibility
        /// （ヒューリスティックが真のコストを超えない）は保たれる。同じ理由で、経路そのものの
        /// 到達可能性はjitterで変化しない（辺の存在／非存在は変えない。コストの相対順序が変わるだけ）。
        /// </summary>
        public List<WorldPos> FindPath(WorldPos from, WorldPos to, float snapRadius, uint seed, float jitter)
        {
            return FindPath(from, to, snapRadius, seed, jitter, snapRadius);
        }

        /// <summary>Task88（ユーザー要望「宿敵への陸上経路は可能な限り道路上を」）: 目的地側の
        /// スナップ半径だけを独立に指定できる版。徘徊する脅威（ゴジラ等）は道路から
        /// snapRadius(200)以上離れていることが多く、従来はスナップ失敗→経路null→全行程が
        /// 直線移動（オフロード）になっていた。destSnapRadiusにfloat.MaxValueを渡せば
        /// 「目的地に最も近い道路ノードまでは必ず道路で行き、残りだけ直線」になる
        /// （経路が尽きた後の直線フォールバックはMovementStepの既存挙動）。
        /// 出発側のスナップは従来どおりsnapRadiusのまま（ユニット自身が道路から遠い場合は
        /// 直線移動が正しい）。</summary>
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

            return null; // 到達不能
        }

        /// <summary>
        /// (seed, nodeA, nodeB) から決定的に [0,1) の係数を導く。a/bの小さい方・大きい方で正規化するため、
        /// 辺をどちら向きに辿っても同じ係数になる（順序無依存）。System.Randomは使わず純粋な整数演算のみ。
        /// </summary>
        private static float EdgeJitterFactor(uint seed, ushort nodeA, ushort nodeB)
        {
            uint lo = nodeA < nodeB ? nodeA : nodeB;
            uint hi = nodeA < nodeB ? nodeB : nodeA;
            uint h = Mix(seed ^ lo);
            h = Mix(h ^ hi);
            // 上位24bitを使い [0, 1) に正規化する。
            return (h >> 8) / (float)(1u << 24);
        }

        /// <summary>xorshift風の整数アバランチミックス（32bit）。単純な決定的ハッシュで、
        /// 同じ入力からは常に同じ出力を返す。System.Random等の非決定的/シード付き乱数は使わない。</summary>
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
