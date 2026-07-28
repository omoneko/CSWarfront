using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// AssetAssignPanel のうち、アセット種別（プロップ/建物/車両/樹木）ドロップダウン・検索欄・
    /// サブスク済みトグル・一覧の構築とイベントハンドラだけを分離した partial class（Task41で新設、
    /// 500行制限のため。旧AssetAssignPanelControls.csに同居していたが、Task41で検索欄の可読性修正と
    /// アセット種別ドロップダウンを追加した際に分離した）。
    /// フィールドは全て AssetAssignPanel.cs 側で宣言されている（partial class は private メンバーも
    /// 全パーツで共有するため問題ない）。全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    ///
    /// 検索欄(UITextField)の可読性修正: 旧実装は textColor を黒(0,0,0)のまま normalBgSprite="TextFieldPanel"
    /// （CS既定の暗色パネルスプライト）に重ねていたため、黒文字が暗い背景に沈んで読めなかった。
    /// ColossalManaged.dllをリフレクションで確認したUITextFieldの全プロパティ（Task41 task-41-report.md
    /// 参照）のうち、以下を明示設定して修正する: textColor(Color32、ほぼ白へ)、color(Color32、白のまま＝
    /// スプライトを暗くtintしない)、selectionBackgroundColor(Color32、選択範囲の可視化)、
    /// padding(RectOffset、2倍スケール)、horizontalAlignment/verticalAlignment(左/中央)、
    /// textScale(1.5倍スケール)、cursorWidth(Int32)。normalBgSprite/hoveredBgSprite/
    /// focusedBgSprite/disabledBgSprite/selectionSprite は既存のまま（"TextFieldPanel"系は実際に
    /// このプロジェクトで使用実績のあるスプライト名のため、未検証の別名への変更はしない）。
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        /// <summary>現在選択中のアセット種別。ドロップダウン未生成の間は既定のProp(0)を返す
        /// （ドロップダウンの選択インデックス0..3が AssetKindUtil.All と同じ並びであることに依存）。</summary>
        internal static AssetKind SelectedAssetKind
        {
            get { return _assetKindDropdown != null ? (AssetKind)_assetKindDropdown.selectedIndex : AssetKind.Prop; }
        }

        private static UITextField BuildSearchField(float x, float y, float width)
        {
            UITextField tf = _panel.AddUIComponent<UITextField>();
            tf.size = new Vector2(width, RowHeight);
            tf.relativePosition = new Vector3(x, y);
            tf.padding = new RectOffset(9, 9, 9, 9); // 旧(6,6,6,6) ×1.5
            tf.builtinKeyNavigation = true;
            tf.isInteractive = true;
            tf.readOnly = false;
            tf.selectOnFocus = true;
            tf.horizontalAlignment = UIHorizontalAlignment.Left;
            tf.verticalAlignment = UIVerticalAlignment.Middle; // 検証済み、新規設定（縦中央で読みやすく）
            tf.normalBgSprite = "TextFieldPanel";
            tf.hoveredBgSprite = "TextFieldPanelHovered";
            tf.focusedBgSprite = "TextFieldPanelFocused";
            tf.disabledBgSprite = "TextFieldPanel";
            tf.color = Color.white; // 検証済み、新規設定。スプライトを暗くtintしない（白のまま表示）。
            tf.textColor = new Color32(240, 240, 240, 255); // 旧(0,0,0)＝黒 → ほぼ白に変更（可読性修正の本体）
            tf.disabledTextColor = new Color32(150, 150, 150, 255); // 検証済み、新規設定
            tf.textScale = 0.8f; // 他パネルと統一
            tf.cursorWidth = 2; // 旧1
            tf.cursorBlinkTime = 0.45f;
            tf.selectionSprite = "EmptySprite";
            tf.selectionBackgroundColor = new Color32(70, 130, 200, 160); // 検証済み、新規設定（選択範囲の可視化）
            tf.text = "";
            tf.eventTextChanged += OnSearchTextChanged;
            return tf;
        }

        /// <summary>Task41: プロップ/建物/車両/樹木を切り替えるドロップダウン。既定インデックス0(プロップ)。
        /// 選択インデックスは AssetKindUtil.All / AssetKind の並びと一致させている。</summary>
        private static UIDropDown BuildAssetKindDropdown(float x, float y, float width)
        {
            UIDropDown dd = _panel.AddUIComponent<UIDropDown>();
            dd.size = new Vector2(width, RowHeight);
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
            dd.textScale = 0.75f;
            dd.textFieldPadding = new RectOffset(12, 12, 9, 0);
            dd.itemPadding = new RectOffset(12, 0, 5, 0);
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            string[] labels = new string[AssetKindUtil.All.Length];
            for (int i = 0; i < AssetKindUtil.All.Length; i++) labels[i] = AssetKindUtil.DisplayNameJa(AssetKindUtil.All[i]);
            dd.items = labels;
            dd.selectedIndex = 0; // 既定 プロップ

            UIButton trigger = dd.AddUIComponent<UIButton>();
            trigger.text = "▼";
            trigger.textScale = 0.7f;
            trigger.size = new Vector2(40f, RowHeight);
            trigger.relativePosition = new Vector3(width - 40f, 0f);
            trigger.normalBgSprite = "ButtonMenu";
            trigger.hoveredBgSprite = "ButtonMenuHovered";
            trigger.pressedBgSprite = "ButtonMenuPressed";
            dd.triggerButton = trigger;

            dd.eventSelectedIndexChanged += OnAssetKindChanged;
            return dd;
        }

        private static void OnAssetKindChanged(UIComponent component, int value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshAssetList();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnAssetKindChanged error: " + e);
            }
        }

        private static void OnSearchTextChanged(UIComponent component, string value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshAssetList();
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
                RefreshAssetList();
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

        /// <summary>Task40: 一覧選択が変わるたびにサムネイルを更新する（要件3）。選択解除（-1、
        /// RefreshAssetListによるリセット等）の場合は、現在適用中のアセットのサムネイルへ戻す。
        /// Task41: サムネイル解決には現在の種類ドロップダウン選択(SelectedAssetKind)を使う
        /// （一覧はその種類のアセットしか含まないため）。</summary>
        private static void OnAssetSelected(UIComponent component, int value)
        {
            try
            {
                if (_suppressEvents) return;

                if (value >= 0 && value < _filteredAssetNames.Count)
                {
                    RefreshThumbnail(SelectedAssetKind, _filteredAssetNames[value]);
                }
                else
                {
                    RefreshCurrentBindingLabel(); // 内部でRefreshThumbnail(既定/現在の割り当て)も更新する
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnAssetSelected error: " + e);
            }
        }

        /// <summary>検索文字列・サブスクライブ済みのみトグル・アセット種別に基づき一覧を再構築する。
        /// MaxListItems件を超える場合は打ち切り、その旨を _truncatedLabel に表示する。</summary>
        private static void RefreshAssetList()
        {
            if (_propListBox == null) return;

            string filter = _searchField != null ? _searchField.text : null;
            List<string> names = AssetCatalog.GetNames(SelectedAssetKind, _customOnly, filter);

            _filteredAssetNames.Clear();
            bool truncated = names.Count > MaxListItems;
            int count = truncated ? MaxListItems : names.Count;
            for (int i = 0; i < count; i++) _filteredAssetNames.Add(names[i]);

            _suppressEvents = true;
            try
            {
                _propListBox.items = _filteredAssetNames.ToArray();
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
    }
}
