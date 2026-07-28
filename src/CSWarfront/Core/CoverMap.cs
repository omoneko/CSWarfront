using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>1件の遮蔽物（建物/Prop）。Radiusはその遮蔽物のおおよその半径（footprint基準）。</summary>
    public struct CoverPoint
    {
        public readonly WorldPos Position;
        public readonly float Radius;

        public CoverPoint(WorldPos position, float radius)
        {
            Position = position;
            Radius = radius;
        }
    }

    /// <summary>
    /// Game層（CoverMapBuilder）から供給される遮蔽物（建物/Prop）の集合＋「遮蔽を求める」ロジック
    /// （Task44）。UnityEngine非依存・決定的。RoadGraphと同じ供給パターン：Game層がsimスレッドで
    /// CS建物/Propバッファを読み取り、この単純なPOCOへ詰め替えてWarState.Coverへ渡す。
    /// </summary>
    public class CoverMap
    {
        /// <summary>遮蔽物の縁から実際に立つ位置までの余白（メートル相当のマップ単位）。
        /// 0だと壁に張り付いてしまい見た目が不自然になるため、少し離す。</summary>
        public const float StandoffMargin = 4f;

        // スコアリングの重み（チューニング値）。低いスコアほど良い候補。
        //  - DistanceWeight: ユニットからの近さを優先する度合い。
        //  - ExposureWeight: 「脅威とユニットの間」に来ているかを優先する度合い
        //    （ExposureScoreの方がDistanceより荒れやすいレンジのため、やや強めに重み付けする）。
        //  - JitterMagnitude: 複数ユニットが同一の最良点へ密集するのを防ぐための小さな決定的な
        //    好み（同点/僅差の候補間でのみ順位を左右する程度に小さく保つ）。
        //  - OutOfSegmentPenalty: 「ユニット→脅威」の線分の外側（＝そもそも遮蔽として機能しない位置、
        //    例えばユニットの背後）にある候補を強く減点する係数。近さだけで「背後の遮蔽物」が
        //    「間にある遮蔽物」に勝ってしまわないよう、距離の重みより十分大きくしてある。
        private const float DistanceWeight = 1f;
        private const float ExposureWeight = 1.5f;
        private const float OutOfSegmentPenalty = 120f;
        private const float JitterMagnitude = 3f;

        private readonly List<CoverPoint> _points = new List<CoverPoint>();

        public int Count => _points.Count;

        public void Add(WorldPos position, float radius)
        {
            _points.Add(new CoverPoint(position, radius));
        }

        /// <summary>
        /// unitPosからsearchRadius以内の遮蔽物の中で、threatPosから最もよく身を隠せる位置を探す。
        /// 見つかった場合、戻り値のWorldPosはその遮蔽物の「脅威から見て奥側」の立ち位置
        /// （coverCentre + normalize(coverCentre - threatPos) * (radius + StandoffMargin)）。
        /// 見つからなければfalse（呼び出し側は既存の移動ロジックへフォールバックすること）。
        /// </summary>
        public bool TryFindBestCover(WorldPos unitPos, WorldPos threatPos, float searchRadius, uint seed, out WorldPos coverPos)
        {
            coverPos = default(WorldPos);
            if (searchRadius <= 0f || _points.Count == 0) return false;

            int bestIndex = -1;
            float bestScore = float.MaxValue;

            for (int i = 0; i < _points.Count; i++)
            {
                CoverPoint cp = _points[i];

                // 空間的なショートカット: 水平距離の全計算(sqrt)前に軸ごとのバウンディングボックスで弾く。
                float dx = unitPos.X - cp.Position.X;
                if (dx > searchRadius || dx < -searchRadius) continue;
                float dz = unitPos.Z - cp.Position.Z;
                if (dz > searchRadius || dz < -searchRadius) continue;

                float distToUnit = unitPos.HorizontalDistanceTo(cp.Position);
                if (distToUnit > searchRadius) continue;

                float exposureScore = ExposureScore(cp.Position, unitPos, threatPos);
                float jitter = JitterFactor(seed, i) * JitterMagnitude;
                float score = distToUnit * DistanceWeight + exposureScore * ExposureWeight + jitter;

                // 厳密な"<"のみで更新するため、完全な同点は先に見つかった（=より低いindexの）候補が
                // 自動的に勝つ。これが決定的なタイブレークになる。
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0) return false;

            CoverPoint best = _points[bestIndex];
            coverPos = StandingPosition(best, threatPos);
            return true;
        }

        /// <summary>ユニット→脅威の線分上にどれだけ乗っているか（=どれだけ間に割り込めているか）を
        /// スコア化する。0に近いほど良い。線分から外れた垂直距離＋線分の外側(t&lt;0 or t&gt;1)へ出た分の
        /// ペナルティの合計。ユニットと脅威が同じ位置にある退化ケースは全候補を同等（0）として扱う。</summary>
        private static float ExposureScore(WorldPos coverPos, WorldPos unitPos, WorldPos threatPos)
        {
            float ux = threatPos.X - unitPos.X;
            float uz = threatPos.Z - unitPos.Z;
            float lenSq = ux * ux + uz * uz;
            if (lenSq < 0.0001f) return 0f;

            float cx = coverPos.X - unitPos.X;
            float cz = coverPos.Z - unitPos.Z;
            float t = (cx * ux + cz * uz) / lenSq;

            float projX = unitPos.X + ux * t;
            float projZ = unitPos.Z + uz * t;
            float pdx = coverPos.X - projX;
            float pdz = coverPos.Z - projZ;
            float perpDist = (float)System.Math.Sqrt(pdx * pdx + pdz * pdz);

            float outOfSegment = 0f;
            if (t < 0f) outOfSegment = -t * OutOfSegmentPenalty;
            else if (t > 1f) outOfSegment = (t - 1f) * OutOfSegmentPenalty;

            return perpDist + outOfSegment;
        }

        /// <summary>脅威から見て遮蔽物の奥側にあたる、実際にユニットが立つ位置を求める。
        /// coverCentreとthreatPosがほぼ同一（退化ケース）の場合は決定的な既定方向(+Z)へ逃がす。</summary>
        private static WorldPos StandingPosition(CoverPoint cover, WorldPos threatPos)
        {
            float ax = cover.Position.X - threatPos.X;
            float az = cover.Position.Z - threatPos.Z;
            float len = (float)System.Math.Sqrt(ax * ax + az * az);

            float nx, nz;
            if (len < 0.0001f)
            {
                nx = 0f; nz = 1f;
            }
            else
            {
                nx = ax / len; nz = az / len;
            }

            float standoff = cover.Radius + StandoffMargin;
            return new WorldPos(
                cover.Position.X + nx * standoff,
                cover.Position.Y,
                cover.Position.Z + nz * standoff);
        }

        /// <summary>(seed, candidateIndex)から決定的に[0,1)の係数を導く。RoadGraph.EdgeJitterFactorと
        /// 同じ技法（純整数演算のアバランチミックス、System.Random不使用）。</summary>
        private static float JitterFactor(uint seed, int candidateIndex)
        {
            uint h = Mix(seed ^ (uint)candidateIndex);
            h = Mix(h ^ 0x9e3779b9U);
            return (h >> 8) / (float)(1u << 24);
        }

        private static uint Mix(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352dU;
            x ^= x >> 15;
            x *= 0x846ca68bU;
            x ^= x >> 16;
            return x;
        }
    }
}
