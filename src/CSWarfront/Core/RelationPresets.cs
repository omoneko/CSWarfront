namespace CSWarfront.Core
{
    /// <summary>
    /// Default presets for faction relations. Extracts the "every pair Hostile" logic previously
    /// written inline in Game/MilitaryManager.EnsureInitialized, so Core (testable) and Game (the
    /// Options screen's "reset all to hostile" button) share the same implementation.
    /// No UnityEngine dependency; deterministic (depends only on its inputs, holds no internal
    /// state).
    /// </summary>
    public static class RelationPresets
    {
        /// <summary>
        /// Sets every distinct faction pair in 0..count-1 to Hostile. count must not exceed m's real
        /// size (callers are expected to pass at most the factionCount given to m's constructor).
        /// Does nothing when m is null.
        /// </summary>
        public static void ApplyAllHostile(RelationMatrix m, int count)
        {
            if (m == null) return;

            for (byte i = 0; i < count; i++)
                for (byte j = (byte)(i + 1); j < count; j++)
                    m.Set(i, j, Relation.Hostile);
        }
    }
}
