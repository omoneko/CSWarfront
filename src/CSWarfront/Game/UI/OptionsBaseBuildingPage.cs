using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using CSWarfront.Core;
using CSWarfront.Game;
using ICities;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task74: Mod Options（Game/Mod.cs、OnSettingsUI）内に直接構築する「基地に使う建物」サブページ。
    ///
    /// 従来（Task18〜）は電力タブに複製した専用プレハブ（WarfrontBasePrefab、見た目はまだ風力タービンの
    /// ままでツールバーのサムネイルも紛らわしい）を配置することでのみ基地を作れた。本ページはその代わりに
    /// 「Optionで既存の建物アセットを基地種別ごとに1つ指定しておけば、その建物をプレイヤーが（その建物
    /// 自身の通常のメニューから）どこにでも建てるだけで、それが自動的にその種別の基地として機能する」
    /// 方式を追加する（見た目の差し替え・Hidden化トリックは一切無い。アセットは常にネイティブの見た目の
    /// まま）。永続化は<see cref="BaseBuildingDesignation"/>（&lt;modDir&gt;\base-buildings.txt）が担当し、
    /// 実際の認識（新規配置イベント→論理MilitaryBase登録）は BasePlacementWatcher.ProcessCreated が
    /// 電力タブのクローンプレハブ判定に続く第二の判定として行う。
    ///
    /// 電力タブのクローンプレハブは本ページの指定の有無に関わらず常にそのまま機能し続ける（変更なし、
    /// フォールバック兼「セットアップ不要ですぐ使える」経路として残る。クラス冒頭の「coexistence」注記）。
    ///
    /// UI構成: OptionsModelAssignPageと同じ制約（ICities.UIHelperBase はスクロール一覧を持たないため、
    /// 検索欄で絞り込んでからドロップダウンで選ぶ方式）に従う。検索欄・「サブスクライブ済みのみ」トグルは
    /// 4行（陸軍/海軍/空軍/ミサイル）で共有し、各行は「種別ラベル付きドロップダウン」「現在の指定」表示
    /// ラベル・「既定に戻す」ボタンの3点で構成する（複製適用・勢力別のような追加概念は無い＝基地種別ごとに
    /// 単一のグローバルな指定のため、勢力ドロップダウンやTypeKeyコンセプトは持ち込まない）。
    ///
    /// ドロップダウンの選択は即座に反映される（OptionsModelAssignPageの「適用」ボタンとは異なり、本ページ
    /// では各行が独立した単一のドロップダウンのため、選ぶ操作自体が「この種別にはこのアセット」という
    /// 唯一の意思表示になる。「（未選択）」を選ぶことは「既定に戻す」ボタンと同じ意味＝指定解除）。
    ///
    /// Task52バグ修正と同じパターン: OptionsMainPanelはMODのOnSettingsUIをOptions画面を開くたびには
    /// 再実行しない（詳細はOptionsRelationsPage.csのクラス冒頭コメント参照）ため、グループパネルの
    /// eventVisibilityChangedを購読し、このMODのOptionsタブが選択されるたびにRefreshFromStateで
    /// ドロップダウン内容・現在の指定表示・isEnabled・ヒントラベルを再同期する。
    ///
    /// 「既存の建物は対象外」: BasePlacementWatcher.ProcessCreatedはCSのEventBuildingCreatedイベント
    /// （新規配置/道路移動含む再作成時にのみ発火）にのみ反応するため、指定した瞬間に既に建っている
    /// 同名アセットの建物が遡って基地化することは無い（本ページのヒントラベルで明示する）。
    ///
    /// 全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    /// </summary>
    internal static class OptionsBaseBuildingPage
    {
        private const string GroupTitle = "基地に使う建物";
        private const string NoSelectionLabel = "（未選択）";

        // 行の並び。UnitAssetBindingsBaseTypesの表示ラベル定数をそのまま流用する（UI文言の一元管理）。
        private static readonly BaseType[] RowTypes = { BaseType.Army, BaseType.Navy, BaseType.AirForce, BaseType.MissileBase };
        private static readonly string[] RowDisplayNames =
        {
            UnitAssetBindings.ArmyBaseDisplayName,
            UnitAssetBindings.NavyBaseDisplayName,
            UnitAssetBindings.AirBaseDisplayName,
            UnitAssetBindings.MissileBaseDisplayName
        };

        private static UICheckBox _customOnlyCheckbox;
        private static UITextField _searchField;
        private static UILabel _countLabel;
        private static UILabel _hintLabel;

        private static readonly UIDropDown[] _rowDropdowns = new UIDropDown[4];
        private static readonly UILabel[] _rowLabels = new UILabel[4];
        private static readonly UIButton[] _rowResetButtons = new UIButton[4];

        private static readonly List<string> _filteredNames = new List<string>();
        private static bool _customOnly = true;
        private static bool _suppressEvents;

        /// <summary>Mod.OnSettingsUIから呼ぶ。渡された helper 配下に「基地に使う建物」グループを構築する。</summary>
        public static void Build(UIHelperBase helper)
        {
            try
            {
                UIHelperBase group = helper.AddGroup(GroupTitle);
                UIComponent groupPanel = (group as UIHelper) != null ? ((UIHelper)group).self as UIComponent : null;

                _customOnlyCheckbox = group.AddCheckbox("サブスクライブ済みのみ", _customOnly, OnCustomOnlyChanged) as UICheckBox;
                _searchField = group.AddTextfield("検索（部分一致）", "", OnSearchTextChanged, OnSearchTextSubmitted) as UITextField;

                if (groupPanel != null)
                {
                    _countLabel = groupPanel.AddUIComponent<UILabel>();
                    _countLabel.textScale = 0.75f;
                    _countLabel.textColor = new Color32(200, 200, 200, 255);
                    _countLabel.text = "";
                }

                object lastControl = null;
                for (int i = 0; i < RowTypes.Length; i++)
                {
                    lastControl = BuildRow(group, groupPanel, i);
                }

                _hintLabel = CreateNoteLabel(lastControl, groupPanel);

                if (groupPanel != null) groupPanel.eventVisibilityChanged += OnGroupVisibilityChanged;

                RefreshFromState();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.Build error: " + e);
            }
        }

        /// <summary>1行分（種別ラベル付きドロップダウン＋現在の指定ラベル＋既定に戻すボタン）を構築する。
        /// 戻り値は最後に生成したコントロール（ヒントラベルをその直後へ並べるためにBuildが使う）。</summary>
        private static object BuildRow(UIHelperBase group, UIComponent groupPanel, int index)
        {
            BaseType type = RowTypes[index];
            string displayName = RowDisplayNames[index];
            int rowIndex = index; // クロージャ捕獲用ローカルコピー

            UIDropDown dd = group.AddDropdown(displayName, new[] { NoSelectionLabel }, 0,
                i => OnAssetSelected(rowIndex, i)) as UIDropDown;
            _rowDropdowns[index] = dd;

            if (groupPanel != null)
            {
                UILabel label = groupPanel.AddUIComponent<UILabel>();
                label.textScale = 0.8f;
                label.textColor = new Color32(200, 200, 200, 255);
                label.wordWrap = true;
                label.autoHeight = true;
                label.width = 500f;
                label.text = "";
                _rowLabels[index] = label;
            }

            object resetObj = group.AddButton("既定に戻す", () => OnResetClick(rowIndex));
            _rowResetButtons[index] = resetObj as UIButton;

            return resetObj;
        }

        /// <summary>ヒントラベルを最後のコントロールの直後（同じ親パネル内）に追加する
        /// （OptionsModelAssignPage.CreateNoteLabelと同じ手法）。</summary>
        private static UILabel CreateNoteLabel(object afterObj, UIComponent fallbackParent)
        {
            try
            {
                UIComponent after = afterObj as UIComponent;
                UIComponent parent = after != null && after.parent != null ? after.parent : fallbackParent;
                if (parent == null) return null;

                UILabel label = parent.AddUIComponent<UILabel>();
                label.textScale = 0.8f;
                label.textColor = new Color32(255, 190, 120, 255);
                label.wordWrap = true;
                label.autoHeight = true;
                label.width = 500f;
                label.text = "";
                return label;
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.CreateNoteLabel error: " + e);
                return null;
            }
        }

        /// <summary>グループパネルのeventVisibilityChangedハンドラ（Task52バグ修正と同じパターン）。
        /// isVisible==trueの時だけRefreshFromStateを呼ぶ。</summary>
        private static void OnGroupVisibilityChanged(UIComponent component, bool isVisible)
        {
            if (!isVisible) return;
            RefreshFromState();
        }

        /// <summary>Build()での初回構築時、および以後Options内でこのMODのタブが選択されるたびに呼ぶ
        /// 共通の再同期処理。新しいコントロールは一切生成しない。例外はここで握りつぶす。</summary>
        private static void RefreshFromState()
        {
            try
            {
                bool stateReady = AssetAssignPanel.HasAnyProps();

                RefreshFilteredNames();
                RefreshAllRowDropdownItems();
                RefreshAllRowLabels();
                RefreshHint(stateReady);

                SetControlsEnabled(stateReady);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.RefreshFromState error: " + e);
            }
        }

        private static void SetControlsEnabled(bool enabled)
        {
            if (_customOnlyCheckbox != null) _customOnlyCheckbox.isEnabled = enabled;
            if (_searchField != null) _searchField.isEnabled = enabled;
            for (int i = 0; i < RowTypes.Length; i++)
            {
                if (_rowDropdowns[i] != null) _rowDropdowns[i].isEnabled = enabled;
                if (_rowResetButtons[i] != null) _rowResetButtons[i].isEnabled = enabled;
            }
        }

        /// <summary>マップ未ロード（メインメニュー等）で建物アセットが1件も無い状況、および本ページの
        /// 使い方（「既存の建物は対象外」「未指定の種別は電力タブから」）を常に案内する。</summary>
        private static void RefreshHint(bool stateReady)
        {
            if (_hintLabel == null) return;

            const string usage = "指定した建物を建てると、その建物が基地として機能します（既存の建物は対象外）。未指定の基地種別は従来どおり電力タブから配置します。";
            _hintLabel.text = stateReady
                ? usage
                : usage + "\n現在利用可能な建物アセットが0件です（メインメニューから開いた場合など）。マップを読み込んだ後にもう一度開くと、サブスクライブ済みの建物が一覧に表示されます。";
        }

        private static void RefreshFilteredNames()
        {
            string filter = _searchField != null ? _searchField.text : null;
            List<string> names = AssetCatalog.GetNames(AssetKind.Building, _customOnly, filter);

            _filteredNames.Clear();
            bool truncated = names.Count > AssetAssignPanel.MaxListItems;
            int count = truncated ? AssetAssignPanel.MaxListItems : names.Count;
            for (int i = 0; i < count; i++) _filteredNames.Add(names[i]);

            if (_countLabel != null)
            {
                _countLabel.text = truncated
                    ? "※ " + names.Count + " 件中 " + AssetAssignPanel.MaxListItems + " 件のみ表示（検索で絞り込んでください）"
                    : names.Count + " 件";
            }
        }

        /// <summary>4行すべてのドロップダウンのitems/selectedIndexを、現在のフィルタ結果＋各行の現在の
        /// 指定（BaseBuildingDesignation.TryGet）に合わせて再構築する。_suppressEventsで囲むため、
        /// 「検索/トグルの都合でフィルタから外れた指定」がここでOnAssetSelected経由で誤ってClearされる
        /// ことは無い（実際の指定はBaseBuildingDesignation側の値のみを信頼し、ドロップダウンの見た目上の
        /// selectedIndexとは独立に扱う）。</summary>
        private static void RefreshAllRowDropdownItems()
        {
            string[] items = new string[_filteredNames.Count + 1];
            items[0] = NoSelectionLabel;
            for (int i = 0; i < _filteredNames.Count; i++) items[i + 1] = _filteredNames[i];

            _suppressEvents = true;
            try
            {
                for (int r = 0; r < RowTypes.Length; r++)
                {
                    UIDropDown dd = _rowDropdowns[r];
                    if (dd == null) continue;

                    dd.items = items;

                    string current;
                    int selectedIndex = 0;
                    if (BaseBuildingDesignation.TryGet(RowTypes[r], out current))
                    {
                        int idx = _filteredNames.IndexOf(current);
                        if (idx >= 0) selectedIndex = idx + 1;
                    }
                    dd.selectedIndex = selectedIndex;
                }
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private static void RefreshAllRowLabels()
        {
            for (int i = 0; i < RowTypes.Length; i++) RefreshRowLabel(i);
        }

        private static void RefreshRowLabel(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= RowTypes.Length) return;
            UILabel label = _rowLabels[rowIndex];
            if (label == null) return;

            string current;
            label.text = BaseBuildingDesignation.TryGet(RowTypes[rowIndex], out current)
                ? "現在の指定: " + current
                : "現在の指定: （未設定。電力タブの複製建物で配置します）";
        }

        private static void OnCustomOnlyChanged(bool value)
        {
            try
            {
                _customOnly = value;
                RefreshFilteredNames();
                RefreshAllRowDropdownItems();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.OnCustomOnlyChanged error: " + e);
            }
        }

        private static void OnSearchTextChanged(string value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshFilteredNames();
                RefreshAllRowDropdownItems();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.OnSearchTextChanged error: " + e);
            }
        }

        /// <summary>AddTextfieldのOnTextSubmitted用no-op（OnTextChangedだけで絞り込み済みのため）。</summary>
        private static void OnSearchTextSubmitted(string value)
        {
        }

        /// <summary>行のドロップダウン選択が変わった時の処理。index0（未選択）を選ぶことは
        /// 「既定に戻す」ボタンと同じ意味（指定解除）として扱う。それ以外はフィルタ後の一覧から
        /// 該当アセット名を指定として直ちに保存する（このページには「適用」ボタンは無い＝選ぶ操作自体が
        /// 唯一の意思表示のため）。</summary>
        private static void OnAssetSelected(int rowIndex, int selectedIndex)
        {
            try
            {
                if (_suppressEvents) return;
                if (rowIndex < 0 || rowIndex >= RowTypes.Length) return;

                BaseType type = RowTypes[rowIndex];
                if (selectedIndex <= 0)
                {
                    BaseBuildingDesignation.Clear(type);
                }
                else if (selectedIndex - 1 < _filteredNames.Count)
                {
                    BaseBuildingDesignation.Set(type, _filteredNames[selectedIndex - 1]);
                }

                RefreshRowLabel(rowIndex);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.OnAssetSelected error: " + e);
            }
        }

        private static void OnResetClick(int rowIndex)
        {
            try
            {
                if (rowIndex < 0 || rowIndex >= RowTypes.Length) return;

                BaseBuildingDesignation.Clear(RowTypes[rowIndex]);
                RefreshAllRowDropdownItems(); // ドロップダウンの見た目上の選択も「（未選択）」へ戻す
                RefreshRowLabel(rowIndex);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsBaseBuildingPage.OnResetClick error: " + e);
            }
        }
    }
}
