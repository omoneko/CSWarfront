using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task134 (Workshop request from siddyskylines1989: "could you add a button to remove all
    /// troops?"): sim-side handling of the Disband All Units button (MilitaryManager partial, split out
    /// to keep the files under the 500-line limit).
    ///
    /// Same handoff as the defense-layout buttons: the UI (main thread) only raises a flag, and
    /// ProcessDisbandRequest drains it in OnSimTick on the sim thread, because unit removal must not
    /// race the simulation. The result is reported back through the existing UI toast queue.
    ///
    /// Note this does not stop bases from building replacements — a base with auto-production on will
    /// start refilling immediately. That is deliberate: the button clears the map right now, and which
    /// bases keep producing stays the player's decision (Auto-produce toggle in the base info panel).
    /// </summary>
    public static partial class MilitaryManager
    {
        private static bool _disbandAllRequested;

        /// <summary>Called from the main thread (MilitaryBuildPanel).</summary>
        public static void RequestDisbandAllUnits()
        {
            lock (_stateLock) { _disbandAllRequested = true; }
        }

        /// <summary>Main thread (MilitaryBuildPanel): how many units are on the map right now, for the
        /// confirmation prompt. Snapshot only — the count may have moved on by the time the request is
        /// processed, which does not matter for a prompt.</summary>
        public static int CountLivingUnits()
        {
            lock (_stateLock)
            {
                if (State == null) return 0;
                int count = 0;
                for (int i = 0; i < State.Units.Count; i++)
                    if (State.Units[i].IsAlive) count++;
                return count;
            }
        }

        /// <summary>Sim thread (OnSimTick, inside _stateLock): drains the pending request.</summary>
        private static void ProcessDisbandRequest()
        {
            if (State == null || !_disbandAllRequested) return;
            _disbandAllRequested = false;

            int removed = DisbandCommand.DisbandAll(State);
            QueueUiToast(removed == 0
                ? WarfrontStrings.BuildPanel_ToastNoUnitsToDisband
                : string.Format(WarfrontStrings.BuildPanel_ToastDisbanded, removed));
            ModConfig.Log("DisbandAll: removed " + removed + " unit(s) on player request");
        }
    }
}
