using System;
using System.Text;
using ColossalFramework.UI;
using CSWarfront.Core;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Status panel for the unit selected by clicking (Task31/Task32). Shown only while
    /// UnitSelection.SelectedInstanceId is non-zero and MilitaryManager.TryGetUnitSnapshot returns a living
    /// unit for that id.
    ///
    /// Task32: follows the target unit on screen (like CS's vanilla vehicle/citizen world info panels).
    /// Unlike BaseInfoPanel (Game/UI/BaseInfoPanel.cs), this is NOT the "follow alongside a vanilla panel"
    /// approach — unit selection uses our own click hit-testing (UnitSelection) unrelated to CS's
    /// WorldInfoPanel system, so there is no vanilla panel to follow. Instead, the actual render position
    /// (world coordinates) obtained via UnitVisuals.TryGetPosition is converted to screen coordinates every
    /// frame and the panel is placed directly above it.
    ///
    /// Threading note: all public methods of this class are main-thread only (because they call Unity UI
    /// APIs). Expected to be called every frame from WarfrontThreadingExtension.OnUpdate. Never touches
    /// WarState directly; reads only via MilitaryManager.TryGetUnitSnapshot / UnitVisuals.TryGetPosition
    /// (_stateLock is taken briefly inside the former and never held here).
    /// </summary>
    internal static class UnitInfoPanel
    {
        private const string PanelName = "CSWarfrontUnitInfoPanel";

        // Task33: at the old 240px the status lines did not fit within a wordWrap-based width, and the
        // word-level wrapping pushed "Status", "Target", and "Path" outside/below the panel. The label is
        // now fixed to wordWrap=false, and the panel width is widened until the longest line
        // ("Armor: 100    Speed: 50km/h", "Target: Unit#<uint>", etc.) fits at textScale=0.75 (a rough
        // estimate of full-width≈16px/half-width≈9px @ scale1.0 plus a safety margin puts the longest line
        // at about 163px; 260px (244px inner width) is reserved to allow for deviation from the actual
        // font metrics).
        private const float PanelWidth = 260f;
        private const float Pad = 8f;
        private const float TitleRowHeight = 22f;
        private const float CloseButtonSize = 20f;
        private const float ButtonGap = 4f;

        /// <summary>
        /// Task32: upward offset in world coordinates for placing the panel directly above the unit.
        /// Aims reliably higher than the visibility marker (UnitVisuals.MarkerSize=8, lifted MarkerHeight=5
        /// off the ground and center-positioned, so its top is roughly ground+9); chose +12 (about 3 units
        /// of clearance above the marker top).
        /// </summary>
        private static readonly Vector3 VerticalOffset = new Vector3(0f, 12f, 0f);

        private static UIPanel _panel;
        private static UILabel _titleLabel;
        private static UIButton _closeButton;
        private static UIButton _collapseButton;
        private static PanelChrome.Handles _chrome; // Task40: collapse button + drag handle on the title row
        private static UILabel _statusLabel;
        private static bool _loggedCreated;

        /// <summary>Task40: newly added because this panel originally had no collapse feature. Same idea as
        /// BaseInfoPanel.ApplyCollapsedState (fold down to just the title row). Persists across
        /// deselect/reselect within the session.</summary>
        private static bool _collapsed;

        /// <summary>Task40: cached full height when expanded (same role as BaseInfoPanel._expandedHeight).</summary>
        private static float _expandedHeight;

        /// <summary>
        /// Task40: becomes true after the user drags the title row; while true, the per-frame unit tracking
        /// by UpdateTrackingPosition is stopped (to preserve the dragged position). Reset to false when the
        /// panel "closes" (deselection, the × button, or the target unit disappearing), so the next selected
        /// unit is tracked normally again.
        /// </summary>
        private static bool _detached;

        /// <summary>Buffer reused every frame by RefreshContents (same reason as BaseInfoPanel: avoid
        /// per-frame allocations from string concatenation by reusing via StringBuilder.Clear()).</summary>
        private static readonly StringBuilder _statusBuilder = new StringBuilder(256);

        /// <summary>
        /// Idempotent. Builds our own panel once the UIView is ready, if not created yet
        /// (same "poll every frame and create exactly once when the conditions are met" approach as
        /// BaseInfoPanel.EnsureCreated).
        /// </summary>
        public static void EnsureCreated()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi()) return; // Task56: do not touch the UI library while loading/unloading
                if (_panel != null) return;
                UIView view = PanelChrome.GetCachedView();
                if (view == null) return; // UI not initialized yet. Retry next frame.
                Build(view);
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitInfoPanel.EnsureCreated error: " + e);
            }
        }

        /// <summary>
        /// Call every main-thread frame. Shows and refreshes the panel only while a selected unit exists;
        /// hides it otherwise. If the selected unit can no longer be found (dead/removed etc.), the panel is
        /// hidden and the selection is cleared at the same time (to avoid being stuck with a vanished unit
        /// selected until the next click).
        /// </summary>
        public static void UpdateVisibility()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi()) return; // Task56: do not touch the UI library while loading/unloading
                if (_panel == null) return; // Waiting for EnsureCreated

                // Task47: while the vanilla Esc menu is open, skip this frame's processing entirely and hide
                // only the raw UIPanel (never touching logic state such as _detached). The reason for not
                // going through the private Hide() is the same as BaseInfoPanel.UpdateVisibility (Hide() is
                // equivalent to "deselection" and would reset _detached=false, so on the frame after closing
                // the menu the panel could no longer resume tracking the same unit normally).
                if (PanelChrome.IsGameMenuOpen())
                {
                    if (_panel.isVisible) _panel.Hide();
                    return;
                }

                uint selected = UnitSelection.SelectedInstanceId;
                if (selected == 0)
                {
                    Hide();
                    return;
                }

                UnitUiSnapshot snapshot;
                if (!MilitaryManager.TryGetUnitSnapshot(selected, out snapshot))
                {
                    Hide();
                    UnitSelection.Clear();
                    return;
                }

                // Task40: while collapsed only the title row is visible, so rebuilding is wasted work (same optimization as BaseInfoPanel).
                if (!_collapsed) RefreshContents(snapshot);
                UpdateTrackingPosition(selected);
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitInfoPanel.UpdateVisibility error: " + e);
            }
        }

        /// <summary>Call at level unload (via MilitaryManager.Reset). Destroys the panel and leaves no static state behind.</summary>
        public static void Destroy()
        {
            try
            {
                if (_closeButton != null) _closeButton.eventClick -= OnCloseClick;
                PanelChrome.Unsubscribe(_chrome, OnCollapseClick); // Task40
                if (_chrome != null && _chrome.DragHandle != null) _chrome.DragHandle.eventMouseDown -= OnTitleBarMouseDown;
                if (_panel != null) UnityEngine.Object.Destroy(_panel.gameObject);
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitInfoPanel.Destroy error: " + e);
            }
            finally
            {
                _panel = null;
                _titleLabel = null;
                _closeButton = null;
                _collapseButton = null;
                _chrome = null;
                _statusLabel = null;
                _collapsed = false;
                _expandedHeight = 0f;
                _detached = false;
            }
        }

        /// <summary>Hides the panel. Task40: this is equivalent to "closing" (deselection, the × button, or
        /// the target unit disappearing), so it also exits detached mode, letting the next selection be
        /// tracked normally. The temporary hide paths inside UpdateTrackingPosition (visual not yet created /
        /// behind the camera etc.) early-return on a separate branch while _detached==true and never reach
        /// here, so there is no risk of an accidental reset during a drag.</summary>
        private static void Hide()
        {
            if (_panel != null && _panel.isVisible) _panel.Hide();
            _detached = false;
        }

        private static void Build(UIView view)
        {
            if (view.FindUIComponent<UIPanel>(PanelName) != null) return; // Prevent double creation

            UIPanel panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
            if (panel == null)
            {
                ModConfig.LogError("UnitInfoPanel.Build: failed to create UIPanel");
                return;
            }
            _panel = panel;
            _panel.name = PanelName;
            _panel.backgroundSprite = "MenuPanel2";
            _panel.width = PanelWidth;

            float w = PanelWidth - Pad * 2f;
            float y = Pad;

            // Task40: first add a drag handle (target=_panel) covering the entire title row, then layer the
            // title label (non-interactive) plus the collapse button and × (close) button (both interactive)
            // on top. Components added later render in front, so button clicks are not intercepted by the
            // drag handle (same approach as BaseInfoPanel; see PanelChrome.AddTitleBarChrome).
            _chrome = PanelChrome.AddTitleBarChrome(_panel, PanelWidth, y, Pad, OnCollapseClick);
            _chrome.DragHandle.eventMouseDown += OnTitleBarMouseDown;
            _collapseButton = _chrome.CollapseButton;
            // Move the collapse button further left so it sits to the left of the × button
            // (AddTitleBarChrome's default position is the panel's right edge = the same spot as the ×
            // button, so it is repositioned here).
            _collapseButton.relativePosition = new Vector3(
                PanelWidth - Pad - CloseButtonSize - ButtonGap - PanelChrome.CollapseButtonSize, y);

            _titleLabel = _panel.AddUIComponent<UILabel>();
            _titleLabel.text = "";
            _titleLabel.textScale = 0.9f;
            _titleLabel.width = w - CloseButtonSize - PanelChrome.CollapseButtonSize - ButtonGap * 2f;
            _titleLabel.relativePosition = new Vector3(Pad, y);

            _closeButton = _panel.AddUIComponent<UIButton>();
            _closeButton.text = "×"; // × (close button, required)
            _closeButton.size = new Vector2(CloseButtonSize, CloseButtonSize);
            _closeButton.relativePosition = new Vector3(PanelWidth - Pad - CloseButtonSize, y - 2f);
            _closeButton.textScale = 0.9f;
            _closeButton.normalBgSprite = "ButtonMenu";
            _closeButton.hoveredBgSprite = "ButtonMenuHovered";
            _closeButton.pressedBgSprite = "ButtonMenuPressed";
            _closeButton.eventClick += OnCloseClick;
            y += TitleRowHeight + 4f;

            _statusLabel = _panel.AddUIComponent<UILabel>();
            _statusLabel.textScale = 0.75f;
            _statusLabel.textColor = new Color32(220, 220, 220, 255);
            // Task33: the combination of autoSize (default true) and wordWrap was the direct cause of the
            // bug where the label width shrank word by word, so autoSize=false and wordWrap=false are fixed
            // and the width w is kept. Instead, autoHeight=true lets the label's own height follow the
            // actual number of lines (the number of "\n"s), and RecomputePanelHeight() derives the whole
            // panel's height from it on every refresh.
            _statusLabel.wordWrap = false;
            _statusLabel.autoSize = false;
            _statusLabel.autoHeight = true;
            _statusLabel.width = w;
            _statusLabel.text = "";
            _statusLabel.relativePosition = new Vector3(Pad, y);

            RecomputePanelHeight();
            _panel.isVisible = false;
            ApplyCollapsedState(); // Task40: initial application of the expanded/collapsed state (same approach as BaseInfoPanel.Build)

            if (!_loggedCreated)
            {
                _loggedCreated = true;
                ModConfig.Log("UnitInfoPanel: created");
            }
        }

        /// <summary>Click handler for the × button. Clears the selection and hides the panel (the user's explicit close action).</summary>
        private static void OnCloseClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                UnitSelection.Clear();
                Hide();
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitInfoPanel.OnCloseClick error: " + e);
            }
        }

        /// <summary>Task40: applies the current value of _collapsed to the UI (same approach as
        /// BaseInfoPanel.ApplyCollapsedState). Simple, since this panel has only one collapsible section:
        /// the status label.</summary>
        private static void ApplyCollapsedState()
        {
            if (_panel == null) return;

            if (_statusLabel != null) _statusLabel.isVisible = !_collapsed;
            _panel.height = _collapsed ? (Pad + TitleRowHeight + Pad) : _expandedHeight;

            if (_collapseButton != null)
            {
                _collapseButton.text = PanelChrome.CollapseGlyph(_collapsed);
            }
        }

        /// <summary>Click handler for the collapse toggle button (same approach as BaseInfoPanel.OnCollapseClick).</summary>
        private static void OnCollapseClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                _collapsed = !_collapsed;
                ApplyCollapsedState();
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitInfoPanel.OnCollapseClick error: " + e);
            }
        }

        /// <summary>Task40: the first mouse down on the title row (drag handle) enters "detached" mode.
        /// From then on, automatic tracking by UpdateTrackingPosition is stopped so the UIDragHandle itself
        /// can move the panel freely (avoiding contention between the per-frame position overwrite and the
        /// drag gesture). Reset to false by Hide() (deselection, the × button, or the target unit
        /// disappearing).</summary>
        private static void OnTitleBarMouseDown(UIComponent component, UIMouseEventParameter eventParam)
        {
            _detached = true;
        }

        /// <summary>Updates the title and status label text from a snapshot already copied inside the lock.</summary>
        private static void RefreshContents(UnitUiSnapshot snapshot)
        {
            if (_titleLabel != null)
            {
                _titleLabel.text = snapshot.TypeKey + "  (Tier " + snapshot.Tier + ")";
            }

            if (_statusLabel != null)
            {
                string[] names = WarfrontSettings.FactionNames;
                string factionName = snapshot.FactionId < names.Length ? names[snapshot.FactionId] : "?";

                StringBuilder sb = _statusBuilder;
                sb.Length = 0;

                sb.Append("Faction: ").Append(factionName);
                sb.Append("\nHP: ").Append(snapshot.CurrentHP.ToString("0")).Append(" / ").Append(snapshot.MaxHP.ToString("0"));
                sb.Append("\nAttack: ").Append(snapshot.Attack.ToString("0")).Append("/h  Range: ").Append(snapshot.Range.ToString("0"));
                sb.Append("\nArmor: ").Append(snapshot.Armor.ToString("0")).Append("    Speed: ").Append(snapshot.SpeedKmh.ToString("0")).Append("km/h");
                sb.Append("\nAccuracy: ").Append((snapshot.Accuracy * 100f).ToString("0")).Append("%");
                if (snapshot.AccuracyBoosted) sb.Append(" (Spotted)");
                // Task99: ammo gauge (hidden for branches with infinite ammo and for the Invader). Running out of ammo is warned about explicitly.
                if (snapshot.HasAmmoGauge)
                {
                    sb.Append("\nAmmo: ").Append((snapshot.Ammo * 100f).ToString("0")).Append("%");
                    if (snapshot.Ammo <= 0f) sb.Append("  [OUT OF AMMO]");
                }
                if (snapshot.IsSupplyTruck)
                    sb.Append("\nSupplies: ").Append((snapshot.SupplyLoad * 100f).ToString("0")).Append("%");
                sb.Append("\nStatus: ").Append(StateLabel(snapshot.State));
                sb.Append("\nTarget: ").Append(snapshot.TargetId.HasValue ? "Unit#" + snapshot.TargetId.Value : "none");
                sb.Append("\nPath: ").Append(snapshot.PathCount > 0 ? snapshot.PathIndex + "/" + snapshot.PathCount : "Direct");
                sb.Append("\nOrder: ").Append(OrderLabel(snapshot.Order)); // Task48

                _statusLabel.text = sb.ToString();
                RecomputePanelHeight();
            }
        }

        /// <summary>
        /// Task33: derives the whole panel height from the actual height of the status label (with
        /// autoHeight enabled) and rewrites it only when it changed (avoiding wasteful layout recalculation
        /// every frame).
        /// Task40: with the addition of the collapse feature, the expanded height is now cached in
        /// _expandedHeight (same approach as BaseInfoPanel.RecomputeExpandedHeight. This method is only
        /// called via RefreshContents while _collapsed==false, so it is safe to assume it always computes
        /// the "expanded height").
        /// </summary>
        private static void RecomputePanelHeight()
        {
            if (_statusLabel == null || _panel == null) return;

            float newHeight = _statusLabel.relativePosition.y + _statusLabel.height + Pad;
            if (Mathf.Abs(newHeight - _expandedHeight) > 0.01f)
            {
                _expandedHeight = newHeight;
            }

            if (!_collapsed && Mathf.Abs(_panel.height - _expandedHeight) > 0.01f)
            {
                _panel.height = _expandedHeight;
            }
        }

        private static string StateLabel(UnitState state)
        {
            switch (state)
            {
                case UnitState.Idle: return "Idle";
                case UnitState.Moving: return "Moving";
                case UnitState.Engaging: return "Engaging";
                case UnitState.Dead: return "Dead";
                default: return state.ToString();
            }
        }

        /// <summary>Task48: display label for the player's command (UnitOrder).</summary>
        private static string OrderLabel(UnitOrder order)
        {
            switch (order)
            {
                case UnitOrder.FreeAdvance: return "Advance";
                case UnitOrder.Hold: return "Hold";
                case UnitOrder.RallyHold: return "Rally & Hold";
                case UnitOrder.AiControlled: default: return "AI";
            }
        }

        /// <summary>
        /// Task32: converts the selected unit's actual render position to screen coordinates every frame and
        /// keeps the panel directly above it. If any of the prerequisites for positioning (the visual, the
        /// main camera, the UIView) is missing this frame, or the target is behind the camera, the panel is
        /// hidden for this frame only while keeping the selection (to avoid mirrored or wrongly positioned
        /// display).
        ///
        /// Coordinate conversion APIs (verified against ColossalManaged.dll via reflection/IL disassembly):
        ///   - UIView.GetAView(): static UIView
        ///   - UIView.WorldPointToGUI(Camera cam, Vector3 worldPoint): Vector2
        ///     The implementation is "Camera.WorldToScreenPoint → scale x/y by
        ///     (GetScreenResolution() / uiCamera.pixelWidth and pixelHeight respectively) →
        ///     UIView.ScreenPointToGUI", which correctly converts Unity screen coordinates (bottom-left
        ///     origin, real pixels) into UIView's virtual GUI resolution (GetScreenResolution(), the
        ///     coordinate space used by relativePosition etc.).
        ///   - UIView.ScreenPointToGUI(Vector2): Vector2 — on its own this only performs the simple Y-flip
        ///     `result.y = GetScreenResolution().y - result.y` and applies no X/Y scale correction when the
        ///     real screen pixels and UIView's virtual resolution differ (UI scale setting etc.).
        ///     Therefore this method calls Camera.WorldToScreenPoint itself only for the "behind the camera"
        ///     check, and delegates the actual conversion to GUI coordinates to WorldPointToGUI for the
        ///     reason above (both are methods on the same UIView instance obtained via UIView.GetAView()).
        /// </summary>
        private static void UpdateTrackingPosition(uint instanceId)
        {
            if (_panel == null) return;

            // Task40: while the user has already dragged the title row (detached mode), skip entirely here
            // and stop the per-frame position overwrite (preserving the position the UIDragHandle itself
            // moved the panel to). The temporary hides for when the world position / camera / UIView are
            // missing this frame (the Hide() calls below) also become unnecessary: since we are not tracking
            // at all, the panel may stay visible even without those prerequisites.
            if (_detached)
            {
                if (!_panel.isVisible) _panel.Show();
                _panel.BringToFront();
                return;
            }

            Vector3 unitPos;
            if (!UnitVisuals.TryGetPosition(instanceId, out unitPos))
            {
                Hide(); // The visual is not created yet / already destroyed this frame. Keep the selection and retry next frame.
                return;
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                Hide(); // Camera not ready (during level load etc.). Keep the selection and retry next frame.
                return;
            }

            UIView view = PanelChrome.GetCachedView(); // Task56: called every frame, so use the cached accessor
            if (view == null)
            {
                Hide();
                return;
            }

            Vector3 targetPos = unitPos + VerticalOffset;

            // Only for the behind-the-camera check. The GUI coordinates themselves are delegated to
            // WorldPointToGUI below (see the class comment: ScreenPointToGUI alone lacks the scale correction).
            Vector3 screenPoint = cam.WorldToScreenPoint(targetPos);
            if (screenPoint.z <= 0f)
            {
                // Behind the camera = it would be displayed mirrored as-is, so hide for this frame (keeping the selection).
                Hide();
                return;
            }

            Vector2 guiPoint = view.WorldPointToGUI(cam, targetPos);
            Vector2 res = view.GetScreenResolution();

            // Horizontally centered on the unit; vertically, place the whole panel above guiPoint.
            float x = guiPoint.x - _panel.width * 0.5f;
            float y = guiPoint.y - _panel.height;

            // Clamp so the whole panel never spills over any edge of the screen.
            x = Mathf.Clamp(x, 0f, Mathf.Max(0f, res.x - _panel.width));
            y = Mathf.Clamp(y, 0f, Mathf.Max(0f, res.y - _panel.height));

            _panel.relativePosition = new Vector3(x, y);

            if (!_panel.isVisible) _panel.Show();
            _panel.BringToFront();
        }
    }
}
