using System;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// 電力（Electricity）タブに独自の「軍事基地」BuildingInfo を実行時登録する。
    /// アセットエディタを使わず、既存の電力系プレハブの GameObject を Instantiate で複製することで、
    /// setter非公開の m_UICategory 等の private フィールドを一切のリフレクション無しで引き継ぐ
    /// （.superpowers/sdd/research-power-tab-building.md §6 で検証済みの経路）。
    /// Task18でこのプレハブの配置をロジック上の基地（MilitaryBase）に紐付ける。本タスクではプレハブ登録のみ。
    /// </summary>
    public static class WarfrontBasePrefab
    {
        public const string PrefabName = "CSWarfront Military Base";
        private const string CollectionName = "CSWarfront";

        public static BuildingInfo Prefab { get; private set; }
        public static bool IsRegistered { get { return Prefab != null; } }

        /// <summary>冪等。ゲームロード毎（OnLevelLoaded）に呼ばれる想定。</summary>
        public static void EnsureRegistered()
        {
            if (IsRegistered)
            {
                ModConfig.Log("WarfrontBasePrefab.EnsureRegistered: already registered ('" + Prefab.name + "'), skip");
                return;
            }

            try
            {
                BuildingInfo source = FindElectricitySource();
                if (source == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab: no suitable PowerPlantAI-family Electricity-service BuildingInfo found among " +
                        PrefabCollection<BuildingInfo>.LoadedCount() + " loaded prefabs (need m_electricityProduction > 0); aborting registration");
                    return;
                }
                PowerPlantAI sourceAi = source.m_buildingAI as PowerPlantAI;
                ModConfig.Log("WarfrontBasePrefab: source prefab chosen = '" + source.name + "' (category='" + SafeCategory(source) + "')" +
                    " aiType=" + (source.m_buildingAI != null ? source.m_buildingAI.GetType().Name : "null") +
                    " m_electricityProduction=" + (sourceAi != null ? sourceAi.m_electricityProduction.ToString() : "?") +
                    " m_resourceType=" + (sourceAi != null ? sourceAi.m_resourceType.ToString() : "?") +
                    " m_resourceConsumption=" + (sourceAi != null ? sourceAi.m_resourceConsumption.ToString() : "?") +
                    " m_resourceCapacity=" + (sourceAi != null ? sourceAi.m_resourceCapacity.ToString() : "?") +
                    " m_isRenewable=" + (sourceAi != null ? sourceAi.m_isRenewable.ToString() : "?") +
                    " workPlaceCountSum=" + (sourceAi != null ? (sourceAi.m_workPlaceCount0 + sourceAi.m_workPlaceCount1 + sourceAi.m_workPlaceCount2 + sourceAi.m_workPlaceCount3).ToString() : "?"));

                GameObject sourceGo = source.gameObject;
                if (sourceGo == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab: source prefab '" + source.name + "' has null gameObject; aborting");
                    return;
                }

                GameObject cloneGo = (GameObject)UnityEngine.Object.Instantiate(sourceGo);
                BuildingInfo clone = cloneGo.GetComponent<BuildingInfo>();
                if (clone == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab: clone GameObject has no BuildingInfo component; aborting");
                    UnityEngine.Object.Destroy(cloneGo);
                    return;
                }

                clone.name = PrefabName;
                cloneGo.name = PrefabName;
                clone.m_prefabInitialized = false;
                ModConfig.Log("WarfrontBasePrefab: clone created and renamed to '" + PrefabName + "'");

                if (clone.m_class == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab: cloned prefab '" + PrefabName + "' has null m_class after clone; aborting");
                    return;
                }
                if (clone.m_class.m_service != ItemClass.Service.Electricity)
                {
                    ModConfig.LogError("WarfrontBasePrefab: cloned prefab m_class.m_service = " + clone.m_class.m_service +
                        " (expected Electricity); aborting");
                    return;
                }

                SanitizeClonedPowerPlantAi(clone);

                PrefabCollection<BuildingInfo>.InitializePrefabs(CollectionName, clone, null);
                ModConfig.Log("WarfrontBasePrefab: InitializePrefabs('" + CollectionName + "', '" + PrefabName + "', null) done");

                PrefabCollection<BuildingInfo>.BindPrefabs();
                ModConfig.Log("WarfrontBasePrefab: BindPrefabs() done");

                RefreshPanels();

                Prefab = clone;
                ModConfig.Log("WarfrontBasePrefab: registration COMPLETE, Prefab.name='" + Prefab.name + "'");
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefab.EnsureRegistered exception: " + e);
            }
        }

        /// <summary>
        /// 電力（Electricity）サービスのプレハブから複製元を選ぶ。
        /// 単に最初に見つかった電力系プレハブを使うと、電柱/変電所など PowerPlantAI の数値フィールドが
        /// 全てゼロの物を拾ってしまい、ゲーム本体の PowerPlantAI.ProduceGoods 内の整数除算が
        /// DivideByZeroException でクラッシュする（実機ログで確認済み）。
        /// そのため BuildingAI が PowerPlantAI 系統かつ m_electricityProduction > 0 の物だけを候補とし、
        /// 中でも再生可能エネルギー（燃料消費の分岐が無い＝m_resourceType==None）を優先する。
        /// AI参照の取得は reflection で確認した BuildingInfo.m_buildingAI（public フィールド）を使う
        /// （GetComponent&lt;BuildingAI&gt;() も動作するはずだが、こちらは既にゲームが解決済みの参照であり
        /// 追加のコンポーネント探索コストが無いため採用）。
        /// </summary>
        private static BuildingInfo FindElectricitySource()
        {
            BuildingInfo bestRenewableNoFuel = null;
            BuildingInfo bestAnyProducing = null;

            int count = PrefabCollection<BuildingInfo>.LoadedCount();
            for (uint i = 0; i < (uint)count; i++)
            {
                BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded(i);
                if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Electricity) continue;

                PowerPlantAI ai = info.m_buildingAI as PowerPlantAI;
                if (ai == null) continue; // 電柱・変電所など PowerPlantAI を持たない物は除外
                if (ai.m_electricityProduction <= 0) continue; // 数値フィールドが全ゼロの物（ゼロ割の元）を除外

                if (bestAnyProducing == null) bestAnyProducing = info;

                if (ai.m_isRenewable && ai.m_resourceType == TransferManager.TransferReason.None)
                {
                    bestRenewableNoFuel = info;
                    break; // 最優先条件が見つかったので探索終了
                }
            }

            if (bestRenewableNoFuel != null) return bestRenewableNoFuel;
            return bestAnyProducing; // 見つからなければ null（呼び出し元が中止する）
        }

        /// <summary>
        /// 複製後の AI が PowerPlantAI 系統であれば、ゲーム本体の PowerPlantAI.ProduceGoods 等が
        /// 除算に使う可能性のあるフィールドを、どれが分母に使われても安全なようゼロ回避値に強制する
        /// （多層防御: FindElectricitySource で健全な複製元を選んでいても、将来複製元の実装が変わる、
        /// もしくは複製時にUnity側で値が失われるケースに備える）。
        /// 意図的にクローンは「小さな発電所」として振る舞う（若干の電力を供給する）— MVPとして許容。
        /// 専用アセットが用意でき次第、置き換え可能。
        /// </summary>
        private static void SanitizeClonedPowerPlantAi(BuildingInfo clone)
        {
            PowerPlantAI ai = clone.m_buildingAI as PowerPlantAI;
            if (ai == null) return;

            TransferManager.TransferReason oldResourceType = ai.m_resourceType;
            if (ai.m_resourceType != TransferManager.TransferReason.None)
            {
                ai.m_resourceType = TransferManager.TransferReason.None;
                ModConfig.Log("WarfrontBasePrefab: sanitize m_resourceType " + oldResourceType + " -> None");
            }

            if (ai.m_electricityProduction <= 0)
            {
                int old = ai.m_electricityProduction;
                ai.m_electricityProduction = 16;
                ModConfig.Log("WarfrontBasePrefab: sanitize m_electricityProduction " + old + " -> 16");
            }

            if (ai.m_resourceCapacity <= 0)
            {
                int old = ai.m_resourceCapacity;
                ai.m_resourceCapacity = 1;
                ModConfig.Log("WarfrontBasePrefab: sanitize m_resourceCapacity " + old + " -> 1");
            }

            if (ai.m_resourceConsumption <= 0)
            {
                int old = ai.m_resourceConsumption;
                ai.m_resourceConsumption = 1;
                ModConfig.Log("WarfrontBasePrefab: sanitize m_resourceConsumption " + old + " -> 1");
            }

            if (ai.m_workPlaceCount0 + ai.m_workPlaceCount1 + ai.m_workPlaceCount2 + ai.m_workPlaceCount3 == 0)
            {
                ai.m_workPlaceCount0 = 1;
                ModConfig.Log("WarfrontBasePrefab: sanitize workPlaceCountSum 0 -> 1 (m_workPlaceCount0=1)");
            }

            if (ai.m_constructionCost <= 0)
            {
                int old = ai.m_constructionCost;
                ai.m_constructionCost = 1000;
                ModConfig.Log("WarfrontBasePrefab: sanitize m_constructionCost " + old + " -> 1000");
            }

            if (ai.m_maintenanceCost <= 0)
            {
                int old = ai.m_maintenanceCost;
                ai.m_maintenanceCost = 100;
                ModConfig.Log("WarfrontBasePrefab: sanitize m_maintenanceCost " + old + " -> 100");
            }
        }

        private static string SafeCategory(BuildingInfo info)
        {
            try { return info.category; }
            catch (Exception) { return "?"; }
        }

        /// <summary>
        /// OnLevelLoaded 時点でパネルが既にPopulate済みの場合に備え、明示的に再描画させる。
        /// パネルが未生成でも失敗させない（null ガード＋個別 try/catch でログのみ）。
        /// </summary>
        private static void RefreshPanels()
        {
            try
            {
                ElectricityGroupPanel groupPanel = UnityEngine.Object.FindObjectOfType<ElectricityGroupPanel>();
                if (groupPanel != null)
                {
                    groupPanel.RefreshPanel();
                    ModConfig.Log("WarfrontBasePrefab: ElectricityGroupPanel.RefreshPanel() called");
                }
                else
                {
                    ModConfig.Log("WarfrontBasePrefab: ElectricityGroupPanel not found via FindObjectOfType (may not be instantiated yet)");
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefab: ElectricityGroupPanel refresh failed: " + e);
            }

            try
            {
                ElectricityPanel panel = UnityEngine.Object.FindObjectOfType<ElectricityPanel>();
                if (panel != null)
                {
                    panel.RefreshPanel();
                    ModConfig.Log("WarfrontBasePrefab: ElectricityPanel.RefreshPanel() called");
                }
                else
                {
                    ModConfig.Log("WarfrontBasePrefab: ElectricityPanel not found via FindObjectOfType (may not be instantiated yet)");
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefab: ElectricityPanel refresh failed: " + e);
            }
        }
    }
}
