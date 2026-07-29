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
    /// Task47: Mod Options（Game/Mod.cs、OnSettingsUI）内に直接構築する「モデル割り当て」サブページ。
    /// Task40〜41までは「モデル割り当てを開く」ボタンを押すと独立したフローティングパネル
    /// （AssetAssignPanel）を開いていたが、ユーザー要望によりOptions画面のグループ内にコントロール
    /// 一式を直接並べる方式（サブページ）へ変更した。
    ///
    /// ICities.UIHelperBase は AddGroup/AddDropdown/AddCheckbox/AddTextfield/AddButton/AddSpace の
    /// 6メソッドしか持たない（スクロール可能な一覧＝AssetAssignPanelのUIListBox相当が無い）ため、
    /// アセット一覧は「検索欄で絞り込んでからドロップダウンで選ぶ」方式に縮退している（要件で明示された
    /// 「対応する制御が無ければドロップダウンで代替する」方針）。件数表示ラベルで、ドロップダウンに
    /// 入り切らなかった件数（AssetAssignPanel.MaxListItems=300件超）をユーザーに伝える。
    ///
    /// UIHelperBase実装の実体はAssembly-CSharpのグローバル名前空間クラス「UIHelper」で、
    /// `object AddXxx(...)` の戻り値は実際には生成したUIコンポーネント自身（UIDropDown/UITextField等）
    /// であり、`public object self { get; }` は AddGroup が返す子ヘルパーが包む「グループの中身パネル」
    /// （UIComponent）を返す（いずれもColossalManaged.dll/Assembly-CSharp.dllのILを
    /// PowerShellリフレクションで逆アセンブルし、newobj/callvirt命令の対象型を確認済み）。
    /// これにより、ドロップダウン等の戻り値をキャストしてitems/selectedIndexを後から書き換えたり、
    /// group.selfを起点にUILabel/UISpriteのような「現在の割り当て」プレビュー表示をヘルパーの外側から
    /// 直接子として追加できる（Mod.cs Task40時点のCreateHintLabelと同じ手法の一般化）。
    /// raw UIComponentをAddUIComponentする呼び出しを他のAdd*呼び出しの間に挟むと、同じ親パネルの
    /// 子として「呼んだ順」に並ぶため、この方式のままレイアウト順を制御できる。
    ///
    /// このページ自体はマップ未ロード（メインメニュー）から開かれる可能性があり、その場合
    /// PrefabCollection&lt;PropInfo/BuildingInfo/VehicleInfo/TreeInfo&gt; がまだpopulateされていない
    /// ため一覧は0件になる。AssetAssignPanel.HasAnyProps()（Rescan込みの4種類横断チェック）を再利用して
    /// その旨をヒントラベルに表示する（Task40時点のMod.csの挙動を踏襲）。
    ///
    /// 実際の複製ロジック（Task47「複製適用」）は UnitAssetBindings.CopyTo が一元的に持ち、このクラスは
    /// スコープドロップダウンの選択値を渡すだけ（AssetAssignPanelCopy.cs のフローティングパネル側と
    /// 完全に同じ呼び出し方）。
    ///
    /// 全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    ///
    /// Task52バグ修正: 上の段落は「OnSettingsUIはOptions画面を開くたびに再実行されうる」という
    /// 誤った前提で書かれていた。実際にはCSのOptionsMainPanel（Assembly-CSharp.dll、ILSpyで逆コンパイル
    /// して確認済み。詳細はOptionsRelationsPage.csのクラス冒頭コメント参照）は各MODのOnSettingsUIを
    /// OptionsMainPanel.Awake()（通常は都市読み込み前のメインメニューの時点）でただ一度だけ呼び、
    /// 以後はロケール変更かMOD有効/無効の変更の時だけ再構築する。つまりBuild()はAssetCatalogが空
    /// （都市未読み込み）の状態で一度だけ実行され、以後どれだけ都市を読み込み直してOptions画面を開き
    /// 直しても、Build()自体は二度と呼ばれない。
    ///
    /// 修正はOptionsRelationsPageと同じパターン: グループパネルのeventVisibilityChangedを購読し
    /// （Options画面でこのMODのタブが選択されるたびに、配下の全コンポーネントへ伝播する形で発火する。
    /// UIComponent、ColossalManaged.dllを逆コンパイルして確認済み）、発火のたびに
    /// RefreshFromState()で（1)AssetCatalogを再走査した上で都市読み込み状況を判定し、
    /// (2)ユニット種別/アセットのドロップダウン内容と「現在の割り当て」表示を再構築し、
    /// (3)全コントロールのisEnabledを判定結果へ同期し、(4)ヒントラベルを更新する。
    /// 新しいコントロールは一切生成しない（Build()で一度だけ生成した既存のフィールドを更新するのみ、
    /// 二重生成防止）。
    /// </summary>
    internal static partial class OptionsModelAssignPage
    {
        private const string GroupTitle = "モデル割り当て";
        private const string NoSelectionLabel = "（未選択）";

        private static UIDropDown _factionDropdown;
        private static UIDropDown _typeKeyDropdown;
        private static UIDropDown _assetKindDropdown;
        private static UICheckBox _customOnlyCheckbox;
        private static UITextField _searchField;
        private static UIDropDown _assetDropdown;
        private static UIDropDown _copyScopeDropdown;
        private static UIButton _applyButton;
        private static UIButton _resetButton;
        private static UIButton _copyApplyButton;
        private static UILabel _currentBindingLabel;
        private static UISprite _thumbnailSprite;
        private static UILabel _countLabel;
        private static UILabel _hintLabel;

        private static string[] _typeKeys;
        private static readonly List<string> _filteredAssetNames = new List<string>();
        private static bool _customOnly = true;
        private static bool _suppressEvents;

        private static byte SelectedFactionId
        {
            get { return _factionDropdown != null ? (byte)_factionDropdown.selectedIndex : (byte)0; }
        }

        private static AssetKind SelectedAssetKind
        {
            get { return _assetKindDropdown != null ? (AssetKind)_assetKindDropdown.selectedIndex : AssetKind.Prop; }
        }

        /// <summary>Mod.OnSettingsUIから呼ぶ。渡された helper 配下に「モデル割り当て」グループを構築する。</summary>
        public static void Build(UIHelperBase helper)
        {
            try
            {
                UIHelperBase group = helper.AddGroup(GroupTitle);
                UIComponent groupPanel = (group as UIHelper) != null ? ((UIHelper)group).self as UIComponent : null;
                if (groupPanel == null)
                {
                    ModConfig.LogError("OptionsModelAssignPage.Build: グループパネルの取得に失敗したため、現在の割り当て表示/サムネイルは省略します。");
                }

                _factionDropdown = group.AddDropdown("勢力", WarfrontSettings.FactionNames, 0, OnFactionChanged) as UIDropDown;

                BuildTypeKeys();
                _typeKeyDropdown = group.AddDropdown("ユニット種別", BuildTypeKeyLabels(), 0, OnTypeKeyChanged) as UIDropDown;

                if (groupPanel != null) BuildBindingPreview(groupPanel);

                string[] kindLabels = new string[AssetKindUtil.All.Length];
                for (int i = 0; i < AssetKindUtil.All.Length; i++) kindLabels[i] = AssetKindUtil.DisplayNameJa(AssetKindUtil.All[i]);
                _assetKindDropdown = group.AddDropdown("アセット種別", kindLabels, 0, OnAssetKindChanged) as UIDropDown;

                _customOnlyCheckbox = group.AddCheckbox("サブスクライブ済みのみ", _customOnly, OnCustomOnlyChanged) as UICheckBox;
                // Task47: AddTextfieldのOnTextSubmittedは未使用（OnTextChangedだけで十分）だが、
                // UIHelper実装がコールバックのnullガードを持つか未検証のため、nullは渡さずno-opを渡す
                // （IL上はeventTextSubmitted購読が無条件に見えたため、安全側に倒す）。
                _searchField = group.AddTextfield("検索（部分一致）", "", OnSearchTextChanged, OnSearchTextSubmitted) as UITextField;
                _assetDropdown = group.AddDropdown("アセット", new[] { NoSelectionLabel }, 0, OnAssetSelected) as UIDropDown;

                if (groupPanel != null)
                {
                    _countLabel = groupPanel.AddUIComponent<UILabel>();
                    _countLabel.textScale = 0.75f;
                    _countLabel.textColor = new Color32(200, 200, 200, 255);
                    _countLabel.text = "";
                }

                string[] copyLabels = new string[CopyScopeUtil.All.Length];
                for (int i = 0; i < CopyScopeUtil.All.Length; i++) copyLabels[i] = CopyScopeUtil.DisplayNameJa(CopyScopeUtil.All[i]);
                // OnCopyApplyClickが押された時点でselectedIndexを読むだけなので変更通知は不要だが、
                // 上記と同じ理由でnullではなくno-opコールバックを渡す。
                _copyScopeDropdown = group.AddDropdown("複製適用の範囲", copyLabels, 0, OnCopyScopeChanged) as UIDropDown;

                _applyButton = group.AddButton("適用", OnApplyClick) as UIButton;
                _resetButton = group.AddButton("既定に戻す", OnResetClick) as UIButton;
                object copyButtonObj = group.AddButton("複製適用", OnCopyApplyClick);
                _copyApplyButton = copyButtonObj as UIButton;

                _hintLabel = CreateNoteLabel(copyButtonObj);

                // Task70: 「全て初期化」ボタンと「セット」プリセット行（実体はOptionsModelAssignPagePresets.cs、
                // 500行制限のため新規追加分は最初からそちらへ置いた）。
                BuildPresetsSection(group);

                // Task52バグ修正: このMODのOptionsタブが選択される（＝祖先コンポーネントのisVisibleが
                // trueへ変わり、それがこのグループパネルまで伝播する）たびに発火するeventVisibilityChanged
                // を購読し、その時点の状態でRefreshFromStateを呼ぶ（クラス冒頭のコメント参照）。
                // Build()自体はOptionsMainPanel.Awake()等で一度しか呼ばれないため、これが無いと
                // メインメニューで一度だけ構築された無効化済みのUIが都市読み込み後も更新されない。
                if (groupPanel != null) groupPanel.eventVisibilityChanged += OnGroupVisibilityChanged;

                RefreshFromState();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.Build error: " + e);
            }
        }

        // Task70: BuildBindingPreview/RefreshAssetDropdown/RefreshCurrentBinding/RefreshThumbnail は
        // 500行制限のため OptionsModelAssignPageBinding.cs（同じpartial class）へ分離した。

        /// <summary>ボタンの親パネルへ状態表示用のUILabelを追加する（Task40時点のMod.CreateHintLabelと
        /// 同じ手法）。取得に失敗しても致命的ではない（ログのみ表示できなくなるだけ）。</summary>
        private static UILabel CreateNoteLabel(object afterObj)
        {
            try
            {
                UIComponent after = afterObj as UIComponent;
                if (after == null || after.parent == null) return null;

                UILabel label = after.parent.AddUIComponent<UILabel>();
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
                ModConfig.LogError("OptionsModelAssignPage.CreateNoteLabel error: " + e);
                return null;
            }
        }

        /// <summary>Task66: 先頭に基地種別ごとの4キー（陸軍/海軍/空軍/ミサイル、UnitAssetBindings.
        /// ArmyBaseTypeKey等）を追加した上で、LandUnitRoster.All()（カテゴリ宣言順→Tier1〜5）から35件の
        /// TypeKeyを構築する（計39件）。AssetAssignPanelControls.BuildTypeKeysと同じ方針（フローティング
        /// パネル/Optionsサブページの両方に同じ4エントリを表示する。Task60時点は単一キーだった）。</summary>
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

        /// <summary>Task66: 基地種別キー（陸軍/海軍/空軍/ミサイル）は生のキー文字列ではなく
        /// UnitAssetBindings.DisplayNameForBaseKeyの日本語ラベルを表示する
        /// （AssetAssignPanelControls.BuildDropdownLabelsと同じ方針）。割り当ての解決も種別別キー→
        /// 旧統合キーの順（UnitAssetBindings.TryGetEffective）で行う。</summary>
        private static string[] BuildTypeKeyLabels()
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
                    : "(既定)";
                string displayKey = UnitAssetBindings.DisplayNameForBaseKey(_typeKeys[i]);
                labels[i] = displayKey + " → " + suffix;
            }
            return labels;
        }

        private static void RefreshTypeKeyLabels(int selectedIndex)
        {
            if (_typeKeyDropdown == null || _typeKeys == null) return;

            _suppressEvents = true;
            try
            {
                _typeKeyDropdown.items = BuildTypeKeyLabels();
                if (selectedIndex >= 0 && selectedIndex < _typeKeys.Length) _typeKeyDropdown.selectedIndex = selectedIndex;
                else if (_typeKeys.Length > 0) _typeKeyDropdown.selectedIndex = 0;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        /// <summary>マップ未ロード（メインメニュー等）でアセットが1件も無い状況をヒントラベルへ表示する。
        /// stateReadyはAssetAssignPanel.HasAnyProps()（Rescan込みの4種類横断チェック、Task40の挙動を踏襲）
        /// の結果を呼び出し元（RefreshFromState）から受け取る。ここで再度HasAnyProps()を呼ぶと
        /// AssetCatalog.Rescan()が二重に走ってしまうため（Rescanはキャッシュを空にするだけで、直後の
        /// GetNamesが4種類とも再走査し直すことになり無駄）、呼び出し元で1回だけ判定させる。</summary>
        private static void RefreshHint(bool stateReady)
        {
            if (_hintLabel == null) return;
            _hintLabel.text = stateReady
                ? ""
                : "現在利用可能なアセット（プロップ/建物/車両/樹木）が0件です（メインメニューから開いた場合など）。マップを読み込んだ後にもう一度開くと、サブスクライブ済みのアセットが一覧に表示されます。";
        }

        /// <summary>グループパネルのeventVisibilityChangedハンドラ（Task52バグ修正）。
        /// isVisible==trueの時だけ（＝Options内でこのMODのタブが選択された/表示された時だけ）
        /// RefreshFromStateを呼ぶ。非表示化(false)の際は何もしない
        /// （OptionsRelationsPage.OnGroupVisibilityChangedと同じ方針）。</summary>
        private static void OnGroupVisibilityChanged(UIComponent component, bool isVisible)
        {
            if (!isVisible) return;
            RefreshFromState();
        }

        /// <summary>Task52バグ修正: Build()での初回構築時、および以後Options内でこのMODのタブが
        /// 選択されるたびに呼ぶ共通の再同期処理。AssetAssignPanel.HasAnyProps()（内部でAssetCatalog.Rescan()
        /// を行った上で4種類横断チェックする、Task40から使っている既存メソッド）で現在都市が読み込まれて
        /// いるかを判定し、ユニット種別/アセットのドロップダウン内容・現在の割り当て表示・ヒントラベルを
        /// 最新化した上で、全コントロールのisEnabledを判定結果へ同期する。新しいコントロールは一切
        /// 生成しない（Build()で一度だけ生成した既存フィールドを更新するのみ＝二重生成防止）。
        /// 例外はここで握りつぶし、UIコールバックからゲームループへ例外を伝播させない。</summary>
        private static void RefreshFromState()
        {
            try
            {
                bool stateReady = AssetAssignPanel.HasAnyProps();

                RefreshTypeKeyLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
                RefreshAssetDropdown();
                RefreshCurrentBinding();
                RefreshHint(stateReady);
                RefreshPresetSlotLabels(); // Task70: タブ表示のたびに空スロット表示を最新化

                SetControlsEnabled(stateReady);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.RefreshFromState error: " + e);
            }
        }

        /// <summary>都市読み込み状況（stateReady）に応じて、全コントロールのisEnabledを一括同期する
        /// （OptionsRelationsPage.RefreshFromStateのisEnabled同期と同じ考え方をコントロール一式へ拡張した
        /// もの）。stateReady==falseの間（メインメニュー等でアセットが1件も無い）はユーザーが操作しても
        /// 意味のある結果にならないため、無効化した上でヒントラベル（RefreshHint）で理由を説明する。</summary>
        private static void SetControlsEnabled(bool enabled)
        {
            if (_factionDropdown != null) _factionDropdown.isEnabled = enabled;
            if (_typeKeyDropdown != null) _typeKeyDropdown.isEnabled = enabled;
            if (_assetKindDropdown != null) _assetKindDropdown.isEnabled = enabled;
            if (_customOnlyCheckbox != null) _customOnlyCheckbox.isEnabled = enabled;
            if (_searchField != null) _searchField.isEnabled = enabled;
            if (_assetDropdown != null) _assetDropdown.isEnabled = enabled;
            if (_copyScopeDropdown != null) _copyScopeDropdown.isEnabled = enabled;
            if (_applyButton != null) _applyButton.isEnabled = enabled;
            if (_resetButton != null) _resetButton.isEnabled = enabled;
            if (_copyApplyButton != null) _copyApplyButton.isEnabled = enabled;
            if (_clearAllButton != null) _clearAllButton.isEnabled = enabled; // Task70
            if (_presetSlotDropdown != null) _presetSlotDropdown.isEnabled = enabled;
            if (_presetSaveButton != null) _presetSaveButton.isEnabled = enabled;
            if (_presetLoadButton != null) _presetLoadButton.isEnabled = enabled;
        }

        // Task70: OnFactionChanged/OnTypeKeyChanged/OnAssetKindChanged/OnCustomOnlyChanged/
        // OnSearchTextChanged/OnSearchTextSubmitted/OnCopyScopeChanged/OnAssetSelected は
        // 500行制限のため OptionsModelAssignPageEvents.cs（同じpartial class）へ分離した。

        private static void OnApplyClick()
        {
            try
            {
                if (_typeKeyDropdown == null || _typeKeys == null || _assetDropdown == null) return;

                int typeIdx = _typeKeyDropdown.selectedIndex;
                if (typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

                int assetIdx = _assetDropdown.selectedIndex;
                if (assetIdx < 1 || assetIdx - 1 >= _filteredAssetNames.Count)
                {
                    ModConfig.Log("OptionsModelAssignPage: 適用スキップ（アセットが未選択）");
                    return;
                }

                string typeKey = _typeKeys[typeIdx];
                string assetName = _filteredAssetNames[assetIdx - 1];
                AssetKind kind = SelectedAssetKind;

                UnitAssetBindings.Set(SelectedFactionId, typeKey, kind, assetName);
                ApplyBindingChange(typeIdx);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnApplyClick error: " + e);
            }
        }

        private static void OnResetClick()
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
                ModConfig.LogError("OptionsModelAssignPage.OnResetClick error: " + e);
            }
        }

        /// <summary>「複製適用」ボタン。現在選択中(勢力,ユニット種別)の割り当てを、選択中スコープの
        /// 全(勢力,種別)へ複製する（UnitAssetBindings.CopyTo、AssetAssignPanelCopy.csと同じ呼び出し方）。</summary>
        private static void OnCopyApplyClick()
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
                if (written > 0) ApplyBindingChange(typeIdx);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnCopyApplyClick error: " + e);
            }
        }

        /// <summary>割り当て変更（適用/既定に戻す/複製適用 共通）の反映処理。ラベル再構築と現在の割り当て
        /// 表示更新の上で、既存の見た目を全破棄する（AssetAssignPanelControls.ApplyBindingChangeと同じ方針:
        /// 次回SyncでUnitMeshSource経由の新しい割り当てが解決し直される）。</summary>
        private static void ApplyBindingChange(int typeIdx)
        {
            RefreshTypeKeyLabels(typeIdx);
            RefreshCurrentBinding();

            UnitVisuals.DestroyAll();
            BaseVisuals.DestroyAll(); // Task60: 拠点の勢力別オーバーレイも破棄し、次回Syncで再解決させる
            ModConfig.Log("OptionsModelAssignPage: 割り当て変更を反映するため UnitVisuals.DestroyAll()/BaseVisuals.DestroyAll() を実行しました");

            WarnIfNoOwnedBaseForKey(typeIdx);
        }

        /// <summary>Task66バグ調査対応: AssetAssignPanelControls.WarnIfNoOwnedBaseForKeyと同じ目的
        /// （適用先が基地種別キーで、選択中の勢力がその種別の拠点を1つも所有していない場合、割り当ては
        /// 保存されるが見た目には何も反映されないため、ユーザーへ明示的に案内する）。</summary>
        private static void WarnIfNoOwnedBaseForKey(int typeIdx)
        {
            if (_typeKeys == null || typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

            BaseType baseType;
            if (!UnitAssetBindings.TryGetBaseTypeForKey(_typeKeys[typeIdx], out baseType)) return;
            if (MilitaryManager.HasOwnedBaseOfType(SelectedFactionId, baseType)) return;

            string note = "（この勢力が所有する" + UnitAssetBindings.DisplayNameForBaseKey(_typeKeys[typeIdx]) +
                "が現在ありません。拠点を建設/所属変更すると反映されます）";
            ModConfig.Log("OptionsModelAssignPage: " + note);
            if (_currentBindingLabel != null) _currentBindingLabel.text += "\n" + note;
        }
    }
}
