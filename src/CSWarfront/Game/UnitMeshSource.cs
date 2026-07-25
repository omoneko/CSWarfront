using System;
using System.Collections.Generic;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// ユニットの見た目に使うメッシュ＋マテリアルを「名前」で解決する小さなヘルパー。
    /// VehicleInfo からは m_mesh / m_material だけを借用し、AI（VehicleAI派生）には一切触れない。
    /// これにより借用元は素の乗用車からWorkshopの改造車両まで、どんなAIを積んでいても安全
    /// （AIが一切インスタンス化されないため、車両AI由来の副作用・クラッシュが原理的に起こらない）。
    /// 解決結果はプレハブ名単位でキャッシュし、スキャン（全プレハブ走査）は最初の1回だけ発生する。
    /// メインスレッド専用（Material生成・PrefabCollectionアクセスを伴う）。
    /// </summary>
    internal static class UnitMeshSource
    {
        private struct Resolved
        {
            public Mesh Mesh;
            public Material Material;
            public bool Ok;
        }

        // 既定（AssetPrefabName未指定）時に試す既知の乗り物名。見つからなければ全プレハブ走査へ。
        private static readonly string[] DefaultCandidateNames =
        {
            "Fire Truck", "Police Car", "Ambulance", "Garbage Truck", "Bus"
        };

        private const string DefaultCacheKey = ""; // AssetPrefabName が空の全ユニット共通キー

        private static readonly Dictionary<string, Resolved> _cache = new Dictionary<string, Resolved>();
        private static bool _loggedSourceOnce;
        private static bool _loggedFailureOnce;
        private static Mesh _fallbackCubeMesh;
        private static Material _fallbackMaterial;

        /// <summary>
        /// assetPrefabName（空可）からメッシュ・マテリアルを解決する。
        /// 解決順: (a) assetPrefabName で FindLoaded → (b) 既定候補名 → 全VehicleInfo走査 →
        /// (c) プリミティブ（Cube）フォールバック。全滅時のみ false を返す。
        /// </summary>
        public static bool TryResolve(string assetPrefabName, out Mesh mesh, out Material material)
        {
            string key = assetPrefabName ?? DefaultCacheKey;

            Resolved cached;
            if (_cache.TryGetValue(key, out cached))
            {
                mesh = cached.Mesh;
                material = cached.Material;
                return cached.Ok;
            }

            Resolved result = Resolve(key);
            _cache[key] = result;
            mesh = result.Mesh;
            material = result.Material;
            return result.Ok;
        }

        private static Resolved Resolve(string key)
        {
            try
            {
                VehicleInfo info = null;
                if (!string.IsNullOrEmpty(key))
                {
                    info = PrefabCollection<VehicleInfo>.FindLoaded(key);
                }
                if (info == null)
                {
                    info = FindDefaultPrefab();
                }

                Mesh mesh = null;
                Material material = null;
                if (info != null)
                {
                    mesh = info.m_mesh != null ? info.m_mesh : info.m_lodMesh;
                    material = info.m_material != null ? info.m_material : info.m_lodMaterial;
                }

                if (mesh != null && material != null)
                {
                    if (!_loggedSourceOnce)
                    {
                        _loggedSourceOnce = true;
                        ModConfig.Log("UnitMeshSource: source prefab='" + info.name + "' mesh='" + mesh.name + "' を借用します（AIは使用しません）");
                    }
                    return new Resolved { Mesh = mesh, Material = material, Ok = true };
                }

                // (c) プリミティブフォールバック。
                if (TryGetPrimitiveFallback(out mesh, out material))
                {
                    if (!_loggedSourceOnce)
                    {
                        _loggedSourceOnce = true;
                        ModConfig.Log("UnitMeshSource: 車両プレハブのメッシュが見つからず、プリミティブ(Cube)にフォールバックしました");
                    }
                    return new Resolved { Mesh = mesh, Material = material, Ok = true };
                }

                if (!_loggedFailureOnce)
                {
                    _loggedFailureOnce = true;
                    ModConfig.LogError("UnitMeshSource: メッシュ解決に完全失敗（プレハブ・プリミティブ共に不可）key='" + key + "'");
                }
                return new Resolved { Mesh = null, Material = null, Ok = false };
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMeshSource.Resolve(" + key + ") error: " + e);
                return new Resolved { Mesh = null, Material = null, Ok = false };
            }
        }

        /// <summary>既定候補名を順に試し、全滅なら全VehicleInfoを走査して mesh/material を持つ最初の1つを返す。</summary>
        private static VehicleInfo FindDefaultPrefab()
        {
            for (int i = 0; i < DefaultCandidateNames.Length; i++)
            {
                VehicleInfo info = PrefabCollection<VehicleInfo>.FindLoaded(DefaultCandidateNames[i]);
                if (info != null && info.m_mesh != null && info.m_material != null) return info;
            }

            int count = PrefabCollection<VehicleInfo>.LoadedCount();
            for (uint i = 0; i < (uint)count; i++)
            {
                VehicleInfo info = PrefabCollection<VehicleInfo>.GetLoaded(i);
                if (info != null && info.m_mesh != null && info.m_material != null) return info;
            }
            return null;
        }

        private static bool TryGetPrimitiveFallback(out Mesh mesh, out Material material)
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
                if (_fallbackMaterial == null)
                {
                    Shader shader = Shader.Find("Diffuse");
                    if (shader == null) shader = Shader.Find("VertexLit");
                    if (shader == null) shader = Shader.Find("Standard");
                    _fallbackMaterial = shader != null ? new Material(shader) : null;
                }

                mesh = _fallbackCubeMesh;
                material = _fallbackMaterial;
                return mesh != null && material != null;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMeshSource.TryGetPrimitiveFallback error: " + e);
                mesh = null;
                material = null;
                return false;
            }
        }
    }
}
