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
                var target = TargetSearch.FindNearestHostile(self, state.Units, state.Relations, type.Range);
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
                // 兵科相性倍率(CombatMatchup、Task29)に比例する。
                float dmg = CombatMath.DamagePerHit(type.Attack, targetArmor) * dt * matchup;
                target.CurrentHP -= dmg;
            }
            // 2) 死亡判定
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.State != UnitState.Dead && u.CurrentHP <= 0f) u.State = UnitState.Dead;
            }
        }
    }
}
