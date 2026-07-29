namespace CSWarfront.Core
{
    /// <summary>
    /// 脅威（ゴジラ/エイリアン）の近接オーラダメージ（Task65、ユーザー要望「付近にいるだけで削られる」）。
    /// MilitaryManager.OnSimTickでThreatCombatStepの直後に実行される。
    ///
    /// ThreatCombatStepとの違い（意図的な非対称設計）:
    ///  - ThreatCombatStepは「ユニット→脅威」方向の攻撃で、WarState.ThreatRelationsがHostile/Nemesis
    ///    の勢力のユニットしかダメージを与えない（Options画面で個別に設定できる）。
    ///  - このstepは逆方向「脅威→ユニット」で、ThreatRelationsを一切参照しない。暴れ回る巨大な脅威は
    ///    勢力の立場（同盟/中立/敵対）を判別せず、近くにいる全ユニットを等しく傷つける
    ///    （Neutral/Alliedに設定した勢力のユニットが脅威の真横に立っても無傷、というのは不自然なため）。
    ///  - 装甲(UnitType.Armor)を一切参照しない、定額(dt比例)のダメージ。ThreatArmorはユニット→脅威
    ///    方向にのみ意味を持つ値であり、こちら向きの防御要因ではない（単純さ優先）。
    ///
    /// 死亡判定・KillEvent発行はCombatStep.Advanceの第2パス（死亡判定ループ）と全く同じ形をここでも
    /// 独立して行う。CombatStep/BaseCombatStep/ThreatCombatStepは既にそれぞれ自分のAdvance内で
    /// 「自分が削った分の死亡確定」を完結させているため、このstepが同tick内で二重にKillEventを積むことは
    /// ない（他stepで既にDeadへ遷移済みのユニットはState!=Deadの条件で自然にスキップされる）。
    /// </summary>
    public static class ThreatAuraStep
    {
        /// <summary>ゴジラ(Kaiju)のオーラダメージ（ゲーム内1時間あたり、装甲無視）。
        /// T1戦車(HP140、LandUnitRoster)は 140/90 ≈ 1.6時間、T5戦車(HP336、TierScaling.Hp(140,5)=140*2.4)
        /// は 336/90 ≈ 3.7時間でオーラのみで撃破される。ゴジラ攻略に推奨される約50両のT5戦車編成が
        /// ThreatCombatStepの交戦（数時間〜十数時間規模）と並行してこのオーラにさらされ続けるため、
        /// 数時間おきに部隊が入れ替わるほどの継続的な消耗＝「高コストな攻略」になるよう意図した値
        /// （task-65レポートの計算例参照）。</summary>
        public const float GodzillaAuraDamage = 90f;

        /// <summary>エイリアン(Alien)のオーラダメージ（ゲーム内1時間あたり、装甲無視）。ゴジラより
        /// 一回り小規模な脅威という位置づけに合わせ、GodzillaAuraDamageよりやや低い値にした。</summary>
        public const float AlienAuraDamage = 50f;

        /// <summary>脅威の当たり半径(ExternalThreat.Radius)に加える、オーラが届く追加距離。
        /// ThreatCombatStep.Advanceが「射程+Radius」を実効射程とするのと対になる考え方で、
        /// 巨体の脅威の「近く」を単純な定数マージンで表現する。</summary>
        public const float AuraMargin = 60f;

        public static void Advance(WarState state, float dt)
        {
            for (int t = 0; t < state.Threats.Count; t++)
            {
                var threat = state.Threats[t];
                if (threat.IsDefeated) continue; // 撃破済みの脅威はもうオーラを発しない

                float dmgPerHour = DamagePerHourFor(threat.Kind);
                if (dmgPerHour <= 0f) continue;
                float effectiveRadius = threat.Radius + AuraMargin;

                for (int i = 0; i < state.Units.Count; i++)
                {
                    var u = state.Units[i];
                    if (!u.IsAlive) continue;
                    if (u.Position.HorizontalDistanceTo(threat.Position) > effectiveRadius) continue;

                    // 装甲無視・勢力/ThreatRelations無視の定額ダメージ（クラスコメント参照）。
                    u.CurrentHP -= dmgPerHour * dt;
                }
            }

            // 死亡判定（CombatStep.Advanceの死亡判定パスと同じパターン）。他stepで既にDeadへ
            // 遷移済みのユニットはState!=Deadの条件で自然にスキップされ、二重にKillEventを積まない。
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.State != UnitState.Dead && u.CurrentHP <= 0f)
                {
                    u.State = UnitState.Dead;
                    var uType = state.Types.Get(u.TypeKey);
                    var category = uType != null ? uType.Category : default(UnitCategory);
                    state.AddKill(new KillEvent(u.Position, u.FactionId, category));
                }
            }
        }

        private static float DamagePerHourFor(ThreatKind kind)
        {
            switch (kind)
            {
                case ThreatKind.Kaiju: return GodzillaAuraDamage;
                case ThreatKind.Alien: return AlienAuraDamage;
                default: return 0f;
            }
        }
    }
}
