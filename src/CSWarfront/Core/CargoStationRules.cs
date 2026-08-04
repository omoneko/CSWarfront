namespace CSWarfront.Core
{
    /// <summary>Task101: 軍用貨物駅の稼働判定（設計§3）。駅はレール網（WarState.Rails）の
    /// ノードがRailSnapRadius以内にあるときだけ「接続済み」となり、鉄道輸送（TrainStep）に使われる。
    /// 未接続でも備蓄（StoredSupplies）・占領は機能する。</summary>
    public static class CargoStationRules
    {
        /// <summary>駅とみなすレールノードまでの最大距離（m）。</summary>
        public const float RailSnapRadius = 100f;

        /// <summary>全貨物駅のRailConnectedを引き直す（レール網の構築/再構築のたびにGame層が呼ぶ）。</summary>
        public static void RefreshConnectivity(WarState state)
        {
            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.Type != BaseType.CargoStation) continue;
                ushort nodeId;
                b.RailConnected = state.Rails != null &&
                    state.Rails.TryFindNearestNode(b.Position, RailSnapRadius, out nodeId);
            }
        }

        /// <summary>この駅は鉄道輸送に使えるか（所有・レール接続・HP残存）。</summary>
        public static bool IsOperational(MilitaryBase b)
        {
            return b != null && b.Type == BaseType.CargoStation
                && b.OwnerFactionId != null && b.RailConnected && b.CurrentHP > 0f;
        }
    }
}
