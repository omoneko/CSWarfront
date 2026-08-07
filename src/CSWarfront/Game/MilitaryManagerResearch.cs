using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// Additional MilitaryManager members for player-driven research investment and Tier unlocking
    /// (Task35). Following the same policy as MilitaryManagerManualProduction.cs, split into a
    /// partial class because of the 500-line limit on MilitaryManager.cs. _stateLock / State are
    /// private static members declared on the MilitaryManager.cs side; since this is a partial
    /// class they can be accessed directly from here as well.
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>
        /// Player-driven investment of funds into research points, called from the base info panel
        /// (Task35). Thin wrapper that just delegates to Core.Research.TryInvest inside _stateLock.
        /// </summary>
        /// <returns>false if State is uninitialized, factionId is unknown, or funds are insufficient.</returns>
        public static bool TryInvestResearch(byte factionId, float amount)
        {
            lock (_stateLock)
            {
                if (State == null) return false;
                Faction f = State.FindFaction(factionId);
                if (f == null) return false;
                return Research.TryInvest(f, amount);
            }
        }

        /// <summary>
        /// Player-driven unlock of the next Tier, called from the base info panel (Task35).
        /// Thin wrapper that just delegates to Core.Research.TryUnlockNext inside _stateLock.
        /// </summary>
        /// <returns>false if State is uninitialized, factionId is unknown, research points are
        /// insufficient, or the maximum Tier is already reached.</returns>
        public static bool TryUnlockNextTier(byte factionId)
        {
            lock (_stateLock)
            {
                if (State == null) return false;
                Faction f = State.FindFaction(factionId);
                if (f == null) return false;
                return Research.TryUnlockNext(f);
            }
        }
    }
}
