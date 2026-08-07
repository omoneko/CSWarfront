using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>
    /// Partial class of AssetAssignPanel that isolates only the construction of the unit-type dropdown
    /// and the Apply / Reset-to-Default / Close buttons plus their event handlers (split off because of
    /// the 500-line limit on the AssetAssignPanel.cs side; same policy as
    /// BaseInfoPanel/BaseInfoPanelProduction). The search field, asset-kind dropdown, subscribed-only
    /// toggle, and list were moved to AssetAssignPanelAssetList.cs in Task41.
    /// All fields are declared on the AssetAssignPanel.cs side (fine because a partial class shares
    /// private members across all parts). All methods are main-thread only (they call Unity UI APIs).
    /// </summary>
    internal static partial class AssetAssignPanel
    {
        /// <summary>Task66: Builds the TypeKey list by first prepending the 4 per-base-type keys
        /// (army/navy/air/missile; UnitAssetBindings.ArmyBaseTypeKey etc.) and then adding the 35
        /// TypeKeys from LandUnitRoster.All() (category declaration order -> Tier1-5), for a total of 39.
        /// Bases are not unit types (they are not included in LandUnitRoster.All()), but the requirement
        /// is that they be selectable on equal footing as targets of the model-assignment UI, so they are
        /// manually inserted at the top (at Task60 there was a single key, but it was split into 4 keys to
        /// allow per-base-type assignment).</summary>
        private static void BuildTypeKeys()
        {
            List<string> keys = new List<string>
            {
                UnitAssetBindings.ArmyBaseTypeKey,
                UnitAssetBindings.NavyBaseTypeKey,
                UnitAssetBindings.AirBaseTypeKey,
                UnitAssetBindings.MissileBaseTypeKey
            };
            foreach (UnitType t in LandUnitRoster.All()) keys.Add(t.TypeKey);
            _typeKeys = keys.ToArray();
        }

        private static UIDropDown BuildTypeKeyDropdown(float x, float y, float width)
        {
            UIDropDown dd = _panel.AddUIComponent<UIDropDown>();
            dd.size = new Vector2(width, DropdownHeight);
            dd.relativePosition = new Vector3(x, y);
            dd.normalBgSprite = "ButtonMenu";
            dd.hoveredBgSprite = "ButtonMenuHovered";
            dd.disabledBgSprite = "ButtonMenuDisabled";
            dd.listBackground = "GenericPanelLight";
            dd.itemHeight = 26; // text at 1x; line spacing slightly wider
            dd.itemHover = "ListItemHover";
            dd.itemHighlight = "ListItemHighlight";
            dd.listWidth = (int)width;
            dd.listHeight = 300; // old 200 x1.5
            dd.listPosition = UIDropDown.PopupListPosition.Below;
            dd.textScale = 0.75f; // unified with the other panels
            dd.textFieldPadding = new RectOffset(12, 12, 9, 0); // old (8,8,6,0) x1.5
            dd.itemPadding = new RectOffset(12, 0, 5, 0); // old (8,0,3,0) x1.5
            dd.popupColor = new Color32(45, 52, 61, 255);
            dd.popupTextColor = new Color32(230, 230, 230, 255);
            dd.foregroundSpriteMode = UIForegroundSpriteMode.Stretch;
            dd.verticalAlignment = UIVerticalAlignment.Middle;
            dd.horizontalAlignment = UIHorizontalAlignment.Left;

            dd.items = BuildDropdownLabels();
            dd.selectedIndex = 0;

            UIButton trigger = dd.AddUIComponent<UIButton>();
            trigger.text = "▼";
            trigger.textScale = 0.7f; // unified with the other panels
            trigger.size = new Vector2(36f, DropdownHeight); // old 24f x1.5
            trigger.relativePosition = new Vector3(width - 48f, 0f);
            trigger.normalBgSprite = "ButtonMenu";
            trigger.hoveredBgSprite = "ButtonMenuHovered";
            trigger.pressedBgSprite = "ButtonMenuPressed";
            dd.triggerButton = trigger;

            dd.eventSelectedIndexChanged += OnTypeKeyChanged;
            return dd;
        }

        /// <summary>Rebuilds the 35 labels of the form "TypeKey → current binding (or (default) if none)"
        /// and restores the selection to selectedIndex (used for refreshing the list right after
        /// Apply/Reset, and for the redisplay on every Show()).</summary>
        private static void RefreshDropdownLabels(int selectedIndex)
        {
            if (_typeKeyDropdown == null || _typeKeys == null) return;

            _suppressEvents = true;
            try
            {
                _typeKeyDropdown.items = BuildDropdownLabels();
                if (selectedIndex >= 0 && selectedIndex < _typeKeys.Length)
                    _typeKeyDropdown.selectedIndex = selectedIndex;
                else if (_typeKeys.Length > 0)
                    _typeKeyDropdown.selectedIndex = 0;
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        /// <summary>Task40: The labels show the bindings of the currently selected faction
        /// (SelectedFactionId) (they are rebuilt via OnFactionChanged every time the faction dropdown
        /// changes).
        /// Task41: When a kind other than prop is bound, a kind tag is prefixed, e.g.
        /// "[Building]MyBuilding" (AssetKindUtil.Describe).
        /// Task66: For the base-type keys (army/navy/air/missile), the Japanese label from
        /// UnitAssetBindings.DisplayNameForBaseKey is shown instead of the raw key string, so they are
        /// not mistaken for one of the units. Binding resolution also proceeds in the order per-type key
        /// -> old unified key (UnitAssetBindings.TryGetEffective), and when the fallback from the old
        /// unified key is in effect, that too is shown as the "current binding".</summary>
        private static string[] BuildDropdownLabels()
        {
            if (_typeKeys == null) return new string[0];

            byte factionId = SelectedFactionId;
            string[] labels = new string[_typeKeys.Length];
            for (int i = 0; i < _typeKeys.Length; i++)
            {
                AssetKind kind;
                string name;
                string suffix = UnitAssetBindings.TryGetEffective(factionId, _typeKeys[i], out kind, out name)
                    ? AssetKindUtil.Describe(kind, name)
                    : WarfrontStrings.AssetPanel_DefaultBinding;
                string displayKey = UnitAssetBindings.DisplayNameForBaseKey(_typeKeys[i]);
                labels[i] = string.Format(WarfrontStrings.AssetPanel_TypeKeyLabelFormat, displayKey, suffix);
            }
            return labels;
        }

        private static void OnTypeKeyChanged(UIComponent component, int value)
        {
            try
            {
                if (_suppressEvents) return;
                RefreshCurrentBindingLabel();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnTypeKeyChanged error: " + e);
            }
        }

        /// <summary>Task40: Shows the binding of the currently selected (faction, TypeKey) and updates
        /// the thumbnail to match (independent of the list selection: this is a preview of the asset
        /// "currently applied").
        /// Task41: Also resolves the kind (AssetKind) of the binding and uses it for the thumbnail and
        /// label display.</summary>
        private static void RefreshCurrentBindingLabel()
        {
            if (_currentBindingLabel == null || _typeKeyDropdown == null || _typeKeys == null) return;

            int idx = _typeKeyDropdown.selectedIndex;
            if (idx < 0 || idx >= _typeKeys.Length)
            {
                _currentBindingLabel.text = "";
                RefreshThumbnail(AssetKind.Prop, null);
                return;
            }

            AssetKind kind;
            string name;
            bool bound = UnitAssetBindings.TryGetEffective(SelectedFactionId, _typeKeys[idx], out kind, out name);
            _currentBindingLabel.text = string.Format(WarfrontStrings.AssetPanel_CurrentBindingFormat, bound ? AssetKindUtil.Describe(kind, name) : WarfrontStrings.AssetPanel_DefaultModel);
            RefreshThumbnail(bound ? kind : AssetKind.Prop, bound ? name : null);
        }

        private static void OnApplyClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_typeKeyDropdown == null || _typeKeys == null || _propListBox == null) return;

                int typeIdx = _typeKeyDropdown.selectedIndex;
                if (typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

                int assetIdx = _propListBox.selectedIndex;
                if (assetIdx < 0 || assetIdx >= _filteredAssetNames.Count)
                {
                    ModConfig.Log("AssetAssignPanel: apply skipped (no asset selected)");
                    return;
                }

                string typeKey = _typeKeys[typeIdx];
                string assetName = _filteredAssetNames[assetIdx];
                AssetKind kind = SelectedAssetKind;

                UnitAssetBindings.Set(SelectedFactionId, typeKey, kind, assetName);
                ApplyBindingChange(typeIdx);
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnApplyClick error: " + e);
            }
        }

        private static void OnResetClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                if (_typeKeyDropdown == null || _typeKeys == null) return;

                int typeIdx = _typeKeyDropdown.selectedIndex;
                if (typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

                UnitAssetBindings.Clear(SelectedFactionId, _typeKeys[typeIdx]);
                ApplyBindingChange(typeIdx);
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnResetClick error: " + e);
            }
        }

        /// <summary>Processing to reflect a binding change (shared by Apply/Reset). After updating the
        /// dropdown labels and the current-binding display, destroys all existing visuals (Task36
        /// requirement: reflect immediately; on the next Sync, CreateVisual re-resolves the new binding
        /// (kind included, Task41) via UnitMeshSource).</summary>
        private static void ApplyBindingChange(int typeIdx)
        {
            RefreshDropdownLabels(typeIdx);
            RefreshCurrentBindingLabel();

            UnitVisuals.DestroyAll();
            BaseVisuals.DestroyAll(); // Task60: also destroy the per-faction base overlays so the next Sync re-resolves them
            ModConfig.Log("AssetAssignPanel: ran UnitVisuals.DestroyAll()/BaseVisuals.DestroyAll() to apply the binding change");

            WarnIfNoOwnedBaseForKey(typeIdx);
        }

        /// <summary>Response to the Task66 bug investigation: when the target is a base-type key and the
        /// selected faction owns no base of that type, the binding itself is saved correctly but nothing
        /// changes visually (there is no base to apply it to), so to keep the user from misreading
        /// "nothing changed = a bug", an explicit notice is written to the log (the same wording is also
        /// appended to the current-binding label).</summary>
        private static void WarnIfNoOwnedBaseForKey(int typeIdx)
        {
            if (_typeKeys == null || typeIdx < 0 || typeIdx >= _typeKeys.Length) return;

            BaseType baseType;
            if (!UnitAssetBindings.TryGetBaseTypeForKey(_typeKeys[typeIdx], out baseType)) return;
            if (MilitaryManager.HasOwnedBaseOfType(SelectedFactionId, baseType)) return;

            string note = string.Format(WarfrontStrings.AssetPanel_NoOwnedBaseWarningFormat, UnitAssetBindings.DisplayNameForBaseKey(_typeKeys[typeIdx]));
            ModConfig.Log("AssetAssignPanel: " + note);
            if (_currentBindingLabel != null) _currentBindingLabel.text += "\n" + note;
        }

        private static void OnCloseClick(UIComponent component, UIMouseEventParameter eventParam)
        {
            try
            {
                Hide();
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetAssignPanel.OnCloseClick error: " + e);
            }
        }
    }
}
