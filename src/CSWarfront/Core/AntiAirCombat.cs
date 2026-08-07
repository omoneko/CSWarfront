namespace CSWarfront.Core
{
    /// <summary>
    /// The AntiAir category's rules for fighting aircraft (Task90, user request):
    ///  - It fires its gun at suicide drones and surface-to-air missiles at fighters/bombers.
    ///  - Both carry a per-shot hit chance by tier, so shots can miss.
    ///
    /// Unlike regular categories (the expected-value scheme: Accuracy applied continuously every tick
    /// as a damage multiplier), AA fire at aircraft resolves as discrete single shots every
    /// FireIntervalHours: each shot rolls hit/miss with a deterministic hash (no RNG — derived from
    /// state.TickCounter and the attacker/target IDs), and only a hit deals the lump damage
    /// "Attack × FireIntervalHours × matchup". A miss deals nothing at all; only a ShotEvent with
    /// Missed=true is queued (the Game layer uses it for the "SAM veering off + the target popping
    /// flares and jinking" display).
    ///
    /// Expected DPS is "hit chance × Attack × matchup" — the same shape as the old expected-value
    /// scheme — so the hit-chance table values themselves double as the balance dials.
    /// </summary>
    public static class AntiAirCombat
    {
        /// <summary>The AA gun's per-tier hit chance (vs suicide drones). A fast close-range target,
        /// but bullets arrive instantly, so it lands more reliably than the missiles.
        /// T1=0.70 → T5=0.90.</summary>
        public static float GunHitChance(byte tier)
        {
            float chance = 0.70f + 0.05f * (tier - 1);
            return chance > 0.90f ? 0.90f : chance;
        }

        /// <summary>The SAM's per-tier hit chance (vs fighters/bombers). Lower than the gun because
        /// targets deflect it with flares and evasive maneuvers (the miss display, Game layer).
        /// T1=0.55 → T5=0.83.</summary>
        public static float MissileHitChance(byte tier)
        {
            float chance = 0.55f + 0.07f * (tier - 1);
            return chance > 0.90f ? 0.90f : chance;
        }

        /// <summary>Whether to use a SAM against this air target (false = the gun). Small low-flying
        /// targets like suicide drones get the gun; other aircraft (fighters/bombers) get
        /// missiles.</summary>
        public static bool UsesMissileAgainst(UnitCategory targetCategory)
        {
            return !targetCategory.IsKamikaze();
        }

        /// <summary>The per-shot hit chance for this tier and target.</summary>
        public static float HitChanceFor(byte tier, UnitCategory targetCategory)
        {
            return UsesMissileAgainst(targetCategory) ? MissileHitChance(tier) : GunHitChance(tier);
        }

        /// <summary>One shot's hit roll (deterministic, no RNG). The same prime-multiply composition +
        /// finalizer as BallisticMissiles.HashSeed derives a uniform [0,1) value from (attacker,
        /// target, tick) and compares it to chance.</summary>
        public static bool RollHit(uint attackerId, uint targetId, uint tick, float chance)
        {
            unchecked
            {
                uint h = attackerId;
                h = h * 2654435761u + targetId;
                h = h * 2654435761u + tick;
                // finalizer (fmix32, MurmurHash3)
                h ^= h >> 16;
                h *= 0x85ebca6bu;
                h ^= h >> 13;
                h *= 0xc2b2ae35u;
                h ^= h >> 16;
                float roll = (h & 0xFFFFFF) / (float)0x1000000; // [0,1)
                return roll < chance;
            }
        }
    }
}
