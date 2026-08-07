using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Globalization;
using ColossalFramework.Plugins;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Loads Locales/&lt;lang&gt;.txt from the mod folder and overwrites WarfrontStrings' fields via
    /// reflection (Task113, community localization; see WarfrontStrings for the scheme).
    ///
    ///  - Language detection: LocaleManager.instance.language (the game's own two-letter code).
    ///    "en" (or anything unresolvable) keeps the built-in English defaults.
    ///  - Idempotent per language: calling EnsureLoaded() again is a no-op unless the game language
    ///    changed since the last load (then the file for the new language is applied on top of a
    ///    fresh English baseline, so switching languages back and forth stays correct).
    ///  - Locales/en.txt is (re)generated from the current defaults when missing, so subscribers and
    ///    translators always have an up-to-date template next to the mod.
    ///  - Never throws: any failure logs once and leaves the English defaults.
    ///
    /// Call sites: Mod.OnSettingsUI (options screens) and MilitaryManager.EnsureInitialized (level
    /// UI) — i.e. before the first string is displayed on either path. Already-built panels do not
    /// re-render on a language change; strings apply from the next panel build (documented
    /// limitation, same as most CS1 mods).
    /// </summary>
    internal static class LocaleLoader
    {
        private const string LocalesFolder = "Locales";

        private static string _loadedLanguage;                       // last applied language, null = never
        private static Dictionary<string, string> _englishDefaults;  // field name -> built-in default

        /// <summary>Idempotent (per game language). Safe to call from any UI entry point.</summary>
        public static void EnsureLoaded()
        {
            try
            {
                string language = CurrentLanguage();
                if (language == _loadedLanguage) return;

                CaptureEnglishDefaultsOnce();
                RestoreEnglishDefaults(); // fresh baseline so partial translations never leak between languages

                string modPath = ResolveModPath();
                if (!string.IsNullOrEmpty(modPath))
                {
                    string dir = Path.Combine(modPath, LocalesFolder);
                    EnsureTemplate(dir);

                    if (language != "en")
                    {
                        string path = Path.Combine(dir, language + ".txt");
                        if (File.Exists(path))
                        {
                            int applied = Apply(LocaleFileParser.Parse(File.ReadAllText(path)));
                            ModConfig.Log("LocaleLoader: applied " + applied + " string(s) from " + path);
                        }
                    }
                }

                _loadedLanguage = language;
            }
            catch (Exception e)
            {
                _loadedLanguage = "en"; // do not retry every call on a persistent failure
                ModConfig.LogError("LocaleLoader.EnsureLoaded error (using built-in English): " + e);
            }
        }

        private static string CurrentLanguage()
        {
            try
            {
                if (LocaleManager.exists)
                {
                    string lang = LocaleManager.instance.language;
                    if (!string.IsNullOrEmpty(lang)) return lang;
                }
            }
            catch (Exception)
            {
                // LocaleManager unavailable (too early / headless): fall through to English.
            }
            return "en";
        }

        private static FieldInfo[] StringFields()
        {
            FieldInfo[] fields = typeof(WarfrontStrings).GetFields(BindingFlags.Public | BindingFlags.Static);
            var result = new List<FieldInfo>(fields.Length);
            for (int i = 0; i < fields.Length; i++)
                if (fields[i].FieldType == typeof(string)) result.Add(fields[i]);
            return result.ToArray();
        }

        private static void CaptureEnglishDefaultsOnce()
        {
            if (_englishDefaults != null) return;
            _englishDefaults = new Dictionary<string, string>();
            foreach (FieldInfo f in StringFields())
                _englishDefaults[f.Name] = (string)f.GetValue(null);
        }

        private static void RestoreEnglishDefaults()
        {
            foreach (FieldInfo f in StringFields())
            {
                string value;
                if (_englishDefaults.TryGetValue(f.Name, out value)) f.SetValue(null, value);
            }
        }

        private static int Apply(Dictionary<string, string> map)
        {
            int applied = 0;
            foreach (FieldInfo f in StringFields())
            {
                string value;
                if (map.TryGetValue(f.Name, out value) && !string.IsNullOrEmpty(value))
                {
                    f.SetValue(null, value);
                    applied++;
                }
            }
            return applied;
        }

        /// <summary>Writes Locales/en.txt from the current English defaults when missing (the
        /// translation template; same policy as UnitStatsFile.WriteTemplate).</summary>
        private static void EnsureTemplate(string dir)
        {
            try
            {
                string path = Path.Combine(dir, "en.txt");
                if (File.Exists(path)) return;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                CaptureEnglishDefaultsOnce();
                using (var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false)))
                {
                    w.WriteLine("# CSWarfront UI strings (English template).");
                    w.WriteLine("# To translate: copy this file to <language code>.txt (the code the game uses,");
                    w.WriteLine("# e.g. de/fr/es/zh/ja), translate the values, keep the {0}/{1} placeholders and");
                    w.WriteLine("# the \\n line-break escapes. Missing keys fall back to English.");
                    w.WriteLine("# Contributions welcome: https://github.com/omoneko/CSWarfront");
                    w.WriteLine();
                    foreach (FieldInfo f in StringFields())
                        w.WriteLine(f.Name + " = " + LocaleFileParser.Escape(_englishDefaults[f.Name]));
                }
                ModConfig.Log("LocaleLoader: wrote template " + path);
            }
            catch (Exception e)
            {
                ModConfig.LogError("LocaleLoader.EnsureTemplate error: " + e);
            }
        }

        private static string ResolveModPath()
        {
            try
            {
                PluginManager.PluginInfo info =
                    Singleton<PluginManager>.instance.FindPluginInfo(Assembly.GetExecutingAssembly());
                return info != null ? info.modPath : null;
            }
            catch (Exception e)
            {
                ModConfig.LogError("LocaleLoader.ResolveModPath error: " + e);
                return null;
            }
        }
    }
}
