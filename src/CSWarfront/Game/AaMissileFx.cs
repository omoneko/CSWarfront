using System;
using System.Collections.Generic;
using CSWarfront.Game.Effects;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Anti-air missile flight effects (Task90, per the user requests "anti-air weapons should fire
    /// anti-air missiles at fighters and bombers" and "model the interception animation on the
    /// MissileDisaster MOD").
    /// A port of the homing-projectile pattern from MissileDisaster.Game.InterceptorProjectile:
    ///  - A model projectile (Models/Prop_Interceptor.obj, +Z=nose) flies from the launch position
    ///    toward the target aircraft's current position (via UnitVisuals, homing every frame). With a
    ///    SamTrail (nozzle flame + exhaust smoke).
    ///  - Hit round (Missed=false): SamBurstFx.PlayFlash (flash) on arrival. Damage is already settled
    ///    on the Core side.
    ///  - Miss round (Missed=true): aims at a point veering off to the side of the target, and plays
    ///    PlayFizzle (dud smoke) on arrival.
    ///  - In both cases, once on approach (within FlareTriggerDistance), the target aircraft releases
    ///    flares (SamBurstFx.PlayFlares) and takes an evasive maneuver (UnitVisuals.NotifyEvade, a
    ///    visual jink). The hit/miss outcome itself is already settled by the Core hit roll — the
    ///    flares and evasion are theatrics explaining "why it missed".
    ///
    /// Thread boundary: all public methods are main thread only (same convention as CombatFx/BombFx).
    /// </summary>
    internal static class AaMissileFx
    {
        private const int MaxLive = 32;
        private const float Speed = 260f;               // m/sec (real time). Covers the 120-190m range in 0.5-0.8 seconds
        private const float CatchRadius = 10f;          // Arrival-detection distance
        private const float MaxFlightSeconds = 4f;      // Safeguard when homing becomes impossible
        private const float MissOffsetDistance = 30f;   // Distance a miss round veers off to the side of the target
        private const float FlareTriggerDistance = 70f; // Approach distance at which flare release / evasion starts
        private const string ModelName = "Prop_Interceptor";

        private class Sam
        {
            public GameObject Root;
            public uint TargetId;
            public Vector3 AimPos;      // Last known point when the target disappears (or the veer-off point of a miss round)
            public Vector3 MissOffset;  // Miss rounds only: offset added to the target position
            public bool Missed;
            public bool FlareDone;
            public float Elapsed;
        }

        private static readonly List<Sam> _live = new List<Sam>();

        private static Mesh _mesh;
        private static Material[] _materials;
        private static bool _modelResolveAttempted;
        private static Material _fallbackMaterial;

        /// <summary>Launches one round (from the SamMissile branch of CombatFx.SpawnOne.
        /// from=launch position (including muzzle height), to=target position at launch time,
        /// targetId=target aircraft, missed=miss flag already settled on the Core side).</summary>
        public static void Spawn(Vector3 from, Vector3 to, uint targetId, bool missed)
        {
            try
            {
                if (_live.Count >= MaxLive) return;

                GameObject go = CreateProjectile();
                if (go == null) return;
                go.transform.position = from;

                var sam = new Sam
                {
                    Root = go,
                    TargetId = targetId,
                    AimPos = to,
                    Missed = missed,
                    FlareDone = false,
                    Elapsed = 0f
                };
                if (missed)
                {
                    // Offset veering sideways relative to the direction of travel (deterministic:
                    // left/right decided by the parity of targetId).
                    Vector3 dir = (to - from);
                    dir.y = 0f;
                    Vector3 side = dir.sqrMagnitude > 1e-4f
                        ? Vector3.Cross(dir.normalized, Vector3.up)
                        : Vector3.right;
                    sam.MissOffset = side * ((targetId & 1) == 0 ? MissOffsetDistance : -MissOffsetDistance)
                        + Vector3.up * (MissOffsetDistance * 0.4f);
                }

                SamTrail.Attach(go);
                AimAt(go, to);
                _live.Add(sam);
            }
            catch (Exception e)
            {
                ModConfig.LogError("AaMissileFx.Spawn error: " + e);
            }
        }

        /// <summary>Advances all rounds in flight in real time (main thread only).</summary>
        public static void Update(float realDeltaTime)
        {
            if (_live.Count == 0) return;

            try
            {
                for (int i = _live.Count - 1; i >= 0; i--)
                {
                    Sam sam = _live[i];
                    if (sam.Root == null) { _live.RemoveAt(i); continue; }

                    sam.Elapsed += realDeltaTime;

                    // Home in on the target's current position (or the last known point if the visual
                    // has disappeared). Miss rounds get the offset added.
                    Vector3 targetPos;
                    if (UnitVisuals.TryGetPosition(sam.TargetId, out targetPos))
                        sam.AimPos = sam.Missed ? targetPos + sam.MissOffset : targetPos;

                    Vector3 pos = sam.Root.transform.position;
                    Vector3 delta = sam.AimPos - pos;
                    float dist = delta.magnitude;
                    float step = Speed * realDeltaTime;

                    // On approach, the target aircraft scatters flares once and enters an evasive
                    // maneuver (the outcome is already settled by Core).
                    if (!sam.FlareDone && dist <= FlareTriggerDistance)
                    {
                        sam.FlareDone = true;
                        Vector3 planePos;
                        if (UnitVisuals.TryGetPosition(sam.TargetId, out planePos))
                        {
                            SamBurstFx.PlayFlares(planePos);
                            UnitVisuals.NotifyEvade(sam.TargetId);
                        }
                    }

                    bool reached = dist <= Mathf.Max(step, CatchRadius);
                    bool timedOut = sam.Elapsed >= MaxFlightSeconds;
                    if (reached || timedOut)
                    {
                        Vector3 point = reached ? sam.AimPos : pos;
                        if (sam.Missed || timedOut) SamBurstFx.PlayFizzle(point);
                        else SamBurstFx.PlayFlash(point);

                        SamTrail.DetachAndLinger(sam.Root);
                        UnityEngine.Object.Destroy(sam.Root);
                        _live.RemoveAt(i);
                        continue;
                    }

                    Vector3 next = pos + delta / dist * step;
                    sam.Root.transform.position = next;
                    AimAt(sam.Root, sam.AimPos);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AaMissileFx.Update error: " + e);
            }
        }

        /// <summary>On level unload (MilitaryManager.Reset, main thread only).</summary>
        public static void DestroyAll()
        {
            try
            {
                for (int i = 0; i < _live.Count; i++)
                {
                    if (_live[i] != null && _live[i].Root != null)
                        UnityEngine.Object.Destroy(_live[i].Root);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AaMissileFx.DestroyAll error: " + e);
            }
            finally
            {
                _live.Clear();
                _mesh = null;
                _materials = null;
                _modelResolveAttempted = false;
            }
        }

        private static void AimAt(GameObject go, Vector3 aim)
        {
            Vector3 dir = aim - go.transform.position;
            if (dir.sqrMagnitude > 1e-6f) go.transform.rotation = Quaternion.LookRotation(dir);
        }

        private static GameObject CreateProjectile()
        {
            ResolveModel();

            if (_mesh != null)
            {
                var go = new GameObject("CSWarfrontSam");
                MeshFilter mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = _mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                if (_materials != null) mr.sharedMaterials = _materials;
                return go;
            }

            // Fallback: an elongated white box (same role as InterceptorProjectile's fallback sphere).
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Collider col = box.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
            box.name = "CSWarfrontSamFallback";
            box.transform.localScale = new Vector3(0.7f, 0.7f, 3.5f);
            Renderer r = box.GetComponent<Renderer>();
            if (r != null && GetFallbackMaterial() != null) r.sharedMaterial = GetFallbackMaterial();
            return box;
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
                ModConfig.Log("AaMissileFx: interceptor model resolved (" + ModelName + ").");
            }
            else
            {
                ModConfig.Log("AaMissileFx: interceptor model not found (" + ModelName + "); using fallback box.");
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
                _fallbackMaterial.color = new Color(0.9f, 0.9f, 0.88f, 1f);
            }
            catch (Exception e)
            {
                ModConfig.LogError("AaMissileFx.GetFallbackMaterial error: " + e);
            }
            return _fallbackMaterial;
        }
    }
}
