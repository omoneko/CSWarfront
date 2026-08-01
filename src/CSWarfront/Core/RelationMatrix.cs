namespace CSWarfront.Core
{
    /// <summary>対称な勢力関係表。自分自身は常に Allied。
    /// Task95: 行列の外側のId（Faction.InvaderFactionId等、factionCount以上）が絡む関係は
    /// 常にHostileへハードコードされる（Setも無視）。外部襲来のInvader勢力を「どう操作しても
    /// 確定で敵対」にするための仕様で、5x5固定のセーブ形式（WarStateSerializer）も変えずに済む。</summary>
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
            if (a >= _count || b >= _count) return a == b ? Relation.Allied : Relation.Hostile; // Invader等は常時敵対
            return _rel[a, b];
        }

        public void Set(int a, int b, Relation r)
        {
            if (a == b) return;      // 自己関係は不変
            if (a >= _count || b >= _count) return; // Invader等の行列外Idは常時Hostile固定（変更不可）
            _rel[a, b] = r;
            _rel[b, a] = r;          // 対称
        }
    }
}
