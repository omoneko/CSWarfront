using System;
using System.Text;
using ColossalFramework.UI;
using CSWarfront.Core;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Info panel for military base buildings (Options-designated buildings, <see cref="BaseBuildingDesignation"/>)
    /// (Task25; names/comments updated to the current approach in Task82 when the duplicated
    /// electricity-tab prefab mechanism was removed).
    /// While the vanilla building info panel (CityServiceWorldInfoPanel, the designated building's own panel)
    /// is shown, if the selected building is a registered logical base (MilitaryManager.State.Bases),
    /// display our own small UIPanel next to it with an owning-faction dropdown and status.
    ///
    /// Rather than AddUIComponent-ing directly into the vanilla panel, we create an independent UIPanel
    /// directly under UIView and make it follow to the right of the vanilla panel (or left if off-screen):
    /// the vanilla BuildingWorldInfoPanel family internally recomputes its own child layout on every
    /// RefreshData etc., so inserting ourselves as a direct child risks position corruption/disappearance
    /// on future vanilla-side refreshes (the same kind of regeneration as DisastersPanel's RefreshPanel).
    /// An independent panel is unaffected (the same "permanent panel directly under UIView" approach as
    /// MissileDisaster.Game.UI.MissilePanel).
    ///
    /// Threading note: all public methods of this class are main-thread only (they call Unity UI APIs).
    /// They are expected to be called every frame from WarfrontThreadingExtension.OnUpdate. They never
    /// touch WarState directly; reads/writes go only through MilitaryManager.TryGetBaseSnapshot /
    /// TrySetBaseOwner (_stateLock is taken only briefly inside those and is never held here).
    /// </summary>
    internal static partial class BaseInfoPanel
    {
        private const string PanelName = "CSWarfrontBaseInfoPanel";
        private const string VanillaPanelName = "CityServiceWorldInfoPanel";
        private const string TitleText = "CSWarfront Military Base";

        // Task33: at the old 260px, status lines (especially long single lines like
        // "Producing: MechInfantry_T5  62%  (3.0h left)") did not fit the wordWrap-oriented width,
        // and on real hardware word-level wrapping caused overflow out of the panel. The policy was
        // changed to pin the labels to wordWrap=false so one line is always rendered as one line, and
        // widen the panel until the longest line fits at textScale=0.75 (character width estimated at
        // full-width≈16px / half-width≈9px @ scale1.0 plus a safety margin; the longest line
        // "Producing: ..." estimates to about 269px, and 340px (inner width 324px) is reserved to
        // allow for deviation from the actual measured font).
        private const float PanelWidth = 340f;
        private const float Pad = 8f;
        private const float TitleRowHeight = 22f;
        private const float DropdownHeight = 28f;
        private const float VanillaGap = 8f;

        private static UIPanel _panel;
        private static UILabel _titleLabel;
        private static UIButton _collapseButton;
        private static PanelChrome.Handles _chrome; // Task40: collapse button + drag handle on the title row
        private static UILabel _factionSectionLabel;
        private static UIDropDown _factionDropdown;
        private static UILabel _statusLabel;

        private static ushort _currentBaseId; // 0 = not shown (CS building id 0 means "none")
        private static bool _suppressDropdownEvent;
        private static bool _loggedCreated;

        /// <summary>Task40: becomes true after the user drags the title row; while true, the per-frame
        /// auto-follow via PositionNextToVanilla is stopped (to keep the dragged position).
        /// When the panel "closes" (vanilla panel closes / building deselected etc., via Hide()) this
        /// resets to false, and the next selected base follows the vanilla panel as usual.</summary>
        private static bool _detachedFromVanilla;

        /// <summary>Buffer reused every frame by RefreshContents (Task30: reuse via
        /// StringBuilder.Clear() to avoid per-frame allocations from string concatenation).</summary>
        private static readonly StringBuilder _statusBuilder = new StringBuilder(256);

        /// <summary>UI setting for the panel collapse toggle (Task27). Persists across deselect/reselect
        /// within a session (not changed by Hide/UpdateVisibility; reset only on Destroy = level unload).</summary>
        private static bool _collapsed;

        /// <summary>Total height when expanded. Fixed once in Build(); restore exactly from this when
        /// going collapsed -&gt; expanded (recomputing from the shrunken size would accumulate errors,
        /// so always use this cached value).</summary>
        private static float _expandedHeight;

        /// <summary>Multiplier applied to the panel height. Text size and width stay unchanged; only
        /// vertical space gets extra room.</summary>
        private const float VerticalScale = 1.5f;

        /// <summary>
        /// Idempotent. If not yet created, build our panel once the vanilla building info panel type is
        /// obtainable from the library (same "poll every frame and create exactly once when conditions
        /// are met" approach as MissileDisasterButton.EnsureAttached).
        /// </summary>
        public static void EnsureCreated()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi()) return; // Task56: do not touch the UI library while loading/unloading
                if (_panel != null) return;
                if (!EnsureVanillaPanelsCached()) return; // UI not initialized yet. Retry next frame.
                Build();
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.EnsureCreated error: " + e);
            }
        }

        /// <summary>
        /// Call every main-thread frame. Show and refresh our panel only while the vanilla building info
        /// panel is visible and the selected building is a registered logical base; otherwise hide it.
        /// </summary>
        public static void UpdateVisibility()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi()) return; // Task56: do not touch the UI library while loading/unloading
                if (_panel == null) return; // waiting for EnsureCreated

                // Task47: while the vanilla Esc menu is open, skip this frame's processing entirely and
                // hide only the raw UIPanel (never touching logic state such as
                // _currentBaseId/_detachedFromVanilla). Why not go through Hide(): Hide() is a strong
                // operation meaning "vanilla panel closed / building deselected" and resets
                // _currentBaseId=0 and _detachedFromVanilla=false, which would prevent returning to the
                // same base's panel normally on the frame after the menu closes.
                if (PanelChrome.IsGameMenuOpen())
                {
                    if (_panel.isVisible) _panel.Hide();
                    return;
                }

                WorldInfoPanel vanilla = TryGetVisibleVanillaPanel();
                if (vanilla == null)
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
                    Hide(); // vanilla panel is showing a non-military-base building, or it is unregistered
                    return;
                }

                _currentBaseId = buildingId;
                // While collapsed only the title row is visible, so rebuilding the dropdown/status is
                // wasted work (Task30: this runs every frame, so skip it reliably here). _currentBaseId
                // and the snapshot itself are still updated every frame so the correct contents appear
                // the instant the panel is expanded.
                if (!_collapsed) RefreshContents(snapshot);
                // Task33: positioning (including bottom-edge screen clamping) is done after this frame's
                // height is finalized. The old implementation positioned before RefreshContents, so on
                // frames where content grew and the height changed, clamping used a height one frame
                // stale and the panel was misplaced.
                // Task40: while the user has dragged the panel (_detachedFromVanilla), skip this and keep
                // the post-drag position (so per-frame auto-follow does not fight the drag operation).
                if (!_detachedFromVanilla) PositionNextToVanilla(vanilla);
                if (!_panel.isVisible) _panel.Show();
                _panel.BringToFront();
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseInfoPanel.UpdateVisibility error: " + e);
            }
        }

        /// <summary>Call on level unload (via MilitaryManager.Reset). Destroys the panel leaving no static state.</summary>
        public static void Destroy()
        {
            try
            {
                if (_factionDropdown != null) _factionDropdown.eventSelectedIndexChanged -= OnFactionSelected;
                PanelChrome.Unsubscribe(_chrome, OnCollapseClick); // Task40
                if (_chrome != null && _chrome.DragHandle != null) _chrome.DragHandle.eventMouseDown -= OnTitleBarMouseDown;
                DestroyModelButtonSection(); // Task36: unsubscribe events + reset fields
                DestroyProductionSection(); // Task34: unsubscribe events + reset fields
                DestroyMissileSection(); // Task63: unsubscribe events + reset fields
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
                _chrome = null;
                _factionSectionLabel = null;
                _factionDropdown = null;
                _statusLabel = null;
                _currentBaseId = 0;
                _suppressDropdownEvent = false;
                _collapsed = false;
                _expandedHeight = 0f;
                _vanillaPanels = null; // Task115: drop the cached panel references (next level re-finds them)
                _detachedFromVanilla = false;
            }
        }

        private static void Hide()
        {
            if (_panel != null && _panel.isVisible) _panel.Hide();
            _currentBaseId = 0;
            // Task40: this counts as a "close" (vanilla panel closed / building deselected etc.), so the
            // next selection returns to normal auto-follow. The temporary hide path inside
            // UpdateTrackingPosition does not pass through here (PositionNextToVanilla itself never calls
            // Hide(), so this is not reset accidentally mid-drag).
            _detachedFromVanilla = false;
        }

        /// <summary>Task115 (Workshop report "vanilla bunker ruins work as bunkers but show no
        /// panel"): fortifications can be designated from ANY building asset, and each asset
        /// category opens a different vanilla WorldInfoPanel subclass — the old implementation only
        /// followed CityServiceWorldInfoPanel, so our panel never appeared next to e.g. a ruins
        /// info panel. All WorldInfoPanel instances are cached once (the library set is static for
        /// the session) and we follow whichever one is visible.</summary>
        private static WorldInfoPanel[] _vanillaPanels;

        private static bool EnsureVanillaPanelsCached()
        {
            if (_vanillaPanels != null && _vanillaPanels.Length > 0) return true;
            // UIView.library.Get<T> returns null (not an exception) when the panel is not yet
            // registered/created, so "not ready" is treated as a normal path here (avoids log spam
            // every frame). The city-service panel doubles as the "library initialized" probe.
            if (UIView.library == null ||
                UIView.library.Get<CityServiceWorldInfoPanel>(VanillaPanelName) == null) return false;
            // FindObjectsOfTypeAll also finds panels whose GameObject is currently inactive
            // (hidden info panels may be deactivated); too expensive per frame but fine once.
            _vanillaPanels = Resources.FindObjectsOfTypeAll<WorldInfoPanel>();
            return _vanillaPanels != null && _vanillaPanels.Length > 0;
        }

        private static WorldInfoPanel TryGetVisibleVanillaPanel()
        {
            if (!EnsureVanillaPanelsCached()) return null;
            for (int i = 0; i < _vanillaPanels.Length; i++)
            {
                WorldInfoPanel p = _vanillaPanels[i];
                if (p != null && p.component != null && p.component.isVisible) return p;
            }
            return null;
        }

        private static void Build()
        {
            UIView view = UIView.GetAView();
            if (view == null) return;
            if (view.FindUIComponent<UIPanel>(PanelName) != null) return; // prevent double creation

            UIPanel panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
            if (panel == null)
            {
                ModConfig.LogError("BaseInfoPanel.Build: failed to create UIPanel");
                return;
            }
            _panel = panel;
            _panel.name = PanelName;
            _panel.backgroundSprite = "MenuPanel2";
            _panel.width = PanelWidth;

            float w = PanelWidth - Pad * 2f;
            float y = Pad;

            // Task40: add the drag handle covering the whole title row (target=_panel) first, then layer
            // the title label (non-interactive, lets clicks pass through) and the collapse button
            // (interactive) on top. Because the button is added later, its clicks are not intercepted by
            // the drag handle.
            _chrome = PanelChrome.AddTitleBarChrome(_panel, PanelWidth, y, Pad, OnCollapseClick);
            _chrome.DragHandle.eventMouseDown += OnTitleBarMouseDown;
            _collapseButton = _chrome.CollapseButton;

            _titleLabel = _panel.AddUIComponent<UILabel>();
            _titleLabel.text = WarfrontStrings.BaseInfo_Title;
            _titleLabel.textScale = 0.9f;
            _titleLabel.relativePosition = new Vector3(Pad, y);
            y += TitleRowHeight;

            y = AddSectionLabel(WarfrontStrings.BaseInfo_SectionFaction, Pad, y, out _factionSectionLabel);
            _factionDropdown = BuildFactionDropdown(Pad, y, w);
            y += DropdownHeight + 8f;

            y = BuildModelButtonSection(Pad, y, w); // Task36: opens the model-assignment UI for subscribed props

            y += 4f;
            _statusLabel = _panel.AddUIComponent<UILabel>();
            _statusLabel.textScale = 0.75f;
            _statusLabel.textColor = new Color32(220, 220, 220, 255);
            // Task33: the combination of autoSize (default true) and wordWrap shrank the label width to
            // word granularity, which was the direct cause of a bug where even a short single line like
            // "Faction: Blue (HQ)" wrapped at every word and overflowed the panel; so pin
            // autoSize=false and wordWrap=false and keep the width at w. Instead, autoHeight=true lets
            // the label's own height track the actual line count (number of "\n"), and
            // RecomputeExpandedHeight() derives the whole panel height from that.
            _statusLabel.wordWrap = false;
            _statusLabel.autoSize = false;
            _statusLabel.autoHeight = true;
            _statusLabel.width = w;
            _statusLabel.text = "";
            _statusLabel.relativePosition = new Vector3(Pad, y);

            BuildProductionSection(w); // Task34: auto-produce toggle / order / cancel UI. Split into BaseInfoPanelProduction.cs.
            BuildMissileSection(w); // Task63: ballistic-missile-base-only UI (stockpile/build/launch). Split into BaseInfoPanelMissile.cs.

            RecomputeExpandedHeight();
            _panel.isVisible = false;
            ApplyCollapsedState(); // initial apply of expanded/collapsed (_collapsed persists within the session, normally false)

            if (!_loggedCreated)
            {
                _loggedCreated = true;
                ModConfig.Log("BaseInfoPanel: created");
            }
        }

        /// <summary>Applies the current value of _collapsed to the UI (show/hide, panel height, button
        /// caption). Called on toggle click and right after panel creation (restoring the persisted
        /// previous state).</summary>
        private static void ApplyCollapsedState()
        {
            if (_panel == null) return;

            if (_factionSectionLabel != null) _factionSectionLabel.isVisible = !_collapsed;
            if (_factionDropdown != null) _factionDropdown.isVisible = !_collapsed;
            ApplyModelButtonCollapsedState(_collapsed); // Task36
            if (_statusLabel != null) _statusLabel.isVisible = !_collapsed;
            // Task63: while collapsed, hide both. Which one is actually shown when expanded is
            // re-decided correctly each time by the next RefreshContents
            // (RefreshProductionSection/RefreshMissileSection) based on the selected base's type
            // (_lastIsMissileBase).
            ApplyProductionCollapsedState(_collapsed); // Task34
            ApplyMissileSectionCollapsedState(_collapsed); // Task63

            _panel.height = _collapsed ? (Pad + TitleRowHeight + Pad) : _expandedHeight;

            if (_collapseButton != null)
            {
                _collapseButton.text = PanelChrome.CollapseGlyph(_collapsed);
            }
        }

        /// <summary>Click handler for the collapse toggle button. Only flips _collapsed and applies it to
        /// the UI; never touches MilitaryManager state (a purely visual UI setting).</summary>
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
        /// Dropdown selection-change handler. _suppressDropdownEvent prevents an infinite loop where the
        /// state-to-UI application in RefreshContents (writing selectedIndex back) would call this
        /// handler again.
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

        /// <summary>Updates the dropdown selection and status label text from a snapshot already copied while holding the lock.</summary>
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
                    : WarfrontStrings.BaseInfo_Unaffiliated;

                StringBuilder sb = _statusBuilder;
                sb.Length = 0;

                // Task61: show the base type and producible domains first (with the addition of naval/air
                // bases, let the player see at a glance what this base can produce).
                sb.Append(WarfrontStrings.BaseInfo_StatusTypePrefix).Append(BaseTypeLabel(snapshot.Type));
                sb.Append(WarfrontStrings.BaseInfo_StatusCanProducePrefix).Append(SpawnableDomainsLabel(snapshot.SpawnableDomains));

                sb.Append(WarfrontStrings.BaseInfo_StatusFactionPrefix).Append(ownerName);
                if (snapshot.IsHeadquarters) sb.Append(WarfrontStrings.BaseInfo_StatusHqSuffix);

                sb.Append(WarfrontStrings.BaseInfo_StatusHpPrefix).Append(snapshot.CurrentHP.ToString("0")).Append(" / ").Append(snapshot.MaxHP.ToString("0"));
                sb.Append(WarfrontStrings.BaseInfo_StatusTreasuryPrefix).Append(snapshot.OwnerTreasury.ToString("0"));
                // Task99: three-resource economy + supplies (residential -> Manpower, commercial/office -> Treasury, industrial -> Production).
                sb.Append(WarfrontStrings.BaseInfo_StatusManpowerPrefix).Append(snapshot.OwnerManpower.ToString("0"))
                  .Append(WarfrontStrings.BaseInfo_StatusProductionPrefix).Append(snapshot.OwnerProduction.ToString("0"))
                  .Append(WarfrontStrings.BaseInfo_StatusSuppliesPrefix).Append(snapshot.OwnerSupplyStock.ToString("0"));

                // Task101: field-fortification status display (only for the relevant types).
                if (snapshot.Type == BaseType.SupplyDepot || snapshot.Type == BaseType.CargoStation)
                {
                    sb.Append(WarfrontStrings.BaseInfo_StatusStoredSuppliesPrefix).Append(snapshot.StoredSupplies.ToString("0"))
                      .Append(" / ").Append(FortificationRules.StoredSupplyCap(snapshot.Type).ToString("0"));
                    if (snapshot.Type == BaseType.CargoStation)
                        sb.Append(snapshot.RailConnected ? WarfrontStrings.BaseInfo_StatusRailConnected : WarfrontStrings.BaseInfo_StatusRailNotConnected);
                }
                else if (snapshot.Type == BaseType.Bunker || snapshot.Type == BaseType.ArtilleryPost)
                {
                    sb.Append(WarfrontStrings.BaseInfo_StatusFortAmmoPrefix).Append((snapshot.FortAmmo * 100f).ToString("0")).Append("%");
                    if (snapshot.FortAmmo <= 0f) sb.Append(WarfrontStrings.BaseInfo_StatusOutOfAmmo);
                }

                // Task35: income from the development of occupied territory was already implemented, but
                // its magnitude was small and it never appeared in the UI at all, so it looked
                // "unimplemented". Displaying it even when 0 communicates that fact.
                sb.Append(WarfrontStrings.BaseInfo_StatusIncomePrefix).Append(snapshot.LastIncome.ToString("0.0")).Append(WarfrontStrings.BaseInfo_StatusIncomeSuffix);

                sb.Append(WarfrontStrings.BaseInfo_StatusTechTierPrefix).Append(snapshot.OwnerUnlockedTier);
                if (snapshot.OwnerUnlockedTier >= 5)
                {
                    sb.Append(WarfrontStrings.BaseInfo_StatusTierMaxSuffix);
                }
                else
                {
                    sb.Append(WarfrontStrings.BaseInfo_StatusResearchPrefix).Append(snapshot.OwnerResearchPoints.ToString("0"))
                      .Append(WarfrontStrings.BaseInfo_StatusResearchNextSeparator).Append(snapshot.OwnerNextTierCost.ToString("0")).Append(")");
                }

                sb.Append(WarfrontStrings.BaseInfo_StatusUnitsPrefix).Append(snapshot.OwnerUnitCount);

                if (string.IsNullOrEmpty(snapshot.ProducingTypeKey))
                {
                    sb.Append(WarfrontStrings.BaseInfo_StatusProducingNone);
                }
                else
                {
                    float pct = Mathf.Clamp01(snapshot.ProducingProgress) * 100f;
                    float remainHours = (1f - Mathf.Clamp01(snapshot.ProducingProgress)) * snapshot.ProducingBuildTime;
                    if (remainHours < 0f) remainHours = 0f;
                    sb.Append(WarfrontStrings.BaseInfo_StatusProducingPrefix).Append(snapshot.ProducingTypeKey).Append("  ").Append(pct.ToString("0")).Append("%")
                      .Append("  (").Append(remainHours.ToString("0.0")).Append(WarfrontStrings.BaseInfo_StatusHoursLeftSuffix);
                }

                int waiting = snapshot.QueueCount - (string.IsNullOrEmpty(snapshot.ProducingTypeKey) ? 0 : 1);
                if (waiting > 0) sb.Append(WarfrontStrings.BaseInfo_StatusQueuedPrefix).Append(waiting);

                if (snapshot.CaptureGraceHours > 0f)
                    sb.Append(WarfrontStrings.BaseInfo_StatusCaptureGracePrefix).Append(snapshot.CaptureGraceHours.ToString("0.0")).Append("h");

                _statusLabel.text = sb.ToString();
                RefreshProductionSection(snapshot); // Task34: re-place the production section below the status lines
                // Task63: missile-base-only section. Placed from the same starting Y as the unit
                // production section (directly below the status lines); since they are mutually
                // exclusive, starting from the same position causes no overlap.
                RefreshMissileSection(snapshot, _statusLabel.relativePosition.y + _statusLabel.height + ProductionRowGap);
                RecomputeExpandedHeight();
            }
        }

    }
}
