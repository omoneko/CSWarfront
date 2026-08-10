using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// Task101: the infantry's fortification-seeking AI (design §1.4, user request "infantry should
    /// actively head for trenches and bunkers near the enemy"). Infantry-class units
    /// (AiControlled/FreeAdvance) with a hostile unit inside EnemyRadius are given, as their stand, the
    /// fortification within SeekRadius that lies "closest to the enemy".
    ///
    /// The implementation reuses the same CoverDestination/CoverHold fields as CoverSeekStep's
    /// cover movement, and runs **after CoverSeekStep** to overwrite it (fortifications always beat
    /// building shadows). A stateless design re-derived deterministically every tick. Once the enemy is
    /// gone it does nothing = the unit naturally reverts to regular cover/advance, and every hold is
    /// time-boxed by MaxFortHoldHours (Task120) so an assault can never stall in a trench.
    ///
    /// Eligible fortifications (Task121): trenches, bunkers, artillery positions, AT pillboxes and AA
    /// positions. The firing emplacements and bunkers must be friendly-owned (a defunct, ownerless one
    /// still counts as terrain); trenches are ownerless by design and open to anyone — but never one
    /// already held by enemy infantry. Each fortification holds at most GarrisonCapacity friendly
    /// units; overflow goes to the next-best fortification in range.
    /// </summary>
    public static class FortSeekStep
    {
        /// <summary>Seek a fortification when a hostile unit is within this distance.</summary>
        public const float EnemyRadius = 600f;

        /// <summary>The fortification search radius.</summary>
        public const float SeekRadius = 300f;

        /// <summary>Task120 (playtest report "infantry pile into trenches and get stuck; attackers never
        /// capture and eventually vanish"): the maximum time a unit may stay entrenched. The original
        /// implementation re-zeroed CoverHoldTimer every tick, which permanently defeated
        /// MovementStep.MaxCoverHoldHours — units pinned themselves to a trench forever, never resumed the
        /// assault, and (being State==Moving yet motionless) were eventually despawned by
        /// StuckCleanupStep. Entrenching is now time-boxed.</summary>
        public const float MaxFortHoldHours = 4f;

        /// <summary>Task120: after a hold is released, this long must pass before the unit may entrench
        /// again — otherwise the very next tick would re-pin it to the same fortification.</summary>
        public const float ReseekCooldownHours = 8f;

        /// <summary>Task121 (user request "at most three friendly units per fortification; overflow goes
        /// to an adjacent one"): how many friendly units may occupy one fortification at a time.
        /// Occupancy counts units standing in it AND units currently marching to it (both hold it as
        /// their CoverDestination), so a full position never attracts a fourth unit that would end up
        /// milling around outside. When every fortification in range is full the unit simply advances as
        /// usual.</summary>
        public const int GarrisonCapacity = 3;

        /// <summary>Task120: a unit this close to its objective (OrderTargetPos — the base it is assaulting
        /// or capturing) never diverts to a fortification. Taking the objective always outranks digging
        /// in nearby.</summary>
        public const float ObjectiveLockRadius = 200f;

        public static void Advance(WarState state, float dt)
        {
            state.UnitGrid.Build(state.Units);

            // Task121: current occupancy per (fortification, faction), plus which fortification each
            // unit is already committed to. Built once per tick from the live assignments, then kept in
            // step as units are (re)assigned below, so the capacity check sees this tick's decisions too.
            Dictionary<long, int> garrison;
            Dictionary<uint, ushort> assignedFort;
            BuildGarrisonTally(state, out garrison, out assignedFort);

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.IsCarried) continue;
                if (u.Order != UnitOrder.AiControlled && u.Order != UnitOrder.FreeAdvance) continue;

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null) continue;
                if (type.Category != UnitCategory.Infantry && type.Category != UnitCategory.MechInfantry) continue;

                // Task120: run down the re-seek cooldown; while it lasts the unit advances normally.
                if (u.FortSeekCooldown > 0f)
                {
                    u.FortSeekCooldown -= dt;
                    if (u.FortSeekCooldown > 0f) continue;
                    u.FortSeekCooldown = 0f;
                }

                // Task120: close to the objective, press the attack instead of digging in.
                if (u.OrderTargetPos.HasValue &&
                    u.Position.HorizontalDistanceTo(u.OrderTargetPos.Value) <= ObjectiveLockRadius)
                {
                    u.FortHoldTimer = 0f;
                    continue;
                }

                UnitInstance enemy = TargetSearch.FindNearestHostile(u, state.UnitGrid, state.Relations,
                    EnemyRadius, DomainMask.All, state.Types);
                if (enemy == null) { u.FortHoldTimer = 0f; ReleaseGarrison(u, garrison, assignedFort); continue; }

                ushort currentFortId;
                bool hasCurrent = assignedFort.TryGetValue(u.InstanceId, out currentFortId);
                MilitaryBase fort = FindBestFort(state, u, enemy.Position, garrison,
                    hasCurrent ? (ushort?)currentFortId : null);
                if (fort == null) { u.FortHoldTimer = 0f; ReleaseGarrison(u, garrison, assignedFort); continue; }

                // Task120: only count time actually spent entrenched (arrived), not the approach march.
                bool arrived = u.Position.HorizontalDistanceTo(fort.Position) <= MovementStep.CoverArrivalDistance;
                if (arrived)
                {
                    u.FortHoldTimer += dt;
                    if (u.FortHoldTimer > MaxFortHoldHours)
                    {
                        // Time boxed out: let go and resume the advance (the objective matters more).
                        u.FortHoldTimer = 0f;
                        u.FortSeekCooldown = ReseekCooldownHours;
                        u.CoverDestination = null;
                        u.CoverHold = false;
                        u.CoverHoldTimer = 0f;
                        ReleaseGarrison(u, garrison, assignedFort); // Task121: frees the slot for someone else
                        continue;
                    }
                }

                // Head for / hold the fortification (overwriting CoverSeekStep's decision, see the class comment).
                u.CoverDestination = fort.Position;
                u.CoverHold = true;
                u.CoverHoldTimer = 0f; // the fort hold has its own cap (MaxFortHoldHours) instead

                // Task121: keep the running tally in step with this decision.
                if (!hasCurrent || currentFortId != fort.BaseId)
                {
                    if (hasCurrent) Bump(garrison, currentFortId, u.FactionId, -1);
                    Bump(garrison, fort.BaseId, u.FactionId, +1);
                    assignedFort[u.InstanceId] = fort.BaseId;
                }
            }
        }

        /// <summary>Task121: the garrisonable fortification types and the radius (m) within which a unit
        /// counts as being "in" one. Trenches and bunkers reuse the defense-bonus radii; the firing
        /// emplacements use a radius derived from their footprint (AT pillbox 16×16, artillery position
        /// and AA position 24×24).</summary>
        public static bool TryGetGarrisonRadius(BaseType type, out float radius)
        {
            switch (type)
            {
                case BaseType.Trench: radius = FortDefenseBonus.TrenchRadius; return true;
                case BaseType.Bunker: radius = FortDefenseBonus.BunkerRadius; return true;
                case BaseType.AtPillbox: radius = 12f; return true;
                case BaseType.ArtilleryPost: radius = 18f; return true;
                case BaseType.AaPosition: radius = 18f; return true;
                default: radius = 0f; return false;
            }
        }

        /// <summary>Task121: how many of factionId's units currently hold this fortification (standing in
        /// it or marching to it). Used by the capacity check and exposed for tests.</summary>
        public static int CountGarrison(WarState state, MilitaryBase fort, byte factionId)
        {
            float radius;
            if (fort == null || !TryGetGarrisonRadius(fort.Type, out radius)) return 0;

            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance o = state.Units[i];
                if (!o.IsAlive || o.IsCarried) continue;
                if (o.FactionId != factionId) continue;
                if (!o.CoverHold || !o.CoverDestination.HasValue) continue;
                if (o.CoverDestination.Value.HorizontalDistanceTo(fort.Position) <= radius) count++;
            }
            return count;
        }

        private static long GarrisonKey(ushort baseId, byte factionId)
        {
            return ((long)baseId << 8) | factionId;
        }

        private static void Bump(Dictionary<long, int> garrison, ushort baseId, byte factionId, int delta)
        {
            long key = GarrisonKey(baseId, factionId);
            int n;
            garrison.TryGetValue(key, out n);
            n += delta;
            if (n <= 0) garrison.Remove(key);
            else garrison[key] = n;
        }

        private static int GarrisonOf(Dictionary<long, int> garrison, ushort baseId, byte factionId)
        {
            int n;
            garrison.TryGetValue(GarrisonKey(baseId, factionId), out n);
            return n;
        }

        /// <summary>Task121: tallies the live assignments once per tick (see Advance).</summary>
        private static void BuildGarrisonTally(WarState state, out Dictionary<long, int> garrison,
            out Dictionary<uint, ushort> assignedFort)
        {
            garrison = new Dictionary<long, int>();
            assignedFort = new Dictionary<uint, ushort>();

            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance o = state.Units[i];
                if (!o.IsAlive || o.IsCarried) continue;
                if (!o.CoverHold || !o.CoverDestination.HasValue) continue;

                for (int b = 0; b < state.Bases.Count; b++)
                {
                    MilitaryBase mb = state.Bases[b];
                    float radius;
                    if (!TryGetGarrisonRadius(mb.Type, out radius)) continue;
                    if (o.CoverDestination.Value.HorizontalDistanceTo(mb.Position) > radius) continue;

                    Bump(garrison, mb.BaseId, o.FactionId, +1);
                    assignedFort[o.InstanceId] = mb.BaseId;
                    break; // one unit occupies at most one fortification
                }
            }
        }

        /// <summary>Task121: gives up this unit's garrison slot (it is no longer heading for a fort).</summary>
        private static void ReleaseGarrison(UnitInstance u, Dictionary<long, int> garrison,
            Dictionary<uint, ushort> assignedFort)
        {
            ushort fortId;
            if (!assignedFort.TryGetValue(u.InstanceId, out fortId)) return;
            Bump(garrison, fortId, u.FactionId, -1);
            assignedFort.Remove(u.InstanceId);
        }

        /// <summary>The usable fortification within SeekRadius closest to the enemy position, skipping
        /// ones already at GarrisonCapacity (Task121 — that is the "overflow moves to an adjacent
        /// position" rule). The fortification this unit already occupies never counts itself as full.
        /// Null when nothing suitable is in range.</summary>
        private static MilitaryBase FindBestFort(WarState state, UnitInstance u, WorldPos enemyPos,
            Dictionary<long, int> garrison, ushort? currentFortId)
        {
            MilitaryBase best = null;
            float bestEnemyDist = float.MaxValue;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                float radius;
                if (!TryGetGarrisonRadius(mb.Type, out radius)) continue;

                // Owned emplacements (bunker, artillery position, AT pillbox, AA position) must be
                // friendly (or defunct = neutral): never charge into a working enemy one. Trenches are
                // ownerless by design and open to anyone.
                if (mb.Type != BaseType.Trench && mb.OwnerFactionId != null &&
                    mb.OwnerFactionId.Value != u.FactionId) continue;

                if (u.Position.HorizontalDistanceTo(mb.Position) > SeekRadius) continue;
                if (mb.Type == BaseType.Trench && IsHeldByEnemyInfantry(state, mb, u.FactionId, radius)) continue;

                // Task121: capacity. The slot this unit already holds is excluded from the count, so
                // staying put is always allowed.
                int occupants = GarrisonOf(garrison, mb.BaseId, u.FactionId);
                if (currentFortId.HasValue && currentFortId.Value == mb.BaseId) occupants--;
                if (occupants >= GarrisonCapacity) continue;

                float d = enemyPos.HorizontalDistanceTo(mb.Position);
                // Deterministic tie-break on BaseId (equal distances must not depend on list order).
                if (d < bestEnemyDist || (d == bestEnemyDist && best != null && mb.BaseId < best.BaseId))
                { bestEnemyDist = d; best = mb; }
            }
            return best;
        }

        /// <summary>Whether enemy-faction infantry already sits on this trench (never head for a
        /// captured trench).</summary>
        private static bool IsHeldByEnemyInfantry(WarState state, MilitaryBase trench, byte factionId, float radius)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance o = state.Units[i];
                if (!o.IsAlive || o.IsCarried) continue;
                if (!state.Relations.Get(factionId, o.FactionId).IsHostile()) continue;
                UnitType t = state.Types.Get(o.TypeKey);
                if (t == null || (t.Category != UnitCategory.Infantry && t.Category != UnitCategory.MechInfantry)) continue;
                if (trench.Position.HorizontalDistanceTo(o.Position) <= radius) return true;
            }
            return false;
        }
    }
}
