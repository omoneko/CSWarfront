using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Game-layer builder for Core.SeaGrid (the naval navigation grid, Task92). Fills the grid by
    /// testing each cell center with WaterSampler (draft-aware IsWater, Task88). Same supply pattern
    /// as RoadGraphBuilder: sim thread only (from MilitaryManager.OnSimTick), returns null on failure
    /// and never throws into the game loop.
    ///
    /// Grid extent: map center ±HalfExtent (4800m). Covers CS's 25 purchasable tiles (the central
    /// 4.32km square) with margin. Cell 96m → 100×100 = 10,000 cells. Each cell makes only one call
    /// to TerrainManager's HasWater/WaterLevel/SampleDetailHeight (inside WaterSampler.IsWater), so a
    /// full build is light enough to finish well within one tick (comparable to RoadGraphBuilder's
    /// full scan). Because water areas can change through the player's terraforming and water-source
    /// manipulation, the SimTick side rebuilds at a fixed interval (24h) (longer than the road
    /// network's 12h = water rarely changes).
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
                return navigable > 0 ? grid : null; // On landlocked maps with no water, null = keep the previous behavior
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("SeaGridBuilder.Build error: " + e);
                return null;
            }
        }
    }
}
