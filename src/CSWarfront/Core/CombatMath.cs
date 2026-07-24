using System;
namespace CSWarfront.Core
{
    public static class CombatMath
    {
        /// <summary>1発のダメージ。装甲で軽減、最低1を保証。</summary>
        public static float DamagePerHit(float attack, float armor)
        {
            return Math.Max(1f, attack - armor);
        }
    }
}
