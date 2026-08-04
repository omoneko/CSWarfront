namespace CSWarfront.Core
{
    /// <summary>
    /// Task101: 塹壕・掩蔽壕の上にいる歩兵系（Infantry/MechInfantry）への守備ボーナス+50%
    /// （＝被ダメージ÷1.5。設計§1.2）。**所有勢力・稼働状態は不問**——敵に取られた塹壕は
    /// 敵歩兵に使われるし、機能停止した掩蔽壕も地形としての防御価値は残る。
    /// 適用: CombatStepの対ユニットダメージ・KamikazeStepの起爆・FortCombatStepの射撃。
    /// 外部脅威（ゴジラ光線等）・災害ミサイルは対象外（regular兵器でないため貫通する）。
    /// </summary>
    public static class FortDefenseBonus
    {
        /// <summary>塹壕（16×32m）の効果半径（対角半径強の簡易円判定）。</summary>
        public const float TrenchRadius = 18f;

        /// <summary>掩蔽壕（16×16m）の効果半径。</summary>
        public const float BunkerRadius = 12f;

        /// <summary>被ダメージの除数（1.5=守備+50%）。</summary>
        public const float DamageDivisor = 1.5f;

        /// <summary>targetへ適用する被ダメージ倍率（ボーナス圏内の歩兵系なら1/1.5、それ以外1.0）。</summary>
        public static float Multiplier(WarState state, UnitInstance target, UnitType targetType)
        {
            if (targetType == null) return 1f;
            if (targetType.Category != UnitCategory.Infantry &&
                targetType.Category != UnitCategory.MechInfantry) return 1f;
            return IsOnFortification(state, target.Position) ? 1f / DamageDivisor : 1f;
        }

        /// <summary>posが塹壕/掩蔽壕のボーナス圏内か（FortSeekStepの「既に陣取っている」判定にも使う）。</summary>
        public static bool IsOnFortification(WarState state, WorldPos pos)
        {
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                float radius;
                if (b.Type == BaseType.Trench) radius = TrenchRadius;
                else if (b.Type == BaseType.Bunker) radius = BunkerRadius;
                else continue;
                if (pos.HorizontalDistanceTo(b.Position) <= radius) return true;
            }
            return false;
        }
    }
}
