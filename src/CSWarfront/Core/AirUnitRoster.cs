using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// The three air unit classes (AirSuperiority = fighter / TacticalBomber = bomber / SuicideDrone) ×
    /// tiers 1–5 — 15 UnitType definitions (Task61). Same shape as LandUnitRoster/NavalUnitRoster.
    ///
    /// Per user request the scope is narrowed: the other air categories present in UnitCategory
    /// (GroundAttack/StrategicBomber/ElectronicWarfare/Awacs) are not defined in this roster and remain
    /// unimplemented (never registered with UnitTypeRegistry, hence unselectable).
    ///
    /// Tier-1 base stats (design table, task-61 values):
    ///   AirSuperiority (fighter): HP120 Attack85  Range90 Armor4 Speed900km/h Splash0  Accuracy0.80
    ///                           Cost200 BuildTime14 Gunfire/0.30h
    ///   TacticalBomber (bomber): HP180 Attack120 Range70 Armor6 Speed650km/h Splash45 Accuracy0.65
    ///                           Cost260 BuildTime18 DirectFire/1.0h
    ///   SuicideDrone:           HP40 Attack260 Range20 Armor0 Speed420km/h Splash35 Accuracy0.90
    ///                           Cost90 BuildTime6 DirectFire/0.5h, IsOneShot=true (see below)
    ///
    /// Target domains (CanTargetDomains, Task61): all air units were All (Land|Sea|Air) — per the
    /// "Air can hit everything" spec, anything on land, sea or air could become an engagement candidate
    /// (actual effectiveness expressed by CombatMatchup multipliers; e.g. TacticalBomber vs Air at 0.2 is
    /// effectively harmless).
    ///
    /// SuicideDrone (Task79 redesign): no longer "fire then self-destruct" — KamikazeStep (core) owns a
    /// dedicated engagement flow (lock a target in range → dive straight in via MovementStep's dive
    /// movement → apply Attack once in full within DetonateDistance and self-destruct).
    /// CombatStep/BaseCombatStep/ThreatCombatStep check UnitCategoryFlags.IsKamikaze() and continue
    /// early, so of these categories only the SuicideDrone never enters the normal firing pipeline
    /// (ShotEvents / dt-scaled damage). Cost=90 remains "the price of one expendable use" (the old
    /// UnitType.IsOneShot flag has been removed; see KamikazeStep.cs/MovementStep.cs).
    /// </summary>
    public static class AirUnitRoster
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

        // Task91 (realistic rate recalibration): fighters fire cannon bursts frequently (85/0.25h, 21 per
        // shot) versus the bomber's one heavy bomb per pass (125/1.5h, 187 per shot).
        private static readonly BaseStats[] Bases =
        {
            new BaseStats(UnitCategory.AirSuperiority, 120f, 85f,  90f, 4f, 900f,  0f, 200f, 14f, 0.80f, 0.25f, ShotKind.Gunfire),
            new BaseStats(UnitCategory.TacticalBomber,  180f, 125f, 70f, 6f, 650f, 45f, 260f, 18f, 0.65f, 1.50f, ShotKind.DirectFire),
            // SuicideDrone: ShotKind/FireIntervalHours are never read by KamikazeStep (it emits no
            // ShotEvents). The old roster values remain only to keep the same BaseStats shape as the other
            // categories (simply ignored here; harmless).
            new BaseStats(UnitCategory.SuicideDrone,     40f, 260f, 20f, 0f, 420f, 35f,  90f,  6f, 0.90f, 0.50f, ShotKind.DirectFire),
            // Task101: the attack helicopter (ground-only, hovering — no racetrack passes, low altitude
            // 60m) and the transport helicopter (an unarmed logistics craft operated by TransportHeliStep;
            // auto-maintained, never in the production queue).
            new BaseStats(UnitCategory.AttackHelicopter, 90f,  45f, 100f, 3f, 220f,  0f, 220f, 12f, 0.80f, 0.50f, ShotKind.Gunfire),
            new BaseStats(UnitCategory.TransportHelicopter, 60f, 0f,  0f, 1f, 220f,  0f,  80f,  6f, 0f,    1f,    ShotKind.Gunfire),
        };

        /// <summary>Assembles a "&lt;Category&gt;_T&lt;tier&gt;"-format key (same format as
        /// LandUnitRoster.TypeKey).</summary>
        public static string TypeKey(UnitCategory category, byte tier)
        {
            return category + "_T" + tier;
        }

        /// <summary>Yields the 15 UnitTypes: 3 categories × tiers 1–5.</summary>
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

        /// <summary>Registers all 15 entries from All().</summary>
        public static void RegisterAll(UnitTypeRegistry registry)
        {
            foreach (var t in All()) registry.Register(t);
        }

        private static UnitType Build(BaseStats b, byte tier)
        {
            // Task92: base values can be overridden via UnitStatOverrides (unit-stats.xml), as in
            // LandUnitRoster.
            return new UnitType(
                TypeKey(b.Category, tier), Domain.Air, b.Category, tier,
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

        /// <summary>Task85 (retires Task61's "Air can hit everything"): target domains per category.
        ///  - Fighter: air only (dedicated to dogfighting fighters/bombers/suicide drones; cannot shoot at
        ///    land or sea).
        ///  - Bomber: land and sea (Task88: originally land-only, but playtesting showed it "ignoring
        ///    enemy naval units", so anti-ship bombing was allowed. Air remains off-limits).
        ///  - Suicide drone: all domains (still positioned as a ramming weapon; a category outside the
        ///    user's spec, so the traditional All is kept).
        /// Whether bases/KAIJU can be attacked is TargetingRules' concern (on the
        /// BaseCombatStep/ThreatCombatStep side).</summary>
        private static DomainMask TargetDomainsFor(UnitCategory category)
        {
            switch (category)
            {
                case UnitCategory.AirSuperiority: return DomainMask.Air;
                case UnitCategory.TacticalBomber: return DomainMask.Land | DomainMask.Sea;
                case UnitCategory.AttackHelicopter: return DomainMask.Land; // Task101: ground-only (no helis, fixed-wing or ships)
                case UnitCategory.TransportHelicopter: return DomainMask.None; // Task101: unarmed
                default: return DomainMask.All;
            }
        }
    }
}
