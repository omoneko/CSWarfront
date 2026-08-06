namespace CSWarfront.Core
{
    /// <summary>Each faction spends its war chest refilling the production queues of the bases it owns
    /// (pure logic, deterministic). Task46: the selection logic moved into AiProductionPolicy.Decide (the
    /// old rule "buy the most expensive unit currently affordable" caused late-game infantry spam).</summary>
    public static class ProductionPlanning
    {
        public const int QueueCap = 2;             // maximum queue length per base

        /// <summary>Task97 (playtest feedback "the game slows down as fighting progresses"): while a
        /// faction's living unit count is at or above this, its automatic production (queue refills at
        /// AutoProduce bases) stops. Measured growth was unbounded — 690 units total (355 for Blue alone)
        /// — with engagement checks and visual sync dominating the load, hence the cap. Research
        /// investment sits inside this gate and pauses with it, but once attrition drops the count below
        /// the cap, production resumes automatically at the next economy tick. Manual production (UI
        /// orders at AutoProduce=OFF bases) and missile construction are exempt (never obstruct the
        /// player's explicit actions).</summary>
        public const int MaxUnitsPerFaction = 150;

        /// <summary>Cap on decisions per base per tick (defensive). Research investments consume no queue
        /// slot, so in theory the loop could continue for as long as "research keeps being chosen". It is
        /// actually finite because the Treasury shrinks by ResearchInvestPerDecision each time, but the
        /// cap exists as insurance (the same defensive-cap design policy as
        /// WarState.MaxRecentShotsPerTick).</summary>
        private const int MaxDecisionsPerBasePerTick = 20;

        public static void Advance(WarState state)
        {
            // Task97: count living units per faction once (+1 for the Invader faction).
            // Task99: supply trucks have their own cap (SupplyTruckStep.MaxTrucksPerFaction) separate from
            // the 150 combat cap, so they are not counted here.
            var aliveCounts = new int[Faction.InvaderFactionId + 1];
            for (int ui = 0; ui < state.Units.Count; ui++)
            {
                UnitInstance u = state.Units[ui];
                if (!u.IsAlive || u.FactionId >= aliveCounts.Length) continue;
                UnitType ut = state.Types.Get(u.TypeKey);
                if (ut != null && ut.Category == UnitCategory.SupplyTruck) continue;
                aliveCounts[u.FactionId]++;
            }

            for (int fi = 0; fi < state.Factions.Count; fi++)
            {
                Faction f = state.Factions[fi];
                if (f.Eliminated) continue;
                bool atUnitCap = f.Id < aliveCounts.Length && aliveCounts[f.Id] >= MaxUnitsPerFaction;

                for (int bi = 0; bi < state.Bases.Count; bi++)
                {
                    MilitaryBase b = state.Bases[bi];
                    if (b.OwnerFactionId == null || b.OwnerFactionId.Value != f.Id) continue;
                    if (!b.AutoProduce) continue; // Task34: the AI never touches bases the player manages manually

                    // Task103: fortifications and cargo stations are exempt from AI auto-production (a
                    // station's trains come from manual orders plus TrainStep.MaintainTrains; supply depots
                    // etc. produce nothing at all).
                    if (FortificationRules.IsFortification(b.Type)) continue;

                    // Task63: missile bases never use the unit Queue (SpawnableDomains=None, so the
                    // AiProductionPolicy.Decide below could only ever return None anyway). They branch
                    // instead into the simple "start building one missile if none is underway" via
                    // MissileStockpile.
                    if (b.Type == BaseType.MissileBase)
                    {
                        MissileStockpile.TryBuildMissile(state, b.BaseId);
                        continue;
                    }

                    // Task97: factions at the unit cap stop auto-production (see MaxUnitsPerFaction;
                    // missile construction already happened above and is exempt).
                    if (atUnitCap) continue;

                    // Seed derivation: (faction id, base id, running number of decisions made at this base
                    // this tick). Varying the seed per decision even within the same base and tick keeps
                    // the research/production choice from locking into a monotone pattern (Task46).
                    uint decisionCount = 0;
                    for (int attempts = 0; b.Queue.Count < QueueCap && attempts < MaxDecisionsPerBasePerTick; attempts++)
                    {
                        uint seed = MakeSeed(f.Id, b.BaseId, decisionCount);
                        decisionCount++;

                        AiDecision decision = AiProductionPolicy.Decide(state, f, b, seed);
                        if (decision.Choice == AiSpendChoice.None) break; // nothing affordable to buy or invest

                        if (decision.Choice == AiSpendChoice.Research)
                        {
                            // Decide already verified Treasury >= ResearchReserve, so TryInvest normally
                            // succeeds; defensively bail out on false.
                            if (!Research.TryInvest(f, AiProductionPolicy.ResearchInvestPerDecision)) break;
                            Research.TryUnlockNext(f); // unlock the next tier immediately if funded
                            continue; // no queue slot was consumed, so retry the same slot
                        }

                        UnitType type = state.Types.Get(decision.TypeKey);
                        if (type == null) break;
                        // Task61: defensive guard. AiProductionPolicy.Decide already picks only composition
                        // tables matching the base's SpawnableDomains, so this is normally redundant — but
                        // if a future implementation bug ever returned a domain-mismatched TypeKey, the
                        // enqueue itself is blocked here (a second safety net).
                        if (!DomainMaskUtil.Contains(b.SpawnableDomains, type.Domain)) break;
                        // Task99: three-resource payment (manpower + production, shortfall substituted by
                        // funds). The AI preserves its research reserve, so the funds-substitution cap uses
                        // the same "Treasury - research reserve" as Decide's spendCap (no reserve needed
                        // once tier 5 is unlocked = the whole treasury is usable; the same rule as the
                        // spendCap derivation inside Decide).
                        float fundsCap = f.UnlockedTier < 5 ? f.Treasury - AiProductionPolicy.ResearchReserve : f.Treasury;
                        if (fundsCap < 0f) fundsCap = 0f;
                        if (!UnitCosts.TryPay(f, type, fundsCap)) break;
                        b.Queue.Add(new ProductionOrder(type.TypeKey, type.Cost, type.BuildTime));
                    }
                }
            }
        }

        private static uint MakeSeed(byte factionId, ushort baseId, uint decisionCount)
        {
            unchecked
            {
                uint h = factionId;
                h = h * 2654435761u + baseId;
                h = h * 2654435761u + decisionCount;
                return h;
            }
        }
    }
}
