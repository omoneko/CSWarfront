using System;

namespace CSWarfront.Core
{
    /// <summary>
    /// カメラレイと地形（IHeightSampler）の交点を求める純粋計算（Task77）。
    ///
    /// 背景: CS1の地形はUnityの物理コライダーを持たないため、開けた地面に向けた
    /// Physics.Raycastは常に「何もヒットしない」で失敗する（実機output_log.txtで
    /// 「rally click rejected - raycast hit nothing」が多数記録されたのが根本原因の証拠。
    /// ユニット/建物はMOD自前のコライダーがあるためヒットする）。右クリックの地点指定
    /// （集結地点・ミサイル目標）はPhysics.Raycastが外れた後、この地形交差へ
    /// フォールバックする。
    ///
    /// アルゴリズム: 粗いレイマーチ（CoarseStep間隔）で「レイ高さが地形高さを下回る」
    /// 最初の区間を見つけ、その区間を二分法で細分して交点を確定する。単純な
    /// 平面交差1回だと丘の斜面や段差を突き抜けて遥か遠方にヒットしてしまうため、
    /// マーチが必要（浅い角度のカメラで特に顕著）。浮動小数のみの決定的計算。
    /// </summary>
    public static class TerrainRaycast
    {
        /// <summary>粗いマーチの刻み幅（メートル）。CSのdetail heightmapは約4m/セルなので
        /// 16mで丘を跨ぎ越すことは実用上ない（跨いでも二分法の対象区間が1つ後ろにずれるだけ）。</summary>
        private const float CoarseStep = 16f;

        private const int BisectIterations = 24; // 16m / 2^24 ≒ 1μm、十分に収束する

        /// <summary>レイ原点(origin)から方向(dirX,dirY,dirZ)（正規化不要）へ最大maxDistanceまで
        /// 地形との交点を探す。見つかればtrueを返しhitに交点を格納する。
        /// 原点が既に地形より下（地下カメラ等の異常系）・上向きレイ・sampler失敗・
        /// maxDistance内に交点なし、の場合はfalse。</summary>
        public static bool TryFind(
            WorldPos origin, float dirX, float dirY, float dirZ,
            IHeightSampler sampler, float maxDistance,
            out WorldPos hit)
        {
            hit = default(WorldPos);
            if (sampler == null || maxDistance <= 0f) return false;

            float len = (float)Math.Sqrt(dirX * dirX + dirY * dirY + dirZ * dirZ);
            if (len < 1e-6f) return false;
            float nx = dirX / len, ny = dirY / len, nz = dirZ / len;

            // 原点が地形より上にあることを確認（下なら後退ヒットになるため不成立とする）
            float h0;
            if (!sampler.TrySampleHeight(origin.X, origin.Z, out h0)) return false;
            if (origin.Y < h0) return false;

            // 粗いマーチ: 「地形より上」から「地形以下」へ転じる最初の区間 [tPrev, t] を探す
            float tPrev = 0f;
            bool found = false;
            float tLow = 0f, tHigh = 0f;
            for (float t = CoarseStep; t <= maxDistance; t += CoarseStep)
            {
                float x = origin.X + nx * t;
                float y = origin.Y + ny * t;
                float z = origin.Z + nz * t;
                float h;
                if (!sampler.TrySampleHeight(x, z, out h)) return false;
                if (y <= h)
                {
                    tLow = tPrev;
                    tHigh = t;
                    found = true;
                    break;
                }
                tPrev = t;
            }
            if (!found) return false;

            // 二分法で区間を細分（区間内の地形は単調でなくてもよい: 常に
            // 「lowは地形より上/highは地形以下」の不変条件を保って縮める）
            for (int i = 0; i < BisectIterations; i++)
            {
                float tMid = (tLow + tHigh) * 0.5f;
                float x = origin.X + nx * tMid;
                float y = origin.Y + ny * tMid;
                float z = origin.Z + nz * tMid;
                float h;
                if (!sampler.TrySampleHeight(x, z, out h)) return false;
                if (y <= h) tHigh = tMid; else tLow = tMid;
            }

            float tf = tHigh;
            float hx = origin.X + nx * tf;
            float hz = origin.Z + nz * tf;
            float hy;
            if (!sampler.TrySampleHeight(hx, hz, out hy)) return false;
            hit = new WorldPos(hx, hy, hz);
            return true;
        }
    }
}
