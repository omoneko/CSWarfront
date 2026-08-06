namespace CSWarfront.Core
{
    /// <summary>The visual kind of a shot (Task42). The Game layer picks from this whether to draw a
    /// tracer (gunfire), direct fire (tank), indirect fire (artillery's ballistic arc) or a SAM (Task90,
    /// the interceptor-missile projectile).</summary>
    public enum ShotKind { Gunfire, DirectFire, IndirectFire, SamMissile }

    /// <summary>
    /// A lightweight event representing one "visible shot" (Task42).
    ///
    /// Design premise: damage applies every sim tick (roughly 60 per real second) as a continuous
    /// expected value proportional to elapsed in-game time (dt) — see CombatStep/BaseCombatStep.
    /// Emitting an effect per damage application would flood the screen with muzzle fire, so ShotEvents
    /// are treated as throttled, presentation-only events separate from the damage computation itself.
    /// They have zero influence on the damage logic or numbers.
    ///
    /// Throttled via UnitInstance.FireCooldown (a per-attacker accumulator, no RNG), so at most one
    /// event is queued per attacking unit per its UnitType.FireIntervalHours (preserving the
    /// deterministic-simulation premise).
    ///
    /// Queued into WarState.RecentShots and consumed by the Game layer
    /// (MilitaryManager.OnMainVisualUpdate), which copies inside the lock every frame. Not persisted
    /// (never written by WarStateSerializer).
    /// </summary>
    public struct ShotEvent
    {
        public WorldPos From;
        public WorldPos To;
        public ShotKind Kind;
        public byte FactionId;

        /// <summary>UnitInstance.InstanceId of the firing unit (Task43). The Game layer uses it to
        /// resolve the From-side muzzle height (model center). The value 0 is never used
        /// (UnitInstance.InstanceId is allocated from 1 by WarState.AllocInstanceId).</summary>
        public uint AttackerId;

        /// <summary>UnitInstance.InstanceId of the impact target (Task43). In unit-vs-unit combat
        /// (CombatStep) this is the target unit's InstanceId; in base sieges (BaseCombatStep) it is 0,
        /// since bases have no logical unit id. The Game layer treats TargetId==0 as "a base (or an
        /// unknown target)" and uses the taller default impact height for buildings.</summary>
        public uint TargetId;

        /// <summary>The firing unit's category (Task51, per-class firing sounds). The Game layer
        /// (WarfrontSounds.ShotSoundFor) picks rifle/heavy-MG/cannon/SAM audio from this. Never used for
        /// damage or hit computation (like ShotKind, purely visual/audio side data).</summary>
        public UnitCategory Category;

        /// <summary>Task90: whether this round missed (used only by the discrete AA fire; always false
        /// otherwise). The Game layer uses it to trigger the "SAM veering off and self-destructing" and
        /// the target's flare/evasive-maneuver displays. Damage is already settled on the core side
        /// (a Missed=true shot dealt none).</summary>
        public bool Missed;

        public ShotEvent(WorldPos from, WorldPos to, ShotKind kind, byte factionId, uint attackerId, uint targetId,
            UnitCategory category)
            : this(from, to, kind, factionId, attackerId, targetId, category, false)
        {
        }

        public ShotEvent(WorldPos from, WorldPos to, ShotKind kind, byte factionId, uint attackerId, uint targetId,
            UnitCategory category, bool missed)
        {
            From = from; To = to; Kind = kind; FactionId = factionId;
            AttackerId = attackerId; TargetId = targetId; Category = category;
            Missed = missed;
        }
    }
}
