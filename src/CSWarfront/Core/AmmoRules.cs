namespace CSWarfront.Core
{
    /// <summary>
    /// Task99: the ammunition gauge (design: 2026-08-03-economy-supply-design.md §3).
    ///
    /// Every combat unit carries Ammo (0..1), which drains by dt/AmmoCombatHours only on ticks it fires.
    /// Running dry (Ammo&lt;=0) merely stops firing (movement and capture still work). Recovery comes
    /// from in-zone base auto-resupply (ResupplyStep) and supply trucks (SupplyTruckStep).
    ///
    /// Exceptions (HasAmmo always true): categories with AmmoCombatHours&lt;=0 (the carrier = a platform,
    /// suicide drones = the ram is the weapon, supply trucks).
    ///
    /// The Invader faction (outside incursions): originally had infinite ammo, but playtesting showed
    /// "the invading force is too favored" (user feedback 2026-08-03), so it switched to living off the
    /// land — they get only their spawn-time load (full), and afterwards recover InvaderAmmoPerKill each
    /// time they destroy an enemy unit (RewardInvaderKill, called from CombatStep's kill resolution).
    /// They receive no in-zone resupply and no supply trucks (ResupplyStep/SupplyTruckStep exclude
    /// Invaders). A wave that cannot keep killing goes dry, stops shooting, and the defenders can mop it
    /// up.
    /// </summary>
    public static class AmmoRules
    {
        /// <summary>Default sustained-fire duration per category (in-game hours, tier-independent).
        /// 0 = infinite ammo. Overridable via ammoCombatHours in unit-stats.xml
        /// (UnitStatOverrides.AmmoHours).</summary>
        public static float DefaultCombatHours(UnitCategory category)
        {
            switch (category)
            {
                case UnitCategory.Infantry:
                case UnitCategory.MechInfantry:
                    return 12f;
                case UnitCategory.Tank:
                case UnitCategory.Apc:
                case UnitCategory.DroneInfantry:
                case UnitCategory.Destroyer:
                    return 8f;
                case UnitCategory.AntiAir:
                    return 6f;
                case UnitCategory.Artillery:
                    return 4f;
                case UnitCategory.AirSuperiority:
                case UnitCategory.TacticalBomber:
                case UnitCategory.AttackHelicopter: // Task101: helicopters share the rearm loop
                    return 3f; // ~2-3 sorties (creates the dry → rearm at base/carrier → resortie loop)
                default:
                    return 0f; // Carrier / SuicideDrone / SupplyTruck / unimplemented categories = infinite
            }
        }

        /// <summary>Ammo an Invader unit recovers per enemy kill (living off the land; 0.25 = two hours
        /// of fire in tank terms). See the class comment.</summary>
        public const float InvaderAmmoPerKill = 0.25f;

        /// <summary>Whether the unit can fire. Always true outside the ammo system
        /// (AmmoCombatHours&lt;=0).</summary>
        public static bool HasAmmo(UnitInstance u, UnitType t)
        {
            if (t == null || t.AmmoCombatHours <= 0f) return true;
            return u.Ammo > 0f;
        }

        /// <summary>Ammo drain for a tick the unit fired on. No-op outside the ammo system.</summary>
        public static void ConsumeFire(UnitInstance u, UnitType t, float dt)
        {
            if (t == null || t.AmmoCombatHours <= 0f) return;
            u.Ammo -= dt / t.AmmoCombatHours;
            if (u.Ammo < 0f) u.Ammo = 0f;
        }

        /// <summary>The kill reward (Invaders only): recovers InvaderAmmoPerKill of ammo the moment an
        /// enemy unit dies (capped at 1). Called from CombatStep's "was this damage the killing blow"
        /// check (the same spot as AwardKillReward). Regular factions are excluded (their supply net
        /// provides).</summary>
        public static void RewardInvaderKill(UnitInstance killer, UnitType killerType)
        {
            if (killer == null || killer.FactionId != Faction.InvaderFactionId) return;
            if (killerType == null || killerType.AmmoCombatHours <= 0f) return;
            killer.Ammo += InvaderAmmoPerKill;
            if (killer.Ammo > 1f) killer.Ammo = 1f;
        }
    }
}
