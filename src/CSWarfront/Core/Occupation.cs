namespace CSWarfront.Core
{
    /// <summary>HP0の基地を最近接の敵対攻撃側へ移管する（純ロジック）。Task46: 勢力の脱落判定
    /// （Eliminated）はここでは直接いじらない。FactionStatus.Refreshが「所有基地の有無」から
    /// 毎tick導出し直す唯一の場所になった（一度脱落した勢力でも基地を取り戻せば復活できるように
    /// するため、Eliminated=trueを立てっぱなしにする経路をここから排除した）。</summary>
    public static class Occupation
    {
        public static void ResolveCaptures(WarState state)
        {
            for (int j = 0; j < state.Bases.Count; j++)
            {
                var b = state.Bases[j];
                if (b.CurrentHP > 0f || b.OwnerFactionId == null) continue;
                byte oldOwner = b.OwnerFactionId.Value;

                // 圏内・敵対の攻撃側から最近接を新所有者に
                UnitInstance nearest = null; float best = float.MaxValue;
                for (int i = 0; i < state.Units.Count; i++)
                {
                    var u = state.Units[i];
                    if (!u.IsAlive) continue;
                    if (state.Relations.Get(oldOwner, u.FactionId) != Relation.Hostile) continue;
                    float d = b.Position.HorizontalDistanceTo(u.Position);
                    if (d > b.InfluenceRadius) continue;
                    if (d < best) { best = d; nearest = u; }
                }
                if (nearest == null) continue; // 攻撃側不在なら保留（次tickへ）

                b.OwnerFactionId = nearest.FactionId;
                b.CurrentHP = b.MaxHP;         // 再稼働（キューはそのまま＝奪取）
            }
        }
    }
}
