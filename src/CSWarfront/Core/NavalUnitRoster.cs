using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// The two naval classes (Destroyer = missile destroyer / Carrier) × tiers 1–5 — 10 UnitType
    /// definitions (Task61). Exactly the LandUnitRoster shape (tier-1 base-stat table + TierScaling
    /// deriving tiers 2+, key format "&lt;Category&gt;_T&lt;tier&gt;"), differing in Domain=Sea, speeds an
    /// order of magnitude higher, and an entirely different movement model (no land RoadGraph/CoverMap
    /// at all — MovementStep's Sea branch and WarState.IWaterSampler keep them on water only).
    ///
    /// Per user request the scope is narrowed: the other naval categories present in UnitCategory
    /// (Cruiser/Frigate/Minelayer/Minesweeper/Submarine/FastBoat/SuicideBoat/SeaDrone) are not defined
    /// in this roster and remain unimplemented (never registered with UnitTypeRegistry, hence
    /// unselectable).
    ///
    /// Tier-1 base stats (design table, task-61 values):
    ///   Destroyer: HP260 Attack70 Range220 Armor14 Speed55km/h Splash20 Accuracy0.70 Cost180 BuildTime16
    ///              IndirectFire/1.2h (the lobbed representation of ship-to-ship missiles)
    ///   Carrier:   HP520 Attack30 Range120 Armor20 Speed45km/h Splash0  Accuracy0.60 Cost400 BuildTime30
    ///              DirectFire/2.0h (direct fire from deck guns; the carrier itself is a platform of
    ///              endurance rather than striking power)
    ///
    /// Target domains (CanTargetDomains, Task61): ships are Land|Sea (they can attack coastal targets
    /// and other ships but have no anti-air capability). On the CombatMatchup side the Carrier also gets
    /// a low 0.6 multiplier against every category, per the design intent "a platform of survivability
    /// over striking power" (see CombatMatchup.cs).
    /// </summary>
    public static class NavalUnitRoster
    {
        private struct BaseStats
        {
            public readonly UnitCategory Category;
            public readonly float Hp, Attack, Range, Armor, SpeedKmh, Splash, Cost, BuildTime, Accuracy;
            public readonly float FireIntervalHours;
            public readonly ShotKind ShotKind;

            public BaseStats(UnitCategory category, float hp, float attack, float range, float armor,
                float speedKmh, float splash, float cost, float buildTime, float accuracy,
                float fireIntervalHours, ShotKind shotKind)
            {
                Category = category; Hp = hp; Attack = attack; Range = range; Armor = armor;
                SpeedKmh = speedKmh; Splash = splash; Cost = cost; BuildTime = buildTime;
                Accuracy = accuracy;
                FireIntervalHours = fireIntervalHours; ShotKind = shotKind;
            }
        }

        // Task91 (realistic rate recalibration): the destroyer now fires the heavy, infrequent blow
        // befitting "a salvo of ship-to-shore/ship-to-ship missiles" — 160 per shot (80×2.0h), formerly
        // 84/1.2h. The Carrier's numbers are unused since Task85 (a platform specialist that never
        // attacks).
        private static readonly BaseStats[] Bases =
        {
            new BaseStats(UnitCategory.Destroyer, 260f, 80f, 220f, 14f, 55f, 20f, 180f, 16f, 0.70f, 2.0f, ShotKind.IndirectFire),
            new BaseStats(UnitCategory.Carrier,   520f, 30f, 120f, 20f, 45f,  0f, 400f, 30f, 0.60f, 2.0f, ShotKind.DirectFire),
        };

        /// <summary>Assembles a "&lt;Category&gt;_T&lt;tier&gt;"-format key (same format as
        /// LandUnitRoster.TypeKey).</summary>
        public static string TypeKey(UnitCategory category, byte tier)
        {
            return category + "_T" + tier;
        }

        /// <summary>Yields the 10 UnitTypes: 2 categories × tiers 1–5.</summary>
        public static IEnumerable<UnitType> All()
        {
            for (int i = 0; i < Bases.Length; i++)
                for (byte tier = 1; tier <= 5; tier++)
                    yield return Build(Bases[i], tier);
        }

        /// <summary>Builds one entry for the given category and tier. Null for categories not in this
        /// roster.</summary>
        public static UnitType Get(UnitCategory category, byte tier)
        {
            for (int i = 0; i < Bases.Length; i++)
                if (Bases[i].Category == category) return Build(Bases[i], tier);
            return null;
        }

        /// <summary>Registers all 10 entries from All().</summary>
        public static void RegisterAll(UnitTypeRegistry registry)
        {
            foreach (var t in All()) registry.Register(t);
        }

        private static UnitType Build(BaseStats b, byte tier)
        {
            // Task92: base values can be overridden via UnitStatOverrides (unit-stats.xml), as in
            // LandUnitRoster.
            return new UnitType(
                TypeKey(b.Category, tier), Domain.Sea, b.Category, tier,
                TierScaling.Hp(UnitStatOverrides.Hp(b.Category, b.Hp), tier),
                TierScaling.Attack(UnitStatOverrides.Attack(b.Category, b.Attack), tier),
                TierScaling.Range(UnitStatOverrides.Range(b.Category, b.Range), tier),
                TierScaling.Armor(UnitStatOverrides.Armor(b.Category, b.Armor), tier),
                SpeedCalibration.UnitsPerGameHourFromKmh(TierScaling.SpeedKmh(UnitStatOverrides.SpeedKmh(b.Category, b.SpeedKmh), tier)),
                UnitStatOverrides.Splash(b.Category, b.Splash),
                TierScaling.Cost(UnitStatOverrides.Cost(b.Category, b.Cost), tier),
                TierScaling.BuildTime(UnitStatOverrides.BuildTime(b.Category, b.BuildTime), tier),
                "",
                TierScaling.Accuracy(UnitStatOverrides.Accuracy(b.Category, b.Accuracy), tier),
                UnitStatOverrides.FireInterval(b.Category, b.FireIntervalHours),
                b.ShotKind,
                TargetDomainsFor(b.Category),
                UnitStatOverrides.AmmoHours(b.Category, AmmoRules.DefaultCombatHours(b.Category))); // Task99
        }

        /// <summary>Task85: target domains per category.
        ///  - Destroyer: land and sea (as before; no anti-air capability, Task61).
        ///  - Carrier: none (a dedicated flight platform that never attacks; CarrierAirWing handles its
        ///    aircraft).
        /// Whether bases/KAIJU can be attacked is TargetingRules' concern (the carrier is barred from
        /// everything there too).</summary>
        private static DomainMask TargetDomainsFor(UnitCategory category)
        {
            switch (category)
            {
                case UnitCategory.Carrier: return DomainMask.None;
                default: return DomainMask.Land | DomainMask.Sea;
            }
        }
    }
}
