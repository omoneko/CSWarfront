namespace CSWarfront.Core
{
    /// <summary>
    /// The per-faction relation table toward external threats (KAIJU/Alien, Task59). Where
    /// RelationMatrix holds symmetric faction-to-faction relations, this one is asymmetric: the threat
    /// side has no "faction", so no mirror-side concept exists (only the one-way relation from the
    /// factionId's point of view).
    ///
    /// Every entry defaults to Hostile. This is a design requirement: for players who don't have the
    /// Godzilla/Alien MODs installed, or haven't touched the Options screen yet, it preserves the
    /// pre-Task58 behavior of "always unconditionally hostile to every faction" exactly (and when
    /// WarStateSerializer reads a v4-or-earlier save, this default provides the same backward
    /// compatibility).
    /// </summary>
    public class ThreatRelations
    {
        /// <summary>The total number of ThreatKind enum values. When adding a new kind to ThreatKind,
        /// bump this too (Get/Set's bounds and the serialized block length follow it).</summary>
        public const int ThreatKindCount = 2; // Kaiju, Alien

        private readonly Relation[,] _rel;
        private readonly int _factionCount;

        public ThreatRelations(int factionCount)
        {
            _factionCount = factionCount;
            _rel = new Relation[factionCount, ThreatKindCount];
            for (int f = 0; f < factionCount; f++)
                for (int k = 0; k < ThreatKindCount; k++)
                    _rel[f, k] = Relation.Hostile;
        }

        public Relation Get(byte factionId, ThreatKind kind)
        {
            // Task95: ids beyond the table (Faction.InvaderFactionId etc.) are permanently Hostile
            // (same treatment as RelationMatrix. Invader forces fight external threats too, matching
            // the "a faction that is definitively hostile" spec).
            if (factionId >= _factionCount) return Relation.Hostile;
            return _rel[factionId, (int)kind];
        }

        public void Set(byte factionId, ThreatKind kind, Relation r)
        {
            if (factionId >= _factionCount) return; // out-of-table ids (Invader etc.) stay locked to Hostile
            _rel[factionId, (int)kind] = r;
        }

        /// <summary>The faction count WarStateSerializer uses when writing the v5 block (the value
        /// passed to the constructor).</summary>
        public int FactionCount { get { return _factionCount; } }
    }
}
