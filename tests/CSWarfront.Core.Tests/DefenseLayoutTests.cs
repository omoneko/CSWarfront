using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
public class DefenseLayoutTests
{
    private static MilitaryBase Fort(ushort id, BaseType type, float x, float z, byte? owner)
    {
        var b = new MilitaryBase(id, type, new WorldPos(x, 0, z));
        b.OwnerFactionId = owner;
        b.MaxHP = FortificationRules.DefaultMaxHP(type);
        b.CurrentHP = b.MaxHP;
        return b;
    }

    [Fact]
    public void Eligible_covers_own_live_forts_and_all_trenches_only()
    {
        Assert.True(DefenseLayout.IsEligible(Fort(1, BaseType.Bunker, 0, 0, 0), 0));
        Assert.True(DefenseLayout.IsEligible(Fort(2, BaseType.Trench, 0, 0, null), 0)); // trenches are ownerless by design
        Assert.False(DefenseLayout.IsEligible(Fort(3, BaseType.Bunker, 0, 0, 1), 0));   // enemy-owned
        Assert.False(DefenseLayout.IsEligible(Fort(4, BaseType.Bunker, 0, 0, null), 0)); // defunct (owner nulled at 0 HP)
        Assert.False(DefenseLayout.IsEligible(Fort(5, BaseType.Army, 0, 0, 0), 0));     // regular bases are out of scope

        var dead = Fort(6, BaseType.ArtilleryPost, 0, 0, 0);
        dead.CurrentHP = 0f;
        Assert.False(DefenseLayout.IsEligible(dead, 0));
    }

    [Fact]
    public void Missing_detection_skips_intact_positions_and_finds_lost_ones()
    {
        var s = new WarState();
        s.DefenseLayout.Add(new DefenseLayoutEntry { Type = BaseType.Bunker, Position = new WorldPos(0, 0, 0) });
        s.DefenseLayout.Add(new DefenseLayoutEntry { Type = BaseType.ArtilleryPost, Position = new WorldPos(100, 0, 0) });
        s.DefenseLayout.Add(new DefenseLayoutEntry { Type = BaseType.Trench, Position = new WorldPos(200, 0, 0) });

        // The bunker still stands (within MatchRadius); the artillery post and trench are gone.
        s.Bases.Add(Fort(1, BaseType.Bunker, 3, 0, 0));

        var missing = DefenseLayout.FindMissing(s);
        Assert.Equal(2, missing.Count);
        Assert.Equal(BaseType.ArtilleryPost, missing[0].Type);
        Assert.Equal(BaseType.Trench, missing[1].Type);
    }

    [Fact]
    public void Defunct_wreck_counts_as_missing_and_is_reported_as_blocker()
    {
        var s = new WarState();
        var entry = new DefenseLayoutEntry { Type = BaseType.Bunker, Position = new WorldPos(0, 0, 0) };
        s.DefenseLayout.Add(entry);
        s.Bases.Add(Fort(9, BaseType.Bunker, 1, 0, null)); // 0-HP bunker: building stands, owner nulled

        Assert.Single(DefenseLayout.FindMissing(s));
        ushort blocker;
        Assert.True(DefenseLayout.TryFindBlocker(s, entry, out blocker));
        Assert.Equal(9, blocker);
    }

    [Fact]
    public void Enemy_captured_depot_is_satisfied_not_rebuilt_on_top()
    {
        var s = new WarState();
        var entry = new DefenseLayoutEntry { Type = BaseType.SupplyDepot, Position = new WorldPos(0, 0, 0) };
        s.DefenseLayout.Add(entry);
        s.Bases.Add(Fort(3, BaseType.SupplyDepot, 2, 0, 1)); // captured by faction 1, still alive

        Assert.Empty(DefenseLayout.FindMissing(s));
    }

    [Fact]
    public void Same_type_far_away_does_not_satisfy_an_entry()
    {
        var s = new WarState();
        s.DefenseLayout.Add(new DefenseLayoutEntry { Type = BaseType.Bunker, Position = new WorldPos(0, 0, 0) });
        s.Bases.Add(Fort(1, BaseType.Bunker, DefenseLayout.MatchRadius + 1f, 0, 0));

        Assert.Single(DefenseLayout.FindMissing(s));
    }

    [Fact]
    public void Serializer_roundtrips_the_defense_layout()
    {
        var types = new UnitTypeRegistry();
        var s = new WarState();
        s.DefenseLayout.Add(new DefenseLayoutEntry
        {
            Type = BaseType.ArtilleryPost,
            Position = new WorldPos(12.5f, 3f, -8f),
            Angle = 1.25f
        });

        var r = WarStateSerializer.Deserialize(WarStateSerializer.Serialize(s), types);

        Assert.Single(r.DefenseLayout);
        Assert.Equal(BaseType.ArtilleryPost, r.DefenseLayout[0].Type);
        Assert.Equal(12.5f, r.DefenseLayout[0].Position.X);
        Assert.Equal(-8f, r.DefenseLayout[0].Position.Z);
        Assert.Equal(1.25f, r.DefenseLayout[0].Angle);
    }
}
}
