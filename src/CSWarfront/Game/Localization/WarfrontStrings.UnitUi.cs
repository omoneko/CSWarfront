namespace CSWarfront.Game
{
    /// <summary>Strings for the unit UI (unit info panel, box selection, and command hotkey toasts). See WarfrontStrings.cs for the localization scheme.</summary>
    public static partial class WarfrontStrings
    {
        // --- Unit info panel (Game/UI/UnitInfoPanel.cs) ----------------------------------------
        public static string UnitInfo_TitleFormat = "{0}  (Tier {1})";
        public static string UnitInfo_UnknownFaction = "?";
        public static string UnitInfo_FactionLabel = "Faction: ";
        public static string UnitInfo_HpLabel = "\nHP: ";
        public static string UnitInfo_AttackLabel = "\nAttack: ";
        public static string UnitInfo_AttackPerHourRangeLabel = "/h  Range: ";
        public static string UnitInfo_ArmorLabel = "\nArmor: ";
        public static string UnitInfo_SpeedLabel = "    Speed: ";
        public static string UnitInfo_SpeedUnit = "km/h";
        public static string UnitInfo_AccuracyLabel = "\nAccuracy: ";
        public static string UnitInfo_SpottedSuffix = " (Spotted)";
        public static string UnitInfo_AmmoLabel = "\nAmmo: ";
        public static string UnitInfo_OutOfAmmoSuffix = "  [OUT OF AMMO]";
        public static string UnitInfo_SuppliesLabel = "\nSupplies: ";
        public static string UnitInfo_StatusLabel = "\nStatus: ";
        public static string UnitInfo_TargetLabel = "\nTarget: ";
        public static string UnitInfo_TargetUnitPrefix = "Unit#";
        public static string UnitInfo_TargetNone = "none";
        public static string UnitInfo_PathLabel = "\nPath: ";
        public static string UnitInfo_PathDirect = "Direct";
        public static string UnitInfo_OrderLabel = "\nOrder: ";

        public static string UnitInfo_StateIdle = "Idle";
        public static string UnitInfo_StateMoving = "Moving";
        public static string UnitInfo_StateEngaging = "Engaging";
        public static string UnitInfo_StateDead = "Dead";

        public static string UnitInfo_OrderAdvance = "Advance";
        public static string UnitInfo_OrderHold = "Hold";
        public static string UnitInfo_OrderRallyHold = "Rally & Hold";
        public static string UnitInfo_OrderAi = "AI";

        // --- Command toasts (Game/UI/UnitCommandInput.cs, Game/UI/UnitBoxSelection.cs) --------
        public static string Toast_SelectionModeOn = "Unit selection mode ON (drag to box-select)";
        public static string Toast_SelectionModeOff = "Unit selection mode OFF";
        public static string Toast_AdvanceFormat = "Advance x{0}";
        public static string Toast_HoldFormat = "Hold x{0}";
        public static string Toast_RallyArmed = "Rally & Hold (right-click to set a destination)";
        public static string Toast_RallyCancelled = "Cancelled rally targeting";
        public static string Toast_RallyPointSetFormat = "Rally point set x{0}";
    }
}
