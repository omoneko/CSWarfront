using CSWarfront.Core;
using Xunit;

public class RelationMatrixTests
{
    [Fact]
    public void Default_is_neutral_between_different_factions()
    {
        var m = new RelationMatrix(5);
        Assert.Equal(Relation.Neutral, m.Get(0, 1));
    }

    [Fact]
    public void Set_is_symmetric()
    {
        var m = new RelationMatrix(5);
        m.Set(0, 3, Relation.Hostile);
        Assert.Equal(Relation.Hostile, m.Get(0, 3));
        Assert.Equal(Relation.Hostile, m.Get(3, 0)); // 鏡側も更新
    }

    [Fact]
    public void Self_relation_is_always_allied()
    {
        var m = new RelationMatrix(5);
        Assert.Equal(Relation.Allied, m.Get(2, 2));
    }
}
