using CSWarfront.Core;
using Xunit;

public class RelationPresetsTests
{
    [Fact]
    public void ApplyAllHostile_sets_every_distinct_pair_to_hostile()
    {
        var m = new RelationMatrix(5);
        RelationPresets.ApplyAllHostile(m, 5);

        for (byte i = 0; i < 5; i++)
            for (byte j = (byte)(i + 1); j < 5; j++)
                Assert.Equal(Relation.Hostile, m.Get(i, j));
    }

    [Fact]
    public void ApplyAllHostile_leaves_self_relation_allied()
    {
        var m = new RelationMatrix(5);
        RelationPresets.ApplyAllHostile(m, 5);

        for (byte i = 0; i < 5; i++)
            Assert.Equal(Relation.Allied, m.Get(i, i));
    }

    [Fact]
    public void ApplyAllHostile_overwrites_previously_set_relations()
    {
        var m = new RelationMatrix(5);
        m.Set(0, 1, Relation.Allied);
        m.Set(2, 3, Relation.Neutral);

        RelationPresets.ApplyAllHostile(m, 5);

        Assert.Equal(Relation.Hostile, m.Get(0, 1));
        Assert.Equal(Relation.Hostile, m.Get(2, 3));
    }

    [Fact]
    public void ApplyAllHostile_respects_smaller_count_than_matrix_size()
    {
        var m = new RelationMatrix(5);
        RelationPresets.ApplyAllHostile(m, 3);

        Assert.Equal(Relation.Hostile, m.Get(0, 1));
        Assert.Equal(Relation.Hostile, m.Get(1, 2));
        // pairs involving index >= 3 untouched (still default Neutral)
        Assert.Equal(Relation.Neutral, m.Get(0, 3));
        Assert.Equal(Relation.Neutral, m.Get(3, 4));
    }

    [Fact]
    public void ApplyAllHostile_null_matrix_does_not_throw()
    {
        RelationPresets.ApplyAllHostile(null, 5);
    }
}
