using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Read-only snapshot for the base info panel (Game/UI/BaseInfoPanel) (Task25).
    /// So that the UI never touches WarState / MilitaryBase directly, MilitaryManager.TryGetBaseSnapshot
    /// copies the values inside _stateLock and hands them over. Split out of MilitaryManager.cs because
    /// of the 500-line limit (Task30).
    /// </summary>
    public struct BaseUiSnapshot
    {
        public byte? OwnerFactionId;
        public float CurrentHP;
        public float MaxHP;
        public float CaptureGraceHours;
        public int QueueCount;
        public bool IsHeadquarters;

        /// <summary>The head production order (empty string if the queue is empty). Used to show in the UI what is being built (Task30).</summary>
        public string ProducingTypeKey;
        /// <summary>Progress of the head order (0..1). 0 if the queue is empty.</summary>
        public float ProducingProgress;
        /// <summary>Build time of the head order (in-game time). 0 if the queue is empty.</summary>
        public float ProducingBuildTime;
        /// <summary>War treasury of the owning faction. 0 if unowned.</summary>
        public float OwnerTreasury;
        /// <summary>Number of living units the owning faction currently has. 0 if unowned.</summary>
        public int OwnerUnitCount;

        /// <summary>If true, the AI auto-refills this base's queue (Task34, a copy of MilitaryBase.AutoProduce).</summary>
        public bool AutoProduce;

        /// <summary>If true, the AI (MissileDoctrine) auto-launches ballistic missiles from this base
        /// (Task90, a copy of MilitaryBase.AutoLaunchMissiles. Unused for anything other than MissileBase).</summary>
        public bool AutoLaunchMissiles;

        /// <summary>Display copy of the current queue contents as an array of TypeKeys only (Task34).
        /// index 0 == in production (same content as ProducingTypeKey). Built only for the single
        /// selected base, so this is not a per-tick hot path (only when TryGetBaseSnapshot is called).</summary>
        public string[] QueuedTypeKeys;

        /// <summary>Income actually credited from this base in the most recent economy tick (Task35;
        /// a copy of MilitaryBase.LastIncome). Always filled so it can be shown as 0 even when unowned.</summary>
        public float LastIncome;

        /// <summary>Research points of the owning faction (Task35). 0 if unowned.</summary>
        public float OwnerResearchPoints;

        /// <summary>Highest production Tier the owning faction has unlocked (1..5, Task35). 1 (the Faction default) if unowned.</summary>
        public byte OwnerUnlockedTier;

        /// <summary>Research points required to unlock the next Tier (Research.CostToUnlock(OwnerUnlockedTier+1), Task35).
        /// 0 if already at the maximum Tier (5) or unowned.</summary>
        public float OwnerNextTierCost;

        /// <summary>This base's type (Army/Navy/AirForce/MissileBase, Task61). Used by BaseInfoPanel
        /// for labels like "Army base / Navy base / Air base".</summary>
        public BaseType Type;

        /// <summary>Domains this base can produce for (a copy of MilitaryBase.SpawnableDomains, Task61).
        /// Used by BaseInfoPanel for labels like "Can produce: Land".</summary>
        public DomainMask SpawnableDomains;

        /// <summary>Number of completed ballistic missiles in stock (Task63, a copy of MilitaryBase.StockpiledMissiles).
        /// Always 0 for anything other than BaseType.MissileBase.</summary>
        public int StockpiledMissiles;

        /// <summary>Progress of the missile currently under construction (0..1, Task63, a copy of MilitaryBase.MissileBuildProgress).</summary>
        public float MissileBuildProgress;

        /// <summary>Whether a missile is under construction (a copy of MissileStockpile.IsBuilding, Task63).</summary>
        public bool IsBuildingMissile;

        // --- Task99: three-resource economy + supply goods (copies of the owning faction's pools) ---
        public float OwnerManpower;
        public float OwnerProduction;
        public float OwnerSupplyStock;

        // --- Task101: field fortification (copies of this base's own stockpile / fort ammo / rail connection) ---
        public float StoredSupplies;
        public float FortAmmo;
        public bool RailConnected;
    }

    /// <summary>
    /// Assembly logic for BaseUiSnapshot (Task30). Intended to be called from inside _stateLock in
    /// MilitaryManager.TryGetBaseSnapshot — the caller must hold the lock (this class itself does not lock).
    /// Split out because of MilitaryManager.cs's 500-line limit.
    /// </summary>
    internal static class BaseUiSnapshotBuilder
    {
        public static BaseUiSnapshot Build(MilitaryBase mb, WarState state)
        {
            string producingTypeKey = "";
            float producingProgress = 0f;
            float producingBuildTime = 0f;
            if (mb.Queue.Count > 0)
            {
                ProductionOrder head = mb.Queue[0];
                producingTypeKey = head.TypeKey;
                producingProgress = head.Progress;
                producingBuildTime = head.BuildTime;
            }

            float ownerTreasury = 0f;
            int ownerUnitCount = 0;
            float ownerResearchPoints = 0f;
            byte ownerUnlockedTier = 1;
            float ownerNextTierCost = 0f;
            float ownerManpower = 0f, ownerProduction = 0f, ownerSupplyStock = 0f; // Task99
            if (mb.OwnerFactionId.HasValue)
            {
                byte owner = mb.OwnerFactionId.Value;
                Faction f = state.FindFaction(owner);
                if (f != null)
                {
                    ownerTreasury = f.Treasury;
                    ownerManpower = f.Manpower;         // Task99: three resources + supply goods
                    ownerProduction = f.Production;
                    ownerSupplyStock = f.SupplyStock;
                    // Task35: copy research points, unlocked Tier, and the cost to the next Tier for UI display.
                    ownerResearchPoints = f.ResearchPoints;
                    ownerUnlockedTier = f.UnlockedTier;
                    ownerNextTierCost = f.UnlockedTier < 5
                        ? Research.CostToUnlock((byte)(f.UnlockedTier + 1))
                        : 0f;
                }

                for (int u = 0; u < state.Units.Count; u++)
                {
                    UnitInstance unit = state.Units[u];
                    if (unit.FactionId == owner && unit.State != UnitState.Dead) ownerUnitCount++;
                }
            }

            // Task34: copy only the queue's TypeKeys for UI display, for the single selected base only.
            var queuedTypeKeys = new string[mb.Queue.Count];
            for (int q = 0; q < mb.Queue.Count; q++) queuedTypeKeys[q] = mb.Queue[q].TypeKey;

            return new BaseUiSnapshot
            {
                OwnerFactionId = mb.OwnerFactionId,
                CurrentHP = mb.CurrentHP,
                MaxHP = mb.MaxHP,
                CaptureGraceHours = mb.CaptureGraceHours,
                QueueCount = mb.Queue.Count,
                IsHeadquarters = mb.IsHeadquarters,
                ProducingTypeKey = producingTypeKey,
                ProducingProgress = producingProgress,
                ProducingBuildTime = producingBuildTime,
                OwnerTreasury = ownerTreasury,
                OwnerManpower = ownerManpower,       // Task99
                OwnerProduction = ownerProduction,
                OwnerSupplyStock = ownerSupplyStock,
                StoredSupplies = mb.StoredSupplies,  // Task101
                FortAmmo = mb.FortAmmo,
                RailConnected = mb.RailConnected,
                OwnerUnitCount = ownerUnitCount,
                AutoProduce = mb.AutoProduce,
                AutoLaunchMissiles = mb.AutoLaunchMissiles,
                QueuedTypeKeys = queuedTypeKeys,
                LastIncome = mb.LastIncome,
                OwnerResearchPoints = ownerResearchPoints,
                OwnerUnlockedTier = ownerUnlockedTier,
                OwnerNextTierCost = ownerNextTierCost,
                Type = mb.Type,
                SpawnableDomains = mb.SpawnableDomains,
                StockpiledMissiles = mb.StockpiledMissiles,
                MissileBuildProgress = mb.MissileBuildProgress,
                IsBuildingMissile = MissileStockpile.IsBuilding(mb)
            };
        }
    }
}
