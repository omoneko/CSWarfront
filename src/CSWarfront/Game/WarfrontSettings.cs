using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Holds the selected "build target faction" used when placing an Options-designated building
    /// (BaseBuildingDesignation) as a military base, plus the key bindings for unit commands (Task48)
    /// (Task82: comment wording updated to the current approach after removing the duplicated-prefab
    /// mechanism on the electricity tab; the settings themselves and their behavior are unchanged).
    /// To avoid a known CS pitfall (naming the settings class/file the same as the assembly causes a
    /// "same key" exception and a settings-deletion loop), nothing is persisted; values live in memory
    /// only (MVP). The class name deliberately does not match the assembly name "CSWarfront".
    /// For the same reason the Task48 key bindings do not use SavedInt (GameSettings persistence)
    /// either; like the existing BuildFactionId they are kept in memory only (resetting to defaults
    /// across sessions is acceptable, MVP).
    /// </summary>
    public static class WarfrontSettings
    {
        public const int MaxFactions = 5;

        private static int _buildFactionId; // 0..MaxFactions-1, default 0 (Red)

        public static byte BuildFactionId { get { return (byte)_buildFactionId; } }

        public static void SetBuildFactionId(int id)
        {
            if (id < 0) id = 0;
            if (id > MaxFactions - 1) id = MaxFactions - 1;
            _buildFactionId = id;
        }

        /// <summary>Task88 (user request): faction names are color names matching the sphere icons /
        /// faction colors (UnitMaterialFactory.FactionColors: 0=red, 1=blue, 2=green, 3=yellow,
        /// 4=magenta).</summary>
        public static string[] FactionNames
        {
            get { return new[] { "Red", "Blue", "Green", "Yellow", "Magenta" }; }
        }

        // --- Task94: external invasion events (Workshop comment request: "option where enemies attack from outside the city") ---

        /// <summary>While ON, Core.InvasionEvents spawns raid squads at the map edge at random times
        /// (default OFF = the traditional playstyle of manually placing enemy bases).
        /// Like the other WarfrontSettings, this is memory-only (reverts to the session default, MVP
        /// policy).</summary>
        public static bool InvasionEventsEnabled = false;

        /// <summary>Invasion frequency (0=Low/1=Medium/2=High, index into Core.InvasionEvents.ChancePerCheck).</summary>
        public static int InvasionFrequencyIndex = 1;

        // --- Task139: civilian buildings held by troops are shown as abandoned ---

        /// <summary>While ON, a city building infantry are holding (BuildingGarrisonStep) carries
        /// Building.Flags.Abandoned for as long as they are in it — the residents have fled the fighting.
        /// The flag is this mod's alone and is handed back when the troops leave, on save and on level
        /// unload (GarrisonAbandonSync), so nothing permanent happens to the city. Default ON: it is the
        /// behaviour that was asked for, and it reverts by itself. Turn it off to keep the city looking
        /// untouched while the fighting goes on around it.
        /// Like the other WarfrontSettings, memory-only (reverts to this default each session).</summary>
        public static bool GarrisonAbandonsBuildings = true;

        // --- Task49: toggle for the faction icons above units (small spheres, Game/UnitVisuals) ---

        private static bool _showFactionIcons = true; // default ON

        public static bool ShowFactionIcons
        {
            get { return _showFactionIcons; }
            set { _showFactionIcons = value; }
        }

        // --- Task48: key bindings for unit commands ---

        /// <summary>Hotkey candidates (numpad-centric, same idea as MissileDisaster.ModSettings.KeyOptions:
        /// only numpad/function keys that are unlikely to clash with vanilla controls are offered).
        /// The dropdowns in OnSettingsUI manage the selected value as an index into this array.</summary>
        public static readonly KeyCode[] KeyOptions =
        {
            KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3, KeyCode.Keypad4, KeyCode.Keypad5,
            KeyCode.Keypad6, KeyCode.Keypad7, KeyCode.Keypad8, KeyCode.Keypad9, KeyCode.Keypad0,
            KeyCode.F5, KeyCode.F6, KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12,
        };

        private static KeyCode _freeAdvanceKey = KeyCode.Keypad1;
        private static KeyCode _holdKey = KeyCode.Keypad2;
        private static KeyCode _rallyKey = KeyCode.Keypad3;

        // --- Task76: hotkey that toggles unit selection mode on/off ---

        private static KeyCode _selectionModeKey = KeyCode.Keypad0;

        private static KeyCode _buildPanelKey = KeyCode.Keypad4;

        /// <summary>Task102: toggle key that opens/closes the military build panel (MilitaryBuildPanel). Default Numpad 4.</summary>
        public static KeyCode BuildPanelKey
        {
            get { return _buildPanelKey; }
            set { _buildPanelKey = value; }
        }

        /// <summary>Each press toggles unit selection mode (box-drag area selection) ON/OFF.
        /// Drag-based area selection only works while it is ON. Single-click selection
        /// (Game/UI/UnitSelection) always works regardless of this mode's state. Default Numpad 0.
        /// The actual toggle handling lives in Game/UI/UnitBoxSelection (same pattern as the other
        /// command keys that are simply picked from the KeyOptions list; see OnSettingsUI in
        /// Game/Mod.cs).</summary>
        public static KeyCode SelectionModeKey
        {
            get { return _selectionModeKey; }
            set { _selectionModeKey = value; }
        }

        /// <summary>Free advance (send the selected units toward the nearest enemy stronghold, each at its
        /// own top speed). Default Numpad 1.</summary>
        public static KeyCode FreeAdvanceKey
        {
            get { return _freeAdvanceKey; }
            set { _freeAdvanceKey = value; }
        }

        /// <summary>Hold (stop the selected units in place; they keep returning fire at enemies within
        /// range). Default Numpad 2.</summary>
        public static KeyCode HoldKey
        {
            get { return _holdKey; }
            set { _holdKey = value; }
        }

        /// <summary>Key that starts "rally and wait" (move the selected units to a point designated by
        /// right-click; after arrival they stop and stick to passive defense). Pressing it enters a
        /// "the next right-click designates the point" mode. Default Numpad 3.</summary>
        public static KeyCode RallyKey
        {
            get { return _rallyKey; }
            set { _rallyKey = value; }
        }

        // --- Task51: volume settings for per-branch firing/kill sounds ---
        // Memory-only like the other settings (see the comment at the top of the class; resetting to
        // defaults across sessions is acceptable, MVP).

        private static int _soundVolume = 50; // 0..100, default 50%

        /// <summary>Volume of firing/kill sounds (0=silent to 100=max). WarfrontSoundPlayer reads it
        /// every time as AudioSource.volume = SoundVolume / 100f.</summary>
        public static int SoundVolume
        {
            get { return _soundVolume; }
            set { _soundVolume = value < 0 ? 0 : (value > 100 ? 100 : value); }
        }

        private static bool _soundMuted; // default OFF (sounds play)

        /// <summary>While ON, WarfrontSoundPlayer plays no sound at all (regardless of the SoundVolume
        /// value).</summary>
        public static bool SoundMuted
        {
            get { return _soundMuted; }
            set { _soundMuted = value; }
        }
    }
}
