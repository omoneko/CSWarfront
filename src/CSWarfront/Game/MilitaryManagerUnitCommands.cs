using System.Collections.Generic;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Additional MilitaryManager members for player unit commands (box selection → free advance /
    /// hold / rally-and-wait / delegate to AI, Task48). Split into a partial class because of the
    /// 500-line limit on MilitaryManager.cs (same policy as MilitaryManagerManualProduction from
    /// Task34).
    /// _stateLock / State are private static members declared on the MilitaryManager.cs side;
    /// since this is a partial class they can be accessed directly from here as well.
    ///
    /// The caller (Game/UI/UnitCommandInput) calls from the main thread. Each method is a thin
    /// wrapper that holds _stateLock only briefly and delegates to Core.UnitCommands, and never
    /// touches any Unity API (following the established convention of not calling Unity APIs
    /// while holding the lock).
    /// </summary>
    public static partial class MilitaryManager
    {
        /// <summary>Free advance (Task48). Delegates to Core.UnitCommands.ApplyFreeAdvance and logs
        /// the number of affected units in one line. Returns 0 if State is uninitialized.</summary>
        public static int CommandFreeAdvance(IList<uint> instanceIds)
        {
            lock (_stateLock)
            {
                if (State == null) return 0;
                int n = UnitCommands.ApplyFreeAdvance(State, instanceIds);
                ModConfig.Log("MilitaryManager: FreeAdvance applied to " + n + " unit(s)");
                return n;
            }
        }

        /// <summary>Hold (Task48). Delegates to Core.UnitCommands.ApplyHold and logs the number of
        /// affected units in one line. Returns 0 if State is uninitialized.</summary>
        public static int CommandHold(IList<uint> instanceIds)
        {
            lock (_stateLock)
            {
                if (State == null) return 0;
                int n = UnitCommands.ApplyHold(State, instanceIds);
                ModConfig.Log("MilitaryManager: Hold applied to " + n + " unit(s)");
                return n;
            }
        }

        /// <summary>Rally and wait (Task48). Delegates to Core.UnitCommands.ApplyRally and logs the
        /// number of affected units in one line. Returns 0 if State is uninitialized.</summary>
        public static int CommandRally(IList<uint> instanceIds, WorldPos rallyPoint)
        {
            lock (_stateLock)
            {
                if (State == null) return 0;
                int n = UnitCommands.ApplyRally(State, instanceIds, rallyPoint);
                ModConfig.Log("MilitaryManager: Rally applied to " + n + " unit(s) at " +
                    rallyPoint.X.ToString("0") + "," + rallyPoint.Z.ToString("0"));
                return n;
            }
        }

        /// <summary>Return control to the AI (Task48). Delegates to Core.UnitCommands.ClearOrders and
        /// logs the number of affected units in one line. Returns 0 if State is uninitialized.</summary>
        public static int CommandClear(IList<uint> instanceIds)
        {
            lock (_stateLock)
            {
                if (State == null) return 0;
                int n = UnitCommands.ClearOrders(State, instanceIds);
                ModConfig.Log("MilitaryManager: orders cleared (AI-controlled) for " + n + " unit(s)");
                return n;
            }
        }
    }
}
