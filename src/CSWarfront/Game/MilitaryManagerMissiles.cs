using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Additional MilitaryManager members for player ballistic-missile operations (manual build
    /// orders and launch-target designation from the base panel, Task63). Split into a partial
    /// class because of the 500-line limit on MilitaryManager.cs (same policy as
    /// MilitaryManagerManualProduction.cs from Task34).
    /// _stateLock / State are private static members declared on the MilitaryManager.cs side;
    /// since this is a partial class they can be accessed directly from here as well. All methods
    /// are thin wrappers that hold _stateLock only briefly and delegate to Core, and never touch
    /// any Unity API.
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>
        /// Player-driven manual missile build order, called from the base info panel (Task63).
        /// Thin wrapper that just delegates to Core.MissileStockpile.TryBuildMissile inside _stateLock.
        /// </summary>
        public static MissileBuildResult TryQueueMissileBuild(ushort baseId)
        {
            lock (_stateLock)
            {
                if (State == null) return MissileBuildResult.BaseNotFound;
                return MissileStockpile.TryBuildMissile(State, baseId);
            }
        }

        /// <summary>
        /// Player-driven missile launch, called from the base info panel (Task63).
        /// target is the world position resolved by the UI side (the same raycast path as
        /// UnitCommandInput's rally-point designation).
        /// Thin wrapper that just delegates to Core.MissileStep.TryLaunch inside _stateLock.
        /// </summary>
        public static LaunchResult TryLaunchMissile(ushort baseId, Vector3 target)
        {
            lock (_stateLock)
            {
                if (State == null) return LaunchResult.BaseNotFound;
                return MissileStep.TryLaunch(State, baseId, new WorldPos(target.x, target.y, target.z));
            }
        }
    }
}
