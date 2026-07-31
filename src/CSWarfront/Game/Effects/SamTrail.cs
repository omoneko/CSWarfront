using UnityEngine;

namespace CSWarfront.Game.Effects
{
    /// <summary>
    /// 対空ミサイルに付けるロケット排気トレイル（ノズル火炎＋噴煙）。Task90、
    /// MissileDisaster.Game.Effects.InterceptorTrailの移植（ユーザー指定「迎撃アニメーション周りは
    /// MissileDisaster MODを参考に」——構造は不変、定数はスケールに合わせて縮小）。
    /// ワールド空間なので飛翔経路に噴煙の航跡を残し、弾体破棄後も噴煙は寿命まで漂わせる。
    /// GameObject/Material/Meshを生成するためメインスレッド専用。
    /// </summary>
    internal static class SamTrail
    {
        // MissileDisasterのExhaust*定数を対空ミサイルの規模（弾体3〜4m、飛翔0.5秒前後）へ縮小した較正値。
        private const float FireRate = 90f;
        private const float FireLifetime = 0.2f;
        private const float FireSize = 3f;
        private const float SmokeRate = 70f;
        private const float SmokeLifetime = 1.8f;
        private const float SmokeSize = 2.5f;
        private static readonly Color FireColor = new Color(1f, 0.9f, 0.6f, 1f);
        private static readonly Color SmokeColor = new Color(0.85f, 0.85f, 0.85f, 0.32f);

        private static Material _fireMat;
        private static Material _smokeMat;
        private static Texture2D _glowTex;
        private static bool _ready;

        /// <summary>対空ミサイルGameObjectにノズル火炎と噴煙を子として付与する。失敗しても飛翔は継続。</summary>
        public static void Attach(GameObject missile)
        {
            if (missile == null) return;
            try
            {
                EnsureAssets();
                CreateSystem(missile, "CSWarfrontSamExhaust_Fire", FireLifetime, 1.2f, FireSize, FireColor,
                    FireRate, _fireMat, 1f, 0.1f, 0f);
                CreateSystem(missile, "CSWarfrontSamExhaust_Smoke", SmokeLifetime, 0.6f, SmokeSize, SmokeColor,
                    SmokeRate, _smokeMat, 0.5f, 1.1f, 20f);
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("SamTrail.Attach error: " + e);
            }
        }

        /// <summary>弾体からトレイルを切り離し、新規放出だけ止めて既存の噴煙は寿命まで残す
        /// （InterceptorTrail.DetachAndLingerの移植）。迎撃点到達時にこれを呼んでから弾体を破棄する。</summary>
        public static void DetachAndLinger(GameObject missile)
        {
            if (missile == null) return;
            try
            {
                ParticleSystem[] systems = missile.GetComponentsInChildren<ParticleSystem>();
                float life = Mathf.Max(FireLifetime, SmokeLifetime);
                for (int i = 0; i < systems.Length; i++)
                {
                    ParticleSystem ps = systems[i];
                    if (ps == null) continue;
                    ps.transform.SetParent(null, true); // ワールド位置維持で独立
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    Object.Destroy(ps.gameObject, life + 0.1f);
                }
            }
            catch (System.Exception e)
            {
                ModConfig.LogError("SamTrail.DetachAndLinger error: " + e);
            }
        }

        private static void EnsureAssets()
        {
            if (_ready) return;
            _ready = true;
            _glowTex = FxShaderAssets.BuildGlowTexture(64);
            _fireMat = FxShaderAssets.BuildAdditiveMaterial(_glowTex);
            _smokeMat = FxShaderAssets.BuildAlphaBlendedMaterial(_glowTex);
        }

        private static void CreateSystem(GameObject parent, string name, float lifetime, float speed, float size,
            Color color, float rate, Material material, float sizeFrom, float sizeTo, float sortingFudge)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = Vector3.one;
            var ps = go.AddComponent<ParticleSystem>();

            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Local;
            main.startLifetime = lifetime;
            main.startSpeed = speed;
            main.startSize = size;
            main.startColor = new ParticleSystem.MinMaxGradient(color);
            main.maxParticles = 400;

            var emission = ps.emission;
            emission.rateOverTime = rate;

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
                    new GradientAlphaKey(0.8f, 0.3f),
                    new GradientAlphaKey(0f, 1f),
                });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(new Keyframe(0f, sizeFrom), new Keyframe(1f, sizeTo)));

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (material != null) renderer.material = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            if (sortingFudge > 0f) renderer.sortingFudge = sortingFudge;
            ps.Play();
        }
    }
}
