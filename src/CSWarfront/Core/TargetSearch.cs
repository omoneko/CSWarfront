using System.Collections.Generic;
namespace CSWarfront.Core
{
    public static class TargetSearch
    {
        /// <summary>射程内・敵対（Hostile/Nemesis）・生存のうち水平距離最小の敵を返す。無ければ null。
        /// Task59: 宿敵(Nemesis)関係にある相手が射程内に1体でもいれば、通常のHostileより優先し、
        /// 宿敵の中で最近接のものを返す。宿敵が1体もいなければ、従来通り最近接の通常Hostileを返す
        /// （宿敵が存在しない場合は挙動が完全に従来のまま＝既存テストへの後方互換）。</summary>
        public static UnitInstance FindNearestHostile(UnitInstance self,
            IEnumerable<UnitInstance> all, RelationMatrix rel, float range)
        {
            UnitInstance bestHostile = null;
            float bestHostileDist = float.MaxValue;
            UnitInstance bestNemesis = null;
            float bestNemesisDist = float.MaxValue;

            foreach (var u in all)
            {
                if (u.InstanceId == self.InstanceId || !u.IsAlive) continue;
                Relation r = rel.Get(self.FactionId, u.FactionId);
                if (!r.IsHostile()) continue;
                float d = self.Position.HorizontalDistanceTo(u.Position);
                if (d > range) continue;

                if (r == Relation.Nemesis)
                {
                    if (d < bestNemesisDist) { bestNemesisDist = d; bestNemesis = u; }
                }
                else
                {
                    if (d < bestHostileDist) { bestHostileDist = d; bestHostile = u; }
                }
            }
            return bestNemesis != null ? bestNemesis : bestHostile;
        }
    }
}
