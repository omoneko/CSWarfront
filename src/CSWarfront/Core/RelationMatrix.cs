namespace CSWarfront.Core
{
    /// <summary>対称な勢力関係表。自分自身は常に Allied。</summary>
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

        public Relation Get(int a, int b) { return _rel[a, b]; }

        public void Set(int a, int b, Relation r)
        {
            if (a == b) return;      // 自己関係は不変
            _rel[a, b] = r;
            _rel[b, a] = r;          // 対称
        }
    }
}
