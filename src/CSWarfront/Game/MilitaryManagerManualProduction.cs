using CSWarfront.Core;
namespace CSWarfront.Game
{
    /// <summary>
    /// Additional MilitaryManager members for player-driven manual production (ordering,
    /// cancellation, and auto-production toggling from the base panel) (Task34). Split into a
    /// partial class because of the 500-line limit on MilitaryManager.cs (same policy as the
    /// BaseUiSnapshotBuilder split in Task30).
    /// _stateLock / State are private static members declared on the MilitaryManager.cs side;
    /// since this is a partial class they can be accessed directly from here as well.
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>
        /// Player-driven manual unit order, called from the base info panel (Task34).
        /// Thin wrapper that just delegates to Core.ManualProduction.TryEnqueue inside _stateLock.
        /// Returns BaseNotFound if State is uninitialized (in practice EnsureInitialized is assumed
        /// to have run, but we defensively null-guard so we do not depend on call timing).
        /// </summary>
        public static QueueResult TryQueueUnit(ushort baseId, string typeKey)
        {
            lock (_stateLock)
            {
                if (State == null) return QueueResult.BaseNotFound;
                return ManualProduction.TryEnqueue(State, baseId, typeKey);
            }
        }

        /// <summary>
        /// Player-driven cancellation of a manual order, called from the base info panel (Task34).
        /// Thin wrapper that just delegates to Core.ManualProduction.TryCancelLast inside _stateLock.
        /// </summary>
        public static QueueResult TryCancelLastOrder(ushort baseId)
        {
            lock (_stateLock)
            {
                if (State == null) return QueueResult.BaseNotFound;
                return ManualProduction.TryCancelLast(State, baseId);
            }
        }

        /// <summary>
        /// Toggles a base's AI auto-production ON/OFF, called from the base info panel (Task34).
        /// </summary>
        /// <returns>false if no base with baseId is found.</returns>
        public static bool TrySetAutoProduce(ushort baseId, bool value)
        {
            lock (_stateLock)
            {
                if (State == null) return false;
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    if (State.Bases[i].BaseId != baseId) continue;
                    State.Bases[i].AutoProduce = value;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Toggles a missile base's auto-launch ON/OFF, called from the base info panel (Task90).
        /// While OFF, MissileDoctrine (the AI's automatic launching) will not fire from this base;
        /// it can only launch via the player's "Set Launch Target".
        /// </summary>
        /// <returns>false if no base with baseId is found.</returns>
        public static bool TrySetMissileAutoLaunch(ushort baseId, bool value)
        {
            lock (_stateLock)
            {
                if (State == null) return false;
                for (int i = 0; i < State.Bases.Count; i++)
                {
                    if (State.Bases[i].BaseId != baseId) continue;
                    State.Bases[i].AutoLaunchMissiles = value;
                    return true;
                }
                return false;
            }
        }
    }
}
