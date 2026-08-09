using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class splitting out of BaseInfoPanel only the auto-follow positioning next to the
    /// vanilla panel and the "title-row drag start -&gt; detach from auto-follow" behavior added in
    /// Task40 (because of the 500-line limit on BaseInfoPanel.cs; same policy as
    /// BaseInfoPanelProduction/BaseInfoPanelModelButton). All methods are main-thread only (they call
    /// Unity UI APIs).
    /// </summary>
    internal static partial class BaseInfoPanel
    {
        /// <summary>Task40: the first mouse-down on the title row (drag handle) enters "detached" mode.
        /// From then on, auto-follow via PositionNextToVanilla is stopped so the UIDragHandle itself can
        /// move the panel freely (avoiding a conflict between the per-frame position overwrite and the
        /// drag operation). Reset to false by Hide() (the "close" equivalent, e.g. the vanilla panel
        /// closing).</summary>
        private static void OnTitleBarMouseDown(UIComponent component, UIMouseEventParameter eventParam)
        {
            _detachedFromVanilla = true;
        }

        /// <summary>Makes our panel follow to the right of the vanilla panel's absolute position (or the left if off-screen). Also clamps to the bottom screen edge.</summary>
        private static void PositionNextToVanilla(WorldInfoPanel vanilla)
        {
            UIComponent vc = vanilla.component;
            UIView view = PanelChrome.GetCachedView(); // Task56: called every frame, so use the cached accessor
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
