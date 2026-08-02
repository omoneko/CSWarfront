using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// 基地情報パネル（Game/UI/BaseInfoPanel）向けの読み取り専用スナップショット（Task25）。
    /// UIが WarState / MilitaryBase へ直接触れずに済むよう、MilitaryManager.TryGetBaseSnapshot が
    /// _stateLock 内で値をコピーして渡す。500行制限のため MilitaryManager.cs から分離（Task30）。
    /// </summary>
    public struct BaseUiSnapshot
    {
        public byte? OwnerFactionId;
        public float CurrentHP;
        public float MaxHP;
        public float CaptureGraceHours;
        public int QueueCount;
        public bool IsHeadquarters;

        /// <summary>先頭の生産オーダー（キューが空なら空文字列）。何を作っているかをUIに出すため（Task30）。</summary>
        public string ProducingTypeKey;
        /// <summary>先頭オーダーの進捗（0..1）。キューが空なら0。</summary>
        public float ProducingProgress;
        /// <summary>先頭オーダーのビルド時間（ゲーム内時間）。キューが空なら0。</summary>
        public float ProducingBuildTime;
        /// <summary>所属勢力の軍資金。未所属なら0。</summary>
        public float OwnerTreasury;
        /// <summary>所属勢力が現在保有する生存ユニット数。未所属なら0。</summary>
        public int OwnerUnitCount;

        /// <summary>trueならAIがこの基地のキューを自動補充する（Task34、MilitaryBase.AutoProduceの写し）。</summary>
        public bool AutoProduce;

        /// <summary>trueならAI（MissileDoctrine）がこの基地から弾道ミサイルを自動発射する
        /// （Task90、MilitaryBase.AutoLaunchMissilesの写し。MissileBase以外では未使用）。</summary>
        public bool AutoLaunchMissiles;

        /// <summary>現在のキュー内容をTypeKeyだけの配列にした表示用コピー（Task34）。
        /// index 0 == 生産中（ProducingTypeKeyと同じ内容）。選択中の1基地分のみ構築するため
        /// 毎tickのホットパスではない（TryGetBaseSnapshot呼び出し時のみ）。</summary>
        public string[] QueuedTypeKeys;

        /// <summary>直近の経済tickでこの基地から実際に加算された収入（Task35。MilitaryBase.LastIncomeの
        /// 写し）。未所属でも0として表示できるよう常に埋める。</summary>
        public float LastIncome;

        /// <summary>所属勢力の研究点（Task35）。未所属なら0。</summary>
        public float OwnerResearchPoints;

        /// <summary>所属勢力が解禁済みの最大生産Tier（1..5、Task35）。未所属なら1（Faction既定値）。</summary>
        public byte OwnerUnlockedTier;

        /// <summary>次のTier解禁に必要な研究点（Research.CostToUnlock(OwnerUnlockedTier+1)、Task35）。
        /// 既に最大Tier（5）または未所属なら0。</summary>
        public float OwnerNextTierCost;

        /// <summary>この基地の種別（Army/Navy/AirForce/MissileBase、Task61）。BaseInfoPanelが
        /// 「陸軍基地/海軍基地/航空基地」等の表示に使う。</summary>
        public BaseType Type;

        /// <summary>この基地が生産できる領域（MilitaryBase.SpawnableDomainsの写し、Task61）。
        /// BaseInfoPanelが「生産可能: 陸上」等の表示に使う。</summary>
        public DomainMask SpawnableDomains;

        /// <summary>完成済みの弾道ミサイル備蓄数（Task63、MilitaryBase.StockpiledMissilesの写し）。
        /// BaseType.MissileBase以外では常に0。</summary>
        public int StockpiledMissiles;

        /// <summary>現在建造中のミサイルの進捗（0..1、Task63、MilitaryBase.MissileBuildProgressの写し）。</summary>
        public float MissileBuildProgress;

        /// <summary>建造中か（MissileStockpile.IsBuildingの写し、Task63）。</summary>
        public bool IsBuildingMissile;

        // --- Task99: 3資源経済＋補給物資（所有勢力のプールの写し） ---
        public float OwnerManpower;
        public float OwnerProduction;
        public float OwnerSupplyStock;
    }

    /// <summary>
    /// BaseUiSnapshot の組み立てロジック（Task30）。MilitaryManager.TryGetBaseSnapshot の
    /// _stateLock 内から呼ばれる想定 — 呼び出し側がロックを保持していること（このクラス自体はロックしない）。
    /// MilitaryManager.cs の500行制限のため分離。
    /// </summary>
    internal static class BaseUiSnapshotBuilder
    {
        public static BaseUiSnapshot Build(MilitaryBase mb, WarState state)
        {
            string producingTypeKey = "";
            float producingProgress = 0f;
            float producingBuildTime = 0f;
            if (mb.Queue.Count > 0)
            {
                ProductionOrder head = mb.Queue[0];
                producingTypeKey = head.TypeKey;
                producingProgress = head.Progress;
                producingBuildTime = head.BuildTime;
            }

            float ownerTreasury = 0f;
            int ownerUnitCount = 0;
            float ownerResearchPoints = 0f;
            byte ownerUnlockedTier = 1;
            float ownerNextTierCost = 0f;
            float ownerManpower = 0f, ownerProduction = 0f, ownerSupplyStock = 0f; // Task99
            if (mb.OwnerFactionId.HasValue)
            {
                byte owner = mb.OwnerFactionId.Value;
                Faction f = state.FindFaction(owner);
                if (f != null)
                {
                    ownerTreasury = f.Treasury;
                    ownerManpower = f.Manpower;         // Task99: 3資源＋補給物資
                    ownerProduction = f.Production;
                    ownerSupplyStock = f.SupplyStock;
                    // Task35: 研究点・解禁Tier・次のTierまでのコストをUI表示用に写す。
                    ownerResearchPoints = f.ResearchPoints;
                    ownerUnlockedTier = f.UnlockedTier;
                    ownerNextTierCost = f.UnlockedTier < 5
                        ? Research.CostToUnlock((byte)(f.UnlockedTier + 1))
                        : 0f;
                }

                for (int u = 0; u < state.Units.Count; u++)
                {
                    UnitInstance unit = state.Units[u];
                    if (unit.FactionId == owner && unit.State != UnitState.Dead) ownerUnitCount++;
                }
            }

            // Task34: 選択中の1基地分のみ、キューのTypeKeyだけをUI表示用にコピーする。
            var queuedTypeKeys = new string[mb.Queue.Count];
            for (int q = 0; q < mb.Queue.Count; q++) queuedTypeKeys[q] = mb.Queue[q].TypeKey;

            return new BaseUiSnapshot
            {
                OwnerFactionId = mb.OwnerFactionId,
                CurrentHP = mb.CurrentHP,
                MaxHP = mb.MaxHP,
                CaptureGraceHours = mb.CaptureGraceHours,
                QueueCount = mb.Queue.Count,
                IsHeadquarters = mb.IsHeadquarters,
                ProducingTypeKey = producingTypeKey,
                ProducingProgress = producingProgress,
                ProducingBuildTime = producingBuildTime,
                OwnerTreasury = ownerTreasury,
                OwnerManpower = ownerManpower,       // Task99
                OwnerProduction = ownerProduction,
                OwnerSupplyStock = ownerSupplyStock,
                OwnerUnitCount = ownerUnitCount,
                AutoProduce = mb.AutoProduce,
                AutoLaunchMissiles = mb.AutoLaunchMissiles,
                QueuedTypeKeys = queuedTypeKeys,
                LastIncome = mb.LastIncome,
                OwnerResearchPoints = ownerResearchPoints,
                OwnerUnlockedTier = ownerUnlockedTier,
                OwnerNextTierCost = ownerNextTierCost,
                Type = mb.Type,
                SpawnableDomains = mb.SpawnableDomains,
                StockpiledMissiles = mb.StockpiledMissiles,
                MissileBuildProgress = mb.MissileBuildProgress,
                IsBuildingMissile = MissileStockpile.IsBuilding(mb)
            };
        }
    }
}
