namespace CSWarfront.Core
{
    /// <summary>交戦tick（純ロジック）。射程内の敵を探し、毎tick DamagePerHit を適用する。</summary>
    public static class CombatStep
    {
        public static void Advance(WarState state, float dt)
        {
            // 1) ターゲット選定と発火（ダメージは後段で一括適用しないシンプル版：都度適用）
            for (int i = 0; i < state.Units.Count; i++)
            {
                var self = state.Units[i];
                if (!self.IsAlive) continue;
                var type = state.Types.Get(self.TypeKey);
                if (type == null) continue;

                // ターゲット選定は依然として単純な「射程内最近接の敵」（TargetSearch）。
                // 有利な相性（CombatMatchup）を優先してターゲットを選ぶ賢いAIは将来の拡張課題とする。
                // Task61: type.CanTargetDomainsで領域フィルタをかける（AntiAir以外の陸上兵科は航空ユニットを
                // 一切狙わない、艦艇は航空ユニットを狙わない、航空ユニットは全領域を狙える、等）。
                var target = TargetSearch.FindNearestHostile(self, state.Units, state.Relations, type.Range,
                    type.CanTargetDomains, state.Types);
                if (target == null)
                {
                    if (self.State == UnitState.Engaging) self.State = UnitState.Idle;
                    self.TargetId = null;
                    continue;
                }
                self.State = UnitState.Engaging;
                self.TargetId = target.InstanceId;
                var targetType = state.Types.Get(target.TypeKey);
                float targetArmor = targetType != null ? targetType.Armor : 0f;
                float matchup = targetType != null
                    ? CombatMatchup.Multiplier(type.Category, targetType.Category)
                    : 1.0f;
                // Attack はゲーム内1時間あたりのダメージ量。実際に適用するダメージは経過ゲーム内時間(dt)と
                // 兵科相性倍率(CombatMatchup、Task29)、命中率(CombatSynergy.AccuracyFor、Task38)に比例する。
                // 命中率は乱数抽選ではなく期待値ダメージ倍率として適用する（決定的シミュレーションを保つため）。
                float accuracy = CombatSynergy.AccuracyFor(state, self, type);
                float dmg = CombatMath.DamagePerHit(type.Attack, targetArmor) * dt * matchup * accuracy;
                // 撃破報酬（Task35）: このダメージでHPが初めて0以下へ落ちた瞬間だけ、攻撃側(self)の勢力へ
                // Research.KillReward を与える。wasAlive フラグで「このダメージがとどめだったか」を判定し、
                // 同tick内で同じ的へ複数回ダメージが入っても二重付与しない。
                bool wasAlive = target.CurrentHP > 0f;
                target.CurrentHP -= dmg;
                if (wasAlive && target.CurrentHP <= 0f) AwardKillReward(state, self.FactionId, targetType);

                // Task54: 被弾地点を「戦闘域」として報告する（民間交通の迂回用）。近傍への報告は
                // CombatZoneTracker側でマージされるため、ここでは間引き不要。
                state.CombatZones.ReportCombat(target.Position);

                // 発砲エフェクトの間引き（Task42、Task58でFireEffects.EmitThrottledへ集約）: 実際に
                // ダメージを与えたこのtickでのみFireCooldownをdt分だけ減算する。0以下になった瞬間だけ
                // ShotEventを1つ積み、FireIntervalHoursへリセットする（乱数不使用・決定的）。
                FireEffects.EmitThrottled(state, self, type, target.Position, target.InstanceId, dt);

                // Task61: 自爆ドローン（UnitType.IsOneShot）は、実際にダメージを与えた瞬間に自壊する。
                // CurrentHPを0にするだけで、死亡判定・KillEvent発行は下の第2パス（既存の死亡判定ループ）が
                // そのまま担う（自爆用の特別な分岐を増やさず、既存の「死亡はCurrentHP<=0から導出する」
                // という単一の真実源を維持する）。
                if (type.IsOneShot) self.CurrentHP = 0f;
            }
            // 2) 死亡判定
            // Task51: ここが「ユニットが実際にDeadへ遷移する、まさにその瞬間」なので、撃破音の
            // トリガーとしてKillEventを1件だけ積む（State != Deadを条件にしているため二重には積まない）。
            // Task53: KillEventに被撃破ユニットのUnitCategoryを積む（歩兵系の撃破音オミット判定用）。
            // UnitTypeが見つからない防御的ケースではdefault(UnitCategory)（Tank=0、撃破音は鳴る側）にする。
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

        /// <summary>撃破報酬（Task35）を killerFactionId の勢力へ加算する。victimType/勢力が見つからなければ
        /// 何もしない（防御的：整合性が崩れているケースで例外にしないため）。</summary>
        private static void AwardKillReward(WarState state, byte killerFactionId, UnitType victimType)
        {
            if (victimType == null) return;
            Faction killer = state.FindFaction(killerFactionId);
            if (killer == null) return;
            killer.AddResearchPoints(Research.KillReward(victimType));
        }
    }
}
