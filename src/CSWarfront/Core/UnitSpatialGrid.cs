using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// Task97（実機フィードバック「戦闘が進むと重くなる」）: 交戦判定の空間グリッド。
    ///
    /// 従来のTargetSearch.FindNearestHostileは全ユニット総当たり（呼び出し元がユニットごとに
    /// 呼ぶためO(N²)）で、実測690体で毎tick約48万回の距離計算が主要なボトルネックだった。
    /// このグリッドは毎tickの先頭でユニットをCellSizeの正方セルへ振り分け（O(N)）、探索を
    /// 「射程円と重なるセル内の候補だけ」に絞る（近傍のみ・ほぼO(N)）。
    ///
    /// 結果は総当たり版と完全に同一（決定的シミュレーションの維持）:
    ///  - セルは候補の絞り込みにしか使わず、射程・敵対関係・領域・生存の判定は探索時に行う
    ///    （同tick内で先に撃破されたユニットが以後の探索から即座に消える挙動も総当たり版と同じ）。
    ///  - 同距離のタイはstate.Unitsのインデックスが小さい方を採用（総当たり版の「リスト先頭優先」
    ///    と厳密に一致させるため、インデックスを明示的に比較する）。
    ///
    /// 使い方: 各step（CombatStep/KamikazeStep）がAdvanceの先頭でBuild()を呼び、
    /// TargetSearch.FindNearestHostileのグリッド版オーバーロードへ渡す。ユニットの位置は
    /// MovementStep（別step）でしか変わらないため、step内でグリッドが古くなることはない。
    /// WarStateのランタイム専用メンバ（非永続、simスレッド専用）。
    /// </summary>
    public class UnitSpatialGrid
    {
        /// <summary>セル1辺（m）。最長射程の兵科でも射程円が数セルに収まる程度に大きく、
        /// かつ1セルに数十体以上が密集しない程度に小さい値。</summary>
        public const float CellSize = 256f;

        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private List<UnitInstance> _units;

        /// <summary>ユニット一覧をセルへ振り分け直す（毎tick・各stepの先頭で呼ぶ）。
        /// 生存ユニットのみ登録する（死亡済みは候補になり得ないため）。リストは再利用して
        /// tickごとのアロケーションを避ける。</summary>
        public void Build(List<UnitInstance> units)
        {
            _units = units;
            foreach (List<int> cell in _cells.Values) cell.Clear();

            for (int i = 0; i < units.Count; i++)
            {
                UnitInstance u = units[i];
                if (!u.IsAlive) continue;
                long key = KeyFor(u.Position.X, u.Position.Z);
                List<int> cell;
                if (!_cells.TryGetValue(key, out cell))
                {
                    cell = new List<int>();
                    _cells[key] = cell;
                }
                cell.Add(i);
            }
        }

        /// <summary>総当たり版TargetSearch.FindNearestHostileと同一の結果を返すグリッド探索。
        /// 詳細はクラスコメント参照。</summary>
        public UnitInstance FindNearestHostile(UnitInstance self, RelationMatrix rel, float range,
            DomainMask attackerCanTarget, UnitTypeRegistry types)
        {
            if (_units == null) return null;

            UnitInstance bestHostile = null;
            float bestHostileDist = float.MaxValue;
            int bestHostileIdx = int.MaxValue;
            UnitInstance bestNemesis = null;
            float bestNemesisDist = float.MaxValue;
            int bestNemesisIdx = int.MaxValue;

            // Task101: 対ヘリ規則の判定に攻撃側の兵科が要る（TargetSearchの総当たり版と同じ）。
            UnitCategory? selfCategory = null;
            if (types != null)
            {
                UnitType selfType = types.Get(self.TypeKey);
                if (selfType != null) selfCategory = selfType.Category;
            }

            int cxMin = CellOf(self.Position.X - range), cxMax = CellOf(self.Position.X + range);
            int czMin = CellOf(self.Position.Z - range), czMax = CellOf(self.Position.Z + range);
            for (int cx = cxMin; cx <= cxMax; cx++)
            {
                for (int cz = czMin; cz <= czMax; cz++)
                {
                    List<int> cell;
                    if (!_cells.TryGetValue(Key(cx, cz), out cell)) continue;

                    for (int c = 0; c < cell.Count; c++)
                    {
                        int idx = cell[c];
                        UnitInstance u = _units[idx];
                        if (u.InstanceId == self.InstanceId || !u.IsAlive) continue;
                        if (u.IsCarried) continue; // Task101: 搭乗中は攻撃対象にならない
                        Relation r = rel.Get(self.FactionId, u.FactionId);
                        if (!r.IsHostile()) continue;
                        float d = self.Position.HorizontalDistanceTo(u.Position);
                        if (d > range) continue;

                        if (types != null)
                        {
                            UnitType targetType = types.Get(u.TypeKey);
                            if (targetType != null)
                            {
                                // Task101: ヘリ標的の専用規則（総当たり版TargetSearchと同一）。
                                if (TargetingRules.IsHelicopter(targetType.Category))
                                {
                                    if (!selfCategory.HasValue || !TargetingRules.CanTargetHelicopter(selfCategory.Value))
                                        continue;
                                }
                                else if (!DomainMaskUtil.Contains(attackerCanTarget, targetType.Domain))
                                {
                                    continue;
                                }
                            }
                        }

                        if (r == Relation.Nemesis)
                        {
                            if (d < bestNemesisDist || (d == bestNemesisDist && idx < bestNemesisIdx))
                            { bestNemesisDist = d; bestNemesis = u; bestNemesisIdx = idx; }
                        }
                        else
                        {
                            if (d < bestHostileDist || (d == bestHostileDist && idx < bestHostileIdx))
                            { bestHostileDist = d; bestHostile = u; bestHostileIdx = idx; }
                        }
                    }
                }
            }
            return bestNemesis != null ? bestNemesis : bestHostile;
        }

        private static int CellOf(float v)
        {
            return (int)System.Math.Floor(v / CellSize);
        }

        private static long Key(int cx, int cz)
        {
            return ((long)cx << 32) ^ (uint)cz;
        }

        private static long KeyFor(float x, float z)
        {
            return Key(CellOf(x), CellOf(z));
        }
    }
}
