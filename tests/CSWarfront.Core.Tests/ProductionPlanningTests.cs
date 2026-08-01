using CSWarfront.Core;
using Xunit;

public class ProductionPlanningTests
{
    // Task46: unit selection moved from "cheapest/strongest affordable overall" (ChooseUnitKey) into
    // AiProductionPolicy.Decide (composition targets + research investment). These tests focus on the
    // Advance() wiring: does it call Decide for each empty queue slot, spend/enqueue correctly, and
    // respect QueueCap / AutoProduce / Eliminated. AiProductionPolicy's own selection rules (composition,
    // research probability, tier/reserve gating, determinism) are covered by AiProductionPolicyTests.

    [Fact]
    public void Advance_spends_treasury_and_fills_queue_to_cap()
    {
        // UnlockedTier=5 removes the research reserve/consideration so this test isolates the
        // spend+enqueue wiring deterministically (only Tank_T1 is registered, so it's the only
        // possible pick regardless of composition targets).
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions[0].AddTreasury(200f);
        s.Factions[0].UnlockedTier = 5;
        s.Types.Register(MvpUnitTypes.Tank_T1()); // Cost 60
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        ProductionPlanning.Advance(s);

        Assert.Equal(2, s.Bases[0].Queue.Count);
        Assert.Equal("Tank_T1", s.Bases[0].Queue[0].TypeKey);
        Assert.Equal("Tank_T1", s.Bases[0].Queue[1].TypeKey);
        Assert.Equal(80f, s.Factions[0].Treasury, 3); // 200 - 2*60
    }

    [Fact]
    public void Advance_stops_producing_at_the_per_faction_unit_cap()
    {
        // Task97: 生存ユニットがMaxUnitsPerFaction以上の勢力は自動生産を止める（重さ対策）。
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions[0].AddTreasury(10000f);
        s.Factions[0].UnlockedTier = 5;
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);
        for (uint i = 0; i < ProductionPlanning.MaxUnitsPerFaction; i++)
            s.Units.Add(new UnitInstance(1000 + i, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));

        ProductionPlanning.Advance(s);

        Assert.Empty(s.Bases[0].Queue);
        Assert.Equal(10000f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void Advance_resumes_producing_when_units_are_lost_below_the_cap()
    {
        // Task97: 上限を1体でも割れば（死亡ユニットは数えない）自動生産が再開する。
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions[0].AddTreasury(10000f);
        s.Factions[0].UnlockedTier = 5;
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);
        for (uint i = 0; i < ProductionPlanning.MaxUnitsPerFaction; i++)
            s.Units.Add(new UnitInstance(1000 + i, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0)));
        s.Units[0].CurrentHP = 0f;
        s.Units[0].State = UnitState.Dead; // 1体撃破され149体 → 上限未満

        ProductionPlanning.Advance(s);

        Assert.Equal(2, s.Bases[0].Queue.Count);
    }

    [Fact]
    public void Advance_does_not_queue_when_faction_broke()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        ProductionPlanning.Advance(s);

        Assert.Empty(s.Bases[0].Queue);
        Assert.Equal(0f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void Advance_skips_eliminated_faction()
    {
        var s = new WarState();
        var f = new Faction(0, "Red");
        f.Eliminated = true;
        f.AddTreasury(200f);
        s.Factions.Add(f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);

        ProductionPlanning.Advance(s);

        Assert.Empty(s.Bases[0].Queue);
        Assert.Equal(200f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void Advance_respects_queue_cap()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions[0].AddTreasury(500f);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f));
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f));
        s.Bases.Add(b);

        ProductionPlanning.Advance(s);

        Assert.Equal(2, s.Bases[0].Queue.Count);
        Assert.Equal(500f, s.Factions[0].Treasury, 3);
    }

    // --- Full roster / composition-driven selection (Task46) ---

    private static WarState WithFullRoster(float treasury, byte unlockedTier = 5)
    {
        var s = new WarState();
        LandUnitRoster.RegisterAll(s.Types);
        var f = new Faction(0, "Red");
        f.AddTreasury(treasury);
        f.UnlockedTier = unlockedTier;
        s.Factions.Add(f);
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        s.Bases.Add(b);
        return s;
    }

    [Fact]
    public void Advance_with_full_roster_and_tiny_treasury_queues_nothing()
    {
        var s = WithFullRoster(5f);
        ProductionPlanning.Advance(s);
        Assert.Empty(s.Bases[0].Queue);
        Assert.Equal(5f, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void Advance_with_full_roster_and_empty_army_fills_both_slots_with_tank()
    {
        // Task46: an AI starting with no units targets Tank first (largest deficit vs its 30% target).
        // Both empty slots get filled in the same Advance() call since queuing alone doesn't spawn
        // units, so the composition tally is identical for slot 1 and slot 2.
        var s = WithFullRoster(1000f, unlockedTier: 5);
        ProductionPlanning.Advance(s);

        Assert.Equal(2, s.Bases[0].Queue.Count);
        foreach (var order in s.Bases[0].Queue)
        {
            UnitType t = s.Types.Get(order.TypeKey);
            Assert.Equal(UnitCategory.Tank, t.Category);
        }
    }

    [Fact]
    public void Advance_with_full_roster_and_UnlockedTier1_never_queues_above_tier1()
    {
        var s = WithFullRoster(10000f, unlockedTier: 1);
        ProductionPlanning.Advance(s);

        Assert.NotEmpty(s.Bases[0].Queue);
        foreach (var order in s.Bases[0].Queue)
        {
            UnitType t = s.Types.Get(order.TypeKey);
            Assert.Equal((byte)1, t.Tier);
        }
    }

    [Fact]
    public void Advance_can_invest_in_research_when_behind_and_affordable()
    {
        // AiProductionPolicy.Decide's research roll depends on a seed Advance() derives internally
        // (faction/base/decision index), so we search across a bounded range of baseIds for one whose
        // seed sequence happens to roll research on the very first decision. This is a deterministic,
        // bounded search at test time (not runtime randomness) that proves Advance actually wires
        // Research.TryInvest through, rather than re-testing the probability itself (AiProductionPolicyTests
        // already covers that the ~35% chance exists and respects UnlockedTier/reserve).
        bool foundResearch = false;
        for (ushort baseId = 100; baseId < 600 && !foundResearch; baseId++)
        {
            var s = WithFullRoster(200f, unlockedTier: 1); // Treasury >= ResearchReserve(150)
            s.Bases[0].BaseId = baseId;

            ProductionPlanning.Advance(s);

            if (s.Factions[0].ResearchPoints > 0f) foundResearch = true;
        }

        Assert.True(foundResearch, "expected at least one baseId/seed combination to trigger a research investment");
    }

    // --- Task34: AutoProduce=false opts a base out of AI auto-fill ---

    [Fact]
    public void Advance_skips_base_with_AutoProduce_false_while_other_bases_still_fill()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions[0].AddTreasury(200f);
        s.Factions[0].UnlockedTier = 5; // isolate the AutoProduce wiring from research non-determinism
        s.Types.Register(MvpUnitTypes.Tank_T1());

        var playerControlled = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        playerControlled.OwnerFactionId = 0;
        playerControlled.AutoProduce = false;
        s.Bases.Add(playerControlled);

        var aiControlled = new MilitaryBase(101, BaseType.Army, new WorldPos(10, 0, 0));
        aiControlled.OwnerFactionId = 0;
        s.Bases.Add(aiControlled);

        ProductionPlanning.Advance(s);

        Assert.Empty(playerControlled.Queue);
        Assert.Equal(2, aiControlled.Queue.Count);
        Assert.Equal(80f, s.Factions[0].Treasury, 3); // 200 - 2*60, spent only on the AI base
    }

    // --- Task63: MissileBase branches into MissileStockpile instead of the unit Queue ---

    [Fact]
    public void Advance_starts_a_missile_build_for_MissileBase_instead_of_queuing_units()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions[0].AddTreasury(1000f);
        var missileBase = new MilitaryBase(300, BaseType.MissileBase, new WorldPos(0, 0, 0));
        missileBase.OwnerFactionId = 0;
        s.Bases.Add(missileBase);

        ProductionPlanning.Advance(s);

        Assert.Empty(missileBase.Queue); // never touches the unit queue
        Assert.True(MissileStockpile.IsBuilding(missileBase));
        Assert.Equal(1000f - MissileStockpile.MissileCost, s.Factions[0].Treasury, 3);
    }

    [Fact]
    public void Advance_does_not_start_a_second_missile_build_while_one_is_in_progress()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Factions[0].AddTreasury(1000f);
        var missileBase = new MilitaryBase(300, BaseType.MissileBase, new WorldPos(0, 0, 0));
        missileBase.OwnerFactionId = 0;
        s.Bases.Add(missileBase);

        ProductionPlanning.Advance(s);
        float afterFirst = s.Factions[0].Treasury;
        ProductionPlanning.Advance(s); // called again in a later tick, still building

        Assert.Equal(afterFirst, s.Factions[0].Treasury, 3); // no second spend
    }
}
