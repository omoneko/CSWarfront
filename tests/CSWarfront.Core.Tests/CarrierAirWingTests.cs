using System.Collections.Generic;
using System.Linq;
using CSWarfront.Core;
using Xunit;

// Task64: CarrierAirWing — each living Carrier maintains its own escort wing of up to WingSize(4)
// Air-domain aircraft, building them one at a time (paid upfront, at build start) and spawning them at
// the carrier's own position. Deterministic (hash-based) type cycling, no System.Random.
public class CarrierAirWingTests
{
    private static WarState WithCarrier(float treasury, byte unlockedTier = 1, bool registerAirTypes = true)
    {
        var s = new WarState();
        var faction = new Faction(0, "Red");
        faction.AddTreasury(treasury);
        // Task99: 3資源経済。人的資源は潤沢・生産力0にして、費用が従来どおり資金残高だけで
        // 決まるようにする（資金費用 = ProductionCost×2 = Cost×0.8×2 = Cost×1.6、Air系share=0.2）。
        faction.AddManpower(1000000f);
        faction.UnlockedTier = unlockedTier;
        s.Factions.Add(faction);

        s.Types.Register(NavalUnitRoster.Get(UnitCategory.Carrier, 1));
        if (registerAirTypes) AirUnitRoster.RegisterAll(s.Types);

        var carrier = new UnitInstance(1, NavalUnitRoster.TypeKey(UnitCategory.Carrier, 1), 0, 520f, new WorldPos(1000, 0, 1000));
        s.Units.Add(carrier);
        s.NextInstanceId = 2;
        return s;
    }

    private static List<UnitInstance> AirUnitsOf(WarState s, byte factionId)
    {
        var result = new List<UnitInstance>();
        foreach (var u in s.Units)
        {
            var t = s.Types.Get(u.TypeKey);
            if (u.FactionId == factionId && t != null && t.Domain == Domain.Air) result.Add(u);
        }
        return result;
    }

    [Fact]
    public void Carrier_builds_up_to_WingSize_aircraft_and_then_stops()
    {
        var s = WithCarrier(100000f);
        var carrier = s.FindUnit(1);

        // dt=20 exceeds every AirUnitRoster Tier1 BuildTime (14/18/6), so each Advance call that starts
        // a build also finishes it within the same tick.
        for (int i = 0; i < 4; i++) CarrierAirWing.Advance(s, 20f);

        Assert.Equal(CarrierAirWing.WingSize, AirUnitsOf(s, 0).Count);

        // A 5th call must not exceed the cap.
        CarrierAirWing.Advance(s, 20f);
        Assert.Equal(CarrierAirWing.WingSize, AirUnitsOf(s, 0).Count);
        Assert.Equal(0f, carrier.CarrierBuildProgress, 3); // not mid-build; simply idle at full wing
    }

    [Fact]
    public void Full_wing_costs_exactly_the_sum_of_one_of_each_cycle_category_at_Tier1()
    {
        var s = WithCarrier(100000f);
        var faction = s.FindFaction(0);

        for (int i = 0; i < 4; i++) CarrierAirWing.Advance(s, 20f);

        // Cycle is AirSuperiority, AirSuperiority, TacticalBomber, SuicideDrone at Tier1:
        // 200 + 200 + 260 + 90 = 750 (AirUnitRoster Tier1 costs).
        // Task99: 生産力0のため資金費用は750×1.6=1200（WithCarrierのコメント参照）。
        Assert.Equal(100000f - 750f * 1.6f, faction.Treasury, 2);
    }

    [Fact]
    public void Cost_is_deducted_at_build_start_not_incrementally_per_tick()
    {
        var s = WithCarrier(100000f);
        var faction = s.FindFaction(0);
        var carrier = s.FindUnit(1);

        // dt=1 is far too small to finish any Tier1 aircraft (min BuildTime=6h), but the very first
        // tick must already pay the full unit cost up front.
        CarrierAirWing.Advance(s, 1f);

        Assert.True(carrier.CarrierBuildProgress > 0f && carrier.CarrierBuildProgress < 1f);
        Assert.Empty(AirUnitsOf(s, 0)); // not finished yet
        Assert.True(faction.Treasury <= 100000f - 90f); // at least the cheapest Tier1 aircraft was already paid
    }

    [Fact]
    public void Respects_UnlockedTier_and_never_builds_above_it()
    {
        var s = WithCarrier(100000f, unlockedTier: 1); // AirUnitRoster.RegisterAll registers Tiers 1..5
        for (int i = 0; i < 4; i++) CarrierAirWing.Advance(s, 20f);

        foreach (var u in AirUnitsOf(s, 0))
        {
            var t = s.Types.Get(u.TypeKey);
            Assert.Equal((byte)1, t.Tier);
        }
    }

    [Fact]
    public void No_build_happens_when_no_category_in_the_cycle_is_registered_at_all()
    {
        var s = WithCarrier(100000f, registerAirTypes: false); // only the Carrier type itself is registered
        var faction = s.FindFaction(0);

        CarrierAirWing.Advance(s, 20f);

        Assert.Empty(AirUnitsOf(s, 0));
        Assert.Equal(100000f, faction.Treasury, 2);
    }

    [Fact]
    public void No_build_when_the_faction_cannot_afford_any_cycle_category()
    {
        // Cheapest Tier1 aircraft (SuicideDrone) costs 90; 50 cannot afford any of the 4 cycle entries.
        var s = WithCarrier(50f);
        var faction = s.FindFaction(0);
        var carrier = s.FindUnit(1);

        CarrierAirWing.Advance(s, 20f);

        Assert.Empty(AirUnitsOf(s, 0));
        Assert.Equal(50f, faction.Treasury, 2);
        Assert.Equal(0f, carrier.CarrierBuildProgress, 3); // waiting, not stuck mid-build

        // Once funded, the very next call starts (and, with a generous dt, finishes) a build.
        faction.AddTreasury(1000f);
        CarrierAirWing.Advance(s, 20f);
        Assert.Single(AirUnitsOf(s, 0));
    }

    [Fact]
    public void New_aircraft_spawn_at_the_carriers_position_with_AiControlled_order()
    {
        var s = WithCarrier(100000f);
        var carrier = s.FindUnit(1);

        CarrierAirWing.Advance(s, 20f);

        var built = Assert.Single(AirUnitsOf(s, 0));
        Assert.Equal(carrier.Position.X, built.Position.X, 3);
        Assert.Equal(carrier.Position.Z, built.Position.Z, 3);
        Assert.Equal(UnitOrder.AiControlled, built.Order);
    }

    [Fact]
    public void Wing_resumes_building_after_a_member_dies()
    {
        var s = WithCarrier(100000f);
        for (int i = 0; i < 4; i++) CarrierAirWing.Advance(s, 20f);
        Assert.Equal(4, AirUnitsOf(s, 0).Count);

        // Kill one wing member.
        var victim = AirUnitsOf(s, 0)[0];
        victim.CurrentHP = 0f;
        victim.State = UnitState.Dead;

        CarrierAirWing.Advance(s, 20f); // wing count is now 3 (<4) -> should build a replacement

        Assert.Equal(4, AirUnitsOf(s, 0).Count(u => u.IsAlive));
    }

    [Fact]
    public void Build_cycle_produces_exactly_two_fighters_one_bomber_one_drone_over_a_full_wing()
    {
        var s = WithCarrier(100000f);
        for (int i = 0; i < 4; i++) CarrierAirWing.Advance(s, 20f);

        var categories = AirUnitsOf(s, 0).Select(u => s.Types.Get(u.TypeKey).Category).OrderBy(c => c.ToString()).ToList();
        var expected = new List<UnitCategory>
        {
            UnitCategory.AirSuperiority, UnitCategory.AirSuperiority,
            UnitCategory.SuicideDrone, UnitCategory.TacticalBomber
        }.OrderBy(c => c.ToString()).ToList();

        Assert.Equal(expected, categories);
    }

    [Fact]
    public void Build_order_is_fully_deterministic_across_separate_identical_runs()
    {
        var s1 = WithCarrier(100000f);
        var order1 = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            CarrierAirWing.Advance(s1, 20f);
            order1 = AirUnitsOf(s1, 0).Select(u => u.TypeKey).ToList();
        }

        var s2 = WithCarrier(100000f); // identical setup: same carrier InstanceId, same faction, same treasury
        var order2 = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            CarrierAirWing.Advance(s2, 20f);
            order2 = AirUnitsOf(s2, 0).Select(u => u.TypeKey).ToList();
        }

        Assert.Equal(order1, order2);
    }

    [Fact]
    public void Existing_nearby_air_units_count_toward_the_wing_even_if_not_carrier_built()
    {
        var s = WithCarrier(100000f);
        var carrier = s.FindUnit(1);
        // Pre-seed 4 friendly Air-domain units already near the carrier (e.g. from a land airbase).
        for (uint i = 2; i < 6; i++)
            s.Units.Add(new UnitInstance(i, AirUnitRoster.TypeKey(UnitCategory.AirSuperiority, 1), 0, 120f, carrier.Position));

        var faction = s.FindFaction(0);
        CarrierAirWing.Advance(s, 20f);

        Assert.Equal(4, AirUnitsOf(s, 0).Count); // still just the 4 pre-seeded ones
        Assert.Equal(100000f, faction.Treasury, 2); // nothing spent, wing already full
    }

    [Fact]
    public void Dead_carriers_do_not_build()
    {
        var s = WithCarrier(100000f);
        var carrier = s.FindUnit(1);
        carrier.CurrentHP = 0f;
        carrier.State = UnitState.Dead;

        CarrierAirWing.Advance(s, 20f);

        Assert.Empty(AirUnitsOf(s, 0));
    }
}
