using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// 交戦中のユニットに、近くの遮蔽物（建物/Prop、WarState.Cover）を活かした立ち位置
    /// （UnitInstance.CoverDestination）を割り当てる（純ロジック、Task44）。
    /// MilitaryManager.OnSimTickではMovementStepより前に呼ぶこと（このtickで決めた立ち位置へ
    /// 同じtick内でMovementStepが動き出せるようにするため、RoadGraph→InvasionOrders→MovementStepと
    /// 同じ「先に意思決定、後で移動」の順序）。
    /// </summary>
    public static class CoverSeekStep
    {
        /// <summary>ユニットごとの遮蔽再評価間隔（ゲーム内時間）。毎tick探索しない（性能とジッタ抑制の
        /// 両方が目的）。UnitInstance.CoverReevaluateCooldownで管理する。</summary>
        public const float CoverReevaluateHours = 0.5f;

        /// <summary>カテゴリ別の遮蔽探索半径。0以下＝そのカテゴリは遮蔽移動をしない
        /// （Artilleryは後方から曲射するため、遮蔽物の陰に隠れる必要がない）。</summary>
        private static readonly Dictionary<UnitCategory, float> SearchRadiusByCategory = new Dictionary<UnitCategory, float>
        {
            { UnitCategory.Infantry, 60f },
            { UnitCategory.MechInfantry, 60f },
            { UnitCategory.DroneInfantry, 60f },
            { UnitCategory.Apc, 45f },
            { UnitCategory.Tank, 45f },
            { UnitCategory.AntiAir, 45f },
            { UnitCategory.Artillery, 0f },
        };

        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;

                if (u.State != UnitState.Engaging || !u.TargetId.HasValue)
                {
                    // 交戦が終わった＝次に交戦し始めたら即座に再評価してほしいので、
                    // クールダウンも一緒にリセットする（残クールダウンを持ち越さない）。
                    ClearCover(u);
                    u.CoverReevaluateCooldown = 0f;
                    continue;
                }

                UnitInstance target = state.FindUnit(u.TargetId.Value);
                if (target == null || !target.IsAlive)
                {
                    ClearCover(u);
                    u.CoverReevaluateCooldown = 0f;
                    continue;
                }

                // 再評価クールダウン: まだ間隔が空いていなければ既存のCoverDestinationを維持する
                // （設定済みでも未設定でも、そのまま変えない＝毎tick探索しないための間引き）。
                u.CoverReevaluateCooldown -= dt;
                if (u.CoverReevaluateCooldown > 0f) continue;
                u.CoverReevaluateCooldown = CoverReevaluateHours;

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null)
                {
                    ClearCover(u);
                    continue;
                }

                float searchRadius = SearchRadiusByCategory.TryGetValue(type.Category, out float r) ? r : 0f;
                if (searchRadius <= 0f || state.Cover == null)
                {
                    ClearCover(u);
                    continue;
                }

                if (state.Cover.TryFindBestCover(u.Position, target.Position, searchRadius, u.InstanceId, out WorldPos coverPos))
                    u.CoverDestination = coverPos;
                else
                    ClearCover(u);
            }
        }

        private static void ClearCover(UnitInstance u)
        {
            u.CoverDestination = null;
        }
    }
}
