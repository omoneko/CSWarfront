using System;
using UnityEngine;

namespace CSWarfront.Game.Effects
{
    /// <summary>
    /// CSのUnityランタイムで「実際に利用可能なシェーダー」を見つけるユーティリティ
    /// （Task90、MissileDisaster.Game.Effects.RenderAssetsの縮小移植。ロジック不変）。
    /// CSは未参照の組み込みシェーダー（例: "Particles/Additive"）をビルドから除去していることが多く、
    /// Shader.Findがそれらにnullを返す→マテリアルが付かず「マゼンタ」になる。実在するものを順に探す。
    /// 全てShader/Resourcesに触れるためメインスレッド専用。
    /// </summary>
    internal static class FxShaderAssets
    {
        /// <summary>候補名を順にShader.Findし、最初に見つかった(非null)シェーダーを返す。全滅ならnull。</summary>
        public static Shader FindFirst(params string[] names)
        {
            if (names == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    Shader s = Shader.Find(names[i]);
                    if (s != null) return s;
                }
                catch (Exception) { /* 次の候補へ */ }
            }
            return null;
        }

        /// <summary>ロード済みシェーダーから、名前にsubstrsLowerのいずれか(小文字)を含む最初のものを返す。</summary>
        public static Shader FindLoadedContaining(params string[] substrsLower)
        {
            try
            {
                Shader[] all = Resources.FindObjectsOfTypeAll<Shader>();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null || string.IsNullOrEmpty(all[i].name)) continue;
                    string lower = all[i].name.ToLowerInvariant();
                    for (int j = 0; j < substrsLower.Length; j++)
                    {
                        if (!string.IsNullOrEmpty(substrsLower[j]) && lower.Contains(substrsLower[j])) return all[i];
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("FxShaderAssets.FindLoadedContaining error: " + e);
            }
            return null;
        }

        /// <summary>パーティクル用マテリアルへ、シーンの奥行きに対して正しく遮蔽される描画状態を強制する
        /// （透明キュー＋ZTest LEqual＋ZWrite Off。MissileDisasterで実機検証済み）。</summary>
        public static void ApplyDepthOcclusion(Material mat)
        {
            if (mat == null) return;
            try
            {
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
                mat.SetInt("_ZWrite", 0);
            }
            catch (Exception e)
            {
                ModConfig.LogError("FxShaderAssets.ApplyDepthOcclusion error: " + e);
            }
        }

        /// <summary>加算パーティクル用マテリアル（グローテクスチャ付き）。MissileDisaster
        /// InterceptFx.BuildAdditiveMaterialの移植。</summary>
        public static Material BuildAdditiveMaterial(Texture2D tex)
        {
            Shader shader = FindFirst("Particles/Additive", "Legacy Shaders/Particles/Additive", "Mobile/Particles/Additive");
            if (shader == null) shader = FindLoadedContaining("additive");
            if (shader == null) shader = FindFirst("Sprites/Default", "Unlit/Transparent");
            if (shader == null) shader = FindLoadedContaining("particle", "sprite", "unlit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null;

            var mat = new Material(shader);
            ApplyTexAndTint(mat, tex);
            return mat;
        }

        /// <summary>アルファブレンドパーティクル用マテリアル（噴煙向け）。InterceptorTrail
        /// BuildParticleMaterial(additive:false)の移植＋遮蔽適用。</summary>
        public static Material BuildAlphaBlendedMaterial(Texture2D tex)
        {
            Shader shader = FindFirst("Particles/Alpha Blended", "Legacy Shaders/Particles/Alpha Blended",
                "Particles/Alpha Blended Premultiply");
            if (shader == null) shader = FindLoadedContaining("alpha blend", "alphablend");
            if (shader == null) shader = FindFirst("Sprites/Default", "Unlit/Transparent");
            if (shader == null) shader = FindLoadedContaining("particle", "sprite", "unlit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null;

            var mat = new Material(shader);
            ApplyTexAndTint(mat, tex);
            ApplyDepthOcclusion(mat);
            return mat;
        }

        private static void ApplyTexAndTint(Material mat, Texture2D tex)
        {
            if (tex != null)
            {
                mat.mainTexture = tex;
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            }
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", Color.white);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            mat.color = Color.white;
        }

        /// <summary>中心が明るく外周が透明な放射状グローテクスチャ（InterceptFx.BuildGlowTextureの移植）。</summary>
        public static Texture2D BuildGlowTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float half = (size - 1) * 0.5f;
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a;
                    pixels[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
