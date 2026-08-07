using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
    /// <summary>
    /// Tests for TerrainRaycast (Task77: fix for right-click point targeting).
    /// CS1 terrain has no Unity physics collider, so the intersection of the camera ray
    /// and the terrain height (IHeightSampler) is computed purely mathematically.
    /// </summary>
    public sealed class TerrainRaycastTests
    {
        /// <summary>Flat terrain that always returns a constant height.</summary>
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

        /// <summary>Stepped terrain that rises at x&gt;=threshold (used to verify hits land in front of the hill).</summary>
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
            // From origin (0,100,0) with dir=(1,-1,0)/sqrt(2) -> intersects the y=0 plane at x=100
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
            // Nearly horizontal ray: a gently descending ray from y=150 hits the wall of the step (height 200) at x=100.
            // A single plane intersection would land far away, but ray marching stops at the hill in front.
            WorldPos hit;
            bool ok = TerrainRaycast.TryFind(
                new WorldPos(0f, 150f, 0f), 0.999f, -0.045f, 0f,
                new StepSampler(), 10000f, out hit);

            Assert.True(ok);
            // Hits just after the ray height (just under 150) drops below the terrain (200) past the step (x=100)
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
            // Terrain is 100 m straight below, but with maxDistance=50 it cannot be reached
            WorldPos hit;
            bool ok = TerrainRaycast.TryFind(
                new WorldPos(0f, 100f, 0f), 0f, -1f, 0f,
                new FlatSampler(0f), 50f, out hit);

            Assert.False(ok);
        }

        [Fact]
        public void OriginBelowTerrain_MissesInsteadOfSnappingBackward()
        {
            // Start point is already below the terrain (abnormal case such as an underground camera): fail instead of hitting backwards
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
