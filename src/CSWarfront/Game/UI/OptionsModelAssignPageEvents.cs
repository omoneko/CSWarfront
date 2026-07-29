using System;
using CSWarfront.Game;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// OptionsModelAssignPage のうち、勢力/ユニット種別/アセット種別/検索欄/複製範囲/アセット選択の
    /// 各コントロールの変更通知ハンドラだけを分離した partial class（OptionsModelAssignPage.cs 側が
    /// 500行制限に達したため、Task70で新設。純粋なコード移動のみでロジックの変更は無い）。
    /// フィールド（_suppressEvents/_customOnly/_filteredAssetNames等）は全て OptionsModelAssignPage.cs
    /// 側で宣言されている（partial class は private メンバーも全パーツで共有するため問題ない）。
    /// 全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    /// </summary>
    internal static partial class OptionsModelAssignPage
    {
        private static void OnFactionChanged(int value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshTypeKeyLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
                RefreshCurrentBinding();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnFactionChanged error: " + e);
            }
        }

        private static void OnTypeKeyChanged(int value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshCurrentBinding();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnTypeKeyChanged error: " + e);
            }
        }

        private static void OnAssetKindChanged(int value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshAssetDropdown();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnAssetKindChanged error: " + e);
            }
        }

        private static void OnCustomOnlyChanged(bool value)
        {
            try
            {
                _customOnly = value;
                RefreshAssetDropdown();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnCustomOnlyChanged error: " + e);
            }
        }

        private static void OnSearchTextChanged(string value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshAssetDropdown();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnSearchTextChanged error: " + e);
            }
        }

        /// <summary>AddTextfieldのOnTextSubmitted用no-op（Enter確定時の追加処理は不要。OnTextChangedで
        /// 都度絞り込み済みのため）。</summary>
        private static void OnSearchTextSubmitted(string value)
        {
        }

        /// <summary>AddDropdownのeventCallback用no-op（複製先スコープはOnCopyApplyClickが押された
        /// 時点でselectedIndexを読むだけで足りるため）。</summary>
        private static void OnCopyScopeChanged(int value)
        {
        }

        private static void OnAssetSelected(int value)
        {
            try
            {
                if (_suppressEvents) return;

                if (value >= 1 && value - 1 < _filteredAssetNames.Count) RefreshThumbnail(SelectedAssetKind, _filteredAssetNames[value - 1]);
                else RefreshCurrentBinding();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnAssetSelected error: " + e);
            }
        }
    }
}
