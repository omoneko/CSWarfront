namespace CSWarfront.Game
{
    /// <summary>
    /// Every player-facing UI string, as a public static field whose initializer is the built-in
    /// English default (Task113, community localization).
    ///
    /// How localization works:
    ///  - Field name = the key in Locales/&lt;lang&gt;.txt (e.g. "BuildPanel_Title = ...").
    ///  - LocaleLoader.EnsureLoaded() detects the game language and overwrites these fields via
    ///    reflection from the matching locale file. No file / unknown key = the English default
    ///    stays, so a partial translation is always safe.
    ///  - UI code references WarfrontStrings.Xxx instead of string literals. Strings with runtime
    ///    values are string.Format templates ({0}, {1}, ...) — translators must keep the
    ///    placeholders.
    ///
    /// The class is partial: each UI area contributes its own WarfrontStrings.*.cs file next to
    /// this one, so the field groups stay reviewable per panel. Debug/log strings are deliberately
    /// NOT localized (logs should stay grep-able in English).
    ///
    /// To add a language: copy Locales/en.txt to Locales/&lt;two-letter code&gt;.txt (the code the
    /// game reports, e.g. de/fr/es/zh/ja), translate the values, and submit a pull request to
    /// https://github.com/omoneko/CSWarfront (or just drop the file into the mod folder locally).
    /// </summary>
    public static partial class WarfrontStrings
    {
        // --- Mod entry (Content Manager) -------------------------------------------------------
        public static string Mod_Description =
            "A tier-based military simulation with 5 factions (land/sea/air, bases, territory, occupation). Building the building designated in Options turns it into a military base.";

        // --- Shared -----------------------------------------------------------------------------
        public static string Common_None = "None";
        public static string Common_Close = "Close";
        public static string Faction_Invader = "Invader";
    }
}
