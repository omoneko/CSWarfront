using System;
using System.Collections.Generic;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// ユニット描画用マテリアルを自前生成する小さなヘルパー。
    /// CS車両のマテリアル（VehicleInfo.m_material / m_lodMaterial）は専用シェーダーが
    /// 前提とし、そのシェーダーはCSのカスタムレンダラーが供給するper-instanceデータ
    /// （カラー配列・変換行列・ライティング状態）を要求する。素の MeshRenderer に割り当てると
    /// 不可視/黒でレンダリングされる（実際に発生していたバグ）ため、CS由来マテリアルは一切借用しない。
    /// 代わりに標準シェーダーで自前の Material を作り、勢力ごとに色分けする。
    /// マテリアルは勢力id単位（最大 <see cref="WarfrontSettings.MaxFactions"/> 種）でキャッシュ・共有し、
    /// sharedMaterial として割り当てる（per-instance化してリークさせない）。
    /// メインスレッド専用（Material/Shader生成を伴う）。
    ///
    /// Task37: 割り当て済みプロップについては勢力色で塗らず、プロップ自身の見た目
    /// （テクスチャ）を維持する。ただしCS側の Material オブジェクトそのものは
    /// （上記と同じ理由で）借用しない — <see cref="TryGetPropMaterial"/> は
    /// PropInfo.m_material.mainTexture だけを読み取り、自前の標準シェーダーMaterialに
    /// 貼り直す（Material.mainTexture / Material(Shader) は UnityEngine.dll をリフレクションで
    /// 検証済み。PropInfo.m_material は Assembly-CSharp.dll をリフレクションで検証済み、
    /// Task36 task-36-report.md 参照）。
    /// </summary>
    internal static class UnitMaterialFactory
    {
        // 勢力id 0..4 に対応する識別色。0=赤, 1=青, 2=緑, 3=黄, 4=マゼンタ。
        private static readonly Color[] FactionColors =
        {
            Color.red, Color.blue, Color.green, Color.yellow, Color.magenta
        };

        private static readonly Color FallbackColor = Color.white;

        private static readonly Dictionary<byte, Material> _cache = new Dictionary<byte, Material>();

        // プロップ名単位のマテリアルキャッシュ（Task37）。TryGetPropMaterial専用。
        private static readonly Dictionary<string, Material> _propCache = new Dictionary<string, Material>();

        private static Shader _shader;
        private static bool _shaderResolved;
        private static bool _loggedShaderFailure;

        /// <summary>
        /// 勢力idに対応するマテリアルを取得する（無ければ生成してキャッシュ）。
        /// シェーダーが一切見つからない環境（理論上のみ）では false を返す。
        /// </summary>
        public static bool TryGetFactionMaterial(byte factionId, out Material material)
        {
            Material cached;
            if (_cache.TryGetValue(factionId, out cached) && cached != null)
            {
                material = cached;
                return true;
            }

            Shader shader = ResolveShader();
            if (shader == null)
            {
                material = null;
                return false;
            }

            try
            {
                Material mat = new Material(shader);
                mat.color = ColorForFaction(factionId);
                _cache[factionId] = mat;
                material = mat;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMaterialFactory.TryGetFactionMaterial(" + factionId + ") error: " + e);
                material = null;
                return false;
            }
        }

        /// <summary>
        /// Task37: 割り当て済みプロップ用のマテリアルを取得する（無ければ生成してキャッシュ、プロップ名単位）。
        /// 自前の標準シェーダーMaterialを作り、色は白（tintしない＝勢力色で塗らない）のまま、
        /// mainTextureだけプロップ自身のマテリアル（PropInfo.m_material）から借用する。
        /// CSの Material オブジェクトそのものは一切割り当てない（TryGetFactionMaterialと同じ理由）。
        /// テクスチャが取得できない場合は白一色の標準マテリアルへフォールバックする
        /// （勢力色にはフォールバックしない＝要件2「勢力色で塗るのをやめる」を守る）。
        /// </summary>
        public static bool TryGetPropMaterial(string propName, out Material material)
        {
            if (string.IsNullOrEmpty(propName))
            {
                material = null;
                return false;
            }

            Material cached;
            if (_propCache.TryGetValue(propName, out cached) && cached != null)
            {
                material = cached;
                return true;
            }

            Shader shader = ResolveShader();
            if (shader == null)
            {
                material = null;
                return false;
            }

            try
            {
                Texture mainTexture = TryGetPropMainTexture(propName);

                Material mat = new Material(shader);
                mat.color = Color.white; // tintしない。プロップ自身の見た目を維持する。
                if (mainTexture != null) mat.mainTexture = mainTexture;

                _propCache[propName] = mat;
                material = mat;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMaterialFactory.TryGetPropMaterial(" + propName + ") error: " + e);
                material = null;
                return false;
            }
        }

        /// <summary>PropInfo.m_material.mainTexture を安全に読み取る（見つからない/null時はnullを返す）。</summary>
        private static Texture TryGetPropMainTexture(string propName)
        {
            try
            {
                PropInfo info = PrefabCollection<PropInfo>.FindLoaded(propName);
                if (info == null || info.m_material == null) return null;
                return info.m_material.mainTexture;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMaterialFactory.TryGetPropMainTexture(" + propName + ") error: " + e);
                return null;
            }
        }

        private static Color ColorForFaction(byte factionId)
        {
            return factionId < FactionColors.Length ? FactionColors[factionId] : FallbackColor;
        }

        private static Shader ResolveShader()
        {
            if (_shaderResolved) return _shader;
            _shaderResolved = true;

            try
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                if (shader == null) shader = Shader.Find("Diffuse");
                _shader = shader;

                if (shader == null && !_loggedShaderFailure)
                {
                    _loggedShaderFailure = true;
                    ModConfig.LogError("UnitMaterialFactory: シェーダー解決に失敗（Standard/Legacy Shaders/Diffuse/Diffuse 全滅）、ユニットは描画されません");
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMaterialFactory.ResolveShader error: " + e);
                _shader = null;
            }

            return _shader;
        }
    }
}
