using System;
using UnityEngine;

namespace CSWarfront.Game.Effects
{
    /// <summary>
    /// Utility for finding shaders that are "actually available" in CS's Unity runtime
    /// (Task90, a reduced port of MissileDisaster.Game.Effects.RenderAssets. Logic unchanged).
    /// CS often strips unreferenced built-in shaders (e.g. "Particles/Additive") from the build,
    /// so Shader.Find returns null for them -> the material has no shader and turns "magenta".
    /// Probes candidates in order for one that actually exists.
    /// Main thread only, since everything touches Shader/Resources.
    /// </summary>
    internal static class FxShaderAssets
    {
        /// <summary>Runs Shader.Find over the candidate names in order and returns the first
        /// (non-null) shader found. Null if all fail.</summary>
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
                catch (Exception) { /* Move on to the next candidate */ }
            }
            return null;
        }

        /// <summary>Returns the first loaded shader whose name contains any of substrsLower (lowercase).</summary>
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

        /// <summary>Forces a particle material into a render state that is correctly occluded by scene
        /// depth (transparent queue + ZTest LEqual + ZWrite Off. Verified in-game in MissileDisaster).</summary>
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

        /// <summary>Material for additive particles (with the glow texture). Port of MissileDisaster
        /// InterceptFx.BuildAdditiveMaterial.</summary>
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

        /// <summary>Material for alpha-blended particles (for exhaust smoke). Port of InterceptorTrail
        /// BuildParticleMaterial(additive:false), plus occlusion applied.</summary>
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

        /// <summary>Radial glow texture, bright at the center and transparent at the rim
        /// (port of InterceptFx.BuildGlowTexture).</summary>
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
