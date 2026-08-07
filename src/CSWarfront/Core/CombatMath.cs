using System;
namespace CSWarfront.Core
{
    public static class CombatMath
    {
        /// <summary>One hit's damage. Reduced by armor, floored at 1.</summary>
        public static float DamagePerHit(float attack, float armor)
        {
            return Math.Max(1f, attack - armor);
        }
    }
}
