namespace CSWarfront.Core
{
    /// <summary>HP0の基地を最近接の敵対攻撃側へ移管し、HQ喪失勢力を脱落させる（純ロジック）。</summary>
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

                // HQ喪失判定
                var loser = state.FindFaction(oldOwner);
                if (loser != null && loser.HomeBaseId.HasValue && loser.HomeBaseId.Value == b.BaseId)
                    loser.Eliminated = true;
            }
        }
    }
}
