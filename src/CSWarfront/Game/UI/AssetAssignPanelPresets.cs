using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// AssetAssignPanel のうち、Task70で新設した「全て初期化」ボタンと「セット」プリセット行
    /// （スロット1-3の保存/読込）だけを分離した partial class（AssetAssignPanel.cs 側の500行制限の
    /// ため。AssetAssignPanelCopy.cs と同じ方針）。実際の永続化処理は UnitAssetBindings.ClearAll/
    /// SaveToSlot/LoadFromSlot（Game/UnitAssetBindingsPresets.cs）が一元的に担い、このファイルはUI
    /// （ボタン/ドロップダウンの構築・クリックハンドラ・フィードバック表示）のみを持つ。Options
    /// サブページ（Game/UI/OptionsModelAssignPagePresets.cs）も同じAPIを呼ぶため、永続化ロジック
    /// 自体は二重実装しない。全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        private static UIButton _clearAllButton;
        private static UILabel _presetSectionLabel;
        private static UIDropDown _presetSlotDropdown;
        private static UIButton _presetSaveButton;
        private static UIButton _presetLoadButton;
        private static UILabel _presetMessageLabel;

        /// <summary>ドロップダウンの選択インデックス(0..2)からスロット番号(1..3)へ変換する。</summary>
        private static int SelectedPresetSlot
        {
            get { return _presetSlotDropdown != null ? _presetSlotDropdown.selectedIndex + 1 : 1; }
        }

        private static UIButton BuildClearAllButton(float x, float y, float width)
        {
            _clearAllButton = BuildButton("Reset All", x, y, width, OnClearAllClick);
            return _clearAllButton;
        }

        /// <summary>「セット」行: スロット選択ドロップダウン(1/2/3、空スロットには「（空）」を付す) +
        /// 保存/読込ボタン。dropdownは行幅の約1/3、残りを保存/読込ボタンで等分する。</summary>
        private static void BuildPresetRow(float x, float y, float width)
        {
            float dropdownWidth = width * 0.34f;
            float buttonWidth = (width - dropdownWidth - SectionGap * 2f) / 2f;

            _presetSlotDropdown = BuildPresetSlotDropdown(x, y, dropdownWidth);
            _presetSaveButton = BuildButton("Save", x + dropdownWidth + SectionGap, y, buttonWidth, OnSaveSlotClick);
            _presetLoadButton = BuildButton("Load", x + dropdownWidth + SectionGap + buttonWidth + SectionGap, y, buttonWidth, OnLoadSlotClick);
        }

        private static UIDropDown BuildPresetSlotDropdown(float x, float y, float width)
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
            dd.listHeight = 90;
            dd.listPosition = UIDropDown.PopupListPosition.Below;
            dd.textScale = 0.75f; // 他パネルと統一
            dd.textFieldPadding = new RectOffset(12, 12, 9, 0);
            dd.itemPadding = new RectOffset(12, 0, 5, 0);
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            dd.items = BuildPresetSlotLabels();
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

        /// <summary>「1」「2（空）」のように、UnitAssetBindings.SlotExists（安価なFile.Existsのみ）で
        /// 存在しないスロットへ「（空）」を付与したラベルを構築する。</summary>
        private static string[] BuildPresetSlotLabels()
        {
            string[] labels = new string[3];
            for (int i = 0; i < 3; i++)
            {
                int slot = i + 1;
                labels[i] = UnitAssetBindings.SlotExists(slot) ? slot.ToString() : slot + " (empty)";
            }
            return labels;
        }

        /// <summary>スロットドロップダウンのラベルだけを、選択位置を保ったまま再構築する。Show()、
        /// および保存/読込直後（内容が変わりうるため）に呼ぶ。</summary>
        private static void RefreshPresetSlotLabels()
        {
            if (_presetSlotDropdown == null) return;

            int selected = _presetSlotDropdown.selectedIndex;
            _suppressEvents = true;
            try
            {
                _presetSlotDropdown.items = BuildPresetSlotLabels();
                _presetSlotDropdown.selectedIndex = (selected >= 0 && selected < 3) ? selected : 0;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        /// <summary>「全て初期化」ボタン。UnitAssetBindings.ClearAllで全割り当てを既定へ戻し、
        /// 削除件数をメッセージ表示する。1件以上削除した場合のみ、既存のApply/Reset/CopyApplyと
        /// 同じ反映処理（ラベル再構築＋見た目破棄）を行う。</summary>
        private static void OnClearAllClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                int removed = UnitAssetBindings.ClearAll();
                SetPresetMessage(removed > 0
                    ? "Reset all bindings (" + removed + " entries)"
                    : "Nothing to reset (already at default)");

                if (removed > 0)
                {
                    RefreshDropdownLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
                    RefreshCurrentBindingLabel();
                    UnitVisuals.DestroyAll();
                    BaseVisuals.DestroyAll();
                    ModConfig.Log("AssetAssignPanel: ran UnitVisuals.DestroyAll()/BaseVisuals.DestroyAll() to apply the full reset");
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnClearAllClick error: " + e);
            }
        }

        private static void OnSaveSlotClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                int slot = SelectedPresetSlot;
                bool ok = UnitAssetBindings.SaveToSlot(slot);
                SetPresetMessage(ok ? "Saved to preset " + slot : "Failed to save to preset " + slot);
                if (ok) RefreshPresetSlotLabels();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnSaveSlotClick error: " + e);
            }
        }

        /// <summary>読込はテーブル全体を置き換える（UnitAssetBindings.LoadFromSlotのREPLACEセマンティクス、
        /// クラス冒頭コメント参照）ため、成功時は勢力/種別ドロップダウンのラベル・現在の割り当て表示・
        /// 見た目を全て再同期する（Apply/Reset/CopyApplyと同じ反映処理）。</summary>
        private static void OnLoadSlotClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                int slot = SelectedPresetSlot;
                bool ok = UnitAssetBindings.LoadFromSlot(slot);
                SetPresetMessage(ok ? "Loaded preset " + slot + " (replaced the existing bindings)" : "Preset " + slot + " is empty");

                if (ok)
                {
                    RefreshPresetSlotLabels();
                    RefreshDropdownLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
                    RefreshCurrentBindingLabel();
                    UnitVisuals.DestroyAll();
                    BaseVisuals.DestroyAll();
                    ModConfig.Log("AssetAssignPanel: ran UnitVisuals.DestroyAll()/BaseVisuals.DestroyAll() to apply the preset load");
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnLoadSlotClick error: " + e);
            }
        }

        private static void SetPresetMessage(string text)
        {
            if (_presetMessageLabel != null) _presetMessageLabel.text = text;
        }

        /// <summary>Destroy()から呼ばれる。イベント購読解除とフィールドリセットのみ
        /// （AssetAssignPanelFaction.DestroyFactionSectionと同じ方針）。</summary>
        private static void DestroyPresetsSection()
        {
            if (_clearAllButton != null) _clearAllButton.eventClick -= OnClearAllClick;
            if (_presetSaveButton != null) _presetSaveButton.eventClick -= OnSaveSlotClick;
            if (_presetLoadButton != null) _presetLoadButton.eventClick -= OnLoadSlotClick;
            _clearAllButton = null;
            _presetSectionLabel = null;
            _presetSlotDropdown = null;
            _presetSaveButton = null;
            _presetLoadButton = null;
            _presetMessageLabel = null;
        }
    }
}
