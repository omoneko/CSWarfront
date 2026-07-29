namespace CSWarfront.Core
{
    /// <summary>
    /// 発砲エフェクト(ShotEvent)の間引き（UnitInstance.FireCooldownアキュムレータ）を一箇所に
    /// 集約する共有ヘルパー（Task58）。CombatStep（対ユニット）・BaseCombatStep（対基地）・
    /// ThreatCombatStep（対外部脅威＝ゴジラ/エイリアン）が全く同じ契約を共有する:
    /// 「ダメージを実適用したこのtickでのみFireCooldownをdt分減算し、0以下になった瞬間だけ
    /// ShotEventを1つ積んでUnitType.FireIntervalHoursへリセットする」（乱数不使用・決定的）。
    /// ダメージ計算そのものには一切関与しない（ShotEvent.cs冒頭のコメント参照）。
    /// </summary>
    public static class FireEffects
    {
        /// <summary>attacker が targetPos（targetId）へダメージを与えたこのtickに呼ぶ。
        /// targetId は対象がユニットならそのInstanceId、基地・外部脅威など論理ユニットを持たない
        /// 対象なら0（ShotEvent.TargetIdの既存の契約と同じ）。</summary>
        public static void EmitThrottled(WarState state, UnitInstance attacker, UnitType attackerType,
            WorldPos targetPos, uint targetId, float dt)
        {
            attacker.FireCooldown -= dt;
            if (attacker.FireCooldown <= 0f)
            {
                state.AddShot(new ShotEvent(attacker.Position, targetPos, attackerType.ShotKind, attacker.FactionId,
                    attacker.InstanceId, targetId, attackerType.Category));
                attacker.FireCooldown = attackerType.FireIntervalHours;
            }
        }
    }
}
