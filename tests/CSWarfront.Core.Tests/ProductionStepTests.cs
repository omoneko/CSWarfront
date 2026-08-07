using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

public class ProductionStepTests
{
    private static WarState OneBaseWithQueue()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var b = new MilitaryBase(100, BaseType.Army, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        b.Queue.Add(new ProductionOrder("Tank_T1", 50f, 10f));
        s.Bases.Add(b);
        return s;
    }

    [Fact]
    public void Advance_progresses_but_not_complete_before_buildtime()
    {
        var s = OneBaseWithQueue();
        var done = ProductionStep.Advance(s, 5f); // 5 out of 10 seconds
        Assert.Empty(done);
        Assert.Equal(0.5f, s.Bases[0].Queue[0].Progress, 3);
    }

    [Fact]
    public void Advance_completes_and_dequeues_at_buildtime()
    {
        var s = OneBaseWithQueue();
        var done = ProductionStep.Advance(s, 10f);
        Assert.Single(done);
        Assert.Equal("Tank_T1", done[0].TypeKey);
        Assert.Equal((byte)0, done[0].FactionId);
        Assert.Empty(s.Bases[0].Queue); // already dequeued
    }

    [Fact]
    public void Unowned_base_does_not_produce()
    {
        var s = OneBaseWithQueue();
        s.Bases[0].OwnerFactionId = null;
        var done = ProductionStep.Advance(s, 10f);
        Assert.Empty(done);
    }

    // --- Task78: one cause of "sea units never move toward enemy bases and stay holed up at their own base" —
    // the navy base (BaseType.Navy) building position itself is not on water, so a ship spawned there kept
    // failing the straight-line water check (AdvanceSea) from its very first step and could never move.
    // Nudge SpawnPos onto water.

    private class FakeWaterHalfPlane : IWaterSampler
    {
        private readonly float _waterMinX;
        public FakeWaterHalfPlane(float waterMinX) { _waterMinX = waterMinX; }
        public bool IsWater(float x, float z) { return x >= _waterMinX; }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return IsWater(x, z); }
    }

    private class FakeAlwaysLand : IWaterSampler
    {
        public bool IsWater(float x, float z) { return false; }
        public bool TrySampleWaterLevel(float x, float z, out float level) { level = 0f; return false; }
    }

    private static WarState OneNavyBaseWithQueue()
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        NavalUnitRoster.RegisterAll(s.Types);
        var b = new MilitaryBase(200, BaseType.Navy, new WorldPos(0, 0, 0));
        b.OwnerFactionId = 0;
        b.Queue.Add(new ProductionOrder(NavalUnitRoster.TypeKey(UnitCategory.Destroyer, 1), 50f, 10f));
        s.Bases.Add(b);
        return s;
    }

    [Fact]
    public void Navy_base_not_on_water_spawns_the_unit_nudged_onto_the_nearest_water_point()
    {
        var s = OneNavyBaseWithQueue(); // base sits at (0,0,0), which is land here
        s.Water = new FakeWaterHalfPlane(waterMinX: 50f); // only x>=50 is water

        var done = ProductionStep.Advance(s, 10f);

        Assert.Single(done);
        Assert.True(s.Water.IsWater(done[0].SpawnPos.X, done[0].SpawnPos.Z),
            "spawn position must be nudged onto water so the sea unit's very first movement step is not immediately land-blocked");
    }

    [Fact]
    public void Navy_base_already_on_water_spawns_at_the_base_position_unchanged()
    {
        var s = OneNavyBaseWithQueue(); // base at (0,0,0)
        s.Water = new FakeWaterHalfPlane(waterMinX: -1000f); // everywhere (including the base) is water

        var done = ProductionStep.Advance(s, 10f);

        Assert.Single(done);
        Assert.Equal(0f, done[0].SpawnPos.X, 3);
        Assert.Equal(0f, done[0].SpawnPos.Z, 3);
    }

    [Fact]
    public void Navy_base_with_no_water_within_search_radius_falls_back_to_the_base_position()
    {
        var s = OneNavyBaseWithQueue();
        s.Water = new FakeAlwaysLand(); // nowhere is water, nudging cannot find anywhere better

        var done = ProductionStep.Advance(s, 10f);

        Assert.Single(done);
        Assert.Equal(0f, done[0].SpawnPos.X, 3);
        Assert.Equal(0f, done[0].SpawnPos.Z, 3);
    }

    [Fact]
    public void Army_base_spawn_position_is_never_nudged_by_the_water_sampler()
    {
        var s = OneBaseWithQueue(); // BaseType.Army at (0,0,0)
        s.Water = new FakeWaterHalfPlane(waterMinX: 50f); // (0,0) reads as "land" here, but must not matter

        var done = ProductionStep.Advance(s, 10f);

        Assert.Single(done);
        Assert.Equal(0f, done[0].SpawnPos.X, 3);
        Assert.Equal(0f, done[0].SpawnPos.Z, 3);
    }

    [Fact]
    public void Navy_base_spawn_position_is_unchanged_when_no_water_sampler_is_supplied()
    {
        var s = OneNavyBaseWithQueue();
        // s.Water intentionally left null: matches every other Core step's "no sampler => don't touch it" fallback.

        var done = ProductionStep.Advance(s, 10f);

        Assert.Single(done);
        Assert.Equal(0f, done[0].SpawnPos.X, 3);
        Assert.Equal(0f, done[0].SpawnPos.Z, 3);
    }
}
