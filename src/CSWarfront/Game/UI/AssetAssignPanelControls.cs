using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// AssetAssignPanel のうち、ユニット種別ドロップダウン/検索/サブスク済みトグル/プロップ一覧/
    /// 適用・既定に戻す・閉じるボタンの構築とイベントハンドラだけを分離した partial class
    /// （AssetAssignPanel.cs 側の500行制限のため。BaseInfoPanel/BaseInfoPanelProduction と同じ方針）。
    /// フィールドは全て AssetAssignPanel.cs 側で宣言されている（partial class は private メンバーも
    /// 全パーツで共有するため問題ない）。全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        /// <summary>LandUnitRoster.All()（カテゴリ宣言順→Tier1〜5）から35件のTypeKeyを構築する。</summary>
        private static void BuildTypeKeys()
        {
            List<string> keys = new List<string>();
            foreach (UnitType t in LandUnitRoster.All()) keys.Add(t.TypeKey);
            _typeKeys = keys.ToArray();
        }

        private static UIDropDown BuildTypeKeyDropdown(float x, float y, float width)
        {
            UIDropDown dd = _panel.AddUIComponent<UIDropDown>();
            dd.size = new Vector2(width, DropdownHeight);
            dd.relativePosition = new Vector3(x, y);
            dd.normalBgSprite = "ButtonMenu";
            dd.hoveredBgSprite = "ButtonMenuHovered";
            dd.disabledBgSprite = "ButtonMenuDisabled";
            dd.listBackground = "GenericPanelLight";
            dd.itemHeight = 22;
            dd.itemHover = "ListItemHover";
            dd.itemHighlight = "ListItemHighlight";
            dd.listWidth = (int)width;
            dd.listHeight = 200;
            dd.listPosition = UIDropDown.PopupListPosition.Below;
            dd.textScale = 0.75f;
            dd.textFieldPadding = new RectOffset(8, 8, 6, 0);
            dd.itemPadding = new RectOffset(8, 0, 3, 0);
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            dd.items = BuildDropdownLabels();
            dd.selectedIndex = 0;

            UIButton trigger = dd.AddUIComponent<UIButton>();
            trigger.text = "▼";
            trigger.textScale = 0.7f;
            trigger.size = new Vector2(24f, DropdownHeight);
            trigger.relativePosition = new Vector3(width - 24f, 0f);
            trigger.normalBgSprite = "ButtonMenu";
            trigger.hoveredBgSprite = "ButtonMenuHovered";
            trigger.pressedBgSprite = "ButtonMenuPressed";
            dd.triggerButton = trigger;

            dd.eventSelectedIndexChanged += OnTypeKeyChanged;
            return dd;
        }

        private static UITextField BuildSearchField(float x, float y, float width)
        {
            UITextField tf = _panel.AddUIComponent<UITextField>();
            tf.size = new Vector2(width, RowHeight);
            tf.relativePosition = new Vector3(x, y);
            tf.padding = new RectOffset(6, 6, 6, 6);
            tf.builtinKeyNavigation = true;
            tf.isInteractive = true;
            tf.readOnly = false;
            tf.selectOnFocus = true;
            tf.horizontalAlignment = UIHorizontalAlignment.Left;
            tf.normalBgSprite = "TextFieldPanel";
            tf.hoveredBgSprite = "TextFieldPanelHovered";
            tf.focusedBgSprite = "TextFieldPanelFocused";
            tf.disabledBgSprite = "TextFieldPanel";
            tf.textColor = new Color32(0, 0, 0, 255);
            tf.textScale = 0.8f;
            tf.cursorWidth = 1;
            tf.cursorBlinkTime = 0.45f;
            tf.selectionSprite = "EmptySprite";
            tf.text = "";
            tf.eventTextChanged += OnSearchTextChanged;
            return tf;
        }

        /// <summary>"TypeKey → 現在の割り当て（無ければ (既定)）" の35件ラベルを再構築し、選択位置を
        /// selectedIndexへ復元する（Apply/Reset直後の一覧更新、およびShow()での毎回の再表示用）。</summary>
        private static void RefreshDropdownLabels(int selectedIndex)
        {
            if (_typeKeyDropdown == null || _typeKeys == null) return;

            _suppressEvents = true;
            try
            {
                _typeKeyDropdown.items = BuildDropdownLabels();
                if (selectedIndex >= 0 && selectedIndex < _typeKeys.Length)
                    _typeKeyDropdown.selectedIndex = selectedIndex;
                else if (_typeKeys.Length > 0)
                    _typeKeyDropdown.selectedIndex = 0;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private static string[] BuildDropdownLabels()
        {
            if (_typeKeys == null) return new string[0];

            string[] labels = new string[_typeKeys.Length];
            for (int i = 0; i < _typeKeys.Length; i++)
            {
                string propName;
                string suffix = UnitAssetBindings.TryGet(_typeKeys[i], out propName) ? propName : "(既定)";
                labels[i] = _typeKeys[i] + " → " + suffix;
            }
            return labels;
        }

        private static void OnTypeKeyChanged(UIComponent component, int value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshCurrentBindingLabel();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnTypeKeyChanged error: " + e);
            }
        }

        private static void RefreshCurrentBindingLabel()
        {
            if (_currentBindingLabel == null || _typeKeyDropdown == null || _typeKeys == null) return;

            int idx = _typeKeyDropdown.selectedIndex;
            if (idx < 0 || idx >= _typeKeys.Length)
            {
                _currentBindingLabel.text = "";
                return;
            }

            string propName;
            string current = UnitAssetBindings.TryGet(_typeKeys[idx], out propName) ? propName : "(既定のモデル)";
            _currentBindingLabel.text = "現在の割り当て: " + current;
        }

        private static void OnSearchTextChanged(UIComponent component, string value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshPropList();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnSearchTextChanged error: " + e);
            }
        }

        private static void OnCustomOnlyClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                _customOnly = !_customOnly;
                UpdateCustomOnlyLabel();
                RefreshPropList();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnCustomOnlyClick error: " + e);
            }
        }

        private static void UpdateCustomOnlyLabel()
        {
            if (_customOnlyToggle != null)
            {
                _customOnlyToggle.text = "サブスクライブ済みのみ: " + (_customOnly ? "ON" : "OFF");
            }
        }

        private static void OnPropSelected(UIComponent component, int value)
        {
            // 選択自体は _propListBox.selectedIndex を都度読めば十分なため、ここでは何もしない
            // （適用ボタン押下時にまとめて読む）。将来プレビュー等を足す場合の拡張点として残す。
        }

        /// <summary>検索文字列・サブスクライブ済みのみトグルに基づき一覧を再構築する。
        /// MaxListItems件を超える場合は打ち切り、その旨を _truncatedLabel に表示する。</summary>
        private static void RefreshPropList()
        {
            if (_propListBox == null) return;

            string filter = _searchField != null ? _searchField.text : null;
            List<string> names = PropCatalog.GetNames(_customOnly, filter);

            _filteredProps.Clear();
            bool truncated = names.Count > MaxListItems;
            int count = truncated ? MaxListItems : names.Count;
            for (int i = 0; i < count; i++) _filteredProps.Add(names[i]);

            _suppressEvents = true;
            try
            {
                _propListBox.items = _filteredProps.ToArray();
                _propListBox.selectedIndex = -1;
            }
            finally
            {
                _suppressEvents = false;
            }

            if (_truncatedLabel != null)
            {
                _truncatedLabel.text = truncated
                    ? "※ " + names.Count + " 件中 " + MaxListItems + " 件のみ表示（検索で絞り込んでください）"
                    : names.Count + " 件";
            }
        }

        private static void OnApplyClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_typeKeyDropdown == null || _typeKeys == null || _propListBox == null) return;

                int typeIdx = _typeKeyDropdown.selectedIndex;
                if (typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

                int propIdx = _propListBox.selectedIndex;
                if (propIdx < 0 || propIdx >= _filteredProps.Count)
                {
                    ModConfig.Log("AssetAssignPanel: 適用スキップ（プロップが未選択）");
                    return;
                }

                string typeKey = _typeKeys[typeIdx];
                string propName = _filteredProps[propIdx];

                UnitAssetBindings.Set(typeKey, propName);
                ApplyBindingChange(typeIdx);
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnApplyClick error: " + e);
            }
        }

        private static void OnResetClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_typeKeyDropdown == null || _typeKeys == null) return;

                int typeIdx = _typeKeyDropdown.selectedIndex;
                if (typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

                UnitAssetBindings.Clear(_typeKeys[typeIdx]);
                ApplyBindingChange(typeIdx);
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnResetClick error: " + e);
            }
        }

        /// <summary>割り当て変更（Apply/Reset共通）の反映処理。ドロップダウンのラベルと現在割り当て表示を
        /// 更新した上で、既存の見た目を全破棄する（Task36要件: 即座に反映、次回SyncでCreateVisualが
        /// UnitMeshSource経由の新しい割り当てを解決し直す）。</summary>
        private static void ApplyBindingChange(int typeIdx)
        {
            RefreshDropdownLabels(typeIdx);
            RefreshCurrentBindingLabel();

            UnitVisuals.DestroyAll();
            ModConfig.Log("AssetAssignPanel: 割り当て変更を反映するため UnitVisuals.DestroyAll() を実行しました");
        }

        private static void OnCloseClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                Hide();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnCloseClick error: " + e);
            }
        }
    }
}
