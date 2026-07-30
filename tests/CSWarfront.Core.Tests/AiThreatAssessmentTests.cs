using CSWarfront.Core;
using Xunit;

// Task80: AiThreatAssessment is the "observe" phase of the game-theoretic AI production policy
// (see AiProductionPolicy's class comment for the overall observe -> payoff -> hedge pipeline).
// These tests exercise it directly (independent of AiProductionPolicy.Decide) so the enemy-mix
// math, Nemesis weighting, external-threat pseudo-category, and research-formula inputs can each
// be pinned down precisely.
public class AiThreatAssessmentTests
{
    private static WarState NewState()
    {
        var s = new WarState();
        LandUnitRoster.RegisterAll(s.Types);
        AirUnitRoster.RegisterAll(s.Types);
        return s;
    }

    private static Faction AddFaction(WarState s, byte id, string name)
    {
        var f = new Faction(id, name);
        s.Factions.Add(f);
        return f;
    }

    private static void AddUnit(WarState s, byte factionId, UnitCategory category, byte tier)
    {
        string key = category + "_T" + tier;
        s.Units.Add(new UnitInstance(s.AllocInstanceId(), key, factionId, 100f, new WorldPos(0, 0, 0)));
    }

    // --- Observe: normalization and hostility gating ---

    [Fact]
    public void Observe_returns_HasHostiles_false_when_no_hostile_units_or_threats_exist()
    {
        var s = NewState();
        AddFaction(s, 0, "Red");
        var mix = AiThreatAssessment.Observe(s, 0);
        Assert.False(mix.HasHostiles);
        foreach (float share in mix.Shares) Assert.Equal(0f, share);
    }

    [Fact]
    public void Observe_ignores_own_and_neutral_units_but_counts_hostile_ones()
    {
        var s = NewState();
        AddFaction(s, 0, "Red");
        AddFaction(s, 1, "Blue");   // stays Neutral (RelationMatrix default)
        AddFaction(s, 2, "Green");
        s.Relations.Set(0, 2, Relation.Hostile);

        AddUnit(s, 0, UnitCategory.Tank, 1);       // own -> ignored
        AddUnit(s, 1, UnitCategory.Infantry, 1);   // neutral -> ignored
        AddUnit(s, 2, UnitCategory.Apc, 1);        // hostile -> counted

        var mix = AiThreatAssessment.Observe(s, 0);
        Assert.True(mix.HasHostiles);
        Assert.Equal(1f, mix.Shares[(int)UnitCategory.Apc]);
        Assert.Equal(0f, mix.Shares[(int)UnitCategory.Tank]);
        Assert.Equal(0f, mix.Shares[(int)UnitCategory.Infantry]);
    }

    [Fact]
    public void Observe_normalizes_shares_by_category_across_a_single_hostile_faction()
    {
        var s = NewState();
        AddFaction(s, 0, "Red");
        AddFaction(s, 1, "Blue");
        s.Relations.Set(0, 1, Relation.Hostile);

        for (int i = 0; i < 3; i++) AddUnit(s, 1, UnitCategory.Tank, 1);
        AddUnit(s, 1, UnitCategory.Infantry, 1);

        var mix = AiThreatAssessment.Observe(s, 0);
        Assert.True(mix.HasHostiles);
        Assert.Equal(0.75f, mix.Shares[(int)UnitCategory.Tank], 3);
        Assert.Equal(0.25f, mix.Shares[(int)UnitCategory.Infantry], 3);
    }

    [Fact]
    public void Observe_weights_Nemesis_faction_units_twice_as_heavily_as_plain_Hostile()
    {
        var s = NewState();
        AddFaction(s, 0, "Red");
        AddFaction(s, 1, "NemesisFaction");
        AddFaction(s, 2, "PlainHostile");
        s.Relations.Set(0, 1, Relation.Nemesis);
        s.Relations.Set(0, 2, Relation.Hostile);

        AddUnit(s, 1, UnitCategory.Tank, 1);      // Nemesis: weight 2
        AddUnit(s, 2, UnitCategory.Infantry, 1);  // Hostile: weight 1

        var mix = AiThreatAssessment.Observe(s, 0);
        // total weight = 2 + 1 = 3 -> Tank 2/3, Infantry 1/3
        Assert.Equal(2f / 3f, mix.Shares[(int)UnitCategory.Tank], 3);
        Assert.Equal(1f / 3f, mix.Shares[(int)UnitCategory.Infantry], 3);
    }

    [Fact]
    public void Observe_treats_hostile_external_threats_as_Tank_like_pseudo_category()
    {
        var s = NewState();
        AddFaction(s, 0, "Red");
        // WarState.ThreatRelations defaults every faction to Hostile against every ThreatKind.
        s.Threats.Add(new ExternalThreat { Id = 1, Kind = ThreatKind.Kaiju, MaxHP = 1000f, CurrentHP = 1000f });

        var mix = AiThreatAssessment.Observe(s, 0);
        Assert.True(mix.HasHostiles);
        Assert.Equal(1f, mix.Shares[(int)UnitCategory.Tank]);
    }

    [Fact]
    public void Observe_ignores_defeated_external_threats()
    {
        var s = NewState();
        AddFaction(s, 0, "Red");
        s.Threats.Add(new ExternalThreat { Id = 1, Kind = ThreatKind.Kaiju, MaxHP = 1000f, CurrentHP = 0f });

        var mix = AiThreatAssessment.Observe(s, 0);
        Assert.False(mix.HasHostiles);
    }

    // --- AverageFieldedTier / StrongestHostileAverageTier (research formula inputs) ---

    [Fact]
    public void AverageFieldedTier_is_zero_with_no_living_units()
    {
        var s = NewState();
        AddFaction(s, 0, "Red");
        Assert.Equal(0f, AiThreatAssessment.AverageFieldedTier(s, 0));
    }

    [Fact]
    public void AverageFieldedTier_averages_across_mixed_tiers()
    {
        var s = NewState();
        AddFaction(s, 0, "Red");
        AddUnit(s, 0, UnitCategory.Tank, 1);
        AddUnit(s, 0, UnitCategory.Tank, 3);
        AddUnit(s, 0, UnitCategory.Infantry, 5);

        Assert.Equal(3f, AiThreatAssessment.AverageFieldedTier(s, 0), 3); // (1+3+5)/3
    }

    [Fact]
    public void StrongestHostileAverageTier_picks_the_highest_among_hostile_factions_and_ignores_others()
    {
        var s = NewState();
        AddFaction(s, 0, "Red");
        AddFaction(s, 1, "WeakHostile");
        AddFaction(s, 2, "StrongHostile");
        AddFaction(s, 3, "AlliedHighTier"); // not hostile -> must be ignored despite higher tier
        s.Relations.Set(0, 1, Relation.Hostile);
        s.Relations.Set(0, 2, Relation.Nemesis);
        s.Relations.Set(0, 3, Relation.Allied);

        AddUnit(s, 1, UnitCategory.Tank, 2);
        AddUnit(s, 2, UnitCategory.Tank, 4);
        AddUnit(s, 3, UnitCategory.Tank, 5);

        Assert.Equal(4f, AiThreatAssessment.StrongestHostileAverageTier(s, 0));
    }

    // --- OutnumberedFactor ---

    [Theory]
    [InlineData(10, 10, 0f)]   // 1:1 -> not outnumbered
    [InlineData(6, 10, 0.6666667f)] // 10/6 ~= 1.67:1 -> factor = 1.67-1 = 0.67
    [InlineData(5, 10000, 1f)] // way past 2:1 -> clamps to 1
    public void OutnumberedFactor_scales_from_zero_at_parity_to_one_at_two_to_one(int own, int hostile, float expected)
    {
        var s = NewState();
        AddFaction(s, 0, "Red");
        AddFaction(s, 1, "Blue");
        s.Relations.Set(0, 1, Relation.Hostile);
        for (int i = 0; i < own; i++) AddUnit(s, 0, UnitCategory.Infantry, 1);
        for (int i = 0; i < hostile; i++) AddUnit(s, 1, UnitCategory.Infantry, 1);

        Assert.Equal(expected, AiThreatAssessment.OutnumberedFactor(s, 0), 3);
    }

    [Fact]
    public void OutnumberedFactor_is_one_when_faction_has_zero_units_but_a_hostile_exists()
    {
        var s = NewState();
        AddFaction(s, 0, "Red");
        AddFaction(s, 1, "Blue");
        s.Relations.Set(0, 1, Relation.Hostile);
        AddUnit(s, 1, UnitCategory.Infantry, 1);

        Assert.Equal(1f, AiThreatAssessment.OutnumberedFactor(s, 0));
    }

    [Fact]
    public void OutnumberedFactor_is_zero_when_there_are_no_hostile_units_at_all()
    {
        var s = NewState();
        AddFaction(s, 0, "Red");
        Assert.Equal(0f, AiThreatAssessment.OutnumberedFactor(s, 0));
    }
}
