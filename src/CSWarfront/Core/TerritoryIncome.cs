using System.Collections.Generic;
namespace CSWarfront.Core
{
    /// <summary>発展度サンプルのゾーン種別（Task99: 3資源経済）。Game層のDevelopmentSamplerが
    /// CSのItemClass.Serviceから分類する。Otherはどの資源にも寄与しない（公共施設等）。</summary>
    public enum ZoneKind { Other, Residential, CommercialOffice, Industrial }

    public struct DevelopmentSample
    {
        public WorldPos Position;
        public float Development;
        public ZoneKind Zone; // Task99: 既定Other（旧テスト/呼び出し元は資源産出に寄与しない）
    }

    /// <summary>ゾーン別収入（Task99）: 住宅→人的資源、商業/オフィス→資金、工業→生産力。</summary>
    public struct ZonedIncome
    {
        public float Manpower;
        public float Funds;
        public float Production;
    }

    /// <summary>基地の勢力圏内の発展度合計×レート＝収入（純ロジック）。</summary>
    public static class TerritoryIncome
    {
        /// <summary>Task99: 3資源経済のスキャン半径（ユーザー仕様「基地半径1km以内」）。
        /// InfluenceRadius（500m、占領・UIリング等の勢力圏）とは独立の経済専用半径。</summary>
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

        /// <summary>Task99: EconomyRadius圏内の発展度をゾーン別に集計して3資源の収入にする。
        /// 未所属基地は全て0（ForBaseと同じ規約）。</summary>
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
