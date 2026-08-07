using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

/// <summary>
/// Tests for UnitSpatialGrid (Task97: spatial-grid acceleration of engagement checks, the O(N^2)
/// countermeasure). The core property is that the results are exactly identical to the brute-force
/// TargetSearch.FindNearestHostile (preserving the deterministic simulation, including the
/// lowest-list-index tie-break).
/// </summary>
public class UnitSpatialGridTests
{
    /// <summary>Scatters coordinates via an fmix32-style deterministic hash (no RNG in tests, the Core-wide policy).</summary>
    private static float Hash01(uint a, uint b)
    {
        unchecked
        {
            uint h = a * 2654435761u + b;
            h ^= h >> 16; h *= 0x85ebca6bu; h ^= h >> 13; h *= 0xc2b2ae35u; h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    /// <summary>A scattered state including multiple factions, a nemesis relation, dead units, cell-boundary straddling, and negative coordinates.</summary>
    private static WarState ScatteredState(int unitCount)
    {
        var s = new WarState();
        for (byte i = 0; i < 5; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 5);
        s.Relations.Set(0, 1, Relation.Nemesis);
        s.Relations.Set(2, 3, Relation.Neutral); // mix in a non-hostile pair too
        s.Types.Register(MvpUnitTypes.Tank_T1());

        for (uint i = 0; i < (uint)unitCount; i++)
        {
            float x = (Hash01(i, 1u) - 0.5f) * 4000f; // ±2000m (spans far beyond the 256m cell size)
            float z = (Hash01(i, 2u) - 0.5f) * 4000f;
            byte faction = (byte)(Hash01(i, 3u) * 5f);
            var u = new UnitInstance(100 + i, "Tank_T1", faction, 100f, new WorldPos(x, 0, z));
            if (Hash01(i, 4u) < 0.15f) { u.CurrentHP = 0f; u.State = UnitState.Dead; } // mix in dead units too
            s.Units.Add(u);
        }
        return s;
    }

    [Fact]
    public void Grid_search_matches_linear_search_exactly()
    {
        var s = ScatteredState(120);
        s.UnitGrid.Build(s.Units);

        float[] ranges = { 60f, 250f, 600f, 5000f }; // within-cell, cross-cell, and wide-area cases
        foreach (float range in ranges)
        {
            for (int i = 0; i < s.Units.Count; i++)
            {
                UnitInstance self = s.Units[i];
                if (!self.IsAlive) continue;

                UnitInstance linear = TargetSearch.FindNearestHostile(self, s.Units, s.Relations, range,
                    DomainMask.All, s.Types);
                UnitInstance grid = TargetSearch.FindNearestHostile(self, s.UnitGrid, s.Relations, range,
                    DomainMask.All, s.Types);

                Assert.Same(linear, grid);
            }
        }
    }

    [Fact]
    public void Grid_search_prefers_the_lower_list_index_on_distance_ties()
    {
        // Two equidistant enemies (equally far to the east and west of self, spaced far enough
        // apart to land in different cells).
        // The brute-force version prefers the lowest list index = returns the one added first.
        // The grid version must behave the same.
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var self = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        var east = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(300f, 0, 0));
        var west = new UnitInstance(3, "Tank_T1", 1, 100f, new WorldPos(-300f, 0, 0));
        s.Units.Add(self); s.Units.Add(east); s.Units.Add(west);
        s.UnitGrid.Build(s.Units);

        UnitInstance linear = TargetSearch.FindNearestHostile(self, s.Units, s.Relations, 500f,
            DomainMask.All, s.Types);
        UnitInstance grid = TargetSearch.FindNearestHostile(self, s.UnitGrid, s.Relations, 500f,
            DomainMask.All, s.Types);

        Assert.Same(east, linear); // confirm the lowest-list-index premise
        Assert.Same(linear, grid);
    }

    [Fact]
    public void Grid_search_finds_targets_across_cell_boundaries()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        // Just on either side of a cell boundary (256m). Must be found even with range 60.
        var self = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(255f, 0, 0));
        var enemy = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(258f, 0, 0));
        s.Units.Add(self); s.Units.Add(enemy);
        s.UnitGrid.Build(s.Units);

        Assert.Same(enemy, TargetSearch.FindNearestHostile(self, s.UnitGrid, s.Relations, 60f,
            DomainMask.All, s.Types));
    }

    [Fact]
    public void Grid_search_applies_the_domain_filter()
    {
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        s.Types.Register(MvpUnitTypes.Tank_T1()); // Domain.Land
        var self = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        var enemy = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(30f, 0, 0));
        s.Units.Add(self); s.Units.Add(enemy);
        s.UnitGrid.Build(s.Units);

        // For an anti-air-only attacker (can only target Air), a land unit within range is not a valid target.
        Assert.Null(TargetSearch.FindNearestHostile(self, s.UnitGrid, s.Relations, 60f,
            DomainMask.Air, s.Types));
        Assert.Same(enemy, TargetSearch.FindNearestHostile(self, s.UnitGrid, s.Relations, 60f,
            DomainMask.Land, s.Types));
    }

    [Fact]
    public void Units_killed_after_build_disappear_from_subsequent_searches()
    {
        // CombatStep excludes enemies already killed earlier in the same tick from later searches (same behavior as the brute-force version).
        var s = new WarState();
        for (byte i = 0; i < 2; i++) s.Factions.Add(new Faction(i, "F" + i));
        RelationPresets.ApplyAllHostile(s.Relations, 2);
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var self = new UnitInstance(1, "Tank_T1", 0, 100f, new WorldPos(0, 0, 0));
        var enemy = new UnitInstance(2, "Tank_T1", 1, 100f, new WorldPos(30f, 0, 0));
        s.Units.Add(self); s.Units.Add(enemy);
        s.UnitGrid.Build(s.Units);

        enemy.CurrentHP = 0f; // dies after Build (IsAlive becomes false even before the State transition)
        Assert.Null(TargetSearch.FindNearestHostile(self, s.UnitGrid, s.Relations, 60f,
            DomainMask.All, s.Types));
    }
}
