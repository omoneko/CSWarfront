using System;
using System.Collections.Generic;
using CSWarfront.Core;
using CSWarfront.Game.Models;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// Small helper that resolves the mesh used for a unit's visual by "name".
    /// From VehicleInfo it borrows only m_mesh (or m_lodMesh if absent) and never touches the AI
    /// (VehicleAI-derived). This makes any borrow source safe — from plain passenger cars to modded
    /// Workshop vehicles — regardless of what AI it carries (since no AI is ever instantiated,
    /// side effects and crashes originating from vehicle AI are impossible by construction).
    /// Materials are NOT borrowed from CS vehicles (CS vehicle materials use dedicated shaders that
    /// require per-instance data supplied by CS's own renderer, so assigning them to a plain
    /// MeshRenderer renders invisible/black). Materials are generated in-house by
    /// <see cref="UnitMaterialFactory"/>.
    /// Resolution results are cached per prefab name, so the scan (full prefab sweep) happens only
    /// once, on the first call.
    /// Main thread only (involves PrefabCollection access).
    /// </summary>
    internal static class UnitMeshSource
    {
        private struct Resolved
        {
            public Mesh Mesh;
            public bool Ok;
        }

        // Known vehicle names tried by default (when AssetPrefabName is unspecified). If none is
        // found, we fall through to the full prefab sweep.
        private static readonly string[] DefaultCandidateNames =
        {
            "Fire Truck", "Police Car", "Ambulance", "Garbage Truck", "Bus"
        };

        private const string DefaultCacheKey = ""; // shared key for all units with empty AssetPrefabName

        private static readonly Dictionary<string, Resolved> _cache = new Dictionary<string, Resolved>();
        // Records names for which FindLoaded(name) missed and we fell back to the default, solely to
        // avoid duplicate warning logs (NOT added to the cache — so every future call retries
        // FindLoaded; see TryResolve for details).
        private static readonly HashSet<string> _warnedMissingNames = new HashSet<string>();
        // Task36: dedicated to de-duplicating the "bound but asset not found yet (not loaded, etc.)"
        // warning (keyed by "faction|typeKey=kind:assetName"; Task41 added the kind to the key.
        // Results coming from UnitAssetBindings/AssetCatalog are intentionally not cached, as
        // explained in TryResolve below, so this set never blocks the FindLoaded retry that happens
        // on every call).
        private static readonly HashSet<string> _warnedMissingBindings = new HashSet<string>();
        private static bool _loggedSourceOnce;
        private static bool _loggedFailureOnce;
        private static Mesh _fallbackCubeMesh;

        /// <summary>
        /// Task36: resolves a mesh from typeKey (may be empty) and assetPrefabName (may be empty).
        /// Task40: made binding resolution per-faction (factionId).
        /// Task41: added support for bound asset kinds beyond props (buildings/vehicles/trees).
        /// Resolution order: (a) the UnitAssetBindings binding for (factionId, typeKey)
        ///        (per-faction first, then all-factions shared; resolved inside
        ///        UnitAssetBindings.TryGet, returned together with its kind (AssetKind))
        ///        → resolve that asset's mesh via AssetCatalog
        ///        → (b) FindLoaded by assetPrefabName → default candidate names → full VehicleInfo sweep
        ///        → (c) primitive (Cube) fallback. Returns false only when everything fails.
        ///
        /// Results from (a) are intentionally not cached (same for AssetCatalog.TryGetMesh, keeping
        /// the policy from the PropCatalog era). Binding changes made through
        /// UnitAssetBindings.Set/Clear take effect by having UnitVisuals.DestroyAll() discard the
        /// existing visuals (destroyed visuals always go through CreateVisual on the next Sync →
        /// this resolution runs again), so holding a per-name cache here would carry the greater
        /// risk of changes not taking effect / stale results lingering. With no cache, the question
        /// of "including (faction id, kind, name) in the cache key" never arises in the first place
        /// (Task40/Task41 requirement: caches must not leak across factions or kinds).
        /// The existing cache for (b)/(c) (the overload below) is keyed by assetPrefabName only and
        /// depends on neither faction nor kind (that path is the default fallback that always deals
        /// with VehicleInfo only, so the concept of AssetKind is simply not involved).
        ///
        /// By contrast, <see cref="UnitMaterialFactory"/> caches textures persistently per
        /// (kind, name) (regenerating materials on every unit visual destroy/recreate cycle would be
        /// too costly). Including the kind in the cache key ensures that, e.g., a building and a
        /// prop sharing the same name never get each other's textures (Task41 requirement).
        ///
        /// Task37: reports via <paramref name="fromAssignedProp"/> whether the mesh was resolved
        /// through path (a), the "bound asset". The caller (UnitVisuals) uses this to decide not to
        /// show the visibility marker cube or faction color when a bound asset exists.
        /// <paramref name="resolvedKind"/>/<paramref name="resolvedAssetName"/> carry meaningful
        /// values only when fromAssignedProp=true (they are passed to
        /// UnitMaterialFactory.TryGetAssetMaterial).
        ///
        /// Task57: inserted (b) "the built-in default model for the unit's UnitCategory" between
        /// (a) and (c). The typeKey is parsed with Core.TypeKeyParser (same technique as the Tier
        /// fallback search), and if a src/CSWarfront/Models/Unit_*.obj exists for the category, its
        /// mesh is returned via <see cref="Models.WarfrontModelProvider"/>. Whether this path
        /// resolved is reported via <paramref name="fromBuiltInModel"/>. Unlike fromAssignedProp,
        /// this path has no asset-specific texture, but the caller uses it the same way as
        /// fromAssignedProp for the "do not show the visibility marker" decision. Categories not
        /// covered (all categories are covered as of Task69) pass through to (c).
        ///
        /// Task69: changed the built-in model's materials to come from
        /// <see cref="Models.WarfrontModelProvider.TryGetModel"/> (the multi-material variant that
        /// carries one submesh + dedicated material per usemtl block in the .obj), returned as
        /// <paramref name="builtInMaterials"/>. Blender-made models (from
        /// tools/export_builtin_obj.py) carry the model's own actual colors, so from this point on,
        /// whenever fromBuiltInModel is true this array is always used for rendering (instead of a
        /// faction-color tint; the branch lives in UnitVisuals.CreateVisual. Faction identification
        /// was consolidated onto the existing faction icons).
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

        /// <summary>Task57/Task61: mapping table from UnitCategory -&gt; the file name (without
        /// extension) of src/CSWarfront/Models/Unit_*.obj. Beyond the 7 land branches, Task61 added
        /// 2 naval kinds (Destroyer/Carrier) and 3 air kinds (AirSuperiority/TacticalBomber/
        /// SuicideDrone). Other not-yet-implemented categories return false, and the caller falls
        /// back to (c) vehicle borrowing / primitive.</summary>
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
                // Task99: dedicated supply truck model (models.blend 20_Supply_Truck, a 6x6 canvas-
                // covered truck 7.77x2.78x2.91m created by the user on 2026-08-03; replaces the
                // initial stand-in that reused the APC model).
                case UnitCategory.SupplyTruck: modelName = "Unit_SupplyTruck"; return true;
                // Task101: new Update3 branches (models.blend 25_Transport_Helo/26_Attack_Helo/28_Freight_Train).
                case UnitCategory.TransportHelicopter: modelName = "Unit_TransportHeli"; return true;
                case UnitCategory.AttackHelicopter: modelName = "Unit_AttackHeli"; return true;
                case UnitCategory.MilitaryTrain: modelName = "Unit_MilitaryTrain"; return true;
                default: modelName = null; return false;
            }
        }

        /// <summary>
        /// Resolves a mesh from assetPrefabName (may be empty).
        /// Resolution order: (a) FindLoaded by assetPrefabName → (b) default candidate names → full
        /// VehicleInfo sweep → (c) primitive (Cube) fallback. Returns false only when everything fails.
        ///
        /// Caching policy: when assetPrefabName is given, only a result where the direct
        /// FindLoaded(name) "succeeded" is cached persistently under that key. If the direct lookup
        /// misses and we fall back to a default prefab, that call returns the default, but nothing
        /// is cached under the named key.
        /// Otherwise, being called just once during the brief window before a Workshop asset has
        /// loaded would burn in the incorrect cache entry "this asset name can never be resolved",
        /// and the asset would never resolve correctly even after it loads later (a bug that
        /// actually happened).
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

            // The default key (no name given) is always cached. Named keys are cached only on a
            // direct hit; on a miss nothing is written to the cache so FindLoaded can be retried on
            // every call.
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

                // (c) Primitive fallback.
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

        /// <summary>Tries the default candidate names in order; if all miss, sweeps every VehicleInfo
        /// and returns the first one that has a mesh.</summary>
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
                    UnityEngine.Object.Destroy(temp); // the mesh itself is a built-in shared Unity asset, so it is not destroyed
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
