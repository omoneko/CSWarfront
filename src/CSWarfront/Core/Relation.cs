namespace CSWarfront.Core
{
    // Task59: Nemesis を末尾に追記した。WarStateSerializerは関係をint(enumの数値)としてそのまま
    // 書き出す(v4以前のフォーマット変更は不要)ため、既存の値(Hostile=0/Neutral=1/Allied=2)の意味は
    // 変わらない。Nemesis=3は新規追記分。
    public enum Relation { Hostile, Neutral, Allied, Nemesis }

    /// <summary>
    /// Task59:「宿敵」は通常のHostileに「他の敵対勢力より優先して狙われる」という優先度を足しただけの
    /// 特殊な敵対関係であり、ダメージ適用・基地占領・被占領・AI進軍先選定など「敵対かどうか」を
    /// 判定する全ての箇所ではHostileと全く同じに扱う必要がある。素の `== Relation.Hostile` 比較を
    /// Core中に残すとNemesisがそこだけ非敵対扱いになってしまうため、判定は必ずこのヘルパー経由で行う
    /// （TargetSearch/BaseCombatStep/Occupation/AiTargeting/ThreatCombatStep/InvasionOrders参照）。
    /// </summary>
    public static class RelationExtensions
    {
        public static bool IsHostile(this Relation r)
        {
            return r == Relation.Hostile || r == Relation.Nemesis;
        }
    }
}
