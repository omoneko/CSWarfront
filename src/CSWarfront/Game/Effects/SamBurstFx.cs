using System;
using UnityEngine;

namespace CSWarfront.Game.Effects
{
    /// <summary>
    /// One-shot burst FX related to anti-air missiles (Task90, a port of
    /// MissileDisaster.Game.Effects.InterceptFx):
    ///  - PlayFlash: flash on hit (a spark burst of additive particles)
    ///  - PlayFizzle: small dud smoke on miss/self-destruct
    ///  - PlayFlares: flare release by the target aircraft (bright orange light points burn out while
    ///    falling under gravity. Visual expression of the user request "fighters and bombers should
    ///    release flares against anti-air missiles and evade")
    /// Self-contained implementation that does not depend on the vanilla explosion prefabs. Main
    /// thread only because it creates GameObjects/Materials.
    /// </summary>
    internal static class SamBurstFx
    {
        // Calibrated values scaling InterceptFlash* (MissileDisaster) down to anti-air scale.
        private const int FlashBurst = 30;
        private const float FlashLifetime = 0.45f;
        private const float FlashSpeed = 35f;
        private const float FlashSize = 14f;
        private static readonly Color FlashCoreColor = new Color(1f, 0.95f, 0.7f, 1f);
        private static readonly Color FlashEdgeColor = new Color(1f, 0.55f, 0.15f, 1f);

        private const int FizzleBurst = 10;

        // Flares: scatter a few bright light points behind, letting them burn out while falling under gravity.
        private const int FlareBurst = 6;
        private const float FlareLifetime = 1.4f;
        private const float FlareSpeed = 18f;
        private const float FlareSize = 6f;
        private const float FlareGravity = 1.2f;
        private static readonly Color FlareCoreColor = new Color(1f, 0.85f, 0.4f, 1f);
        private static readonly Color FlareEdgeColor = new Color(1f, 0.5f, 0.1f, 1f);

        private static Material _flashMat;
        private static Texture2D _glowTex;
        private static bool _ready;

        /// <summary>Emits the on-hit flash once.</summary>
        public static void PlayFlash(Vector3 point)
        {
            Emit("CSWarfrontSamFlash", point, FlashBurst, FlashSize, FlashSpeed, FlashLifetime,
                FlashCoreColor, FlashEdgeColor, 0f);
        }

        /// <summary>Emits the small dud smoke on a miss (self-destruct).</summary>
        public static void PlayFizzle(Vector3 point)
        {
            Emit("CSWarfrontSamFizzle", point, FizzleBurst, FlashSize * 0.4f, FlashSpeed * 0.35f, FlashLifetime,
                FlashCoreColor, FlashEdgeColor, 0f);
        }

        /// <summary>Flare release by the target aircraft (flickering light points falling under gravity).</summary>
        public static void PlayFlares(Vector3 point)
        {
            Emit("CSWarfrontFlares", point, FlareBurst, FlareSize, FlareSpeed, FlareLifetime,
                FlareCoreColor, FlareEdgeColor, FlareGravity);
        }

        private static void Emit(string name, Vector3 point, int burst, float size, float speed, float lifetime,
            Color coreColor, Color edgeColor, float gravity)
        {
            try
            {
                EnsureAssets();
                var go = new GameObject(name);
                go.transform.position = point;
                var ps = go.AddComponent<ParticleSystem>();

                var main = ps.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.startLifetime = lifetime;
                main.startSpeed = speed;
                main.startSize = size;
                main.startColor = new ParticleSystem.MinMaxGradient(coreColor, edgeColor);
                main.maxParticles = 128;
                if (gravity > 0f) main.gravityModifier = gravity;

                var emission = ps.emission;
                emission.enabled = true;
                emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)burst) });

                var shape = ps.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = size * 0.2f;

                var col = ps.colorOverLifetime;
                col.enabled = true;
                var grad = new Gradient();
                grad.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new[]
                    {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(0.7f, 0.4f),
                        new GradientAlphaKey(0f, 1f),
                    });
                col.color = new ParticleSystem.MinMaxGradient(grad);

                var sol = ps.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f,
                    new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.15f)));

                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                if (_flashMat != null) renderer.material = _flashMat;
                renderer.renderMode = ParticleSystemRenderMode.Billboard;

                ps.Play();
                UnityEngine.Object.Destroy(go, lifetime + 0.3f);
            }
            catch (Exception e)
            {
                ModConfig.LogError("SamBurstFx.Emit(" + name + ") error: " + e);
            }
        }

        private static void EnsureAssets()
        {
            if (_ready) return;
            _ready = true;
            _glowTex = FxShaderAssets.BuildGlowTexture(64);
            _flashMat = FxShaderAssets.BuildAdditiveMaterial(_glowTex);
        }
    }
}
