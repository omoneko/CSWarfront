using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// The "Model Settings" panel (Task36; Task40 added per-faction assignment, thumbnails, minimize,
    /// and dragging; Task41 scaled the panel to 1.5x, improved readability of the search field/list,
    /// and added support for asset kinds other than props).
    /// UI for assigning, per unit type (TypeKey) x faction (factionId), a currently subscribed
    /// kind (prop/building/vehicle/tree) x asset as the visual model. Openable both from the
    /// "Model Settings" button on BaseInfoPanel (Game/UI/BaseInfoPanelModelButton.cs) and from the
    /// Mod Options screen (Game/Mod.cs.OnSettingsUI, Task40); an independent, persistent panel
    /// (same approach as UnitInfoPanel/BaseInfoPanel: "create exactly one instance directly under
    /// UIView and show/hide it via isVisible"). The initial position is fixed at screen center, but
    /// Task40 added support for drag-to-move.
    ///
    /// Task41: The panel's overall dimensions, control heights, and text scales were uniformly
    /// multiplied by <see cref="UiScale"/> (each constant below is the Task40-era 1x value x1.5.
    /// Initially 2x was used, but it proved too large in-game, so it was adjusted to 1.5x).
    /// The title-row height and minimize-button size of the shared helper <see cref="PanelChrome"/>
    /// (shared with BaseInfoPanel/UnitInfoPanel) are not changed here
    /// (the request was to enlarge only this panel, so the other two panels must not be affected).
    ///
    /// Due to the 500-line limit, the code is split as follows (same policy as
    /// BaseInfoPanel/BaseInfoPanelProduction):
    ///   - This file: panel creation/destruction, skeleton layout, and minimized-state management.
    ///   - AssetAssignPanelControls.cs: unit-type dropdown / Apply, Reset-to-Default, and Close buttons.
    ///   - AssetAssignPanelFaction.cs: faction dropdown and thumbnail display (new in Task40).
    ///   - AssetAssignPanelAssetList.cs: kind dropdown / search / subscribed-only toggle / list
    ///     (new in Task41, split off from the old AssetAssignPanelControls.cs).
    ///
    /// Threading note: All public methods of this class are main-thread only (they call Unity UI APIs).
    /// It never touches WarState/MilitaryManager. It only reads/writes UnitAssetBindings (persistence
    /// of assignments) and AssetCatalog (asset enumeration, mesh resolution, thumbnail resolution),
    /// neither of which holds CS entities. When an assignment changes, UnitVisuals.DestroyAll() is
    /// called to destroy the existing visuals so the next Sync reflects the new assignment
    /// (see the caching policy of UnitMeshSource.TryResolve).
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        private const string PanelName = "CSWarfrontAssetAssignPanel";
        private const string TitleText = "Model Settings";

        internal const int MaxListItems = 300; // Upper bound on item count. Unrelated to panel dimensions, so not scaled.

        // Task41: Scale the entire panel. 2x was too large, so adjusted to 1.5x ("old" in the comments = the Task40-era 1x value).
        internal const float UiScale = 1.5f;

        internal const float PanelWidth = 570f;      // old 380f x1.5
        internal const float Pad = 12f;               // old 8f x1.5
        internal const float RowHeight = 36f;         // old 24f x1.5
        internal const float DropdownHeight = 39f;    // old 26f x1.5
        internal const float ListHeight = 330f;       // old 220f x1.5 (itemHeight is also 1.5x, so visible rows stay ~11)
        internal const float ButtonRowHeight = 39f;   // old 26f x1.5
        internal const float SectionGap = 9f;         // old 6f x1.5
        internal const float ThumbnailSize = 96f;     // old 64f x1.5

        // Task41: Width of the "asset kind" dropdown placed to the right of the search field.
        internal const float AssetKindDropdownWidth = 165f;   // old 110f x1.5

        private static UIPanel _panel;
        private static UILabel _titleLabel;
        private static UIButton _collapseButton;
        private static PanelChrome.Handles _chrome; // Task40: minimize button + drag handle in the title row
        private static UILabel _typeKeySectionLabel; // Task40: kept as a target for visibility toggling when collapsed
        private static UIDropDown _typeKeyDropdown;
        private static UILabel _currentBindingLabel;
        private static UISprite _thumbnailSprite; // Task40
        private static bool _hasThumbnail; // Task40: whether the last RefreshThumbnail found a valid thumbnail
        private static UILabel _searchSectionLabel; // Task40: kept as a target for visibility toggling when collapsed
        private static UITextField _searchField;
        private static UIDropDown _assetKindDropdown; // Task41: switch between prop/building/vehicle/tree
        private static UIButton _customOnlyToggle;
        private static UIListBox _propListBox;
        private static UILabel _truncatedLabel;
        private static UIButton _applyButton;
        private static UIButton _resetButton;
        private static UIButton _closeButton;

        // TypeKey list in the same order as LandUnitRoster.All() (category declaration order -> Tier1-5).
        // Used to map the dropdown's display labels (e.g. "Tank_T3 → (default)") to selection indices.
        private static string[] _typeKeys;

        // Asset names in the same order as _propListBox.items (post-filter, truncated to MaxListItems).
        // The Apply button takes the name at the selected index from this array.
        private static readonly List<string> _filteredAssetNames = new List<string>();

        private static bool _customOnly = true; // Default ON (specified in Task36)
        private static bool _suppressEvents;
        private static bool _loggedCreated;

        /// <summary>Task40: Collapsed state. Kept for the session, like BaseInfoPanel/UnitInfoPanel.</summary>
        private static bool _collapsed;

        /// <summary>Task47: true when this panel was temporarily hidden because the vanilla Esc menu was
        /// opened. This panel is toggle-based (there is no per-frame visibility-condition recomputation
        /// like BaseInfoPanel/UnitInfoPanel), so the fact that "the user had it open" is remembered by
        /// this flag alone, and when the menu closes we simply restore isVisible (we never touch the
        /// logical "open" state managed by Toggle/Show/Hide, so the panel is restored exactly as it was,
        /// including the _typeKeyDropdown selection, scroll position, etc.).</summary>
        private static bool _hiddenByMenu;

        /// <summary>Task40: Cached total height when expanded (same role as BaseInfoPanel._expandedHeight).</summary>
        private static float _expandedHeight;

        /// <summary>Whether the panel has been created. Used by Mod Options (Game/Mod.cs, Task40) to detect
        /// being outside the game (the extreme case where UIView itself is unusable, e.g. main menu).</summary>
        public static bool IsCreated { get { return _panel != null; } }

        /// <summary>Idempotent. If not created yet, builds once UIView is ready (same approach as the other panels).</summary>
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
                ModConfig.LogError("AssetAssignPanel.EnsureCreated error: " + e);
            }
        }

        /// <summary>Called from BaseInfoPanel's "Model Settings" button. Hides if visible, opens if hidden.</summary>
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

        /// <summary>Task40: Called from Mod Options (Game/Mod.cs). Does not close even if already open
        /// (unlike Toggle: no matter how many times the Options button is pressed, always ending up open
        /// is easier to understand). Assumes the caller has already called EnsureCreated() and confirmed
        /// _panel is non-null.</summary>
        internal static void Show()
        {
            if (_panel == null) return;

            // Rescan every time the panel opens (the "on demand" policy). A full prefab scan is expensive,
            // so it is not done every frame; only at the moment the user opens this panel do we refresh to
            // "the assets currently subscribed" (for all 4 kinds).
            AssetCatalog.Rescan();

            RefreshDropdownLabels(_typeKeyDropdown != null ? _typeKeyDropdown.selectedIndex : 0);
            RefreshAssetList();
            RefreshCurrentBindingLabel();
            RefreshPresetSlotLabels(); // Task70: refresh empty-slot display (cheap File.Exists only) each time the panel opens

            _panel.isVisible = true;
            _panel.BringToFront();
        }

        private static void Hide()
        {
            if (_panel != null) _panel.isVisible = false;
        }

        /// <summary>Task47: Called every frame from WarfrontThreadingExtension.OnUpdate. Unlike
        /// BaseInfoPanel/UnitInfoPanel's UpdateVisibility, this panel has no continuously polled
        /// visibility condition, so we only remember via _hiddenByMenu "whether it was visible when the
        /// Esc menu opened", and restore isVisible when the menu closes (Hide() is not called, which
        /// distinguishes this from the user's explicit "close" action).</summary>
        public static void UpdateGameMenuState()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi()) return; // Task56: do not touch the UI library while loading/unloading
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

        /// <summary>Called on level unload (via MilitaryManager.Reset). Destroys the panel and leaves no static state.</summary>
        public static void Destroy()
        {
            try
            {
                PanelChrome.Unsubscribe(_chrome, OnCollapseClick); // Task40
                if (_typeKeyDropdown != null) _typeKeyDropdown.eventSelectedIndexChanged -= OnTypeKeyChanged;
                DestroyFactionSection(); // Task40: unsubscribes events + resets fields
                if (_searchField != null) _searchField.eventTextChanged -= OnSearchTextChanged;
                if (_assetKindDropdown != null) _assetKindDropdown.eventSelectedIndexChanged -= OnAssetKindChanged;
                if (_customOnlyToggle != null) _customOnlyToggle.eventClick -= OnCustomOnlyClick;
                if (_propListBox != null) _propListBox.eventSelectedIndexChanged -= OnAssetSelected;
                if (_applyButton != null) _applyButton.eventClick -= OnApplyClick;
                if (_resetButton != null) _resetButton.eventClick -= OnResetClick;
                if (_copyApplyButton != null) _copyApplyButton.eventClick -= OnCopyApplyClick; // Task47
                DestroyPresetsSection(); // Task70: unsubscribes events of the clear-all button + preset row, and resets fields
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
            if (view.FindUIComponent<UIPanel>(PanelName) != null) return; // prevent double creation

            UIPanel panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
            if (panel == null)
            {
                ModConfig.LogError("AssetAssignPanel.Build: failed to create UIPanel");
                return;
            }
            _panel = panel;
            _panel.name = PanelName;
            _panel.backgroundSprite = "MenuPanel2";
            _panel.width = PanelWidth;
            _panel.isVisible = false;

            float w = PanelWidth - Pad * 2f;
            float y = Pad;

            // Task40: First add a drag handle covering the whole title row (target=_panel), then layer
            // the title label (non-interactive) and minimize button (interactive) on top (same approach
            // as BaseInfoPanel). The title-row height and button size on the PanelChrome side are shared
            // with the other two panels, so they are not scaled
            // (this is a panel-specific request; changing the shared helper would affect the other panels).
            _chrome = PanelChrome.AddTitleBarChrome(_panel, PanelWidth, y, Pad, OnCollapseClick);
            _collapseButton = _chrome.CollapseButton;

            _titleLabel = _panel.AddUIComponent<UILabel>();
            _titleLabel.text = TitleText;
            _titleLabel.textScale = 0.9f;   // unified with the other panels (only dimensions are 1.5x; text stays 1x)
            _titleLabel.relativePosition = new Vector3(Pad, y);
            y += RowHeight;

            y = AddSectionLabel("Faction", y, out _factionSectionLabel); // field is declared in AssetAssignPanelFaction.cs
            BuildFactionDropdown(Pad, y, w); // AssetAssignPanelFaction.cs
            y += DropdownHeight + SectionGap;

            y = AddSectionLabel("Unit Type", y, out _typeKeySectionLabel);
            BuildTypeKeys();
            _typeKeyDropdown = BuildTypeKeyDropdown(Pad, y, w);
            y += DropdownHeight + SectionGap;

            // Task40: Place the "current binding" label (left) and the thumbnail (right, 128x128) on the same row.
            float bindingLabelWidth = w - ThumbnailSize - SectionGap;
            _currentBindingLabel = _panel.AddUIComponent<UILabel>();
            _currentBindingLabel.textScale = 0.75f; // unified with the other panels
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

            y = AddSectionLabel("Search (partial match) / Asset Type", y, out _searchSectionLabel);

            // Task41: Place the search field (left) and the asset-kind dropdown (right, prop/building/vehicle/tree) on the same row.
            float searchWidth = w - AssetKindDropdownWidth - SectionGap;
            _searchField = BuildSearchField(Pad, y, searchWidth); // AssetAssignPanelAssetList.cs
            _assetKindDropdown = BuildAssetKindDropdown(Pad + searchWidth + SectionGap, y, AssetKindDropdownWidth); // AssetAssignPanelAssetList.cs
            y += RowHeight + SectionGap;

            _customOnlyToggle = _panel.AddUIComponent<UIButton>();
            _customOnlyToggle.textScale = 0.75f; // unified with the other panels
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
            _propListBox.itemHeight = 26; // text stays 1x, but line spacing is slightly wider
            _propListBox.itemHover = "ListItemHover";
            _propListBox.itemHighlight = "ListItemHighlight";
            _propListBox.textScale = 0.75f; // unified with the other panels
            // Task41: Against GenericPanelLight (a bright background), itemTextColor (the actual text color
            // of each row) must be dark or the list is unreadable. The old implementation set only
            // textColor, but UIListBox row rendering is dominated by itemTextColor (verified via reflection
            // to exist in ColossalManaged.dll as a Color32 property), so that one is set explicitly here
            // (textColor is kept for compatibility).
            _propListBox.textColor = new Color32(230, 230, 230, 255);
            _propListBox.itemTextColor = new Color32(20, 20, 24, 255);
            _propListBox.eventSelectedIndexChanged += OnAssetSelected;
            y += ListHeight + SectionGap;

            _truncatedLabel = _panel.AddUIComponent<UILabel>();
            _truncatedLabel.textScale = 0.65f; // unified with the other panels
            _truncatedLabel.textColor = new Color32(255, 190, 120, 255);
            _truncatedLabel.wordWrap = false;
            _truncatedLabel.autoSize = false;
            _truncatedLabel.width = w;
            _truncatedLabel.text = "";
            _truncatedLabel.relativePosition = new Vector3(Pad, y);
            y += 24f + SectionGap; // old 16f x1.5

            // Task47: "Apply to Multiple" = UI to copy the currently selected (faction, unit type)
            // assignment to other (faction, type) pairs in one operation. The scope-selection dropdown
            // (left) and the button (right) share the same row
            // (see AssetAssignPanelCopy.cs).
            y = AddSectionLabel("Apply to Multiple", y, out _copyScopeSectionLabel);
            float copyButtonWidth = w * 0.35f;
            float copyDropdownWidth = w - copyButtonWidth - SectionGap;
            _copyScopeDropdown = BuildCopyScopeDropdown(Pad, y, copyDropdownWidth);
            _copyApplyButton = BuildButton("Apply to Multiple", Pad + copyDropdownWidth + SectionGap, y, copyButtonWidth, OnCopyApplyClick);
            y += DropdownHeight + SectionGap;

            // Task70: Added "Clear All" as a 4th button on the same row as the existing 3 buttons
            // (requirement: "next to the existing buttons"). To divide the row evenly into 4, the width
            // calculation of the existing 3 buttons is changed as well.
            float buttonWidth = (w - SectionGap * 3f) / 4f;
            _applyButton = BuildButton("Apply", Pad, y, buttonWidth, OnApplyClick);
            _resetButton = BuildButton("Reset to Default", Pad + (buttonWidth + SectionGap), y, buttonWidth, OnResetClick);
            _clearAllButton = BuildClearAllButton(Pad + (buttonWidth + SectionGap) * 2f, y, buttonWidth);
            _closeButton = BuildButton("Close", Pad + (buttonWidth + SectionGap) * 3f, y, buttonWidth, OnCloseClick);
            y += ButtonRowHeight + SectionGap;

            // Task70: The "preset" row (save/load for slots 1-3, see AssetAssignPanelPresets.cs).
            y = AddSectionLabel("Preset", y, out _presetSectionLabel);
            BuildPresetRow(Pad, y, w);
            y += DropdownHeight + SectionGap;

            _presetMessageLabel = _panel.AddUIComponent<UILabel>();
            _presetMessageLabel.textScale = 0.65f; // unified with the other panels (same scale as _truncatedLabel)
            _presetMessageLabel.textColor = new Color32(255, 210, 140, 255);
            _presetMessageLabel.wordWrap = true;
            _presetMessageLabel.autoSize = false;
            _presetMessageLabel.autoHeight = true;
            _presetMessageLabel.width = w;
            _presetMessageLabel.text = "";
            _presetMessageLabel.relativePosition = new Vector3(Pad, y);
            y += 24f + Pad;

            _expandedHeight = y;
            _panel.height = y;
            CenterOnScreen(view);

            UpdateCustomOnlyLabel();
            RefreshCurrentBindingLabel();
            ApplyCollapsedState(); // apply the initial expanded/collapsed state (same approach as BaseInfoPanel.Build)

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

        /// <summary>Task40: Changed to return the label reference to the caller (used from
        /// SetSectionVisible/ApplyCollapsedState so that isVisible can be toggled in bulk when collapsed).</summary>
        private static float AddSectionLabel(string text, float y, out UILabel label)
        {
            label = _panel.AddUIComponent<UILabel>();
            label.text = text;
            label.textScale = 0.75f; // unified with the other panels
            label.textColor = new Color32(200, 200, 200, 255);
            label.relativePosition = new Vector3(Pad, y);
            return y + 27f; // old 18f x1.5
        }

        private static UIButton BuildButton(string text, float x, float y, float width, MouseEventHandler handler)
        {
            UIButton btn = _panel.AddUIComponent<UIButton>();
            btn.text = text;
            btn.textScale = 0.8f; // unified with the other panels
            btn.size = new Vector2(width, ButtonRowHeight);
            btn.relativePosition = new Vector3(x, y);
            btn.normalBgSprite = "ButtonMenu";
            btn.hoveredBgSprite = "ButtonMenuHovered";
            btn.pressedBgSprite = "ButtonMenuPressed";
            btn.eventClick += handler;
            return btn;
        }

        /// <summary>Task40: Reflects the current value of _collapsed in the UI (same approach as
        /// BaseInfoPanel.ApplyCollapsedState). Shows/hides all controls other than the title row at once.</summary>
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

        /// <summary>Toggles isVisible of all controls other than the title row (title label, minimize
        /// button, drag handle) at once. The thumbnail is shown only when "expanded and a valid thumbnail
        /// has been found" (_hasThumbnail, updated by RefreshThumbnail in AssetAssignPanelFaction.cs).</summary>
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
            if (_clearAllButton != null) _clearAllButton.isVisible = visible; // Task70
            if (_presetSectionLabel != null) _presetSectionLabel.isVisible = visible;
            if (_presetSlotDropdown != null) _presetSlotDropdown.isVisible = visible;
            if (_presetSaveButton != null) _presetSaveButton.isVisible = visible;
            if (_presetLoadButton != null) _presetLoadButton.isVisible = visible;
            if (_presetMessageLabel != null) _presetMessageLabel.isVisible = visible;
            if (_closeButton != null) _closeButton.isVisible = visible;
        }
    }
}
