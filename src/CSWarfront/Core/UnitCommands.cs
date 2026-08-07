using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// Applies the command orders the player gives a box-selected force (free advance / hold / rally &amp;
    /// hold / delegate to AI) onto UnitInstance.Order/RallyPoint (pure logic, Task48). The Game layer
    /// (UnitCommandInput/MilitaryManager) only has to pass the selected InstanceId list in here.
    ///
    /// Each Apply first squares away the "runtime state left over from the old order" (Path/PathIndex/
    /// PathTarget/OrderTargetPos/CoverDestination/CoverHold) into the shape fitting the new order, then
    /// rewrites Order. That contract keeps stale state from leaking into the next tick's
    /// MovementStep/CoverSeekStep. Nonexistent/dead IDs are silently skipped (excluded from the returned
    /// count; nothing throws).
    /// </summary>
    public static class UnitCommands
    {
        /// <summary>Free advance (FreeAdvance): move at each unit's own top speed toward the nearest
        /// hostile base and engage as normal. The next InvasionOrders.AssignAdvance re-lays target base
        /// and path, so here it suffices to drop the old target/path/cover state.</summary>
        public static int ApplyFreeAdvance(WarState state, IList<uint> instanceIds)
        {
            return ForEachLiving(state, instanceIds, u =>
            {
                u.Order = UnitOrder.FreeAdvance;
                u.RallyPoint = null;
                u.ClearPath();
                u.OrderTargetPos = null;
                u.CoverDestination = null;
                u.CoverHold = false;
                if (u.State != UnitState.Engaging) u.State = UnitState.Idle;
            });
        }

        /// <summary>Hold: never move from the spot (MovementStep sees Order==Hold and always skips), but
        /// keep returning fire at enemies in range (CombatStep is untouched, so this is automatically
        /// passive defense).</summary>
        public static int ApplyHold(WarState state, IList<uint> instanceIds)
        {
            return ForEachLiving(state, instanceIds, u =>
            {
                u.Order = UnitOrder.Hold;
                u.RallyPoint = null;
                u.ClearPath();
                u.OrderTargetPos = null;
                u.CoverDestination = null;
                u.CoverHold = false;
                if (u.State != UnitState.Engaging) u.State = UnitState.Idle;
            });
        }

        /// <summary>Rally &amp; hold (RallyHold): move to rallyPoint and stop on arrival; whether moving
        /// or stopped, fire only at enemies in range (CoverSeekStep excludes Order==RallyHold from cover
        /// moves, so no pursuit or base advances happen). If state.Roads is supplied, the same road
        /// pathfinding (A*) as InvasionOrders.AssignAdvance is computed once toward rallyPoint and
        /// stored in Path/PathIndex/PathTarget (no per-tick recompute; MovementStep.AdvanceTowardRally
        /// consumes it).</summary>
        public static int ApplyRally(WarState state, IList<uint> instanceIds, WorldPos rallyPoint)
        {
            return ForEachLiving(state, instanceIds, u =>
            {
                u.Order = UnitOrder.RallyHold;
                u.RallyPoint = rallyPoint;
                u.CoverDestination = null;
                u.CoverHold = false;
                u.ClearPath();

                // Task92: pathing is picked by domain (land = road A*, sea = SeaGrid, air = pathless
                // straight line). Sea units used to get road paths too, harmlessly, because the sea code
                // ignored Path. Now that AdvanceSea consumes Path, avoid handing over a bogus road path.
                UnitType rallyType = state.Types.Get(u.TypeKey);
                Domain rallyDomain = rallyType != null ? rallyType.Domain : Domain.Land;
                if (rallyDomain == Domain.Land && state.Roads != null)
                {
                    List<WorldPos> path = state.Roads.FindPath(
                        u.Position, rallyPoint, InvasionOrders.PathSnapRadius, u.InstanceId, InvasionOrders.PathJitter);
                    if (path != null)
                    {
                        u.Path = path;
                        u.PathIndex = 0;
                        u.PathTarget = rallyPoint;
                    }
                }
                else if (rallyDomain == Domain.Sea && state.SeaNav != null)
                {
                    List<WorldPos> path = state.SeaNav.FindPath(u.Position, rallyPoint, InvasionOrders.SeaPathSnapRadius);
                    if (path != null)
                    {
                        u.Path = path;
                        u.PathIndex = 0;
                        u.PathTarget = rallyPoint;
                    }
                }

                if (u.State != UnitState.Engaging) u.State = UnitState.Moving;
            });
        }

        /// <summary>Return to AI control (AiControlled). The next InvasionOrders.AssignAdvance re-lays
        /// target/path as usual, so here it suffices to drop all the old state.</summary>
        public static int ClearOrders(WarState state, IList<uint> instanceIds)
        {
            return ForEachLiving(state, instanceIds, u =>
            {
                u.Order = UnitOrder.AiControlled;
                u.RallyPoint = null;
                u.ClearPath();
                u.OrderTargetPos = null;
                u.CoverDestination = null;
                u.CoverHold = false;
            });
        }

        private static int ForEachLiving(WarState state, IList<uint> instanceIds, System.Action<UnitInstance> apply)
        {
            if (state == null || instanceIds == null) return 0;
            int count = 0;
            for (int i = 0; i < instanceIds.Count; i++)
            {
                UnitInstance u = state.FindUnit(instanceIds[i]);
                if (u == null || !u.IsAlive) continue;
                apply(u);
                count++;
            }
            return count;
        }
    }
}
