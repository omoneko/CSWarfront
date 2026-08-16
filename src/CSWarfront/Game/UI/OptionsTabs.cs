using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Task138 (Workshop request from lilMobStick: "you should split the mod settings into multiple
    /// pages, it's pretty cluttered right now"): turns the mod's Options screen into tabbed pages.
    ///
    /// Everything below one long scroll was equally buried — the two asset selectors most of all, which
    /// is the part players actually spend time in. There are eight groups; with tabs, each page holds one
    /// or two and the asset pages open with their search field already on screen.
    ///
    /// How it works, and why this way: the game builds every group itself through UIHelper.AddGroup, and
    /// its layout, width and styling are worth keeping exactly. So this class does not build containers of
    /// its own and does no layout maths. It watches which components the game appended to the options
    /// panel between one <see cref="Page"/> call and the next, remembers those ranges, and then shows the
    /// selected range and hides the rest. The panel's own auto-layout closes the gaps.
    ///
    /// OnSettingsUI runs once per session (the Options screen keeps the panel it built), so pages are
    /// built up front and only their visibility changes afterwards — there is nothing to rebuild on a
    /// tab click.
    ///
    /// Failure policy: if anything cannot be resolved, every group is left visible — that is exactly the
    /// pre-Task138 screen, so a broken tab strip can never cost the player access to a setting.
    /// </summary>
    internal static class OptionsTabs
    {
        // --- design constants -----------------------------------------------------------------------

        /// <summary>Height of a tab. Matches the game's own small menu buttons so the strip reads as part
        /// of the options screen rather than something bolted on.</summary>
        private const float TabHeight = 30f;

        /// <summary>Gap between tabs, and between the strip and the first group.</summary>
        private const float TabGap = 4f;

        /// <summary>Side padding inside a tab, added to the measured width of its caption. Tabs are sized
        /// to their text rather than to a fixed width: the captions differ a lot in length, and equal-width
        /// tabs would either truncate the long ones or strand the short ones in whitespace.</summary>
        private const float TabPaddingX = 14f;

        private const float TabTextScale = 0.85f;

        /// <summary>Rough width per character at TabTextScale, used to size a tab before the label has
        /// been measured (UIButton does not report a text width until it has rendered). Deliberately
        /// generous — a slightly wide tab looks intentional, a clipped caption looks broken.</summary>
        private const float ApproxCharWidth = 8.5f;

        private const float MinTabWidth = 64f;

        // --- state ----------------------------------------------------------------------------------

        private class PageEntry
        {
            public string Title;
            public int FirstComponent;   // index into the options panel's component list
            public int ComponentCount;
            public UIButton Tab;
        }

        private static UIComponent _root;
        private static UIPanel _strip;
        private static readonly List<PageEntry> _pages = new List<PageEntry>();
        private static PageEntry _current;
        private static int _selected;
        private static bool _failed;

        /// <summary>Starts a tabbed layout inside the options panel this helper writes to. Everything the
        /// caller adds afterwards belongs to whichever <see cref="Page"/> is open at the time.</summary>
        public static void Begin(UIHelperBase helper)
        {
            _root = null;
            _strip = null;
            _pages.Clear();
            _current = null;
            _selected = 0;
            _failed = false;

            try
            {
                UIHelper concrete = helper as UIHelper;
                _root = concrete != null ? concrete.self as UIComponent : null;
                if (_root == null)
                {
                    _failed = true;
                    ModConfig.LogError("OptionsTabs.Begin: options panel unavailable; falling back to one long page");
                    return;
                }

                _strip = _root.AddUIComponent<UIPanel>();
                _strip.name = "WarfrontOptionsTabs";
                _strip.autoLayout = true;
                _strip.autoLayoutDirection = LayoutDirection.Horizontal;
                _strip.autoLayoutPadding = new RectOffset(0, (int)TabGap, 0, (int)TabGap);
                _strip.wrapLayout = true;              // narrow windows wrap onto a second row rather than clipping
                _strip.autoFitChildrenVertically = true;
                _strip.width = _root.width;
            }
            catch (Exception e)
            {
                _failed = true;
                ModConfig.LogError("OptionsTabs.Begin error (falling back to one long page): " + e);
            }
        }

        /// <summary>Opens a new page. Groups added after this call appear on it.</summary>
        public static void Page(string title)
        {
            if (_failed || _root == null) return;
            try
            {
                CloseCurrent();
                _current = new PageEntry { Title = title, FirstComponent = _root.components.Count };
            }
            catch (Exception e)
            {
                _failed = true;
                ModConfig.LogError("OptionsTabs.Page error: " + e);
            }
        }

        /// <summary>Finishes the layout: builds the tabs and opens the first page.</summary>
        public static void End()
        {
            if (_failed || _root == null) { ShowEverything(); return; }
            try
            {
                CloseCurrent();
                if (_pages.Count <= 1) { ShowEverything(); return; } // one page is not worth a tab strip

                for (int i = 0; i < _pages.Count; i++) _pages[i].Tab = BuildTab(_pages[i], i);
                Select(0);
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsTabs.End error (falling back to one long page): " + e);
                ShowEverything();
            }
        }

        private static void CloseCurrent()
        {
            if (_current == null) return;
            _current.ComponentCount = _root.components.Count - _current.FirstComponent;
            if (_current.ComponentCount > 0) _pages.Add(_current); // a page that added nothing gets no tab
            _current = null;
        }

        private static UIButton BuildTab(PageEntry page, int index)
        {
            UIButton tab = _strip.AddUIComponent<UIButton>();
            tab.name = "WarfrontOptionsTab" + index;
            tab.text = page.Title;
            tab.textScale = TabTextScale;
            tab.textHorizontalAlignment = UIHorizontalAlignment.Center;
            tab.textVerticalAlignment = UIVerticalAlignment.Middle;

            float width = page.Title.Length * ApproxCharWidth + TabPaddingX * 2f;
            tab.size = new Vector2(width < MinTabWidth ? MinTabWidth : width, TabHeight);

            ApplyTabStyle(tab, false);
            tab.eventClick += (c, e) => Select(index);
            return tab;
        }

        /// <summary>The selected tab keeps the pressed sprite and full-strength text; the others sit back
        /// in the normal sprite with dimmed text. Colour alone would be too quiet a signal on this
        /// background, and a sprite change alone reads as a stuck button — so both move together.</summary>
        private static void ApplyTabStyle(UIButton tab, bool selected)
        {
            if (tab == null) return;
            tab.normalBgSprite = selected ? "ButtonMenuFocused" : "ButtonMenu";
            tab.hoveredBgSprite = "ButtonMenuHovered";
            tab.pressedBgSprite = "ButtonMenuPressed";
            tab.textColor = selected ? new Color32(255, 255, 255, 255) : new Color32(185, 195, 205, 255);
            tab.hoveredTextColor = new Color32(255, 255, 255, 255);
        }

        private static void Select(int index)
        {
            try
            {
                if (index < 0 || index >= _pages.Count) return;
                _selected = index;
                for (int i = 0; i < _pages.Count; i++)
                {
                    SetPageVisible(_pages[i], i == index);
                    ApplyTabStyle(_pages[i].Tab, i == index);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsTabs.Select error (showing everything instead): " + e);
                ShowEverything();
            }
        }

        private static void SetPageVisible(PageEntry page, bool visible)
        {
            for (int i = 0; i < page.ComponentCount; i++)
            {
                int index = page.FirstComponent + i;
                if (index < 0 || index >= _root.components.Count) continue;
                UIComponent c = _root.components[index];
                if (c == null || c == _strip) continue;
                c.isVisible = visible;
            }
        }

        /// <summary>The fallback, and the state the screen had before Task138: every group visible at
        /// once. Also hides the tab strip, so a half-built strip is never left on screen.</summary>
        private static void ShowEverything()
        {
            try
            {
                if (_strip != null) _strip.isVisible = false;
                if (_root == null) return;
                for (int i = 0; i < _root.components.Count; i++)
                {
                    UIComponent c = _root.components[i];
                    if (c != null && c != _strip) c.isVisible = true;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OptionsTabs.ShowEverything error: " + e);
            }
        }

        /// <summary>Which page is open (for tests/diagnostics).</summary>
        public static int SelectedPage { get { return _selected; } }
    }
}
