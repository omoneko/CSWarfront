using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// AssetAssignPanel のうち、Task47で新設した「複製適用」（現在選択中の(勢力,ユニット種別)の割り当てを
    /// 他の(勢力,種別)へまとめて複製する）専用の範囲ドロップダウンとボタンだけを分離した partial class
    /// （AssetAssignPanel.cs 側の500行制限のため。AssetAssignPanelFaction.cs と同じ方針で、フィールドも
    /// ここで宣言する）。実際の複製処理は UnitAssetBindings.CopyTo（Game/UnitAssetBindings.cs）が担い、
    /// このファイルはUI（ドロップダウン構築・クリックハンドラ）のみを持つ。Options サブページ
    /// （Game/UI/OptionsModelAssignPage.cs）も同じ UnitAssetBindings.CopyTo を呼ぶため、複製ロジック自体は
    /// 二重実装しない。全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        private static UILabel _copyScopeSectionLabel;
        private static UIDropDown _copyScopeDropdown;
        private static UIButton _copyApplyButton;

        private static UIDropDown BuildCopyScopeDropdown(float x, float y, float width)
        {
            UIDropDown dd = _panel.AddUIComponent<UIDropDown>();
            dd.size = new Vector2(width, DropdownHeight);
            dd.relativePosition = new Vector3(x, y);
            dd.normalBgSprite = "ButtonMenu";
            dd.hoveredBgSprite = "ButtonMenuHovered";
            dd.disabledBgSprite = "ButtonMenuDisabled";
            dd.listBackground = "GenericPanelLight";
            dd.itemHeight = 26;
            dd.itemHover = "ListItemHover";
            dd.itemHighlight = "ListItemHighlight";
            dd.listWidth = (int)width;
            dd.listHeight = 200;
            dd.listPosition = UIDropDown.PopupListPosition.Below;
            dd.textScale = 0.7f; // 他パネルと統一（やや小さめ、ラベルが長いため）
            dd.textFieldPadding = new RectOffset(10, 10, 9, 0);
            dd.itemPadding = new RectOffset(10, 0, 5, 0);
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            string[] labels = new string[CopyScopeUtil.All.Length];
            for (int i = 0; i < CopyScopeUtil.All.Length; i++) labels[i] = CopyScopeUtil.DisplayNameJa(CopyScopeUtil.All[i]);
            dd.items = labels;
            dd.selectedIndex = 0;

            UIButton trigger = dd.AddUIComponent<UIButton>();
            trigger.text = "▼";
            trigger.textScale = 0.7f;
            trigger.size = new Vector2(36f, DropdownHeight);
            trigger.relativePosition = new Vector3(width - 36f, 0f);
            trigger.normalBgSprite = "ButtonMenu";
            trigger.hoveredBgSprite = "ButtonMenuHovered";
            trigger.pressedBgSprite = "ButtonMenuPressed";
            dd.triggerButton = trigger;

            return dd;
        }

        /// <summary>「複製適用」ボタンのクリックハンドラ。現在の(勢力,ユニット種別)の割り当てを
        /// 選択中スコープへ複製し、UnitAssetBindings.CopyTo と同じ「反映」処理（ラベル再構築＋見た目破棄）
        /// を通す。コピー元に割り当てが無い場合（既定のまま）は CopyTo が0件でスキップしログを残すのみ。</summary>
        private static void OnCopyApplyClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_typeKeyDropdown == null || _typeKeys == null || _copyScopeDropdown == null) return;

                int typeIdx = _typeKeyDropdown.selectedIndex;
                if (typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

                int scopeIdx = _copyScopeDropdown.selectedIndex;
                if (scopeIdx < 0 || scopeIdx >= CopyScopeUtil.All.Length) return;
                CopyScope scope = CopyScopeUtil.All[scopeIdx];

                int written = UnitAssetBindings.CopyTo(SelectedFactionId, _typeKeys[typeIdx], scope);
                if (written > 0)
                {
                    ApplyBindingChange(typeIdx); // 既存のApply/Reset共通反映処理（ラベル再構築＋UnitVisuals.DestroyAll）
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnCopyApplyClick error: " + e);
            }
        }
    }
}
