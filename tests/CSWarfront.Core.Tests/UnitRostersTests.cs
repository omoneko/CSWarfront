using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task157. Code that meant "every unit type" said LandUnitRoster.All() and quietly meant
/// "every ground unit". Harmless while only ground vehicles could be given a model; not harmless once
/// aircraft and ships could too, because the model copy looked the source type up in the land roster
/// alone and rejected every aircraft key as unknown.</summary>
public class UnitRostersTests
{
    [Fact]
    public void Holds_every_unit_type_from_all_three_rosters()
    {
        var keys = new List<string>();
        foreach (UnitType t in UnitRosters.All()) keys.Add(t.TypeKey);

        Assert.Equal(80, keys.Count); // 45 land + 25 air + 10 sea
        Assert.Contains("Tank_T1", keys);
        Assert.Contains("TacticalBomber_T1", keys);
        Assert.Contains("AttackHelicopter_T3", keys);
        Assert.Contains("Destroyer_T5", keys);
        Assert.Contains("Carrier_T1", keys);
    }

    [Fact]
    public void Lists_each_type_once()
    {
        var seen = new HashSet<string>();
        foreach (UnitType t in UnitRosters.All())
            Assert.True(seen.Add(t.TypeKey), t.TypeKey + " is listed twice");
    }

    /// <summary>Land first, then air, then sea. Callers index into this, so the order is part of the
    /// contract - it is what the model assignment dropdown shows.</summary>
    [Fact]
    public void Lists_land_then_air_then_sea()
    {
        var keys = new List<string>();
        foreach (UnitType t in UnitRosters.All()) keys.Add(t.TypeKey);

        Assert.True(keys.IndexOf("Tank_T1") < keys.IndexOf("TacticalBomber_T1"));
        Assert.True(keys.IndexOf("TacticalBomber_T1") < keys.IndexOf("Destroyer_T1"));
    }

    /// <summary>The lookup the model copy depends on. Every key it yields must resolve, or copying a
    /// model from that type silently writes nothing.</summary>
    [Fact]
    public void Every_key_it_yields_resolves_back_to_its_category()
    {
        foreach (UnitType t in UnitRosters.All())
        {
            UnitCategory category;
            Assert.True(UnitRosters.TryGetCategory(t.TypeKey, out category),
                t.TypeKey + " does not resolve back to a category");
            Assert.Equal(t.Category, category);
        }
    }

    [Fact]
    public void Does_not_resolve_a_key_no_roster_has()
    {
        UnitCategory category;
        Assert.False(UnitRosters.TryGetCategory("MilitaryBase", out category)); // a base-type key
        Assert.False(UnitRosters.TryGetCategory("Tank_T9", out category));      // no such tier
        Assert.False(UnitRosters.TryGetCategory("", out category));
    }

    [Fact]
    public void Spells_keys_the_way_the_rosters_do()
    {
        Assert.Equal(LandUnitRoster.TypeKey(UnitCategory.Tank, 3), UnitRosters.TypeKey(UnitCategory.Tank, 3));
        Assert.Equal(AirUnitRoster.TypeKey(UnitCategory.TacticalBomber, 2),
            UnitRosters.TypeKey(UnitCategory.TacticalBomber, 2));
        Assert.Equal(NavalUnitRoster.TypeKey(UnitCategory.Destroyer, 5),
            UnitRosters.TypeKey(UnitCategory.Destroyer, 5));
    }
}
}
