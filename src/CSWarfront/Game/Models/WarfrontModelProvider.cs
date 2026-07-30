using System;
using System.Collections.Generic;
using System.IO;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Models
{
    /// <summary>
    /// 兵科別ユニットの既定（built-in）モデルの単一窓口（Task57、Task69でマルチマテリアル対応）。
    /// Mod 配置フォルダの Models/&lt;name&gt;.obj(+.mtl) から実行時にメッシュ/マテリアルを構築して
    /// キャッシュする。
    /// <see cref="TryGetModel"/>: MissileDisaster.Game.Models.MissileModelProvider.BuildFromObj を
    /// 縮小移植。.obj の usemtl ブロックをそのままサブメッシュとして残し、サブメッシュごとに .mtl の
    /// Kd 色で塗った自前マテリアルを返す（GameObject化は呼び出し側 UnitVisuals が行う）。
    /// ユニットの既定モデル解決の主経路（Task69: 既定モデルは常に自分自身のMTL色で描画し、勢力色
    /// ティントは廃止した）。
    /// AssetBundle・デカール・GameObject即時生成等は持ち込まない。
    /// メッシュ/マテリアル生成を伴うため、必ずメインスレッドから呼ぶこと。
    ///
    /// Task82: 拠点（電力タブの複製プレハブ、WarfrontBasePrefab）専用だった単一メッシュ統合版
    /// <c>TryGetMesh</c> と、その平均色を返す <c>TryGetAverageColor</c> は、複製プレハブ機構自体の
    /// 完全撤去に伴い呼び出し元が無くなったため削除した（Building_*.obj のモデルファイル自体は
    /// asset-editor-export の書き出しフローが引き続き使うため残置、tools/ 参照）。
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

        /// <summary>冪等。WarfrontLoadingExtension.LoadModAssets から、UnitAssetBindings.Load /
        /// WarfrontSounds.Initialize と同じタイミングで呼ぶこと。</summary>
        public static void Initialize(string modDirectory)
        {
            if (_initialized) return;
            _initialized = true;
            _modDirectory = modDirectory;
        }

        /// <summary>
        /// Task69: Models/&lt;modelName&gt;.obj(+.mtl) を、.obj が持つ usemtl ブロックそのままの
        /// マルチサブメッシュ Mesh + サブメッシュごとの .mtl 色マテリアル配列として読み込む
        /// （<see cref="WarfrontMeshBuilder.TryBuild"/> 参照。MissileDisaster.Game.Models.
        /// MissileModelProvider.BuildFromObj を縮小移植したもの）。
        /// 既定モデルは全て自分自身のMTL色で描画する方針（勢力色ティントは既定モデルでは廃止、
        /// 呼び出し側 UnitVisuals 参照）のため、こちらが built-in モデル解決の主経路になる。
        /// 失敗時（未初期化/ファイル無し/解析失敗）は false を返す。名前単位でキャッシュする
        /// （静的モデルのため、生成した Mesh/Material[] をそのまま全ユニットで共有して問題ない）。
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
