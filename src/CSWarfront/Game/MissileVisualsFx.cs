using System;
using System.Collections.Generic;
using CSWarfront.Core;
using CSWarfront.Game.Audio;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Partial class separating out only the impact/interception effects (flash/explosion + sound,
    /// Task63) from MissileVisuals (for the readability of the MissileVisuals.cs side; same splitting
    /// policy as UnitVisuals/UnitVisualsFactionIcon).
    /// Receives a snapshot of WarState.RecentImpacts (Core.MissileImpactEvent) and emits: an impact
    /// (larger explosion + an explosion sound close to the existing bombardment/kill sounds) if
    /// Intercepted=false, or an interception (small flash only, no damage effects) if Intercepted=true.
    /// All methods are main thread only (because they call Unity APIs).
    /// </summary>
    internal static partial class MissileVisuals
    {
        private class FxEntry
        {
            public GameObject Root;
            public float Elapsed;
            public float Duration;
            public float InitialSize;
        }

        private const float ImpactFlashDuration = 0.6f;
        private const float ImpactFlashSize = 14f;
        private const float InterceptFlashDuration = 0.3f;
        private const float InterceptFlashSize = 5f;

        private static readonly Color ImpactColor = new Color(1f, 0.55f, 0.1f);
        private static readonly Color InterceptColor = new Color(1f, 0.95f, 0.75f);
        private static Material _impactMaterial;
        private static Material _interceptMaterial;

        private static readonly List<FxEntry> _fx = new List<FxEntry>();

        /// <summary>Generates impact/interception effects from one tick's worth of MissileImpactEvents
        /// (main thread only). Same "fetch the camera position once, then process all entries"
        /// pattern as CombatFx.Spawn.</summary>
        public static void HandleImpacts(List<MissileImpactEvent> events)
        {
            if (events == null || events.Count == 0) return;

            try
            {
                Camera cam = Camera.main;
                Vector3? cameraPos = cam != null ? (Vector3?)cam.transform.position : null;

                for (int i = 0; i < events.Count; i++)
                {
                    MissileImpactEvent e = events[i];
                    Vector3 pos = new Vector3(e.Position.X, e.Position.Y, e.Position.Z);

                    if (e.Intercepted)
                    {
                        SpawnFlash(pos, InterceptFlashSize, InterceptFlashDuration, GetInterceptMaterial());
                        WarfrontSoundPlayer.PlayShot(WarfrontSounds.AaMissile, pos, cameraPos);
                    }
                    else
                    {
                        SpawnFlash(pos, ImpactFlashSize, ImpactFlashDuration, GetImpactMaterial());
                        WarfrontSoundPlayer.PlayKill(pos, cameraPos); // Reuse the existing "explosion"-equivalent sound (vehicle_destroyed)
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.HandleImpacts error: " + e);
            }
        }

        /// <summary>Advances the live effects (flashes) in real time and destroys those whose lifetime
        /// has expired (main thread only). Call every frame from MilitaryManager.OnMainVisualUpdate.</summary>
        public static void UpdateFx(float realDeltaTime)
        {
            if (_fx.Count == 0) return;

            try
            {
                for (int i = _fx.Count - 1; i >= 0; i--)
                {
                    FxEntry fx = _fx[i];
                    if (fx.Root == null) { _fx.RemoveAt(i); continue; }

                    fx.Elapsed += realDeltaTime;
                    float t = fx.Duration > 0f ? Mathf.Clamp01(fx.Elapsed / fx.Duration) : 1f;
                    float size = Mathf.Lerp(fx.InitialSize, 0f, t);
                    fx.Root.transform.localScale = new Vector3(size, size, size);

                    if (fx.Elapsed >= fx.Duration)
                    {
                        UnityEngine.Object.Destroy(fx.Root);
                        _fx.RemoveAt(i);
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.UpdateFx error: " + e);
            }
        }

        /// <summary>Destroys the live effects (on level unload, main thread only).</summary>
        public static void DestroyAllFx()
        {
            try
            {
                for (int i = 0; i < _fx.Count; i++)
                {
                    if (_fx[i] != null && _fx[i].Root != null) UnityEngine.Object.Destroy(_fx[i].Root);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.DestroyAllFx error: " + e);
            }
            finally
            {
                _fx.Clear();
            }
        }

        private static void SpawnFlash(Vector3 position, float size, float duration, Material material)
        {
            if (material == null) return;
            try
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Collider col = go.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col);

                go.name = "CSWarfrontMissileFx";
                go.transform.position = position;
                go.transform.localScale = new Vector3(size, size, size);

                Renderer renderer = go.GetComponent<Renderer>();
                if (renderer != null) renderer.sharedMaterial = material;

                _fx.Add(new FxEntry { Root = go, Elapsed = 0f, Duration = duration, InitialSize = size });
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.SpawnFlash error: " + e);
            }
        }

        private static Material GetImpactMaterial()
        {
            if (_impactMaterial == null) _impactMaterial = BuildMaterial(ImpactColor);
            return _impactMaterial;
        }

        private static Material GetInterceptMaterial()
        {
            if (_interceptMaterial == null) _interceptMaterial = BuildMaterial(InterceptColor);
            return _interceptMaterial;
        }
    }
}
