using System;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task62 (Mount&amp;Blade-style order feedback 2/2): a simple toast shown briefly at the top
    /// center of the screen every time a unit command is issued or changes. A one-way API where
    /// each command-issuing site in UnitCommandInput (free advance / halt / arming rally-wait /
    /// rally-point confirmation / rally cancel) merely calls Show(message). Giving the content its
    /// meaning (e.g. whether to include a target count such as "x12") is the caller's
    /// responsibility.
    ///
    /// Creation/display updates follow the same scheme as UnitBoxSelection's rectangle panel
    /// (idempotent EnsureCreated + per-frame Update): a single UILabel is created directly under
    /// the UIView and reused. Every Show() call resets the string and the dismissal timer (based on
    /// Time.realtimeSinceStartup, which advances even while paused). Update() is called every
    /// frame; once the remaining time drops below FadeDurationSeconds it fades the opacity
    /// linearly, and hides the label when it reaches 0.
    ///
    /// Main thread only (because it calls Unity/ColossalFramework UI APIs). Call
    /// EnsureCreated()→Update() in that order every frame from WarfrontThreadingExtension.OnUpdate,
    /// like the other UI updates.
    /// </summary>
    public static class CommandToast
    {
        private const string LabelName = "CSWarfrontCommandToast";

        private const float DisplaySeconds = 2.5f;
        private const float FadeDurationSeconds = 0.5f; // linearly fade the opacity for this many seconds just before disappearing
        private const float TopOffset = 70f; // distance from the top edge of the screen (enough not to overlap the vanilla top toolbar)
        private const float LabelTextScale = 1.3f;

        private static UILabel _label;
        private static float _hideAtRealtime;
        private static bool _visible;

        /// <summary>Idempotent. Creates the label exactly once as soon as the UIView is ready (same scheme as the other panels).</summary>
        public static void EnsureCreated()
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi()) return; // Task56: do not touch the UI library while loading/unloading
                if (_label != null) return;
                UIView view = PanelChrome.GetCachedView();
                if (view == null) return;
                if (view.FindUIComponent<UILabel>(LabelName) != null) return;

                UILabel label = view.AddUIComponent(typeof(UILabel)) as UILabel;
                if (label == null)
                {
                    ModConfig.LogError("CommandToast.EnsureCreated: failed to create UILabel");
                    return;
                }
                label.name = LabelName;
                label.textScale = LabelTextScale;
                label.textColor = new Color32(255, 235, 180, 255);
                label.textAlignment = UIHorizontalAlignment.Center;
                label.autoSize = true;
                label.isInteractive = false; // do not intercept clicks/drags
                label.isVisible = false;
                label.opacity = 1f;
                _label = label;
            }
            catch (Exception e)
            {
                ModConfig.LogError("CommandToast.EnsureCreated error: " + e);
            }
        }

        /// <summary>Displays a command event. Overwrites even if already showing and resets the
        /// dismissal timer (when commands are issued in quick succession, the newest message always
        /// stays displayed).</summary>
        public static void Show(string message)
        {
            try
            {
                if (_label == null) return; // not created yet (loading etc.). Silently drop this event.
                _label.text = message ?? "";
                _label.opacity = 1f;
                CenterLabel();
                _label.Show();
                _label.BringToFront();
                _visible = true;
                _hideAtRealtime = Time.realtimeSinceStartup + DisplaySeconds;
            }
            catch (Exception e)
            {
                ModConfig.LogError("CommandToast.Show error: " + e);
            }
        }

        /// <summary>Called every main-thread frame. Only handles the time-based fade/hide bookkeeping.</summary>
        public static void Update()
        {
            try
            {
                if (_label == null || !_visible) return;

                if (!PanelChrome.IsGameReadyForUi() || PanelChrome.IsGameMenuOpen())
                {
                    // Task62: temporarily hide while loading or while the Esc menu is shown (no
                    // toggle state is kept — it just visually disappears. It is NOT automatically
                    // shown again if grace time still remains after closing the menu; for
                    // simplicity the display simply ends here).
                    HideNow();
                    return;
                }

                float remaining = _hideAtRealtime - Time.realtimeSinceStartup;
                if (remaining <= 0f)
                {
                    HideNow();
                    return;
                }

                _label.opacity = remaining < FadeDurationSeconds ? Mathf.Clamp01(remaining / FadeDurationSeconds) : 1f;
            }
            catch (Exception e)
            {
                ModConfig.LogError("CommandToast.Update error: " + e);
            }
        }

        /// <summary>Called on level unload (via MilitaryManager.Reset). Destroys the label so no static state lingers.</summary>
        public static void Destroy()
        {
            try
            {
                if (_label != null) UnityEngine.Object.Destroy(_label.gameObject);
            }
            catch (Exception e)
            {
                ModConfig.LogError("CommandToast.Destroy error: " + e);
            }
            finally
            {
                _label = null;
                _visible = false;
            }
        }

        private static void HideNow()
        {
            if (_label != null && _label.isVisible) _label.Hide();
            _visible = false;
        }

        private static void CenterLabel()
        {
            UIView view = PanelChrome.GetCachedView();
            if (view == null || _label == null) return;
            Vector2 res = view.GetScreenResolution();
            float x = (res.x - _label.width) * 0.5f;
            _label.relativePosition = new Vector3(x, TopOffset);
        }
    }
}
