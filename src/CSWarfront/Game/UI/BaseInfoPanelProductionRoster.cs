using System;
using System.Collections.Generic;
using System.Text;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class splitting out of BaseInfoPanelProduction only the construction/content updating of
    /// the unit selection dropdown and the assembly of the queue display string (split off because of
    /// the 500-line limit on BaseInfoPanelProduction.cs; same policy as the Task34
    /// BaseInfoPanelProduction.cs split and the Task63 BaseInfoPanelMissile.cs split).
    /// All methods are main-thread only (they call Unity UI APIs).
    /// </summary>
    internal static partial class BaseInfoPanel
    {
        private static UIDropDown BuildUnitDropdown(float x, float y, float width)
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
            dd.textScale = 0.75f;
            dd.textFieldPadding = new RectOffset(8, 8, 6, 0);
            dd.itemPadding = new RectOffset(8, 0, 3, 0);
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            dd.items = _unitDropdownItems;
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

            return dd;
        }

        /// <summary>Builds the paired arrays of dropdown display texts and TypeKeys for ordering, from
        /// the roster of domains the selected base can produce (Task61: spawnableDomains, a copy of
        /// MilitaryBase.SpawnableDomains) (army bases use LandUnitRoster, naval bases NavalUnitRoster,
        /// air bases AirUnitRoster; if none matches, e.g. an unaffiliated base, fall back to
        /// LandUnitRoster). Entries whose tier exceeds unlockedTier get " [Locked]" appended (Task35:
        /// they remain selectable and the produce button reports TierLocked; they are not removed from
        /// the choices so it is obvious at a glance what research will unlock). If both unlockedTier and
        /// spawnableDomains are the same as last time, do nothing (the list is not rebuilt even though
        /// this is called every frame).</summary>
        private static void EnsureUnitDropdownItemsBuilt(byte unlockedTier, DomainMask spawnableDomains,
            BaseType baseType = BaseType.Army)
        {
            if (_unitDropdownItems != null && _lastUnitDropdownUnlockedTier == unlockedTier &&
                _lastUnitDropdownDomains == spawnableDomains && _lastUnitDropdownBaseType == baseType) return;

            // Task61: on a roster switch (e.g. army -> naval base) there is no point in preserving the
            // selection index (the unit-branch list is completely different), so reset to the head. If
            // only the tier changed, keep the selection position as before. Capture the pre-overwrite
            // value here.
            bool rosterChanged = _lastUnitDropdownDomains != spawnableDomains;

            IEnumerable<UnitType> roster;
            if (DomainMaskUtil.Contains(spawnableDomains, Domain.Sea)) roster = NavalUnitRoster.All();
            else if (DomainMaskUtil.Contains(spawnableDomains, Domain.Air)) roster = AirUnitRoster.All();
            else roster = LandUnitRoster.All();

            var items = new List<string>();
            var keys = new List<string>();
            foreach (UnitType t in roster)
            {
                // Task103: per-branch production eligibility (military trains only at cargo stations,
                // and cargo stations only produce trains. Do not list trains in a military strongpoint's
                // menu).
                if (!FortificationRules.CanProduceUnit(baseType, t.Category)) continue;

                string label = t.TypeKey + "  (¥" + t.Cost.ToString("0") + ")";
                if (t.Tier > unlockedTier) label += " [Locked]";
                items.Add(label);
                keys.Add(t.TypeKey);
            }
            _unitDropdownItems = items.ToArray();
            _unitDropdownTypeKeys = keys.ToArray();
            _lastUnitDropdownUnlockedTier = unlockedTier;
            _lastUnitDropdownDomains = spawnableDomains;
            _lastUnitDropdownBaseType = baseType;

            if (_unitDropdown != null)
            {
                int prevSelected = _unitDropdown.selectedIndex;
                _unitDropdown.items = _unitDropdownItems;
                if (!rosterChanged && prevSelected >= 0 && prevSelected < _unitDropdownItems.Length)
                    _unitDropdown.selectedIndex = prevSelected;
                else if (_unitDropdownItems.Length > 0)
                    _unitDropdown.selectedIndex = 0;
            }
        }

        /// <summary>Assembles one line in the "Queue: Tank_T3(producing) -> Infantry_T2 -> …" format.
        /// Index 0 is always in production (the head) and gets "(producing)" appended. Entries beyond
        /// QueueDisplayMax are elided with "…".</summary>
        private static string BuildQueueDisplayText(string[] queuedTypeKeys)
        {
            if (queuedTypeKeys == null || queuedTypeKeys.Length == 0) return "Queue: none";

            StringBuilder sb = _queueBuilder;
            sb.Length = 0;
            sb.Append("Queue: ");

            int shown = Math.Min(queuedTypeKeys.Length, QueueDisplayMax);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(" -> ");
                sb.Append(queuedTypeKeys[i]);
                if (i == 0) sb.Append("(producing)");
            }
            if (queuedTypeKeys.Length > shown) sb.Append(" → …");

            return sb.ToString();
        }
    }
}
