using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// 「モデル設定」パネル（Task36、Task40で勢力別割り当て・サムネイル・最小化・ドラッグに対応、
    /// Task41でパネルを1.5倍スケール化・検索欄/一覧の可読性改善・プロップ以外の種類にも対応）。
    /// ユニット種別（TypeKey）×勢力（factionId）ごとに、現在サブスクライブしている種類（プロップ/建物/
    /// 車両/樹木）×アセットを見た目のモデルとして割り当てるUI。BaseInfoPanel の「モデル設定」ボタン
    /// （Game/UI/BaseInfoPanelModelButton.cs）、および Mod Options 画面（Game/Mod.cs.OnSettingsUI、Task40）
    /// の両方から開ける、独立した常設パネル（UnitInfoPanel/BaseInfoPanelと同じ「UIView直下に1枚だけ生成し、
    /// isVisibleで出し入れする」方式）。初期位置は画面中央固定だが、Task40でドラッグ移動に対応した。
    ///
    /// Task41: パネル全体の寸法・コントロール高さ・テキストスケールを一律 <see cref="UiScale"/> 倍した
    /// （下記の各定数がTask40時点の等倍値×1.5になっている。当初2倍にしたが実機で大きすぎたため1.5倍へ調整）。
    /// 共有ヘルパー <see cref="PanelChrome"/> のタイトル行の高さ・最小化ボタンの大きさ
    /// （BaseInfoPanel/UnitInfoPanelと共有）はここでは変更しない
    /// （このパネルだけを拡大する要求であり、他の2パネルへ影響させないため）。
    ///
    /// 500行制限のため、以下のように分割している（BaseInfoPanel/BaseInfoPanelProduction と同じ方針）:
    ///   - このファイル: パネルの生成・破棄・骨格レイアウト・最小化状態の管理。
    ///   - AssetAssignPanelControls.cs: ユニット種別ドロップダウン/適用・既定に戻す・閉じるボタン。
    ///   - AssetAssignPanelFaction.cs: 勢力ドロップダウン・サムネイル表示（Task40で新設）。
    ///   - AssetAssignPanelAssetList.cs: 種類ドロップダウン/検索/サブスク済みトグル/一覧（Task41で新設、
    ///     旧AssetAssignPanelControls.csから分離）。
    ///
    /// スレッド注記: このクラスの public メソッドは全てメインスレッド専用（Unity UI API呼び出しのため）。
    /// WarState/MilitaryManagerへは一切触れない。読み書きするのは UnitAssetBindings（割り当ての永続化）と
    /// AssetCatalog（アセット列挙・メッシュ解決・サムネイル解決）のみで、いずれもCS実体を持たない。
    /// 割り当てを変更した際は UnitVisuals.DestroyAll() を呼び、既存の見た目を破棄することで次回Syncで
    /// 新しい割り当てが反映されるようにする（UnitMeshSource.TryResolve のキャッシュ方針を参照）。
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        private const string PanelName = "CSWarfrontAssetAssignPanel";
        private const string TitleText = "モデル設定";

        internal const int MaxListItems = 300; // アイテム件数の上限。パネルの寸法とは無関係のためスケールしない。

        // Task41: パネル全体をスケール化。2倍は大きすぎたため1.5倍へ調整（コメントの「旧」= Task40時点の等倍値）。
        internal const float UiScale = 1.5f;

        internal const float PanelWidth = 570f;      // 旧380f ×1.5
        internal const float Pad = 12f;               // 旧8f ×1.5
        internal const float RowHeight = 36f;         // 旧24f ×1.5
        internal const float DropdownHeight = 39f;    // 旧26f ×1.5
        internal const float ListHeight = 330f;       // 旧220f ×1.5（itemHeightも1.5倍のため可視行数は約11で変わらず）
        internal const float ButtonRowHeight = 39f;   // 旧26f ×1.5
        internal const float SectionGap = 9f;         // 旧6f ×1.5
        internal const float ThumbnailSize = 96f;     // 旧64f ×1.5

        // Task41: 検索欄の右に並べる「アセット種別」ドロップダウンの幅。
        internal const float AssetKindDropdownWidth = 165f;   // 旧110f ×1.5

        private static UIPanel _panel;
        private static UILabel _titleLabel;
        private static UIButton _collapseButton;
        private static PanelChrome.Handles _chrome; // Task40: タイトル行の最小化ボタン+ドラッグハンドル
        private static UILabel _typeKeySectionLabel; // Task40: 折りたたみ時の表示切り替え対象として保持
        private static UIDropDown _typeKeyDropdown;
        private static UILabel _currentBindingLabel;
        private static UISprite _thumbnailSprite; // Task40
        private static bool _hasThumbnail; // Task40: 直近のRefreshThumbnailが有効なサムネイルを見つけたか
        private static UILabel _searchSectionLabel; // Task40: 折りたたみ時の表示切り替え対象として保持
        private static UITextField _searchField;
        private static UIDropDown _assetKindDropdown; // Task41: プロップ/建物/車両/樹木の切り替え
        private static UIButton _customOnlyToggle;
        private static UIListBox _propListBox;
        private static UILabel _truncatedLabel;
        private static UIButton _applyButton;
        private static UIButton _resetButton;
        private static UIButton _closeButton;

        // LandUnitRoster.All() と同じ並び（カテゴリ宣言順→Tier1〜5）のTypeKey一覧。ドロップダウンの
        // 表示ラベル（"Tank_T3 → (既定)" 等）と選択インデックスを対応付けるために使う。
        private static string[] _typeKeys;

        // _propListBox.items と同じ並び（フィルタ後・MaxListItems件で打ち切り済み）のアセット名。
        // 適用ボタンはこの配列から選択中インデックスの名前を取り出す。
        private static readonly List<string> _filteredAssetNames = new List<string>();

        private static bool _customOnly = true; // 既定ON（Task36指定）
        private static bool _suppressEvents;
        private static bool _loggedCreated;

        /// <summary>Task40: 折りたたみ状態。BaseInfoPanel/UnitInfoPanelと同じくセッション中は保持する。</summary>
        private static bool _collapsed;

        /// <summary>Task47: バニラEscメニューを開いたことでこのパネルを一時的に隠した場合にtrue。
        /// このパネルはトグル開閉式（BaseInfoPanel/UnitInfoPanelのような毎フレームの表示条件再計算が無い）
        /// ため、「ユーザーが開いていた」という事実をこのフラグだけで覚えておき、メニューが閉じたら
        /// 単純に isVisible を戻す（Toggle/Show/Hideが管理する「開いている」という論理状態そのものには
        /// 一切触れないため、_typeKeyDropdownの選択やスクロール位置等も含め開いていた時のまま復元される）。</summary>
        private static bool _hiddenByMenu;

        /// <summary>Task40: 展開時の全体高さキャッシュ（BaseInfoPanel._expandedHeightと同じ役割）。</summary>
        private static float _expandedHeight;

        /// <summary>パネルが生成済みかどうか。Mod Options（Game/Mod.cs、Task40）がゲーム外
        /// （メインメニュー等でUIView自体が使えない極端なケース）を検出するために使う。</summary>
        public static bool IsCreated { get { return _panel != null; } }

        /// <summary>冪等。まだ生成していなければ UIView が準備できた時点で構築する（他パネルと同じ方式）。</summary>
        public static void EnsureCreated()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi()) return; // Task56: ロード/アンロード中はUIライブラリに触れない
                if (_panel != null) return;
                UIView view = PanelChrome.GetCachedView();
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

        /// <summary>Task40: Mod Options（Game/Mod.cs）から呼ばれる。既に開いていても閉じない
        /// （Toggleと違い、Optionsから何度押しても常に開いた状態にする方が分かりやすいため）。
        /// EnsureCreated() が既に呼ばれ _panel が非nullであることを呼び出し側が確認済みの前提。</summary>
        internal static void Show()
        {
            if (_panel == null) return;

            // 開くたびに再走査する（"on demand" 方針）。全プレハブ走査は高コストなため毎フレームは行わず、
            // ユーザーがこのパネルを開いた瞬間だけ「今サブスクライブしているアセット」（4種類とも）に更新する。
            AssetCatalog.Rescan();

            RefreshDropdownLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
            RefreshAssetList();
            RefreshCurrentBindingLabel();

            _panel.isVisible = true;
            _panel.BringToFront();
        }

        private static void Hide()
        {
            if (_panel != null) _panel.isVisible = false;
        }

        /// <summary>Task47: WarfrontThreadingExtension.OnUpdateから毎フレーム呼ぶ。BaseInfoPanel/
        /// UnitInfoPanelのUpdateVisibilityと異なりこのパネルは常時ポーリングの表示条件を持たないため、
        /// 「Escメニューを開いたときに表示中だったか」を_hiddenByMenuだけで覚えておき、閉じたら
        /// isVisibleを戻す（Hide()は呼ばない＝ユーザーの「閉じる」操作と区別する）。</summary>
        public static void UpdateGameMenuState()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi()) return; // Task56: ロード/アンロード中はUIライブラリに触れない
                if (_panel == null) return;

                bool menuOpen = PanelChrome.IsGameMenuOpen();
                if (menuOpen)
                {
                    if (_panel.isVisible)
                    {
                        _panel.isVisible = false;
                        _hiddenByMenu = true;
                    }
                }
                else if (_hiddenByMenu)
                {
                    _hiddenByMenu = false;
                    _panel.isVisible = true;
                    _panel.BringToFront();
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.UpdateGameMenuState error: " + e);
            }
        }

        /// <summary>レベルアンロード時（MilitaryManager.Reset経由）に呼ぶ。パネルを破棄し静的状態を残さない。</summary>
        public static void Destroy()
        {
            try
            {
                PanelChrome.Unsubscribe(_chrome, OnCollapseClick); // Task40
                if (_typeKeyDropdown != null) _typeKeyDropdown.eventSelectedIndexChanged -= OnTypeKeyChanged;
                DestroyFactionSection(); // Task40: イベント購読解除＋フィールドのリセット
                if (_searchField != null) _searchField.eventTextChanged -= OnSearchTextChanged;
                if (_assetKindDropdown != null) _assetKindDropdown.eventSelectedIndexChanged -= OnAssetKindChanged;
                if (_customOnlyToggle != null) _customOnlyToggle.eventClick -= OnCustomOnlyClick;
                if (_propListBox != null) _propListBox.eventSelectedIndexChanged -= OnAssetSelected;
                if (_applyButton != null) _applyButton.eventClick -= OnApplyClick;
                if (_resetButton != null) _resetButton.eventClick -= OnResetClick;
                if (_copyApplyButton != null) _copyApplyButton.eventClick -= OnCopyApplyClick; // Task47
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
                _collapseButton = null;
                _chrome = null;
                _typeKeySectionLabel = null;
                _typeKeyDropdown = null;
                _currentBindingLabel = null;
                _thumbnailSprite = null;
                _hasThumbnail = false;
                _searchSectionLabel = null;
                _searchField = null;
                _assetKindDropdown = null;
                _customOnlyToggle = null;
                _propListBox = null;
                _truncatedLabel = null;
                _applyButton = null;
                _resetButton = null;
                _copyScopeSectionLabel = null; // Task47
                _copyScopeDropdown = null;
                _copyApplyButton = null;
                _closeButton = null;
                _typeKeys = null;
                _filteredAssetNames.Clear();
                _customOnly = true;
                _suppressEvents = false;
                _collapsed = false;
                _expandedHeight = 0f;
                _hiddenByMenu = false;
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

            // Task40: タイトル行全体を覆うドラッグハンドル(target=_panel)を先に追加し、その後に
            // タイトルラベル(非対話的)・最小化ボタン(対話的)を重ねる（BaseInfoPanelと同じ方式）。
            // PanelChrome側のタイトル行高さ・ボタンサイズは他2パネルと共有のためスケールしない
            // （このパネル固有の要求であり、共有ヘルパーを変えると他パネルにも影響するため）。
            _chrome = PanelChrome.AddTitleBarChrome(_panel, PanelWidth, y, Pad, OnCollapseClick);
            _collapseButton = _chrome.CollapseButton;

            _titleLabel = _panel.AddUIComponent<UILabel>();
            _titleLabel.text = TitleText;
            _titleLabel.textScale = 0.9f;   // 他パネルと統一（寸法のみ1.5倍、文字は等倍）
            _titleLabel.relativePosition = new Vector3(Pad, y);
            y += RowHeight;

            y = AddSectionLabel("勢力", y, out _factionSectionLabel); // フィールドはAssetAssignPanelFaction.cs側で宣言
            BuildFactionDropdown(Pad, y, w); // AssetAssignPanelFaction.cs
            y += DropdownHeight + SectionGap;

            y = AddSectionLabel("ユニット種別", y, out _typeKeySectionLabel);
            BuildTypeKeys();
            _typeKeyDropdown = BuildTypeKeyDropdown(Pad, y, w);
            y += DropdownHeight + SectionGap;

            // Task40: 「現在の割り当て」ラベル（左）とサムネイル（右、128x128）を同じ行に並べる。
            float bindingLabelWidth = w - ThumbnailSize - SectionGap;
            _currentBindingLabel = _panel.AddUIComponent<UILabel>();
            _currentBindingLabel.textScale = 0.75f; // 他パネルと統一
            _currentBindingLabel.textColor = new Color32(200, 200, 200, 255);
            _currentBindingLabel.wordWrap = true;
            _currentBindingLabel.autoSize = false;
            _currentBindingLabel.autoHeight = false;
            _currentBindingLabel.width = bindingLabelWidth;
            _currentBindingLabel.height = ThumbnailSize;
            _currentBindingLabel.text = "";
            _currentBindingLabel.relativePosition = new Vector3(Pad, y);

            BuildThumbnailSprite(Pad + bindingLabelWidth + SectionGap, y); // AssetAssignPanelFaction.cs
            y += ThumbnailSize + SectionGap;

            y = AddSectionLabel("検索（部分一致） / アセット種別", y, out _searchSectionLabel);

            // Task41: 検索欄（左）とアセット種別ドロップダウン（右、プロップ/建物/車両/樹木）を同じ行に並べる。
            float searchWidth = w - AssetKindDropdownWidth - SectionGap;
            _searchField = BuildSearchField(Pad, y, searchWidth); // AssetAssignPanelAssetList.cs
            _assetKindDropdown = BuildAssetKindDropdown(Pad + searchWidth + SectionGap, y, AssetKindDropdownWidth); // AssetAssignPanelAssetList.cs
            y += RowHeight + SectionGap;

            _customOnlyToggle = _panel.AddUIComponent<UIButton>();
            _customOnlyToggle.textScale = 0.75f; // 他パネルと統一
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
            _propListBox.itemHeight = 26; // 文字は等倍だが行間は少し広めに
            _propListBox.itemHover = "ListItemHover";
            _propListBox.itemHighlight = "ListItemHighlight";
            _propListBox.textScale = 0.75f; // 他パネルと統一
            // Task41: GenericPanelLight（明るい背景）に対しては itemTextColor（各行の実際の文字色）を
            // 濃色にしないと可読性が無い。旧実装は textColor のみ設定していたが、UIListBox の行描画は
            // itemTextColor（ColossalManaged.dllをリフレクションで存在確認済み、Color32プロパティ）が
            // 支配的なため、こちらを明示的に設定する（textColorは互換のため残す）。
            _propListBox.textColor = new Color32(230, 230, 230, 255);
            _propListBox.itemTextColor = new Color32(20, 20, 24, 255);
            _propListBox.eventSelectedIndexChanged += OnAssetSelected;
            y += ListHeight + SectionGap;

            _truncatedLabel = _panel.AddUIComponent<UILabel>();
            _truncatedLabel.textScale = 0.65f; // 他パネルと統一
            _truncatedLabel.textColor = new Color32(255, 190, 120, 255);
            _truncatedLabel.wordWrap = false;
            _truncatedLabel.autoSize = false;
            _truncatedLabel.width = w;
            _truncatedLabel.text = "";
            _truncatedLabel.relativePosition = new Vector3(Pad, y);
            y += 24f + SectionGap; // 旧16f ×1.5

            // Task47: 「複製適用」= 現在選択中(勢力,ユニット種別)の割り当てを他の(勢力,種別)へまとめて
            // 複製するUI。範囲選択ドロップダウン（左）とボタン（右）を同じ行に並べる
            // （AssetAssignPanelCopy.cs 参照）。
            y = AddSectionLabel("複製適用", y, out _copyScopeSectionLabel);
            float copyButtonWidth = w * 0.35f;
            float copyDropdownWidth = w - copyButtonWidth - SectionGap;
            _copyScopeDropdown = BuildCopyScopeDropdown(Pad, y, copyDropdownWidth);
            _copyApplyButton = BuildButton("複製適用", Pad + copyDropdownWidth + SectionGap, y, copyButtonWidth, OnCopyApplyClick);
            y += DropdownHeight + SectionGap;

            float buttonWidth = (w - SectionGap * 2f) / 3f;
            _applyButton = BuildButton("適用", Pad, y, buttonWidth, OnApplyClick);
            _resetButton = BuildButton("既定に戻す", Pad + buttonWidth + SectionGap, y, buttonWidth, OnResetClick);
            _closeButton = BuildButton("閉じる", Pad + (buttonWidth + SectionGap) * 2f, y, buttonWidth, OnCloseClick);
            y += ButtonRowHeight + Pad;

            _expandedHeight = y;
            _panel.height = y;
            CenterOnScreen(view);

            UpdateCustomOnlyLabel();
            RefreshCurrentBindingLabel();
            ApplyCollapsedState(); // 展開/折りたたみの初期反映（BaseInfoPanel.Buildと同じ方式）

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

        /// <summary>Task40: 呼び出し側にラベル参照を返すようにした（折りたたみ時にisVisibleを
        /// 一括切り替えるため、SetSectionVisible/ApplyCollapsedStateから使う）。</summary>
        private static float AddSectionLabel(string text, float y, out UILabel label)
        {
            label = _panel.AddUIComponent<UILabel>();
            label.text = text;
            label.textScale = 0.75f; // 他パネルと統一
            label.textColor = new Color32(200, 200, 200, 255);
            label.relativePosition = new Vector3(Pad, y);
            return y + 27f; // 旧18f ×1.5
        }

        private static UIButton BuildButton(string text, float x, float y, float width, MouseEventHandler handler)
        {
            UIButton btn = _panel.AddUIComponent<UIButton>();
            btn.text = text;
            btn.textScale = 0.8f; // 他パネルと統一
            btn.size = new Vector2(width, ButtonRowHeight);
            btn.relativePosition = new Vector3(x, y);
            btn.normalBgSprite = "ButtonMenu";
            btn.hoveredBgSprite = "ButtonMenuHovered";
            btn.pressedBgSprite = "ButtonMenuPressed";
            btn.eventClick += handler;
            return btn;
        }

        /// <summary>Task40: _collapsed の現在値をUIに反映する（BaseInfoPanel.ApplyCollapsedStateと同じ方式）。
        /// タイトル行以外の全コントロールをまとめて表示/非表示にする。</summary>
        private static void ApplyCollapsedState()
        {
            if (_panel == null) return;

            SetSectionVisible(!_collapsed);
            _panel.height = _collapsed ? (Pad + PanelChrome.TitleRowHeight + Pad) : _expandedHeight;

            if (_collapseButton != null)
            {
                _collapseButton.text = PanelChrome.CollapseGlyph(_collapsed);
            }
        }

        private static void OnCollapseClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                _collapsed = !_collapsed;
                ApplyCollapsedState();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnCollapseClick error: " + e);
            }
        }

        /// <summary>タイトル行(タイトルラベル・最小化ボタン・ドラッグハンドル)以外の全コントロールの
        /// isVisibleを一括切り替えする。サムネイルは「展開中かつ有効なサムネイルが見つかっている」
        /// 場合のみ表示する（_hasThumbnail、AssetAssignPanelFaction.cs の RefreshThumbnail が更新）。</summary>
        private static void SetSectionVisible(bool visible)
        {
            if (_factionSectionLabel != null) _factionSectionLabel.isVisible = visible;
            if (_factionDropdown != null) _factionDropdown.isVisible = visible;
            if (_typeKeySectionLabel != null) _typeKeySectionLabel.isVisible = visible;
            if (_typeKeyDropdown != null) _typeKeyDropdown.isVisible = visible;
            if (_currentBindingLabel != null) _currentBindingLabel.isVisible = visible;
            if (_thumbnailSprite != null) _thumbnailSprite.isVisible = visible && _hasThumbnail;
            if (_searchSectionLabel != null) _searchSectionLabel.isVisible = visible;
            if (_searchField != null) _searchField.isVisible = visible;
            if (_assetKindDropdown != null) _assetKindDropdown.isVisible = visible;
            if (_customOnlyToggle != null) _customOnlyToggle.isVisible = visible;
            if (_propListBox != null) _propListBox.isVisible = visible;
            if (_truncatedLabel != null) _truncatedLabel.isVisible = visible;
            if (_applyButton != null) _applyButton.isVisible = visible;
            if (_resetButton != null) _resetButton.isVisible = visible;
            if (_copyScopeSectionLabel != null) _copyScopeSectionLabel.isVisible = visible; // Task47
            if (_copyScopeDropdown != null) _copyScopeDropdown.isVisible = visible;
            if (_copyApplyButton != null) _copyApplyButton.isVisible = visible;
            if (_closeButton != null) _closeButton.isVisible = visible;
        }
    }
}
