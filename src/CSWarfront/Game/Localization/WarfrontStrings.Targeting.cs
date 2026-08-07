namespace CSWarfront.Game
{
    /// <summary>Strings for the world-click targeting modes (missile launch / trench line placement). See WarfrontStrings.cs for the localization scheme.</summary>
    public static partial class WarfrontStrings
    {
        public static string Missile_ArmedPrompt = "Please set a missile launch target";
        public static string Missile_CancelledToast = "Cancelled launch targeting";
        public static string Missile_LaunchedToast = "Launched";
        public static string Missile_FailNoStockpile = "No missiles in stockpile";
        public static string Missile_FailOutOfRange = "Out of range";
        public static string Missile_FailNoOwner = "No owner";
        public static string Missile_FailNotMissileBase = "Not a missile base";
        public static string Missile_FailBaseNotFound = "Base not found";

        public static string Trench_StartPrompt = "Trench line: right-click the START point (Esc to cancel)";
        public static string Trench_CancelledToast = "Cancelled trench line";
        public static string Trench_EndPrompt = "Trench line: right-click the END point";
        public static string Trench_DiggingToast = "Digging trench line...";
    }
}
