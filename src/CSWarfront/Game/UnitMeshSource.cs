using System;
using System.Collections.Generic;
using CSWarfront.Core;
using CSWarfront.Game.Models;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// ユニットの見た目に使うメッシュを「名前」で解決する小さなヘルパー。
    /// VehicleInfo からは m_mesh（無ければ m_lodMesh）だけを借用し、AI（VehicleAI派生）には一切触れない。
    /// これにより借用元は素の乗用車からWorkshopの改造車両まで、どんなAIを積んでいても安全
    /// （AIが一切インスタンス化されないため、車両AI由来の副作用・クラッシュが原理的に起こらない）。
    /// マテリアルはCS車両のものを借用しない（CS車両マテリアルは専用シェーダーが独自レンダラー由来の
    /// per-instanceデータを要求するため、素のMeshRendererに割り当てると不可視/黒になる）。
    /// マテリアルは <see cref="UnitMaterialFactory"/> が自前で生成する。
    /// 解決結果はプレハブ名単位でキャッシュし、スキャン（全プレハブ走査）は最初の1回だけ発生する。
    /// メインスレッド専用（PrefabCollectionアクセスを伴う）。
    /// </summary>
    internal static class UnitMeshSource
    {
        private struct Resolved
        {
            public Mesh Mesh;
            public bool Ok;
        }

        // 既定（AssetPrefabName未指定）時に試す既知の乗り物名。見つからなければ全プレハブ走査へ。
        private static readonly string[] DefaultCandidateNames =
        {
            "Fire Truck", "Police Car", "Ambulance", "Garbage Truck", "Bus"
        };

        private const string DefaultCacheKey = ""; // AssetPrefabName が空の全ユニット共通キー

        private static readonly Dictionary<string, Resolved> _cache = new Dictionary<string, Resolved>();
        // FindLoaded(name) が見つからず既定へフォールバックした名前を、警告ログの重複を避けるためだけに記録する
        // （キャッシュには入れない＝次回呼び出しで必ず FindLoaded を再試行させる。詳細は TryResolve 参照）。
        private static readonly HashSet<string> _warnedMissingNames = new HashSet<string>();
        // Task36: バインド済みだがそのアセットがまだ見つからない（未ロード等）警告の重複防止専用
        // （"faction|typeKey=kind:assetName" 単位、Task41で種類も含めた。UnitAssetBindings/AssetCatalog
        // 由来の結果は下記TryResolveの通り意図的にキャッシュしないため、この集合は毎回のFindLoaded
        // 再試行そのものは妨げない）。
        private static readonly HashSet<string> _warnedMissingBindings = new HashSet<string>();
        private static bool _loggedSourceOnce;
        private static bool _loggedFailureOnce;
        private static Mesh _fallbackCubeMesh;

        /// <summary>
        /// Task36: typeKey（空可）とassetPrefabName（空可）からメッシュを解決する。
        /// Task40: 割り当ての解決を勢力別（factionId）にした。
        /// Task41: 割り当て済みアセットの種類（プロップ以外に建物/車両/樹木も）に対応した。
        /// 解決順: (a) (factionId, typeKey) に対する UnitAssetBindings の割り当て（勢力別 → 全勢力共通の順、
        ///        UnitAssetBindings.TryGet内部で解決、種類(AssetKind)込みで返る）
        ///        → AssetCatalog でそのアセットのメッシュを解決
        ///        → (b) assetPrefabName で FindLoaded → 既定候補名 → 全VehicleInfo走査
        ///        → (c) プリミティブ（Cube）フォールバック。全滅時のみ false を返す。
        ///
        /// (a) の結果は意図的にキャッシュしない（AssetCatalog.TryGetMeshも同様、PropCatalog時代からの
        /// 方針を踏襲）。UnitAssetBindings.Set/Clear による割り当て変更は UnitVisuals.DestroyAll() で
        /// 既存の見た目を破棄させることで反映される（破棄された見た目は次回Syncで必ずCreateVisual→
        /// この解決を再実行する）ため、ここで名前単位キャッシュを持つと変更が反映されない/古い結果が
        /// 残るリスクの方が大きい。キャッシュが無いため「(勢力ID, 種類, 名前) をキャッシュキーに含める」
        /// 問題はそもそも発生しない（Task40/Task41要件: 勢力間・種類間でキャッシュが漏れないこと）。
        /// (b)/(c) の既存キャッシュ（下記オーバーロード）は assetPrefabName 単位のみで勢力にも種類にも
        /// 依存しない（このパスは常にVehicleInfoのみを扱う既定フォールバックであり、AssetKindの概念自体が
        /// 関与しないため）。
        ///
        /// 一方 <see cref="UnitMaterialFactory"/> はテクスチャを (kind, name) 単位で永続キャッシュする
        /// （マテリアルはユニットのビジュアル破棄・再生成のたびに毎回生成し直すのはコストが大きいため）。
        /// キャッシュキーに種類を含めることで、例えば同名の建物とプロップが両方存在しても互いの
        /// テクスチャを取り違えない（Task41要件）。
        ///
        /// Task37: メッシュが (a) の「割り当て済みアセット」経由で解決できたかどうかを
        /// <paramref name="fromAssignedProp"/> で報告する。呼び出し側（UnitVisuals）はこれを使って、
        /// 割り当て済みアセットがある場合は可視性マーカー立方体や勢力色を出さない判断をする。
        /// <paramref name="resolvedKind"/>/<paramref name="resolvedAssetName"/> は fromAssignedProp=true
        /// の時のみ意味のある値を返す（UnitMaterialFactory.TryGetAssetMaterial に渡すため）。
        ///
        /// Task57: (a)と(c)の間に (b) 「ユニットのUnitCategoryに対応する既定(built-in)モデル」を
        /// 挿入した。typeKeyを Core.TypeKeyParser で解析し（Tierフォールバック探索と同じ手法）、
        /// カテゴリに対応する src/CSWarfront/Models/Unit_*.obj があれば
        /// <see cref="Models.WarfrontModelProvider"/> 経由でそのメッシュを返す。この経路で解決できたかは
        /// <paramref name="fromBuiltInModel"/> で報告する。fromAssignedProp と違いこちらはアセット固有の
        /// テクスチャを持たないため、呼び出し側は fromAssignedProp と同様「可視性マーカーを出さない」
        /// 判断に使う。対象外のカテゴリ（Task69時点で全カテゴリ対応済み）は素通りして (c) へ進む。
        ///
        /// Task69: 既定モデルのマテリアルを <see cref="Models.WarfrontModelProvider.TryGetModel"/>
        /// （.obj の usemtl ブロックごとにサブメッシュ+専用マテリアルを持つマルチマテリアル版）から
        /// 取得し、<paramref name="builtInMaterials"/> として返すよう変更した。Blender製モデル
        /// （tools/export_builtin_obj.py 由来）はモデル自身の実際の色を持つため、これ以降 fromBuiltInModel
        /// の場合は（勢力色ティントではなく）常にこの配列を使って描画する（UnitVisuals.CreateVisual
        /// 側で分岐。勢力の識別は既存の勢力アイコンに一本化した）。
        /// </summary>
        public static bool TryResolve(byte factionId, string typeKey, string assetPrefabName, out Mesh mesh, out Material[] builtInMaterials, out bool fromAssignedProp, out bool fromBuiltInModel, out AssetKind resolvedKind, out string resolvedAssetName)
        {
            fromAssignedProp = false;
            fromBuiltInModel = false;
            builtInMaterials = null;
            resolvedKind = AssetKind.Prop;
            resolvedAssetName = null;

            try
            {
                if (!string.IsNullOrEmpty(typeKey))
                {
                    AssetKind boundKind;
                    string boundName;
                    if (UnitAssetBindings.TryGet(factionId, typeKey, out boundKind, out boundName))
                    {
                        Mesh assetMesh;
                        if (AssetCatalog.TryGetMesh(boundKind, boundName, out assetMesh))
                        {
                            mesh = assetMesh;
                            fromAssignedProp = true;
                            resolvedKind = boundKind;
                            resolvedAssetName = boundName;
                            return true;
                        }

                        string warnKey = factionId + "|" + typeKey + "=" + boundKind + ":" + boundName;
                        if (_warnedMissingBindings.Add(warnKey))
                        {
                            ModConfig.Log("UnitMeshSource: faction=" + factionId + " '" + typeKey + "' bound " + boundKind + " '" + boundName +
                                "' not found (not loaded, etc). Falling back to assetPrefabName/default");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMeshSource.TryResolve(faction=" + factionId + ", typeKey=" + typeKey + ") binding lookup error: " + e);
            }

            try
            {
                UnitCategory category;
                byte tier;
                string builtInModelName;
                if (!string.IsNullOrEmpty(typeKey) &&
                    TypeKeyParser.TryParse(typeKey, out category, out tier) &&
                    TryGetBuiltInModelName(category, out builtInModelName))
                {
                    Mesh builtInMesh;
                    Material[] builtInMats;
                    if (WarfrontModelProvider.TryGetModel(builtInModelName, out builtInMesh, out builtInMats))
                    {
                        mesh = builtInMesh;
                        builtInMaterials = builtInMats;
                        fromBuiltInModel = true;
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMeshSource.TryResolve(faction=" + factionId + ", typeKey=" + typeKey + ") built-in model lookup error: " + e);
            }

            return TryResolve(assetPrefabName, out mesh);
        }

        /// <summary>Task57/Task61: UnitCategory -&gt; src/CSWarfront/Models/Unit_*.obj のファイル名
        /// （拡張子無し）への対応表。陸上7兵種に加え、Task61で海上2種(Destroyer/Carrier)・
        /// 航空3種(AirSuperiority/TacticalBomber/SuicideDrone)を追加した。他の未実装カテゴリは
        /// false を返し、呼び出し側は (c) の車両借用/プリミティブへフォールバックする。</summary>
        private static bool TryGetBuiltInModelName(UnitCategory category, out string modelName)
        {
            switch (category)
            {
                case UnitCategory.Infantry: modelName = "Unit_Infantry"; return true;
                case UnitCategory.MechInfantry: modelName = "Unit_MechInfantry"; return true;
                case UnitCategory.Apc: modelName = "Unit_Apc"; return true;
                case UnitCategory.Tank: modelName = "Unit_Tank"; return true;
                case UnitCategory.Artillery: modelName = "Unit_Artillery"; return true;
                case UnitCategory.AntiAir: modelName = "Unit_AntiAir"; return true;
                case UnitCategory.DroneInfantry: modelName = "Unit_Drone"; return true;
                case UnitCategory.Destroyer: modelName = "Unit_Destroyer"; return true;
                case UnitCategory.Carrier: modelName = "Unit_Carrier"; return true;
                case UnitCategory.AirSuperiority: modelName = "Unit_Fighter"; return true;
                case UnitCategory.TacticalBomber: modelName = "Unit_Bomber"; return true;
                case UnitCategory.SuicideDrone: modelName = "Unit_SuicideDrone"; return true;
                // Task99: 補給トラック専用モデル（models.blend 20_Supply_Truck、2026-08-03ユーザー作成の
                // 6×6幌付きトラック 7.77×2.78×2.91m。当初のAPCモデル代用を置き換えた）。
                case UnitCategory.SupplyTruck: modelName = "Unit_SupplyTruck"; return true;
                // Task101: Update3の新兵科（models.blend 25_Transport_Helo/26_Attack_Helo/28_Freight_Train）。
                case UnitCategory.TransportHelicopter: modelName = "Unit_TransportHeli"; return true;
                case UnitCategory.AttackHelicopter: modelName = "Unit_AttackHeli"; return true;
                case UnitCategory.MilitaryTrain: modelName = "Unit_MilitaryTrain"; return true;
                default: modelName = null; return false;
            }
        }

        /// <summary>
        /// assetPrefabName（空可）からメッシュを解決する。
        /// 解決順: (a) assetPrefabName で FindLoaded → (b) 既定候補名 → 全VehicleInfo走査 →
        /// (c) プリミティブ（Cube）フォールバック。全滅時のみ false を返す。
        ///
        /// キャッシュ方針: assetPrefabName 指定時、直接の FindLoaded(name) が「成功」した結果のみを
        /// そのキーで永続キャッシュする。直接ヒットせず既定プレハブへフォールバックした場合は、
        /// その回の呼び出し結果としては既定を返すが named-key ではキャッシュしない。
        /// こうしないと、Workshopアセットがまだロードされていない一瞬に呼ばれただけで「そのアセット名は
        /// 永久に解決不能」という誤ったキャッシュが焼き付いてしまい、後でアセットがロードされても
        /// 二度と正しく解決されなくなる（実際に起きていたバグ）。
        /// </summary>
        public static bool TryResolve(string assetPrefabName, out Mesh mesh)
        {
            string key = assetPrefabName ?? DefaultCacheKey;

            Resolved cached;
            if (_cache.TryGetValue(key, out cached))
            {
                mesh = cached.Mesh;
                return cached.Ok;
            }

            bool namedLookupSucceeded;
            Resolved result = Resolve(key, out namedLookupSucceeded);

            // 既定キー（名前未指定）は常にキャッシュ。名前指定キーは直接ヒット時のみキャッシュし、
            // ミス時は毎回 FindLoaded を再試行できるようにキャッシュへ書き込まない。
            if (string.IsNullOrEmpty(key) || namedLookupSucceeded)
            {
                _cache[key] = result;
            }

            mesh = result.Mesh;
            return result.Ok;
        }

        private static Resolved Resolve(string key, out bool namedLookupSucceeded)
        {
            namedLookupSucceeded = false;
            try
            {
                VehicleInfo info = null;
                if (!string.IsNullOrEmpty(key))
                {
                    info = PrefabCollection<VehicleInfo>.FindLoaded(key);
                    if (info != null) namedLookupSucceeded = true;
                }
                if (info == null)
                {
                    if (!string.IsNullOrEmpty(key) && _warnedMissingNames.Add(key))
                    {
                        ModConfig.Log("UnitMeshSource: named asset '" + key + "' not found yet (FindLoaded miss); using default prefab for now, will retry this name on future calls");
                    }
                    info = FindDefaultPrefab();
                }

                Mesh mesh = null;
                if (info != null)
                {
                    mesh = info.m_mesh != null ? info.m_mesh : info.m_lodMesh;
                }

                if (mesh != null)
                {
                    if (!_loggedSourceOnce)
                    {
                        _loggedSourceOnce = true;
                        ModConfig.Log("UnitMeshSource: borrowing source prefab='" + info.name + "' mesh='" + mesh.name + "' (not using its AI or material)");
                    }
                    return new Resolved { Mesh = mesh, Ok = true };
                }

                // (c) プリミティブフォールバック。
                if (TryGetPrimitiveFallback(out mesh))
                {
                    if (!_loggedSourceOnce)
                    {
                        _loggedSourceOnce = true;
                        ModConfig.Log("UnitMeshSource: vehicle prefab mesh not found, fell back to primitive (Cube)");
                    }
                    return new Resolved { Mesh = mesh, Ok = true };
                }

                if (!_loggedFailureOnce)
                {
                    _loggedFailureOnce = true;
                    ModConfig.LogError("UnitMeshSource: mesh resolution failed completely (neither prefab nor primitive available) key='" + key + "'");
                }
                return new Resolved { Mesh = null, Ok = false };
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMeshSource.Resolve(" + key + ") error: " + e);
                return new Resolved { Mesh = null, Ok = false };
            }
        }

        /// <summary>既定候補名を順に試し、全滅なら全VehicleInfoを走査して mesh を持つ最初の1つを返す。</summary>
        private static VehicleInfo FindDefaultPrefab()
        {
            for (int i = 0; i < DefaultCandidateNames.Length; i++)
            {
                VehicleInfo info = PrefabCollection<VehicleInfo>.FindLoaded(DefaultCandidateNames[i]);
                if (info != null && (info.m_mesh != null || info.m_lodMesh != null)) return info;
            }

            int count = PrefabCollection<VehicleInfo>.LoadedCount();
            for (uint i = 0; i < (uint)count; i++)
            {
                VehicleInfo info = PrefabCollection<VehicleInfo>.GetLoaded(i);
                if (info != null && (info.m_mesh != null || info.m_lodMesh != null)) return info;
            }
            return null;
        }

        private static bool TryGetPrimitiveFallback(out Mesh mesh)
        {
            try
            {
                if (_fallbackCubeMesh == null)
                {
                    GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    MeshFilter filter = temp.GetComponent<MeshFilter>();
                    _fallbackCubeMesh = filter != null ? filter.sharedMesh : null;
                    UnityEngine.Object.Destroy(temp); // メッシュ自体はUnity組込共有アセットのため破棄されない
                }

                mesh = _fallbackCubeMesh;
                return mesh != null;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMeshSource.TryGetPrimitiveFallback error: " + e);
                mesh = null;
                return false;
            }
        }
    }
}
