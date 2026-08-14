namespace CSWarfront.Core
{
    /// <summary>
    /// Task134 (Workshop request from siddyskylines1989: "My FPS has been lagging behind and I think
    /// its because I have dozens and dozens of troops. Could you add a button to remove all troops?"):
    /// removing units from play on demand.
    ///
    /// The removal is the quiet kind the stall watchdog already uses (State=Dead, no KillEvent): nothing
    /// explodes, no losses are reported and no faction is credited with a kill — the troops are simply
    /// gone. MilitaryManager's per-tick dead sweep drops them from the list and UnitVisuals reconciles
    /// the GameObjects away by itself, so this needs no engine coupling at all.
    ///
    /// Passengers are dealt with explicitly. A unit riding a transport that was just disbanded would
    /// otherwise keep a CarriedByUnitId pointing at a unit that no longer exists, which excludes it from
    /// every step forever — a permanently frozen squad. Anyone whose carrier is gone is put back on its
    /// own feet. (DisbandAll removes carriers and passengers alike, but the faction-scoped overload can
    /// legitimately disband one side of that pair.)
    ///
    /// Pure logic: deterministic, no RNG, UnityEngine-free.
    /// </summary>
    public static class DisbandCommand
    {
        /// <summary>Disbands every living unit on the map. Returns how many were removed.</summary>
        public static int DisbandAll(WarState state)
        {
            return Disband(state, null);
        }

        /// <summary>Disbands every living unit belonging to one faction. Returns how many were removed.</summary>
        public static int DisbandFaction(WarState state, byte factionId)
        {
            return Disband(state, factionId);
        }

        private static int Disband(WarState state, byte? factionId)
        {
            if (state == null) return 0;

            int removed = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                if (factionId.HasValue && u.FactionId != factionId.Value) continue;

                u.CarriedByUnitId = null;
                u.State = UnitState.Dead;
                u.CurrentHP = 0f;
                removed++;
            }
            if (removed == 0) return 0;

            ReleaseOrphanedPassengers(state);
            return removed;
        }

        /// <summary>Puts back on their own feet any survivors still riding a carrier that was just
        /// disbanded. Without this they stay IsCarried forever and every step skips them.</summary>
        private static void ReleaseOrphanedPassengers(WarState state)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || !u.CarriedByUnitId.HasValue) continue;

                UnitInstance carrier = state.FindUnit(u.CarriedByUnitId.Value);
                if (carrier == null || !carrier.IsAlive) u.CarriedByUnitId = null;
            }
        }
    }
}
