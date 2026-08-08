using System;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task102 (user request "I want the military mod's buildings gathered in one tab. Hunting for
    /// them across the Police tab, Disaster Services tab, and Harbor tab is a pain"): the military
    /// construction panel.
    ///
    /// Lines up the 9 military building kinds designated in Options (4 bases + 5 fortifications) as
    /// buttons on a single panel; clicking one starts the vanilla BuildingTool with that asset
    /// (i.e. this panel effectively becomes the "military tab" without caring which construction
    /// tab an asset actually lives in). Injecting a real custom tab into the toolbar is not adopted
    /// because it depends heavily on vanilla UI internals and is fragile (design decision, approved
    /// by the user).
    ///
    /// Open/close: a hotkey (WarfrontSettings.BuildPanelKey, default Numpad4) and a small resident
    /// button (movable by dragging). Cancelling the placement tool is the ordinary vanilla
    /// operation (Esc/right click) unchanged. Kinds that are undesignated/unsubscribed are shown
    /// grayed out. The designations are reloaded every time the panel opens.
    /// All methods are main-thread only (driven from WarfrontThreadingExtension.OnUpdate).
    /// </summary>
    internal static class MilitaryBuildPanel
    {
        private static readonly BaseType[] RowTypes =
        {
            BaseType.Army, BaseType.Navy, BaseType.AirForce, BaseType.MissileBase,
            BaseType.Bunker, BaseType.ArtilleryPost, BaseType.SupplyDepot, BaseType.Trench, BaseType.CargoStation
        };
        private static readonly string[] RowDisplayNames =
        {
            WarfrontStrings.BuildPanel_RowArmyBase, WarfrontStrings.BuildPanel_RowNavalBase,
            WarfrontStrings.BuildPanel_RowAirBase, WarfrontStrings.BuildPanel_RowMissileBase,
            WarfrontStrings.BuildPanel_RowBunker, WarfrontStrings.BuildPanel_RowArtilleryPosition,
            WarfrontStrings.BuildPanel_RowSupplyDepot, WarfrontStrings.BuildPanel_RowTrench,
            WarfrontStrings.BuildPanel_RowCargoStation
        };

        private const float PanelWidth = 240f;
        private const float RowHeight = 30f;
        private const float Pad = 8f;

        /// <summary>Extra height for the defense-layout button row at the bottom (Task114).</summary>
        private const float DefenseRowHeight = 26f;

        private static UIPanel _panel;
        private static UIButton[] _rowButtons;
        private static UIButton _toggleButton;

        /// <summary>Whether the construction tool was started via this panel (for the Esc-cancel handling).</summary>
        private static bool _placementActive;

        /// <summary>Every frame (main thread): creation, hotkey, menu synchronization, Esc cancel.</summary>
        public static void Update()
        {
            if (!PanelChrome.IsGameReadyForUi()) return;

            EnsureCreated();

            // Task114: relay result messages from the sim-side defense-layout processing (the sim
            // thread cannot touch Unity UI, so it queues strings and we toast them here).
            string simMessage;
            while (MilitaryManager.TryDequeueUiToast(out simMessage))
                CommandToast.Show(simMessage);

            // Live-game bug fix (user report "pressing Esc does not cancel out of build mode"):
            // in vanilla, the construction menu (GeneratedGroupPanel) receives Esc and clears the
            // tool, but this panel calls SetTool from outside that menu, so that path does not
            // exist. Detect Esc here, explicitly return to DefaultTool, and close the pause menu
            // that opened in the same frame (i.e. reproduce the vanilla feel of "first Esc cancels
            // placement, second Esc opens the menu").
            if (_placementActive)
            {
                ToolBase current = ToolsModifierControl.toolController != null
                    ? ToolsModifierControl.toolController.CurrentTool : null;
                if (!(current is BuildingTool))
                {
                    _placementActive = false; // placement finished / already switched to another tool
                }
                else if (Input.GetKeyDown(KeyCode.Escape))
                {
                    ToolsModifierControl.SetTool<DefaultTool>();
                    _placementActive = false;
                    try { UIView.library.Hide("PauseMenu"); } catch (Exception) { /* ignore if not open */ }
                    return;
                }
            }

            if (_panel != null && _panel.isVisible && PanelChrome.IsGameMenuOpen())
            {
                _panel.Hide(); // hidden while the ESC menu is open, same as the other panels
                return;
            }

            if (UIView.HasInputFocus()) return;
            if (Input.GetKeyDown(WarfrontSettings.BuildPanelKey)) Toggle();
        }

        public static void Toggle()
        {
            if (_panel == null) return;
            if (_panel.isVisible) _panel.Hide();
            else
            {
                RefreshRows(); // reload the designations every open (also reflects changes made in Options just now)
                _panel.Show();
                _panel.BringToFront();
            }
        }

        private static void EnsureCreated()
        {
            if (_panel != null && _toggleButton != null) return;
            UIView view = PanelChrome.GetCachedView();
            if (view == null) return;

            if (_toggleButton == null)
            {
                _toggleButton = view.AddUIComponent(typeof(UIButton)) as UIButton;
                _toggleButton.name = "WarfrontBuildToggle";
                _toggleButton.text = WarfrontStrings.BuildPanel_ToggleButtonText;
                _toggleButton.tooltip = WarfrontStrings.BuildPanel_ToggleTooltip;
                _toggleButton.textScale = 0.8f;
                _toggleButton.size = new Vector2(36f, 36f);
                _toggleButton.normalBgSprite = "ButtonMenu";
                _toggleButton.hoveredBgSprite = "ButtonMenuHovered";
                _toggleButton.pressedBgSprite = "ButtonMenuPressed";
                // Task110: Place it in the top row of screen icons (to the right of the two round
                // buttons at top-left) (user request).
                _toggleButton.relativePosition = new Vector3(150f, 10f);
                // Live-game bug fix: a UIDragHandle covering the whole button can steal clicks, so
                // the resident button is made non-draggable at a fixed position (click reliability
                // takes priority).
                _toggleButton.eventClick += (c, e) => Toggle();
            }

            if (_panel == null)
            {
                _panel = view.AddUIComponent(typeof(UIPanel)) as UIPanel;
                _panel.name = "WarfrontBuildPanel";
                _panel.backgroundSprite = "MenuPanel2";
                _panel.width = PanelWidth;
                _panel.height = Pad + 28f + RowTypes.Length * (RowHeight + 4f) + DefenseRowHeight + 4f + Pad;
                _panel.relativePosition = new Vector3(150f, 55f); // Task110: opens directly below the top-row button
                _panel.isVisible = false;

                // Live-game bug fix: the drag handle used to cover even the x button and steal its
                // clicks, so shrink the handle width to stop just short of the x button.
                UIDragHandle drag = _panel.AddUIComponent<UIDragHandle>();
                drag.target = _panel;
                drag.size = new Vector2(PanelWidth - 34f, 28f);
                drag.relativePosition = Vector3.zero;

                UILabel title = _panel.AddUIComponent<UILabel>();
                title.text = WarfrontStrings.BuildPanel_Title;
                title.textScale = 0.9f;
                title.relativePosition = new Vector3(Pad, 8f);

                UIButton close = _panel.AddUIComponent<UIButton>();
                close.text = "x";
                close.textScale = 0.8f;
                close.size = new Vector2(22f, 22f);
                close.normalBgSprite = "ButtonMenu";
                close.hoveredBgSprite = "ButtonMenuHovered";
                close.pressedBgSprite = "ButtonMenuPressed";
                close.relativePosition = new Vector3(PanelWidth - 22f - 4f, 4f);
                close.eventClick += (c, e) => _panel.Hide();
                close.BringToFront();

                _rowButtons = new UIButton[RowTypes.Length];
                for (int i = 0; i < RowTypes.Length; i++)
                {
                    UIButton b = _panel.AddUIComponent<UIButton>();
                    b.size = new Vector2(PanelWidth - Pad * 2f, RowHeight);
                    b.textScale = 0.85f;
                    b.textHorizontalAlignment = UIHorizontalAlignment.Left;
                    b.textPadding = new RectOffset(8, 4, 6, 0);
                    b.normalBgSprite = "ButtonMenu";
                    b.hoveredBgSprite = "ButtonMenuHovered";
                    b.pressedBgSprite = "ButtonMenuPressed";
                    b.disabledBgSprite = "ButtonMenuDisabled";
                    b.relativePosition = new Vector3(Pad, Pad + 28f + i * (RowHeight + 4f));
                    int rowIndex = i; // copy for the closure
                    b.eventClick += (c, e) => OnRowClick(rowIndex);
                    _rowButtons[i] = b;
                }

                // Task114 (Workshop request): save/rebuild the defense layout with one click each.
                float defenseRowY = Pad + 28f + RowTypes.Length * (RowHeight + 4f);
                float halfWidth = (PanelWidth - Pad * 2f - 4f) * 0.5f;

                UIButton save = _panel.AddUIComponent<UIButton>();
                save.text = WarfrontStrings.BuildPanel_RegisterDefenseButton;
                save.tooltip = WarfrontStrings.BuildPanel_RegisterDefenseTooltip;
                save.textScale = 0.7f;
                save.size = new Vector2(halfWidth, DefenseRowHeight);
                save.normalBgSprite = "ButtonMenu";
                save.hoveredBgSprite = "ButtonMenuHovered";
                save.pressedBgSprite = "ButtonMenuPressed";
                save.relativePosition = new Vector3(Pad, defenseRowY);
                save.eventClick += (c, e) => MilitaryManager.RequestRegisterDefenseLayout();

                UIButton rebuild = _panel.AddUIComponent<UIButton>();
                rebuild.text = WarfrontStrings.BuildPanel_RebuildDefenseButton;
                rebuild.tooltip = WarfrontStrings.BuildPanel_RebuildDefenseTooltip;
                rebuild.textScale = 0.7f;
                rebuild.size = new Vector2(halfWidth, DefenseRowHeight);
                rebuild.normalBgSprite = "ButtonMenu";
                rebuild.hoveredBgSprite = "ButtonMenuHovered";
                rebuild.pressedBgSprite = "ButtonMenuPressed";
                rebuild.relativePosition = new Vector3(Pad + halfWidth + 4f, defenseRowY);
                rebuild.eventClick += (c, e) => MilitaryManager.RequestRebuildDefenses();

                RefreshRows();
            }
        }

        private static void RefreshRows()
        {
            if (_rowButtons == null) return;
            for (int i = 0; i < RowTypes.Length; i++)
            {
                UIButton b = _rowButtons[i];
                if (b == null) continue;

                string assetName;
                bool designated = BaseBuildingDesignation.TryGet(RowTypes[i], out assetName);
                BuildingInfo info = designated ? PrefabCollection<BuildingInfo>.FindLoaded(assetName) : null;

                if (info != null)
                {
                    b.text = RowDisplayNames[i];
                    b.tooltip = assetName;
                    b.isEnabled = true;
                }
                else
                {
                    // Undesignated (no building chosen in Options) / designated asset not loaded.
                    b.text = RowDisplayNames[i] + (designated ? WarfrontStrings.BuildPanel_RowSuffixAssetMissing : WarfrontStrings.BuildPanel_RowSuffixNotSet);
                    b.tooltip = WarfrontStrings.BuildPanel_RowTooltipNotSet;
                    b.isEnabled = false;
                }
            }
        }

        private static void OnRowClick(int rowIndex)
        {
            try
            {
                if (rowIndex < 0 || rowIndex >= RowTypes.Length) return;
                string assetName;
                if (!BaseBuildingDesignation.TryGet(RowTypes[rowIndex], out assetName)) return;
                BuildingInfo info = PrefabCollection<BuildingInfo>.FindLoaded(assetName);
                if (info == null)
                {
                    CommandToast.Show(string.Format(WarfrontStrings.BuildPanel_ToastAssetNotLoaded, assetName));
                    return;
                }

                // Task106: Trenches use line-laying mode (continuous placement between two
                // right-clicked points. It does not use the vanilla placement tool, so it is not
                // subject to the "must be placed adjacent to a road" requirement).
                if (RowTypes[rowIndex] == BaseType.Trench)
                {
                    TrenchLineTargeting.Begin();
                    if (_panel != null) _panel.Hide(); // close so it does not get in the way of ground clicks
                    return;
                }

                // Start the vanilla construction tool directly with this prefab (BuildingTool is a
                // permanently-registered vanilla tool, so no pre-registration is needed for
                // SetTool<T>). Placement, rotation, and cancellation afterwards are the ordinary
                // construction operations unchanged.
                BuildingTool tool = ToolsModifierControl.SetTool<BuildingTool>();
                if (tool == null)
                {
                    ModConfig.LogError("MilitaryBuildPanel: BuildingTool unavailable");
                    return;
                }
                tool.m_prefab = info;
                tool.m_relocate = 0;
                _placementActive = true; // make it subject to the Esc-cancel handling (Update)
                CommandToast.Show(string.Format(WarfrontStrings.BuildPanel_ToastPlacing, RowDisplayNames[rowIndex]));
            }
            catch (Exception e)
            {
                ModConfig.LogError("MilitaryBuildPanel.OnRowClick error: " + e);
            }
        }

        /// <summary>On level unload (via WarfrontLoadingExtension): discard references so the next load recreates them.</summary>
        public static void Reset()
        {
            _panel = null;
            _rowButtons = null;
            _toggleButton = null;
            _placementActive = false;
        }
    }
}
