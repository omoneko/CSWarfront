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

                var target = TargetSearch.FindNearestHostile(self, state.Units, state.Relations, type.Range);
                if (target == null)
                {
                    if (self.State == UnitState.Engaging) self.State = UnitState.Idle;
                    self.TargetId = null;
                    continue;
                }
                self.State = UnitState.Engaging;
                self.TargetId = target.InstanceId;
                // Attack はゲーム内1時間あたりのダメージ量。実際に適用するダメージは経過ゲーム内時間(dt)に比例する。
                float dmg = CombatMath.DamagePerHit(type.Attack, TypeArmorOf(state, target)) * dt;
                target.CurrentHP -= dmg;
            }
            // 2) 死亡判定
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.State != UnitState.Dead && u.CurrentHP <= 0f) u.State = UnitState.Dead;
            }
        }

        private static float TypeArmorOf(WarState state, UnitInstance u)
        {
            var t = state.Types.Get(u.TypeKey);
            return t != null ? t.Armor : 0f;
        }
    }
}
