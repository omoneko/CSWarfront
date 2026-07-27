namespace CSWarfront.Core
{
    /// <summary>
    /// 拠点の自衛射撃（純ロジック、Task29）。基地は所有者があり、DefenseAttack&gt;0、かつ占領猶予中
    /// でない（CaptureGraceHours&lt;=0）ときのみ、DefenseRange内の最近接の敵対生存ユニットへ反撃する。
    /// 猶予中の基地はBaseCombatStepでダメージを受けないのと対称に、こちらから撃つこともしない
    /// （プレイヤーが両陣営を配置し終える前に一方的に殲滅されるのを防ぐ、という既存ルールと一貫させる）。
    ///
    /// 呼び出し順序: Game層は CombatStep → BaseCombatStep → BaseDefenseStep → Occupation の順で呼ぶ
    /// （MilitaryManager.OnSimTick）。死亡判定（死亡パス）はこのステップの最後で自前に行う
    /// （CombatStepの死亡パスは同ステップ内のダメージしか見ないため、拠点の反撃だけで0HPになった
    /// ユニットを、次のCombatStep呼び出しまで「HPは0以下だがまだDeadでない」状態のまま
    /// 残さないようにするため）。
    /// </summary>
    public static class BaseDefenseStep
    {
        public static void Advance(WarState state, float dt)
        {
            for (int j = 0; j < state.Bases.Count; j++)
            {
                var b = state.Bases[j];
                if (b.OwnerFactionId == null) continue;
                if (b.DefenseAttack <= 0f) continue;
                if (b.CaptureGraceHours > 0f) continue; // 猶予中は撃たない（撃たれもしないのと対称）

                var target = FindNearestHostileUnit(state, b);
                if (target == null) continue;

                var targetType = state.Types.Get(target.TypeKey);
                float targetArmor = targetType != null ? targetType.Armor : 0f;
                // DefenseAttack はゲーム内1時間あたりのダメージ量。実際に適用するダメージは
                // 経過ゲーム内時間(dt)に比例する（CombatStep/BaseCombatStepと同じ規約）。
                target.CurrentHP -= CombatMath.DamagePerHit(b.DefenseAttack, targetArmor) * dt;
            }

            // 死亡判定：拠点の反撃で0HP以下になったユニットをこのステップ内で確定させる。
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (u.State != UnitState.Dead && u.CurrentHP <= 0f) u.State = UnitState.Dead;
            }
        }

        /// <summary>DefenseRange内・基地の所有者に敵対・生存のうち水平距離最小のユニットを返す。
        /// 同距離の場合はInstanceIdが小さい方を優先し、結果を決定的にする。</summary>
        private static UnitInstance FindNearestHostileUnit(WarState state, MilitaryBase b)
        {
            UnitInstance best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (!u.IsAlive) continue;
                if (state.Relations.Get(b.OwnerFactionId.Value, u.FactionId) != Relation.Hostile) continue;
                float d = b.Position.HorizontalDistanceTo(u.Position);
                if (d > b.DefenseRange) continue;
                if (best == null || d < bestDist || (d == bestDist && u.InstanceId < best.InstanceId))
                {
                    bestDist = d;
                    best = u;
                }
            }
            return best;
        }
    }
}
