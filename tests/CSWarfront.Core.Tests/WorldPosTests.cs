using CSWarfront.Core;
using Xunit;

public class WorldPosTests
{
    [Fact]
    public void DistanceTo_computes_3d_euclidean()
    {
        var a = new WorldPos(0f, 0f, 0f);
        var b = new WorldPos(3f, 0f, 4f);
        Assert.Equal(5f, a.DistanceTo(b), 3);
    }

    [Fact]
    public void HorizontalDistanceTo_ignores_y()
    {
        var a = new WorldPos(0f, 100f, 0f);
        var b = new WorldPos(3f, 999f, 4f);
        Assert.Equal(5f, a.HorizontalDistanceTo(b), 3);
    }
}
