using System;
using System.Text;
using ColossalFramework.UI;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// 軍事基地建物（WarfrontBasePrefab）の情報パネル（Task25）。
    /// バニラの建物情報パネル（CityServiceWorldInfoPanel、電力タブ複製元のため）が表示されている間、
    /// 選択中の建物が登録済み論理基地（MilitaryManager.State.Bases）であれば、その隣に所属勢力
    /// ドロップダウンとステータスを表示する自前の小さな UIPanel を出す。
    ///
    /// バニラパネルの中へ直接 AddUIComponent するのではなく、独立した UIPanel を UIView 直下に
    /// 生成してバニラパネルの右（画面外なら左）に追従させる方式を採る：バニラの
    /// BuildingWorldInfoPanel 系は内部で自前の子要素レイアウトを RefreshData 等のたびに再計算しており、
    /// 直接子として差し込むと将来のバニラ側リフレッシュで位置崩れ・消失（DisastersPanel の
    /// RefreshPanel と同様の再生成）を起こすリスクがある。独立パネルなら影響を受けない
    /// （MissileDisaster.Game.UI.MissilePanel と同じ「UIView直下の常設パネル」方式）。
    ///
    /// スレッド注記: このクラスの public メソッドは全てメインスレッド専用（Unity UI API呼び出しのため）。
    /// WarfrontThreadingExtension.OnUpdate から毎フレーム呼ばれる想定。WarState へは一切直接触れず、
    /// MilitaryManager.TryGetBaseSnapshot / TrySetBaseOwner 経由でのみ読み書きする（_stateLock は
    /// それらの内部で短時間だけ取られ、ここでは保持しない）。
    /// </summary>
    internal static class BaseInfoPanel
    {
        private const string PanelName = "CSWarfrontBaseInfoPanel";
        private const string VanillaPanelName = "CityServiceWorldInfoPanel";
        private const string TitleText = "CSWarfront 軍事基地";

        private const float PanelWidth = 260f;
        private const float Pad = 8f;
        private const float TitleRowHeight = 22f;
        private const float DropdownHeight = 28f;
        // ステータス表示行数が増えた（Task30: 所属/体力/防衛/軍資金/部隊数/生産中/待機/占領猶予 最大8行）ため
        // 1行あたり約12pxで見積もった予約高さ。実際の行数は可変（待機0件・猶予0hなら省略）だが、
        // ラベルの wordWrap 前提で最大行数ぶんの領域を固定確保しておけば十分。
        private const float StatusLabelReserveHeight = 150f;
        private const float VanillaGap = 8f;
        private const float CollapseButtonSize = 20f;
        private const string CollapseGlyphExpanded = "–"; // – (最小化する = クリックすると畳む)
        private const string CollapseGlyphCollapsed = "+";     // + (展開する = クリックすると開く)

        private static UIPanel _panel;
        private static UILabel _titleLabel;
        private static UIButton _collapseButton;
        private static UILabel _factionSectionLabel;
        private static UIDropDown _factionDropdown;
        private static UILabel _statusLabel;

        private static ushort _currentBaseId; // 0 = 未表示（CSの建物id 0 は「無し」を意味する）
        private static bool _suppressDropdownEvent;
        private static bool _loggedCreated;

        /// <summary>RefreshContents で毎フレーム再利用するバッファ（Task30: 文字列連結の毎フレーム
        /// アロケーションを避けるため、StringBuilder.Clear() で使い回す）。</summary>
        private static readonly StringBuilder _statusBuilder = new StringBuilder(256);

        /// <summary>パネル最小化トグルのUI設定（Task27）。セッション中は選択解除・再選択をまたいで保持する
        /// （Hide/UpdateVisibilityでは変更しない。Destroy＝レベルアンロード時のみリセット）。</summary>
        private static bool _collapsed;

        /// <summary>展開時の全体高さ。Build() で一度だけ確定させ、折りたたみ→復元の際にここから正確に戻す
        /// （縮小後のサイズから再計算すると誤差が積み重なるため、必ずこのキャッシュ値を使う）。</summary>
        private static float _expandedHeight;

        /// <summary>
        /// 冪等。まだ生成していなければ、バニラの建物情報パネル型がライブラリから取得できる状態に
        /// なった時点で自前パネルを構築する（MissileDisasterButton.EnsureAttached と同じ
        /// 「毎フレームポーリングして条件が揃ったら一度だけ作る」方式）。
        /// </summary>
        public static void EnsureCreated()
        {
            try
            {
                if (_panel != null) return;
                if (TryGetVanillaPanel() == null) return; // UI未初期化。次フレーム再試行。
                Build();
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.EnsureCreated error: " + e);
            }
        }

        /// <summary>
        /// 毎メインスレッドフレーム呼ぶ。バニラ建物情報パネルが表示中かつ選択建物が登録済み論理基地の
        /// 場合のみ自前パネルを表示・内容更新し、それ以外は隠す。
        /// </summary>
        public static void UpdateVisibility()
        {
            try
            {
                if (_panel == null) return; // EnsureCreated 待ち

                CityServiceWorldInfoPanel vanilla = TryGetVanillaPanel();
                if (vanilla == null || vanilla.component == null || !vanilla.component.isVisible)
                {
                    Hide();
                    return;
                }

                InstanceID iid = WorldInfoPanel.GetCurrentInstanceID();
                ushort buildingId = iid.Building;
                if (buildingId == 0)
                {
                    Hide();
                    return;
                }

                BaseUiSnapshot snapshot;
                if (!MilitaryManager.TryGetBaseSnapshot(buildingId, out snapshot))
                {
                    Hide(); // バニラパネルは軍事基地以外の建物を表示中、または未登録
                    return;
                }

                _currentBaseId = buildingId;
                PositionNextToVanilla(vanilla);
                // 折りたたみ中はタイトル行しか見えないため、ドロップダウン/ステータスの再構築は無駄
                // （Task30: 毎フレーム呼ばれるのでここで確実にスキップする）。展開された瞬間に
                // 正しい内容が出るよう、_currentBaseId とスナップショット自体は毎フレーム更新しておく。
                if (!_collapsed) RefreshContents(snapshot);
                if (!_panel.isVisible) _panel.Show();
                _panel.BringToFront();
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.UpdateVisibility error: " + e);
            }
        }

        /// <summary>レベルアンロード時（MilitaryManager.Reset経由）に呼ぶ。パネルを破棄し静的状態を残さない。</summary>
        public static void Destroy()
        {
            try
            {
                if (_factionDropdown != null) _factionDropdown.eventSelectedIndexChanged -= OnFactionSelected;
                if (_collapseButton != null) _collapseButton.eventClick -= OnCollapseClick;
                if (_panel != null) UnityEngine.Object.Destroy(_panel.gameObject);
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.Destroy error: " + e);
            }
            finally
            {
                _panel = null;
                _titleLabel = null;
                _collapseButton = null;
                _factionSectionLabel = null;
                _factionDropdown = null;
                _statusLabel = null;
                _currentBaseId = 0;
                _suppressDropdownEvent = false;
                _collapsed = false;
                _expandedHeight = 0f;
            }
        }

        private static void Hide()
        {
            if (_panel != null && _panel.isVisible) _panel.Hide();
            _currentBaseId = 0;
        }

        private static CityServiceWorldInfoPanel TryGetVanillaPanel()
        {
            // UIView.library.Get<T> はまだ登録/生成されていない場合 null を返す（例外ではない）ため、
            // ここは「未準備」を通常経路として扱う（毎フレームのログ連発を避ける）。
            return UIView.library.Get<CityServiceWorldInfoPanel>(VanillaPanelName);
        }

        private static void Build()
        {
            UIView view = UIView.GetAView();
            if (view == null) return;
            if (view.FindUIComponent<UIPanel>(PanelName) != null) return; // 二重生成防止

            UIPanel panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
            if (panel == null)
            {
                ModConfig.LogError("BaseInfoPanel.Build: UIPanel の生成に失敗");
                return;
            }
            _panel = panel;
            _panel.name = PanelName;
            _panel.backgroundSprite = "MenuPanel2";
            _panel.width = PanelWidth;

            float w = PanelWidth - Pad * 2f;
            float y = Pad;

            _titleLabel = _panel.AddUIComponent<UILabel>();
            _titleLabel.text = TitleText;
            _titleLabel.textScale = 0.9f;
            _titleLabel.relativePosition = new Vector3(Pad, y);

            _collapseButton = _panel.AddUIComponent<UIButton>();
            _collapseButton.size = new Vector2(CollapseButtonSize, CollapseButtonSize);
            _collapseButton.relativePosition = new Vector3(PanelWidth - Pad - CollapseButtonSize, y);
            _collapseButton.textScale = 0.8f;
            _collapseButton.normalBgSprite = "ButtonMenu";
            _collapseButton.hoveredBgSprite = "ButtonMenuHovered";
            _collapseButton.pressedBgSprite = "ButtonMenuPressed";
            _collapseButton.eventClick += OnCollapseClick;
            y += TitleRowHeight;

            y = AddSectionLabel("所属勢力", Pad, y, out _factionSectionLabel);
            _factionDropdown = BuildFactionDropdown(Pad, y, w);
            y += DropdownHeight + 8f;

            y += 4f;
            _statusLabel = _panel.AddUIComponent<UILabel>();
            _statusLabel.textScale = 0.75f;
            _statusLabel.textColor = new Color32(220, 220, 220, 255);
            _statusLabel.wordWrap = true;
            _statusLabel.width = w;
            _statusLabel.text = "";
            _statusLabel.relativePosition = new Vector3(Pad, y);
            y += StatusLabelReserveHeight;

            _expandedHeight = y + Pad;
            _panel.isVisible = false;
            ApplyCollapsedState(); // 展開/折りたたみの初期反映（_collapsedはセッション内で永続、通常は false）

            if (!_loggedCreated)
            {
                _loggedCreated = true;
                ModConfig.Log("BaseInfoPanel: created");
            }
        }

        /// <summary>_collapsed の現在値をUIに反映する（表示/非表示、パネル高さ、ボタン文言）。
        /// トグルクリック時と、パネル生成直後（永続化された前回の状態を復元）に呼ぶ。</summary>
        private static void ApplyCollapsedState()
        {
            if (_panel == null) return;

            if (_factionSectionLabel != null) _factionSectionLabel.isVisible = !_collapsed;
            if (_factionDropdown != null) _factionDropdown.isVisible = !_collapsed;
            if (_statusLabel != null) _statusLabel.isVisible = !_collapsed;

            _panel.height = _collapsed ? (Pad + TitleRowHeight + Pad) : _expandedHeight;

            if (_collapseButton != null)
            {
                _collapseButton.text = _collapsed ? CollapseGlyphCollapsed : CollapseGlyphExpanded;
            }
        }

        /// <summary>最小化トグルボタンのクリックハンドラ。_collapsed を反転してUIに反映するだけで、
        /// MilitaryManager 側の状態には一切触れない（純粋なUI表示設定）。</summary>
        private static void OnCollapseClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                _collapsed = !_collapsed;
                ApplyCollapsedState();
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnCollapseClick error: " + e);
            }
        }

        private static UIDropDown BuildFactionDropdown(float x, float y, float width)
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
            dd.textScale = 0.8f;
            dd.textFieldPadding = new RectOffset(8, 8, 6, 0);
            dd.itemPadding = new RectOffset(8, 0, 3, 0);
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            dd.items = WarfrontSettings.FactionNames;
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

            dd.eventSelectedIndexChanged += OnFactionSelected;
            return dd;
        }

        private static float AddSectionLabel(string text, float x, float y, out UILabel label)
        {
            label = _panel.AddUIComponent<UILabel>();
            label.text = text;
            label.textScale = 0.75f;
            label.textColor = new Color32(200, 200, 200, 255);
            label.relativePosition = new Vector3(x, y);
            return y + 18f;
        }

        /// <summary>
        /// ドロップダウン選択変更ハンドラ。RefreshContents による状態→UI反映（selectedIndex書き戻し）が
        /// 自分自身を再度呼ばないよう _suppressDropdownEvent で無限ループを防ぐ。
        /// </summary>
        private static void OnFactionSelected(UIComponent component, int value)
        {
            try
            {
                if (_suppressDropdownEvent) return;
                if (_currentBaseId == 0) return;
                string[] names = WarfrontSettings.FactionNames;
                if (value < 0 || value >= names.Length) return;

                bool ok = MilitaryManager.TrySetBaseOwner(_currentBaseId, (byte)value);
                if (!ok)
                {
                    ModConfig.LogError("BaseInfoPanel: TrySetBaseOwner failed baseId=" + _currentBaseId + " factionId=" + value);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.OnFactionSelected error: " + e);
            }
        }

        /// <summary>ドロップダウンの選択・ステータスラベルの文言を、ロック内でコピー済みのスナップショットから更新する。</summary>
        private static void RefreshContents(BaseUiSnapshot snapshot)
        {
            byte ownerIndex = snapshot.OwnerFactionId ?? 0;
            if (_factionDropdown != null && _factionDropdown.selectedIndex != ownerIndex)
            {
                _suppressDropdownEvent = true;
                try { _factionDropdown.selectedIndex = ownerIndex; }
                finally { _suppressDropdownEvent = false; }
            }

            if (_statusLabel != null)
            {
                string[] names = WarfrontSettings.FactionNames;
                string ownerName = (snapshot.OwnerFactionId.HasValue && snapshot.OwnerFactionId.Value < names.Length)
                    ? names[snapshot.OwnerFactionId.Value]
                    : "未所属";

                StringBuilder sb = _statusBuilder;
                sb.Length = 0;

                sb.Append("所属: ").Append(ownerName);
                if (snapshot.IsHeadquarters) sb.Append(" (HQ)");

                sb.Append("\n体力: ").Append(snapshot.CurrentHP.ToString("0")).Append(" / ").Append(snapshot.MaxHP.ToString("0"));
                sb.Append("\n防衛: 攻撃").Append(snapshot.DefenseAttack.ToString("0")).Append("/h 射程").Append(snapshot.DefenseRange.ToString("0"));
                sb.Append("\n軍資金: ").Append(snapshot.OwnerTreasury.ToString("0"));
                sb.Append("\n部隊数: ").Append(snapshot.OwnerUnitCount);

                if (string.IsNullOrEmpty(snapshot.ProducingTypeKey))
                {
                    sb.Append("\n生産中: なし");
                }
                else
                {
                    float pct = Mathf.Clamp01(snapshot.ProducingProgress) * 100f;
                    float remainHours = (1f - Mathf.Clamp01(snapshot.ProducingProgress)) * snapshot.ProducingBuildTime;
                    if (remainHours < 0f) remainHours = 0f;
                    sb.Append("\n生産中: ").Append(snapshot.ProducingTypeKey).Append("  ").Append(pct.ToString("0")).Append("%")
                      .Append("  (残り ").Append(remainHours.ToString("0.0")).Append("h)");
                }

                int waiting = snapshot.QueueCount - (string.IsNullOrEmpty(snapshot.ProducingTypeKey) ? 0 : 1);
                if (waiting > 0) sb.Append("\n待機: ").Append(waiting).Append(" 件");

                if (snapshot.CaptureGraceHours > 0f)
                    sb.Append("\n占領猶予: ").Append(snapshot.CaptureGraceHours.ToString("0.0")).Append("h");

                _statusLabel.text = sb.ToString();
            }
        }

        /// <summary>バニラパネルの絶対位置の右（画面外なら左）に自前パネルを追従させる。画面下端もクランプする。</summary>
        private static void PositionNextToVanilla(CityServiceWorldInfoPanel vanilla)
        {
            UIComponent vc = vanilla.component;
            UIView view = UIView.GetAView();
            if (vc == null || _panel == null || view == null) return;

            Vector3 abs = vc.absolutePosition;
            float x = abs.x + vc.width + VanillaGap;
            float y = abs.y;

            Vector2 res = view.GetScreenResolution();
            if (x + _panel.width > res.x) x = Mathf.Max(0f, abs.x - _panel.width - VanillaGap);
            if (y + _panel.height > res.y) y = Mathf.Max(0f, res.y - _panel.height);
            if (y < 0f) y = 0f;

            _panel.relativePosition = new Vector3(x, y);
        }
    }
}
