using CSWarfront.Core;
using Xunit;

/// <summary>
/// Tests for ThreatBeamStep (Task83: unit damage checks for the Godzilla beam / tripod laser).
/// For a one-shot beam (line segment) fired by another MOD, deals one-hit damage that ignores armor
/// and is faction-indiscriminate to all units within a fixed width of the segment.
/// </summary>
public class ThreatBeamStepTests
{
    private static WarState StateWithTank(float x, float z, float hp = 100f)
    {
        var s = new WarState();
        s.Factions.Add(new Faction(0, "Red"));
        s.Types.Register(MvpUnitTypes.Tank_T1());
        var u = new UnitInstance(1, "Tank_T1", 0, hp, new WorldPos(x, 0, z));
        s.Units.Add(u);
        return s;
    }

    [Fact]
    public void Unit_on_beam_path_takes_kaiju_damage_and_dies()
    {
        var s = StateWithTank(50f, 0f); // Directly on the segment (0,0)-(100,0)
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);

        Assert.Equal(1, hit);
        Assert.True(s.Units[0].CurrentHP <= 0f);
        Assert.Equal(UnitState.Dead, s.Units[0].State);
        Assert.Single(s.RecentKills);
    }

    [Fact]
    public void Unit_within_lateral_width_is_hit()
    {
        var s = StateWithTank(50f, ThreatBeamStep.KaijuBeamWidth - 1f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);
        Assert.Equal(1, hit);
    }

    [Fact]
    public void Unit_outside_lateral_width_is_untouched()
    {
        var s = StateWithTank(50f, ThreatBeamStep.KaijuBeamWidth + 1f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);

        Assert.Equal(0, hit);
        Assert.Equal(100f, s.Units[0].CurrentHP, 1);
        Assert.Empty(s.RecentKills);
    }

    [Fact]
    public void Unit_beyond_segment_end_is_untouched()
    {
        var s = StateWithTank(100f + ThreatBeamStep.KaijuBeamWidth + 1f, 0f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);
        Assert.Equal(0, hit);
    }

    [Fact]
    public void Unit_within_endpoint_circle_is_hit()
    {
        // Being within the width of a segment endpoint (the rounded end cap) also counts as a hit
        var s = StateWithTank(100f + ThreatBeamStep.KaijuBeamWidth - 1f, 0f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);
        Assert.Equal(1, hit);
    }

    [Fact]
    public void Alien_strike_uses_alien_damage()
    {
        // Give it more HP than AlienBeamDamage and confirm it "does not die and loses exactly AlienBeamDamage worth of HP"
        var s = StateWithTank(50f, 0f, ThreatBeamStep.AlienBeamDamage + 100f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Alien, 0f, 0f, 100f, 0f);

        Assert.Equal(1, hit);
        Assert.Equal(100f, s.Units[0].CurrentHP, 1);
        Assert.NotEqual(UnitState.Dead, s.Units[0].State);
        Assert.Empty(s.RecentKills);
    }

    [Fact]
    public void Dead_unit_is_skipped_and_no_duplicate_kill_event()
    {
        var s = StateWithTank(50f, 0f);
        s.Units[0].State = UnitState.Dead;
        s.Units[0].CurrentHP = 0f;

        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);

        Assert.Equal(0, hit);
        Assert.Empty(s.RecentKills);
    }

    [Fact]
    public void Damage_ignores_faction_relations()
    {
        // Same policy as ThreatAuraStep: never consults ThreatRelations and hits units of all factions equally.
        // (The fact that damage lands even without any relation setup confirms that it "does not consult" them.)
        var s = StateWithTank(50f, 0f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Kaiju, 0f, 0f, 100f, 0f);
        Assert.Equal(1, hit);
    }

    [Fact]
    public void Degenerate_zero_length_segment_acts_as_circle()
    {
        // Degenerate cases where start = end, such as a tripod firing straight down, are treated as a circle around the point
        var s = StateWithTank(5f, 5f, ThreatBeamStep.AlienBeamDamage + 50f);
        int hit = ThreatBeamStep.ApplyStrike(s, ThreatKind.Alien, 0f, 0f, 0f, 0f);
        Assert.Equal(1, hit); // Distance sqrt(50) ≈ 7.1 < AlienBeamWidth (15)
    }
}
