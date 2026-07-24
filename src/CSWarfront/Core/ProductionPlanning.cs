namespace CSWarfront.Core
{
    /// <summary>各勢力が軍資金を使って所有基地の生産キューを補充する（純ロジック・決定的）。</summary>
    public static class ProductionPlanning
    {
        public const int QueueCap = 2;             // 1基地あたり最大キュー長
        public const string MvpUnitKey = "Tank_T1";

        public static void Advance(WarState state)
        {
            for (int fi = 0; fi < state.Factions.Count; fi++)
            {
                Faction f = state.Factions[fi];
                if (f.Eliminated) continue;
                UnitType type = state.Types.Get(MvpUnitKey);
                if (type == null) continue;
                for (int bi = 0; bi < state.Bases.Count; bi++)
                {
                    MilitaryBase b = state.Bases[bi];
                    if (b.OwnerFactionId == null || b.OwnerFactionId.Value != f.Id) continue;
                    while (b.Queue.Count < QueueCap && f.TrySpend(type.Cost))
                        b.Queue.Add(new ProductionOrder(type.TypeKey, type.Cost, type.BuildTime));
                }
            }
        }
    }
}
