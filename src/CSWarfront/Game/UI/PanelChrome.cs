using System;
using ColossalFramework;
using ColossalFramework.UI;
using CSWarfront.Game;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task40: Small shared helper that builds the "title-row minimize toggle" and "drag-to-move"
    /// behavior common to the three panels BaseInfoPanel/UnitInfoPanel/AssetAssignPanel. It stays
    /// completely out of each panel's individual logic (what to collapse, what to track, etc.) and
    /// only provides UI component creation plus a minimal set of visual constants.
    ///
    /// Drag-to-move: a <see cref="ColossalFramework.UI.UIDragHandle"/> (`target` (UIComponent),
    /// `size` (Vector2), `relativePosition` (Vector3) verified to exist via reflection;
    /// ColossalManaged.dll) is added first as a transparent child component covering the entire
    /// title row (panel width x TitleRowHeight), with `target` set to the panel itself. The title
    /// label (non-interactive, passes clicks through by default) and the minimize button
    /// (interactive, added later so it stacks on top of the drag handle) are created by the caller,
    /// so add the label/button AFTER the drag handle returned by this helper (UI components are
    /// layered front-most in addition order, so the button's clicks are not intercepted by the
    /// drag handle).
    ///
    /// Minimize toggle: only the glyph strings are provided ("–" = expanded / collapsing turns
    /// –→+, "+" = collapsed). The actual collapse targets (which components to hide, how to restore
    /// the panel height) differ greatly per panel, so this helper only creates the button and
    /// subscribes the click handler; the ApplyCollapsedState-equivalent logic stays in the caller
    /// (each panel).
    /// </summary>
    internal static class PanelChrome
    {
        public const float TitleRowHeight = 22f;
        public const float CollapseButtonSize = 20f;

        private const string CollapseGlyphExpanded = "–"; // – (minimize = clicking collapses)
        private const string CollapseGlyphCollapsed = "+";     // + (expand = clicking opens)

        // Task47: Component name of the vanilla pause/ESC menu (PauseMenu, the list screen with
        // "Quit", "Options" etc. opened by Esc). Retrieval via UIView.library.Get&lt;T&gt;(name) follows
        // exactly the same established pattern (type name = registered name) BaseInfoPanel uses to
        // obtain CityServiceWorldInfoPanel.
        // Verification method and selection rationale (confirmed by reflecting over
        // ColossalManaged.dll/Assembly-CSharp.dll in PowerShell):
        //   - PauseMenu : MenuPanel : ColossalFramework.UI.UICustomControl. UICustomControl exposes
        //     `UIComponent component { get; }`, whose isVisible is exactly the visibility state of
        //     the vanilla "pause/options menu opened by Esc" (this panel itself is what the Esc
        //     toggle drives).
        //   - Other APIs considered as candidates but rejected:
        //     - Singleton&lt;SimulationManager&gt;.instance.SimulationPaused ... also becomes true when
        //       the user manually presses the "pause" button, so it cannot be distinguished from
        //       the case where the Esc menu is not open (the task requirements explicitly labeled
        //       this "insufficient").
        //     - UIView.HasModalInput() ... a broader concept that is true whenever anything is on
        //       the PushModal stack. UIDropDown popups (used heavily, including by this mod) do not
        //       use the modal stack per the implementation of UIDropDown.OpenPopup etc. (verified
        //       via reflection that there is no trace of a PushModal call), so it would not
        //       immediately conflict, but it also becomes true for other vanilla modal UI
        //       (save/load dialogs etc.) and thus means far more than "the Esc menu is open".
        //       Since this task requires behavior limited to "the Esc menu", we adopt the more
        //       narrowly targeted PauseMenu.component.isVisible.
        private const string PauseMenuName = "PauseMenu";

        // Task56: Post-crash investigation revealed that UIView.library.Get&lt;T&gt; (actually
        // ColossalFramework.UI.UIDynamicPanels.Get, confirmed by decompiling ColossalManaged.dll
        // with ilspycmd) is itself just a lookup into m_CachedPanels (Dictionary) and never
        // instantiates a prefab per call (all single-instance panels are created in bulk at startup
        // via UIView.Awake→m_PanelsLibrary.Init(this); Get merely returns the cached instance).
        // That said, performing library-based type resolution "every frame from multiple panels"
        // not only repeats wasteful GetComponent calls, but while the UIView itself is unregistered
        // (e.g. during loading) UIView.library returns null and the UIDynamicPanels.Get call throws
        // a NullReferenceException (already swallowed by try/catch, but during loading it becomes a
        // breeding ground for per-frame exceptions and log spam). Defensively resolve once, cache,
        // and reuse thereafter. If Unity destroys this instance, the UnityEngine.Object operator==
        // overload automatically makes it "null-equivalent" again, but just in case we also
        // explicitly null it via ResetCache() from MilitaryManager.Reset() (on level unload).
        private static PauseMenu _cachedPauseMenu;

        // Task56: UIView.GetAView() (static Dictionary&lt;string,UIView&gt;.Values.FirstOrDefault(),
        // also confirmed via ilspycmd; it does not instantiate either) was being called every frame
        // from multiple places, so we share a cache using the same reasoning (used by
        // BaseInfoPanelDrag.PositionNextToVanilla / UnitInfoPanel.UpdateTrackingPosition /
        // UnitBoxSelection.UpdateRectVisual).
        private static UIView _cachedView;

        /// <summary>Result of building the title row. Each panel keeps this in a field and uses it
        /// on Destroy to unsubscribe from CollapseButton.eventClick (the DragHandle has no events of
        /// its own, so no unsubscription is needed).</summary>
        public sealed class Handles
        {
            public UIDragHandle DragHandle;
            public UIButton CollapseButton;
        }

        /// <summary>
        /// Adds, directly under the panel, a UIDragHandle covering the whole title row
        /// (x=0..panelWidth, y=titleRowY..+TitleRowHeight) (target=panel, making the entire panel
        /// draggable), plus a minimize toggle button overlaid at its right edge. The button's click
        /// handler is supplied by the caller (expected to be a thin handler that just toggles each
        /// panel's _collapsed field and calls its ApplyCollapsedState equivalent).
        /// </summary>
        public static Handles AddTitleBarChrome(UIPanel panel, float panelWidth, float titleRowY, float pad, MouseEventHandler onCollapseClick)
        {
            Handles h = new Handles();

            UIDragHandle handle = panel.AddUIComponent<UIDragHandle>();
            handle.size = new Vector2(panelWidth, TitleRowHeight);
            handle.relativePosition = new Vector3(0f, titleRowY);
            handle.target = panel;
            h.DragHandle = handle;

            UIButton collapse = panel.AddUIComponent<UIButton>();
            collapse.size = new Vector2(CollapseButtonSize, CollapseButtonSize);
            collapse.relativePosition = new Vector3(panelWidth - pad - CollapseButtonSize, titleRowY);
            collapse.textScale = 0.8f;
            collapse.normalBgSprite = "ButtonMenu";
            collapse.hoveredBgSprite = "ButtonMenuHovered";
            collapse.pressedBgSprite = "ButtonMenuPressed";
            collapse.text = CollapseGlyphExpanded;
            collapse.eventClick += onCollapseClick;
            h.CollapseButton = collapse;

            return h;
        }

        /// <summary>Returns the button glyph corresponding to the given collapsed state (so the
        /// caller's ApplyCollapsedState equivalent only needs to assign it to _collapseButton.text).</summary>
        public static string CollapseGlyph(bool collapsed)
        {
            return collapsed ? CollapseGlyphCollapsed : CollapseGlyphExpanded;
        }

        /// <summary>Called from Destroy(). Unsubscribes the CollapseButton event subscription
        /// (the DragHandle is out of scope on the assumption the caller subscribed no events on it;
        /// it disappears along with the GameObject when the panel itself is destroyed).</summary>
        public static void Unsubscribe(Handles h, MouseEventHandler onCollapseClick)
        {
            if (h == null) return;
            if (h.CollapseButton != null) h.CollapseButton.eventClick -= onCollapseClick;
        }

        /// <summary>
        /// Task47: Whether the vanilla Esc (pause/options selection) menu is open. Called from the
        /// per-frame updates of BaseInfoPanel/UnitInfoPanel/AssetAssignPanel; while true, it is used
        /// to hide each panel visually only (without touching internal logic state such as the
        /// "currently selected base/unit"). UIView.library.Get&lt;T&gt; returns null (not an exception)
        /// when unregistered/not yet created, so the situation right after game start where
        /// PauseMenu does not exist yet is treated as "the menu is not open" (same
        /// "not-ready-is-the-normal-path" policy as BaseInfoPanel.TryGetVanillaPanel).
        /// </summary>
        public static bool IsGameMenuOpen()
        {
            try
            {
                // Task56: Instead of re-calling UIView.library.Get&lt;T&gt; every frame, reuse the cache
                // once resolved (see the field comment above. Get itself is a non-destructive
                // lookup, but UIView.library can be null during loading, so having a cache lets us
                // skip re-resolution entirely).
                if (_cachedPauseMenu == null)
                {
                    UIDynamicPanels lib = UIView.library;
                    if (lib != null) _cachedPauseMenu = lib.Get<PauseMenu>(PauseMenuName);
                }
                return _cachedPauseMenu != null && _cachedPauseMenu.component != null && _cachedPauseMenu.component.isVisible;
            }
            catch (Exception e)
            {
                ModConfig.LogError("PanelChrome.IsGameMenuOpen error: " + e);
                return false;
            }
        }

        /// <summary>Task56: Cached accessor for UIView.GetAView() (see the field comment above).
        /// The multiple per-frame callers (BaseInfoPanelDrag/UnitInfoPanel/UnitBoxSelection) use this.</summary>
        public static UIView GetCachedView()
        {
            if (_cachedView == null)
            {
                _cachedView = UIView.GetAView();
            }
            return _cachedView;
        }

        /// <summary>
        /// Task56: Whether the game is in a state where the UI library (vanilla UI and our own
        /// panels alike) may be touched. Returns false while a level is loading/unloading, and the
        /// callers (each panel's EnsureCreated/UpdateVisibility, and per-frame UI entry points such
        /// as UnitSelection/UnitBoxSelection/UnitCommandInput) skip this frame's processing
        /// entirely (MilitaryManager.OnMainVisualUpdate's unit visual sync — which only touches
        /// Unity GameObjects — is exempt; it does not touch the UI library and may continue).
        ///
        /// Signals used for the decision (confirmed by decompiling LoadingManager in
        /// Assembly-CSharp.dll with ilspycmd):
        ///   - public volatile bool LoadingManager.m_loadingComplete: set to true at the very end of
        ///     the level-load coroutine after all steps finish (right before dispatching
        ///     OnLevelLoaded to mod extensions) (LoadingManager.cs line 1813). Reset to false when
        ///     loading starts and when unloading starts (lines 391/401, 429/439, 467/477).
        ///   - public volatile bool LoadingManager.m_applicationQuitting: true once the application
        ///     shutdown sequence begins.
        ///   - Existence is checked first via Singleton&lt;LoadingManager&gt;.exists ( = only a null check
        ///     of the internal static field; unlike .instance it does not create a new object).
        ///     This is the same existing pattern LoadingManager itself uses inside AutoSaveTimer
        ///     (LoadingManager.cs line 52).
        /// All of these are just volatile bool reads with no allocation.
        /// </summary>
        public static bool IsGameReadyForUi()
        {
            try
            {
                return Singleton<LoadingManager>.exists
                    && Singleton<LoadingManager>.instance.m_loadingComplete
                    && !Singleton<LoadingManager>.instance.m_applicationQuitting;
            }
            catch (Exception e)
            {
                ModConfig.LogError("PanelChrome.IsGameReadyForUi error: " + e);
                return false;
            }
        }

        /// <summary>Task56: Called from MilitaryManager.Reset() (on level unload). Discards the
        /// cached PauseMenu/UIView references so the next session resolves them anew (an explicit
        /// clear so stale references are not carried over, regardless of whether Unity actually
        /// destroys these instances during teardown).</summary>
        public static void ResetCache()
        {
            _cachedPauseMenu = null;
            _cachedView = null;
        }
    }
}
