namespace CSWarfront.Core
{
    /// <summary>A data-driven unit definition (one class × one tier). Immutable at runtime.</summary>
    public class UnitType
    {
        public string TypeKey { get; private set; }
        public Domain Domain { get; private set; }
        public UnitCategory Category { get; private set; }
        public byte Tier { get; private set; }
        public float MaxHP { get; private set; }
        public float Attack { get; private set; }
        public float Range { get; private set; }
        public float Armor { get; private set; }
        public float Speed { get; private set; }
        public float SplashRadius { get; private set; }
        public float Cost { get; private set; }
        public float BuildTime { get; private set; }
        public string AssetPrefabName { get; private set; }

        /// <summary>Accuracy (0..1, Task38). An expected-value multiplier that CombatStep/BaseCombatStep
        /// apply to damage — NOT a random hit/miss roll (this project assumes deterministic simulation).
        /// "40% accuracy" means "0.4× damage lands reliably every tick": statistically identical to
        /// random rolls over enough time, while eliminating the unfair "luck" of hit or miss streaks.</summary>
        public float Accuracy { get; private set; }

        /// <summary>Throttling interval of the muzzle effects (in-game hours, Task42). Damage applies
        /// continuously every tick, but the visuals (ShotEvents) appear at most once per this interval
        /// (managed via UnitInstance.FireCooldown). Exempt from TierScaling = the visible firing cadence
        /// does not change with tier (a deliberate simplicity choice).</summary>
        public float FireIntervalHours { get; private set; }

        /// <summary>Kind of muzzle effect (Task42). The Game layer's CombatFx picks gunfire, direct fire
        /// or the ballistic arc based on this.</summary>
        public ShotKind ShotKind { get; private set; }

        /// <summary>The domains this unit may attack (Task61: made anti-air meaningful with the arrival
        /// of sea/air forces). TargetSearch/CombatStep filter candidates by this mask on top of range and
        /// hostility. Land units default to Land only (AntiAir alone gets Land|Air), sea units to
        /// Land|Sea, air units to All (Land|Sea|Air). There is no constructor default — every roster
        /// specifies it explicitly.</summary>
        public DomainMask CanTargetDomains { get; private set; }

        // Task79: the old IsOneShot flag ("self-destructs the moment it attacks once", suicide-drone
        // only) was removed. Suicide drones no longer "attack, then self-destruct" — they never enter
        // the normal firing pipeline (CombatStep/BaseCombatStep/ThreatCombatStep) at all; the dedicated
        // KamikazeStep owns the dive and ramming detonation (see UnitCategoryFlags.IsKamikaze). The
        // three firing steps continue early on type.Category.IsKamikaze(), so no branch reading this
        // flag exists anymore (formerly the if(type.IsOneShot) branches in CombatStep.cs /
        // BaseCombatStep.cs, Task61).

        /// <summary>Task99: the ammo gauge's "hours of continuous fire" (in-game). UnitInstance.Ammo
        /// drains by dt/AmmoCombatHours only while firing (AmmoRules). 0 = infinite ammo (outside the
        /// ammo system: carriers, suicide drones etc.). An optional parameter defaulting to 0, so
        /// existing callers are unchanged.</summary>
        public float AmmoCombatHours { get; private set; }

        public UnitType(string typeKey, Domain domain, UnitCategory category, byte tier,
            float maxHp, float attack, float range, float armor, float speed,
            float splashRadius, float cost, float buildTime, string assetPrefabName,
            float accuracy, float fireIntervalHours, ShotKind shotKind,
            DomainMask canTargetDomains, float ammoCombatHours = 0f)
        {
            TypeKey = typeKey; Domain = domain; Category = category; Tier = tier;
            MaxHP = maxHp; Attack = attack; Range = range; Armor = armor; Speed = speed;
            SplashRadius = splashRadius; Cost = cost; BuildTime = buildTime;
            AssetPrefabName = assetPrefabName ?? "";
            Accuracy = accuracy;
            FireIntervalHours = fireIntervalHours;
            ShotKind = shotKind;
            CanTargetDomains = canTargetDomains;
            AmmoCombatHours = ammoCombatHours;
        }
    }

    /// <summary>
    /// A thin backward-compatibility wrapper (Task28). The substance moved to LandUnitRoster (the seven
    /// land classes × tiers 1–5). The Tank_T1/Infantry_T1 keys and call shapes remain because existing
    /// saves/tests/diagnostics (Game/SpeedCalibrationDiagnostics.cs) keep referencing them, but the
    /// values themselves have LandUnitRoster's tier-1 base-stat table as the source of truth and are not
    /// duplicated here. New code should use LandUnitRoster.RegisterAll / LandUnitRoster.Get directly.
    /// </summary>
    public static class MvpUnitTypes
    {
        public static UnitType Tank_T1() { return LandUnitRoster.Get(UnitCategory.Tank, 1); }
        public static UnitType Infantry_T1() { return LandUnitRoster.Get(UnitCategory.Infantry, 1); }
    }
}
