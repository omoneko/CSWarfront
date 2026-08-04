namespace CSWarfront.Core
{
    /// <summary>
    /// Task98（実機フィードバック）: 水際・行き止まり等でスタックして動けなくなったユニットの自動消滅。
    ///
    /// 「スタック」の定義: State==Moving（移動したいのに）で、基準位置（StuckAnchor）から
    /// 「そのユニットの速度なら進めるはずの距離のProgressFraction」未満しか動けない状態が
    /// DespawnAfterHours続いたユニット（Task98追補: 当初は固定20mだったが、歩兵は約1.0m/ゲーム時
    /// しか進まないため正常行軍のまま12hで12m＜20mとなり誤消滅した——閾値は速度比例が正しい。
    /// 上限MinProgressDistanceは高速ユニットの誤検知マージンとして残す）。
    /// Idle/Engaging/Deadは対象外（止まっているのが正常な状態のため。自拠点で待機している部隊や
    /// 交戦中に立ち止まっている部隊はStateがMovingでないので、そもそもタイマーが進まない）。
    ///
    /// 例外: 自勢力の基地からOwnBaseExemptRadius以内にいるユニットは消さない（ユーザー要望
    /// 「自軍の基地で停止している部隊は別」。基地周辺で何らかの理由により足踏みしていても資産は
    /// 保全する）。タイマーはリセットして再判定を先送りする。
    ///
    /// 消滅は無音・無爆発（KillEventを積まずにDead化するだけ）。撃破と紛らわしい演出を避け、
    /// 「いつの間にか整理されている」挙動にする。Dead化したユニットはMilitaryManagerSimTickの
    /// 毎tickの死亡ユニット掃除がリストから取り除く。
    /// </summary>
    public static class StuckCleanupStep
    {
        /// <summary>この時間（ゲーム内時間）動けないままだと消滅する。</summary>
        public const float DespawnAfterHours = 12f;

        /// <summary>前進判定閾値の速度比例係数。「本来の速度で進めるはずの距離の25%未満しか
        /// 進めていない」＝スタック。壁沿い迂回の斜め成分程度は前進とみなす余裕を持たせた値。</summary>
        public const float ProgressFraction = 0.25f;

        /// <summary>前進判定閾値の上限（水平m）。高速ユニット（戦車・艦艇・航空）で速度比例のまま
        /// だと数百mになり、壁際の往復でも到達してしまうため、従来の固定値でキャップする。</summary>
        public const float MinProgressDistance = 20f;

        /// <summary>自勢力基地からこの距離以内のユニットは消滅対象外。</summary>
        public const float OwnBaseExemptRadius = 200f;

        /// <summary>スタック判定を1tickぶん進める。戻り値は消滅させたユニット数（ログ用）。</summary>
        public static int Advance(WarState state, float dt)
        {
            int despawned = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (u.IsCarried) { u.StuckAnchor = null; u.StuckHours = 0f; continue; } // Task101: 搭乗中は対象外

                if (u.State != UnitState.Moving)
                {
                    u.StuckAnchor = null;
                    u.StuckHours = 0f;
                    continue;
                }

                if (!u.StuckAnchor.HasValue ||
                    u.Position.HorizontalDistanceTo(u.StuckAnchor.Value) >= ProgressThresholdFor(state, u))
                {
                    u.StuckAnchor = u.Position;
                    u.StuckHours = 0f;
                    continue;
                }

                u.StuckHours += dt;
                if (u.StuckHours < DespawnAfterHours) continue;

                if (IsNearOwnBase(state, u))
                {
                    u.StuckHours = 0f; // 自拠点付近は保全（クラスコメント参照）。再判定は最初からやり直す
                    continue;
                }

                // 無音・無爆発の消滅（KillEventは積まない。CombatStepの死亡判定パスは
                // State==Deadを見て二重処理しない）。
                u.State = UnitState.Dead;
                u.CurrentHP = 0f;
                despawned++;
            }
            return despawned;
        }

        /// <summary>このユニットの前進判定閾値: min(速度×DespawnAfterHours×ProgressFraction,
        /// MinProgressDistance)。歩兵（約1.0m/ゲーム時）なら約3m、戦車以上ならキャップの20m。
        /// 型が引けない防御的ケースは0（＝常に前進扱い、消滅させない側に倒す）。</summary>
        private static float ProgressThresholdFor(WarState state, UnitInstance u)
        {
            UnitType type = state.Types.Get(u.TypeKey);
            if (type == null) return 0f;
            float threshold = type.Speed * DespawnAfterHours * ProgressFraction;
            return threshold < MinProgressDistance ? threshold : MinProgressDistance;
        }

        private static bool IsNearOwnBase(WarState state, UnitInstance u)
        {
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != u.FactionId) continue;
                if (u.Position.HorizontalDistanceTo(mb.Position) <= OwnBaseExemptRadius) return true;
            }
            return false;
        }
    }
}
