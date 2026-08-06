namespace CSWarfront.Core
{
    /// <summary>The same "return an enum that follows the check order" style as
    /// ManualProduction.TryEnqueue/TryCancelLast's QueueResult, applied to ordering ballistic-missile
    /// stock (Task63). QueueResult itself is not reused because the unit-production-specific values
    /// (UnknownType/TierLocked/WrongDomain etc.) never apply to ballistic missiles — mixing in
    /// meaningless values would invite misreads in callers' branching.</summary>
    public enum MissileBuildResult
    {
        Ok,
        BaseNotFound,
        NotMissileBase,
        NoOwner,
        /// <summary>The base is already building one (MissileBuildProgress &gt; 0). Multiple simultaneous
        /// builds are impossible (MilitaryBase holds a single progress field rather than a queue —
        /// Task63's deliberate simplification).</summary>
        AlreadyBuilding,
        /// <summary>StockpiledMissiles has already reached MaxStockpile.</summary>
        StockpileFull,
        NotAffordable
    }

    /// <summary>
    /// Ballistic-missile stockpile production (Task63). BaseType.MissileBase only. A separate mechanism
    /// from normal unit production (ProductionOrder/ProductionStep, MilitaryBase.Queue): missiles have no
    /// UnitType (they never take the field as UnitInstances), so instead of reusing
    /// Queue&lt;ProductionOrder&gt; everything lives in the two fields
    /// MilitaryBase.StockpiledMissiles/MissileBuildProgress (a simple one-build-at-a-time model).
    ///
    /// No UnityEngine dependency. Deterministic (no RNG).
    /// </summary>
    public static class MissileStockpile
    {
        /// <summary>Build cost of one missile (war funds).</summary>
        public const float MissileCost = 250f;

        /// <summary>In-game time one build takes.</summary>
        public const float MissileBuildHours = 24f;

        /// <summary>Stockpile cap per base.</summary>
        public const int MaxStockpile = 5;

        /// <summary>Tiny positive value written into MissileBuildProgress the moment a build starts.
        /// Purely a marker distinguishing it from 0f (not building); the first dt increment in Advance
        /// overwrites it naturally (see the XML comment on MilitaryBase.MissileBuildProgress).</summary>
        private const float StartProgress = 0.0001f;

        /// <summary>Whether the base is currently building a missile (MissileBuildProgress &gt; 0).</summary>
        public static bool IsBuilding(MilitaryBase b) { return b.MissileBuildProgress > 0f; }

        /// <summary>
        /// Starts building one missile at the base with baseId (for the player's manual orders / the Game
        /// layer's UI, Task63). The AI's automatic starts (the MissileBase branch inside
        /// ProductionPlanning.Advance) reuse this same implementation. Check order (the first failure
        /// wins):
        ///  1. does the base with baseId exist -&gt; BaseNotFound
        ///  2. is b.Type BaseType.MissileBase -&gt; NotMissileBase
        ///  3. does it have an owner (including Faction resolution) -&gt; NoOwner
        ///  4. not already building (IsBuilding) -&gt; AlreadyBuilding
        ///  5. StockpiledMissiles &lt; MaxStockpile -&gt; StockpileFull
        ///  6. can the owner pay MissileCost (Faction.TrySpend; deducted only on success) -&gt; NotAffordable
        /// If all pass, MissileBuildProgress is set to StartProgress and Ok is returned.
        /// </summary>
        public static MissileBuildResult TryBuildMissile(WarState state, ushort baseId)
        {
            MilitaryBase b = FindBase(state, baseId);
            if (b == null) return MissileBuildResult.BaseNotFound;
            if (b.Type != BaseType.MissileBase) return MissileBuildResult.NotMissileBase;
            if (b.OwnerFactionId == null) return MissileBuildResult.NoOwner;

            Faction owner = state.FindFaction(b.OwnerFactionId.Value);
            if (owner == null) return MissileBuildResult.NoOwner; // defense against inconsistent state

            if (IsBuilding(b)) return MissileBuildResult.AlreadyBuilding;
            if (b.StockpiledMissiles >= MaxStockpile) return MissileBuildResult.StockpileFull;
            if (!owner.TrySpend(MissileCost)) return MissileBuildResult.NotAffordable;

            b.MissileBuildProgress = StartProgress;
            return MissileBuildResult.Ok;
        }

        /// <summary>
        /// Advances build progress (sim thread; called every tick via MilitaryManager.OnSimTick).
        /// Only bases currently building (IsBuilding) are touched: progress grows by dt/MissileBuildHours,
        /// and on reaching 1.0 StockpiledMissiles is incremented (clamped defensively at MaxStockpile)
        /// and the progress resets to 0. Ownership is not checked (a build already paid for completes
        /// even if the base lost its owner to demolition etc.).
        /// </summary>
        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.Type != BaseType.MissileBase) continue;
                if (!IsBuilding(b)) continue;

                b.MissileBuildProgress += dt / MissileBuildHours;
                if (b.MissileBuildProgress >= 1f)
                {
                    if (b.StockpiledMissiles < MaxStockpile) b.StockpiledMissiles++;
                    b.MissileBuildProgress = 0f;
                }
            }
        }

        private static MilitaryBase FindBase(WarState state, ushort baseId)
        {
            for (int i = 0; i < state.Bases.Count; i++)
                if (state.Bases[i].BaseId == baseId) return state.Bases[i];
            return null;
        }
    }
}
