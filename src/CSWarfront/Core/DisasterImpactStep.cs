namespace CSWarfront.Core
{
    /// <summary>
    /// MissileDisaster MOD（プレイヤーが撃つ災害ミサイル）の着弾によるユニットへの一撃ダメージ
    /// （Task94、Workshopコメント対応「ミサイル災害でユニットが死なない」）。
    ///
    /// MissileDisasterは着弾を疎結合ビーコン（ImpactBeacon: 座標＋破壊半径＋延焼半径＋核フラグ）で
    /// 公開し、Game層のDisasterImpactBridgeが新着1件につきこのApplyImpactを1回呼ぶ。
    ///
    /// ダメージ方針はThreatBeamStep（ゴジラ光線）と同じ:
    ///  - 勢力・ThreatRelationsを一切参照しない（災害ミサイルは誰の味方でもない）。
    ///  - 装甲無視の定額一撃。死亡判定・KillEvent発行も同じパターン。
    ///  - 2段階の同心円: 破壊半径内（建物倒壊圏）は大ダメージ、延焼半径内（その外側）は中ダメージ。
    ///    核は破壊圏内が即死級（生存を許さない）、延焼圏（熱線）も重傷級。
    /// </summary>
    public static class DisasterImpactStep
    {
        /// <summary>通常弾頭: 破壊半径内のダメージ（Tier5級のHP336を一撃で沈める規模）。</summary>
        public const float ConventionalDestructionDamage = 500f;

        /// <summary>通常弾頭: 延焼半径内（破壊圏の外側）のダメージ。</summary>
        public const float ConventionalBurnDamage = 120f;

        /// <summary>核弾頭: 破壊半径（5psi圏）内のダメージ。いかなるユニットでも確実に即死する値。</summary>
        public const float NuclearDestructionDamage = 1000000f;

        /// <summary>核弾頭: 熱線半径内（破壊圏の外側）のダメージ。</summary>
        public const float NuclearBurnDamage = 500f;

        /// <summary>着弾1件ぶんのダメージを適用する。戻り値はダメージを受けたユニット数（ログ用）。</summary>
        public static int ApplyImpact(WarState state, float x, float z, float destructionRadius, float burnRadius,
            bool isNuclear)
        {
            float destructionDamage = isNuclear ? NuclearDestructionDamage : ConventionalDestructionDamage;
            float burnDamage = isNuclear ? NuclearBurnDamage : ConventionalBurnDamage;
            float outerRadius = burnRadius > destructionRadius ? burnRadius : destructionRadius;

            int hit = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;

                float dx = u.Position.X - x, dz = u.Position.Z - z;
                float distSq = dx * dx + dz * dz;
                if (distSq > outerRadius * outerRadius) continue;

                u.CurrentHP -= distSq <= destructionRadius * destructionRadius ? destructionDamage : burnDamage;
                hit++;
            }

            // 死亡判定（ThreatBeamStep/ThreatAuraStepと同じパターン。他stepで既にDeadなら二重に積まない）。
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (u.State != UnitState.Dead && u.CurrentHP <= 0f)
                {
                    u.State = UnitState.Dead;
                    UnitType uType = state.Types.Get(u.TypeKey);
                    UnitCategory category = uType != null ? uType.Category : default(UnitCategory);
                    state.AddKill(new KillEvent(u.Position, u.FactionId, category));
                }
            }

            return hit;
        }
    }
}
