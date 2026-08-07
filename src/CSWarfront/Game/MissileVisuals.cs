using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>Main-thread-only snapshot. Only the minimum information needed to determine the
    /// "appearance" of one missile in flight (Task63). Same design philosophy as UnitVisualState.</summary>
    public struct MissileVisualState
    {
        public uint Id;
        public byte FactionId;
        public Vector3 From;
        public Vector3 To;
        /// <summary>Flight progress (0..1, copy of Core.MissileInFlight.Progress).</summary>
        public float Progress;
    }

    /// <summary>
    /// Ballistic missile visuals (Task63, chunk 6 MVP Part2). Same "declarative reconcile" pattern as
    /// UnitVisuals.Sync: the Core-side abstraction that follows the straight From->To line by Progress
    /// is converted, for display only, into a high-altitude parabolic arc (apex height =
    /// min(800, distance*0.5)). The missile is represented by a plain Unity GameObject (a small
    /// elongated box) that borrows no CS vehicle/building at all, plus a TrailRenderer (warm-colored
    /// tracer, following CombatFx's "Standard shader / fixed color / sharedMaterial" policy).
    ///
    /// Impact/interception effects (flash/explosion + sound) live in another file of the same partial
    /// class (MissileVisualsFx.cs).
    ///
    /// Thread boundary: every public method of this class is "main thread only"
    /// (same convention as UnitVisuals). Never call them from the sim thread
    /// (MilitaryManager.OnSimTick).
    /// </summary>
    internal static partial class MissileVisuals
    {
        private class Entry
        {
            public GameObject GameObject;
        }

        // Visual ballistic arc (Core.WorldPos is only the straight From->To line, an abstraction
        // interpolated by Progress).
        private const float ApexHeightCap = 800f;
        private const float ApexHeightRatio = 0.5f;

        // Dimensions of the "small elongated GameObject" (default size sufficiently small relative to
        // the world distance from launch point to target).
        private const float BodyWidth = 2.5f;
        private const float BodyLength = 12f;

        private const float TrailTime = 1.0f;
        private const float TrailStartWidth = 1.4f;
        private const float TrailEndWidth = 0.1f;

        // Small look-ahead amount (on the arc parameter t) used to derive the attitude (direction of travel).
        private const float VelocitySampleDeltaT = 0.01f;

        private static readonly Dictionary<uint, Entry> _visuals = new Dictionary<uint, Entry>();
        private static readonly HashSet<uint> _seenIds = new HashSet<uint>();
        private static readonly List<uint> _staleIds = new List<uint>();

        private static Shader _shader;
        private static bool _shaderResolved;
        private static Material _bodyMaterial;
        private static Material _trailMaterial;

        // Warm tracer colors (same family as CombatFx's Gunfire/DirectFire colors. Not tinted with the
        // faction color = regardless of the launching faction, it is instantly recognizable as "a
        // ballistic missile in flight" itself).
        private static readonly Color BodyColor = new Color(0.85f, 0.35f, 0.15f);
        private static readonly Color TrailColor = new Color(1f, 0.75f, 0.35f);

        public static int Count { get { return _visuals.Count; } }

        /// <summary>Declaratively applies create/move/destroy based on the snapshot (main thread only).
        /// Ids absent from the snapshot (removed by impact, interception, save-load, etc.) are destroyed here.</summary>
        public static void Sync(List<MissileVisualState> snapshot)
        {
            if (snapshot == null) return;

            _seenIds.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                MissileVisualState s = snapshot[i];
                _seenIds.Add(s.Id);

                try
                {
                    Entry entry;
                    if (!_visuals.TryGetValue(s.Id, out entry) || entry.GameObject == null)
                    {
                        entry = Create(s);
                        if (entry == null) continue; // Creation failure is already logged. Retry on the next Sync.
                        _visuals[s.Id] = entry;
                    }
                    UpdatePose(entry, s);
                }
                catch (Exception e)
                {
                    ModConfig.LogError("MissileVisuals.Sync: failed to update missile " + s.Id + ": " + e);
                }
            }

            _staleIds.Clear();
            foreach (var kv in _visuals)
            {
                if (!_seenIds.Contains(kv.Key)) _staleIds.Add(kv.Key);
            }
            for (int i = 0; i < _staleIds.Count; i++) Destroy(_staleIds[i]);
        }

        /// <summary>Destroys all tracked visuals (on level unload, main thread only).</summary>
        public static void DestroyAll()
        {
            try
            {
                foreach (var kv in _visuals)
                {
                    if (kv.Value != null && kv.Value.GameObject != null)
                        UnityEngine.Object.Destroy(kv.Value.GameObject);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.DestroyAll error: " + e);
            }
            finally
            {
                _visuals.Clear();
            }
        }

        private static Entry Create(MissileVisualState s)
        {
            try
            {
                // Task90: if the ballistic missile model (Models/Prop_BallisticMissile.obj, +Z=nose)
                // is available, use it. Until the user's model is added to models.blend, a provisional
                // model (reused from MissileDisaster's IncomingWarhead) is bundled. Only environments
                // where it cannot be resolved fall back to the traditional elongated box.
                GameObject go;
                Mesh modelMesh;
                Material[] modelMaterials;
                if (Models.WarfrontModelProvider.TryGetModel("Prop_BallisticMissile", out modelMesh, out modelMaterials))
                {
                    go = new GameObject();
                    MeshFilter mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = modelMesh;
                    MeshRenderer mr = go.AddComponent<MeshRenderer>();
                    if (modelMaterials != null) mr.sharedMaterials = modelMaterials;
                }
                else
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Collider col = go.GetComponent<Collider>();
                    if (col != null) UnityEngine.Object.Destroy(col); // Do not interfere with selection/raycasts
                    go.transform.localScale = new Vector3(BodyWidth, BodyWidth, BodyLength);
                    Renderer renderer = go.GetComponent<Renderer>();
                    if (renderer != null) renderer.sharedMaterial = GetBodyMaterial();
                }

                go.name = "CSWarfrontMissile_" + s.Id;

                TrailRenderer trail = go.AddComponent<TrailRenderer>();
                trail.time = TrailTime;
                trail.startWidth = TrailStartWidth;
                trail.endWidth = TrailEndWidth;
                trail.material = GetTrailMaterial();
                trail.minVertexDistance = 1f;
                trail.autodestruct = false;

                return new Entry { GameObject = go };
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.Create: missile " + s.Id + " error: " + e);
                return null;
            }
        }

        /// <summary>Interpolates From->To by Progress, while lifting the position onto a parabolic arc
        /// (apex height = min(ApexHeightCap, horizontal distance*ApexHeightRatio)) for display only.
        /// The attitude is oriented along the direction of travel (velocity vector) derived from a
        /// small look-ahead.</summary>
        private static void UpdatePose(Entry entry, MissileVisualState s)
        {
            if (entry.GameObject == null) return;

            float t = Mathf.Clamp01(s.Progress);
            float apex = ApexHeight(s.From, s.To);

            Vector3 pos = ArcPositionAt(s.From, s.To, apex, t);
            entry.GameObject.transform.position = pos;

            float t2 = Mathf.Clamp01(t + VelocitySampleDeltaT);
            if (t2 > t)
            {
                Vector3 ahead = ArcPositionAt(s.From, s.To, apex, t2);
                Vector3 vel = ahead - pos;
                if (vel.sqrMagnitude > 1e-6f) entry.GameObject.transform.rotation = Quaternion.LookRotation(vel);
            }
        }

        private static float ApexHeight(Vector3 from, Vector3 to)
        {
            float dx = to.x - from.x, dz = to.z - from.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            return Mathf.Min(ApexHeightCap, dist * ApexHeightRatio);
        }

        private static Vector3 ArcPositionAt(Vector3 from, Vector3 to, float apex, float t)
        {
            Vector3 pos = Vector3.Lerp(from, to, t);
            pos.y += 4f * apex * t * (1f - t); // Same standard parabolic interpolation as CombatFx.ArcPositionAt
            return pos;
        }

        private static void Destroy(uint id)
        {
            try
            {
                Entry entry;
                if (_visuals.TryGetValue(id, out entry))
                {
                    if (entry != null && entry.GameObject != null) UnityEngine.Object.Destroy(entry.GameObject);
                    _visuals.Remove(id);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.Destroy: missile " + id + " error: " + e);
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
                    ModConfig.LogError("MissileVisuals: failed to resolve shader, missiles will not render");
            }
            catch (Exception e)
            {
                ModConfig.LogError("MissileVisuals.ResolveShader error: " + e);
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
                ModConfig.LogError("MissileVisuals.BuildMaterial error: " + e);
                return null;
            }
        }

        private static Material GetBodyMaterial()
        {
            if (_bodyMaterial == null) _bodyMaterial = BuildMaterial(BodyColor);
            return _bodyMaterial;
        }

        private static Material GetTrailMaterial()
        {
            if (_trailMaterial == null) _trailMaterial = BuildMaterial(TrailColor);
            return _trailMaterial;
        }
    }
}
