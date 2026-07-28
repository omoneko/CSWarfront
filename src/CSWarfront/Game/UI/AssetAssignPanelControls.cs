using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// AssetAssignPanel のうち、ユニット種別ドロップダウンと適用・既定に戻す・閉じるボタンの構築と
    /// イベントハンドラだけを分離した partial class（AssetAssignPanel.cs 側の500行制限のため。
    /// BaseInfoPanel/BaseInfoPanelProduction と同じ方針）。検索欄・アセット種別ドロップダウン・
    /// サブスク済みトグル・一覧は Task41 で AssetAssignPanelAssetList.cs へ分離した。
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
            dd.itemHeight = 26; // 文字等倍・行間は少し広め
            dd.itemHover = "ListItemHover";
            dd.itemHighlight = "ListItemHighlight";
            dd.listWidth = (int)width;
            dd.listHeight = 300; // 旧200 ×1.5
            dd.listPosition = UIDropDown.PopupListPosition.Below;
            dd.textScale = 0.75f; // 他パネルと統一
            dd.textFieldPadding = new RectOffset(12, 12, 9, 0); // 旧(8,8,6,0) ×1.5
            dd.itemPadding = new RectOffset(12, 0, 5, 0); // 旧(8,0,3,0) ×1.5
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            dd.items = BuildDropdownLabels();
            dd.selectedIndex = 0;

            UIButton trigger = dd.AddUIComponent<UIButton>();
            trigger.text = "▼";
            trigger.textScale = 0.7f; // 他パネルと統一
            trigger.size = new Vector2(36f, DropdownHeight); // 旧24f ×1.5
            trigger.relativePosition = new Vector3(width - 48f, 0f);
            trigger.normalBgSprite = "ButtonMenu";
            trigger.hoveredBgSprite = "ButtonMenuHovered";
            trigger.pressedBgSprite = "ButtonMenuPressed";
            dd.triggerButton = trigger;

            dd.eventSelectedIndexChanged += OnTypeKeyChanged;
            return dd;
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

        /// <summary>Task40: ラベルは現在選択中の勢力(SelectedFactionId)の割り当てを表示する
        /// （勢力ドロップダウンを切り替えるたびに OnFactionChanged 経由で再構築される）。
        /// Task41: プロップ以外の種類が割り当てられている場合は "[建物]MyBuilding" のように種類タグを
        /// 付ける（AssetKindUtil.Describe）。</summary>
        private static string[] BuildDropdownLabels()
        {
            if (_typeKeys == null) return new string[0];

            byte factionId = SelectedFactionId;
            string[] labels = new string[_typeKeys.Length];
            for (int i = 0; i < _typeKeys.Length; i++)
            {
                AssetKind kind;
                string name;
                string suffix = UnitAssetBindings.TryGet(factionId, _typeKeys[i], out kind, out name)
                    ? AssetKindUtil.Describe(kind, name)
                    : "(既定)";
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

        /// <summary>Task40: 現在選択中の(勢力, TypeKey)の割り当てを表示し、サムネイルもそれに合わせて
        /// 更新する（一覧選択とは独立: ここは「今適用されている」アセットのプレビュー）。
        /// Task41: 割り当ての種類(AssetKind)もあわせて解決し、サムネイル・ラベル表示に使う。</summary>
        private static void RefreshCurrentBindingLabel()
        {
            if (_currentBindingLabel == null || _typeKeyDropdown == null || _typeKeys == null) return;

            int idx = _typeKeyDropdown.selectedIndex;
            if (idx < 0 || idx >= _typeKeys.Length)
            {
                _currentBindingLabel.text = "";
                RefreshThumbnail(AssetKind.Prop, null);
                return;
            }

            AssetKind kind;
            string name;
            bool bound = UnitAssetBindings.TryGet(SelectedFactionId, _typeKeys[idx], out kind, out name);
            _currentBindingLabel.text = "現在の割り当て: " + (bound ? AssetKindUtil.Describe(kind, name) : "(既定のモデル)");
            RefreshThumbnail(bound ? kind : AssetKind.Prop, bound ? name : null);
        }

        private static void OnApplyClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_typeKeyDropdown == null || _typeKeys == null || _propListBox == null) return;

                int typeIdx = _typeKeyDropdown.selectedIndex;
                if (typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

                int assetIdx = _propListBox.selectedIndex;
                if (assetIdx < 0 || assetIdx >= _filteredAssetNames.Count)
                {
                    ModConfig.Log("AssetAssignPanel: 適用スキップ（アセットが未選択）");
                    return;
                }

                string typeKey = _typeKeys[typeIdx];
                string assetName = _filteredAssetNames[assetIdx];
                AssetKind kind = SelectedAssetKind;

                UnitAssetBindings.Set(SelectedFactionId, typeKey, kind, assetName);
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

                UnitAssetBindings.Clear(SelectedFactionId, _typeKeys[typeIdx]);
                ApplyBindingChange(typeIdx);
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnResetClick error: " + e);
            }
        }

        /// <summary>割り当て変更（Apply/Reset共通）の反映処理。ドロップダウンのラベルと現在割り当て表示を
        /// 更新した上で、既存の見た目を全破棄する（Task36要件: 即座に反映、次回SyncでCreateVisualが
        /// UnitMeshSource経由の新しい割り当て(種類込み、Task41)を解決し直す）。</summary>
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
