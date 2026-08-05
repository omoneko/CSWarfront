using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>Task101: 軍用貨物駅の稼働判定（設計§3）。駅はレール網（WarState.Rails）の
    /// ノードがRailSnapRadius以内にあるときだけ「接続済み」となり、鉄道輸送（TrainStep）に使われる。
    /// 未接続でも備蓄（StoredSupplies）・占領は機能する。</summary>
    public static class CargoStationRules
    {
        /// <summary>駅とみなすレールノードまでの最大距離（m）。</summary>
        public const float RailSnapRadius = 100f;

        /// <summary>Task108: 本線網（最大の連結成分）上の進入点を探すときの最大距離（m）。
        /// RailSnapRadiusより広いのは、駅の真横が「その駅の引き込み線＝本線から分断された小さな成分」
        /// である場合に、少し離れた本線のノードを掴ませるため（実機で路線が1本も成立しなかった
        /// ケースの対策）。</summary>
        public const float RailEntryRadius = 300f;

        /// <summary>全貨物駅のRailConnected／RailEntry（列車が実際に発着するレール上の地点）を
        /// 引き直す（レール網の構築/再構築のたびにGame層が呼ぶ）。
        /// 進入点は「本線網（最大の連結成分）でRailEntryRadius以内の最寄りノード」を第一候補とし、
        /// 見つからなければ従来どおりRailSnapRadius以内の最寄りノードへフォールバックする。</summary>
        public static void RefreshConnectivity(WarState state)
        {
            Dictionary<ushort, int> components = null;
            int mainComponent = 0;
            bool hasMain = false;
            if (state.Rails != null)
            {
                components = state.Rails.ComputeComponentIds();
                hasMain = state.Rails.TryGetLargestComponent(components, out mainComponent);
            }

            for (int i = 0; i < state.Bases.Count; i++)
            {
                MilitaryBase b = state.Bases[i];
                if (b.Type != BaseType.CargoStation) continue;

                b.RailEntry = null;
                if (state.Rails == null) { b.RailConnected = false; continue; }

                // 本線網の進入点が少し遠くても（RailSnapRadius超でも）採用する——駅の真横にある
                // 引き込み線だけを掴んで孤立するより、本線へ出られる方が常に望ましい。
                ushort nodeId = 0;
                bool found = hasMain && state.Rails.TryFindNearestNode(
                    b.Position, RailEntryRadius, components, mainComponent, out nodeId);
                if (!found) found = state.Rails.TryFindNearestNode(b.Position, RailSnapRadius, out nodeId);

                b.RailConnected = found;
                WorldPos entry;
                if (found && state.Rails.TryGetNodePosition(nodeId, out entry)) b.RailEntry = entry;
            }
        }

        /// <summary>Task108: この駅で列車が発着するレール上の地点（進入点。未解決なら駅そのものの位置）。</summary>
        public static WorldPos RailPointOf(MilitaryBase b)
        {
            return b.RailEntry.HasValue ? b.RailEntry.Value : b.Position;
        }

        /// <summary>この駅は鉄道輸送に使えるか（所有・レール接続・HP残存）。</summary>
        public static bool IsOperational(MilitaryBase b)
        {
            return b != null && b.Type == BaseType.CargoStation
                && b.OwnerFactionId != null && b.RailConnected && b.CurrentHP > 0f;
        }
    }
}
