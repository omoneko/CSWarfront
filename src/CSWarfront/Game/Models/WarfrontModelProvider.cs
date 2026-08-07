using System;
using System.Collections.Generic;
using System.IO;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Models
{
    /// <summary>
    /// Single entry point for the default (built-in) per-branch unit models (Task57; multi-material
    /// support added in Task69). Builds and caches meshes/materials at runtime from
    /// Models/&lt;name&gt;.obj(+.mtl) in the mod's deployment folder.
    /// <see cref="TryGetModel"/>: a trimmed-down port of
    /// MissileDisaster.Game.Models.MissileModelProvider.BuildFromObj. Keeps the .obj's usemtl blocks
    /// as-is as submeshes and returns per-submesh materials tinted with the .mtl's Kd colors (the
    /// caller, UnitVisuals, handles turning them into GameObjects).
    /// This is the primary path for resolving a unit's default model (Task69: default models are
    /// always rendered with their own MTL colors; faction-color tinting was removed).
    /// No AssetBundles, decals, or immediate GameObject creation are involved.
    /// Because it creates meshes/materials, it must always be called from the main thread.
    ///
    /// Task82: the single-mesh merged variant <c>TryGetMesh</c> (used only for bases, i.e. the cloned
    /// electricity-tab prefab WarfrontBasePrefab) and its average-color companion
    /// <c>TryGetAverageColor</c> were deleted because the cloned-prefab mechanism itself was fully
    /// removed and they had no callers left (the Building_*.obj model files themselves remain because
    /// the asset-editor-export write-out flow still uses them; see tools/).
    /// </summary>
    internal static class WarfrontModelProvider
    {
        private class BuiltModel
        {
            public Mesh Mesh;
            public Material[] Materials;
        }

        private static string _modDirectory;
        private static bool _initialized;
        private static readonly Dictionary<string, BuiltModel> _modelCache = new Dictionary<string, BuiltModel>();
        private static readonly HashSet<string> _warnedMissingModel = new HashSet<string>();

        /// <summary>Idempotent. Must be called from WarfrontLoadingExtension.LoadModAssets at the same
        /// timing as UnitAssetBindings.Load / WarfrontSounds.Initialize.</summary>
        public static void Initialize(string modDirectory)
        {
            if (_initialized) return;
            _initialized = true;
            _modDirectory = modDirectory;
        }

        /// <summary>
        /// Task69: loads Models/&lt;modelName&gt;.obj(+.mtl) as a multi-submesh Mesh keeping the
        /// .obj's usemtl blocks as-is, plus an array of per-submesh materials colored from the .mtl
        /// (see <see cref="WarfrontMeshBuilder.TryBuild"/>; a trimmed-down port of
        /// MissileDisaster.Game.Models.MissileModelProvider.BuildFromObj).
        /// Since the policy is that all default models render with their own MTL colors
        /// (faction-color tinting was dropped for default models; see the caller UnitVisuals), this is
        /// the primary path for built-in model resolution.
        /// Returns false on failure (not initialized / file missing / parse failure). Cached per name
        /// (the models are static, so the generated Mesh/Material[] can safely be shared by all
        /// units).
        /// </summary>
        public static bool TryGetModel(string modelName, out Mesh mesh, out Material[] materials)
        {
            mesh = null;
            materials = null;
            try
            {
                if (string.IsNullOrEmpty(modelName)) return false;

                BuiltModel cached;
                if (_modelCache.TryGetValue(modelName, out cached) && cached != null && cached.Mesh != null)
                {
                    mesh = cached.Mesh;
                    materials = cached.Materials;
                    return true;
                }

                if (string.IsNullOrEmpty(_modDirectory))
                {
                    ModConfig.LogError("WarfrontModelProvider.TryGetModel(" + modelName + "): modDirectory not initialized (Initialize not called)");
                    return false;
                }

                string modelsDir = Path.Combine(_modDirectory, ModConfig.ModelsFolderName);
                string objPath = Path.Combine(modelsDir, modelName + ".obj");
                if (!File.Exists(objPath))
                {
                    if (_warnedMissingModel.Add(modelName))
                    {
                        ModConfig.LogError("WarfrontModelProvider: OBJ not found path=" + objPath);
                    }
                    return false;
                }

                ObjData data = ObjParser.Parse(File.ReadAllText(objPath));

                Dictionary<string, MtlColor> mtl = null;
                string mtlPath = Path.Combine(modelsDir, modelName + ".mtl");
                if (File.Exists(mtlPath))
                {
                    mtl = MtlParser.Parse(File.ReadAllText(mtlPath));
                }

                Mesh built;
                Material[] builtMaterials;
                if (!WarfrontMeshBuilder.TryBuild(data, mtl, ModConfig.ObjFallbackColor, out built, out builtMaterials))
                {
                    ModConfig.LogError("WarfrontModelProvider: multi-material mesh build failed name=" + modelName + " path=" + objPath);
                    return false;
                }

                _modelCache[modelName] = new BuiltModel { Mesh = built, Materials = builtMaterials };
                ModConfig.Log("WarfrontModelProvider: loaded built-in model (multi-material) name=" + modelName +
                    " subMeshes=" + (builtMaterials != null ? builtMaterials.Length : 0));
                mesh = built;
                materials = builtMaterials;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontModelProvider.TryGetModel(" + modelName + ") error: " + e);
                mesh = null;
                materials = null;
                return false;
            }
        }

    }
}
