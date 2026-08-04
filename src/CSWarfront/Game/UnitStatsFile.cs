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
    /// MODフォルダの unit-stats.xml から兵科基礎値の上書きを読み込み、Core.UnitStatOverridesへ
    /// 供給する（Task92、ユーザー要望「UnitType定義のXML/JSON外出し」設計書§4.3）。
    ///
    /// ファイル形式（属性は全て省略可＝省略した項目はMOD既定値のまま）:
    ///   &lt;UnitStats&gt;
    ///     &lt;Unit category="Tank" hp="140" attack="42" range="60" armor="10" speedKmh="40"
    ///           splash="0" cost="60" buildTime="8" accuracy="0.70" fireIntervalHours="1.20" /&gt;
    ///   &lt;/UnitStats&gt;
    /// 値はいずれもTier1基準（Tier2以降はMODのTierScalingが同様にかかる）。categoryは
    /// UnitCategoryの列挙名（Infantry/MechInfantry/Apc/Tank/Artillery/DroneInfantry/AntiAir/
    /// Destroyer/Carrier/AirSuperiority/TacticalBomber/SuicideDrone）。
    ///
    /// ファイルが無い場合は、現在の既定値を全て書き込んだテンプレートを生成する（購読者が
    /// 数値を書き換えるだけでバランス調整できるように）。読み込みはレベルロードごとに1回
    /// （EnsureLoadedは冪等）。変更の反映にはセーブのロードし直しが必要。
    ///
    /// スレッド注記: EnsureLoadedはロード経路（WarStateDataExtension.OnLoadData/
    /// MilitaryManagerの状態初期化）から、ロスター構築より前に呼ぶこと。
    /// </summary>
    internal static class UnitStatsFile
    {
        private const string FileName = "unit-stats.xml";

        private static bool _loadAttempted;

        /// <summary>冪等。ロスター構築（RegisterAll）より前に呼ぶ。</summary>
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
                    return; // テンプレート＝既定値なので読み込む必要は無い
                }

                Load(path);
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitStatsFile.EnsureLoaded error (using built-in stats): " + e);
            }
        }

        /// <summary>レベルアンロード時。次のロードで再読込させる（プレイ中のファイル編集を
        /// 次セッションで拾えるように）。上書き自体はロスター構築時に固定化済みのため、
        /// ここでUnitStatOverridesをクリアしても進行中の状態には影響しない。</summary>
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
                    AmmoCombatHours = ParseAttr(el, "ammoCombatHours") // Task99: 連続射撃可能時間（0=弾薬無限）
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

        /// <summary>現在のMOD既定値（Tier1基準）を全て書き込んだテンプレートを生成する。</summary>
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
            // SpeedはCore内部表現から作者向けのkm/hへ逆変換して書く（読み込み時にまた順変換される）。
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
