using CSWarfront.Core;
using Xunit;

public class MvpUnitTypesTests
{
    [Fact]
    public void Tank_T1_speed_is_40kmh_calibrated()
    {
        var t = MvpUnitTypes.Tank_T1();
        Assert.Equal("Tank_T1", t.TypeKey);
        Assert.Equal(Domain.Land, t.Domain);
        Assert.Equal(UnitCategory.Tank, t.Category);
        // 40km/h -> 11.111... m/s -> / 2.05078125 (InGameHoursPerRealSecond) ≈ 5.418 units/ゲーム内時間
        Assert.Equal(5.418f, t.Speed, 2);
        // 他のステータスは変更されていないこと。
        Assert.Equal(100f, t.MaxHP, 3);
        Assert.Equal(40f, t.Attack, 3);
        Assert.Equal(60f, t.Range, 3);
        Assert.Equal(5f, t.Armor, 3);
        Assert.Equal(50f, t.Cost, 3);
        Assert.Equal(8f, t.BuildTime, 3);
    }

    [Fact]
    public void Infantry_T1_speed_is_5kmh_citizen_walking_pace()
    {
        var t = MvpUnitTypes.Infantry_T1();
        Assert.Equal("Infantry_T1", t.TypeKey);
        Assert.Equal(Domain.Land, t.Domain);
        Assert.Equal(UnitCategory.Infantry, t.Category);
        // 5km/h -> 1.388... m/s -> / 2.05078125 ≈ 0.677 units/ゲーム内時間
        Assert.Equal(0.677f, t.Speed, 2);
        Assert.Equal(60f, t.MaxHP, 3);
        Assert.Equal(20f, t.Attack, 3);
        Assert.Equal(40f, t.Range, 3);
        Assert.Equal(1f, t.Armor, 3);
        Assert.Equal(20f, t.Cost, 3);
        Assert.Equal(4f, t.BuildTime, 3);
    }

    [Fact]
    public void Tank_T1_is_faster_than_Infantry_T1()
    {
        Assert.True(MvpUnitTypes.Tank_T1().Speed > MvpUnitTypes.Infantry_T1().Speed);
    }
}
