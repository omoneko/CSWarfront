using CSWarfront.Core;
using Xunit;

// Task80: AiProductionPolicy was fully upgraded from a fixed-target-ratio-only policy to a
// game-theory-based policy of observe (enemy composition) -> payoff (expected effect E[c]) ->
// hedge (mixed strategy). This file contains:
//   - Existing tests confirming that peacetime behaviour (not a single hostile unit present)
//     remains unchanged from the old Task46 logic (the "Composition", "Research", and "Bootstrap"
//     sections below; kept as-is because nothing changed between old and new).
//   - New tests confirming the new best-response behaviour while at war (enemy composition is
//     observable) (the "Wartime scoring" section).
//   - Tests confirming that tier selection changed from "always the highest tier" to a
//     cost-effectiveness hedge (the "Tier hedging" section; 3 of the old tests had their premises
//     invalidated by this change and were rewritten — the old-vs-new assertion contrast is kept in
//     the comments).
public class AiProductionPolicyTests
{
    private static WarState WithFullRoster(float treasury, byte unlockedTier)
    {
        var s = new WarState();
        LandUnitRoster.RegisterAll(s.Types);
        var f = new Faction(0, "Red");
        f.AddTreasury(treasury);
        // Task99: three-resource economy. The tests in this file verify the semantics of the funds
        // budget (spendCap), so manpower is always abundant and production is 0 — the full production
        // cost is then substituted with funds (x FundsPerProduction), and affordability is still
        // determined by the funds balance alone as before (thresholds are pre-converted in each test).
        f.AddManpower(1000000f);
        f.UnlockedTier = unlockedTier;
        s.Factions.Add(f);
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);
        return s;
    }

    private static void AddLivingUnits(WarState s, UnitCategory category, byte tier, int count)
    {
        string key = LandUnitRoster.TypeKey(category, tier);
        for (int i = 0; i < count; i++)
        {
            s.Units.Add(new UnitInstance(s.AllocInstanceId(), key, 0, 100f, new WorldPos(0, 0, 0)));
        }
    }

    /// <summary>Task80: for wartime scenarios. Builds a WarState with faction0 (Red, the subject
    /// under test) and faction1 (Blue, hostile). Registers both the Land and Air rosters, so enemy
    /// units can also use air categories.</summary>
    private static WarState WithHostileEnemy(float treasury, byte unlockedTier, Relation relation = Relation.Hostile)
    {
        var s = new WarState();
        LandUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);
        var red = new Faction(0, "Red");
        red.AddTreasury(treasury);
        red.AddManpower(1000000f); // Task99: same rationale as WithFullRoster (see comment there)
        red.UnlockedTier = unlockedTier;
        s.Factions.Add(red);
        var blue = new Faction(1, "Blue");
        s.Factions.Add(blue);
        s.Relations.Set(0, 1, relation);
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);
        return s;
    }

    private static void AddEnemyUnits(WarState s, byte factionId, UnitCategory category, byte tier, int count)
    {
        string key = category + "_T" + tier; // Format shared by LandUnitRoster/AirUnitRoster/NavalUnitRoster
        for (int i = 0; i < count; i++)
            s.Units.Add(new UnitInstance(s.AllocInstanceId(), key, factionId, 100f, new WorldPos(0, 0, 0)));
    }

    // --- Composition (peacetime fallback, unchanged since Task46): largest deficit wins ---

    [Fact]
    public void Fresh_faction_with_no_units_and_plenty_of_money_produces_a_tank_not_infantry()
    {
        // UnlockedTier=5 removes the research reserve/consideration entirely so this test isolates
        // the composition rule (Tank has the highest target share, 30%, of any tracked category).
        // Task80: no hostile units exist in this WarState, so mix.HasHostiles==false and the
        // peacetime fixed-target fallback (identical to the pre-Task80 rule) governs this test.
        var s = WithFullRoster(10000f, unlockedTier: 5);
        AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed: 12345u);

        Assert.Equal(AiSpendChoice.Produce, d.Choice);
        UnitType chosen = s.Types.Get(d.TypeKey);
        Assert.NotNull(chosen);
        Assert.Equal(UnitCategory.Tank, chosen.Category);
    }

    [Fact]
    public void After_several_tanks_the_ai_switches_to_the_most_deficient_remaining_category()
    {
        var s = WithFullRoster(10000f, unlockedTier: 5);
        // Tank share is already far above its 30% target; Infantry is close to its target too,
        // leaving MechInfantry (0 units, 20% target) as the clear largest remaining deficit.
        AddLivingUnits(s, UnitCategory.Tank, 1, 10);
        AddLivingUnits(s, UnitCategory.Infantry, 1, 3);

        AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed: 777u);

        Assert.Equal(AiSpendChoice.Produce, d.Choice);
        UnitType chosen = s.Types.Get(d.TypeKey);
        Assert.NotNull(chosen);
        Assert.Equal(UnitCategory.MechInfantry, chosen.Category);
    }

    [Fact]
    public void Category_choice_never_picks_AntiAir_in_peacetime()
    {
        // Task80: AntiAir is no longer hard-excluded from candidates in general (see the Wartime
        // scoring section below, where it DOES get picked once the enemy fields air units) - but
        // the peacetime fixed-target composition table still omits it (a faction facing no threat
        // has no reason to build reactive anti-air gear). This test now documents that narrower,
        // intentional scope instead of the old blanket "never" claim.
        var s = WithFullRoster(10000f, unlockedTier: 5);
        for (uint seed = 0; seed < 200; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            UnitType chosen = s.Types.Get(d.TypeKey);
            Assert.NotEqual(UnitCategory.AntiAir, chosen.Category);
        }
    }

    // --- Wartime scoring (Task80: observe -> payoff -> hedge) ---

    [Fact]
    public void Enemy_mix_heavy_in_infantry_favours_apc_or_artillery_over_many_seeds()
    {
        // Worked math (pure Infantry enemy mix, share=1.0): E[Apc]=Multiplier(Apc,Infantry)=1.4 times
        // survivability(Apc)=1/(1+Multiplier(Infantry,Apc)/2)=1/(1+0.3)=0.769 -> E[Apc]~1.08;
        // E[Artillery]=1.6*1/(1+0.65)=1.6*0.606~0.97; both rank above Tank(~0.92), MechInfantry(~0.83),
        // Infantry(~0.67), DroneInfantry(~0.38), AntiAir(~0.31). Apc/Artillery should dominate the
        // hedge draw (both land in the top-3 by score).
        var s = WithHostileEnemy(10000f, unlockedTier: 5);
        AddEnemyUnits(s, 1, UnitCategory.Infantry, 1, 50);

        int apcOrArtillery = 0, total = 0;
        for (uint seed = 0; seed < 400; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            total++;
            UnitCategory cat = s.Types.Get(d.TypeKey).Category;
            if (cat == UnitCategory.Apc || cat == UnitCategory.Artillery) apcOrArtillery++;
        }
        Assert.True(total > 100, "expected most decisions to produce");
        Assert.True(apcOrArtillery > total / 2,
            $"expected Apc/Artillery to dominate a heavy-Infantry matchup, got {apcOrArtillery}/{total}");
    }

    [Fact]
    public void Enemy_mix_heavy_in_tanks_makes_drone_infantry_the_dominant_pick()
    {
        // Worked math (pure Tank enemy mix): DroneInfantry counters Tank at 2.0x (its designed
        // anti-tank role) and only takes 1.1x back from Tank, so E[DroneInfantry]~1.29 vs the next
        // best (Tank countering itself at 1.0x, ~0.67). DroneInfantry should clearly dominate.
        var s = WithHostileEnemy(10000f, unlockedTier: 5);
        AddEnemyUnits(s, 1, UnitCategory.Tank, 1, 50);

        int drone = 0, total = 0;
        for (uint seed = 0; seed < 400; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            total++;
            if (s.Types.Get(d.TypeKey).Category == UnitCategory.DroneInfantry) drone++;
        }
        Assert.True(total > 100, "expected most decisions to produce");
        Assert.True(drone > total / 2,
            $"expected DroneInfantry to dominate a heavy-Tank matchup, got {drone}/{total}");
    }

    [Fact]
    public void Enemy_fielding_air_units_brings_AntiAir_into_the_mix()
    {
        // Task80: the old rule hard-excluded AntiAir from every candidate list ("no air units exist
        // yet"). Air units exist now (Task61), and CombatMatchup.AntiAir vs AirSuperiority=2.5 was
        // already on the books - this test proves that simply admitting AntiAir back into the
        // wartime candidate set is enough for it to emerge on its own merits (no special-casing
        // needed in the scoring formula itself).
        var s = WithHostileEnemy(10000f, unlockedTier: 5);
        AddEnemyUnits(s, 1, UnitCategory.AirSuperiority, 1, 30);

        bool antiAirEverProduced = false;
        for (uint seed = 0; seed < 200; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            if (s.Types.Get(d.TypeKey).Category == UnitCategory.AntiAir) antiAirEverProduced = true;
        }
        Assert.True(antiAirEverProduced, "expected AntiAir to be produced against an all-air enemy");
    }

    [Fact]
    public void Category_hedge_visits_the_top_three_candidates_with_the_top_scorer_most_often()
    {
        // Same heavy-Infantry scenario as above (top-3 by E[] = Apc > Artillery > Tank). Verifies
        // the "hedge" itself, not just the aggregate Apc-or-Artillery dominance: every one of the
        // top-3 candidates must appear at least once across enough seeds (never a pure argmax-only
        // strategy), and the single best responder (Apc) must be the single most frequent pick.
        var s = WithHostileEnemy(10000f, unlockedTier: 5);
        AddEnemyUnits(s, 1, UnitCategory.Infantry, 1, 50);

        var counts = new System.Collections.Generic.Dictionary<UnitCategory, int>();
        int total = 0;
        for (uint seed = 0; seed < 600; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            total++;
            UnitCategory cat = s.Types.Get(d.TypeKey).Category;
            counts.TryGetValue(cat, out int c);
            counts[cat] = c + 1;
        }

        Assert.True(counts.ContainsKey(UnitCategory.Apc) && counts[UnitCategory.Apc] > 0);
        Assert.True(counts.ContainsKey(UnitCategory.Artillery) && counts[UnitCategory.Artillery] > 0);
        Assert.True(counts.ContainsKey(UnitCategory.Tank) && counts[UnitCategory.Tank] > 0);

        int apcCount = counts[UnitCategory.Apc];
        foreach (var kv in counts)
            Assert.True(apcCount >= kv.Value, $"expected Apc (best responder) to be the most frequent pick, but {kv.Key}={kv.Value} > Apc={apcCount}");
    }

    // --- Tier hedging (Task80: replaces "always highest affordable Tier") ---

    [Fact]
    public void Tier_choice_never_exceeds_UnlockedTier()
    {
        var s = WithFullRoster(10000f, unlockedTier: 3);
        for (uint seed = 0; seed < 200; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            UnitType chosen = s.Types.Get(d.TypeKey);
            Assert.True(chosen.Tier <= 3);
        }
    }

    [Fact]
    public void Tier_choice_never_exceeds_the_research_reserve_floor_below_max_tier()
    {
        // OLD test ("Tier_choice_respects_research_reserve_floor_below_max_tier") asserted a single
        // exact tier (T3) because the old rule always bought the highest affordable tier. Task80
        // replaces that determinism with a cost-effectiveness hedge, so this test instead checks the
        // invariant that must ALWAYS still hold: the reserve floor caps spendCap at
        // Treasury-ResearchReserve, so no tier costing more than that cap is ever chosen, and the
        // category is still Tank (composition target unchanged, no hostiles observed here).
        // Task99 conversion: production is 0, so a unit's funds cost = ProductionCost x 2 = Cost x 0.7 x 2 = Cost x 1.4.
        // Funds cost of Tank_T3 = 132 x 1.4 = 184.8 <= spendCap (334.8 - 150 = 184.8).
        // Tank_T4 = 168 x 1.4 = 235.2 > 184.8, so it must never appear.
        var s = WithFullRoster(334.8f, unlockedTier: 4);
        AddLivingUnits(s, UnitCategory.DroneInfantry, 1, 1); // non-bootstrap: reserve applies
        bool sawT1Through3 = false;
        for (uint seed = 0; seed < 500; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            UnitType chosen = s.Types.Get(d.TypeKey);
            Assert.Equal(UnitCategory.Tank, chosen.Category);
            Assert.True(chosen.Cost <= 132.01f, $"cost {chosen.Cost} exceeded the reserve-capped budget");
            Assert.True(chosen.Tier <= 3);
            if (chosen.Tier >= 1 && chosen.Tier <= 3) sawT1Through3 = true;
        }
        Assert.True(sawT1Through3);
    }

    [Fact]
    public void At_max_tier_the_whole_treasury_is_available_no_reserve_held_back()
    {
        // OLD test asserted exactly Tier5 (the old "always max tier" rule). Task80's hedge normally
        // prefers cheaper tiers, so a literal "always T5" assertion no longer holds.
        // Task99 conversion: production is 0, so funds cost = Cost x 1.4. Treasury=100 exactly covers
        // Tank_T1 (60 x 1.4 = 84), but if a regression wrongly deducted the research reserve even at
        // max tier, spendCap = 100 - 150 < 0 and every decision would be None. The mere fact that
        // something is produced proves "with Tier5 unlocked, the entire treasury is spendable".
        var s = WithFullRoster(100f, unlockedTier: 5);
        UnitType chosen = FindFirstProducedType(s, maxSeed: 2000);
        Assert.NotNull(chosen);
        Assert.Equal(UnitCategory.Tank, chosen.Category);
        Assert.Equal((byte)1, chosen.Tier); // With 100 funds, only T1 (84) is affordable
    }

    [Fact]
    public void Tier_hedge_produces_both_the_top_scoring_tier_and_lower_tiers_across_seeds()
    {
        // unlockedTier=3 with a huge treasury makes Tank_T1/T2/T3 all affordable (candidates=3, so
        // the hedge's top-3 window covers the ENTIRE candidate set) - this is the cleanest way to
        // observe "quality vs quantity" mixing: AiCombatScoring.TierValue = sqrt(HP*Attack)/Cost
        // decreases with Tier for this roster (Cost grows 60%/Tier, sqrt(HP*Attack) only ~37.5%/Tier),
        // so T1 should be the most frequent pick, but T3 (this scenario's "max tier") must still show
        // up sometimes - proving the AI no longer mechanically maxes out every single decision.
        var s = WithFullRoster(10000f, unlockedTier: 3);
        var tierCounts = new System.Collections.Generic.Dictionary<byte, int>();
        for (uint seed = 0; seed < 400; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            UnitType chosen = s.Types.Get(d.TypeKey);
            Assert.Equal(UnitCategory.Tank, chosen.Category);
            tierCounts.TryGetValue(chosen.Tier, out int c);
            tierCounts[chosen.Tier] = c + 1;
        }

        Assert.True(tierCounts.ContainsKey(1) && tierCounts[1] > 0, "expected Tier1 (best cost-effectiveness) to appear");
        Assert.True(tierCounts.ContainsKey(3) && tierCounts[3] > 0, "expected Tier3 (this scenario's max tier) to appear at least once");
        int t1 = tierCounts.TryGetValue(1, out int v1) ? v1 : 0;
        foreach (var kv in tierCounts)
            Assert.True(t1 >= kv.Value, $"expected Tier1 to be the most frequent pick, but Tier{kv.Key}={kv.Value} > Tier1={t1}");
    }

    [Fact]
    public void Tier_hedge_never_picks_an_unaffordable_tier()
    {
        // Bootstrap faction (0 units, so the whole treasury is spendable): Treasury=90 sits strictly
        // between Tank_T1 (60, affordable) and Tank_T2 (96, NOT affordable). Only one candidate tier
        // exists, so the hedge is fully constrained: every decision that produces must be exactly T1.
        var s = WithFullRoster(90f, unlockedTier: 5);
        for (uint seed = 0; seed < 300; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            UnitType chosen = s.Types.Get(d.TypeKey);
            Assert.Equal(UnitCategory.Tank, chosen.Category);
            Assert.Equal((byte)1, chosen.Tier);
        }
    }

    // --- Task61: unit-category composition depending on the base's Domain ---

    [Fact]
    public void Navy_base_only_ever_chooses_naval_categories()
    {
        var s = new WarState();
        NavalUnitRoster.RegisterAll(s.Types);
        var f = new Faction(0, "Red");
        f.AddTreasury(10000f);
        f.AddManpower(1000000f); // Task99: same rationale as WithFullRoster
        f.UnlockedTier = 5;
        s.Factions.Add(f);
        var b = new MilitaryBase(100, BaseType.Navy, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        bool everProduced = false;
        for (uint seed = 0; seed < 200; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, f, b, seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            everProduced = true;
            UnitType chosen = s.Types.Get(d.TypeKey);
            Assert.NotNull(chosen);
            Assert.Equal(Domain.Sea, chosen.Domain);
            Assert.True(chosen.Category == UnitCategory.Destroyer || chosen.Category == UnitCategory.Carrier);
        }
        Assert.True(everProduced);
    }

    [Fact]
    public void AirForce_base_only_ever_chooses_air_categories()
    {
        var s = new WarState();
        AirUnitRoster.RegisterAll(s.Types);
        var f = new Faction(0, "Red");
        f.AddTreasury(10000f);
        f.AddManpower(1000000f); // Task99: same rationale as WithFullRoster
        f.UnlockedTier = 5;
        s.Factions.Add(f);
        var b = new MilitaryBase(100, BaseType.AirForce, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        bool everProduced = false;
        for (uint seed = 0; seed < 200; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, f, b, seed);
            if (d.Choice != AiSpendChoice.Produce) continue;
            everProduced = true;
            UnitType chosen = s.Types.Get(d.TypeKey);
            Assert.NotNull(chosen);
            Assert.Equal(Domain.Air, chosen.Domain);
        }
        Assert.True(everProduced);
    }

    private static UnitType FindFirstProducedType(WarState s, uint maxSeed)
    {
        for (uint seed = 0; seed < maxSeed; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice == AiSpendChoice.Produce) return s.Types.Get(d.TypeKey);
        }
        return null;
    }

    // --- Determinism / affordability edge cases ---

    [Fact]
    public void Decide_is_reproducible_for_the_same_seed()
    {
        var s = WithFullRoster(10000f, unlockedTier: 3);
        AiDecision first = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed: 42u);
        for (int i = 0; i < 10; i++)
        {
            AiDecision again = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed: 42u);
            Assert.Equal(first.Choice, again.Choice);
            Assert.Equal(first.TypeKey, again.TypeKey);
        }
    }

    [Fact]
    public void Decide_is_reproducible_for_the_same_seed_in_a_wartime_scenario()
    {
        // Task80: determinism must hold in the new wartime (hedge) path too, not just peacetime.
        var s = WithHostileEnemy(10000f, unlockedTier: 5);
        AddEnemyUnits(s, 1, UnitCategory.Tank, 1, 20);
        AiDecision first = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed: 123u);
        for (int i = 0; i < 10; i++)
        {
            AiDecision again = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed: 123u);
            Assert.Equal(first.Choice, again.Choice);
            Assert.Equal(first.TypeKey, again.TypeKey);
        }
    }

    [Fact]
    public void Nothing_is_chosen_when_treasury_is_zero()
    {
        var s = WithFullRoster(0f, unlockedTier: 3);
        for (uint seed = 0; seed < 50; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            Assert.Equal(AiSpendChoice.None, d.Choice);
        }
    }

    // --- Task46 bugfix: early-game stall (cross-category fallback + bootstrap exemption) ---
    // Task80: unchanged in intent - the peacetime path still runs the exact old logic, and the
    // wartime path reuses the SAME cross-category fallback loop (just walking a hedge-ordered
    // preference list instead of a pure-deficit one). These tests stay as regression guards.

    [Fact]
    public void Fresh_faction_bootstraps_production_instead_of_stalling_on_the_research_reserve()
    {
        var s = WithFullRoster(200f, unlockedTier: 1);
        bool everProduced = false;
        for (uint seed = 0; seed < 300; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            Assert.NotEqual(AiSpendChoice.None, d.Choice);
            if (d.Choice != AiSpendChoice.Produce) continue;
            everProduced = true;
            UnitType chosen = s.Types.Get(d.TypeKey);
            Assert.NotNull(chosen);
        }
        Assert.True(everProduced, "expected at least one seed to choose Produce (not just Research)");
    }

    [Fact]
    public void Faction_with_units_falls_back_to_a_cheaper_category_when_the_top_pick_is_unaffordable()
    {
        var s = WithFullRoster(200f, unlockedTier: 1);
        AddLivingUnits(s, UnitCategory.MechInfantry, 1, 1);

        UnitType chosen = FindFirstProducedType(s, maxSeed: 2000);
        Assert.NotNull(chosen);
        Assert.Equal(UnitCategory.Infantry, chosen.Category);
        Assert.Equal((byte)1, chosen.Tier);
    }

    [Fact]
    public void None_is_still_returned_when_nothing_is_affordable_with_units_present()
    {
        var s = WithFullRoster(140f, unlockedTier: 1);
        AddLivingUnits(s, UnitCategory.MechInfantry, 1, 1);
        for (uint seed = 0; seed < 200; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            Assert.Equal(AiSpendChoice.None, d.Choice);
        }
    }

    // --- Research (Task80: situational formula replaces the flat 35%) ---

    [Fact]
    public void Research_is_chosen_sometimes_but_not_always_when_behind_and_affordable()
    {
        var s = WithFullRoster(200f, unlockedTier: 1); // Treasury >= ResearchReserve(150)
        int researchCount = 0;
        int total = 1000;
        for (uint seed = 0; seed < total; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice == AiSpendChoice.Research) researchCount++;
        }

        Assert.True(researchCount > 0, "expected research to be chosen for at least some seeds");
        Assert.True(researchCount < total, "expected research to NOT be chosen for at least some seeds");
    }

    [Fact]
    public void Research_is_never_chosen_when_already_at_max_tier()
    {
        var s = WithFullRoster(10000f, unlockedTier: 5);
        for (uint seed = 0; seed < 500; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            Assert.NotEqual(AiSpendChoice.Research, d.Choice);
        }
    }

    [Fact]
    public void Research_is_never_chosen_when_treasury_below_reserve()
    {
        var s = WithFullRoster(149f, unlockedTier: 1); // just under ResearchReserve(150)
        for (uint seed = 0; seed < 500; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            Assert.NotEqual(AiSpendChoice.Research, d.Choice);
        }
    }

    [Fact]
    public void Research_is_chosen_more_often_when_behind_the_strongest_hostile_faction_in_tier()
    {
        // Both scenarios: Red has plenty of treasury/reserve headroom and Tier1 units (so the
        // bootstrap exemption doesn't apply and behindTiers is measurable). The only difference is
        // whether Blue (hostile) fields high-tier units. Research should be picked more often when
        // Red is clearly behind.
        var caughtUp = WithHostileEnemy(2000f, unlockedTier: 4);
        AddLivingUnits(caughtUp, UnitCategory.Infantry, 1, 1);
        AddEnemyUnits(caughtUp, 1, UnitCategory.Tank, 1, 5); // Blue avg tier = 1, same as Red

        var behind = WithHostileEnemy(2000f, unlockedTier: 4);
        AddLivingUnits(behind, UnitCategory.Infantry, 1, 1);
        AddEnemyUnits(behind, 1, UnitCategory.Tank, 5, 5); // Blue avg tier = 5, Red is far behind

        int total = 1500;
        int caughtUpResearch = CountResearch(caughtUp, total);
        int behindResearch = CountResearch(behind, total);

        Assert.True(behindResearch > caughtUpResearch,
            $"expected the behind-in-tier faction to research more often ({behindResearch} vs {caughtUpResearch})");
    }

    [Fact]
    public void Research_is_chosen_less_often_when_badly_outnumbered()
    {
        var evenMatch = WithHostileEnemy(2000f, unlockedTier: 4);
        AddLivingUnits(evenMatch, UnitCategory.Infantry, 1, 10);
        AddEnemyUnits(evenMatch, 1, UnitCategory.Tank, 1, 10); // 1:1

        var outnumbered = WithHostileEnemy(2000f, unlockedTier: 4);
        AddLivingUnits(outnumbered, UnitCategory.Infantry, 1, 5);
        AddEnemyUnits(outnumbered, 1, UnitCategory.Tank, 1, 20); // 4:1, well past the 2:1 cutoff

        int total = 1500;
        int evenResearch = CountResearch(evenMatch, total);
        int outnumberedResearch = CountResearch(outnumbered, total);

        Assert.True(outnumberedResearch < evenResearch,
            $"expected the outnumbered faction to research less often ({outnumberedResearch} vs {evenResearch})");
    }

    [Fact]
    public void Research_probability_never_exceeds_its_documented_bounds()
    {
        // Extreme "should research a lot" scenario (way behind, wealthy, not outnumbered) and
        // extreme "should barely research" scenario (badly outnumbered) must both stay within the
        // documented [0.05, 0.6] clamp - i.e. never 0% and never anywhere near 100%.
        var wantsResearch = WithHostileEnemy(10000f, unlockedTier: 4);
        AddLivingUnits(wantsResearch, UnitCategory.Infantry, 1, 5);
        AddEnemyUnits(wantsResearch, 1, UnitCategory.Tank, 5, 5);

        var avoidsResearch = WithHostileEnemy(2000f, unlockedTier: 4);
        AddLivingUnits(avoidsResearch, UnitCategory.Infantry, 1, 2);
        AddEnemyUnits(avoidsResearch, 1, UnitCategory.Tank, 1, 40);

        int total = 2000;
        int high = CountResearch(wantsResearch, total);
        int low = CountResearch(avoidsResearch, total);

        Assert.True(high < total * 0.65f, $"expected the upper clamp (~0.6) to hold, got {high}/{total}");
        Assert.True(low > total * 0.02f, $"expected the lower clamp (~0.05) to hold, got {low}/{total}");
    }

    private static int CountResearch(WarState s, int totalSeeds)
    {
        int count = 0;
        for (uint seed = 0; seed < (uint)totalSeeds; seed++)
        {
            AiDecision d = AiProductionPolicy.Decide(s, s.Factions[0], s.Bases[0], seed);
            if (d.Choice == AiSpendChoice.Research) count++;
        }
        return count;
    }
}
