using CSWarfront.Core;
using Xunit;

public class UnitTypeRegistryTests
{
    [Fact]
    public void Register_then_Get_returns_same_type()
    {
        var reg = new UnitTypeRegistry();
        reg.Register(MvpUnitTypes.Tank_T1());
        var t = reg.Get("Tank_T1");
        Assert.NotNull(t);
        Assert.Equal(Domain.Land, t.Domain);
        Assert.Equal(40f, t.Attack, 3);
    }

    [Fact]
    public void Get_unknown_returns_null()
    {
        var reg = new UnitTypeRegistry();
        Assert.Null(reg.Get("NoSuch"));
    }

    [Fact]
    public void All_enumerates_every_registered_type()
    {
        var reg = new UnitTypeRegistry();
        reg.Register(MvpUnitTypes.Tank_T1());
        reg.Register(MvpUnitTypes.Infantry_T1());

        var keys = new System.Collections.Generic.List<string>();
        foreach (var t in reg.All()) keys.Add(t.TypeKey);

        Assert.Contains("Tank_T1", keys);
        Assert.Contains("Infantry_T1", keys);
        Assert.Equal(2, keys.Count);
    }
}
