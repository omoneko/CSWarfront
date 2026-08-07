namespace CSWarfront.Core
{
    /// <summary>
    /// A lightweight event marking the moment a unit was destroyed (Task51, per-category firing and
    /// kill sounds). Same design philosophy as ShotEvent: a throttled, presentation-only transient
    /// event with zero influence on the damage computation itself.
    ///
    /// Exactly one is queued in CombatStep.Advance's death-resolution loop (at the very spot the unit
    /// transitions to UnitState.Dead). Because State != Dead gates the transition, the same unit can
    /// never queue more than once within a tick (deterministic).
    ///
    /// Queued into WarState.RecentKills and consumed by the Game layer
    /// (MilitaryManager.OnMainVisualUpdate), which copies inside the lock every frame. Not persisted
    /// (never written by WarStateSerializer).
    /// </summary>
    public struct KillEvent
    {
        /// <summary>The destroyed unit's final position (where the kill sound plays).</summary>
        public WorldPos Position;

        /// <summary>The destroyed unit's (victim's) faction ID.</summary>
        public byte FactionId;

        /// <summary>The destroyed unit's (victim's) category (Task53, omitting infantry kill sounds).
        /// The Game layer (CombatFx.SpawnKillSounds) reads this to skip the "vehicle destroyed"
        /// explosion sound for infantry (Infantry/DroneInfantry) kills. Never used for damage or hit
        /// computation (like ShotEvent.Category, purely visual/audio side data).
        /// In the defensive case where the UnitType cannot be found it holds default(UnitCategory)
        /// (Tank=0) — which falls back to playing the kill sound = the safe side.</summary>
        public UnitCategory Category;

        public KillEvent(WorldPos position, byte factionId, UnitCategory category)
        {
            Position = position;
            FactionId = factionId;
            Category = category;
        }
    }
}
