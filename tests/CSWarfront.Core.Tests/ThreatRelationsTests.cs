using CSWarfront.Core;
using Xunit;

// Task59: ThreatRelations — per-faction relation table toward external threats (KAIJU/Alien).
public class ThreatRelationsTests
{
    [Fact]
    public void Default_is_hostile_for_every_faction_and_kind()
    {
        var t = new ThreatRelations(5);
        for (byte f = 0; f < 5; f++)
        {
            Assert.Equal(Relation.Hostile, t.Get(f, ThreatKind.Kaiju));
            Assert.Equal(Relation.Hostile, t.Get(f, ThreatKind.Alien));
        }
    }

    [Fact]
    public void Set_changes_only_the_targeted_faction_and_kind()
    {
        var t = new ThreatRelations(5);
        t.Set(1, ThreatKind.Kaiju, Relation.Neutral);

        Assert.Equal(Relation.Neutral, t.Get(1, ThreatKind.Kaiju));
        Assert.Equal(Relation.Hostile, t.Get(1, ThreatKind.Alien)); // other kind untouched
        Assert.Equal(Relation.Hostile, t.Get(0, ThreatKind.Kaiju)); // other faction untouched
    }

    [Fact]
    public void Set_supports_nemesis()
    {
        var t = new ThreatRelations(5);
        t.Set(2, ThreatKind.Alien, Relation.Nemesis);
        Assert.Equal(Relation.Nemesis, t.Get(2, ThreatKind.Alien));
    }
}
