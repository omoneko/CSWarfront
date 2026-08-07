using System.Collections.Generic;
namespace CSWarfront.Core
{
    /// <summary>The zone kind of a development sample (Task99: the three-resource economy). The Game
    /// layer's DevelopmentSampler classifies it from CS's ItemClass.Service. Other contributes to no
    /// resource (public facilities etc.).</summary>
    public enum ZoneKind { Other, Residential, CommercialOffice, Industrial }

    public struct DevelopmentSample
    {
        public WorldPos Position;
        public float Development;
        public ZoneKind Zone; // Task99: defaults to Other (legacy tests/callers contribute no resource income)
    }

    /// <summary>Per-zone income (Task99): residential → manpower, commercial/office → funds,
    /// industrial → production.</summary>
    public struct ZonedIncome
    {
        public float Manpower;
        public float Funds;
        public float Production;
    }

    /// <summary>Income = total development inside the base's sphere × rate (pure logic).</summary>
    public static class TerritoryIncome
    {
        /// <summary>Task99: the three-resource economy's scan radius (user spec "within 1km of the
        /// base"). An economy-only radius independent of InfluenceRadius (500m, the sphere for
        /// capture, UI rings etc.).</summary>
        public const float EconomyRadius = 1000f;

        public static float ForBase(MilitaryBase b, IEnumerable<DevelopmentSample> samples, float rate)
        {
            if (b.OwnerFactionId == null) return 0f;
            float sum = 0f;
            foreach (var s in samples)
                if (b.Position.HorizontalDistanceTo(s.Position) <= b.InfluenceRadius)
                    sum += s.Development;
            return sum * rate;
        }

        /// <summary>Task99: tallies development inside EconomyRadius by zone into the three-resource
        /// income. Ownerless bases yield all zeros (the same convention as ForBase).</summary>
        public static ZonedIncome ZonedForBase(MilitaryBase b, IEnumerable<DevelopmentSample> samples, float rate)
        {
            var inc = new ZonedIncome();
            if (b.OwnerFactionId == null) return inc;
            foreach (var s in samples)
            {
                if (b.Position.HorizontalDistanceTo(s.Position) > EconomyRadius) continue;
                switch (s.Zone)
                {
                    case ZoneKind.Residential: inc.Manpower += s.Development * rate; break;
                    case ZoneKind.CommercialOffice: inc.Funds += s.Development * rate; break;
                    case ZoneKind.Industrial: inc.Production += s.Development * rate; break;
                }
            }
            return inc;
        }
    }
}
