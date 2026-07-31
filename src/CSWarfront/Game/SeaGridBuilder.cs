using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Core.SeaGrid（海上航行グリッド、Task92）のGame層ビルダー。WaterSampler（喫水考慮のIsWater、
    /// Task88）でセル中心を判定して埋める。RoadGraphBuilderと同じ供給パターン：simスレッド専用
    /// （MilitaryManager.OnSimTickから）、失敗時はnullを返しゲームループへ例外を投げない。
    ///
    /// グリッド範囲: マップ中央±HalfExtent（4800m）。CSの購入可能25タイル（中央4.32km四方）を
    /// 余白込みで覆う。セル96m → 100×100 = 10,000セル。1セルにつきTerrainManagerの
    /// HasWater/WaterLevel/SampleDetailHeight（WaterSampler.IsWater内）を1回呼ぶだけなので、
    /// フルビルドは1tick内で十分終わる軽さ（RoadGraphBuilderのフルスキャンと同程度）。
    /// 水域はプレイヤーの地形改変・水源操作で変わり得るため、SimTick側で一定間隔（24h）ごとに
    /// 作り直す（道路網の12hより長め＝水はめったに変わらない）。
    /// </summary>
    internal static class SeaGridBuilder
    {
        private const float HalfExtent = 4800f;
        private const float CellSize = 96f;

        public static SeaGrid Build()
        {
            try
            {
                int cells = (int)(HalfExtent * 2f / CellSize);
                var grid = new SeaGrid(-HalfExtent, -HalfExtent, CellSize, cells, cells);
                var water = new WaterSampler();

                int navigable = 0;
                for (int cz = 0; cz < cells; cz++)
                {
                    for (int cx = 0; cx < cells; cx++)
                    {
                        WorldPos center = grid.CellCenter(cx, cz);
                        if (water.IsWater(center.X, center.Z))
                        {
                            grid.SetNavigable(cx, cz, true);
                            navigable++;
                        }
                    }
                }

                ModConfig.Log("SeaGridBuilder: built " + cells + "x" + cells + " grid, navigable cells=" + navigable);
                return navigable > 0 ? grid : null; // 水の無い内陸マップではnull＝従来挙動のまま
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("SeaGridBuilder.Build error: " + e);
                return null;
            }
        }
    }
}
