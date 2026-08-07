using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Additional MilitaryManager members for the UI-facing wrappers used by the base/unit info panels
    /// (ownership changes, UI snapshot retrieval). Split into a partial class because of the 500-line
    /// limit on MilitaryManager.cs (same policy as Task34's MilitaryManagerManualProduction, Task49's
    /// MilitaryManagerRelations, etc. The wrappers for faction relations/research/production/missiles/
    /// unit commands are already split into their own dedicated partials, so nothing is duplicated
    /// here — this file holds only base ownership and base/unit UI snapshot retrieval).
    /// _stateLock / State are private static members declared in MilitaryManager.cs; being a partial
    /// class, they are directly accessible from here.
    ///
    /// Callers (the panels under Game/UI) invoke these from the main thread. Each method is a thin
    /// wrapper that holds _stateLock only briefly and never touches Unity APIs (following the standing
    /// convention of not calling Unity APIs while holding the lock).
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>
        /// Changes a base's owning faction (Task25); called from the base info panel
        /// (Game/UI/BaseInfoPanel). Expected to be called from the main thread, but mutual exclusion is
        /// guaranteed because the sim thread (OnSimTick) takes the same _stateLock. HQ consistency
        /// reuses BasePlacementWatcher.ReassignHqIfCleared (shared to avoid duplicating the demolition
        /// path).
        /// </summary>
        /// <returns>false if the base with baseId or the faction with factionId is not found.</returns>
        public static bool TrySetBaseOwner(ushort baseId, byte factionId)
        {
            lock (_stateLock)
            {
                if (State == null) return false;

                MilitaryBase mb = null;
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    if (State.Bases[i].BaseId == baseId) { mb = State.Bases[i]; break; }
                }
                if (mb == null) return false;

                Faction newFaction = State.FindFaction(factionId);
                if (newFaction == null) return false;

                byte? oldOwner = mb.OwnerFactionId;
                if (oldOwner.HasValue && oldOwner.Value == factionId) return true; // no change

                bool wasHq = mb.IsHeadquarters;
                mb.OwnerFactionId = factionId;
                mb.IsHeadquarters = false;

                // If this was the old owner faction's HQ, clear it and promote another base owned by
                // that faction, if any.
                if (oldOwner.HasValue && wasHq)
                {
                    BasePlacementWatcher.ReassignHqIfCleared(State, oldOwner.Value, baseId);
                }

                // If the new owner faction does not yet have an HQ, make this base its HQ.
                if (!newFaction.HomeBaseId.HasValue)
                {
                    newFaction.HomeBaseId = baseId;
                    mb.IsHeadquarters = true;
                }

                ModConfig.Log("MilitaryManager: base " + baseId + " owner changed " +
                    (oldOwner.HasValue ? oldOwner.Value.ToString() : "none") + " -> " + factionId +
                    (mb.IsHeadquarters ? " (new HQ)" : ""));
                return true;
            }
        }

        /// <summary>
        /// Task66: whether the given faction owns at least one base of the given type (used by
        /// AssetAssignPanel/OptionsModelAssignPage to show a hint to the user when applying a
        /// "per-base-type model assignment" while no base of that type currently exists to apply it to.
        /// As discovered during bug investigation, even when the assignment itself is saved correctly,
        /// nothing visibly changes if no matching base exists, so to the user it looks like the
        /// assignment "did not take effect" — this method exists solely to explain that situation
        /// explicitly and has no effect whatsoever on the assignment save/apply logic itself).
        /// </summary>
        public static bool HasOwnedBaseOfType(byte factionId, BaseType type)
        {
            lock (_stateLock)
            {
                if (State == null) return false;
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    MilitaryBase b = State.Bases[i];
                    if (b.Type == type && b.OwnerFactionId.HasValue && b.OwnerFactionId.Value == factionId) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Copies the values for the base info panel inside the lock and returns them (so the UI never
        /// touches WarState directly, Task25).
        /// </summary>
        public static bool TryGetBaseSnapshot(ushort baseId, out BaseUiSnapshot snapshot)
        {
            lock (_stateLock)
            {
                snapshot = default(BaseUiSnapshot);
                if (State == null) return false;

                for (int i = 0; i < State.Bases.Count; i++)
                {
                    MilitaryBase mb = State.Bases[i];
                    if (mb.BaseId != baseId) continue;

                    snapshot = BaseUiSnapshotBuilder.Build(mb, State);
                    return true;
                }
                return false;
            }
        }

        /// <summary>Copies the values for the unit info panel inside the lock and returns them (Task31;
        /// same pattern as TryGetBaseSnapshot). Dead units may still linger, so they are treated as not
        /// found.</summary>
        public static bool TryGetUnitSnapshot(uint instanceId, out UnitUiSnapshot snapshot)
        {
            lock (_stateLock)
            {
                snapshot = default(UnitUiSnapshot);
                if (State == null) return false;

                UnitInstance unit = State.FindUnit(instanceId);
                if (unit == null || unit.State == UnitState.Dead) return false;

                UnitType type = State.Types.Get(unit.TypeKey);
                snapshot = UnitUiSnapshotBuilder.Build(State, unit, type);
                return true;
            }
        }
    }
}
