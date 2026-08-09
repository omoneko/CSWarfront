namespace CSWarfront.Core
{
    /// <summary>
    /// Task99: automatic supply production and in-base-zone auto-resupply (design:
    /// 2026-08-03-economy-supply-design.md §4.1-4.2).
    ///
    /// Supplies (Faction.SupplyStock) are a faction-wide pool. Every economy tick, ProduceSupplies
    /// auto-produces into the stock from Production (with funds substitution when short, at the same rate
    /// as UnitCosts.FundsPerProduction).
    ///
    /// Advance (every tick) refills the Ammo of friendly units (ammo-bound ones only) within
    /// ResupplyRadius of a friendly base at RefillPerHour, consuming SupplyStock. Living carriers act as
    /// resupply points under the same conditions for aircraft only (rearming the wing at the mothership).
    /// Refills stop when the stock runs dry. The Invader faction has infinite ammo (AmmoRules), so its
    /// Ammo never drops and it naturally falls outside this step.
    /// </summary>
    public static class ResupplyStep
    {
        /// <summary>Supply stock cap.</summary>
        public const float SupplyStockCap = 1000f;

        /// <summary>Maximum supplies auto-produced per economy tick (as long as Production lasts).</summary>
        public const float SupplyPerEconomyTick = 50f;

        /// <summary>Production cost per unit of supplies.</summary>
        public const float ProductionPerSupply = 1f;

        /// <summary>Auto-resupply radius around bases/carriers (m).</summary>
        public const float ResupplyRadius = 200f;

        /// <summary>Ammo refill rate inside a supply zone (fraction per in-game hour).</summary>
        public const float RefillPerHour = 0.25f;

        /// <summary>Supplies consumed to fill Ammo from 0 to 1.</summary>
        public const float SupplyPerFullReload = 10f;

        /// <summary>Per economy tick: auto-produces supplies from Production (funds substitute for the
        /// shortfall). Production amount is the smaller of SupplyPerEconomyTick and the room left below
        /// the cap. When the funding runs out, only that much is produced (partial production).</summary>
        public static void ProduceSupplies(Faction f)
        {
            if (f == null || f.Id == Faction.InvaderFactionId) return;

            float want = SupplyPerEconomyTick;
            float room = SupplyStockCap - f.SupplyStock;
            if (room < want) want = room;
            if (want <= 0f) return;

            // Pay from Production as far as it goes; substitute funds for the rest (the same priority as
            // UnitCosts.TryPay).
            float fromProduction = f.Production < want * ProductionPerSupply ? f.Production : want * ProductionPerSupply;
            float produced = fromProduction / ProductionPerSupply;
            f.TrySpendProduction(fromProduction);

            float shortfall = want - produced;
            if (shortfall > 0f)
            {
                float fundsAffordable = f.Treasury / (ProductionPerSupply * UnitCosts.FundsPerProduction);
                float fromFunds = shortfall < fundsAffordable ? shortfall : fundsAffordable;
                if (fromFunds > 0f)
                {
                    f.TrySpend(fromFunds * ProductionPerSupply * UnitCosts.FundsPerProduction);
                    produced += fromFunds;
                }
            }

            f.AddSupply(produced);
        }

        /// <summary>Every tick: refills the ammo of friendly units inside supply zones (see the class
        /// comment).</summary>
        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance u = state.Units[i];
                if (!u.IsAlive || u.Ammo >= 1f) continue;
                if (u.IsCarried) continue; // Task101: carried units are exempt (refilled after disembarking)

                UnitType type = state.Types.Get(u.TypeKey);
                if (type == null || type.AmmoCombatHours <= 0f) continue; // not ammo-bound
                // Task100: Invaders can never use the supply network (living-off-the-land: they recover
                // only through kills, AmmoRules.RewardInvaderKill). No refills even inside the zone of a
                // captured base.
                if (u.FactionId == Faction.InvaderFactionId) continue;

                Faction f = state.FindFaction(u.FactionId);
                if (f == null) continue;

                // Task101: the source is either a normal base / carrier (consuming the faction pool) or a
                // supply depot (consuming its stock).
                MilitaryBase depot;
                if (!TryFindResupplySource(state, u, type, out depot)) continue;
                float available = depot != null ? depot.StoredSupplies : f.SupplyStock;
                if (available <= 0f) continue;

                float refill = RefillPerHour * dt;
                if (refill > 1f - u.Ammo) refill = 1f - u.Ammo;

                // With insufficient supplies, refill only as much as can be paid (partial refill).
                float supplyCost = refill * SupplyPerFullReload;
                if (supplyCost > available)
                {
                    supplyCost = available;
                    refill = supplyCost / SupplyPerFullReload;
                }
                if (depot != null) depot.StoredSupplies -= supplyCost;
                else f.TrySpendSupply(supplyCost);
                u.Ammo += refill;
                if (u.Ammo > 1f) u.Ammo = 1f;
            }

            // Task101: fortification (Bunker/ArtilleryPost) ammo recovery. An operational fortification
            // refills from a supply source within ResupplyRadius of its own position (a normal base = the
            // faction pool, or a stocked depot) at the same rate and cost as units.
            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase fort = state.Bases[b];
                if (!FortificationRules.IsArmedFortification(fort.Type)) continue; // Task118
                if (fort.OwnerFactionId == null || fort.CurrentHP <= 0f || fort.FortAmmo >= 1f) continue;

                Faction f = state.FindFaction(fort.OwnerFactionId.Value);
                if (f == null) continue;

                MilitaryBase source = null;
                bool nearNormalBase = false;
                for (int k = 0; k < state.Bases.Count; k++)
                {
                    MilitaryBase mb = state.Bases[k];
                    if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != fort.OwnerFactionId.Value) continue;
                    if (mb.BaseId == fort.BaseId) continue;
                    if (fort.Position.HorizontalDistanceTo(mb.Position) > ResupplyRadius) continue;
                    if (!FortificationRules.IsFortification(mb.Type)) { nearNormalBase = true; break; }
                    if (mb.Type == BaseType.SupplyDepot && mb.StoredSupplies > 0f && source == null) source = mb;
                }
                float fortAvailable = nearNormalBase ? f.SupplyStock : (source != null ? source.StoredSupplies : 0f);
                if (fortAvailable <= 0f) continue;

                float fortRefill = RefillPerHour * dt;
                if (fortRefill > 1f - fort.FortAmmo) fortRefill = 1f - fort.FortAmmo;
                float fortCost = fortRefill * SupplyPerFullReload;
                if (fortCost > fortAvailable)
                {
                    fortCost = fortAvailable;
                    fortRefill = fortCost / SupplyPerFullReload;
                }
                if (nearNormalBase) f.TrySpendSupply(fortCost);
                else source.StoredSupplies -= fortCost;
                fort.FortAmmo += fortRefill;
                if (fort.FortAmmo > 1f) fort.FortAmmo = 1f;
            }
        }

        /// <summary>Whether the unit is within ResupplyRadius of a friendly supply point (the consumption
        /// source is not distinguished — the simplified test used by the trucks' "no delivery needed
        /// inside a base zone" check).</summary>
        public static bool IsNearResupplyPoint(WarState state, UnitInstance u, UnitType type)
        {
            MilitaryBase depot;
            return TryFindResupplySource(state, u, type, out depot);
        }

        /// <summary>Task101: finds a supply source within ResupplyRadius. Priority:
        /// ① the four normal base types (faction-pool consumption, depot=null) ② for air units only, a
        /// friendly carrier (same, depot=null) ③ an operational (owned) SupplyDepot with stock (stock
        /// consumption, returned via depot). Cargo stations, bunkers, trenches etc. are not auto-resupply
        /// points. False when none is found.</summary>
        public static bool TryFindResupplySource(WarState state, UnitInstance u, UnitType type, out MilitaryBase depot)
        {
            depot = null;
            MilitaryBase nearestDepot = null;
            float nearestDepotDist = float.MaxValue;

            for (int b = 0; b < state.Bases.Count; b++)
            {
                MilitaryBase mb = state.Bases[b];
                if (mb.OwnerFactionId == null || mb.OwnerFactionId.Value != u.FactionId) continue;
                float d = u.Position.HorizontalDistanceTo(mb.Position);
                if (d > ResupplyRadius) continue;

                if (!FortificationRules.IsFortification(mb.Type)) return true; // a normal base = the faction pool
                if (mb.Type == BaseType.SupplyDepot && mb.StoredSupplies > 0f && d < nearestDepotDist)
                {
                    nearestDepotDist = d;
                    nearestDepot = mb;
                }
            }

            if (type.Domain == Domain.Air)
            {
                for (int i = 0; i < state.Units.Count; i++)
                {
                    UnitInstance other = state.Units[i];
                    if (!other.IsAlive || other.FactionId != u.FactionId || other.InstanceId == u.InstanceId) continue;
                    UnitType otherType = state.Types.Get(other.TypeKey);
                    if (otherType == null || otherType.Category != UnitCategory.Carrier) continue;
                    if (u.Position.HorizontalDistanceTo(other.Position) <= ResupplyRadius) return true;
                }
            }

            if (nearestDepot != null) { depot = nearestDepot; return true; }
            return false;
        }
    }
}
