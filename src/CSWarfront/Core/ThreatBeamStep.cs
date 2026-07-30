using System;

namespace CSWarfront.Core
{
    /// <summary>
    /// 脅威のビーム攻撃（ゴジラ光線/トライポッドレーザー）によるユニットへの一撃ダメージ（Task83、
    /// ユーザー要望「ゴジラ光線とトライポッドのレーザーにユニットに対するダメージ判定をつけて」）。
    ///
    /// 他MOD（GodzillaDisaster/AlienInvasion）はビーム発射を「一回性のイベント」として発射記録API
    /// （CurrentId+Snapshot、Game/ExternalThreatBridge参照）に公開し、CSWarfront側が新着の発射記録
    /// 1件につきこのApplyStrikeを1回呼ぶ。継続DPSではなく一撃ダメージなのは、両MODのビームが
    /// 実際に一瞬の発射（ゴジラ: Rampaging中に1回の紫熱線、トライポッド: 一定間隔の単発レーザー）
    /// だから。
    ///
    /// ダメージ方針はThreatAuraStepと同じ非対称設計:
    ///  - ThreatRelationsを一切参照しない（ビームの経路上にいれば同盟/中立設定の勢力でも等しく当たる）。
    ///  - 装甲(UnitType.Armor)を参照しない定額ダメージ。
    ///  - 死亡判定・KillEvent発行はThreatAuraStep/CombatStepの死亡判定パスと同じパターン。
    ///
    /// 判定は2D（X/Z平面）の「線分から幅W以内」（端点は丸いキャップ）。ゴジラ光線は口元の高さを
    /// 水平に飛ぶがGodzillaRampage側の建物破壊も経路の地表に適用しているため、地上ユニットへの
    /// 判定も2Dで一貫させる。
    /// </summary>
    public static class ThreatBeamStep
    {
        /// <summary>ゴジラ光線（紫熱線）の有効半幅。ゴジラの当たり半径(45)と同程度の太い熱線。</summary>
        public const float KaijuBeamWidth = 40f;

        /// <summary>ゴジラ光線のダメージ（装甲無視・一撃）。5kt核相当の熱線であり、経路上のユニットは
        /// Tier5(HP300超)でも確実に消滅する「回避すべき即死攻撃」として設計（弾道ミサイルの
        /// ImpactDamageThreat=2000と同格）。</summary>
        public const float KaijuBeamDamage = 2000f;

        /// <summary>トライポッドレーザーの有効半幅。単発の細いレーザーなのでゴジラより狭い。</summary>
        public const float AlienBeamWidth = 15f;

        /// <summary>トライポッドレーザーのダメージ（装甲無視・一撃）。Tier1-3級(HP100-250前後)は
        /// 一撃で撃破、Tier5級は大破して生き残る「痛いが即死ではない」規模（Alien=小型脅威の
        /// 位置づけ、ExternalThreatBridgeのHP比と整合）。</summary>
        public const float AlienBeamDamage = 400f;

        /// <summary>ビーム線分(x1,z1)-(x2,z2)から幅以内の全ユニットへ一撃ダメージを与える。
        /// 戻り値はダメージを与えたユニット数（Game層のログ用）。</summary>
        public static int ApplyStrike(WarState state, ThreatKind kind, float x1, float z1, float x2, float z2)
        {
            float width, damage;
            switch (kind)
            {
                case ThreatKind.Kaiju: width = KaijuBeamWidth; damage = KaijuBeamDamage; break;
                case ThreatKind.Alien: width = AlienBeamWidth; damage = AlienBeamDamage; break;
                default: return 0;
            }

            int hitCount = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (DistanceToSegment(u.Position.X, u.Position.Z, x1, z1, x2, z2) > width) continue;

                u.CurrentHP -= damage;
                hitCount++;
            }

            // 死亡判定（ThreatAuraStep/CombatStepの死亡判定パスと同じパターン）。他stepで既にDeadへ
            // 遷移済みのユニットはState!=Deadの条件で自然にスキップされ、二重にKillEventを積まない。
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

            return hitCount;
        }

        /// <summary>2D（X/Z平面）の点(px,pz)から線分(x1,z1)-(x2,z2)までの距離。
        /// 始点=終点の退化ケースは点までの距離。</summary>
        private static float DistanceToSegment(float px, float pz, float x1, float z1, float x2, float z2)
        {
            float dx = x2 - x1, dz = z2 - z1;
            float lenSq = dx * dx + dz * dz;
            float t;
            if (lenSq < 1e-6f)
            {
                t = 0f; // 退化: 始点までの距離
            }
            else
            {
                t = ((px - x1) * dx + (pz - z1) * dz) / lenSq;
                if (t < 0f) t = 0f;
                else if (t > 1f) t = 1f;
            }
            float cx = x1 + dx * t, cz = z1 + dz * t;
            float ex = px - cx, ez = pz - cz;
            return (float)Math.Sqrt(ex * ex + ez * ez);
        }
    }
}
