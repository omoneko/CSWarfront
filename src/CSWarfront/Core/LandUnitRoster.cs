using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// The seven land unit classes (Tank/Apc/MechInfantry/Artillery/DroneInfantry/Infantry/AntiAir) ×
    /// tiers 1–5 — 35 UnitType definitions in total (Task28). Sea/Air rosters move differently and are
    /// defined separately (out of scope for this task).
    ///
    /// Only each category's tier-1 base stats are kept here; tier 2 and above derive mechanically via
    /// TierScaling (writing out per-tier values invites drift/contradiction during future tuning, so the
    /// single source of truth is this table + TierScaling).
    ///
    /// The key format is fixed as "&lt;Category&gt;_T&lt;tier&gt;" (e.g. "Tank_T3"), using
    /// UnitCategory.ToString()'s spelling directly so as not to collide with the "Tank_T1" referenced by
    /// existing saves/tests.
    /// </summary>
    public static class LandUnitRoster
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

        // Tier-1 base stats (design table, task-28 values; Accuracy column added in Task38; muzzle-effect
        // interval (FireIntervalHours) and kind (ShotKind) columns added in Task42).
        // Splash is exempt from TierScaling (does not grow with tier). FireIntervalHours likewise
        // (firing cadence does not visibly change with tier — a deliberate simplicity choice, Task42 spec).
        //
        // Task38: Artillery nerfed from range 160→120 and attack 55→50, with a low 0.35 accuracy —
        //   "devastating when it hits, but it usually doesn't" artillery (its effective damage stays below
        //   the other classes until DroneInfantry observation support kicks in).
        //
        // Task42: muzzle-effect interval table (design values).
        //   Infantry/MechInfantry/Apc/DroneInfantry/AntiAir = Gunfire (rifle tracers)
        //   Tank                                            = DirectFire (direct tank gun)
        //   Artillery                                       = IndirectFire (lobbed ballistic arc)
        //
        // Task43: intervals lengthened across the board per user feedback (Gunfire intervals are the gap
        //   between 3-round bursts; DirectFire/IndirectFire are single-shot intervals. Game/CombatFx
        //   expands the bursts).
        //   Infantry     0.08 -> 0.40
        //   MechInfantry 0.08 -> 0.40
        //   Apc          0.10 -> 0.45
        //   DroneInfantry 0.12 -> 0.50
        //   AntiAir      0.10 -> 0.45
        //   Tank         0.25 -> 0.90
        //   Artillery    0.60 -> 2.00
        // Task43: Infantry movement speed ×1.5 (5 km/h -> 7.5 km/h; off the citizen walking-speed baseline
        //   toward a jog). Other categories' speeds were out of scope and stay put.
        // Task91 (user request "make the attack-power-to-rate-of-fire ratios realistic"): intervals and
        // attack recalibrated to the realistic relation "small arms = fast and weak ⇔ guns/missiles = slow
        // and heavy". Per-shot damage feel (Attack×FireIntervalHours): infantry 5.4 < APC 9.8 < drone 18 <
        // tank 50 < artillery 137 (heaviest, slowest). DPS (Attack) itself moved within ±20% so the
        // existing balance (threat HP etc.) is not upended.
        //   Infantry     20/0.40 -> 18/0.30 (rifle bursts, lightest hit)
        //   MechInfantry 26/0.40 -> 24/0.30
        //   Apc          22/0.45 -> 28/0.35 (autocannon, heavier than rifles)
        //   Tank         40/0.90 -> 42/1.20 (main gun: heavy blow, slow reload)
        //   Artillery    50/2.00 -> 55/2.50 (howitzer salvos: heaviest, slowest)
        //   DroneInfantry 30/0.50 -> 30/0.60
        //   AntiAir      15/0.45 -> 20/0.60 (one SAM hits hard, fired discretely; the per-shot damage of
        //                the discrete AA model (AntiAirCombat) = Attack×Interval×matchup is what matters)
        private static readonly BaseStats[] Bases =
        {
            new BaseStats(UnitCategory.Infantry,      60f, 18f,  40f,  1f,  7.5f,0f, 20f, 4f, 0.75f, 0.30f, ShotKind.Gunfire),
            new BaseStats(UnitCategory.MechInfantry,   90f, 24f,  45f,  4f, 35f,  0f, 40f, 6f, 0.75f, 0.30f, ShotKind.Gunfire),
            new BaseStats(UnitCategory.Apc,           110f, 28f,  50f,  6f, 45f,  0f, 45f, 6f, 0.70f, 0.35f, ShotKind.Gunfire),
            new BaseStats(UnitCategory.Tank,          140f, 42f,  75f, 10f, 40f,  0f, 60f, 8f, 0.70f, 1.20f, ShotKind.DirectFire),
            new BaseStats(UnitCategory.Artillery,      70f, 55f, 145f,  2f, 25f, 30f, 70f, 9f, 0.35f, 2.50f, ShotKind.IndirectFire),
            new BaseStats(UnitCategory.DroneInfantry,  50f, 30f,  90f,  1f, 20f,  0f, 55f, 7f, 0.85f, 0.60f, ShotKind.Gunfire),
            new BaseStats(UnitCategory.AntiAir,        80f, 20f, 120f,  3f, 30f,  0f, 50f, 7f, 0.60f, 0.60f, ShotKind.Gunfire),
            // Task99: supply truck (unarmed, low HP, road speed. Attack/Range/Accuracy=0; never enters the
            // firing pipeline — CanTargetDomains=None plus the TargetingRules exclusions. SupplyTruckStep
            // handles loading/delivery/transfer).
            new BaseStats(UnitCategory.SupplyTruck,    40f,  0f,   0f,  1f, 45f,  0f, 30f, 3f, 0f,    1f,    ShotKind.Gunfire),
            // Task101: military freight train (unarmed, high HP, rail-only movement. Operated by TrainStep;
            // never enters the production queue).
            new BaseStats(UnitCategory.MilitaryTrain, 500f,  0f,   0f,  5f, 160f, 0f, 150f, 6f, 0f,   1f,    ShotKind.Gunfire),
        };

        /// <summary>Assembles a "&lt;Category&gt;_T&lt;tier&gt;"-format key (e.g. Tank, 3 -&gt; "Tank_T3").</summary>
        public static string TypeKey(UnitCategory category, byte tier)
        {
            return category + "_T" + tier;
        }

        /// <summary>Yields the 45 UnitTypes: 9 categories × tiers 1–5.</summary>
        public static IEnumerable<UnitType> All()
        {
            for (int i = 0; i < Bases.Length; i++)
            {
                for (byte tier = 1; tier <= 5; tier++)
                    yield return Build(Bases[i], tier);
            }
        }

        /// <summary>Builds a single entry for the given category and tier. Null when the category is not
        /// in the roster. Also used by MvpUnitTypes' backward-compatibility wrapper.</summary>
        public static UnitType Get(UnitCategory category, byte tier)
        {
            for (int i = 0; i < Bases.Length; i++)
                if (Bases[i].Category == category) return Build(Bases[i], tier);
            return null;
        }

        /// <summary>Registers all 35 entries from All().</summary>
        public static void RegisterAll(UnitTypeRegistry registry)
        {
            foreach (var t in All()) registry.Register(t);
        }

        private static UnitType Build(BaseStats b, byte tier)
        {
            // Task61: land units can, as a rule, target only Land. The one exception is AntiAir — the only
            // land class whose specialty is the air war (Land|Air). Every other land class never targets
            // aircraft thanks to the TargetSearch/CombatStep domain filter (they are not merely at a
            // numeric matchup disadvantage — they never even become engagement candidates).
            DomainMask canTarget = b.Category == UnitCategory.AntiAir
                ? DomainMask.Land | DomainMask.Air
                : DomainMask.Land;
            if (b.Category == UnitCategory.SupplyTruck ||
                b.Category == UnitCategory.MilitaryTrain) canTarget = DomainMask.None; // Task99/101: unarmed

            // Task92: base values can be overridden via UnitStatOverrides (supplied by the Game layer from
            // unit-stats.xml). Without an override the roster's hard-coded value stands. TierScaling
            // applies to the post-override value as usual.
            return new UnitType(
                TypeKey(b.Category, tier), Domain.Land, b.Category, tier,
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
                canTarget,
                UnitStatOverrides.AmmoHours(b.Category, AmmoRules.DefaultCombatHours(b.Category))); // Task99
        }
    }
}
