using System;

namespace CSWarfront.Core
{
    /// <summary>ユニットが敵対基地を射程内で攻撃する（純ロジック）。</summary>
    public static class BaseCombatStep
    {
        /// <summary>基地攻めにおける命中率の下限（Task38）。静止した建物は動くユニットより狙いやすいため、
        /// 素の命中率がこれより低い兵科（例: 命中率0.35のArtillery）でも、基地相手にはこの値まで
        /// 底上げする。これにより砲兵は「対ユニットでは当てにくいが、対基地では依然強力」という
        /// 攻城兵器としての立ち位置を保つ。</summary>
        public const float SiegeAccuracyFloor = 0.8f;

        public static void Advance(WarState state, float dt)
        {
            // 新設基地の占領猶予を先に消化する。猶予中の基地はこのtickのダメージループから完全に除外する
            // （プレイヤーが両陣営を配置し終える前に一方的に占領されるのを防ぐ）。
            for (int j = 0; j < state.Bases.Count; j++)
            {
                var b = state.Bases[j];
                if (b.CaptureGraceHours <= 0f) continue;
                b.CaptureGraceHours -= dt;
                if (b.CaptureGraceHours < 0f) b.CaptureGraceHours = 0f;
            }

            for (int i = 0; i < state.Units.Count; i++)
            {
                var u = state.Units[i];
                if (!u.IsAlive) continue;
                var type = state.Types.Get(u.TypeKey);
                if (type == null) continue;

                for (int j = 0; j < state.Bases.Count; j++)
                {
                    var b = state.Bases[j];
                    if (b.CaptureGraceHours > 0f) continue; // 猶予中は無敵
                    if (b.OwnerFactionId == null) continue;
                    if (b.OwnerFactionId.Value == u.FactionId) continue;
                    if (state.Relations.Get(u.FactionId, b.OwnerFactionId.Value) != Relation.Hostile) continue;
                    if (u.Position.HorizontalDistanceTo(b.Position) > type.Range) continue;
                    // Attack はゲーム内1時間あたりのダメージ量。実際に適用するダメージは経過ゲーム内時間(dt)と
                    // 命中率(Task38)に比例する。ただし静止した建物は動くユニットより狙いやすい格好の的なので、
                    // 命中率にはSiegeAccuracyFloor(0.8)の下限を設ける（例: 命中率0.35の砲兵でも基地攻めでは
                    // 0.8として扱う＝素の命中率が低くても砲兵は依然として有効な攻城兵器のままにする）。
                    float accuracy = CombatSynergy.AccuracyFor(state, u, type);
                    float siegeAccuracy = Math.Max(accuracy, SiegeAccuracyFloor);
                    b.CurrentHP -= CombatMath.DamagePerHit(type.Attack, 0f) * dt * siegeAccuracy;
                    if (b.CurrentHP < 0f) b.CurrentHP = 0f;
                }
            }
        }
    }
}
