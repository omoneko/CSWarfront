using System.Collections.Generic;
namespace CSWarfront.Core
{
    public struct CompletedUnit
    {
        public ushort BaseId;
        public byte FactionId;
        public string TypeKey;
        public WorldPos SpawnPos;
    }

    /// <summary>The production tick (pure logic). Returns finished builds as CompletedUnit (spawning
    /// is the Game layer's job).</summary>
    public static class ProductionStep
    {
        /// <summary>Task78: a fix for one cause of "naval units never move to enemy strongholds and
        /// stay holed up at home". A navy base's (BaseType.Navy) building position is not necessarily
        /// on water (it is built on the shore), so using it directly as a ship's SpawnPos means
        /// MovementStep.AdvanceSea judges the landing point out-of-water from the very first step —
        /// the unit is stuck forever from the moment of birth (the direct cause of the stranded-on-
        /// shore bug). To avoid it, the nearest water point is searched concentrically around the base
        /// (radius growing by NavySpawnSearchStep, scanning NavySpawnSearchRays evenly-spaced
        /// directions per radius); if found, that becomes SpawnPos. If already on water, or no water
        /// is found within NavySpawnSearchMaxRadius, the base position is returned unchanged as
        /// before (best-effort, deterministic, no RNG).</summary>
        private const float NavySpawnSearchStep = 20f;
        private const float NavySpawnSearchMaxRadius = 200f;
        private const int NavySpawnSearchRays = 16;

        public static List<CompletedUnit> Advance(WarState state, float dt)
        {
            var completed = new List<CompletedUnit>();
            for (int i = 0; i < state.Bases.Count; i++)
            {
                var b = state.Bases[i];
                if (b.OwnerFactionId == null || b.Queue.Count == 0) continue;
                var order = b.Queue[0];
                if (order.BuildTime <= 0f) order.Progress = 1f;
                else order.Progress += dt / order.BuildTime;
                if (order.Progress >= 1f)
                {
                    completed.Add(new CompletedUnit
                    {
                        BaseId = b.BaseId,
                        FactionId = b.OwnerFactionId.Value,
                        TypeKey = order.TypeKey,
                        SpawnPos = ResolveSpawnPos(b, state.Water)
                    });
                    b.Queue.RemoveAt(0);
                }
            }
            return completed;
        }

        private static WorldPos ResolveSpawnPos(MilitaryBase b, IWaterSampler water)
        {
            if (b.Type != BaseType.Navy || water == null || water.IsWater(b.Position.X, b.Position.Z))
                return b.Position;

            for (float r = NavySpawnSearchStep; r <= NavySpawnSearchMaxRadius; r += NavySpawnSearchStep)
            {
                for (int a = 0; a < NavySpawnSearchRays; a++)
                {
                    double rad = a * (360.0 / NavySpawnSearchRays) * System.Math.PI / 180.0;
                    float x = b.Position.X + r * (float)System.Math.Cos(rad);
                    float z = b.Position.Z + r * (float)System.Math.Sin(rad);
                    if (water.IsWater(x, z)) return new WorldPos(x, b.Position.Y, z);
                }
            }
            return b.Position; // no water found within the search radius: best-effort base position as before.
        }
    }
}
