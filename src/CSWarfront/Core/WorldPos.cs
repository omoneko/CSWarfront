using System;
namespace CSWarfront.Core
{
    /// <summary>A UnityEngine-free coordinate. The Game layer converts to/from UnityEngine.Vector3.</summary>
    public struct WorldPos
    {
        public readonly float X, Y, Z;
        public WorldPos(float x, float y, float z) { X = x; Y = y; Z = z; }

        public float DistanceTo(WorldPos o)
        {
            float dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public float HorizontalDistanceTo(WorldPos o)
        {
            float dx = X - o.X, dz = Z - o.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
