namespace CSWarfront.Game
{
    /// <summary>Strings for the military construction panel (MilitaryBuildPanel) and the base info panel family (BaseInfoPanel*). See WarfrontStrings.cs for the localization scheme.</summary>
    public static partial class WarfrontStrings
    {
        // --- MilitaryBuildPanel (the "military tab" construction panel) --------------------------
        public static string BuildPanel_Title = "Military Construction";
        public static string BuildPanel_ToggleButtonText = "WF";
        public static string BuildPanel_ToggleTooltip = "CS:WARFRONT military construction (Numpad 4)";
        public static string BuildPanel_RowArmyBase = "Army Base";
        public static string BuildPanel_RowNavalBase = "Naval Base";
        public static string BuildPanel_RowAirBase = "Air Base";
        public static string BuildPanel_RowMissileBase = "Missile Base";
        public static string BuildPanel_RowBunker = "Bunker";
        public static string BuildPanel_RowArtilleryPosition = "Artillery Position";
        public static string BuildPanel_RowSupplyDepot = "Supply Depot";
        public static string BuildPanel_RowTrench = "Trench";
        public static string BuildPanel_RowCargoStation = "Cargo Station";
        public static string BuildPanel_RowAtPillbox = "AT Pillbox";    // Task117
        public static string BuildPanel_RowAaPosition = "AA Position";  // Task117
        public static string BuildPanel_RowSuffixAssetMissing = " (asset missing)";
        public static string BuildPanel_RowSuffixNotSet = " (not set)";
        public static string BuildPanel_RowTooltipNotSet = "Assign a building in Options > Base Buildings";
        public static string BuildPanel_ToastAssetNotLoaded = "Asset not loaded: {0}";
        public static string BuildPanel_ToastPlacing = "Placing: {0}  (Esc to cancel)";

        // --- MilitaryBuildPanel: defense layout save/rebuild (Task114) ---------------------------
        public static string BuildPanel_RegisterDefenseButton = "Save Defense Layout";
        public static string BuildPanel_RegisterDefenseTooltip = "Remember every friendly fortification currently on the map, with its position and orientation";
        public static string BuildPanel_RebuildDefenseButton = "Rebuild Defenses";
        public static string BuildPanel_RebuildDefenseTooltip = "Rebuild the saved fortifications that were destroyed. Intact positions are untouched; normal construction costs apply";
        public static string BuildPanel_ToastLayoutSaved = "Defense layout saved: {0} position(s)";
        public static string BuildPanel_ToastNoLayout = "No defense layout saved yet - click Save Defense Layout first";
        public static string BuildPanel_ToastNothingMissing = "All saved defense positions are intact";
        public static string BuildPanel_ToastRebuilt = "Rebuilt {0} of {1} missing position(s)";
        public static string BuildPanel_ToastRebuiltOutOfMoney = "Rebuilt {0} of {1} missing position(s) - out of money";

        // --- BaseInfoPanel: title / faction section / model button -------------------------------
        public static string BaseInfo_Title = "CSWarfront Military Base";
        public static string BaseInfo_SectionFaction = "Faction";
        public static string BaseInfo_Unaffiliated = "Unaffiliated";
        public static string BaseInfo_ModelSettingsButton = "Model Settings";

        // --- BaseInfoPanel: base type / producible domain labels ---------------------------------
        public static string BaseInfo_TypeArmyBase = "Army Base";
        public static string BaseInfo_TypeNavalBase = "Naval Base";
        public static string BaseInfo_TypeAirBase = "Air Base";
        public static string BaseInfo_TypeMissileBase = "Missile Base";
        public static string BaseInfo_DomainLand = "Land";
        public static string BaseInfo_DomainSea = "Sea";
        public static string BaseInfo_DomainAir = "Air";

        // --- BaseInfoPanel: status block fragments (StringBuilder pieces; translators must keep
        // --- the leading \n line breaks and any leading/trailing spaces) -------------------------
        public static string BaseInfo_StatusTypePrefix = "Type: ";
        public static string BaseInfo_StatusCanProducePrefix = "  Can produce: ";
        public static string BaseInfo_StatusFactionPrefix = "\nFaction: ";
        public static string BaseInfo_StatusHqSuffix = " (HQ)";
        public static string BaseInfo_StatusHpPrefix = "\nHP: ";
        public static string BaseInfo_StatusTreasuryPrefix = "\nTreasury: ";
        public static string BaseInfo_StatusManpowerPrefix = "\nManpower: ";
        public static string BaseInfo_StatusProductionPrefix = "  Production: ";
        public static string BaseInfo_StatusSuppliesPrefix = "  Supplies: ";
        public static string BaseInfo_StatusStoredSuppliesPrefix = "\nStored supplies: ";
        public static string BaseInfo_StatusRailConnected = "  [Rail: connected]";
        public static string BaseInfo_StatusRailNotConnected = "  [Rail: NOT CONNECTED]";
        public static string BaseInfo_StatusFortAmmoPrefix = "\nFort ammo: ";
        public static string BaseInfo_StatusOutOfAmmo = "  [OUT OF AMMO]";
        public static string BaseInfo_StatusIncomePrefix = "\nIncome: +";
        public static string BaseInfo_StatusIncomeSuffix = " / 6h";
        public static string BaseInfo_StatusTechTierPrefix = "\nTech: Tier ";
        public static string BaseInfo_StatusTierMaxSuffix = "  (max)";
        public static string BaseInfo_StatusResearchPrefix = "  (research ";
        public static string BaseInfo_StatusResearchNextSeparator = " / next ";
        public static string BaseInfo_StatusUnitsPrefix = "\nUnits: ";
        public static string BaseInfo_StatusProducingNone = "\nProducing: none";
        public static string BaseInfo_StatusProducingPrefix = "\nProducing: ";
        public static string BaseInfo_StatusHoursLeftSuffix = "h left)";
        public static string BaseInfo_StatusQueuedPrefix = "\nQueued: ";
        public static string BaseInfo_StatusCaptureGracePrefix = "\nCapture grace: ";

        // --- BaseInfoPanel: unit production section ----------------------------------------------
        public static string BaseInfo_AutoProduceOn = "Auto-produce: ON";
        public static string BaseInfo_AutoProduceOff = "Auto-produce: OFF";
        public static string BaseInfo_AutoProduceHint = "The AI will manage this base automatically";
        public static string BaseInfo_ProduceButton = "Produce";
        public static string BaseInfo_CancelButton = "Cancel";
        public static string BaseInfo_InvestButton = "Invest in Research (¥{0})";
        public static string BaseInfo_UnlockTierButton = "Unlock Tier";
        public static string BaseInfo_UnitItemFormat = "{0}  (¥{1})";
        public static string BaseInfo_UnitLockedSuffix = " [Locked]";
        public static string BaseInfo_QueueNone = "Queue: none";
        public static string BaseInfo_QueuePrefix = "Queue: ";
        public static string BaseInfo_QueueProducingSuffix = "(producing)";

        // --- BaseInfoPanel: missile base section -------------------------------------------------
        public static string BaseInfo_BuildMissileButton = "Build Missile (¥{0})";
        public static string BaseInfo_SetLaunchTargetButton = "Set Launch Target";
        public static string BaseInfo_AutoBuildOn = "Auto-build: ON";
        public static string BaseInfo_AutoBuildOff = "Auto-build: OFF";
        public static string BaseInfo_AutoLaunchOn = "Auto-launch: ON";
        public static string BaseInfo_AutoLaunchOff = "Auto-launch: OFF";
        public static string BaseInfo_MissileStockpileFormat = "Stockpile: {0} / {1}";
        public static string BaseInfo_MissileBuildingFormat = "\nBuilding: {0}%  ({1}h left)";
        public static string BaseInfo_MissileBuildingNone = "\nBuilding: none";

        // --- BaseInfoPanel: result / error messages ----------------------------------------------
        public static string BaseInfo_MsgBaseNotFound = "Base not found";
        public static string BaseInfo_MsgNoOwner = "No owner";
        public static string BaseInfo_MsgUnknownType = "Unknown type";
        public static string BaseInfo_MsgQueueFull = "Queue full";
        public static string BaseInfo_MsgInsufficientFunds = "Insufficient funds";
        public static string BaseInfo_MsgTierLocked = "Tier not unlocked";
        public static string BaseInfo_MsgWrongDomain = "This base cannot produce this type";
        public static string BaseInfo_MsgNoOrdersToCancel = "No orders to cancel";
        public static string BaseInfo_MsgMaxTier = "Max tier";
        public static string BaseInfo_MsgInsufficientResearch = "Insufficient research points";
        public static string BaseInfo_MsgNotMissileBase = "Not a missile base";
        public static string BaseInfo_MsgAlreadyBuilding = "Already building";
        public static string BaseInfo_MsgStockpileFull = "Stockpile full";
    }
}
