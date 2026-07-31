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
        // Task28: MvpUnitTypes.Tank_T1()はLandUnitRoster.Get(Tank, 1)の薄いラッパーに置き換わった。
        // 新しいTier1基礎値（LandUnitRosterのTankの行）: HP140, Attack40, Range60, Armor10, Cost60, BuildTime8。
        // Attack/Range/BuildTime/Speedは旧値と一致（意図的に据え置き）。HP/Armor/Costは新テーブルの値へ変更。
        Assert.Equal(140f, t.MaxHP, 3);
        Assert.Equal(42f, t.Attack, 3); // Task91: 40->42
        Assert.Equal(60f, t.Range, 3);
        Assert.Equal(10f, t.Armor, 3);
        Assert.Equal(60f, t.Cost, 3);
        Assert.Equal(8f, t.BuildTime, 3);
    }

    [Fact]
    public void Infantry_T1_speed_is_7_5kmh_after_the_1_5x_speed_buff()
    {
        // Task43: ユーザーフィードバック「歩兵の移動速度は1.5倍」により 5km/h -> 7.5km/h に変更。
        var t = MvpUnitTypes.Infantry_T1();
        Assert.Equal("Infantry_T1", t.TypeKey);
        Assert.Equal(Domain.Land, t.Domain);
        Assert.Equal(UnitCategory.Infantry, t.Category);
        // 7.5km/h -> 2.0833... m/s -> / 2.05078125 ≈ 1.016 units/ゲーム内時間（旧: 5km/hで0.677）
        Assert.Equal(1.02f, t.Speed, 2);
        Assert.Equal(60f, t.MaxHP, 3);
        Assert.Equal(18f, t.Attack, 3); // Task91: 20->18
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
