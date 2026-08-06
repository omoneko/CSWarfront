using System.Collections.Generic;
namespace CSWarfront.Core
{
    public partial class MilitaryBase
    {
        public ushort BaseId;
        public BaseType Type;
        public byte? OwnerFactionId;
        public WorldPos Position;
        public float InfluenceRadius = 500f;
        public bool IsHeadquarters;
        public float MaxHP = 500f;
        public float CurrentHP = 500f;
        public List<ProductionOrder> Queue = new List<ProductionOrder>();

        /// <summary>When true, the AI (ProductionPlanning.Advance) automatically refills this base's queue.
        /// When false, only the player can queue orders manually (Task34: first step of command mode).
        /// Defaults to true (preserving the original fully automatic behavior).</summary>
        public bool AutoProduce = true;

        /// <summary>Queue cap for the player's manual orders (Task34). Larger than the AI production cap
        /// ProductionPlanning.QueueCap (2), so the player can batch several orders at once.</summary>
        public const int ManualQueueCap = 5;

        /// <summary>Capture grace of a newly founded base (in-game hours remaining). 0 = no protection.
        /// The normal constructor leaves it at 0 (no impact on existing behavior/tests); the Game layer
        /// sets NewBaseGraceHours when registering a player-placed base.</summary>
        public float CaptureGraceHours;

        /// <summary>Capture-grace period granted to new bases (one in-game day).</summary>
        public const float NewBaseGraceHours = 24f;

        /// <summary>Income actually credited from this base in the latest economy tick (Task35, per
        /// EconomyIntervalHours of in-game time). MilitaryManager.OnSimTick caches
        /// TerritoryIncome.ForBase's result here so the UI never touches the CS buffers/WarState directly.
        /// Runtime-only, not persisted (not added to WarStateSerializer).</summary>
        public float LastIncome;

        /// <summary>Number of completed ballistic missiles in stock (Task63; BaseType.MissileBase only).
        /// MissileStockpile.Advance increments it on production completion; BallisticMissiles.TryLaunch
        /// consumes it on launch. Persisted at the tail of the base block since v6 by WarStateSerializer.
        /// Older saves restore at the default 0 (harmless — no placeable MissileBase prefab existed before
        /// this task).</summary>
        public int StockpiledMissiles;

        /// <summary>Task90 (user request "let production and launch be switched to manual"): when true,
        /// MissileDoctrine auto-launches from this base. When false, launches happen only via the player's
        /// "designate launch point" (MissileLaunchTargeting). The production side's auto/manual is covered
        /// by the existing AutoProduce (read by ProductionPlanning's MissileBase branch). Persisted in v7.</summary>
        public bool AutoLaunchMissiles = true;

        /// <summary>Build progress of the missile currently under construction (0..1, Task63). 0f means
        /// "not building"; MissileStockpile.TryBuildMissile sets a tiny positive value the moment a build
        /// starts to distinguish "building" (see MissileStockpile.IsBuilding). Persisted at the tail of the
        /// base block since v6. Older saves restore at the default 0 (not building).</summary>
        public float MissileBuildProgress;

        /// <summary>Remaining cooldown of AI auto-launch (MissileDoctrine) (in-game hours, Task63).
        /// Runtime-only, not persisted (same policy as UnitInstance.FireCooldown etc.: resetting to 0 on
        /// save/load has no real balance impact).</summary>
        public float MissileLaunchCooldownRemaining;

        public MilitaryBase(ushort baseId, BaseType type, WorldPos pos)
        { BaseId = baseId; Type = type; Position = pos; }

        /// <summary>Which domains of units this base can produce (Task61). A value derived mechanically
        /// from Type, not an independent field (= no WarStateSerializer format change needed; BaseType has
        /// been persisted since v1 and SpawnableDomains is recomputed from it on every load).
        /// Army→Land, Navy→Sea, AirForce→Air. MissileBase (implemented in Task63) produces no units at all
        /// (it only stockpiles missiles; see MissileStockpile), hence None. ManualProduction.TryEnqueue
        /// thereby automatically rejects regular unit orders at a MissileBase as WrongDomain.</summary>
        public DomainMask SpawnableDomains
        {
            get
            {
                switch (Type)
                {
                    case BaseType.Navy: return DomainMask.Sea;
                    case BaseType.AirForce: return DomainMask.Air;
                    case BaseType.MissileBase: return DomainMask.None;
                    case BaseType.Army: return DomainMask.Land;
                    // Task103: cargo stations can manually produce only military trains (Domain.Land).
                    // The category narrowing is FortificationRules.CanProduceUnit's job; the domain mask
                    // allows Land.
                    case BaseType.CargoStation: return DomainMask.Land;
                    default:
                        // Task101: field fortifications (Bunker/ArtilleryPost/SupplyDepot/Trench) produce
                        // no units. default changed to None (the old default was Land, but Army is now
                        // explicit so the four original base types behave unchanged).
                        return DomainMask.None;
                }
            }
        }

        // --- Task101 (Update 3): extra state for field fortifications and cargo stations ---

        /// <summary>Stored supplies (SupplyDepot/CargoStation only; capped by
        /// FortificationRules.StoredSupplyCap; persisted in v10). On capture the field transfers to the new
        /// owner as-is (= stock seizure).</summary>
        public float StoredSupplies;

        /// <summary>Fortification ammo gauge (Bunker/ArtilleryPost only, 0..1, persisted in v10).
        /// FortCombatStep drains it on firing ticks; ResupplyStep refills it inside supply zones.</summary>
        public float FortAmmo = 1f;

        /// <summary>Whether a cargo station is connected to the rail network (CargoStation only, persisted
        /// in v10). Judged by the Game layer's BasePlacementWatcher at placement against rails within 100m.
        /// Unconnected stations are not used for rail transport (TrainStep) — stock and capture still
        /// work.</summary>
        public bool RailConnected;

        /// <summary>Task108: the point on the rails where trains actually call at this station
        /// (CargoStation only; runtime-only, not persisted — CargoStationRules.RefreshConnectivity
        /// re-resolves it every time the rail network is (re)built). It points at the nearest node of the
        /// main network, not the station building itself — if the node right beside the station belongs to
        /// a siding severed from the main line, no path to the other stations can be laid and trains cannot
        /// move at all. Null while unresolved (rail network not built yet etc.); the station's own position
        /// substitutes meanwhile (CargoStationRules.RailPointOf).</summary>
        public WorldPos? RailEntry;

        /// <summary>Cooldown that throttles the fortifications' muzzle effects (runtime-only, not
        /// persisted; same policy as UnitInstance.FireCooldown).</summary>
        public float FortFireCooldown;
    }
}
