using System.Collections.Generic;
namespace CSWarfront.Core
{
    public struct DevelopmentSample
    {
        public WorldPos Position;
        public float Development;
    }

    /// <summary>基地の勢力圏内の発展度合計×レート＝収入（純ロジック）。</summary>
    public static class TerritoryIncome
    {
        public static float ForBase(MilitaryBase b, IEnumerable<DevelopmentSample> samples, float rate)
        {
            if (b.OwnerFactionId == null) return 0f;
            float sum = 0f;
            foreach (var s in samples)
                if (b.Position.HorizontalDistanceTo(s.Position) <= b.InfluenceRadius)
                    sum += s.Development;
            return sum * rate;
        }
    }
}
