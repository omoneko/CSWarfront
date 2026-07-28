using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// 発砲エフェクト（Task42）: WarState.RecentShotsから受け取ったShotEventを、短命なUnity
    /// GameObjectとして描画する。戦略ビューであることを踏まえ、シューターゲームのような派手さではなく
    /// 「何が起きているか一目でわかる」程度の軽量な表現にとどめる。
    ///
    /// スレッド境界: このクラスの public メソッドは全て「メインスレッド専用」
    /// （new GameObject / AddComponent / Destroy / transform書込みはUnityのメインスレッド制約）。
    /// sim スレッド（MilitaryManager.OnSimTick）からは絶対に呼ばないこと（UnitVisualsと同じ規約）。
    ///
    /// マテリアルはCS由来のものを一切借用しない。UnitMaterialFactoryと同じ理由（CS車両シェーダーは
    /// CS自身のレンダラー由来のper-instanceデータを要求し、素のRendererに割り当てると不可視/黒になる）
    /// により、自前の標準シェーダーMaterialを勢力に依存しない固定色で生成・共有する（sharedMaterial、
    /// per-instance化しない）。勢力色でチントしない＝トレーサーは「発砲そのもの」として読めるようにし、
    /// 陣営マーカーと誤読されないようにする（要件）。
    /// </summary>
    internal static class CombatFx
    {
        /// <summary>同時に生きていられるエフェクトの上限（Task42）。大規模乱戦でGameObjectが
        /// 際限なく増えないようにする防御的上限。ShotEvent側の上限(WarState.MaxRecentShotsPerTick)とは
        /// 独立に、こちらは「現在生存中」の総数を制限する。</summary>
        private const int MaxLiveEffects = 120;

        // カメラから遠すぎる発砲は生成自体をスキップする（軽量な距離チェックのみ）。
        private const float MaxSpawnDistanceFromCamera = 2000f;

        // Gunfire（Infantry/MechInfantry/Apc/DroneInfantry/AntiAir）: 細く短いトレーサー＋小さなマズルフラッシュ。
        private const float GunfireTracerDuration = 0.08f;
        private const float GunfireTracerWidth = 0.15f;
        private const float GunfireFlashSize = 1.2f;

        // DirectFire（Tank、直射）: 同じトレーサーだが太く・明るく・やや長持ち＋一回り大きいフラッシュ。
        private const float DirectFireTracerDuration = 0.15f;
        private const float DirectFireTracerWidth = 0.35f;
        private const float DirectFireFlashSize = 2.2f;

        // IndirectFire（Artillery、曲射）: Fromから放物線を飛ぶ小さな弾＋着弾時の短い噴煙。
        private const float ArcTravelDuration = 1.2f;
        private const float ArcApexRatio = 0.25f;   // 頂点高さ = 水平距離 × この比率
        private const float ArcApexMin = 4f;
        private const float ArcApexMax = 120f;
        private const float ArcShellSize = 1.6f;
        private const float ImpactPuffDuration = 0.3f;
        private const float ImpactPuffSize = 3.5f;

        // 暖色系固定（勢力色でチントしない）。
        private static readonly Color GunfireColor = new Color(1f, 0.92f, 0.55f);     // 暖かい黄白色
        private static readonly Color DirectFireColor = new Color(1f, 0.75f, 0.25f);  // より濃いオレンジ（直射の重み）
        private static readonly Color FlashColor = new Color(1f, 0.95f, 0.8f);
        private static readonly Color ShellColor = new Color(0.85f, 0.85f, 0.8f);
        private static readonly Color PuffColor = new Color(0.55f, 0.5f, 0.45f);

        private enum Phase { Tracer, ArcTravel, ImpactPuff }

        private class Effect
        {
            public GameObject Root;
            public Phase Phase;
            public float Elapsed;
            public float Duration;

            // Tracer(Gunfire/DirectFire)専用。
            public LineRenderer Line;
            public float InitialWidth;

            // Tracerのフラッシュ、またはArc/Impactの弾・噴煙（役割はPhaseで決まる）を指す共有transform。
            public Transform FlashOrShell;
            public float InitialFlashSize;

            // ArcTravel専用。
            public Vector3 From;
            public Vector3 To;
            public float ApexHeight;
        }

        private static readonly List<Effect> _effects = new List<Effect>();

        private static Shader _shader;
        private static bool _shaderResolved;
        private static Material _gunfireMaterial;
        private static Material _directFireMaterial;
        private static Material _flashMaterial;
        private static Material _shellMaterial;
        private static Material _puffMaterial;

        /// <summary>
        /// 1tick分のShotEventからエフェクトを生成する（メインスレッド専用）。
        /// MaxLiveEffectsに達していれば、それ以降のshotsは静かに無視する（例外にしない）。
        /// </summary>
        public static void Spawn(List<ShotEvent> shots)
        {
            if (shots == null || shots.Count == 0) return;

            try
            {
                Camera cam = Camera.main;
                Vector3? cameraPos = cam != null ? (Vector3?)cam.transform.position : null;

                for (int i = 0; i < shots.Count; i++)
                {
                    if (_effects.Count >= MaxLiveEffects) break;
                    SpawnOne(shots[i], cameraPos);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.Spawn error: " + e);
            }
        }

        /// <summary>生存中の全エフェクトを実時間(realDeltaTime)で進め、寿命が尽きたものを破棄する
        /// （メインスレッド専用）。ArcTravelはDurationに達すると新規GameObjectを作らず、同じ弾を
        /// 着弾噴煙(ImpactPuff)へ転生させてから継続する。</summary>
        public static void Update(float realDeltaTime)
        {
            if (_effects.Count == 0) return;

            try
            {
                for (int i = _effects.Count - 1; i >= 0; i--)
                {
                    Effect fx = _effects[i];
                    if (fx.Root == null) { _effects.RemoveAt(i); continue; }

                    fx.Elapsed += realDeltaTime;

                    switch (fx.Phase)
                    {
                        case Phase.Tracer: StepTracer(fx); break;
                        case Phase.ArcTravel: StepArcTravel(fx); break;
                        case Phase.ImpactPuff: StepImpactPuff(fx); break;
                    }

                    if (fx.Elapsed < fx.Duration) continue;

                    if (fx.Phase == Phase.ArcTravel)
                    {
                        TransitionToImpactPuff(fx);
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(fx.Root);
                        _effects.RemoveAt(i);
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.Update error: " + e);
            }
        }

        /// <summary>生存中の全エフェクトを破棄する（レベルアンロード時、メインスレッド専用）。
        /// キャッシュ済みマテリアルはGameObjectではないため破棄しない（UnitMaterialFactoryと同じ扱い、
        /// 次セッションでも使い回せる）。</summary>
        public static void DestroyAll()
        {
            try
            {
                for (int i = 0; i < _effects.Count; i++)
                {
                    if (_effects[i] != null && _effects[i].Root != null)
                        UnityEngine.Object.Destroy(_effects[i].Root);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.DestroyAll error: " + e);
            }
            finally
            {
                _effects.Clear();
            }
        }

        private static void SpawnOne(ShotEvent e, Vector3? cameraPos)
        {
            Vector3 from = new Vector3(e.From.X, e.From.Y, e.From.Z);
            Vector3 to = new Vector3(e.To.X, e.To.Y, e.To.Z);

            if (cameraPos.HasValue)
            {
                Vector3 mid = (from + to) * 0.5f;
                float distSqr = (mid - cameraPos.Value).sqrMagnitude;
                if (distSqr > MaxSpawnDistanceFromCamera * MaxSpawnDistanceFromCamera) return;
            }

            switch (e.Kind)
            {
                case ShotKind.Gunfire:
                    SpawnTracer(from, to, GunfireTracerDuration, GunfireTracerWidth, GunfireFlashSize,
                        GetGunfireMaterial());
                    break;
                case ShotKind.DirectFire:
                    SpawnTracer(from, to, DirectFireTracerDuration, DirectFireTracerWidth, DirectFireFlashSize,
                        GetDirectFireMaterial());
                    break;
                case ShotKind.IndirectFire:
                    SpawnArc(from, to);
                    break;
            }
        }

        private static void SpawnTracer(Vector3 from, Vector3 to, float duration, float width, float flashSize,
            Material tracerMaterial)
        {
            if (tracerMaterial == null) return;

            try
            {
                var go = new GameObject("CSWarfrontShotFx");
                var line = go.AddComponent<LineRenderer>();
                line.sharedMaterial = tracerMaterial;
                line.useWorldSpace = true;
                line.SetVertexCount(2);
                line.SetPosition(0, from);
                line.SetPosition(1, to);
                line.SetWidth(width, width * 0.4f);

                Transform flash = CreateSmallSphere(go.transform, from, flashSize, GetFlashMaterial());

                _effects.Add(new Effect
                {
                    Root = go,
                    Phase = Phase.Tracer,
                    Elapsed = 0f,
                    Duration = duration,
                    Line = line,
                    InitialWidth = width,
                    FlashOrShell = flash,
                    InitialFlashSize = flashSize
                });
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.SpawnTracer error: " + e);
            }
        }

        private static void SpawnArc(Vector3 from, Vector3 to)
        {
            Material shellMaterial = GetShellMaterial();
            if (shellMaterial == null) return;

            try
            {
                var go = new GameObject("CSWarfrontShotFxArc");
                Transform shell = CreateSmallSphere(go.transform, from, ArcShellSize, shellMaterial);

                float horizontalDist = Mathf.Sqrt((to.x - from.x) * (to.x - from.x) + (to.z - from.z) * (to.z - from.z));
                float apex = Mathf.Clamp(horizontalDist * ArcApexRatio, ArcApexMin, ArcApexMax);

                _effects.Add(new Effect
                {
                    Root = go,
                    Phase = Phase.ArcTravel,
                    Elapsed = 0f,
                    Duration = ArcTravelDuration,
                    FlashOrShell = shell,
                    InitialFlashSize = ArcShellSize,
                    From = from,
                    To = to,
                    ApexHeight = apex
                });
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.SpawnArc error: " + e);
            }
        }

        /// <summary>クリック選択のraycastを邪魔しないよう、Colliderを無効化した小さな球を作る
        /// （マズルフラッシュ／曳光弾／着弾噴煙で共用）。</summary>
        private static Transform CreateSmallSphere(Transform parent, Vector3 worldPos, float size, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Collider col = go.GetComponent<Collider>();
            if (col != null) col.enabled = false;

            go.transform.SetParent(parent, false);
            go.transform.position = worldPos;
            go.transform.localScale = new Vector3(size, size, size);

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null && material != null) renderer.sharedMaterial = material;

            return go.transform;
        }

        private static void StepTracer(Effect fx)
        {
            float t = fx.Duration > 0f ? Mathf.Clamp01(fx.Elapsed / fx.Duration) : 1f;
            if (fx.Line != null)
            {
                float w = Mathf.Lerp(fx.InitialWidth, 0f, t);
                fx.Line.SetWidth(w, w * 0.4f);
            }
            if (fx.FlashOrShell != null)
            {
                float s = Mathf.Lerp(fx.InitialFlashSize, 0f, t);
                fx.FlashOrShell.localScale = new Vector3(s, s, s);
            }
        }

        private static void StepArcTravel(Effect fx)
        {
            if (fx.FlashOrShell == null) return;
            float t = fx.Duration > 0f ? Mathf.Clamp01(fx.Elapsed / fx.Duration) : 1f;
            Vector3 pos = Vector3.Lerp(fx.From, fx.To, t);
            // 4*apex*t*(1-t): t=0(発射)/1(着弾)で0、t=0.5でapex高さになる標準的な放物線補間。
            pos.y += 4f * fx.ApexHeight * t * (1f - t);
            fx.FlashOrShell.position = pos;
        }

        private static void StepImpactPuff(Effect fx)
        {
            if (fx.FlashOrShell == null) return;
            float t = fx.Duration > 0f ? Mathf.Clamp01(fx.Elapsed / fx.Duration) : 1f;
            float s = Mathf.Lerp(fx.InitialFlashSize, 0f, t);
            fx.FlashOrShell.localScale = new Vector3(s, s, s);
        }

        /// <summary>着弾した弾(shell)を、新規GameObjectを作らず短い噴煙(puff)へ転生させる。
        /// 転生に失敗した場合はエフェクトを取り残さないよう即座に破棄する。</summary>
        private static void TransitionToImpactPuff(Effect fx)
        {
            try
            {
                if (fx.FlashOrShell != null)
                {
                    fx.FlashOrShell.position = fx.To;
                    fx.FlashOrShell.localScale = new Vector3(ImpactPuffSize, ImpactPuffSize, ImpactPuffSize);
                    Renderer r = fx.FlashOrShell.GetComponent<Renderer>();
                    if (r != null) r.sharedMaterial = GetPuffMaterial();
                }
                fx.Phase = Phase.ImpactPuff;
                fx.Elapsed = 0f;
                fx.Duration = ImpactPuffDuration;
                fx.InitialFlashSize = ImpactPuffSize;
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.TransitionToImpactPuff error: " + e);
                if (fx.Root != null) UnityEngine.Object.Destroy(fx.Root);
                _effects.Remove(fx);
            }
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

                if (shader == null)
                    ModConfig.LogError("CombatFx: シェーダー解決に失敗（Standard/Legacy Shaders/Diffuse/Diffuse 全滅）、発砲エフェクトは描画されません");
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.ResolveShader error: " + e);
                _shader = null;
            }

            return _shader;
        }

        private static Material BuildMaterial(Color color)
        {
            Shader shader = ResolveShader();
            if (shader == null) return null;

            try
            {
                var mat = new Material(shader);
                mat.color = color;
                return mat;
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.BuildMaterial error: " + e);
                return null;
            }
        }

        private static Material GetGunfireMaterial()
        {
            if (_gunfireMaterial == null) _gunfireMaterial = BuildMaterial(GunfireColor);
            return _gunfireMaterial;
        }

        private static Material GetDirectFireMaterial()
        {
            if (_directFireMaterial == null) _directFireMaterial = BuildMaterial(DirectFireColor);
            return _directFireMaterial;
        }

        private static Material GetFlashMaterial()
        {
            if (_flashMaterial == null) _flashMaterial = BuildMaterial(FlashColor);
            return _flashMaterial;
        }

        private static Material GetShellMaterial()
        {
            if (_shellMaterial == null) _shellMaterial = BuildMaterial(ShellColor);
            return _shellMaterial;
        }

        private static Material GetPuffMaterial()
        {
            if (_puffMaterial == null) _puffMaterial = BuildMaterial(PuffColor);
            return _puffMaterial;
        }
    }
}
