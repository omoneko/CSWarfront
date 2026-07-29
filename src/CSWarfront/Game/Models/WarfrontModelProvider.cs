using System;
using System.Collections.Generic;
using System.IO;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Models
{
    /// <summary>
    /// 兵科別・軍事基地の既定（built-in）モデルの単一窓口（Task57、Task69でマルチマテリアル対応）。
    /// Mod 配置フォルダの Models/&lt;name&gt;.obj(+.mtl) から実行時にメッシュ/マテリアルを構築して
    /// キャッシュする。
    /// <see cref="TryGetMesh"/>: MissileDisaster.Game.Models.MissileModelProvider.LoadMergedMesh を
    /// 縮小移植。全サブメッシュを1つに統合した単一 Mesh のみ返す（マテリアルは呼び出し側が塗る）。
    /// 拠点（WarfrontBasePrefab、CSの BuildingInfo.m_material が単一フィールドのため複数マテリアルを
    /// 受け取れない）専用。
    /// <see cref="TryGetModel"/>: MissileDisaster.Game.Models.MissileModelProvider.BuildFromObj を
    /// 縮小移植。.obj の usemtl ブロックをそのままサブメッシュとして残し、サブメッシュごとに .mtl の
    /// Kd 色で塗った自前マテリアルを返す（GameObject化は呼び出し側 UnitVisuals が行う）。
    /// ユニットの既定モデル解決の主経路（Task69: 既定モデルは常に自分自身のMTL色で描画し、勢力色
    /// ティントは廃止した）。
    /// AssetBundle・デカール・GameObject即時生成等は持ち込まない。
    /// メッシュ/マテリアル生成を伴うため、必ずメインスレッドから呼ぶこと。
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
        private static readonly Dictionary<string, Mesh> _meshCache = new Dictionary<string, Mesh>();
        private static readonly Dictionary<string, BuiltModel> _modelCache = new Dictionary<string, BuiltModel>();
        private static readonly Dictionary<string, Color> _averageColorCache = new Dictionary<string, Color>();
        private static readonly HashSet<string> _warnedMissing = new HashSet<string>();
        private static readonly HashSet<string> _warnedMissingModel = new HashSet<string>();

        /// <summary>冪等。WarfrontLoadingExtension.LoadModAssets から、UnitAssetBindings.Load /
        /// WarfrontSounds.Initialize と同じタイミングで（かつ WarfrontBasePrefab.EnsureRegistered
        /// より前に）呼ぶこと。</summary>
        public static void Initialize(string modDirectory)
        {
            if (_initialized) return;
            _initialized = true;
            _modDirectory = modDirectory;
        }

        /// <summary>Models/&lt;modelName&gt;.obj を単一サブメッシュ Mesh として読み込む。
        /// 失敗時（未初期化/ファイル無し/解析失敗）は false を返す。名前単位でキャッシュする
        /// （tools/gen_models.py が出力する静的モデルは実行中に変わらないため、
        /// UnitMeshSourceの割り当て系キャッシュ方針とは異なりキャッシュして問題ない）。</summary>
        public static bool TryGetMesh(string modelName, out Mesh mesh)
        {
            mesh = null;
            try
            {
                if (string.IsNullOrEmpty(modelName)) return false;

                Mesh cached;
                if (_meshCache.TryGetValue(modelName, out cached) && cached != null)
                {
                    mesh = cached;
                    return true;
                }

                if (string.IsNullOrEmpty(_modDirectory))
                {
                    ModConfig.LogError("WarfrontModelProvider.TryGetMesh(" + modelName + "): modDirectory 未初期化 (Initialize 未呼び出し)");
                    return false;
                }

                string objPath = Path.Combine(Path.Combine(_modDirectory, ModConfig.ModelsFolderName), modelName + ".obj");
                if (!File.Exists(objPath))
                {
                    if (_warnedMissing.Add(modelName))
                    {
                        ModConfig.LogError("WarfrontModelProvider: OBJ が見つかりません path=" + objPath);
                    }
                    return false;
                }

                ObjData data = ObjParser.Parse(File.ReadAllText(objPath));

                Mesh built;
                if (!WarfrontMeshBuilder.TryBuildMergedMesh(data, out built))
                {
                    ModConfig.LogError("WarfrontModelProvider: メッシュ構築失敗 name=" + modelName + " path=" + objPath);
                    return false;
                }

                _meshCache[modelName] = built;
                ModConfig.Log("WarfrontModelProvider: built-in model を読み込みました name=" + modelName);
                mesh = built;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontModelProvider.TryGetMesh(" + modelName + ") error: " + e);
                mesh = null;
                return false;
            }
        }

        /// <summary>
        /// Task69: Models/&lt;modelName&gt;.obj(+.mtl) を、.obj が持つ usemtl ブロックそのままの
        /// マルチサブメッシュ Mesh + サブメッシュごとの .mtl 色マテリアル配列として読み込む
        /// （<see cref="WarfrontMeshBuilder.TryBuild"/> 参照。MissileDisaster.Game.Models.
        /// MissileModelProvider.BuildFromObj を縮小移植したもの）。
        /// 既定モデルは全て自分自身のMTL色で描画する方針（勢力色ティントは既定モデルでは廃止、
        /// 呼び出し側 UnitVisuals 参照）のため、こちらが built-in モデル解決の主経路になる。
        /// <see cref="TryGetMesh"/>（単一サブメッシュへ統合、勢力色で塗る用途）は
        /// WarfrontBasePrefab（拠点の既定モデル。CSの BuildingInfo.m_material が単一フィールドの
        /// ため、そもそも複数マテリアルを受け取れない）専用として残す。
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
                    ModConfig.LogError("WarfrontModelProvider.TryGetModel(" + modelName + "): modDirectory 未初期化 (Initialize 未呼び出し)");
                    return false;
                }

                string modelsDir = Path.Combine(_modDirectory, ModConfig.ModelsFolderName);
                string objPath = Path.Combine(modelsDir, modelName + ".obj");
                if (!File.Exists(objPath))
                {
                    if (_warnedMissingModel.Add(modelName))
                    {
                        ModConfig.LogError("WarfrontModelProvider: OBJ が見つかりません path=" + objPath);
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
                    ModConfig.LogError("WarfrontModelProvider: マルチマテリアルメッシュ構築失敗 name=" + modelName + " path=" + objPath);
                    return false;
                }

                _modelCache[modelName] = new BuiltModel { Mesh = built, Materials = builtMaterials };
                ModConfig.Log("WarfrontModelProvider: built-in model(multi-material) を読み込みました name=" + modelName +
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

        /// <summary>
        /// Task71: Models/&lt;modelName&gt;.mtl の全マテリアルのKd値を単純平均した色を返す。
        /// <see cref="TryGetMesh"/>（TryBuildMergedMesh、全サブメッシュを単一メッシュへ統合し
        /// マテリアル情報を破棄する）と組み合わせる拠点の既定モデル向け。基地の建物は
        /// BuildingInfo.m_material が単一フィールドのため、Task69でBlender書き出しに切り替わった
        /// 複数マテリアルモデル（Building_MilitaryBase/NavalBase/AirBase.obj、6色前後のusemtl）を
        /// そのまま多色描画することはできない（要件3、効果/リスクを検討した上での判断。
        /// task-71-report.md参照）。単色化はするが、ハードコードした固定色ではなく実際の
        /// Blenderパレットの平均色を使うことで、完全な作り物の色よりは元の見た目に近づける。
        /// .mtl が無い/空/解析失敗時はfalseを返す（呼び出し側は既定色にフォールバックすること）。
        /// 名前単位でキャッシュする（TryGetMeshと同じ理由）。
        /// </summary>
        public static bool TryGetAverageColor(string modelName, out Color color)
        {
            color = default(Color);
            try
            {
                if (string.IsNullOrEmpty(modelName)) return false;

                Color cached;
                if (_averageColorCache.TryGetValue(modelName, out cached))
                {
                    color = cached;
                    return true;
                }

                if (string.IsNullOrEmpty(_modDirectory)) return false;

                string mtlPath = Path.Combine(Path.Combine(_modDirectory, ModConfig.ModelsFolderName), modelName + ".mtl");
                if (!File.Exists(mtlPath)) return false;

                Dictionary<string, MtlColor> mtl = MtlParser.Parse(File.ReadAllText(mtlPath));
                if (mtl == null || mtl.Count == 0) return false;

                float r = 0f, g = 0f, b = 0f;
                foreach (var kv in mtl)
                {
                    r += kv.Value.R;
                    g += kv.Value.G;
                    b += kv.Value.B;
                }
                Color avg = new Color(r / mtl.Count, g / mtl.Count, b / mtl.Count, 1f);

                _averageColorCache[modelName] = avg;
                color = avg;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontModelProvider.TryGetAverageColor(" + modelName + ") error: " + e);
                color = default(Color);
                return false;
            }
        }
    }
}
