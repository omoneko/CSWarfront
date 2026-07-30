namespace CSWarfront.Core
{
    /// <summary>
    /// UnitCategoryに「戦い方」の分類フラグを与える拡張（Task79）。文字列比較（TypeKeyのContains等）や
    /// カテゴリの列挙を各所にハードコードするのではなく、判定をここ1箇所に集約する。
    /// CombatStep/BaseCombatStep/ThreatCombatStep/MovementStep/KamikazeStepはすべてこのヘルパー
    /// 経由でカテゴリを判定し、UnitCategory.SuicideDroneという具体値を直接比較する箇所を増やさない
    /// （将来、体当たり式のカテゴリが増えてもIsKamikaze側の1箇所を直すだけで済む）。
    /// </summary>
    public static class UnitCategoryFlags
    {
        /// <summary>自爆特攻（目標へ直接ダイブし、体当たりで一度だけ全ダメージを与えて自壊する）で
        /// 戦うカテゴリか（Task79）。現時点ではSuicideDrone専用。trueを返すカテゴリのユニットは、
        /// 通常の射程内ランダムダメージ・ShotEvent発行を行う射撃系ステップ（CombatStep/
        /// BaseCombatStep/ThreatCombatStep）から早期continueで除外され、代わりにKamikazeStepと
        /// MovementStepのダイブ移動が交戦全体を扱う。</summary>
        public static bool IsKamikaze(this UnitCategory category)
        {
            return category == UnitCategory.SuicideDrone;
        }
    }
}
