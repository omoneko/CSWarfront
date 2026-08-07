namespace CSWarfront.Game
{
    /// <summary>Strings for the "Model Settings" panel (Game/UI/AssetAssignPanel*.cs). See WarfrontStrings.cs for the localization scheme.</summary>
    public static partial class WarfrontStrings
    {
        public static string AssetPanel_Title = "Model Settings";

        public static string AssetPanel_SectionFaction = "Faction";
        public static string AssetPanel_SectionUnitType = "Unit Type";
        public static string AssetPanel_SectionSearch = "Search (partial match) / Asset Type";
        public static string AssetPanel_SectionCopy = "Apply to Multiple";
        public static string AssetPanel_SectionPreset = "Preset";

        public static string AssetPanel_ApplyButton = "Apply";
        public static string AssetPanel_ResetButton = "Reset to Default";
        public static string AssetPanel_CloseButton = "Close";
        public static string AssetPanel_CopyApplyButton = "Apply to Multiple";
        public static string AssetPanel_ClearAllButton = "Reset All";
        public static string AssetPanel_PresetSaveButton = "Save";
        public static string AssetPanel_PresetLoadButton = "Load";

        public static string AssetPanel_TypeKeyLabelFormat = "{0} → {1}";
        public static string AssetPanel_DefaultBinding = "(default)";
        public static string AssetPanel_CurrentBindingFormat = "Current binding: {0}";
        public static string AssetPanel_DefaultModel = "(default model)";
        public static string AssetPanel_NoOwnedBaseWarningFormat = "(This faction currently owns no {0}. It will take effect once a base is built or changes ownership)";

        public static string AssetPanel_SubscribedOnlyFormat = "Subscribed only: {0}";
        public static string AssetPanel_ToggleOn = "ON";
        public static string AssetPanel_ToggleOff = "OFF";
        public static string AssetPanel_ListTruncatedFormat = "* Showing {0} of {1} (narrow your search)";
        public static string AssetPanel_ListCountFormat = "{0} item(s)";

        public static string AssetPanel_PresetSlotEmptyFormat = "{0} (empty)";
        public static string AssetPanel_ClearAllDoneFormat = "Reset all bindings ({0} entries)";
        public static string AssetPanel_ClearAllNothing = "Nothing to reset (already at default)";
        public static string AssetPanel_PresetSavedFormat = "Saved to preset {0}";
        public static string AssetPanel_PresetSaveFailedFormat = "Failed to save to preset {0}";
        public static string AssetPanel_PresetLoadedFormat = "Loaded preset {0} (replaced the existing bindings)";
        public static string AssetPanel_PresetEmptyFormat = "Preset {0} is empty";
    }
}
