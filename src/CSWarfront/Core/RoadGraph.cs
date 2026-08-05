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

        /// <summary>Task110: 連結成分の遅延キャッシュ（グラフは構築後は読み取り専用のため、
        /// AddNode/AddEdgeで捨てるだけで十分）。到達可能性の判定を何度も行うために使う。</summary>
        private Dictionary<ushort, int> _components;

        public int NodeCount => _nodes.Count;

        /// <summary>ノードid→連結成分番号（初回アクセスで計算してキャッシュ）。</summary>
        public Dictionary<ushort, int> Components
        {
            get
            {
                if (_components == null) _components = ComputeComponentIds();
                return _components;
            }
        }

        /// <summary>ノードを追加する。同じidが既にあれば無視（idempotent）。</summary>
        public void AddNode(ushort id, WorldPos position)
        {
            if (_nodes.ContainsKey(id)) return;
            _nodes[id] = new Node { Id = id, Position = position };
            _components = null;
        }

        /// <summary>無向辺を追加する。未知のid・自己ループは無視。重複辺は追加しない。</summary>
        public void AddEdge(ushort a, ushort b)
        {
            if (a == b) return;
            if (!_nodes.TryGetValue(a, out var na)) return;
            if (!_nodes.TryGetValue(b, out var nb)) return;
            if (!na.Neighbors.Contains(b)) na.Neighbors.Add(b);
            if (!nb.Neighbors.Contains(a)) nb.Neighbors.Add(a);
            _components = null;
        }

        /// <summary>Task110: 指定ノードと同じ連結成分に属するノードだけを候補に、最近傍を探す。
        /// 「行き先と行き来できるノードから経路を始める」ために使う——単純な最近傍だと、すぐ隣にある
        /// 別網（引き込み線など）のノードを掴んでしまい、そこからは目的地へ絶対に到達できず経路探索が
        /// 必ず失敗する（実機で列車が駅に停まったまま動かなくなった原因）。</summary>
        public bool TryFindNearestNodeInSameComponent(WorldPos p, float maxDistance, ushort referenceNode,
            out ushort nodeId)
        {
            nodeId = 0;
            int component;
            if (!Components.TryGetValue(referenceNode, out component)) return false;
            return TryFindNearestNode(p, maxDistance, Components, component, out nodeId);
        }

        /// <summary>Task110: ノードidを直接指定した経路探索（スナップを伴わない）。呼び出し側が
        /// 到達可能なノードを選び終えている場合に、スナップのやり直しで別ノードを掴まないようにする。
        /// 同一ノードなら空リスト、到達不能ならnull。</summary>
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

        /// <summary>Task108: 位置がほぼ重なっているのに別idになっているノードどうしを辺で繋ぐ
        /// （「融合」＝互いに行き来できる状態にする。idは消さず、隣接関係だけを足す）。
        /// CSでは、駅の構内線と本線の接合部などで幾何的には繋がっているのにNetNodeが別々になって
        /// いることがあり、そのままだと網が別々の連結成分に割れて経路探索が失敗する
        /// （実機で「駅は全部稼働なのに路線が0本」になった不具合の対策）。
        /// radiusは水平距離、heightToleranceは高低差の上限——立体交差（桁下）を誤って繋がない
        /// ためのガード。追加した辺の本数を返す。ノードid昇順で走査するため結果は決定的。</summary>
        public int WeldCoincidentNodes(float radius, float heightTolerance)
        {
            if (radius <= 0f) return 0;

            // 総当たりを避けるため、radius幅のグリッドへバケット分けしてから近傍9マスだけ比較する。
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
                            if (otherId <= a.Id) continue; // 各ペアを1回だけ見る（決定的）
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

        private static long BucketKey(WorldPos p, float cellSize)
        {
            int cx = (int)System.Math.Floor(p.X / cellSize);
            int cz = (int)System.Math.Floor(p.Z / cellSize);
            return ((long)cx << 32) ^ (uint)cz;
        }

        /// <summary>Task108: 連結成分ごとに0,1,2…の番号を振ったノードid→成分番号の対応を返す
        /// （番号はノードid昇順の探索順で決まる＝決定的）。「2つの地点が同じ網に載っているか」を
        /// 経路探索を走らせずに判定・診断するために使う（軍用列車の路線が1本も成立しない不具合の
        /// 原因切り分け＝レール網が分断されているのか、単に近すぎるのかを区別するため）。</summary>
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

        /// <summary>水平距離でmaxDistance以内の最近傍ノードを探す。</summary>
        public bool TryFindNearestNode(WorldPos p, float maxDistance, out ushort nodeId)
        {
            return TryFindNearestNode(p, maxDistance, null, 0, out nodeId);
        }

        /// <summary>Task108: 連結成分を限定できる版。componentsにComputeComponentIdsの結果を、
        /// requiredComponentにその成分番号を渡すと、その成分に属するノードだけを候補にする
        /// （components==nullなら全ノードが候補＝上のオーバーロードと同じ）。
        /// 「駅が自分の引き込み線（本線から分断された小さな成分）にスナップしてしまい、
        /// 本線上の他の駅へ経路が引けない」ケースを避け、本線側のノードを掴ませるために使う。</summary>
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

        /// <summary>Task108: ノードidの座標を返す（駅のレール進入点をキャッシュするために使う）。</summary>
        public bool TryGetNodePosition(ushort nodeId, out WorldPos position)
        {
            Node node;
            if (_nodes.TryGetValue(nodeId, out node)) { position = node.Position; return true; }
            position = default(WorldPos);
            return false;
        }

        /// <summary>Task110: 指定地点からmaxDistance以内にある連結成分ごとの最寄りノードと距離を返す
        /// （成分番号→(ノードid, 水平距離)）。「どの線路網が駅たちに共有されているか」の投票
        /// （CargoStationRules.RefreshConnectivity）に使う。同距離はノードid昇順で決定的。</summary>
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

        /// <summary>Task108: 最も多くのノードを含む連結成分（＝実質的な「本線網」）の番号を返す。
        /// ノードが無ければfalse。同数の場合は成分番号の小さい方（決定的）。</summary>
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
