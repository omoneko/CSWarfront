using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// 「モデル設定」パネル（Task36）。ユニット種別（TypeKey）ごとに、現在サブスクライブしている
    /// プロップ（Workshopアセット含む）を見た目のモデルとして割り当てるUI。BaseInfoPanel の
    /// 「モデル設定」ボタン（Game/UI/BaseInfoPanelModelButton.cs）から開く、独立した常設パネル
    /// （UnitInfoPanel/BaseInfoPanelと同じ「UIView直下に1枚だけ生成し、isVisibleで出し入れする」方式）。
    /// 画面中央固定配置（ドラッグ追従は本タスクの要件外）。
    ///
    /// 500行制限のため、ドロップダウン/検索/一覧/適用ボタンまわりの構築とイベントハンドラは
    /// AssetAssignPanelControls.cs（同じ partial class）に分離している（BaseInfoPanel/
    /// BaseInfoPanelProduction と同じ方針）。このファイルはパネルの生成・破棄・骨格レイアウトのみを持つ。
    ///
    /// スレッド注記: このクラスの public メソッドは全てメインスレッド専用（Unity UI API呼び出しのため）。
    /// WarState/MilitaryManagerへは一切触れない。読み書きするのは UnitAssetBindings（割り当ての永続化）と
    /// PropCatalog（プロップ列挙・メッシュ解決）のみで、いずれもCS実体を持たない。
    /// 割り当てを変更した際は UnitVisuals.DestroyAll() を呼び、既存の見た目を破棄することで次回Syncで
    /// 新しい割り当てが反映されるようにする（UnitMeshSource.TryResolve のキャッシュ方針を参照）。
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        private const string PanelName = "CSWarfrontAssetAssignPanel";
        private const string TitleText = "モデル設定";

        internal const int MaxListItems = 300;

        internal const float PanelWidth = 380f;
        internal const float Pad = 8f;
        internal const float RowHeight = 24f;
        internal const float DropdownHeight = 26f;
        internal const float ListHeight = 220f;
        internal const float ButtonRowHeight = 26f;
        internal const float SectionGap = 6f;

        private static UIPanel _panel;
        private static UILabel _titleLabel;
        private static UIDropDown _typeKeyDropdown;
        private static UILabel _currentBindingLabel;
        private static UITextField _searchField;
        private static UIButton _customOnlyToggle;
        private static UIListBox _propListBox;
        private static UILabel _truncatedLabel;
        private static UIButton _applyButton;
        private static UIButton _resetButton;
        private static UIButton _closeButton;

        // LandUnitRoster.All() と同じ並び（カテゴリ宣言順→Tier1〜5）のTypeKey一覧。ドロップダウンの
        // 表示ラベル（"Tank_T3 → (既定)" 等）と選択インデックスを対応付けるために使う。
        private static string[] _typeKeys;

        // _propListBox.items と同じ並び（フィルタ後・MaxListItems件で打ち切り済み）のプロップ名。
        // 適用ボタンはこの配列から選択中インデックスの名前を取り出す。
        private static readonly List<string> _filteredProps = new List<string>();

        private static bool _customOnly = true; // 既定ON（Task36指定）
        private static bool _suppressEvents;
        private static bool _loggedCreated;

        /// <summary>冪等。まだ生成していなければ UIView が準備できた時点で構築する（他パネルと同じ方式）。</summary>
        public static void EnsureCreated()
        {
            try
            {
                if (_panel != null) return;
                UIView view = UIView.GetAView();
                if (view == null) return; // UI未初期化。次フレーム再試行。
                Build(view);
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.EnsureCreated error: " + e);
            }
        }

        /// <summary>BaseInfoPanelの「モデル設定」ボタンから呼ばれる。表示中なら隠し、隠れていれば開く。</summary>
        public static void Toggle()
        {
            try
            {
                EnsureCreated();
                if (_panel == null) return;
                if (_panel.isVisible) Hide();
                else Show();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.Toggle error: " + e);
            }
        }

        private static void Show()
        {
            if (_panel == null) return;

            // 開くたびに再走査する（"on demand" 方針）。全プレハブ走査は高コストなため毎フレームは行わず、
            // ユーザーがこのパネルを開いた瞬間だけ「今サブスクライブしているプロップ」に更新する。
            PropCatalog.Rescan();

            RefreshDropdownLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
            RefreshPropList();
            RefreshCurrentBindingLabel();

            _panel.isVisible = true;
            _panel.BringToFront();
        }

        private static void Hide()
        {
            if (_panel != null) _panel.isVisible = false;
        }

        /// <summary>レベルアンロード時（MilitaryManager.Reset経由）に呼ぶ。パネルを破棄し静的状態を残さない。</summary>
        public static void Destroy()
        {
            try
            {
                if (_typeKeyDropdown != null) _typeKeyDropdown.eventSelectedIndexChanged -= OnTypeKeyChanged;
                if (_searchField != null) _searchField.eventTextChanged -= OnSearchTextChanged;
                if (_customOnlyToggle != null) _customOnlyToggle.eventClick -= OnCustomOnlyClick;
                if (_propListBox != null) _propListBox.eventSelectedIndexChanged -= OnPropSelected;
                if (_applyButton != null) _applyButton.eventClick -= OnApplyClick;
                if (_resetButton != null) _resetButton.eventClick -= OnResetClick;
                if (_closeButton != null) _closeButton.eventClick -= OnCloseClick;
                if (_panel != null) UnityEngine.Object.Destroy(_panel.gameObject);
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.Destroy error: " + e);
            }
            finally
            {
                _panel = null;
                _titleLabel = null;
                _typeKeyDropdown = null;
                _currentBindingLabel = null;
                _searchField = null;
                _customOnlyToggle = null;
                _propListBox = null;
                _truncatedLabel = null;
                _applyButton = null;
                _resetButton = null;
                _closeButton = null;
                _typeKeys = null;
                _filteredProps.Clear();
                _customOnly = true;
                _suppressEvents = false;
            }
        }

        private static void Build(UIView view)
        {
            if (view.FindUIComponent<UIPanel>(PanelName) != null) return; // 二重生成防止

            UIPanel panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
            if (panel == null)
            {
                ModConfig.LogError("AssetAssignPanel.Build: UIPanel の生成に失敗");
                return;
            }
            _panel = panel;
            _panel.name = PanelName;
            _panel.backgroundSprite = "MenuPanel2";
            _panel.width = PanelWidth;
            _panel.isVisible = false;

            float w = PanelWidth - Pad * 2f;
            float y = Pad;

            _titleLabel = _panel.AddUIComponent<UILabel>();
            _titleLabel.text = TitleText;
            _titleLabel.textScale = 0.9f;
            _titleLabel.relativePosition = new Vector3(Pad, y);
            y += RowHeight;

            y = AddSectionLabel("ユニット種別", y);
            BuildTypeKeys();
            _typeKeyDropdown = BuildTypeKeyDropdown(Pad, y, w);
            y += DropdownHeight + SectionGap;

            _currentBindingLabel = _panel.AddUIComponent<UILabel>();
            _currentBindingLabel.textScale = 0.75f;
            _currentBindingLabel.textColor = new Color32(200, 200, 200, 255);
            _currentBindingLabel.wordWrap = false;
            _currentBindingLabel.autoSize = false;
            _currentBindingLabel.width = w;
            _currentBindingLabel.text = "";
            _currentBindingLabel.relativePosition = new Vector3(Pad, y);
            y += 18f + SectionGap;

            y = AddSectionLabel("検索（部分一致）", y);
            _searchField = BuildSearchField(Pad, y, w);
            y += RowHeight + SectionGap;

            _customOnlyToggle = _panel.AddUIComponent<UIButton>();
            _customOnlyToggle.textScale = 0.75f;
            _customOnlyToggle.size = new Vector2(w, RowHeight);
            _customOnlyToggle.relativePosition = new Vector3(Pad, y);
            _customOnlyToggle.normalBgSprite = "ButtonMenu";
            _customOnlyToggle.hoveredBgSprite = "ButtonMenuHovered";
            _customOnlyToggle.pressedBgSprite = "ButtonMenuPressed";
            _customOnlyToggle.eventClick += OnCustomOnlyClick;
            y += RowHeight + SectionGap;

            _propListBox = _panel.AddUIComponent<UIListBox>();
            _propListBox.size = new Vector2(w, ListHeight);
            _propListBox.relativePosition = new Vector3(Pad, y);
            _propListBox.normalBgSprite = "GenericPanelLight";
            _propListBox.itemHeight = 20;
            _propListBox.itemHover = "ListItemHover";
            _propListBox.itemHighlight = "ListItemHighlight";
            _propListBox.textScale = 0.75f;
            _propListBox.textColor = new Color32(230, 230, 230, 255);
            _propListBox.eventSelectedIndexChanged += OnPropSelected;
            y += ListHeight + SectionGap;

            _truncatedLabel = _panel.AddUIComponent<UILabel>();
            _truncatedLabel.textScale = 0.65f;
            _truncatedLabel.textColor = new Color32(255, 190, 120, 255);
            _truncatedLabel.wordWrap = false;
            _truncatedLabel.autoSize = false;
            _truncatedLabel.width = w;
            _truncatedLabel.text = "";
            _truncatedLabel.relativePosition = new Vector3(Pad, y);
            y += 16f + SectionGap;

            float buttonWidth = (w - SectionGap * 2f) / 3f;
            _applyButton = BuildButton("適用", Pad, y, buttonWidth, OnApplyClick);
            _resetButton = BuildButton("既定に戻す", Pad + buttonWidth + SectionGap, y, buttonWidth, OnResetClick);
            _closeButton = BuildButton("閉じる", Pad + (buttonWidth + SectionGap) * 2f, y, buttonWidth, OnCloseClick);
            y += ButtonRowHeight + Pad;

            _panel.height = y;
            CenterOnScreen(view);

            UpdateCustomOnlyLabel();
            RefreshCurrentBindingLabel();

            if (!_loggedCreated)
            {
                _loggedCreated = true;
                ModConfig.Log("AssetAssignPanel: created");
            }
        }

        private static void CenterOnScreen(UIView view)
        {
            if (_panel == null) return;
            Vector2 res = view.GetScreenResolution();
            float x = Mathf.Max(0f, (res.x - _panel.width) * 0.5f);
            float y = Mathf.Max(0f, (res.y - _panel.height) * 0.5f);
            _panel.relativePosition = new Vector3(x, y);
        }

        private static float AddSectionLabel(string text, float y)
        {
            UILabel label = _panel.AddUIComponent<UILabel>();
            label.text = text;
            label.textScale = 0.75f;
            label.textColor = new Color32(200, 200, 200, 255);
            label.relativePosition = new Vector3(Pad, y);
            return y + 18f;
        }

        private static UIButton BuildButton(string text, float x, float y, float width, MouseEventHandler handler)
        {
            UIButton btn = _panel.AddUIComponent<UIButton>();
            btn.text = text;
            btn.textScale = 0.8f;
            btn.size = new Vector2(width, ButtonRowHeight);
            btn.relativePosition = new Vector3(x, y);
            btn.normalBgSprite = "ButtonMenu";
            btn.hoveredBgSprite = "ButtonMenuHovered";
            btn.pressedBgSprite = "ButtonMenuPressed";
            btn.eventClick += handler;
            return btn;
        }
    }
}
