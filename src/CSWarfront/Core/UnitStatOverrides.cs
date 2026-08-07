using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>One category's base-stat overrides (tier-1 reference values; TierScaling applies to
    /// overridden values as usual). Null fields mean "no override = keep the roster default".</summary>
    public struct UnitStatOverride
    {
        public float? Hp, Attack, Range, Armor, SpeedKmh, Splash, Cost, BuildTime, Accuracy, FireIntervalHours;
        public float? AmmoCombatHours; // Task99: the ammo gauge's continuous-fire duration (0 = infinite ammo)
    }

    /// <summary>
    /// The home for external overrides of UnitType base stats (Task92, user request "move UnitType
    /// definitions out to XML/JSON so users can tweak balance after the Workshop release", design
    /// §4.3).
    ///
    /// The Game layer's UnitStatsFile reads unit-stats.xml from the MOD folder and Sets values here
    /// before roster construction. Each roster (LandUnitRoster/NavalUnitRoster/AirUnitRoster)
    /// resolves its base stats through this class at Build time. With no file or no entry, the
    /// roster's hardcoded defaults are used unchanged (= fully backward compatible).
    ///
    /// No UnityEngine dependency, no file IO (reading is the Game layer's responsibility). Holds
    /// static state, so tests must Clear() after use.
    /// </summary>
    public static class UnitStatOverrides
    {
        private static readonly Dictionary<UnitCategory, UnitStatOverride> _map =
            new Dictionary<UnitCategory, UnitStatOverride>();

        public static void Set(UnitCategory category, UnitStatOverride o) { _map[category] = o; }
        public static void Clear() { _map.Clear(); }
        public static int Count { get { return _map.Count; } }

        public static float Hp(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Hp.HasValue ? o.Hp.Value : def; }
        public static float Attack(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Attack.HasValue ? o.Attack.Value : def; }
        public static float Range(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Range.HasValue ? o.Range.Value : def; }
        public static float Armor(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Armor.HasValue ? o.Armor.Value : def; }
        public static float SpeedKmh(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.SpeedKmh.HasValue ? o.SpeedKmh.Value : def; }
        public static float Splash(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Splash.HasValue ? o.Splash.Value : def; }
        public static float Cost(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Cost.HasValue ? o.Cost.Value : def; }
        public static float BuildTime(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.BuildTime.HasValue ? o.BuildTime.Value : def; }
        public static float Accuracy(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.Accuracy.HasValue ? o.Accuracy.Value : def; }
        public static float FireInterval(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.FireIntervalHours.HasValue ? o.FireIntervalHours.Value : def; }
        public static float AmmoHours(UnitCategory c, float def) { UnitStatOverride o; return _map.TryGetValue(c, out o) && o.AmmoCombatHours.HasValue ? o.AmmoCombatHours.Value : def; }
    }
}
