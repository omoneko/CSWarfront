namespace CSWarfront.Core
{
    /// <summary>
    /// 外部MOD（ゴジラ災害/エイリアン侵略）が生成する脅威(ExternalThreat)との交戦（純ロジック、Task58）。
    ///
    /// 設計上の注意:
    ///  - Task59: 脅威との関係は WarState.ThreatRelations（勢力×ThreatKind、既定Hostile）で
    ///    勢力ごとに設定できる。ダメージを与えるのはIsHostile()（Hostile/Nemesis）な組み合わせのみで、
    ///    Neutral/Alliedに設定した勢力のユニットはその脅威にダメージを与えない
    ///    （Task58時点の「全勢力に対して常に無条件敵対」はThreatRelationsの既定値として残る）。
    ///  - このstepは通常のCombatStep/BaseCombatStepに「加えて」毎tick実行される。ターゲット選定の
    ///    奪い合いはしない＝射程内に脅威と通常の敵の両方がいるユニットは、両方を同時に攻撃する
    ///    （単純さ・予測可能性を優先した意図的な設計。ExternalThreatCombatStepTests参照）。
    ///  - 脅威側からユニットへのダメージはここでは一切適用しない。ゴジラ/エイリアン側のMODが
    ///    既に独自に都市を破壊しているため、CSWarfront側でユニットへ二重に被害を与える必要が無い
    ///    （意図的な非対称設計）。
    /// </summary>
    public static class ThreatCombatStep
    {
        /// <summary>脅威に対する装甲値。ゴジラ/エイリアンは規格外にタフなので、小火器は
        /// CombatMath.DamagePerHitの最低保証(1)しか通らずほぼ無力化される一方、戦車・砲兵クラスの
        /// 攻撃力（Attack &gt; ThreatArmor）があって初めて有効打になる（ThreatCombatStepTests参照）。</summary>
        public const float ThreatArmor = 20f;

        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                var self = state.Units[i];
                if (!self.IsAlive) continue;
                var type = state.Types.Get(self.TypeKey);
                if (type == null) continue;

                for (int j = 0; j < state.Threats.Count; j++)
                {
                    var threat = state.Threats[j];
                    if (threat.IsDefeated) continue; // 撃破済みは以後ダメージを受けない（Game層の除去待ち）

                    // Task59: このユニットの勢力にとってこの脅威が敵対（Hostile/Nemesis）でなければ
                    // ダメージを与えない（Options画面「勢力の関係」でNeutral/Alliedに設定できる）。
                    if (!state.ThreatRelations.Get(self.FactionId, threat.Kind).IsHostile()) continue;

                    // 大型の脅威なので、通常の射程(unitType.Range)に脅威の当たり半径を加えた距離を
                    // 実効射程として扱う（CombatStep/BaseCombatStepは相手のサイズを考慮しないが、
                    // ゴジラ/エイリアンのような巨体は「射程の縁ぎりぎり」でも十分狙える）。
                    float effectiveRange = type.Range + threat.Radius;
                    if (self.Position.HorizontalDistanceTo(threat.Position) > effectiveRange) continue;

                    // Attack はゲーム内1時間あたりのダメージ量。命中率(CombatSynergy.AccuracyFor)は
                    // 期待値ダメージ倍率として適用する（CombatStepと同じ、乱数抽選ではない）。
                    // CombatMatchup（兵科相性）は脅威には適用しない：脅威はUnitCategoryを持たない
                    // 独立した存在なので、装甲(ThreatArmor)のみが唯一の防御要因になる。
                    float accuracy = CombatSynergy.AccuracyFor(state, self, type);
                    float dmg = CombatMath.DamagePerHit(type.Attack, ThreatArmor) * dt * accuracy;
                    threat.CurrentHP -= dmg;
                    if (threat.CurrentHP < 0f) threat.CurrentHP = 0f;

                    // 発砲エフェクト（Task42/58）。対象は脅威なので基地攻めと同じくTargetId=0
                    // （脅威には論理ユニットIDが無い。Game層はTargetId==0を「ユニットでない対象」
                    // として扱う既存の契約をそのまま利用する）。
                    FireEffects.EmitThrottled(state, self, type, threat.Position, 0, dt);
                }
            }
        }
    }
}
