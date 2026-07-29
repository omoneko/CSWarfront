using System;
using ColossalFramework.UI;
using CSWarfront.Core;
using CSWarfront.Game;
using ICities;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// OptionsModelAssignPage のうち、Task70で新設した「全て初期化」ボタンと「セット」プリセット行
    /// （スロット1-3の保存/読込）だけを分離した partial class（OptionsModelAssignPage.cs 側が既に
    /// 500行制限に達しているため、新規追加分は最初からこちらへ置く。AssetAssignPanelPresets.csと
    /// 同じ役割を Options サブページ側に持たせたもの）。実際の永続化処理は UnitAssetBindings.ClearAll/
    /// SaveToSlot/LoadFromSlot（Game/UnitAssetBindingsPresets.cs）が一元的に担い、このファイルはUI
    /// （ボタン/ドロップダウンの構築・クリックハンドラ・フィードバック表示）のみを持つ。フローティング
    /// パネル側（AssetAssignPanelPresets.cs）と完全に同じAPIを呼ぶため、永続化ロジック自体は二重実装
    /// しない。全メソッドはメインスレッド専用（Unity UI API呼び出しのため）。
    ///
    /// ICities.UIHelperBaseにはUIListBox相当が無いため、フローティングパネルと同じ構成（ドロップダウン
    /// +保存/読込ボタン）を素直にAddDropdown/AddButtonで構築する。「現在の割り当て」プレビュー
    /// （OptionsModelAssignPage.BuildBindingPreview）と同じ手法で、ボタンの後ろへ生のUILabelを
    /// 直接追加してフィードバックメッセージ表示に使う（CreateNoteLabelを再利用）。
    /// </summary>
    internal static partial class OptionsModelAssignPage
    {
        private static UIButton _clearAllButton;
        private static UIDropDown _presetSlotDropdown;
        private static UIButton _presetSaveButton;
        private static UIButton _presetLoadButton;
        private static UILabel _presetMessageLabel;

        /// <summary>ドロップダウンの選択インデックス(0..2)からスロット番号(1..3)へ変換する。</summary>
        private static int SelectedPresetSlot
        {
            get { return _presetSlotDropdown != null ? _presetSlotDropdown.selectedIndex + 1 : 1; }
        }

        /// <summary>Build()から呼ぶ。「全て初期化」ボタン、「セット」ドロップダウン+保存/読込ボタン、
        /// フィードバック用ラベルを、渡された group（モデル割り当てグループ）の末尾に追加する。</summary>
        internal static void BuildPresetsSection(UIHelperBase group)
        {
            _clearAllButton = group.AddButton("全て初期化", OnClearAllClick) as UIButton;

            string[] presetLabels = BuildPresetSlotLabels();
            _presetSlotDropdown = group.AddDropdown("セット", presetLabels, 0, OnPresetSlotChanged) as UIDropDown;
            _presetSaveButton = group.AddButton("保存", OnSaveSlotClick) as UIButton;
            object loadButtonObj = group.AddButton("読込", OnLoadSlotClick);
            _presetLoadButton = loadButtonObj as UIButton;

            _presetMessageLabel = CreateNoteLabel(loadButtonObj);
        }

        /// <summary>「1」「2（空）」のように、UnitAssetBindings.SlotExists（安価なFile.Existsのみ）で
        /// 存在しないスロットへ「（空）」を付与したラベルを構築する（AssetAssignPanelPresets.
        /// BuildPresetSlotLabelsと同じ内容）。</summary>
        private static string[] BuildPresetSlotLabels()
        {
            string[] labels = new string[3];
            for (int i = 0; i < 3; i++)
            {
                int slot = i + 1;
                labels[i] = UnitAssetBindings.SlotExists(slot) ? slot.ToString() : slot + "（空）";
            }
            return labels;
        }

        /// <summary>スロットドロップダウンのラベルだけを、選択位置を保ったまま再構築する。
        /// RefreshFromState()（Options内でこのMODのタブが選択されるたび）、および保存/読込直後に呼ぶ。</summary>
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

        /// <summary>AddDropdownのeventCallback用no-op（保存/読込ボタンが押された時点でselectedIndexを
        /// 読むだけで足りるため、OnCopyScopeChangedと同じ方針）。</summary>
        private static void OnPresetSlotChanged(int value)
        {
        }

        private static void SetPresetMessage(string text)
        {
            if (_presetMessageLabel != null) _presetMessageLabel.text = text;
        }

        /// <summary>「全て初期化」ボタン。UnitAssetBindings.ClearAllで全割り当てを既定へ戻し、
        /// 削除件数をメッセージ表示する。1件以上削除した場合のみ既存のApply/Reset/CopyApplyと同じ
        /// 反映処理（ラベル再構築＋見た目破棄）を行う（typeIdx固有のWarnIfNoOwnedBaseForKeyは
        /// 一括操作には該当しないため呼ばない）。</summary>
        private static void OnClearAllClick()
        {
            try
            {
                int removed = UnitAssetBindings.ClearAll();
                SetPresetMessage(removed > 0
                    ? "全割り当てを初期化しました（" + removed + "件）"
                    : "初期化対象の割り当てがありませんでした（既に既定のままです）");

                if (removed > 0) RefreshAfterBulkChange();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnClearAllClick error: " + e);
            }
        }

        private static void OnSaveSlotClick()
        {
            try
            {
                int slot = SelectedPresetSlot;
                bool ok = UnitAssetBindings.SaveToSlot(slot);
                SetPresetMessage(ok ? "セット" + slot + "に保存しました" : "セット" + slot + "への保存に失敗しました");
                if (ok) RefreshPresetSlotLabels();
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnSaveSlotClick error: " + e);
            }
        }

        /// <summary>読込はテーブル全体を置き換える（UnitAssetBindings.LoadFromSlotのREPLACEセマンティクス）
        /// ため、成功時は勢力/種別ドロップダウンのラベル・現在の割り当て表示・見た目を全て再同期する。</summary>
        private static void OnLoadSlotClick()
        {
            try
            {
                int slot = SelectedPresetSlot;
                bool ok = UnitAssetBindings.LoadFromSlot(slot);
                SetPresetMessage(ok ? "セット" + slot + "を読み込みました（既存の割り当てを置き換えました）" : "セット" + slot + "は空です");

                if (ok)
                {
                    RefreshPresetSlotLabels();
                    RefreshAfterBulkChange();
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsModelAssignPage.OnLoadSlotClick error: " + e);
            }
        }

        /// <summary>全て初期化/セット読込 共通の反映処理（ApplyBindingChangeの一括操作版。単一typeIdxに
        /// 紐づくWarnIfNoOwnedBaseForKeyは呼ばない＝複数キーが変わりうるため個別警告は意味を成さない）。</summary>
        private static void RefreshAfterBulkChange()
        {
            RefreshTypeKeyLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
            RefreshCurrentBinding();

            UnitVisuals.DestroyAll();
            BaseVisuals.DestroyAll();
            ModConfig.Log("OptionsModelAssignPage: 一括変更を反映するため UnitVisuals.DestroyAll()/BaseVisuals.DestroyAll() を実行しました");
        }
    }
}
