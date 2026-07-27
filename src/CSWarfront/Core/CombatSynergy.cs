using System;

namespace CSWarfront.Core
{
    /// <summary>
    /// ユニット同士の組み合わせによる相乗効果（シナジー）を計算する（Task38）。
    /// 現時点で定義されているシナジーは「Artillery（砲兵）＋DroneInfantry（ドローン兵）の観測支援」
    /// の1件のみだが、AccuracyFor をエントリポイントにしておくことで、将来
    /// （例: MechInfantryの護衛でTankの装甲UP、AntiAirの掩護でApcの生存率UP等）別カテゴリ向けの
    /// シナジーを追加する際もこのクラスに判定を積み重ねていける。
    ///
    /// 決定的（乱数不使用）・O(units)：味方ユニット一覧を1回線形走査するだけで済む。
    /// </summary>
    public static class CombatSynergy
    {
        /// <summary>味方ドローン兵がこの水平距離内にいれば、砲兵の観測支援（スポット）が成立する。</summary>
        public const float DroneSpotterRadius = 150f;

        /// <summary>観測支援が成立した場合に命中率へ加算するボーナス（乗算ではなく加算）。
        /// 加算後の値は TierScaling.AccuracyMax(0.95) でクランプする。</summary>
        public const float DroneSpotterAccuracyBonus = 0.5f;

        /// <summary>
        /// attacker が attackerType として攻撃する際の「実効命中率」を返す。
        /// 現在定義済みのルール: attackerType.Category が Artillery で、かつ同じ勢力（Faction）の
        /// 生存中DroneInfantryユニットが DroneSpotterRadius 以内（水平距離）に1体以上いれば、
        /// min(TierScaling.AccuracyMax, attackerType.Accuracy + DroneSpotterAccuracyBonus) を返す。
        /// それ以外（非Artillery、または観測支援なし）は attackerType.Accuracy をそのまま返す。
        /// </summary>
        public static float AccuracyFor(WarState state, UnitInstance attacker, UnitType attackerType)
        {
            if (attackerType == null) return 0f;
            if (attackerType.Category != UnitCategory.Artillery) return attackerType.Accuracy;
            if (!HasFriendlyDroneSpotterNearby(state, attacker)) return attackerType.Accuracy;
            return Math.Min(TierScaling.AccuracyMax, attackerType.Accuracy + DroneSpotterAccuracyBonus);
        }

        /// <summary>attacker と同じ勢力(FactionId)の、生存中のDroneInfantryが
        /// DroneSpotterRadius以内（水平距離）に1体でもいればtrue。</summary>
        private static bool HasFriendlyDroneSpotterNearby(WarState state, UnitInstance attacker)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (u.FactionId != attacker.FactionId) continue;
                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null || type.Category != UnitCategory.DroneInfantry) continue;
                if (attacker.Position.HorizontalDistanceTo(u.Position) <= DroneSpotterRadius) return true;
            }
            return false;
        }
    }
}
