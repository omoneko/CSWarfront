using System;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Globalization;
using CSWarfront.Game.Models;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// 電力（Electricity）タブに独自の「軍事基地」BuildingInfo を実行時登録する。
    /// アセットエディタを使わず、既存の電力系プレハブの GameObject を Instantiate で複製することで、
    /// setter非公開の m_UICategory 等の private フィールドを一切のリフレクション無しで引き継ぐ
    /// （.superpowers/sdd/research-power-tab-building.md §6 で検証済みの経路）。
    /// Task18でこのプレハブの配置をロジック上の基地（MilitaryBase）に紐付ける。本タスクではプレハブ登録のみ。
    /// Task33でこのプレハブ名に対するロケール文字列（建物名/説明）の登録を追加した。
    /// </summary>
    public static class WarfrontBasePrefab
    {
        public const string PrefabName = "CSWarfront Military Base";
        private const string CollectionName = "CSWarfront";

        // Task57: 既定(built-in)の見た目モデル。クローン直後の風力タービン見た目を、成功すれば
        // src/CSWarfront/Models/Building_MilitaryBase.obj へ差し替える（失敗時は元の見た目のまま）。
        private const string BuildingModelName = "Building_MilitaryBase";
        private static readonly Color BuildingModelColor = new Color(0.30f, 0.33f, 0.24f, 1f); // 軍用オリーブグレー

        // Task33: バニラ建物情報パネル（CityServiceWorldInfoPanel）が参照するロケール識別子。
        // Assembly-CSharp.dll の文字列テーブルをスキャンして実在を確認済み（BUILDING_TITLE /
        // BUILDING_DESC / BUILDING_SHORT_DESC）。
        private const string LocaleTitleId = "BUILDING_TITLE";
        private const string LocaleDescId = "BUILDING_DESC";
        private const string LocaleShortDescId = "BUILDING_SHORT_DESC";
        private const string LocaleTitleText = "CSWarfront 軍事基地";
        private const string LocaleDescText = "勢力の軍事拠点。部隊を生産し、勢力圏から軍資金を得る。";

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
                SourceSearchStats stats;
                BuildingInfo source = FindElectricitySource(out stats);

                int rejectedTotal = stats.RejectedZeroProduction + stats.RejectedDam + stats.RejectedSubBuildings + stats.RejectedWaterPlacement;
                ModConfig.Log("WarfrontBasePrefab: candidate scan complete: considered=" + stats.Considered +
                    " rejected=" + rejectedTotal +
                    " (zero-production=" + stats.RejectedZeroProduction +
                    ", dam=" + stats.RejectedDam +
                    ", sub-buildings=" + stats.RejectedSubBuildings +
                    ", water-placement=" + stats.RejectedWaterPlacement + ")");

                if (source == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab: no suitable land-placeable single-part PowerPlantAI-family Electricity-service BuildingInfo found among " +
                        PrefabCollection<BuildingInfo>.LoadedCount() + " loaded prefabs (considered=" + stats.Considered +
                        ", rejected zero-production=" + stats.RejectedZeroProduction +
                        ", dam=" + stats.RejectedDam +
                        ", sub-buildings=" + stats.RejectedSubBuildings +
                        ", water-placement=" + stats.RejectedWaterPlacement + "); aborting registration");
                    return;
                }
                PowerPlantAI sourceAi = source.m_buildingAI as PowerPlantAI;
                int sourceSubBuildingCount = source.m_subBuildings != null ? source.m_subBuildings.Length : 0;
                ModConfig.Log("WarfrontBasePrefab: source prefab chosen = '" + source.name + "' (category='" + SafeCategory(source) + "')" +
                    " aiType=" + (source.m_buildingAI != null ? source.m_buildingAI.GetType().Name : "null") +
                    " m_electricityProduction=" + (sourceAi != null ? sourceAi.m_electricityProduction.ToString() : "?") +
                    " m_resourceType=" + (sourceAi != null ? sourceAi.m_resourceType.ToString() : "?") +
                    " m_resourceConsumption=" + (sourceAi != null ? sourceAi.m_resourceConsumption.ToString() : "?") +
                    " m_resourceCapacity=" + (sourceAi != null ? sourceAi.m_resourceCapacity.ToString() : "?") +
                    " m_isRenewable=" + (sourceAi != null ? sourceAi.m_isRenewable.ToString() : "?") +
                    " workPlaceCountSum=" + (sourceAi != null ? (sourceAi.m_workPlaceCount0 + sourceAi.m_workPlaceCount1 + sourceAi.m_workPlaceCount2 + sourceAi.m_workPlaceCount3).ToString() : "?") +
                    " subBuildingCount=" + sourceSubBuildingCount +
                    " placementMode=" + source.m_placementMode);

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
                TrySwapVisualMesh(clone);

                PrefabCollection<BuildingInfo>.InitializePrefabs(CollectionName, clone, null);
                ModConfig.Log("WarfrontBasePrefab: InitializePrefabs('" + CollectionName + "', '" + PrefabName + "', null) done");

                PrefabCollection<BuildingInfo>.BindPrefabs();
                ModConfig.Log("WarfrontBasePrefab: BindPrefabs() done");

                RefreshPanels();

                Prefab = clone;
                RegisterLocalizedStrings();
                ModConfig.Log("WarfrontBasePrefab: registration COMPLETE, Prefab.name='" + Prefab.name + "'");
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefab.EnsureRegistered exception: " + e);
            }
        }

        /// <summary>候補走査の集計（ログ出力・abort判断用）。</summary>
        private struct SourceSearchStats
        {
            public int Considered;             // Electricityサービス かつ PowerPlantAI系統を持つ数
            public int RejectedZeroProduction;  // m_electricityProduction <= 0
            public int RejectedDam;             // DamPowerHouseAI（河川必須）
            public int RejectedSubBuildings;    // m_subBuildings 非空（オフショア随伴施設等の多部品プラント）
            public int RejectedWaterPlacement;  // m_placementMode が水上/岸辺必須
        }

        /// <summary>
        /// 電力（Electricity）サービスのプレハブから複製元を選ぶ。
        /// 単に最初に見つかった電力系プレハブを使うと、電柱/変電所など PowerPlantAI の数値フィールドが
        /// 全てゼロの物を拾ってしまい、ゲーム本体の PowerPlantAI.ProduceGoods 内の整数除算が
        /// DivideByZeroException でクラッシュする（実機ログで確認済み）。
        /// そのため BuildingAI が PowerPlantAI 系統かつ m_electricityProduction > 0 の物だけを候補とする。
        ///
        /// 加えて実機ログで「Ocean Thermal Energy Conversion Plant」（m_isRenewable=True,
        /// m_resourceType=None）が選ばれ、随伴するオフショア専用の子施設
        /// 'Ocean Thermal Energy Conversion Plant Offshore' が生成される事象を確認した。
        /// OTECは水上専用・複数パーツ構成のプラントであり、陸上の単体軍事基地の複製元として不適切。
        /// そのため「再生可能＝優先」という単純基準をやめ、以下を必須条件として追加する:
        ///   - m_subBuildings が空（随伴子施設を持たない単体施設）
        ///   - m_placementMode が水上/岸辺必須（Shoreline / OnWater / ShorelineOrGround）ではない
        ///   - DamPowerHouseAI（河川必須）は無条件で除外
        /// 上記を満たす候補の中で、AIの型により以下の優先順位で選ぶ:
        ///   WindTurbineAI &gt; SolarPowerPlantAI &gt; FusionPowerPlantAI &gt; PowerPlantAI（無印）
        /// （DamPowerHouseAI, FusionPowerPlantAI, SolarPowerPlantAI, WindTurbineAI が
        /// ゲーム本体に存在する PowerPlantAI の全サブクラスであることを reflection で確認済み。
        /// BuildingInfo.m_subBuildings / m_placementMode は public フィールドのためコンパイル時に
        /// 直接参照できる。フィールド名・enum値は Assembly-CSharp.dll を reflection で走査して確認した
        /// （m_subBuildings: BuildingInfo.SubInfo[]、m_placementMode: BuildingInfo.PlacementMode
        /// { Roadside, Shoreline, OnWater, OnGround, OnSurface, OnTerrain, ShorelineOrGround,
        /// PathsideOrGround, Concourse, PitLane }）。
        /// AI参照の取得は reflection で確認した BuildingInfo.m_buildingAI（public フィールド）を使う
        /// （GetComponent&lt;BuildingAI&gt;() も動作するはずだが、こちらは既にゲームが解決済みの参照であり
        /// 追加のコンポーネント探索コストが無いため採用）。
        /// </summary>
        private static BuildingInfo FindElectricitySource(out SourceSearchStats stats)
        {
            stats = new SourceSearchStats();
            BuildingInfo best = null;
            int bestTier = int.MaxValue;

            int count = PrefabCollection<BuildingInfo>.LoadedCount();
            for (uint i = 0; i < (uint)count; i++)
            {
                BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded(i);
                if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Electricity) continue;

                PowerPlantAI ai = info.m_buildingAI as PowerPlantAI;
                if (ai == null) continue; // 電柱・変電所など PowerPlantAI を持たない物は除外

                stats.Considered++;

                if (ai.m_electricityProduction <= 0)
                {
                    stats.RejectedZeroProduction++; // 数値フィールドが全ゼロの物（ゼロ割の元）を除外
                    continue;
                }
                if (ai is DamPowerHouseAI)
                {
                    stats.RejectedDam++; // 河川必須。陸上単体基地の複製元として不適切
                    continue;
                }
                int subBuildingCount = info.m_subBuildings != null ? info.m_subBuildings.Length : 0;
                if (subBuildingCount > 0)
                {
                    stats.RejectedSubBuildings++; // OTEC等の随伴子施設付き多部品プラントを除外
                    continue;
                }
                if (info.m_placementMode == BuildingInfo.PlacementMode.Shoreline ||
                    info.m_placementMode == BuildingInfo.PlacementMode.OnWater ||
                    info.m_placementMode == BuildingInfo.PlacementMode.ShorelineOrGround)
                {
                    stats.RejectedWaterPlacement++; // 水上/岸辺必須の配置は陸上基地に不適切
                    continue;
                }

                int tier = SourcePriorityTier(ai);
                if (tier < bestTier)
                {
                    bestTier = tier;
                    best = info;
                }
            }

            return best; // 見つからなければ null（呼び出し元が中止する）
        }

        /// <summary>
        /// PowerPlantAI系統内での複製元優先順位。数値が小さいほど優先。
        /// WindTurbineAI &gt; SolarPowerPlantAI &gt; FusionPowerPlantAI &gt; PowerPlantAI（無印/その他）。
        /// DamPowerHouseAI は呼び出し元で無条件除外済みのためここには到達しない。
        /// </summary>
        private static int SourcePriorityTier(PowerPlantAI ai)
        {
            if (ai is WindTurbineAI) return 0;
            if (ai is SolarPowerPlantAI) return 1;
            if (ai is FusionPowerPlantAI) return 2;
            return 3; // 無印 PowerPlantAI、または将来追加される未分類サブクラス
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

        /// <summary>
        /// Task57: クローン直後（風力タービン等の借用済み見た目のまま）の <paramref name="clone"/> の
        /// 描画メッシュ/マテリアルを、既定モデル Building_MilitaryBase.obj へ差し替える。
        /// 設定するフィールドは m_mesh / m_lodMesh / m_material / m_lodMaterial の4つ（全てpublic、
        /// BuildingInfoBase宣言。reflection-only読込でAssembly-CSharp.dllを検証済み）。LOD側も
        /// 同じメッシュ・マテリアルをそのまま流用する（専用LODメッシュの生成は行わない＝
        /// "if straightforward" の範囲でシンプルに済ませる）。
        /// 失敗経路: メッシュ読込（WarfrontModelProvider.TryGetMesh）またはマテリアル生成
        /// （UnitMaterialFactory.TryGetSolidColorMaterial）のどちらかが失敗した場合、あるいは
        /// 途中で例外が飛んだ場合は、クローンのフィールドを一切変更せずに return する
        /// （＝Instantiateで複製されたクローン元の風力タービン見た目がそのまま残る）。
        /// 呼び出し元 EnsureRegistered のプレハブ登録処理（InitializePrefabs以降）には一切影響しない。
        /// </summary>
        private static void TrySwapVisualMesh(BuildingInfo clone)
        {
            try
            {
                Mesh mesh;
                if (!WarfrontModelProvider.TryGetMesh(BuildingModelName, out mesh) || mesh == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab.TrySwapVisualMesh: built-in model '" + BuildingModelName +
                        "' の読み込みに失敗。既定（風力タービン借用）の見た目を維持します");
                    return;
                }

                Material material;
                if (!UnitMaterialFactory.TryGetSolidColorMaterial(BuildingModelColor, out material) || material == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab.TrySwapVisualMesh: 建物用マテリアル生成に失敗。既定の見た目を維持します");
                    return;
                }

                clone.m_mesh = mesh;
                clone.m_material = material;
                clone.m_lodMesh = mesh;
                clone.m_lodMaterial = material;

                ModConfig.Log("WarfrontBasePrefab.TrySwapVisualMesh: 見た目を built-in model '" + BuildingModelName + "' へ差し替えました");
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefab.TrySwapVisualMesh error（既定の見た目を維持します）: " + e);
            }
        }

        /// <summary>
        /// Task33: バニラの建物情報パネル（CityServiceWorldInfoPanel）は BuildingInfo.name をキーに
        /// BUILDING_TITLE / BUILDING_SHORT_DESC のロケール文字列を検索して表示する。クローンした
        /// プレハブ名（PrefabName）に対応するエントリが存在しないと、そのまま
        /// "BUILDING_TITLE[CSWarfront Military Base]:0" のような生キーが画面に出てしまう（実機で確認済み）。
        ///
        /// ロケールAPI（ColossalManaged.dll をリフレクション/IL検証済み、Task33）:
        ///   - ColossalFramework.SingletonLite&lt;LocaleManager&gt;.instance : public static プロパティ。
        ///     リフレクション不要で取得可能。
        ///   - LocaleManager.m_Locale : フィールドの実際のアクセシビリティは Assembly（internal）
        ///     （FieldInfo.Attributes で確認済み。IsPublic/IsPrivate は共にfalse）のため、
        ///     別アセンブリの本Modからは直接参照できずリフレクションが必須。型は
        ///     ColossalFramework.Globalization.Locale。
        ///   - Locale.Key : 値型（struct）。public フィールド m_Identifier(string) / m_Key(string) /
        ///     m_Index(int)、いずれも書き込み可（IsInitOnly=false）。専用コンストラクタは
        ///     ctor(string id) の1つのみで用途が異なるため使わず、オブジェクト初期化子で組み立てる。
        ///   - Locale.Exists(Locale.Key id) : public インスタンスメソッド、bool を返す
        ///     （Locale には Exists(string) 等 static オーバーロードも別途あるが、ここで使うのは
        ///     インスタンスメソッドの Key 版）。
        ///   - Locale.AddLocalizedString(Locale.Key k, string v) : public インスタンスメソッド、void。
        ///
        /// EnsureRegistered は冪等（Prefab != null なら即return）なので、このメソッドも実質セッション中
        /// 一度しか呼ばれない。ロケール切り替え（言語変更）時の再登録は本タスクのスコープ外
        /// （既知の制約。切り替え後は再びロケールキーが空になり得るが、ゲームクラッシュ等は起きない）。
        /// 失敗しても Prefab 自体の登録（建物として配置可能な状態）には影響させず、ログのみで継続する。
        /// </summary>
        private static void RegisterLocalizedStrings()
        {
            try
            {
                LocaleManager manager = SingletonLite<LocaleManager>.instance;
                if (manager == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab.RegisterLocalizedStrings: LocaleManager.instance is null; skip (raw locale key will show)");
                    return;
                }

                FieldInfo localeField = typeof(LocaleManager).GetField("m_Locale", BindingFlags.NonPublic | BindingFlags.Instance);
                Locale locale = localeField != null ? localeField.GetValue(manager) as Locale : null;
                if (locale == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab.RegisterLocalizedStrings: could not reflect LocaleManager.m_Locale; skip (raw locale key will show)");
                    return;
                }

                AddIfMissing(locale, LocaleTitleId, LocaleTitleText);
                AddIfMissing(locale, LocaleDescId, LocaleDescText);
                AddIfMissing(locale, LocaleShortDescId, LocaleDescText);

                ModConfig.Log("WarfrontBasePrefab: localized strings registered for '" + PrefabName + "'");
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefab.RegisterLocalizedStrings error (raw locale key will show): " + e);
            }
        }

        /// <summary>既に同じ Key が登録済みなら上書きしない（Exists→AddLocalizedStringの手順はタスク仕様通り）。</summary>
        private static void AddIfMissing(Locale locale, string identifier, string value)
        {
            Locale.Key key = new Locale.Key { m_Identifier = identifier, m_Key = PrefabName, m_Index = 0 };
            if (!locale.Exists(key))
            {
                locale.AddLocalizedString(key, value);
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
