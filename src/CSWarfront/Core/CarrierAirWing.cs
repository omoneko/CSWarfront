namespace CSWarfront.Core
{
    /// <summary>
    /// Task64 (carrier air wings): carriers (UnitCategory.Carrier) are UnitInstances, not bases
    /// (MilitaryBase), so instead of the ProductionOrder/ProductionStep queue machinery a dedicated tick
    /// manages each carrier's escort air wing directly. Ground rules:
    ///
    ///  - Each living carrier counts the faction's living Air-domain units within WingRadius of itself
    ///    and builds aircraft one at a time while below WingSize (aircraft from other sources — flown in
    ///    from a land AirForce base — count toward the wing too: the carrier only wants "WingSize
    ///    Air-domain craft nearby" and does not care who built them).
    ///  - The build cost is charged in one lump via Faction.TrySpend at the moment
    ///    UnitInstance.CarrierBuildProgress moves off 0 (= construction starts); no per-tick installments.
    ///    If unaffordable, nothing happens and the next tick retries (the same "pay on start, wait and
    ///    retry" policy as MissileStockpile).
    ///  - The category built follows the fixed 4-entry cycle AirSuperiority, AirSuperiority,
    ///    TacticalBomber, SuicideDrone, rotated per carrier by a hash of "the carrier's id + its
    ///    cumulative build count (UnitInstance.CarrierBuildCounter)" (no System.Random; deterministic).
    ///    Fighters take 2 of the 4 slots, giving a fighter-heavy composition.
    ///  - The tier actually built per category is the highest registered tier at or below the faction's
    ///    UnlockedTier (the same idea as AiProductionPolicy.ChooseHighestAffordableTier; the budget test
    ///    already happened at start-of-build, so no cost condition here — only the tier narrowing).
    ///    If the category has nothing registered at all (roster gap / future category), do nothing.
    ///  - A finished airframe spawns at the carrier's current position and inherits
    ///    UnitOrder.AiControlled (the default), riding the free-movement logic of air units so it can
    ///    roam with the fleet.
    ///
    /// No UnityEngine dependency. Intended to be called from MilitaryManager.OnSimTick right after
    /// ProductionStep.Advance.
    /// </summary>
    public static class CarrierAirWing
    {
        /// <summary>Horizontal distance from the carrier within which a craft counts as part of the wing.</summary>
        public const float WingRadius = 250f;

        /// <summary>Wing size to maintain; construction continues only while below this.</summary>
        public const int WingSize = 4;

        /// <summary>The fixed build cycle (Task64 spec: 2 fighters, 1 bomber, 1 suicide drone).</summary>
        private static readonly UnitCategory[] BuildCycle =
        {
            UnitCategory.AirSuperiority, UnitCategory.AirSuperiority,
            UnitCategory.TacticalBomber, UnitCategory.SuicideDrone
        };

        /// <summary>Tiny positive value written into CarrierBuildProgress the moment a build starts.
        /// Purely a marker distinguishing it from 0f (not building); the following dt increments overwrite
        /// it naturally (the same technique as MissileStockpile.StartProgress).</summary>
        private const float StartProgress = 0.0001f;

        public static void Advance(WarState state, float dt)
        {
            for (int i = 0; i < state.Units.Count; i++)
            {
                UnitInstance carrier = state.Units[i];
                if (!carrier.IsAlive) continue;

                UnitType carrierType = state.Types.Get(carrier.TypeKey);
                if (carrierType == null || carrierType.Category != UnitCategory.Carrier) continue;

                if (CountNearbyAirUnits(state, carrier) >= WingSize)
                {
                    carrier.CarrierBuildProgress = 0f; // wing complete: leave no half-started progress behind
                    continue;
                }

                Faction faction = state.FindFaction(carrier.FactionId);
                if (faction == null) continue;

                UnitCategory category = NextBuildCategory(carrier);
                UnitType buildType = HighestUnlocked(state, category, faction.UnlockedTier);
                if (buildType == null) continue; // nothing unlocked/registered in this category yet

                if (carrier.CarrierBuildProgress <= 0f)
                {
                    // Build start: charge the cost in one lump. If unaffordable, do nothing and retry next
                    // tick (the same policy as MissileStockpile).
                    // Task99: three-resource payment (manpower + production; shortfall substituted by
                    // funds, capped at the full treasury).
                    if (!UnitCosts.TryPay(faction, buildType, faction.Treasury)) continue;
                    carrier.CarrierBuildProgress = StartProgress;
                }

                if (buildType.BuildTime <= 0f) carrier.CarrierBuildProgress = 1f;
                else carrier.CarrierBuildProgress += dt / buildType.BuildTime;

                if (carrier.CarrierBuildProgress >= 1f)
                {
                    uint id = state.AllocInstanceId();
                    var aircraft = new UnitInstance(id, buildType.TypeKey, carrier.FactionId, buildType.MaxHP, carrier.Position);
                    aircraft.Order = UnitOrder.AiControlled;
                    state.Units.Add(aircraft);

                    carrier.CarrierBuildProgress = 0f;
                    carrier.CarrierBuildCounter++;
                }
            }
        }

        /// <summary>Counts the carrier faction's living Air-domain units within WingRadius of the carrier
        /// (whether the carrier built them itself does not matter).</summary>
        private static int CountNearbyAirUnits(WarState state, UnitInstance carrier)
        {
            int count = 0;
            for (int j = 0; j < state.Units.Count; j++)
            {
                UnitInstance u = state.Units[j];
                if (!u.IsAlive || u.FactionId != carrier.FactionId) continue;

                UnitType t = state.Types.Get(u.TypeKey);
                if (t == null || t.Domain != Domain.Air) continue;

                if (carrier.Position.HorizontalDistanceTo(u.Position) > WingRadius) continue;
                count++;
            }
            return count;
        }

        /// <summary>Picks a per-carrier starting offset deterministically from the hash of
        /// carrier.InstanceId, then walks BuildCycle one entry at a time by adding CarrierBuildCounter
        /// (= a strict "cycle": four consecutive builds consume each of BuildCycle's four entries exactly
        /// once, in a rotation that differs per carrier). The same carrier and build count always yields
        /// the same result (no System.Random; the same MurmurHash3-finalizer technique as
        /// AiProductionPolicy.Hash).</summary>
        private static UnitCategory NextBuildCategory(UnitInstance carrier)
        {
            uint offset = Hash(carrier.InstanceId) % (uint)BuildCycle.Length;
            uint idx = (offset + carrier.CarrierBuildCounter) % (uint)BuildCycle.Length;
            return BuildCycle[idx];
        }

        /// <summary>Returns the highest registered tier of the category at or below unlockedTier.
        /// Null when none qualifies (the same shape as AiProductionPolicy.ChooseHighestAffordableTier,
        /// but without the cost condition — the build cost was already paid at start).</summary>
        private static UnitType HighestUnlocked(WarState state, UnitCategory category, byte unlockedTier)
        {
            UnitType best = null;
            foreach (UnitType t in state.Types.All())
            {
                if (t.Category != category) continue;
                if (t.Tier > unlockedTier) continue;
                if (best == null || t.Tier > best.Tier) best = t;
            }
            return best;
        }

        /// <summary>Deterministic integer hash (MurmurHash3 finalizer equivalent; identical technique to
        /// AiProductionPolicy.Hash/BallisticMissiles.Hash).</summary>
        private static uint Hash(uint x)
        {
            unchecked
            {
                x ^= x >> 16;
                x *= 0x7feb352dU;
                x ^= x >> 15;
                x *= 0x846ca68bU;
                x ^= x >> 16;
                return x;
            }
        }
    }
}
