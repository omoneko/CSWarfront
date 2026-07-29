namespace CSWarfront.Core
{
    /// <summary>
    /// 勢力ごとの外部脅威（KAIJU/Alien、Task59）との関係表。RelationMatrixが勢力同士の対称な関係を
    /// 持つのに対し、こちらは非対称: 脅威側は「勢力」を持たないため鏡側の概念が存在しない
    /// （factionId視点での一方向の関係のみ）。
    ///
    /// 既定値は全エントリHostile。これは「ゴジラ/エイリアンMODが導入されていない、または
    /// Options画面でまだ設定を変えていない」プレイヤーにとって、Task58時点までの「全勢力に対して
    /// 常に無条件敵対」という挙動をそのまま維持するための設計上の要請（WarStateSerializerがv4以前の
    /// セーブを読んだ場合も、このデフォルトにより同じ後方互換が成立する）。
    /// </summary>
    public class ThreatRelations
    {
        /// <summary>ThreatKindの列挙値の総数。ThreatKindへ新しい種別を追記した場合はここも合わせて
        /// 増やすこと（Get/Setの範囲・シリアライズのブロック長がこれに従うため）。</summary>
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
            return _rel[factionId, (int)kind];
        }

        public void Set(byte factionId, ThreatKind kind, Relation r)
        {
            _rel[factionId, (int)kind] = r;
        }

        /// <summary>WarStateSerializerがv5ブロックを書き出す際に使う勢力数（コンストラクタに渡した値）。</summary>
        public int FactionCount { get { return _factionCount; } }
    }
}
