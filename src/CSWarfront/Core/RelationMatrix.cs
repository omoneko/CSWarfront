namespace CSWarfront.Core
{
    /// <summary>The symmetric faction-relation table. Self is always Allied.
    /// Task95: any relation involving an id beyond the matrix (Faction.InvaderFactionId etc., ids at
    /// or above factionCount) is hardcoded to Hostile (Set is ignored too). The spec making the
    /// outside-incursion Invader faction "definitively hostile no matter what", without changing the
    /// fixed 5x5 save format (WarStateSerializer) either.</summary>
    public class RelationMatrix
    {
        private readonly Relation[,] _rel;
        private readonly int _count;

        public RelationMatrix(int factionCount)
        {
            _count = factionCount;
            _rel = new Relation[factionCount, factionCount];
            for (int i = 0; i < factionCount; i++)
                for (int j = 0; j < factionCount; j++)
                    _rel[i, j] = (i == j) ? Relation.Allied : Relation.Neutral;
        }

        public Relation Get(int a, int b)
        {
            if (a >= _count || b >= _count) return a == b ? Relation.Allied : Relation.Hostile; // Invader etc. stay permanently hostile
            return _rel[a, b];
        }

        public void Set(int a, int b, Relation r)
        {
            if (a == b) return;      // the self relation is immutable
            if (a >= _count || b >= _count) return; // out-of-matrix ids (Invader etc.) stay locked to Hostile
            _rel[a, b] = r;
            _rel[b, a] = r;          // symmetric
        }
    }
}
