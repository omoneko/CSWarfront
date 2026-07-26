using CSWarfront.Core;
using Xunit;

public class SpeedCalibrationTests
{
    [Fact]
    public void UnitsPerGameHourFromKmh_zero_is_zero()
    {
        Assert.Equal(0f, SpeedCalibration.UnitsPerGameHourFromKmh(0f), 5);
    }

    [Fact]
    public void UnitsPerGameHourFromKmh_scales_linearly_with_kmh()
    {
        float at40 = SpeedCalibration.UnitsPerGameHourFromKmh(40f);
        float at80 = SpeedCalibration.UnitsPerGameHourFromKmh(80f);
        // 線形（比例）関係: 80km/hの結果は40km/hの結果のちょうど2倍になるはず。
        Assert.Equal(at40 * 2f, at80, 3);
    }

    [Fact]
    public void UnitsPerGameHourFromKmh_40kmh_matches_hand_computed_value()
    {
        // 手計算（本番コードと同じ式を別途展開しただけで、呼び出しはしていない）:
        // metresPerRealSecond = 40 * 1000 / 3600 = 11.111111
        // unitsPerGameHour   = 11.111111 / 2.05078125 = 5.41799 (概算)
        float result = SpeedCalibration.UnitsPerGameHourFromKmh(40f);
        Assert.Equal(5.418f, result, 2);
    }

    [Fact]
    public void UnitsPerGameHourFromKmh_5kmh_matches_hand_computed_value()
    {
        // metresPerRealSecond = 5 * 1000 / 3600 = 1.388889
        // unitsPerGameHour   = 1.388889 / 2.05078125 = 0.677249 (概算)
        float result = SpeedCalibration.UnitsPerGameHourFromKmh(5f);
        Assert.Equal(0.677f, result, 2);
    }

    [Fact]
    public void InGameHoursPerRealSecond_matches_researched_constant()
    {
        // 50Hz(Unity既定Time.fixedDeltaTime=0.02秒)想定 × 0.041015625時間/フレーム
        // (m_timePerFrame = TimeSpan.FromTicks(1_476_562_500)) = 2.05078125
        Assert.Equal(2.05078125f, SpeedCalibration.InGameHoursPerRealSecond, 5);
    }
}
