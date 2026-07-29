using CSWarfront.Core;
using Xunit;

// Task58: ExternalThreat is a plain data holder for HP CSWarfront itself owns for a monster spawned by
// another mod (Godzilla/Alien), since those mods expose no HP/damage API of their own.
public class ExternalThreatTests
{
    [Fact]
    public void IsDefeated_is_false_while_hp_remains()
    {
        var t = new ExternalThreat { Id = 1, Kind = "Godzilla", CurrentHP = 1f };
        Assert.False(t.IsDefeated);
    }

    [Fact]
    public void IsDefeated_becomes_true_once_hp_reaches_zero()
    {
        var t = new ExternalThreat { Id = 1, Kind = "Godzilla", CurrentHP = 0f };
        Assert.True(t.IsDefeated);
    }

    [Fact]
    public void IsDefeated_is_true_below_zero_too()
    {
        var t = new ExternalThreat { Id = 1, Kind = "Godzilla", CurrentHP = -5f };
        Assert.True(t.IsDefeated);
    }

    [Fact]
    public void WarState_Threats_starts_empty_and_is_not_null()
    {
        var s = new WarState();
        Assert.NotNull(s.Threats);
        Assert.Empty(s.Threats);
    }
}
