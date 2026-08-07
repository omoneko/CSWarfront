using System;
using System.Collections.Generic;
using ColossalFramework;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Small explosion effect on vehicle kills (Task65, user request "explosion effect on vehicle
    /// kills (a small explosion is fine)"). Of State.RecentKills, only categories for which
    /// CombatFx.IsVehicleDestructionCategory returns true (vehicle-type kills, excluding infantry and
    /// drone soldiers) are targeted = exactly the same criterion as the kill sounds
    /// (CombatFx.SpawnKillSounds), shared from a single place (CombatFxSound.cs)
    /// (Task51's rule "flesh-and-blood infantry exploding looks unnatural" is kept consistent across
    /// both sound and effects, without duplicating the category list).
    ///
    /// Appearance (Task84, user request "make kill explosions a bit more realistic. Using CS's own
    /// effects is fine. Keep the scale about the same"): plays the CS standard explosion effect
    /// DisasterProperties.m_mediumExplosion via EffectManager.DispatchEffect (the path proven in
    /// AlienInvasion's Effects.PlayImpactBurst. This gives a real explosion including particle
    /// fireball, smoke, and light). The magnitude is calibrated to a modest value matching the old
    /// fireball size (peak ~5.5m) (0.5, smaller than the Alien laser impact's 0.7).
    /// In environments where the EffectInfo cannot be resolved, it automatically falls back to the
    /// old primitive-sphere-based simple explosion (the fallback implementation below, the Task65
    /// implementation as-is).
    ///
    /// The old policy (do not borrow CS-owned resources) targeted the invisible-material bug from
    /// borrowing materials (cs-mesh-material-rendering); dispatching to EffectManager is unrelated to
    /// that problem because CS itself takes care of the rendering (field-proven in the Alien/Godzilla
    /// mods).
    ///
    /// To avoid bloating CombatFx.cs (577 lines, close to 500 at Task65 time) this was created as a
    /// separate file and separate class (an independent class rather than a partial split like
    /// CombatFxSound. The public API is only the three methods Spawn/Update/DestroyAll, and the way
    /// it is called from MilitaryManager.OnMainVisualUpdate/Reset follows exactly the same pattern as
    /// CombatFx's Spawn/Update/DestroyAll).
    ///
    /// Thread boundary: all public methods of this class are main-thread only (same convention as
    /// CombatFx; NEVER call them from the sim thread (MilitaryManager.OnSimTick)).
    /// </summary>
    internal static class KillFx
    {
        /// <summary>Upper bound on simultaneously live effects. Kills are a lower-frequency event
        /// than the gunfire CombatFx handles, so this is more conservative than
        /// CombatFx.MaxLiveEffects (200) (a defensive cap, so GameObjects cannot grow without bound
        /// even during a mass annihilation).</summary>
        private const int MaxLiveEffects = 48;

        // Kills too far from the camera skip spawning entirely (same lightweight distance check as
        // CombatFx.SpawnOne).
        private const float MaxSpawnDistanceFromCamera = 2000f;

        // Fireball: several small spheres slightly offset to look like a "clump". Swells quickly, then shrinks.
        private const int FireballChunkCount = 3;
        private const float FireballDuration = 0.45f;
        private const float FireballPeakSize = 5.5f;
        private const float FireballChunkOffset = 1.2f;
        private const float FireballGrowFraction = 0.4f; // expand during the first 40%, vanish over the rest

        // Black smoke puff: starts slightly after the fireball, slowly swells and fades (stretches
        // the total lifetime to ~1.5s).
        private const float SmokeStartDelay = 0.15f;
        private const float SmokeDuration = 1.35f; // 0.15 + 1.35 = 1.5s total lifetime
        private const float SmokePeakSize = 7f;
        private const float SmokeGrowFraction = 0.3f;

        private static readonly Color FireballColor = new Color(1f, 0.75f, 0.25f, 1f);
        private static readonly Color SmokeColor = new Color(0.18f, 0.16f, 0.14f, 1f);

        private class Effect
        {
            public GameObject Root;
            public Transform[] FireballChunks;
            public Transform Smoke;
            public float Elapsed;
        }

        private static readonly List<Effect> _effects = new List<Effect>();

        /// <summary>Task84: playback intensity of the CS standard explosion. The magnitude argument
        /// to EffectManager.DispatchEffect. More modest than the Alien laser impact (0.7), matched to
        /// roughly the scale of the old small explosion.</summary>
        private const float CsExplosionMagnitude = 0.5f;

        /// <summary>Task84: upper bound on CS explosions dispatched per Spawn call (= per frame).
        /// Prevents particle overload during mass annihilation (same defensive-cap idea as
        /// MaxLiveEffects).</summary>
        private const int MaxCsDispatchPerFrame = 16;

        private static EffectInfo _csEffect;
        private static bool _csEffectResolveAttempted;

        private static Shader _shader;
        private static bool _shaderResolved;
        private static Material _fireballMaterial;
        private static Material _smokeMaterial;

        /// <summary>Spawns explosion effects from one tick's worth of KillEvents (main-thread only).
        /// Kills of infantry/drone soldiers and kills too far from the camera spawn nothing. Once
        /// MaxLiveEffects is reached, further kills are silently ignored (no exception, same policy as
        /// CombatFx.Spawn).</summary>
        public static void Spawn(List<KillEvent> kills)
        {
            if (kills == null || kills.Count == 0) return;

            try
            {
                Camera cam = Camera.main;
                Vector3? cameraPos = cam != null ? (Vector3?)cam.transform.position : null;

                int dispatched = 0;
                for (int i = 0; i < kills.Count; i++)
                {
                    KillEvent k = kills[i];
                    // Shared Task51/65 rule: no explosion for infantry/drone-soldier kills (same criterion as the kill sounds).
                    if (!CombatFx.IsVehicleDestructionCategory(k.Category)) continue;

                    Vector3 pos = new Vector3(k.Position.X, k.Position.Y, k.Position.Z);
                    if (cameraPos.HasValue)
                    {
                        float distSqr = (pos - cameraPos.Value).sqrMagnitude;
                        if (distSqr > MaxSpawnDistanceFromCamera * MaxSpawnDistanceFromCamera) continue;
                    }

                    // Task84: play the CS standard explosion if available (realistic particle
                    // explosion). Only in environments where it cannot be resolved is the old
                    // primitive-sphere fallback used.
                    if (TryDispatchCsExplosion(pos))
                    {
                        if (++dispatched >= MaxCsDispatchPerFrame) break;
                        continue;
                    }

                    if (_effects.Count >= MaxLiveEffects) break;
                    SpawnOne(pos);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.Spawn error: " + e);
            }
        }

        /// <summary>Advances all live effects by real time (realDeltaTime) (main-thread only).
        /// Destroys effects whose combined fireball+smoke lifetime (SmokeStartDelay+SmokeDuration)
        /// has elapsed.</summary>
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
                    StepFireball(fx);
                    StepSmoke(fx);

                    if (fx.Elapsed >= SmokeStartDelay + SmokeDuration)
                    {
                        UnityEngine.Object.Destroy(fx.Root);
                        _effects.RemoveAt(i);
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.Update error: " + e);
            }
        }

        /// <summary>Destroys all live effects (on level unload, main-thread only, called from
        /// MilitaryManager.Reset). Cached materials are not GameObjects and therefore are not
        /// destroyed (same treatment as CombatFx.DestroyAll; they can be reused in the next
        /// session).</summary>
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
                ModConfig.LogError("KillFx.DestroyAll error: " + e);
            }
            finally
            {
                _effects.Clear();
                // Task84: EffectInfo is a prefab reference and after a level reload it can become a
                // destroyed Unity object (the ==null fake-null self-healing does not work on static
                // caches, lesson of cs-static-unity-object-cache). Force re-resolution in the next level.
                _csEffect = null;
                _csEffectResolveAttempted = false;
            }
        }

        /// <summary>Task84: plays the CS standard explosion effect at the kill position. Returns
        /// false if it cannot be resolved, in which case the caller uses the old fallback explosion.
        /// If the dispatch itself throws, CS explosions are given up for the rest of this session and
        /// the fallback takes over (prevents a barrage of per-frame error logs).</summary>
        private static bool TryDispatchCsExplosion(Vector3 pos)
        {
            return TryDispatchCsExplosion(pos, CsExplosionMagnitude);
        }

        /// <summary>Task87: magnitude-specifying overload (reused by BombFx's impact explosion with a
        /// smaller multiplier). Effect resolution and self-disabling on error share the Task84
        /// implementation as-is.</summary>
        internal static bool TryDispatchCsExplosion(Vector3 pos, float magnitude)
        {
            EffectInfo effect = ResolveCsEffect();
            if (effect == null) return false;

            try
            {
                var spawnArea = new EffectInfo.SpawnArea(pos, Vector3.up, 0f);
                Singleton<EffectManager>.instance.DispatchEffect(
                    effect, default(InstanceID), spawnArea, Vector3.zero, 0f, magnitude,
                    Singleton<VehicleManager>.instance.m_audioGroup);
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.TryDispatchCsExplosion error (falling back to simple effect): " + e);
                _csEffect = null; // fallback for the rest of this session (_csEffectResolveAttempted stays set)
                return false;
            }
        }

        /// <summary>Resolves the EffectInfo of the CS standard explosion (same resolution order as
        /// AlienInvasion Effects.ResolveImpactEffect: DisasterProperties.m_mediumExplosion -> meteor
        /// impact effect). Attempted once per process; the result (including failure) is cached.
        /// Because EffectInfo is a prefab reference that can become invalid on level reload, the cache
        /// is discarded in DestroyAll (level unload) and re-resolved in the next level
        /// (to avoid the fake-null problem of [[cs-static-unity-object-cache]], the static cache is
        /// never carried across levels).</summary>
        private static EffectInfo ResolveCsEffect()
        {
            if (_csEffectResolveAttempted) return _csEffect;
            _csEffectResolveAttempted = true;

            try
            {
                DisasterProperties dp = Singleton<DisasterManager>.instance.m_properties;
                if (dp != null && dp.m_mediumExplosion != null)
                {
                    _csEffect = dp.m_mediumExplosion;
                    ModConfig.Log("KillFx: using DisasterProperties.m_mediumExplosion for kill explosions.");
                    return _csEffect;
                }
            }
            catch (Exception)
            {
                // fall through to the fallback
            }

            try
            {
                int count = PrefabCollection<VehicleInfo>.LoadedCount();
                for (int i = 0; i < count; i++)
                {
                    VehicleInfo info = PrefabCollection<VehicleInfo>.GetLoaded((uint)i);
                    if (info == null) continue;
                    MeteorAI ai = info.m_vehicleAI as MeteorAI;
                    if (ai != null && ai.m_impactEffect != null)
                    {
                        _csEffect = ai.m_impactEffect;
                        ModConfig.Log("KillFx: using meteor impact effect for kill explosions.");
                        return _csEffect;
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.ResolveCsEffect error: " + e);
            }

            ModConfig.Log("KillFx: no CS explosion effect available; using simple fallback explosions.");
            _csEffect = null;
            return null;
        }

        private static void SpawnOne(Vector3 pos)
        {
            try
            {
                var go = new GameObject("CSWarfrontKillFx");
                go.transform.position = pos;

                var chunks = new Transform[FireballChunkCount];
                for (int c = 0; c < FireballChunkCount; c++)
                {
                    // Slight visual-only scatter (determinism is a Core-side concern and is not needed for Game-layer visuals).
                    Vector3 offset = UnityEngine.Random.insideUnitSphere * FireballChunkOffset;
                    chunks[c] = CreateSmallSphere(go.transform, pos + offset, 0f, GetFireballMaterial());
                }

                Transform smoke = CreateSmallSphere(go.transform, pos, 0f, GetSmokeMaterial());

                _effects.Add(new Effect
                {
                    Root = go,
                    FireballChunks = chunks,
                    Smoke = smoke,
                    Elapsed = 0f
                });
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.SpawnOne error: " + e);
            }
        }

        /// <summary>Creates a small sphere with its Collider disabled so it does not interfere with
        /// the click-selection raycast (same role and same implementation as
        /// CombatFx.CreateSmallSphere. A local copy for the sake of an independent class rather than
        /// sharing via partial, but the body is intentionally identical).</summary>
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

        private static void StepFireball(Effect fx)
        {
            if (fx.FireballChunks == null) return;
            float t = FireballDuration > 0f ? Mathf.Clamp01(fx.Elapsed / FireballDuration) : 1f;
            // Swells rapidly in the first phase (0->peak), then shrinks to 0 (simple expand->vanish curve).
            float size = t < FireballGrowFraction
                ? Mathf.Lerp(0f, FireballPeakSize, t / FireballGrowFraction)
                : Mathf.Lerp(FireballPeakSize, 0f, (t - FireballGrowFraction) / (1f - FireballGrowFraction));

            for (int i = 0; i < fx.FireballChunks.Length; i++)
            {
                if (fx.FireballChunks[i] != null)
                    fx.FireballChunks[i].localScale = new Vector3(size, size, size);
            }
        }

        private static void StepSmoke(Effect fx)
        {
            if (fx.Smoke == null) return;
            float local = fx.Elapsed - SmokeStartDelay;
            if (local < 0f) { fx.Smoke.localScale = Vector3.zero; return; }

            float t = SmokeDuration > 0f ? Mathf.Clamp01(local / SmokeDuration) : 1f;
            // Slowly swells then shrinks (makes the black smoke appear to thin out and vanish; same simple representation as CombatFx.StepImpactPuff).
            float size = t < SmokeGrowFraction
                ? Mathf.Lerp(0f, SmokePeakSize, t / SmokeGrowFraction)
                : Mathf.Lerp(SmokePeakSize, 0f, (t - SmokeGrowFraction) / (1f - SmokeGrowFraction));
            fx.Smoke.localScale = new Vector3(size, size, size);
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
                    ModConfig.LogError("KillFx: failed to resolve shader (Standard/Legacy Shaders/Diffuse all failed), kill explosion effects will not render");
            }
            catch (Exception e)
            {
                ModConfig.LogError("KillFx.ResolveShader error: " + e);
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
                ModConfig.LogError("KillFx.BuildMaterial error: " + e);
                return null;
            }
        }

        private static Material GetFireballMaterial()
        {
            if (_fireballMaterial == null) _fireballMaterial = BuildMaterial(FireballColor);
            return _fireballMaterial;
        }

        private static Material GetSmokeMaterial()
        {
            if (_smokeMaterial == null) _smokeMaterial = BuildMaterial(SmokeColor);
            return _smokeMaterial;
        }
    }
}
