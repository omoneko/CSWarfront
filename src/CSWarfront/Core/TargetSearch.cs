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
            return FindNearestHostile(self, all, rel, range, DomainMask.All, null);
        }

        /// <summary>Task61: 領域(Domain)フィルタ付きの版。attackerCanTargetにtargetのDomain（typesで解決）が
        /// 含まれない候補は、他の条件（射程・敵対関係）を満たしていても除外する。これがAntiAirを
        /// 唯一の対空適性を持つ陸上兵科にし、通常の陸上兵科が航空ユニットを一切狙わなくする実体。
        /// types が null、または候補のTypeKeyが解決できない場合は領域フィルタを適用しない
        /// （＝従来の4引数版と同じ挙動、既存呼び出し元・テストへの後方互換フォールバック）。</summary>
        public static UnitInstance FindNearestHostile(UnitInstance self,
            IEnumerable<UnitInstance> all, RelationMatrix rel, float range,
            DomainMask attackerCanTarget, UnitTypeRegistry types)
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

                if (types != null)
                {
                    UnitType targetType = types.Get(u.TypeKey);
                    if (targetType != null && !DomainMaskUtil.Contains(attackerCanTarget, targetType.Domain))
                        continue;
                }

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

        /// <summary>Task97: 空間グリッド版。結果は上の総当たり版と完全に同一で、探索候補を
        /// 「射程円と重なるセル内」に絞ることでO(N²)→ほぼO(N)にする（UnitSpatialGridの
        /// クラスコメント参照）。呼び出し元は毎tickの先頭でgrid.Build(state.Units)を済ませておくこと。</summary>
        public static UnitInstance FindNearestHostile(UnitInstance self, UnitSpatialGrid grid,
            RelationMatrix rel, float range, DomainMask attackerCanTarget, UnitTypeRegistry types)
        {
            return grid.FindNearestHostile(self, rel, range, attackerCanTarget, types);
        }
    }
}
