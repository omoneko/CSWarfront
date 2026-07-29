using System;
using System.Collections.Generic;
using System.IO;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Models
{
    /// <summary>
    /// 兵科別・軍事基地の既定（built-in）モデルの単一窓口（Task57）。Mod 配置フォルダの
    /// Models/&lt;name&gt;.obj から実行時にメッシュを構築してキャッシュする。
    /// MissileDisaster.Game.Models.MissileModelProvider.LoadMergedMesh を縮小移植したもの
    /// （AssetBundle・デカール・GameObject即時生成等は持ち込まない。ここではメッシュのみ提供し、
    /// GameObject化やマテリアル割り当ては呼び出し側 UnitVisuals/WarfrontBasePrefab が行う）。
    /// メッシュ生成を伴うため、必ずメインスレッドから呼ぶこと。
    /// </summary>
    internal static class WarfrontModelProvider
    {
        private static string _modDirectory;
        private static bool _initialized;
        private static readonly Dictionary<string, Mesh> _meshCache = new Dictionary<string, Mesh>();
        private static readonly HashSet<string> _warnedMissing = new HashSet<string>();

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
    }
}
