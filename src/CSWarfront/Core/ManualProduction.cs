namespace CSWarfront.Core
{
    /// <summary>Result of the player's manual order/cancel against a base's production queue (Task34).
    /// TryEnqueue and TryCancelLast share the same enum (both express "can this order/cancel proceed",
    /// which is close enough in meaning). Each member's semantics are defined in the methods' XML
    /// comments.</summary>
    public enum QueueResult
    {
        Ok,
        BaseNotFound,
        UnknownType,
        QueueFull,
        NotAffordable,
        NoOwner,
        /// <summary>The ordered UnitType.Tier exceeds Faction.UnlockedTier (Task35: research not unlocked).</summary>
        TierLocked,
        /// <summary>The ordered UnitType.Domain is not contained in the base's
        /// MilitaryBase.SpawnableDomains (Task61: ordering ships/aircraft at an army base, etc.).</summary>
        WrongDomain
    }

    /// <summary>
    /// Handles the player's manual production (ordering and cancelling) (Task34). Kept in a separate
    /// class from the AI's automatic production (ProductionPlanning); both remain UnityEngine-free,
    /// deterministic and RNG-free.
    /// </summary>
    public static class ManualProduction
    {
        /// <summary>
        /// Enqueues one order into a base's production queue. Check order (the first failure wins):
        ///  1. does the base with baseId exist -> BaseNotFound
        ///  2. does it have an owning faction -> NoOwner
        ///  3. is typeKey registered in state.Types -> UnknownType
        ///  4. can the owning Faction be found -> NoOwner (defense against inconsistent state)
        ///  5. is type.Tier at or below owner.UnlockedTier (Task35) -> TierLocked
        ///  6. is type.Domain contained in b.SpawnableDomains (Task61) -> WrongDomain
        ///  7. is Queue.Count below MilitaryBase.ManualQueueCap -> QueueFull
        ///  8. can the owner pay type.Cost (Faction.TrySpend; deducted only on success) -> NotAffordable
        /// If all pass, ProductionOrder(typeKey, type.Cost, type.BuildTime) is appended to the queue and
        /// Ok is returned. Deterministic, RNG-free.
        /// </summary>
        public static QueueResult TryEnqueue(WarState state, ushort baseId, string typeKey)
        {
            MilitaryBase b = FindBase(state, baseId);
            if (b == null) return QueueResult.BaseNotFound;
            if (b.OwnerFactionId == null) return QueueResult.NoOwner;

            UnitType type = state.Types.Get(typeKey);
            if (type == null) return QueueResult.UnknownType;

            Faction owner = state.FindFaction(b.OwnerFactionId.Value);
            if (owner == null) return QueueResult.NoOwner; // defense against inconsistent state (should not happen)

            if (type.Tier > owner.UnlockedTier) return QueueResult.TierLocked; // Task35: tier not yet unlocked

            // Task61: units of a domain outside the base's SpawnableDomains (Army->Land, Navy->Sea,
            // AirForce->Air) cannot be ordered (e.g. no destroyers or fighters from an army base).
            if (!DomainMaskUtil.Contains(b.SpawnableDomains, type.Domain)) return QueueResult.WrongDomain;

            // Task103: per-category production rules (military trains only at cargo stations; cargo
            // stations produce only trains).
            if (!FortificationRules.CanProduceUnit(b.Type, type.Category)) return QueueResult.WrongDomain;

            if (b.Queue.Count >= MilitaryBase.ManualQueueCap) return QueueResult.QueueFull;

            // Task99: three-resource payment (manpower + production, shortfall substituted by funds).
            // Manual production is the player's explicit action, so no research reserve is held back —
            // the full treasury is the substitution cap.
            if (!UnitCosts.TryPay(owner, type, owner.Treasury)) return QueueResult.NotAffordable;

            b.Queue.Add(new ProductionOrder(type.TypeKey, type.Cost, type.BuildTime));
            return QueueResult.Ok;
        }

        /// <summary>
        /// Cancels the tail of the queue (the most recently placed order). (Spec changed in Task35: the
        /// in-progress order (index 0) is now always cancellable, even when it is the only one. The old
        /// rule "the sole order cannot be cancelled once Progress&gt;0" looked to the player like "the
        /// cancel button silently does nothing" — a bug — and was removed.)
        ///
        /// The refund is partial, Cost * (1f - Progress), not the full amount (Task35): a barely started
        /// order refunds nearly everything; one moments from completion refunds almost nothing. The
        /// result is clamped to [0, order.Cost] (defense so the refund can never go negative or exceed
        /// the cost even if Progress strays outside its theoretical 0..1 range).
        ///
        /// Check order: base exists (BaseNotFound) -> has an owner (NoOwner) -> queue not empty
        /// (QueueFull — reusing TryEnqueue's "won't fit because full" value to mean "nothing cancellable")
        /// -> Ok. Deterministic, RNG-free.
        /// </summary>
        public static QueueResult TryCancelLast(WarState state, ushort baseId)
        {
            MilitaryBase b = FindBase(state, baseId);
            if (b == null) return QueueResult.BaseNotFound;
            if (b.OwnerFactionId == null) return QueueResult.NoOwner;

            if (b.Queue.Count == 0) return QueueResult.QueueFull;

            Faction owner = state.FindFaction(b.OwnerFactionId.Value);
            if (owner == null) return QueueResult.NoOwner; // defense against inconsistent state (should not happen)

            int idx = b.Queue.Count - 1;
            ProductionOrder order = b.Queue[idx];
            float refund = order.Cost * (1f - order.Progress);
            if (refund < 0f) refund = 0f;
            if (refund > order.Cost) refund = order.Cost;

            b.Queue.RemoveAt(idx);
            owner.AddTreasury(refund);
            return QueueResult.Ok;
        }

        private static MilitaryBase FindBase(WarState state, ushort baseId)
        {
            for (int i = 0; i < state.Bases.Count; i++)
                if (state.Bases[i].BaseId == baseId) return state.Bases[i];
            return null;
        }
    }
}
