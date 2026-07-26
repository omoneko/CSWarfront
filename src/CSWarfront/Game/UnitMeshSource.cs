using System;
using System.Collections.Generic;
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
        private static bool _loggedSourceOnce;
        private static bool _loggedFailureOnce;
        private static Mesh _fallbackCubeMesh;

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
                        ModConfig.Log("UnitMeshSource: source prefab='" + info.name + "' mesh='" + mesh.name + "' を借用します（AI・マテリアルは使用しません）");
                    }
                    return new Resolved { Mesh = mesh, Ok = true };
                }

                // (c) プリミティブフォールバック。
                if (TryGetPrimitiveFallback(out mesh))
                {
                    if (!_loggedSourceOnce)
                    {
                        _loggedSourceOnce = true;
                        ModConfig.Log("UnitMeshSource: 車両プレハブのメッシュが見つからず、プリミティブ(Cube)にフォールバックしました");
                    }
                    return new Resolved { Mesh = mesh, Ok = true };
                }

                if (!_loggedFailureOnce)
                {
                    _loggedFailureOnce = true;
                    ModConfig.LogError("UnitMeshSource: メッシュ解決に完全失敗（プレハブ・プリミティブ共に不可）key='" + key + "'");
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
