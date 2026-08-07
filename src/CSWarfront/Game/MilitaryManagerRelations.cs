using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Additional MilitaryManager members for faction relations (Task49, the "Faction Relations" group
    /// on the Options screen). Split into a partial class because of the 500-line limit on
    /// MilitaryManager.cs (same policy as Task34's MilitaryManagerManualProduction / Task48's
    /// MilitaryManagerUnitCommands). _stateLock / State are private static members declared in
    /// MilitaryManager.cs; being a partial class, they are directly accessible from here.
    ///
    /// Callers (the Options UI callbacks in Game/Mod.cs) invoke these from the main thread. Each method
    /// is a thin wrapper that holds _stateLock only briefly and delegates to Core.RelationMatrix /
    /// Core.RelationPresets, never touching Unity APIs (following the standing convention of not
    /// calling Unity APIs while holding the lock).
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>
        /// Sets the relation between factions a and b to r (Task49). RelationMatrix.Set is symmetric,
        /// so the mirror side is updated too.
        /// Returns false and does nothing if State is not initialized (e.g. opened from the main menu).
        /// </summary>
        public static bool TrySetRelation(byte a, byte b, Relation r)
        {
            lock (_stateLock)
            {
                if (State == null) return false;

                State.Relations.Set(a, b, r);
                ModConfig.Log("MilitaryManager: relation " + a + " <-> " + b + " set to " + r);
                return true;
            }
        }

        /// <summary>
        /// Gets the current relation between factions a and b (Task49, for the Options UI's initial
        /// display). Returns false if State is not initialized, leaving the out argument at its default
        /// (Neutral).
        /// </summary>
        public static bool TryGetRelation(byte a, byte b, out Relation r)
        {
            lock (_stateLock)
            {
                if (State == null) { r = Relation.Neutral; return false; }

                r = State.Relations.Get(a, b);
                return true;
            }
        }

        /// <summary>
        /// "Reset all to hostile" button (Task49). Delegates to Core.RelationPresets.ApplyAllHostile.
        /// Returns false and does nothing if State is not initialized.
        /// </summary>
        public static bool TryResetRelationsToAllHostile()
        {
            lock (_stateLock)
            {
                if (State == null) return false;

                RelationPresets.ApplyAllHostile(State.Relations, WarfrontSettings.MaxFactions);
                ModConfig.Log("MilitaryManager: all relations reset to Hostile");
                return true;
            }
        }

        /// <summary>
        /// Task59: sets the relation between faction factionId and external threat kind (KAIJU/Alien)
        /// to r.
        /// Returns false and does nothing if State is not initialized (e.g. opened from the main menu).
        /// </summary>
        public static bool TrySetThreatRelation(byte factionId, ThreatKind kind, Relation r)
        {
            lock (_stateLock)
            {
                if (State == null) return false;

                State.ThreatRelations.Set(factionId, kind, r);
                ModConfig.Log("MilitaryManager: threat relation faction " + factionId + " <-> " + kind + " set to " + r);
                return true;
            }
        }

        /// <summary>
        /// Task59: gets the current relation between faction factionId and external threat kind (for
        /// the Options UI's initial display). Returns false if State is not initialized, leaving the
        /// out argument at its default (Hostile, same as ThreatRelations' default).
        /// </summary>
        public static bool TryGetThreatRelation(byte factionId, ThreatKind kind, out Relation r)
        {
            lock (_stateLock)
            {
                if (State == null) { r = Relation.Hostile; return false; }

                r = State.ThreatRelations.Get(factionId, kind);
                return true;
            }
        }
    }
}
