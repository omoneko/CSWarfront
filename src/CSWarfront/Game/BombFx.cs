using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Bomber bomb-drop motion (Task87, per the user request "I'd like bombers to have a motion of
    /// dropping bombs").
    ///
    /// For a bomber (TacticalBomber) ShotEvent, CombatFx.SpawnOne calls this class's SpawnDrop instead
    /// of a direct-fire tracer. The bomb falls from the release position (bomber model center height)
    /// to the impact point — constant velocity horizontally, accelerating vertically (a free-fall-like
    /// quadratic curve) — rotating its nose (+Z) toward the velocity direction. On impact it plays
    /// KillFx.TryDispatchCsExplosion (the standard CS explosion, same path as Task84) at a smaller
    /// magnitude than the kill explosion.
    ///
    /// The model is Models/Prop_Bomb.obj (via WarfrontModelProvider). As of 2026-07-31, models.blend
    /// has no bomb object yet, so a provisionally generated model is bundled — once the user adds
    /// something like "17_Bomb" to models.blend and reruns tools/export_builtin_obj.py, it gets
    /// swapped in. Environments where the model cannot be resolved substitute a primitive sphere
    /// (dark-colored).
    ///
    /// Thread boundary: all public methods are main thread only (same convention as CombatFx/KillFx).
    /// </summary>
    internal static class BombFx
    {
        private const int MaxLiveBombs = 40; // Defensive cap (same idea as KillFx.MaxLiveEffects)

        /// <summary>Fall time (real seconds). Calibrated to a visually trackable speed from cruise at
        /// altitude 120 down to the ground.</summary>
        private const float FallDuration = 1.1f;

        /// <summary>Impact explosion magnitude. More restrained than the kill explosion
        /// (KillFx.CsExplosionMagnitude=0.5) (it fires once per bomb, so this avoids excess during
        /// sustained bombing runs).</summary>
        private const float ImpactExplosionMagnitude = 0.4f;

        private const string ModelName = "Prop_Bomb";

        private class Bomb
        {
            public GameObject Root;
            public Vector3 From;
            public Vector3 To;
            public float Elapsed;
        }

        private static readonly List<Bomb> _bombs = new List<Bomb>();

        private static Mesh _mesh;
        private static Material[] _materials;
        private static bool _modelResolveAttempted;
        private static Material _fallbackMaterial;

        /// <summary>Drops one bomb (main thread only; called from the bomber branch of
        /// CombatFx.SpawnOne). from=release position (including bomber model center height), to=impact point.</summary>
        public static void SpawnDrop(Vector3 from, Vector3 to)
        {
            try
            {
                if (_bombs.Count >= MaxLiveBombs) return;

                GameObject go = CreateBombObject();
                if (go == null) return;
                go.transform.position = from;

                _bombs.Add(new Bomb { Root = go, From = from, To = to, Elapsed = 0f });
            }
            catch (Exception e)
            {
                ModConfig.LogError("BombFx.SpawnDrop error: " + e);
            }
        }

        /// <summary>Advances all falling bombs in real time (main thread only). On impact, plays the
        /// explosion and destroys the bomb.</summary>
        public static void Update(float realDeltaTime)
        {
            if (_bombs.Count == 0) return;

            try
            {
                // Task89: camera position for the impact sound (bombing sound). Fetched only once per
                // frame (same pattern as CombatFxSound.SpawnKillSounds).
                Camera cam = Camera.main;
                Vector3? cameraPos = cam != null ? (Vector3?)cam.transform.position : null;

                for (int i = _bombs.Count - 1; i >= 0; i--)
                {
                    Bomb b = _bombs[i];
                    if (b.Root == null) { _bombs.RemoveAt(i); continue; }

                    b.Elapsed += realDeltaTime;
                    float t = Mathf.Clamp01(b.Elapsed / FallDuration);

                    // Constant velocity horizontally, accelerating vertically (t^2) = drifts forward
                    // right after release, and the nose pitches down the further it falls.
                    float x = Mathf.Lerp(b.From.x, b.To.x, t);
                    float z = Mathf.Lerp(b.From.z, b.To.z, t);
                    float y = Mathf.Lerp(b.From.y, b.To.y, t * t);
                    Vector3 pos = new Vector3(x, y, z);

                    // Point the nose (+Z) toward the velocity direction (analytic derivative:
                    // horizontal=constant, vertical=proportional to 2t).
                    Vector3 vel = new Vector3(
                        (b.To.x - b.From.x) / FallDuration,
                        (b.To.y - b.From.y) * 2f * t / FallDuration,
                        (b.To.z - b.From.z) / FallDuration);
                    b.Root.transform.position = pos;
                    if (vel.sqrMagnitude > 1e-4f)
                        b.Root.transform.rotation = Quaternion.LookRotation(vel);

                    if (t >= 1f)
                    {
                        KillFx.TryDispatchCsExplosion(b.To, ImpactExplosionMagnitude);
                        // Task89 (user request "bombing sound from the bombers"): play an explosion
                        // sound at the moment of impact (reuses the same vehicle_destroyed.wav as the
                        // kill sound, with the same concurrent-playback / distance-attenuation management).
                        Audio.WarfrontSoundPlayer.PlayKill(b.To, cameraPos);
                        UnityEngine.Object.Destroy(b.Root);
                        _bombs.RemoveAt(i);
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BombFx.Update error: " + e);
            }
        }

        /// <summary>On level unload (MilitaryManager.Reset, main thread only).
        /// Also discards the model cache (WarfrontModelProvider caches the Mesh/Material across
        /// levels, but re-fetching our references in the next level is the safer side).</summary>
        public static void DestroyAll()
        {
            try
            {
                for (int i = 0; i < _bombs.Count; i++)
                {
                    if (_bombs[i] != null && _bombs[i].Root != null)
                        UnityEngine.Object.Destroy(_bombs[i].Root);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BombFx.DestroyAll error: " + e);
            }
            finally
            {
                _bombs.Clear();
                _mesh = null;
                _materials = null;
                _modelResolveAttempted = false;
            }
        }

        private static GameObject CreateBombObject()
        {
            ResolveModel();

            if (_mesh != null)
            {
                var go = new GameObject("CSWarfrontBomb");
                MeshFilter mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = _mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                if (_materials != null) mr.sharedMaterials = _materials;
                return go;
            }

            // Fallback: a small dark sphere (the drop is still visible even in environments where
            // the model is unresolved).
            GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Collider col = fallback.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col); // Do not interfere with click hit-testing
            fallback.name = "CSWarfrontBombFallback";
            fallback.transform.localScale = new Vector3(1.6f, 1.6f, 2.4f);
            Renderer r = fallback.GetComponent<Renderer>();
            if (r != null && GetFallbackMaterial() != null) r.sharedMaterial = GetFallbackMaterial();
            return fallback;
        }

        private static void ResolveModel()
        {
            if (_modelResolveAttempted) return;
            _modelResolveAttempted = true;

            Mesh mesh;
            Material[] materials;
            if (Models.WarfrontModelProvider.TryGetModel(ModelName, out mesh, out materials))
            {
                _mesh = mesh;
                _materials = materials;
                ModConfig.Log("BombFx: bomb model resolved (" + ModelName + ").");
            }
            else
            {
                ModConfig.Log("BombFx: bomb model not found (" + ModelName + "); using fallback sphere.");
            }
        }

        private static Material GetFallbackMaterial()
        {
            if (_fallbackMaterial != null) return _fallbackMaterial;
            try
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                if (shader == null) shader = Shader.Find("Diffuse");
                if (shader == null) return null;
                _fallbackMaterial = new Material(shader);
                _fallbackMaterial.color = new Color(0.2f, 0.21f, 0.18f, 1f);
            }
            catch (Exception e)
            {
                ModConfig.LogError("BombFx.GetFallbackMaterial error: " + e);
            }
            return _fallbackMaterial;
        }
    }
}
