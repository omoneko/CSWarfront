namespace CSWarfront.Game
{
    /// <summary>Strings for the Mod Options screen built directly by Mod.cs (the Options subpages
    /// contribute their own partial files). See WarfrontStrings.cs for the localization scheme.</summary>
    public static partial class WarfrontStrings
    {
        // --- Options tab captions (Task138) -------------------------------------------------------
        // Kept to one short word each: the tabs are sized to their caption and wrap onto a second row
        // when the window is narrow, so long captions cost the strip a whole line.
        public static string OptionsTab_General = "General";
        public static string OptionsTab_Factions = "Factions";
        public static string OptionsTab_Buildings = "Buildings";
        public static string OptionsTab_Models = "Models";
        public static string OptionsTab_Controls = "Controls";
        public static string OptionsTab_Audio = "Audio";

        public static string Options_BasePlacementGroup = "Base placement";
        public static string Options_BuildFactionDropdown = "Faction to build for (designated base building)";

        public static string Options_UnitCommandsGroup = "Unit commands (select units with a box drag, then press)";
        public static string Options_SelectionModeKey = "Toggle unit selection mode (drag-box select; single click always works)";
        public static string Options_BuildPanelKey = "Toggle military construction panel";
        public static string Options_FreeAdvanceKey = "Free advance (march at full speed toward the nearest hostile base)";
        public static string Options_HoldKey = "Hold (stop in place, still fires at anything in range)";
        public static string Options_RallyKey = "Rally (then right-click a destination; units move there, stop, and fight defensively only)";

        // Task139: how the city itself reacts to a battle fought inside it.
        public static string Options_BattlefieldGroup = "Battle damage";
        public static string Options_GarrisonAbandons = "Buildings held by infantry look abandoned while occupied (the residents have fled)";

        public static string Options_InvasionGroup = "Invasion events (waves attack from outside the city)";
        public static string Options_InvasionEnable = "Enable invasion events";
        public static string Options_InvasionFrequency = "Invasion frequency";
        public static string Options_InvasionFreqLow = "Low (about every 5 days)";
        public static string Options_InvasionFreqMedium = "Medium (about every 2-3 days)";
        public static string Options_InvasionFreqHigh = "High (about every day)";

        public static string Options_FactionIconsGroup = "Faction icons";
        public static string Options_FactionIconsToggle = "Show a small faction-colored marker above each unit";

        public static string Options_SoundGroup = "Firing sounds";
        public static string Options_SoundVolume = "Sound volume";
        public static string Options_SoundMute = "Mute all firing/kill sounds";
    }
}
