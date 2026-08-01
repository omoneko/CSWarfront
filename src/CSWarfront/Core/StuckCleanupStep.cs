namespace CSWarfront.Core
{
    /// <summary>
    /// Task98（実機フィードバック）: 水際・行き止まり等でスタックして動けなくなったユニットの自動消滅。
    ///
    /// 「スタック」の定義: State==Moving（移動したいのに）で、基準位置（StuckAnchor）から
    /// MinProgressDistance未満しか動けない状態がDespawnAfterHours続いたユニット。
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
        /// <summary>この時間（ゲーム内時間）動けないままだと消滅する。最低速の兵科でも本来なら
        /// 数千mは進んでいる長さで、正常な渋滞・低速移動を誤検知しないマージンを取った値。</summary>
        public const float DespawnAfterHours = 12f;

        /// <summary>この距離（水平m）以上動けていれば「前進できている」とみなしタイマーを0へ戻す。
        /// 海上ユニットの壁沿い迂回の小刻みな往復（数m〜十数m）は前進とみなさない。</summary>
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

                if (u.State != UnitState.Moving)
                {
                    u.StuckAnchor = null;
                    u.StuckHours = 0f;
                    continue;
                }

                if (!u.StuckAnchor.HasValue ||
                    u.Position.HorizontalDistanceTo(u.StuckAnchor.Value) >= MinProgressDistance)
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
