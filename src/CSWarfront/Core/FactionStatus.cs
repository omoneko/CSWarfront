namespace CSWarfront.Core
{
    /// <summary>
    /// Re-derives Eliminated status and HQ consistency (Faction.HomeBaseId) per faction (Task46).
    ///
    /// Previously Occupation.ResolveCaptures merely set Faction.Eliminated=true directly at the moment
    /// the HQ fell, and no path existed to ever clear the flag. So even when the player handed a fresh
    /// base to an eliminated faction, that faction never fought or produced again (a user-reported
    /// bug).
    ///
    /// Refresh re-derives Eliminated every tick from the condition "owns no bases at all", so a
    /// faction that regains a base automatically revives. MilitaryManager.OnSimTick is expected to
    /// call it right after Occupation.ResolveCaptures, inside the same _stateLock.
    /// </summary>
    public static class FactionStatus
    {
        public static void Refresh(WarState state)
        {
            for (int i = 0; i < state.Factions.Count; i++)
            {
                Faction f = state.Factions[i];

                // Task95: the Invader faction (outside incursions only) normally owns zero bases.
                // Flagging it Eliminated here would drop it from AI advances (AssignAdvance) and
                // freeze invasion forces at their spawn point forever (the root cause of the in-game
                // bug), so it is always treated as active.
                if (f.Id == Faction.InvaderFactionId)
                {
                    f.Eliminated = false;
                    continue;
                }

                bool ownsAnyBase = false;
                bool homeStillOwned = false;
                for (int j = 0; j < state.Bases.Count; j++)
                {
                    MilitaryBase b = state.Bases[j];
                    if (!b.OwnerFactionId.HasValue || b.OwnerFactionId.Value != f.Id) continue;
                    ownsAnyBase = true;
                    if (f.HomeBaseId.HasValue && b.BaseId == f.HomeBaseId.Value) homeStillOwned = true;
                }

                f.Eliminated = !ownsAnyBase;

                // Bases are owned but HomeBaseId is invalid (null, or pointing at a base no longer
                // owned): promote the first owned base to HQ.
                if (ownsAnyBase && !homeStillOwned)
                    PromoteFirstOwnedBaseToHq(state, f.Id);
            }
        }

        /// <summary>
        /// Promotes the first base (in state.Bases order) currently owned by factionId to HQ (setting
        /// that base's IsHeadquarters=true and faction.HomeBaseId to its BaseId). Does nothing when no
        /// base is owned.
        ///
        /// The single implementation of the "first owned base becomes the new HQ" rule shared with the
        /// Game layer's Game/BasePlacementWatcher.ReassignHqIfCleared (promotion when the HQ is lost
        /// to demolition or faction reassignment) — Task46 consolidated it into Core to avoid
        /// duplicating the logic, and the Game side calls this.
        /// </summary>
        public static void PromoteFirstOwnedBaseToHq(WarState state, byte factionId)
        {
            Faction f = state.FindFaction(factionId);
            if (f == null) return;

            for (int j = 0; j < state.Bases.Count; j++)
            {
                MilitaryBase b = state.Bases[j];
                if (!b.OwnerFactionId.HasValue || b.OwnerFactionId.Value != factionId) continue;
                b.IsHeadquarters = true;
                f.HomeBaseId = b.BaseId;
                return;
            }
        }
    }
}
