namespace CSWarfront.Core
{
    /// <summary>
    /// Task101: the transport helicopter logistics loop (design §2). The airborne counterpart of the
    /// trucks (SupplyTruckStep), plus infantry airlift.
    ///
    /// The cycle (stateless per-tick derivation; direct flight, no roads needed):
    ///   1. Empty: return to the home base (nearest friendly army base) and, inside its radius, load
    ///      supplies (CargoSupply from the faction pool = SupplyLoad 1.0) and board up to MaxPassengers
    ///      idle infantry-type units in the radius
    ///   2. Loaded: fly straight to the destination (the nearest friendly SupplyDepot with stock room;
    ///      otherwise the nearest "engaging friendly land unit" = the front line)
    ///   3. Arrival (DropRadius): unload supplies into the depot's StoredSupplies (on a front-line drop
    ///      the supplies stay aboard and return), disembark the infantry (deterministic scatter,
    ///      State=Idle → the next AssignAdvance re-tasks them)
    ///   4. When empty, return to the home base
    ///
    /// Carrying (CarriedByUnitId): carried units are excluded from every step and cannot be targeted.
    /// This step synchronizes their position every tick, and a downed helicopter takes them with it
    /// (silent removal = cargo and passengers lost). Maintenance mirrors the trucks (one per army base,
    /// faction cap MaxHelisPerFaction, MaintainHelis on the economy tick, Invaders excluded). Helicopters
    /// under a player's Hold/RallyHold order are never touched.
    /// </summary>
    public static class TransportHeliStep
    {
        public const int MaxHelisPerFaction = 6;
        public const int HelisPerArmyBase = 1;

        /// <summary>Supplies a full load (SupplyLoad=1) carries (twice a truck's 30).</summary>
        public const float CargoSupply = 60f;

        public const int MaxPassengers = 3;

        /// <summary>Loading/boarding radius at the home base (same as the base supply zone).</summary>
        public const float PickupRadius = 200f;

        /// <summary>Arrival radius at the destination = unload/disembark test.</summary>
        public const float DropRadius = 60f;

        /// <summary>Scatter radius on disembark.</summary>
        public const float DisembarkScatter = 20f;

        /// <summary>Per economy tick: army bases auto-maintain transport helicopters (same shape as
        /// SupplyTruckStep.MaintainTrucks).</summary>
        public static void MaintainHelis(WarState state)
        {
            UnitType heliType = state.Types.Get(AirUnitRoster.TypeKey(UnitCategory.TransportHelicopter, 1));
            if (heliType == null) return;

            for (int fi = 0; fi < state.Factions.Count; fi++)
            {
                Faction f = state.Factions[fi];
                if (f.Eliminated || f.Id == Faction.InvaderFactionId) continue;

                int helis = CountHelis(state, f.Id);
                if (helis >= MaxHelisPerFaction) continue;

                for (int bi = 0; bi < state.Bases.Count && helis < MaxHelisPerFaction; bi++)
                {
                    MilitaryBase b = state.Bases[bi];
                    if (b.OwnerFactionId == null || b.OwnerFactionId.Value != f.Id) continue;
                    if (b.Type != BaseType.Army) continue;
                    if (CountHelisNearBase(state, f.Id, b) >= HelisPerArmyBase) continue;
                    if (!UnitCosts.TryPay(f, heliType, f.Treasury)) break;

                    var u = new UnitInstance(state.AllocInstanceId(), heliType.TypeKey, f.Id, heliType.MaxHP,
                        new WorldPos(b.Position.X, b.Position.Y, b.Position.Z));
                    u.State = UnitState.Idle;
                    state.Units.Add(u);
                    helis++;
                }
            }
        }

        public static void Advance(WarState state, float dt)
        {
            // 1) Position sync for carried units, and shared fate when the carrier dies (train passengers
            //    are handled here in one place too — CarriedByUnitId is a common mechanism regardless of
            //    carrier kind).
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.CarriedByUnitId.HasValue) continue;
                UnitInstance carrier = state.FindUnit(u.CarriedByUnitId.Value);
                if (carrier == null || !carrier.IsAlive)
                {
                    // Shared fate (silent removal: no KillEvent — the same policy as stuck cleanup).
                    u.CurrentHP = 0f;
                    u.State = UnitState.Dead;
                    u.CarriedByUnitId = null;
                    continue;
                }
                u.Position = carrier.Position;
            }

            // 2) The transport helicopters' own cycle.
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance heli = state.Units[i];
                if (!heli.IsAlive || heli.IsCarried) continue;
                UnitType type = state.Types.Get(heli.TypeKey);
                if (type == null || type.Category != UnitCategory.TransportHelicopter) continue;
                if (heli.Order == UnitOrder.Hold || heli.Order == UnitOrder.RallyHold) continue;

                MilitaryBase home = FindNearestOwnArmyBase(state, heli);
                if (home == null) { Wait(heli); continue; }

                int passengers = CountPassengers(state, heli.InstanceId);
                bool hasCargo = heli.SupplyLoad > 0f || passengers > 0;

                if (!hasCargo)
                {
                    AdvanceEmptyHeli(state, heli, home, passengers);
                    continue;
                }

                AdvanceLoadedHeli(state, heli, home, passengers);
            }
        }

        private static void AdvanceEmptyHeli(WarState state, UnitInstance heli, MilitaryBase home, int passengers)
        {
            if (heli.Position.HorizontalDistanceTo(home.Position) > PickupRadius)
            {
                FlyTo(heli, home.Position);
                return;
            }

            Faction f = state.FindFaction(heli.FactionId);

            // Load supplies (infantry transport still works even with an empty pool).
            if (f != null && f.SupplyStock > 0f)
            {
                float loadable = f.SupplyStock / CargoSupply;
                float load = loadable < 1f ? loadable : 1f;
                f.TrySpendSupply(load * CargoSupply);
                heli.SupplyLoad = load;
                heli.SupplyLoadFromDepot = false;
            }

            // Board infantry (idle infantry-types inside the base radius; AiControlled/FreeAdvance only).
            int boarded = passengers;
            for (int i = 0; i < state.Units.Count && boarded < MaxPassengers; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.IsCarried || u.InstanceId == heli.InstanceId) continue;
                if (u.FactionId != heli.FactionId) continue;
                if (u.State != UnitState.Idle) continue;
                if (u.Order != UnitOrder.AiControlled && u.Order != UnitOrder.FreeAdvance) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null || (t.Category != UnitCategory.Infantry && t.Category != UnitCategory.MechInfantry)) continue;
                if (u.Position.HorizontalDistanceTo(heli.Position) > PickupRadius) continue;

                u.CarriedByUnitId = heli.InstanceId;
                u.OrderTargetPos = null;
                u.ClearPath();
                boarded++;
            }

            Wait(heli); // nothing to load = wait; if loaded, next tick's AdvanceLoadedHeli picks a destination
        }

        private static void AdvanceLoadedHeli(WarState state, UnitInstance heli, MilitaryBase home, int passengers)
        {
            // Destination: nearest depot with stock room → a fortification low on ammo (Task111) →
            // the nearest engaging friendly land unit (the front line).
            MilitaryBase depot = FindNearestDepotWithRoom(state, heli);
            MilitaryBase fort = depot == null ? FindNearestFortNeedingAmmo(state, heli) : null;
            WorldPos? dest = depot != null ? depot.Position : (fort != null ? fort.Position : (WorldPos?)null);
            if (!dest.HasValue) dest = FindFrontline(state, heli);

            if (!dest.HasValue)
            {
                // Task111 (Workshop report "every transport helicopter lands and stays there forever"):
                // previously the helicopter "waited in place with the cargo aboard", so the moment a depot
                // filled up, helicopters that lost their destination mid-air landed in the field and never
                // moved again. Cargo with no delivery target is now brought home and returned to the
                // faction pool (infantry disembark at the home base too). The helicopter thus always comes
                // home, and re-sorties on the next cycle once demand appears (depot room opens up / a
                // fortification runs low).
                if (heli.Position.HorizontalDistanceTo(home.Position) > PickupRadius)
                {
                    FlyTo(heli, home.Position);
                    return;
                }
                if (passengers > 0) Disembark(state, heli);
                if (heli.SupplyLoad > 0f)
                {
                    Faction f = state.FindFaction(heli.FactionId);
                    if (f != null) f.AddSupply(heli.SupplyLoad * CargoSupply);
                    heli.SupplyLoad = 0f;
                }
                Wait(heli);
                return;
            }

            if (heli.Position.HorizontalDistanceTo(dest.Value) > DropRadius)
            {
                FlyTo(heli, dest.Value);
                return;
            }

            // Arrival: unload supplies at a depot; transfer straight into fort ammo (Task111); infantry
            // disembark at any destination.
            if (depot != null && heli.SupplyLoad > 0f)
            {
                float cap = FortificationRules.StoredSupplyCap(depot.Type);
                float room = cap - depot.StoredSupplies;
                float carried = heli.SupplyLoad * CargoSupply;
                float transfer = carried < room ? carried : room;
                depot.StoredSupplies += transfer;
                heli.SupplyLoad -= transfer / CargoSupply;
                if (heli.SupplyLoad < 0.001f) heli.SupplyLoad = 0f;
            }
            else if (fort != null && heli.SupplyLoad > 0f)
            {
                float need = 1f - fort.FortAmmo;
                float carriedReloads = heli.SupplyLoad * CargoSupply / ResupplyStep.SupplyPerFullReload;
                float refill = need < carriedReloads ? need : carriedReloads;
                fort.FortAmmo += refill;
                if (fort.FortAmmo > 1f) fort.FortAmmo = 1f;
                heli.SupplyLoad -= refill * ResupplyStep.SupplyPerFullReload / CargoSupply;
                if (heli.SupplyLoad < 0.001f) heli.SupplyLoad = 0f;
            }
            Disembark(state, heli);
            Wait(heli); // if now empty, next tick's AdvanceEmptyHeli sends it home
        }

        /// <summary>Task111: the nearest of the faction's operational fortifications (bunker/artillery
        /// position) with ammo below SupplyTruckStep.NeedThreshold. Null when none. Unlike the trucks, no
        /// auto-resupply-zone exclusion (airlift is fast, so responsiveness beats the occasional
        /// duplicated delivery).</summary>
        private static MilitaryBase FindNearestFortNeedingAmmo(WarState state, UnitInstance heli)
        {
            MilitaryBase best = null;
            float bestDist = float.MaxValue;
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase fort = state.Bases[b];
                if (!FortificationRules.IsArmedFortification(fort.Type)) continue; // Task118
                if (fort.OwnerFactionId == null || fort.OwnerFactionId.Value != heli.FactionId) continue;
                if (fort.CurrentHP <= 0f) continue;
                if (fort.FortAmmo >= SupplyTruckStep.NeedThreshold) continue;
                float d = heli.Position.HorizontalDistanceTo(fort.Position);
                if (d < bestDist) { bestDist = d; best = fort; }
            }
            return best;
        }

        /// <summary>Disembarks the passengers (deterministic scatter around the helicopter, State=Idle →
        /// the next AssignAdvance re-tasks them).</summary>
        private static void Disembark(WarState state, UnitInstance heli)
        {
            int n = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.CarriedByUnitId.HasValue || u.CarriedByUnitId.Value != heli.InstanceId) continue;
                // Deterministic scatter: a ring layout stepped 90 degrees in boarding order.
                float ox = (n % 2 == 0 ? 1f : -1f) * DisembarkScatter * ((n / 2) + 1) * 0.5f;
                float oz = (n % 4 < 2 ? 1f : -1f) * DisembarkScatter * 0.5f;
                u.CarriedByUnitId = null;
                u.Position = new WorldPos(heli.Position.X + ox, heli.Position.Y, heli.Position.Z + oz);
                u.State = UnitState.Idle;
                n++;
            }
        }

        private static WorldPos? FindFrontline(WarState state, UnitInstance heli)
        {
            UnitInstance best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.IsCarried || u.FactionId != heli.FactionId) continue;
                if (u.State != UnitState.Engaging) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null || t.Domain != Domain.Land) continue;
                float d = heli.Position.HorizontalDistanceTo(u.Position);
                if (d < bestDist) { bestDist = d; best = u; }
            }
            return best != null ? best.Position : (WorldPos?)null;
        }

        private static void FlyTo(UnitInstance heli, WorldPos dest)
        {
            heli.OrderTargetPos = dest;
            heli.State = UnitState.Moving;
        }

        private static void Wait(UnitInstance heli)
        {
            heli.OrderTargetPos = null;
            heli.ClearPath();
            heli.State = UnitState.Idle;
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

        private static int CountPassengers(WarState state, uint heliId)
        {
            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
                if (state.Units[i].CarriedByUnitId.HasValue && state.Units[i].CarriedByUnitId.Value == heliId) count++;
            return count;
        }

        private static int CountHelis(WarState state, byte factionId)
        {
            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t != null && t.Category == UnitCategory.TransportHelicopter) count++;
            }
            return count;
        }

        private static int CountHelisNearBase(WarState state, byte factionId, MilitaryBase b)
        {
            int count = 0;
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.FactionId != factionId) continue;
                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null || t.Category != UnitCategory.TransportHelicopter) continue;
                if (u.Position.HorizontalDistanceTo(b.Position) <= PickupRadius * 4f) count++;
            }
            return count;
        }
    }
}
