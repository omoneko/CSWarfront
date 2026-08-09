namespace CSWarfront.Core
{
    /// <summary>
    /// Task99: automatic supply-truck maintenance and the logistics loop (design:
    /// 2026-08-03-economy-supply-design.md §4.3).
    ///
    /// Trucks (UnitCategory.SupplyTruck) are unarmed regular units, fully automated:
    ///   1. load supplies from the SupplyStock at a base (a friendly army base)
    ///   2. drive by road toward the friendly land unit with the least ammo (excluding those
    ///      auto-resupplied inside a base zone)
    ///   3. once within TransferRadius, transfer ammo to the friendly land units around
    ///   4. when the load runs out, return to a base to reload (or wait near the base with no one to serve)
    ///
    /// No state machine — every tick the action is derived from SupplyLoad and position (deterministic and
    /// stateless, the same design as AssignAdvance / threat interception). Excluded from AI advances
    /// (AssignAdvance) and regular combat (CombatStep). Trucks the player ordered to Hold/RallyHold are
    /// never touched (unit commands take priority). A destroyed truck loses its cargo with it (no special
    /// handling needed — it creates the value of escorting them).
    /// </summary>
    public static class SupplyTruckStep
    {
        /// <summary>Truck cap per faction (user spec: 30, separate from the 150 combat-unit cap).</summary>
        public const int MaxTrucksPerFaction = 30;

        /// <summary>Trucks each army base maintains (inside the faction cap).</summary>
        public const int TrucksPerArmyBase = 2;

        /// <summary>Distance from a base at which loading/returning counts as complete. Aligned with
        /// ResupplyStep's supply zone.</summary>
        public const float LoadRadius = ResupplyStep.ResupplyRadius;

        /// <summary>Supplies consumed to fill up (SupplyLoad=1).</summary>
        public const float SupplyPerTruckLoad = 30f;

        /// <summary>Ammo is transferred to friendly land units within this distance.</summary>
        public const float TransferRadius = 60f;

        /// <summary>Ammo refill rate via transfer (fraction per in-game hour, per unit). Faster than the
        /// in-base auto-resupply (25%/h) = gives front-line delivery its value.</summary>
        public const float TransferPerHour = 0.5f;

        /// <summary>Load consumed to refill one unit from Ammo 0 to 1 (= five refills per full load).</summary>
        public const float LoadPerFullReload = 0.2f;

        /// <summary>Friendlies below this ammo fraction become delivery targets.</summary>
        public const float NeedThreshold = 0.5f;

        /// <summary>Road path searches allowed per Advance (same idea as AssignAdvance's
        /// maxPathComputations).</summary>
        private const int MaxPathComputationsPerTick = 2;

        /// <summary>Per economy tick: army bases maintain their trucks. One spawn attempt per base per
        /// tick, inside the faction cap and the per-base quota (paid with manpower + production; no
        /// replacement when unaffordable).</summary>
        public static void MaintainTrucks(WarState state)
        {
            UnitType truckType = state.Types.Get(LandUnitRoster.TypeKey(UnitCategory.SupplyTruck, 1));
            if (truckType == null) return;

            for (int fi = 0; fi < state.Factions.Count; fi++)
            {
                Faction f = state.Factions[fi];
                if (f.Eliminated || f.Id == Faction.InvaderFactionId) continue; // Invaders have infinite ammo = no logistics

                int trucks = CountTrucks(state, f.Id);
                if (trucks >= MaxTrucksPerFaction) continue;

                for (int bi = 0; bi < state.Bases.Count && trucks < MaxTrucksPerFaction; bi++)
                {
                    MilitaryBase b = state.Bases[bi];
                    if (b.OwnerFactionId == null || b.OwnerFactionId.Value != f.Id) continue;
                    if (b.Type != BaseType.Army) continue;
                    if (CountTrucksNearBase(state, f.Id, b) >= TrucksPerArmyBase) continue;
                    if (!UnitCosts.TryPay(f, truckType, f.Treasury)) break; // out of resources: this faction is done this tick

                    var u = new UnitInstance(state.AllocInstanceId(), truckType.TypeKey, f.Id, truckType.MaxHP,
                        new WorldPos(b.Position.X, b.Position.Y, b.Position.Z));
                    u.State = UnitState.Idle;
                    state.Units.Add(u);
                    trucks++;
                }
            }
        }

        /// <summary>Every tick: advances loading/delivery/transfer/return for every truck (see the class
        /// comment).</summary>
        public static void Advance(WarState state, float dt)
        {
            int pathComputations = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive) continue;
                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null || type.Category != UnitCategory.SupplyTruck) continue;
                if (u.Order == UnitOrder.Hold || u.Order == UnitOrder.RallyHold) continue; // unit commands take priority

                // Trucks are exempt from AssignAdvance, so the path retry cooldown is consumed here.
                u.PathRetryCooldown -= dt;
                if (u.PathRetryCooldown < 0f) u.PathRetryCooldown = 0f;

                MilitaryBase home = FindNearestOwnArmyBase(state, u);
                if (home == null)
                {
                    // All army bases lost: wait in place (resumes next tick once a base is recaptured).
                    u.OrderTargetPos = null;
                    u.ClearPath();
                    u.State = UnitState.Idle;
                    continue;
                }

                if (u.SupplyLoad <= 0f)
                {
                    AdvanceEmptyTruck(state, u, home, ref pathComputations);
                    continue;
                }

                AdvanceLoadedTruck(state, u, type, home, dt, ref pathComputations);
            }
        }

        /// <summary>Empty: head for the nearest load source (an army base = faction pool, or a stocked
        /// supply depot / cargo station) and load on arrival (Task101: pickup from depots/stations
        /// supported).</summary>
        private static void AdvanceEmptyTruck(WarState state, UnitInstance u, MilitaryBase home, ref int pathComputations)
        {
            Faction f = state.FindFaction(u.FactionId);
            MilitaryBase source = FindNearestLoadSource(state, u, f);
            if (source == null)
            {
                // No supplies anywhere: wait near the army base.
                if (u.Position.HorizontalDistanceTo(home.Position) > LoadRadius)
                    SetDestination(state, u, home.Position, ref pathComputations);
                else
                    Wait(u);
                return;
            }

            if (u.Position.HorizontalDistanceTo(source.Position) > LoadRadius)
            {
                SetDestination(state, u, source.Position, ref pathComputations);
                return;
            }

            bool fromDepot = FortificationRules.IsFortification(source.Type);
            float available = fromDepot ? source.StoredSupplies : f.SupplyStock;
            float room = 1f - u.SupplyLoad;
            float loadable = available / SupplyPerTruckLoad;
            float load = room < loadable ? room : loadable;
            if (load <= 0f) { Wait(u); return; }

            if (fromDepot) source.StoredSupplies -= load * SupplyPerTruckLoad;
            else f.TrySpendSupply(load * SupplyPerTruckLoad);
            u.SupplyLoad += load;
            u.SupplyLoadFromDepot = fromDepot; // depot cargo is never re-stocked into depots (see the UnitInstance comment)
            Wait(u); // loading done; next tick's AdvanceLoadedTruck picks the delivery target
        }

        /// <summary>Task101: load-source candidates = friendly army bases (when the faction pool has
        /// stock) plus operational friendly SupplyDepots/CargoStations with stock. Returns the nearest
        /// (null when none).</summary>
        private static MilitaryBase FindNearestLoadSource(WarState state, UnitInstance u, Faction f)
        {
            MilitaryBase best = null;
            float bestDist = float.MaxValue;
            bool poolHasStock = f != null && f.SupplyStock > 0f;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != u.FactionId) continue;

                bool valid;
                if (mb.Type == BaseType.Army) valid = poolHasStock;
                else if (mb.Type == BaseType.SupplyDepot || mb.Type == BaseType.CargoStation) valid = mb.StoredSupplies > 0f;
                else valid = false;
                if (!valid) continue;

                float d = u.Position.HorizontalDistanceTo(mb.Position);
                if (d < bestDist) { bestDist = d; best = mb; }
            }
            return best;
        }

        /// <summary>Loaded: head for the delivery target and transfer on arrival; wait near the base with
        /// no target.
        /// Task111 (Workshop report "artillery positions are not being supplied"): delivery targets include
        /// not just units but also under-ammo fortifications (bunkers/artillery positions) — previously
        /// fortifications only recovered within 200m of a base/stocked depot (ResupplyStep), so an
        /// artillery position built alone at the front was never resupplied by anyone.</summary>
        private static void AdvanceLoadedTruck(WarState state, UnitInstance u, UnitType type, MilitaryBase home,
            float dt, ref int pathComputations)
        {
            UnitInstance target = FindNeediestLandUnit(state, u);
            MilitaryBase fortTarget = FindNeediestFort(state, u);

            // With both a unit and a fort in need, serve whichever ammo fraction is lower (deterministic;
            // ties go to the unit).
            if (target != null && fortTarget != null && fortTarget.FortAmmo < target.Ammo) target = null;
            if (target == null && fortTarget != null)
            {
                if (u.Position.HorizontalDistanceTo(fortTarget.Position) > TransferRadius)
                {
                    SetDestination(state, u, fortTarget.Position, ref pathComputations);
                    return;
                }

                // Arrived: transfer into the fortification's ammo (same rate and load cost as unit transfers).
                Wait(u);
                float fortRefill = TransferPerHour * dt;
                if (fortRefill > 1f - fortTarget.FortAmmo) fortRefill = 1f - fortTarget.FortAmmo;
                float fortCost = fortRefill * LoadPerFullReload;
                if (fortCost > u.SupplyLoad)
                {
                    fortCost = u.SupplyLoad;
                    fortRefill = fortCost / LoadPerFullReload;
                }
                fortTarget.FortAmmo += fortRefill;
                if (fortTarget.FortAmmo > 1f) fortTarget.FortAmmo = 1f;
                u.SupplyLoad -= fortCost;
                if (u.SupplyLoad < 0.001f) u.SupplyLoad = 0f;
                return;
            }

            if (target == null)
            {
                // Task101: while no friendly needs resupply, haul stock to supply depots (faction-pool
                // cargo only; depot cargo is never re-stocked = prevents the infinite shuffle).
                MilitaryBase depot = u.SupplyLoadFromDepot ? null : FindNearestDepotWithRoom(state, u);
                if (depot != null)
                {
                    if (u.Position.HorizontalDistanceTo(depot.Position) > LoadRadius)
                    {
                        SetDestination(state, u, depot.Position, ref pathComputations);
                        return;
                    }
                    // Unload: move as much as the stock has room for.
                    float cap = FortificationRules.StoredSupplyCap(depot.Type);
                    float roomSupplies = cap - depot.StoredSupplies;
                    float carried = u.SupplyLoad * SupplyPerTruckLoad;
                    float transfer = carried < roomSupplies ? carried : roomSupplies;
                    depot.StoredSupplies += transfer;
                    u.SupplyLoad -= transfer / SupplyPerTruckLoad;
                    if (u.SupplyLoad < 0.001f) u.SupplyLoad = 0f;
                    Wait(u);
                    return;
                }

                // Nowhere to haul to either: return near the base and wait.
                if (u.Position.HorizontalDistanceTo(home.Position) > LoadRadius)
                    SetDestination(state, u, home.Position, ref pathComputations);
                else
                    Wait(u);
                return;
            }

            if (u.Position.HorizontalDistanceTo(target.Position) > TransferRadius)
            {
                SetDestination(state, u, target.Position, ref pathComputations);
                return;
            }

            // Arrived: transfer to ALL friendly land units within TransferRadius, not just the designated
            // target — serving the whole group at the front together is both more natural and simpler.
            Wait(u);
            for (int i = 0; i < state.Units.Count && u.SupplyLoad > 0f; i++)
            {
                UnitInstance ally = state.Units[i];
                if (!IsResupplyCandidate(state, u, ally, 1f)) continue;
                if (u.Position.HorizontalDistanceTo(ally.Position) > TransferRadius) continue;

                float refill = TransferPerHour * dt;
                if (refill > 1f - ally.Ammo) refill = 1f - ally.Ammo;
                float loadCost = refill * LoadPerFullReload;
                if (loadCost > u.SupplyLoad)
                {
                    loadCost = u.SupplyLoad;
                    refill = loadCost / LoadPerFullReload;
                }
                ally.Ammo += refill;
                if (ally.Ammo > 1f) ally.Ammo = 1f;
                u.SupplyLoad -= loadCost;
            }
            if (u.SupplyLoad < 0.001f) u.SupplyLoad = 0f; // trim the remainder so "empty → return" transitions reliably
        }

        /// <summary>The delivery target with the least ammo (a friendly land unit below NeedThreshold).
        /// Ties go to the earliest list entry (deterministic).</summary>
        private static UnitInstance FindNeediestLandUnit(WarState state, UnitInstance truck)
        {
            UnitInstance best = null;
            float bestAmmo = float.MaxValue;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance ally = state.Units[i];
                if (!IsResupplyCandidate(state, truck, ally, NeedThreshold)) continue;
                if (ally.Ammo < bestAmmo) { bestAmmo = ally.Ammo; best = ally; }
            }
            return best;
        }

        /// <summary>Task111: the driest of the faction's operational fortifications (bunker/artillery
        /// position) whose ammo is below NeedThreshold. Forts inside the 200m zone of a base/stocked depot
        /// (= where ResupplyStep auto-refills) are excluded. Null when none.</summary>
        private static MilitaryBase FindNeediestFort(WarState state, UnitInstance truck)
        {
            MilitaryBase best = null;
            float bestAmmo = float.MaxValue;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase fort = state.Bases[b];
                if (!FortificationRules.IsArmedFortification(fort.Type)) continue; // Task118
                if (fort.OwnerFactionId == null || fort.OwnerFactionId.Value != truck.FactionId) continue;
                if (fort.CurrentHP <= 0f) continue;
                if (fort.FortAmmo >= NeedThreshold) continue;
                if (IsFortNearAutoResupply(state, fort)) continue; // in-zone forts are ResupplyStep's job
                if (fort.FortAmmo < bestAmmo) { bestAmmo = fort.FortAmmo; best = fort; }
            }
            return best;
        }

        /// <summary>Task111: is this fortification inside ResupplyStep's auto-refill range (within 200m of
        /// a normal base or a stocked SupplyDepot)? The same test as ResupplyStep's fort-refill loop.</summary>
        private static bool IsFortNearAutoResupply(WarState state, MilitaryBase fort)
        {
            for (int k = 0; k < state.Bases.Count; k++)
            {
                MilitaryBase mb = state.Bases[k];
                if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != fort.OwnerFactionId.Value) continue;
                if (mb.BaseId == fort.BaseId) continue;
                if (fort.Position.HorizontalDistanceTo(mb.Position) > ResupplyStep.ResupplyRadius) continue;
                if (!FortificationRules.IsFortification(mb.Type)) return true;
                if (mb.Type == BaseType.SupplyDepot && mb.StoredSupplies > 0f) return true;
            }
            return false;
        }

        /// <summary>Is this a truck resupply candidate: a living, same-faction, ammo-bound land unit
        /// (excluding the truck itself), with Ammo below the threshold, and outside the in-base
        /// auto-resupply (ResupplyStep) range.</summary>
        private static bool IsResupplyCandidate(WarState state, UnitInstance truck, UnitInstance ally, float threshold)
        {
            if (!ally.IsAlive || ally.FactionId != truck.FactionId || ally.InstanceId == truck.InstanceId) return false;
            if (ally.Ammo >= threshold) return false;
            UnitType allyType = state.Types.Get(ally.TypeKey);
            if (allyType == null || allyType.Domain != Domain.Land) return false;
            if (allyType.AmmoCombatHours <= 0f) return false; // not ammo-bound (includes trucks)
            if (ResupplyStep.IsNearResupplyPoint(state, ally, allyType)) return false; // in-zone units are the base's job
            return true;
        }

        /// <summary>Task101: the nearest operational friendly SupplyDepot with stock room. Null when none.
        /// No stock hauling to CargoStations (stations are fed by rail = TrainStep).</summary>
        private static MilitaryBase FindNearestDepotWithRoom(WarState state, UnitInstance u)
        {
            MilitaryBase best = null;
            float bestDist = float.MaxValue;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != u.FactionId) continue;
                if (mb.Type != BaseType.SupplyDepot) continue;
                if (mb.StoredSupplies >= FortificationRules.StoredSupplyCap(mb.Type)) continue;
                float d = u.Position.HorizontalDistanceTo(mb.Position);
                if (d < bestDist) { bestDist = d; best = mb; }
            }
            return best;
        }

        private static MilitaryBase FindNearestOwnArmyBase(WarState state, UnitInstance u)
        {
            MilitaryBase best = null;
            float bestDist = float.MaxValue;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != u.FactionId) continue;
                if (mb.Type != BaseType.Army) continue;
                float d = u.Position.HorizontalDistanceTo(mb.Position);
                if (d < bestDist) { bestDist = d; best = mb; }
            }
            return best;
        }

        /// <summary>Sets the destination and computes a road path if needed (the same conventions as
        /// AssignAdvance's land branch: rebuild the path when the destination changed substantially;
        /// failures are throttled via PathRetryCooldown).</summary>
        private static void SetDestination(WarState state, UnitInstance u, WorldPos dest, ref int pathComputations)
        {
            const float targetChangeEpsilon = 30f; // delivery targets move; small drifts must not rebuild the path

            u.OrderTargetPos = dest;
            u.State = UnitState.Moving;

            if (u.PathTarget.HasValue &&
                (System.Math.Abs(u.PathTarget.Value.X - dest.X) > targetChangeEpsilon ||
                 System.Math.Abs(u.PathTarget.Value.Z - dest.Z) > targetChangeEpsilon))
                u.ClearPath();

            if (state.Roads != null && u.Path == null && u.PathRetryCooldown <= 0f &&
                pathComputations < MaxPathComputationsPerTick)
            {
                pathComputations++;
                var path = state.Roads.FindPath(u.Position, dest, InvasionOrders.PathSnapRadius,
                    u.InstanceId, InvasionOrders.PathJitter, float.MaxValue);
                u.Path = path;
                u.PathIndex = 0;
                u.PathTarget = dest;
                u.PathRetryCooldown = path == null ? InvasionOrders.PathRetryFailCooldownHours : 0f;
            }
        }

        private static void Wait(UnitInstance u)
        {
            u.OrderTargetPos = null;
            u.ClearPath();
            u.State = UnitState.Idle;
        }

        private static int CountTrucks(WarState state, byte factionId)
        {
            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t != null && t.Category == UnitCategory.SupplyTruck) count++;
            }
            return count;
        }

        /// <summary>Trucks "assigned" to this base. No strict ownership exists, so friendly trucks within
        /// LoadRadius×4 of the base count as its own (a loose test that only prevents over-spawning by
        /// maintenance; trucks away on deliveries may be miscounted, but the faction cap
        /// MaxTrucksPerFaction is the final brake).</summary>
        private static int CountTrucksNearBase(WarState state, byte factionId, MilitaryBase b)
        {
            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null || t.Category != UnitCategory.SupplyTruck) continue;
                if (u.Position.HorizontalDistanceTo(b.Position) <= LoadRadius * 4f) count++;
            }
            return count;
        }
    }
}
