using CSWarfront.Core;
using Xunit;

public class CombatMathTests
{
    [Theory]
    [InlineData(25f, 5f, 20f)]   // 通常
    [InlineData(10f, 10f, 1f)]   // 装甲=攻撃 → 最低1
    [InlineData(3f, 50f, 1f)]    // 装甲超過 → 最低1
    public void DamagePerHit_is_attack_minus_armor_floored_at_1(float atk, float armor, float expected)
    {
        Assert.Equal(expected, CombatMath.DamagePerHit(atk, armor), 3);
    }
}
