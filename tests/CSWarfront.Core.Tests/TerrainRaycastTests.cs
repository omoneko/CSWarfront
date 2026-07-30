using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
    /// <summary>
    /// TerrainRaycast（Task77: 右クリック地点指定の修正）のテスト。
    /// CS1の地形はUnity物理コライダーを持たないため、カメラレイと地形高さ
    /// （IHeightSampler）の交点を純粋計算で求める。
    /// </summary>
    public sealed class TerrainRaycastTests
    {
        /// <summary>常に一定高さを返す平坦地形。</summary>
        private sealed class FlatSampler : IHeightSampler
        {
            private readonly float _height;
            public FlatSampler(float height) { _height = height; }
            public bool TrySampleHeight(float x, float z, out float height)
            {
                height = _height;
                return true;
            }
        }

        /// <summary>x&gt;=閾値で高くなる段差地形（丘の手前でヒットすることの検証用）。</summary>
        private sealed class StepSampler : IHeightSampler
        {
            public bool TrySampleHeight(float x, float z, out float height)
            {
                height = x >= 100f ? 200f : 0f;
                return true;
            }
        }

        private sealed class FailingSampler : IHeightSampler
        {
            public bool TrySampleHeight(float x, float z, out float height)
            {
                height = 0f;
                return false;
            }
        }

        [Fact]
        public void StraightDown_HitsFlatTerrainAtOriginXz()
        {
            WorldPos hit;
            bool ok = TerrainRaycast.TryFind(
                new WorldPos(50f, 500f, -30f), 0f, -1f, 0f,
                new FlatSampler(270f), 10000f, out hit);

            Assert.True(ok);
            Assert.Equal(50f, hit.X, 1);
            Assert.Equal(270f, hit.Y, 0);
            Assert.Equal(-30f, hit.Z, 1);
        }

        [Fact]
        public void ObliqueRay_HitsFlatTerrainAtPlaneIntersection()
        {
            // 原点(0,100,0)から dir=(1,-1,0)/√2 → y=0平面とは x=100 で交わる
            float inv = 1f / (float)System.Math.Sqrt(2.0);
            WorldPos hit;
            bool ok = TerrainRaycast.TryFind(
                new WorldPos(0f, 100f, 0f), inv, -inv, 0f,
                new FlatSampler(0f), 10000f, out hit);

            Assert.True(ok);
            Assert.Equal(100f, hit.X, 0);
            Assert.Equal(0f, hit.Y, 0);
        }

        [Fact]
        public void ShallowRay_HitsRisingStepBeforeFarPlane()
        {
            // ほぼ水平のレイ: y=150から緩く下るレイはx=100の段差(高さ200)の壁に当たる。
            // 平面交差1回では遥か遠方に飛ぶが、レイマーチなら手前の丘で止まる。
            WorldPos hit;
            bool ok = TerrainRaycast.TryFind(
                new WorldPos(0f, 150f, 0f), 0.999f, -0.045f, 0f,
                new StepSampler(), 10000f, out hit);

            Assert.True(ok);
            // 段差(x=100)以降、レイ高さ(150弱)が地形(200)を下回った直後でヒットする
            Assert.InRange(hit.X, 99f, 120f);
        }

        [Fact]
        public void UpwardRay_Misses()
        {
            WorldPos hit;
            bool ok = TerrainRaycast.TryFind(
                new WorldPos(0f, 100f, 0f), 0f, 1f, 0f,
                new FlatSampler(0f), 10000f, out hit);

            Assert.False(ok);
        }

        [Fact]
        public void HorizontalRayAboveTerrain_Misses()
        {
            WorldPos hit;
            bool ok = TerrainRaycast.TryFind(
                new WorldPos(0f, 100f, 0f), 1f, 0f, 0f,
                new FlatSampler(0f), 10000f, out hit);

            Assert.False(ok);
        }

        [Fact]
        public void SamplerFailure_Misses()
        {
            WorldPos hit;
            bool ok = TerrainRaycast.TryFind(
                new WorldPos(0f, 100f, 0f), 0f, -1f, 0f,
                new FailingSampler(), 10000f, out hit);

            Assert.False(ok);
        }

        [Fact]
        public void TerrainBeyondMaxDistance_Misses()
        {
            // 真下100mに地形があるが、maxDistance=50なら届かない
            WorldPos hit;
            bool ok = TerrainRaycast.TryFind(
                new WorldPos(0f, 100f, 0f), 0f, -1f, 0f,
                new FlatSampler(0f), 50f, out hit);

            Assert.False(ok);
        }

        [Fact]
        public void OriginBelowTerrain_MissesInsteadOfSnappingBackward()
        {
            // 開始点が既に地形より下（地下カメラ等の異常系）: 後退ヒットさせず不成立にする
            WorldPos hit;
            bool ok = TerrainRaycast.TryFind(
                new WorldPos(0f, -10f, 0f), 0f, -1f, 0f,
                new FlatSampler(0f), 10000f, out hit);

            Assert.False(ok);
        }

        [Fact]
        public void ZeroDirection_Misses()
        {
            WorldPos hit;
            bool ok = TerrainRaycast.TryFind(
                new WorldPos(0f, 100f, 0f), 0f, 0f, 0f,
                new FlatSampler(0f), 10000f, out hit);

            Assert.False(ok);
        }

        [Fact]
        public void Deterministic_SameInputsSameResult()
        {
            WorldPos a, b;
            TerrainRaycast.TryFind(new WorldPos(10f, 300f, 20f), 0.5f, -0.7f, 0.3f, new StepSampler(), 10000f, out a);
            TerrainRaycast.TryFind(new WorldPos(10f, 300f, 20f), 0.5f, -0.7f, 0.3f, new StepSampler(), 10000f, out b);

            Assert.Equal(a.X, b.X);
            Assert.Equal(a.Y, b.Y);
            Assert.Equal(a.Z, b.Z);
        }
    }
}
