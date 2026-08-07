using System;
using System.IO;
using System.Reflection;
using System.Xml;
using ColossalFramework;
using ColossalFramework.Plugins;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Loads unit-class base-stat overrides from unit-stats.xml in the MOD folder and feeds them to
    /// Core.UnitStatOverrides (Task92, user request "externalize UnitType definitions to XML/JSON",
    /// design doc §4.3).
    ///
    /// File format (all attributes are optional; omitted stats keep the MOD default values):
    ///   &lt;UnitStats&gt;
    ///     &lt;Unit category="Tank" hp="140" attack="42" range="60" armor="10" speedKmh="40"
    ///           splash="0" cost="60" buildTime="8" accuracy="0.70" fireIntervalHours="1.20" /&gt;
    ///   &lt;/UnitStats&gt;
    /// All values are Tier1-based (the MOD's TierScaling applies to Tier2 and above in the same way as
    /// usual). category is a UnitCategory enum name (Infantry/MechInfantry/Apc/Tank/Artillery/
    /// DroneInfantry/AntiAir/Destroyer/Carrier/AirSuperiority/TacticalBomber/SuicideDrone).
    ///
    /// If the file does not exist, a template containing all current default values is generated
    /// (so that subscribers can tune the balance simply by editing the numbers). Loading happens once
    /// per level load (EnsureLoaded is idempotent). Reloading the save is required for changes to take
    /// effect.
    ///
    /// Threading note: EnsureLoaded must be called from the load path
    /// (WarStateDataExtension.OnLoadData / MilitaryManager state initialization), before roster
    /// construction.
    /// </summary>
    internal static class UnitStatsFile
    {
        private const string FileName = "unit-stats.xml";

        private static bool _loadAttempted;

        /// <summary>Idempotent. Call before roster construction (RegisterAll).</summary>
        public static void EnsureLoaded()
        {
            if (_loadAttempted) return;
            _loadAttempted = true;

            try
            {
                string modPath = ResolveModPath();
                if (string.IsNullOrEmpty(modPath))
                {
                    ModConfig.Log("UnitStatsFile: mod path unavailable; using built-in stats.");
                    return;
                }

                string path = Path.Combine(modPath, FileName);
                if (!File.Exists(path))
                {
                    WriteTemplate(path);
                    return; // the template equals the defaults, so there is no need to load it
                }

                Load(path);
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitStatsFile.EnsureLoaded error (using built-in stats): " + e);
            }
        }

        /// <summary>Called on level unload. Makes the next load re-read the file (so that file edits
        /// made during play are picked up in the next session). The overrides themselves have already
        /// been baked in at roster construction time, so clearing UnitStatOverrides here does not
        /// affect the in-progress state.</summary>
        public static void Reset()
        {
            _loadAttempted = false;
            UnitStatOverrides.Clear();
        }

        private static void Load(string path)
        {
            var doc = new XmlDocument();
            doc.Load(path);

            XmlNodeList nodes = doc.SelectNodes("/UnitStats/Unit");
            if (nodes == null) return;

            int applied = 0;
            foreach (XmlNode node in nodes)
            {
                XmlElement el = node as XmlElement;
                if (el == null) continue;

                string categoryName = el.GetAttribute("category");
                UnitCategory category;
                try
                {
                    category = (UnitCategory)Enum.Parse(typeof(UnitCategory), categoryName, true);
                }
                catch (Exception)
                {
                    ModConfig.LogError("UnitStatsFile: unknown category '" + categoryName + "' (entry skipped)");
                    continue;
                }

                var o = new UnitStatOverride
                {
                    Hp = ParseAttr(el, "hp"),
                    Attack = ParseAttr(el, "attack"),
                    Range = ParseAttr(el, "range"),
                    Armor = ParseAttr(el, "armor"),
                    SpeedKmh = ParseAttr(el, "speedKmh"),
                    Splash = ParseAttr(el, "splash"),
                    Cost = ParseAttr(el, "cost"),
                    BuildTime = ParseAttr(el, "buildTime"),
                    Accuracy = ParseAttr(el, "accuracy"),
                    FireIntervalHours = ParseAttr(el, "fireIntervalHours"),
                    AmmoCombatHours = ParseAttr(el, "ammoCombatHours") // Task99: continuous firing time (0 = infinite ammo)
                };
                UnitStatOverrides.Set(category, o);
                applied++;
            }

            ModConfig.Log("UnitStatsFile: loaded " + applied + " unit stat override(s) from " + path);
        }

        private static float? ParseAttr(XmlElement el, string name)
        {
            string raw = el.GetAttribute(name);
            if (string.IsNullOrEmpty(raw)) return null;
            float value;
            if (float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value))
                return value;
            ModConfig.LogError("UnitStatsFile: could not parse " + name + "='" + raw + "' (attribute ignored)");
            return null;
        }

        /// <summary>Generates a template containing all current MOD default values (Tier1-based).</summary>
        private static void WriteTemplate(string path)
        {
            try
            {
                using (var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false)))
                {
                    w.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                    w.WriteLine("<!-- CSWarfront unit stat overrides.");
                    w.WriteLine("     Values are Tier-1 base stats; tiers 2-5 scale from them automatically.");
                    w.WriteLine("     Delete an attribute to fall back to the mod default for that stat.");
                    w.WriteLine("     Changes take effect after reloading a save. -->");
                    w.WriteLine("<UnitStats>");
                    WriteTemplateRow(w, LandUnitRoster.Get(UnitCategory.Infantry, 1));
                    WriteTemplateRow(w, LandUnitRoster.Get(UnitCategory.MechInfantry, 1));
                    WriteTemplateRow(w, LandUnitRoster.Get(UnitCategory.Apc, 1));
                    WriteTemplateRow(w, LandUnitRoster.Get(UnitCategory.Tank, 1));
                    WriteTemplateRow(w, LandUnitRoster.Get(UnitCategory.Artillery, 1));
                    WriteTemplateRow(w, LandUnitRoster.Get(UnitCategory.DroneInfantry, 1));
                    WriteTemplateRow(w, LandUnitRoster.Get(UnitCategory.AntiAir, 1));
                    WriteTemplateRow(w, LandUnitRoster.Get(UnitCategory.SupplyTruck, 1)); // Task99
                    WriteTemplateRow(w, NavalUnitRoster.Get(UnitCategory.Destroyer, 1));
                    WriteTemplateRow(w, NavalUnitRoster.Get(UnitCategory.Carrier, 1));
                    WriteTemplateRow(w, AirUnitRoster.Get(UnitCategory.AirSuperiority, 1));
                    WriteTemplateRow(w, AirUnitRoster.Get(UnitCategory.TacticalBomber, 1));
                    WriteTemplateRow(w, AirUnitRoster.Get(UnitCategory.SuicideDrone, 1));
                    WriteTemplateRow(w, AirUnitRoster.Get(UnitCategory.AttackHelicopter, 1));    // Task101
                    WriteTemplateRow(w, AirUnitRoster.Get(UnitCategory.TransportHelicopter, 1)); // Task101
                    WriteTemplateRow(w, LandUnitRoster.Get(UnitCategory.MilitaryTrain, 1));      // Task101
                    w.WriteLine("</UnitStats>");
                }
                ModConfig.Log("UnitStatsFile: wrote default template to " + path);
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitStatsFile.WriteTemplate error: " + e);
            }
        }

        private static void WriteTemplateRow(StreamWriter w, UnitType t)
        {
            if (t == null) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            // Speed is written by converting the Core internal representation back to author-facing
            // km/h (it is converted forward again on load).
            float speedKmh = SpeedCalibration.KmhFromUnitsPerGameHour(t.Speed);
            w.WriteLine(string.Format(inv,
                "  <Unit category=\"{0}\" hp=\"{1:0.##}\" attack=\"{2:0.##}\" range=\"{3:0.##}\" armor=\"{4:0.##}\"" +
                " speedKmh=\"{5:0.##}\" splash=\"{6:0.##}\" cost=\"{7:0.##}\" buildTime=\"{8:0.##}\"" +
                " accuracy=\"{9:0.##}\" fireIntervalHours=\"{10:0.##}\" ammoCombatHours=\"{11:0.##}\" />",
                t.Category, t.MaxHP, t.Attack, t.Range, t.Armor,
                speedKmh, t.SplashRadius, t.Cost, t.BuildTime, t.Accuracy, t.FireIntervalHours,
                t.AmmoCombatHours));
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
                ModConfig.LogError("UnitStatsFile.ResolveModPath error: " + e);
                return null;
            }
        }
    }
}
