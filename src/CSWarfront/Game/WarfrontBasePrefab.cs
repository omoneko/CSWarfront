using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Globalization;
using CSWarfront.Core;
using CSWarfront.Game.Models;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// 電力（Electricity）タブに3種の独自 BuildingInfo（陸軍/海軍/航空基地、Task61で海軍/航空を追加）を
    /// 実行時登録する。アセットエディタを使わず、既存の電力系プレハブの GameObject を Instantiate で複製
    /// することで、setter非公開の m_UICategory 等の private フィールドを一切のリフレクション無しで引き継ぐ
    /// （.superpowers/sdd/research-power-tab-building.md §6 で検証済みの経路）。
    ///
    /// Task18で陸軍基地1種のプレハブ配置をロジック上の基地（MilitaryBase）に紐付けた（クラス名は当時の
    /// 単数形 WarfrontBasePrefab のまま、ファイル名の破壊的変更を避けるため据え置き）。
    /// Task61で海軍/航空の2種を追加し、{BaseType, プレハブ名, モデルファイル, ロケール文言} の小さな
    /// テーブル（<see cref="Entry"/>/<see cref="Entries"/>）を1回だけ走査する形へ一般化した。
    /// 複製元（電力系プレハブ）の探索・サニタイズ処理は3種で共有する（同じ電力タブ内に3つとも並ぶため、
    /// 既定では全て同じ複製元から作る＝将来より適したタブが見つかれば入れ替え可能）。
    ///
    /// Task81: 配置手段としては非推奨（Task74のOptions指定建物に一本化）になったため、4種とも
    /// <c>m_availableIn = ItemClass.Availability.None</c> を設定し電力タブのツールバーからは見えなく
    /// する（RegisterOne参照）。ただし<b>登録自体は維持</b>する——既存セーブに置かれた基地の
    /// 建物Infoはこのクローンプレハブそのものであり、<see cref="TryMatch"/>/<see cref="IsOwnBase"/>が
    /// 参照一致・名前一致で照合し続けるため、プレハブを削除すると既存セーブの基地が読み込み時に
    /// 壊れる（Info解決不能）。「隠すが消さない」がTask81の設計方針。
    /// </summary>
    public static class WarfrontBasePrefab
    {
        // 後方互換: Task18時点の陸軍基地の値をそのまま公開する（BasePlacementWatcher/CoverMapBuilder等、
        // 既存の「単一の基地プレハブ」を前提にしたコードが無くなるまでのブリッジ）。
        public const string PrefabName = "CSWarfront Military Base";
        private const string CollectionName = "CSWarfront";

        private struct Entry
        {
            public BaseType Type;
            public string PrefabName;
            public string ModelName;
            public Color ModelColor;
            public string LocaleTitleText;
            public string LocaleDescText;
        }

        // Task33のロケール識別子（バニラCityServiceWorldInfoPanelが参照する）はTask61でも変更しない。
        private const string LocaleTitleId = "BUILDING_TITLE";
        private const string LocaleDescId = "BUILDING_DESC";
        private const string LocaleShortDescId = "BUILDING_SHORT_DESC";

        // Task61: 電力（Electricity）タブに3つとも並べる。海軍/航空により適したUICategory/タブが
        // 将来見つかれば、Entry.Type別に複製元探索を分ければ差し替えられる（現状はシンプルさを優先し
        // 3種とも同じ複製元・同じタブを共有する）。
        private static readonly Entry[] Entries =
        {
            new Entry
            {
                Type = BaseType.Army, PrefabName = "CSWarfront Military Base", ModelName = "Building_MilitaryBase",
                ModelColor = new Color(0.30f, 0.33f, 0.24f, 1f), // 軍用オリーブグレー
                LocaleTitleText = "CSWarfront 陸軍基地", LocaleDescText = "勢力の陸上部隊拠点。陸上兵科を生産し、勢力圏から軍資金を得る。"
            },
            new Entry
            {
                Type = BaseType.Navy, PrefabName = "CSWarfront Naval Base", ModelName = "Building_NavalBase",
                ModelColor = new Color(0.20f, 0.28f, 0.34f, 1f), // 軍用スレートブルー
                LocaleTitleText = "CSWarfront 海軍基地", LocaleDescText = "勢力の海上部隊拠点。駆逐艦・空母を生産する。"
            },
            new Entry
            {
                Type = BaseType.AirForce, PrefabName = "CSWarfront Air Base", ModelName = "Building_AirBase",
                ModelColor = new Color(0.34f, 0.34f, 0.30f, 1f), // 軍用コンクリートグレー
                LocaleTitleText = "CSWarfront 航空基地", LocaleDescText = "勢力の航空部隊拠点。戦闘機・爆撃機・自爆ドローンを生産する。"
            },
            new Entry
            {
                // Task63: 弾道ミサイル基地。他3種と違いユニットを一切生産しない
                // （MilitaryBase.SpawnableDomains=None、ミサイルはMissileStockpile経由で備蓄する）。
                Type = BaseType.MissileBase, PrefabName = "CSWarfront Missile Base", ModelName = "Building_MissileBase",
                ModelColor = new Color(0.32f, 0.29f, 0.20f, 1f), // 軍用カーキ
                LocaleTitleText = "CSWarfront ミサイル基地", LocaleDescText = "勢力の弾道ミサイル拠点。ミサイルを備蓄し、遠方の敵拠点へ発射する。"
            },
        };

        private static readonly Dictionary<BaseType, BuildingInfo> _prefabs = new Dictionary<BaseType, BuildingInfo>();

        // 後方互換プロパティ（Task18時点のAPI）。陸軍基地1種のみを見る既存コード用。
        public static BuildingInfo Prefab { get { BuildingInfo p; return _prefabs.TryGetValue(BaseType.Army, out p) ? p : null; } }
        public static bool IsRegistered { get { return _prefabs.ContainsKey(BaseType.Army); } }

        /// <summary>いずれか1種でも登録済みか（Task61）。</summary>
        public static bool IsAnyRegistered { get { return _prefabs.Count > 0; } }

        /// <summary>冪等。ゲームロード毎（OnLevelLoaded）に呼ばれる想定。3種すべての登録を試みる
        /// （1種が既に登録済みでもスキップせず、未登録の残りを試す＝将来的な部分失敗からの回復を許容）。</summary>
        public static void EnsureRegistered()
        {
            if (_prefabs.Count == Entries.Length)
            {
                ModConfig.Log("WarfrontBasePrefab.EnsureRegistered: all " + Entries.Length + " base prefabs already registered, skip");
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

                for (int i = 0; i < Entries.Length; i++)
                {
                    Entry entry = Entries[i];
                    if (_prefabs.ContainsKey(entry.Type)) continue; // 既に登録済み
                    RegisterOne(entry, source);
                }

                // Task81: 旧実装はここで ElectricityGroupPanel/ElectricityPanel.RefreshPanel() を呼んで
                // いた（登録直後に電力タブへ新しいボタンを即座に表示させるため）。RegisterOne が
                // clone.m_availableIn = Availability.None を設定するようになった今、クローンは
                // どのパネルのPopulateAssets/PopulateGroupsでも最初から除外される＝表示すべきボタンが
                // 存在しないため、このリフレッシュは無意味になった。呼び出しと専用メソッドを削除。
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefab.EnsureRegistered exception: " + e);
            }
        }

        /// <summary>指定BaseTypeのプレハブが登録済みならtrueで返す。</summary>
        public static bool TryGetPrefab(BaseType type, out BuildingInfo prefab)
        {
            return _prefabs.TryGetValue(type, out prefab);
        }

        /// <summary>infoが登録済みのいずれかの基地プレハブに一致すれば、そのBaseTypeを返す（Task61）。
        /// 参照一致を優先し、ダメなら名前一致（ゲームがプレハブを再インスタンス化した場合の保険、
        /// Task18からの既存方針を踏襲）。</summary>
        public static bool TryMatch(BuildingInfo info, out BaseType type)
        {
            type = default(BaseType);
            if (info == null) return false;

            foreach (var kv in _prefabs)
            {
                if (ReferenceEquals(info, kv.Value)) { type = kv.Key; return true; }
            }
            foreach (var kv in _prefabs)
            {
                if (info.name == PrefabNameFor(kv.Key)) { type = kv.Key; return true; }
            }
            return false;
        }

        /// <summary>infoが登録済みの自MOD基地プレハブのいずれかであるか（Task61、CoverMapBuilderが
        /// 自陣営の基地建物を遮蔽物マップから除外するために使う）。</summary>
        public static bool IsOwnBase(BuildingInfo info)
        {
            BaseType ignored;
            return TryMatch(info, out ignored);
        }

        private static string PrefabNameFor(BaseType type)
        {
            for (int i = 0; i < Entries.Length; i++)
                if (Entries[i].Type == type) return Entries[i].PrefabName;
            return null;
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
        ///
        /// Task61: 3種の基地プレハブすべてがこの1つの複製元を共有する（EnsureRegistered参照、
        /// 探索コストを3倍にしないため）。
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

        /// <summary>Task61: 1エントリ分（陸軍/海軍/航空のいずれか1種）を複製・サニタイズ・見た目差し替え・
        /// 登録・ロケール登録までまとめて行う。EnsureRegisteredのループから呼ばれる。</summary>
        private static void RegisterOne(Entry entry, BuildingInfo source)
        {
            GameObject sourceGo = source.gameObject;
            if (sourceGo == null)
            {
                ModConfig.LogError("WarfrontBasePrefab: source prefab '" + source.name + "' has null gameObject; aborting '" + entry.PrefabName + "'");
                return;
            }

            GameObject cloneGo = (GameObject)UnityEngine.Object.Instantiate(sourceGo);
            BuildingInfo clone = cloneGo.GetComponent<BuildingInfo>();
            if (clone == null)
            {
                ModConfig.LogError("WarfrontBasePrefab: clone GameObject has no BuildingInfo component; aborting '" + entry.PrefabName + "'");
                UnityEngine.Object.Destroy(cloneGo);
                return;
            }

            clone.name = entry.PrefabName;
            cloneGo.name = entry.PrefabName;
            clone.m_prefabInitialized = false;

            // Task81: 電力タブのツールバーから外す（登録自体は維持——既存セーブの基地が
            // BasePlacementWatcher/ReconcileBasesの照合対象であり続けるため、プレハブそのものは
            // 消せない）。GeneratedScrollPanel.CollectAssets / GeneratedGroupPanel.CollectAssets は
            // どちらも IsPlacementRelevant(BuildingInfo) を通し、その中身は
            // `info.m_availableIn.IsFlagSet(Singleton<ToolManager>.instance.m_properties.m_mode)`
            // （Assembly-CSharp.dll、GeneratedScrollPanel.decompiled.cs 2193-2201行 / GeneratedGroupPanel
            // 同様の行、ilspycmdでの逆コンパイルで確認済み）。IsFlagSet<T>はColossalManaged.dllの
            // EnumExtensions.IsFlagSet（`(value.ToInt64() & flag.ToInt64()) != 0`）で、m_availableInが
            // Availability.None（値0）だと現在のToolManagerモード（Game/MapEditor/AssetEditor等）が
            // 何であっても常にfalseを返す＝どのモードのどのパネルにもボタンが生成されない。
            // BuildingInfo.InitializePrefab()はm_availableInを一切触らないため、この代入は
            // InitializePrefabs/BindPrefabs呼び出しより前でも後でも安全（早期に済ませておく）。
            // BasePlacementWatcher.TryMatch/ReconcileBasesが見るのは_prefabs辞書への参照登録と
            // Info.name一致のみで、m_availableInは一切参照しないため、既存セーブの基地照合には無影響。
            clone.m_availableIn = ItemClass.Availability.None;

            if (clone.m_class == null)
            {
                ModConfig.LogError("WarfrontBasePrefab: cloned prefab '" + entry.PrefabName + "' has null m_class after clone; aborting");
                return;
            }
            if (clone.m_class.m_service != ItemClass.Service.Electricity)
            {
                ModConfig.LogError("WarfrontBasePrefab: cloned prefab m_class.m_service = " + clone.m_class.m_service +
                    " (expected Electricity); aborting '" + entry.PrefabName + "'");
                return;
            }

            SanitizeClonedPowerPlantAi(clone, entry.PrefabName);
            // Task71: 実コンポーネント（MeshFilter/Renderer）の書き換えは InitializePrefabs より
            // 前に行う必要がある（WarfrontBasePrefabVisualSwapのXMLコメント参照。フィールドだけを
            // 書き換えていた旧実装は InitializePrefab() に上書きされ、タービンの見た目が残っていた）。
            bool swapped = WarfrontBasePrefabVisualSwap.TryApplyMesh(clone, entry.ModelName, entry.ModelColor);

            PrefabCollection<BuildingInfo>.InitializePrefabs(CollectionName, clone, null);
            PrefabCollection<BuildingInfo>.BindPrefabs();

            // Task71: 遠距離LOD結合メッシュの焼き込みは InitializePrefabs/BindPrefabs の後
            // （m_lodMesh/m_lodMaterialがInitializePrefab()経由で確定した後）でなければならない。
            if (swapped) WarfrontBasePrefabVisualSwap.FinalizeLod(clone);

            _prefabs[entry.Type] = clone;
            RegisterLocalizedStrings(entry);
            ModConfig.Log("WarfrontBasePrefab: registration COMPLETE for " + entry.Type + " ('" + entry.PrefabName + "')");
        }

        /// <summary>
        /// 複製後の AI が PowerPlantAI 系統であれば、ゲーム本体の PowerPlantAI.ProduceGoods 等が
        /// 除算に使う可能性のあるフィールドを、どれが分母に使われても安全なようゼロ回避値に強制する
        /// （多層防御: FindElectricitySource で健全な複製元を選んでいても、将来複製元の実装が変わる、
        /// もしくは複製時にUnity側で値が失われるケースに備える）。
        /// 意図的にクローンは「小さな発電所」として振る舞う（若干の電力を供給する）— MVPとして許容。
        /// 専用アセットが用意でき次第、置き換え可能。
        /// </summary>
        private static void SanitizeClonedPowerPlantAi(BuildingInfo clone, string prefabName)
        {
            PowerPlantAI ai = clone.m_buildingAI as PowerPlantAI;
            if (ai == null) return;

            if (ai.m_resourceType != TransferManager.TransferReason.None) ai.m_resourceType = TransferManager.TransferReason.None;
            if (ai.m_electricityProduction <= 0) ai.m_electricityProduction = 16;
            if (ai.m_resourceCapacity <= 0) ai.m_resourceCapacity = 1;
            if (ai.m_resourceConsumption <= 0) ai.m_resourceConsumption = 1;
            if (ai.m_workPlaceCount0 + ai.m_workPlaceCount1 + ai.m_workPlaceCount2 + ai.m_workPlaceCount3 == 0)
                ai.m_workPlaceCount0 = 1;
            if (ai.m_constructionCost <= 0) ai.m_constructionCost = 1000;
            if (ai.m_maintenanceCost <= 0) ai.m_maintenanceCost = 100;
        }

        // Task71: 実際の見た目差し替え（メッシュ/マテリアルの実コンポーネント書き換え＋
        // generatedInfo再計算＋LOD結合メッシュ焼き込み）は WarfrontBasePrefabVisualSwap へ分離した
        // （旧TrySwapVisualMeshはフィールドのみを書き換えていたためInitializePrefab()に上書きされ、
        // 風力タービンの見た目が常に残っていた。詳細はWarfrontBasePrefabVisualSwapのXMLコメント、
        // および task-71-report.md 参照）。

        /// <summary>
        /// Task33/Task61: バニラの建物情報パネル（CityServiceWorldInfoPanel）は BuildingInfo.name をキーに
        /// BUILDING_TITLE / BUILDING_SHORT_DESC のロケール文字列を検索して表示する。3種それぞれの
        /// プレハブ名（entry.PrefabName）に対応するエントリを個別に登録する。
        ///
        /// ロケールAPI（ColossalManaged.dll をリフレクション/IL検証済み、Task33）:
        ///   - ColossalFramework.SingletonLite&lt;LocaleManager&gt;.instance : public static プロパティ。
        ///   - LocaleManager.m_Locale : Assembly内部アクセスのためリフレクションが必須。型はLocale。
        ///   - Locale.Key { m_Identifier, m_Key, m_Index } : いずれもpublicで書き込み可。
        ///   - Locale.Exists(Locale.Key) / Locale.AddLocalizedString(Locale.Key, string)。
        /// </summary>
        private static void RegisterLocalizedStrings(Entry entry)
        {
            try
            {
                LocaleManager manager = SingletonLite<LocaleManager>.instance;
                if (manager == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab.RegisterLocalizedStrings: LocaleManager.instance is null; skip '" + entry.PrefabName + "' (raw locale key will show)");
                    return;
                }

                FieldInfo localeField = typeof(LocaleManager).GetField("m_Locale", BindingFlags.NonPublic | BindingFlags.Instance);
                Locale locale = localeField != null ? localeField.GetValue(manager) as Locale : null;
                if (locale == null)
                {
                    ModConfig.LogError("WarfrontBasePrefab.RegisterLocalizedStrings: could not reflect LocaleManager.m_Locale; skip '" + entry.PrefabName + "' (raw locale key will show)");
                    return;
                }

                AddIfMissing(locale, entry.PrefabName, LocaleTitleId, entry.LocaleTitleText);
                AddIfMissing(locale, entry.PrefabName, LocaleDescId, entry.LocaleDescText);
                AddIfMissing(locale, entry.PrefabName, LocaleShortDescId, entry.LocaleDescText);

                ModConfig.Log("WarfrontBasePrefab: localized strings registered for '" + entry.PrefabName + "'");
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontBasePrefab.RegisterLocalizedStrings error (raw locale key will show): " + e);
            }
        }

        /// <summary>既に同じ Key が登録済みなら上書きしない（Exists→AddLocalizedStringの手順はタスク仕様通り）。</summary>
        private static void AddIfMissing(Locale locale, string prefabName, string identifier, string value)
        {
            Locale.Key key = new Locale.Key { m_Identifier = identifier, m_Key = prefabName, m_Index = 0 };
            if (!locale.Exists(key))
            {
                locale.AddLocalizedString(key, value);
            }
        }
    }
}
