namespace CSWarfront.Core
{
    /// <summary>
    /// The catalogue of unit classes across every domain. Of these, Task61 (the sea/air expansion)
    /// actually implemented only five: Destroyer/Carrier (sea) and
    /// AirSuperiority/TacticalBomber/SuicideDrone (air) — the scope was narrowed by user request. The
    /// remaining Sea/Air members are unimplemented placeholders for the future (with no definitions in
    /// NavalUnitRoster/AirUnitRoster they never register with UnitTypeRegistry and cannot be selected).
    ///
    /// Persistence note (verified in Task61): this enum itself is never serialized directly by
    /// WarStateSerializer (units persist by their TypeKey string only, and UnitCategory is reached solely
    /// through runtime resolution via UnitTypeRegistry.Get; KillEvent.Category and ShotEvent.Category
    /// carry these values but only ever live in the non-persisted transient buffers
    /// WarState.RecentKills/RecentShots). Adding SuicideDrone here therefore breaks no existing saves.
    /// Still, before similar future additions, re-verify that nothing has started serializing this
    /// enum's int values directly — and always append new members at the tail.
    /// </summary>
    public enum UnitCategory
    {
        Tank, Apc, MechInfantry, Artillery, DroneInfantry, Infantry, AntiAir,
        Carrier, Cruiser, Destroyer, Frigate, Minelayer, Minesweeper, Submarine,
        FastBoat, SuicideBoat, SeaDrone,
        AirSuperiority, GroundAttack, TacticalBomber, StrategicBomber, ElectronicWarfare, Awacs,
        /// <summary>Suicide drone (added in Task61; redesigned in Task79 from "fire then self-destruct"
        /// to "dive in and detonate on impact"). The only category for which
        /// UnitCategoryFlags.IsKamikaze() returns true: it never enters the normal firing pipeline
        /// (CombatStep/BaseCombatStep/ThreatCombatStep) — the dedicated KamikazeStep owns the entire
        /// engagement flow (target lock → dive → ramming detonation). Appended at the tail without
        /// touching the order of existing members (so any persistence that might depend on the enum's
        /// int values never breaks existing values).</summary>
        SuicideDrone,
        /// <summary>Supply truck (Task99: the economy/supply system). Unarmed (Attack 0,
        /// CanTargetDomains=None), never in the normal firing pipeline; the dedicated SupplyTruckStep
        /// handles load → deliver → transfer → return. Exempt from AI advances
        /// (InvasionOrders.AssignAdvance) and the combat force count
        /// (ProductionPlanning.MaxUnitsPerFaction); truck numbers are capped separately by
        /// SupplyTruckStep.MaxTrucksPerFaction. Appended at the tail (enum-value compatibility).</summary>
        SupplyTruck,
        /// <summary>Transport helicopter (Task101): an unarmed auto-maintained logistics unit
        /// (TransportHeliStep). Hauls supplies base → depot and airlifts infantry to the front. The
        /// anti-helicopter rules live in TargetingRules.CanTargetHelicopter.</summary>
        TransportHelicopter,
        /// <summary>Attack helicopter (Task101): a regular air-base production class. Ground-unit
        /// specialist (no bases, no threats), hovering (no racetrack passes, low altitude 60m).</summary>
        AttackHelicopter,
        /// <summary>Military freight train (Task101): unarmed, rail-only movement (TrainStep). Hauls
        /// supplies and land units between cargo stations.</summary>
        MilitaryTrain
    }
}
