namespace CSWarfront.Game
{
    /// <summary>Strings for the Options subpages: Model Assignment (OptionsModelAssignPage*),
    /// Faction Relations (OptionsRelationsPage) and Base Buildings (OptionsBaseBuildingPage).
    /// See WarfrontStrings.cs for the localization scheme.</summary>
    public static partial class WarfrontStrings
    {
        // --- Model Assignment (OptionsModelAssignPage.cs) ---------------------------------------
        public static string OptionsModel_GroupTitle = "Model Assignment";
        public static string OptionsModel_NoSelection = "(none selected)";
        public static string OptionsModel_FactionDropdown = "Faction";
        public static string OptionsModel_UnitTypeDropdown = "Unit Type";
        public static string OptionsModel_AssetKindDropdown = "Asset Type";
        public static string OptionsModel_SubscribedOnlyCheckbox = "Subscribed only";
        public static string OptionsModel_SearchField = "Search (partial match)";
        public static string OptionsModel_AssetDropdown = "Asset";
        public static string OptionsModel_CopyScopeDropdown = "Apply to Multiple - Scope";
        public static string OptionsModel_ApplyButton = "Apply";
        public static string OptionsModel_ResetButton = "Reset to Default";
        public static string OptionsModel_CopyApplyButton = "Apply to Multiple";
        public static string OptionsModel_DefaultSuffix = "(default)";
        public static string OptionsModel_TypeKeyLabelFormat = "{0} -> {1}";
        public static string OptionsModel_NoAssetsHint = "No assets are currently available (props/buildings/vehicles/trees), e.g. opened from the main menu. Open this again after loading a city to see subscribed assets in the list.";
        public static string OptionsModel_NoOwnedBaseNoteFormat = "(This faction currently owns no {0}. It will take effect once a base is built or changes ownership)";

        // --- Model Assignment presets (OptionsModelAssignPagePresets.cs) ------------------------
        public static string OptionsModel_ClearAllButton = "Reset All";
        public static string OptionsModel_PresetDropdown = "Preset";
        public static string OptionsModel_PresetSaveButton = "Save";
        public static string OptionsModel_PresetLoadButton = "Load";
        public static string OptionsModel_PresetSlotEmptyFormat = "{0} (empty)";
        public static string OptionsModel_ClearAllDoneFormat = "Reset all bindings ({0} entries)";
        public static string OptionsModel_ClearAllNothing = "Nothing to reset (already at default)";
        public static string OptionsModel_PresetSavedFormat = "Saved to preset {0}";
        public static string OptionsModel_PresetSaveFailedFormat = "Failed to save to preset {0}";
        public static string OptionsModel_PresetLoadedFormat = "Loaded preset {0} (replaced the existing bindings)";
        public static string OptionsModel_PresetEmptyFormat = "Preset {0} is empty";

        // --- Model Assignment binding display (OptionsModelAssignPageBinding.cs) ----------------
        public static string OptionsModel_ListTruncatedFormat = "* Showing {0} of {1} (narrow your search)";
        public static string OptionsModel_ListCountFormat = "{0} item(s)";
        public static string OptionsModel_CurrentBindingFormat = "Current binding: {0}";
        public static string OptionsModel_DefaultModel = "(default model)";

        // --- Faction Relations (OptionsRelationsPage.cs) ----------------------------------------
        public static string OptionsRelations_GroupTitle = "Faction Relations";
        public static string OptionsRelations_Hostile = "Hostile";
        public static string OptionsRelations_Neutral = "Neutral";
        public static string OptionsRelations_Allied = "Allied";
        public static string OptionsRelations_Nemesis = "Nemesis";
        public static string OptionsRelations_PairLabelFormat = "{0} ↔ {1}";
        public static string OptionsRelations_KaijuName = "KAIJU";
        public static string OptionsRelations_AlienName = "Alien";
        public static string OptionsRelations_ResetAllButton = "Reset All to Hostile";
        public static string OptionsRelations_NoCityNote = "No city is loaded, so faction relations cannot be edited. Please open this again after loading a city.";

        // --- Base Buildings (OptionsBaseBuildingPage.cs) ----------------------------------------
        public static string OptionsBaseBuilding_GroupTitle = "Base Buildings";
        public static string OptionsBaseBuilding_NoSelection = "(none selected)";
        public static string OptionsBaseBuilding_BunkerRow = "Bunker";
        public static string OptionsBaseBuilding_ArtilleryPostRow = "Artillery Position";
        public static string OptionsBaseBuilding_SupplyDepotRow = "Supply Depot";
        public static string OptionsBaseBuilding_TrenchRow = "Trench";
        public static string OptionsBaseBuilding_CargoStationRow = "Cargo Station";
        public static string OptionsBaseBuilding_AtPillboxRow = "AT Pillbox";     // Task117
        public static string OptionsBaseBuilding_AaPositionRow = "AA Position";   // Task117
        public static string OptionsBaseBuilding_SubscribedOnlyCheckbox = "Subscribed only";
        public static string OptionsBaseBuilding_SearchField = "Search (partial match)";
        public static string OptionsBaseBuilding_ResetButton = "Reset to Default";
        public static string OptionsBaseBuilding_NoAssetsHint = "No building assets are currently available (e.g. opened from the main menu). Open this again after loading a city to see subscribed buildings in the list.";
        public static string OptionsBaseBuilding_ListTruncatedFormat = "* Showing {0} of {1} (narrow your search)";
        public static string OptionsBaseBuilding_ListCountFormat = "{0} item(s)";
        public static string OptionsBaseBuilding_NotSetLabel = "Current designation: (not set. This base type cannot be placed. Please designate a building)";
        public static string OptionsBaseBuilding_CurrentFormat = "Current designation: {0}";
        public static string OptionsBaseBuilding_CurrentAutoFormat = "Current designation: {0}  (auto-detected)";
    }
}
