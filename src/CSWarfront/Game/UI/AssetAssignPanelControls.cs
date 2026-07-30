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
        /// <summary>Task66: 先頭に基地種別ごとの4キー（陸軍/海軍/空軍/ミサイル、UnitAssetBindings.
        /// ArmyBaseTypeKey等）を追加した上で、LandUnitRoster.All()（カテゴリ宣言順→Tier1〜5）から35件の
        /// TypeKeyを構築する（計39件）。拠点はユニット種別ではない（LandUnitRoster.All()には含まれない）が、
        /// モデル割り当てUIの対象としては同列に選べるようにする、という要件のため先頭に手動で挿入する
        /// （Task60時点は単一キーだったが、基地種別ごとの割り当てを可能にするため4キーへ分割した）。</summary>
        private static void BuildTypeKeys()
        {
            List<string> keys = new List<string>
            {
                UnitAssetBindings.ArmyBaseTypeKey,
                UnitAssetBindings.NavyBaseTypeKey,
                UnitAssetBindings.AirBaseTypeKey,
                UnitAssetBindings.MissileBaseTypeKey
            };
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
        /// 付ける（AssetKindUtil.Describe）。
        /// Task66: 基地種別キー（陸軍/海軍/空軍/ミサイル）は生のキー文字列ではなく
        /// UnitAssetBindings.DisplayNameForBaseKeyの日本語ラベルを表示し、ユニットの1つと誤解されない
        /// ようにする。割り当ての解決も種別別キー→旧統合キーの順（UnitAssetBindings.TryGetEffective）で
        /// 行い、旧統合キーからのフォールバックが効いている場合もそれを「現在の割り当て」として表示する。</summary>
        private static string[] BuildDropdownLabels()
        {
            if (_typeKeys == null) return new string[0];

            byte factionId = SelectedFactionId;
            string[] labels = new string[_typeKeys.Length];
            for (int i = 0; i < _typeKeys.Length; i++)
            {
                AssetKind kind;
                string name;
                string suffix = UnitAssetBindings.TryGetEffective(factionId, _typeKeys[i], out kind, out name)
                    ? AssetKindUtil.Describe(kind, name)
                    : "(default)";
                string displayKey = UnitAssetBindings.DisplayNameForBaseKey(_typeKeys[i]);
                labels[i] = displayKey + " → " + suffix;
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
            bool bound = UnitAssetBindings.TryGetEffective(SelectedFactionId, _typeKeys[idx], out kind, out name);
            _currentBindingLabel.text = "Current binding: " + (bound ? AssetKindUtil.Describe(kind, name) : "(default model)");
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
                    ModConfig.Log("AssetAssignPanel: apply skipped (no asset selected)");
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
            BaseVisuals.DestroyAll(); // Task60: 拠点の勢力別オーバーレイも破棄し、次回Syncで再解決させる
            ModConfig.Log("AssetAssignPanel: ran UnitVisuals.DestroyAll()/BaseVisuals.DestroyAll() to apply the binding change");

            WarnIfNoOwnedBaseForKey(typeIdx);
        }

        /// <summary>Task66バグ調査対応: 適用先が基地種別キーで、かつ選択中の勢力がその種別の拠点を
        /// 1つも所有していない場合、割り当て自体は正しく保存されるが見た目には何も反映されないため
        /// （反映先の拠点が存在しない）、ユーザーが「反映されていない＝不具合」と誤解しないよう
        /// ログへ明示的な案内を残す（現在の割り当てラベルにも同じ文言を追記する）。</summary>
        private static void WarnIfNoOwnedBaseForKey(int typeIdx)
        {
            if (_typeKeys == null || typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

            BaseType baseType;
            if (!UnitAssetBindings.TryGetBaseTypeForKey(_typeKeys[typeIdx], out baseType)) return;
            if (MilitaryManager.HasOwnedBaseOfType(SelectedFactionId, baseType)) return;

            string note = "(This faction currently owns no " + UnitAssetBindings.DisplayNameForBaseKey(_typeKeys[typeIdx]) +
                ". It will take effect once a base is built or changes ownership)";
            ModConfig.Log("AssetAssignPanel: " + note);
            if (_currentBindingLabel != null) _currentBindingLabel.text += "\n" + note;
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
