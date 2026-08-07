namespace CSWarfront.Core
{
    public class Faction
    {
        /// <summary>Task95: the fixed id of "Invader", the faction dedicated to outside-incursion
        /// events. A sixth faction placed beyond the player factions (0..4), special-cased so that
        /// (1) RelationMatrix/ThreatRelations hardcode this id as permanently Hostile (it stays hostile
        /// no matter what the Options do), (2) FactionStatus.Refresh excludes it from the Eliminated
        /// derivation (owning zero bases is its normal state; formerly it got flagged Eliminated → was
        /// dropped from AI advances → invasion forces froze at their spawn point, the root cause of the
        /// in-game bug), and (3) it never appears in the construction faction dropdown or the relations
        /// UI.</summary>
        public const byte InvaderFactionId = 5;

        public byte Id { get; private set; }
        public string Name { get; set; }
        public float Treasury { get; private set; }
        public ushort? HomeBaseId { get; set; }
        public bool IsPlayer { get; set; }
        public bool Eliminated { get; set; }

        /// <summary>Research points. Added by kill rewards (Research.KillReward) and cash investment
        /// (Research.TryInvest); Research.TryUnlockNext spends them as the cost of unlocking tiers
        /// (Task35).</summary>
        public float ResearchPoints;

        /// <summary>The highest unlocked production tier (1..5). Defaults to 1 (the land roster's
        /// lowest tier). AiProductionPolicy.Decide (which replaced the old
        /// ProductionPlanning.ChooseUnitKey in Task46) / ManualProduction.TryEnqueue can never select or
        /// order a unit above this tier (Task35).</summary>
        public byte UnlockedTier = 1;

        public Faction(byte id, string name) { Id = id; Name = name; UnlockedTier = 1; }

        // --- Task99: the three-resource economy (manpower/production; money = the existing Treasury
        // pool). Income: each economy tick, from per-zone development within 1km of a base (residential
        // → Manpower, commercial/office → Treasury, industrial → Production;
        // TerritoryIncome.ZonedForBase). Spending: unit/supply-truck production and supplies (see
        // UnitCosts/ResupplyStep; research and missiles still draw on Treasury).

        /// <summary>Manpower (produced from residential-district development; the personnel cost of
        /// unit production).</summary>
        public float Manpower { get; private set; }

        /// <summary>Production capacity (produced from industrial-district development; funds unit
        /// equipment costs and supplies).</summary>
        public float Production { get; private set; }

        /// <summary>The supply stockpile (a faction-wide pool, capped at
        /// ResupplyStep.SupplyStockCap). Auto-produced from Production each economy tick
        /// (ResupplyStep.ProduceSupplies); in-zone auto-resupply and supply-truck loading spend it.
        /// Persisted since v9.</summary>
        public float SupplyStock { get; private set; }

        public void AddSupply(float amount)
        {
            if (amount > 0f) SupplyStock += amount;
        }

        public bool TrySpendSupply(float amount)
        {
            if (amount < 0f || SupplyStock < amount) return false;
            SupplyStock -= amount;
            return true;
        }

        public void AddTreasury(float amount) { if (amount > 0f) Treasury += amount; }

        public void AddManpower(float amount) { if (amount > 0f) Manpower += amount; }

        public void AddProduction(float amount) { if (amount > 0f) Production += amount; }

        public bool TrySpendManpower(float amount)
        {
            if (amount < 0f || Manpower < amount) return false;
            Manpower -= amount;
            return true;
        }

        public bool TrySpendProduction(float amount)
        {
            if (amount < 0f || Production < amount) return false;
            Production -= amount;
            return true;
        }

        /// <summary>Adds research points. Non-positive amounts are ignored (the same convention as
        /// AddTreasury, Task35).</summary>
        public void AddResearchPoints(float amount) { if (amount > 0f) ResearchPoints += amount; }

        public bool TrySpend(float amount)
        {
            if (amount < 0f || Treasury < amount) return false;
            Treasury -= amount;
            return true;
        }
    }
}
