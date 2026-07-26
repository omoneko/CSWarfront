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
