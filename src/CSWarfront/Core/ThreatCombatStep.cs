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
        /// 攻撃力（Attack &gt; ThreatArmor）があって初めて有効打になる（ThreatCombatStepTests参照）。
        ///
        /// Task64再調整（旧20→45）: 旧値20はTier1火力を基準にしていたが、Tier5編成では脆すぎた。
        /// 実際の算出（TierScaling: Attack x2.6・Accuracy基礎値の1.24倍@Tier5、CombatMath.DamagePerHit=
        /// max(1,attack-armor)）:
        ///   Tier5戦車 (Attack 40x2.6=104, Accuracy 0.70x1.24=0.868):
        ///     旧armor20 → DamagePerHit(104,20)=84 × 0.868 ≈ 72.9/h。
        ///     50両編成なら 50*72.9 ≈ 3645/h、旧Godzilla(20000)を約5.5時間で撃破 — Tier5編成に対して
        ///     一瞬で溶けてしまい「弱すぎる」というユーザー指摘の直接の原因。
        ///     新armor45 → DamagePerHit(104,45)=59 × 0.868 ≈ 51.2/h（約30%減）。
        ///   Tier5歩兵 (Attack 20x2.6=52, Accuracy 0.75x1.24=0.93):
        ///     新armor45 → DamagePerHit(52,45)=max(1,7)=7 × 0.93 ≈ 6.5/h。
        ///     戦車(≈51.2/h)の約1/8に留まり、「小火器はほぼ無力」という設計意図はTier5でも健在
        ///     （旧armor20のTier1同士の比較ほど極端(旧19倍)ではないが、装甲45は歩兵の生アタック52
        ///     ぎりぎりを削り取る値に意図的にチューニングしてあり、歩兵単独の脅威狩りを引き続き
        ///     非現実的にする）。
        /// 新しいHP値（Godzilla 65000/Alien 26000）・ミサイル対脅威ダメージ(2000)の算出根拠も
        /// あわせてtask-64レポート参照。</summary>
        public const float ThreatArmor = 45f;

        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                var self = state.Units[i];
                if (!self.IsAlive) continue;
                var type = state.Types.Get(self.TypeKey);
                if (type == null) continue;

                // Task79: 自爆ドローンは脅威（ゴジラ/エイリアン）にも継続的なdtスケール射撃を行わない。
                // KamikazeStepが同じ実効射程(Range+threat.Radius)・同じThreatArmor/ThreatRelations判定を
                // 使って目標をロックし、体当たりで1回だけフルダメージを与える（「巨体には一撃が非常に
                // 効く」という設計、ThreatArmorが小火器をほぼ無効化する一方でSuicideDroneの高Attackは
                // 大きく通る）。
                if (type.Category.IsKamikaze()) continue;

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
