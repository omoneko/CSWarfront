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
    /// Task37: 割り当て済みアセットについては勢力色で塗らず、アセット自身の見た目
    /// （テクスチャ）を維持する。ただしCS側の Material オブジェクトそのものは
    /// （上記と同じ理由で）借用しない。<see cref="TryGetAssetMaterial"/> は
    /// AssetCatalog.TryGetTexture（内部で PropInfo/BuildingInfo/VehicleInfo/TreeInfo の
    /// m_material.mainTexture を読む）だけを使い、自前の標準シェーダーMaterialに貼り直す
    /// （Material.mainTexture / Material(Shader) は UnityEngine.dll をリフレクションで
    /// 検証済み。各アセット型の m_material は Assembly-CSharp.dll をリフレクションで検証済み、
    /// Task36 task-36-report.md / Task41 task-41-report.md 参照）。
    ///
    /// Task41: マテリアルキャッシュのキーを名前(string)単独から (AssetKind, name) の組へ拡張した。
    /// 名前だけをキーにすると、例えば同名の建物とプロップが両方ロードされている場合に片方の
    /// テクスチャがもう片方へ誤って使い回されてしまう（PrefabCollectionは種類ごとに独立した名前空間の
    /// ため、名前の一致は種類をまたいでは何も保証しない）。AssetKey で種類込みの複合キーにすることで
    /// これを防ぐ。
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

        /// <summary>Task41: (種類, 名前) の複合キー。同名でも種類が違えば別エントリとして扱う。</summary>
        private struct AssetKey : IEquatable<AssetKey>
        {
            public AssetKind Kind;
            public string Name;

            public bool Equals(AssetKey other)
            {
                return Kind == other.Kind && string.Equals(Name, other.Name, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is AssetKey && Equals((AssetKey)obj);
            }

            public override int GetHashCode()
            {
                int hash = (int)Kind;
                if (Name != null) hash = (hash * 397) ^ Name.GetHashCode();
                return hash;
            }
        }

        // (種類, 名前) 単位のマテリアルキャッシュ（Task37で導入、Task41で種類込みのキーへ拡張）。
        // TryGetAssetMaterial専用。
        private static readonly Dictionary<AssetKey, Material> _assetCache = new Dictionary<AssetKey, Material>();

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
        /// Task37: 割り当て済みアセット用のマテリアルを取得する（無ければ生成してキャッシュ）。
        /// Task41: 対象をプロップ以外（建物/車両/樹木）にも拡張し、キャッシュキーを (kind, name) にした。
        /// 自前の標準シェーダーMaterialを作り、色は白（tintしない＝勢力色で塗らない）のまま、
        /// mainTextureだけアセット自身のマテリアル（AssetCatalog.TryGetTexture経由）から借用する。
        /// CSの Material オブジェクトそのものは一切割り当てない（TryGetFactionMaterialと同じ理由）。
        /// テクスチャが取得できない場合は白一色の標準マテリアルへフォールバックする
        /// （勢力色にはフォールバックしない＝要件2「勢力色で塗るのをやめる」を守る）。
        /// </summary>
        public static bool TryGetAssetMaterial(AssetKind kind, string assetName, out Material material)
        {
            if (string.IsNullOrEmpty(assetName))
            {
                material = null;
                return false;
            }

            AssetKey key = new AssetKey { Kind = kind, Name = assetName };

            Material cached;
            if (_assetCache.TryGetValue(key, out cached) && cached != null)
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
                Texture mainTexture;
                AssetCatalog.TryGetTexture(kind, assetName, out mainTexture);

                Material mat = new Material(shader);
                mat.color = Color.white; // tintしない。アセット自身の見た目を維持する。
                if (mainTexture != null) mat.mainTexture = mainTexture;

                _assetCache[key] = mat;
                material = mat;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMaterialFactory.TryGetAssetMaterial(" + kind + "," + assetName + ") error: " + e);
                material = null;
                return false;
            }
        }

        /// <summary>
        /// Task57: 軍事基地プレハブ（WarfrontBasePrefab）の既定モデル用マテリアルを生成する。
        /// 勢力別ではない（基地プレハブは全勢力共通の1つのBuildingInfoとして登録されるため、
        /// ユニットのような勢力色分けの入力がそもそも無い）ため、固定色を受け取って単純に
        /// 自前の標準シェーダーMaterialへ塗るだけの薄いヘルパー。キャッシュしない
        /// （EnsureRegistered実行時に1回だけ呼ばれる想定で、頻度・コストとも無視できるため）。
        /// TryGetFactionMaterial/TryGetAssetMaterialと同じ理由でCS側のMaterialは一切借用しない。
        /// </summary>
        public static bool TryGetSolidColorMaterial(Color color, out Material material)
        {
            Shader shader = ResolveShader();
            if (shader == null)
            {
                material = null;
                return false;
            }

            try
            {
                Material mat = new Material(shader);
                mat.color = color;
                material = mat;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMaterialFactory.TryGetSolidColorMaterial error: " + e);
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
