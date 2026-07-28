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
    /// 全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。OnSettingsUIはOptions画面を開く
    /// たびに再実行されうるため、Build()は毎回すべてのコントロールを新規生成する前提で書かれている
    /// （古い参照は次のBuild()呼び出しで静的フィールドごと上書きされる。Mod.cs Task40時点の設計を踏襲）。
    /// </summary>
    internal static class OptionsModelAssignPage
    {
        private const string GroupTitle = "モデル割り当て";
        private const string NoSelectionLabel = "（未選択）";

        private static UIDropDown _factionDropdown;
        private static UIDropDown _typeKeyDropdown;
        private static UIDropDown _assetKindDropdown;
        private static UITextField _searchField;
        private static UIDropDown _assetDropdown;
        private static UIDropDown _copyScopeDropdown;
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

                group.AddCheckbox("サブスクライブ済みのみ", _customOnly, OnCustomOnlyChanged);
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

                group.AddButton("適用", OnApplyClick);
                group.AddButton("既定に戻す", OnResetClick);
                object copyButtonObj = group.AddButton("複製適用", OnCopyApplyClick);

                _hintLabel = CreateNoteLabel(copyButtonObj);

                RefreshAll();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.Build error: " + e);
            }
        }

        /// <summary>「現在の割り当て」ラベルとサムネイルをグループパネルへ直接追加する（AddDropdown等の
        /// 後に呼ぶことで、その直後に表示される位置に並ぶ。AssetAssignPanelFaction.BuildThumbnailSpriteと
        /// 同じ寸法・考え方）。</summary>
        private static void BuildBindingPreview(UIComponent groupPanel)
        {
            _currentBindingLabel = groupPanel.AddUIComponent<UILabel>();
            _currentBindingLabel.textScale = 0.8f;
            _currentBindingLabel.textColor = new Color32(200, 200, 200, 255);
            _currentBindingLabel.wordWrap = true;
            _currentBindingLabel.autoHeight = true;
            _currentBindingLabel.width = 500f;
            _currentBindingLabel.text = "";

            _thumbnailSprite = groupPanel.AddUIComponent<UISprite>();
            _thumbnailSprite.size = new Vector2(96f, 96f);
            _thumbnailSprite.isVisible = false;
        }

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

        private static void BuildTypeKeys()
        {
            List<string> keys = new List<string>();
            foreach (UnitType t in LandUnitRoster.All()) keys.Add(t.TypeKey);
            _typeKeys = keys.ToArray();
        }

        private static string[] BuildTypeKeyLabels()
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

        private static void RefreshAssetDropdown()
        {
            if (_assetDropdown == null) return;

            string filter = _searchField != null ? _searchField.text : null;
            List<string> names = AssetCatalog.GetNames(SelectedAssetKind, _customOnly, filter);

            _filteredAssetNames.Clear();
            bool truncated = names.Count > AssetAssignPanel.MaxListItems;
            int count = truncated ? AssetAssignPanel.MaxListItems : names.Count;
            for (int i = 0; i < count; i++) _filteredAssetNames.Add(names[i]);

            string[] items = new string[_filteredAssetNames.Count + 1];
            items[0] = NoSelectionLabel;
            for (int i = 0; i < _filteredAssetNames.Count; i++) items[i + 1] = _filteredAssetNames[i];

            _suppressEvents = true;
            try
            {
                _assetDropdown.items = items;
                _assetDropdown.selectedIndex = 0;
            }
            finally
            {
                _suppressEvents = false;
            }

            if (_countLabel != null)
            {
                _countLabel.text = truncated
                    ? "※ " + names.Count + " 件中 " + AssetAssignPanel.MaxListItems + " 件のみ表示（検索で絞り込んでください）"
                    : names.Count + " 件";
            }
        }

        private static void RefreshCurrentBinding()
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

        private static void RefreshThumbnail(AssetKind kind, string name)
        {
            if (_thumbnailSprite == null) return;

            UITextureAtlas atlas;
            string spriteName;
            if (!string.IsNullOrEmpty(name) && AssetCatalog.TryGetThumbnail(kind, name, out atlas, out spriteName))
            {
                _thumbnailSprite.atlas = atlas;
                _thumbnailSprite.spriteName = spriteName;
                _thumbnailSprite.isVisible = true;
            }
            else
            {
                _thumbnailSprite.isVisible = false;
            }
        }

        /// <summary>マップ未ロード（メインメニュー等）でアセットが1件も無い状況をヒントラベルへ表示する
        /// （AssetAssignPanel.HasAnyProps=Rescan込みの4種類横断チェックを再利用、Task40の挙動を踏襲）。</summary>
        private static void RefreshHint()
        {
            if (_hintLabel == null) return;
            _hintLabel.text = AssetAssignPanel.HasAnyProps()
                ? ""
                : "現在利用可能なアセット（プロップ/建物/車両/樹木）が0件です（メインメニューから開いた場合など）。マップを読み込んだ後にもう一度開くと、サブスクライブ済みのアセットが一覧に表示されます。";
        }

        private static void RefreshAll()
        {
            AssetCatalog.Rescan();
            RefreshTypeKeyLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
            RefreshAssetDropdown();
            RefreshCurrentBinding();
            RefreshHint();
        }

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
            ModConfig.Log("OptionsModelAssignPage: 割り当て変更を反映するため UnitVisuals.DestroyAll() を実行しました");
        }
    }
}
