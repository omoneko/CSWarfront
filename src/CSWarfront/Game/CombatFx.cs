using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Muzzle-fire effects (Task42): renders the ShotEvents received from WarState.RecentShots as
    /// short-lived Unity GameObjects. Given that this is a strategic view, the presentation is kept
    /// lightweight — just enough to "see at a glance what is happening" — rather than the flashiness
    /// of a shooter game.
    ///
    /// Thread boundary: all public methods on this class are "main thread only"
    /// (new GameObject / AddComponent / Destroy / transform writes are Unity main-thread constraints).
    /// Never call from the sim thread (MilitaryManager.OnSimTick) (same convention as UnitVisuals).
    ///
    /// No CS-derived materials are borrowed at all. For the same reason as UnitMaterialFactory (CS
    /// vehicle shaders require per-instance data provided by CS's own renderers, and assigning them
    /// to a plain Renderer renders invisible/black), we create and share our own standard-shader
    /// Materials with fixed faction-independent colors (sharedMaterial, no per-instance copies).
    /// Not tinting with faction colors means tracers read as "the gunfire itself" and cannot be
    /// misread as side markers (a requirement).
    /// </summary>
    internal static partial class CombatFx
    {
        /// <summary>Cap on how many effects may be alive simultaneously (Task42). A defensive limit so
        /// GameObjects cannot grow without bound in large melees. Independent of the ShotEvent-side cap
        /// (WarState.MaxRecentShotsPerTick), this one limits the total "currently alive" count.
        /// Task43: with gunfire changing from 1 round to a 3-round burst, the number of effects
        /// temporarily alive from the same shot can grow up to nearly 3x, so this was raised from
        /// 120 to 200 (keeping roughly the same headroom as before in large melees).</summary>
        private const int MaxLiveEffects = 200;

        // Shots too far from the camera skip spawning entirely (only a lightweight distance check).
        private const float MaxSpawnDistanceFromCamera = 2000f;

        // Task43: defaults for lifting the firing/impact positions up to model-center height (fallback
        // when UnitVisuals.TryGetMuzzleOffset finds nothing). Used when AttackerId/TargetId is 0 (a base
        // etc. — not a logical unit) or when the visual has not been created yet. Bases are buildings,
        // so their height is set higher than the default firing height.
        private const float DefaultMuzzleHeight = 3f;
        private const float BaseTargetHeight = 8f;

        // Task108 (user report "the artillery post's firing height is misaligned with the model"):
        // fortification structures (bunker / artillery post) are not logical units, so no muzzle offset
        // can be looked up (ShotEvent.AttackerId==0) and they fell back to DefaultMuzzleHeight (3m)
        // above. Replace that with heights measured from the bundled models:
        //   Models/Fort_ArtilleryPost.obj … total height 3.6m. The barrel area of the central howitzer
        //   is roughly 2.2–2.9m
        //   Models/Fort_Bunker.obj        … total height 5.3m (including the antenna). The embrasures
        //   on the body are roughly 1.1–1.6m
        // Fire from a structure maps one-to-one from unit category to structure type (artillery post =
        // Artillery / bunker = Infantry; see attackerCategory in Core/FortCombatStep.cs). Adjust these
        // two values if differently sized assets are designated.
        private const float ArtilleryPostMuzzleHeight = 2.4f;
        private const float BunkerMuzzleHeight = 1.3f;

        // Gunfire (Infantry/MechInfantry/Apc/DroneInfantry/AntiAir): thin short tracer + small muzzle flash.
        // Task43: to match the 1-round -> 3-round-burst change, the per-round display time was slightly
        // shortened from 0.08s to 0.06s (keeping it shorter than the 0.07s burst gap so each round fully
        // fades before the next one fires).
        // Task108 (user report "artillery light trails are too thick; make them more realistic"): tracer
        // widths and muzzle-flash sizes were narrowed toward real-world proportions (gunfire 0.15→0.08 /
        // direct fire 0.35→0.15 / indirect fire 0.4→0.15, flashes shrunk likewise). This range is a
        // practical sweet spot as the lower bound where they do not vanish entirely at long distances.
        private const float GunfireTracerDuration = 0.06f;
        private const float GunfireTracerWidth = 0.08f;
        private const float GunfireFlashSize = 0.7f;

        // Task43: one gunfire shot = a 3-round burst. The first round fires immediately; rounds 2/3 are
        // delayed on a real-time basis at GunfireBurstRoundGap intervals (_pendingBursts, advanced
        // without blocking inside Update).
        private const int GunfireBurstRounds = 3;
        private const float GunfireBurstRoundGap = 0.07f;

        // DirectFire (Tank, direct fire): same tracer but thicker, brighter, slightly longer-lived + a
        // slightly larger flash.
        private const float DirectFireTracerDuration = 0.15f;
        private const float DirectFireTracerWidth = 0.15f; // Task108: 0.35→0.15
        private const float DirectFireFlashSize = 1.4f;    // Task108: 2.2→1.4

        // IndirectFire (Artillery, indirect fire): a light trail flying a parabola from From (a tracer,
        // changed from a model sphere in Task43) + a short impact puff on landing.
        private const float ArcTravelDuration = 1.2f;
        private const float ArcApexRatio = 0.18f;   // apex height = horizontal distance x this ratio (Task108: 0.25→0.18)
        private const float ArcApexMin = 4f;
        private const float ArcApexMax = 120f;

        // Task108 (user report "the artillery post's light trail looks wildly off / just a curve from
        // the muzzle to the impact point would be fine"): previously the trail was drawn as a 2-vertex
        // LineRenderer (head = the shell, tail = a point lagging by TrailLagT), so what was actually
        // drawn was the "chord" connecting two points on the parabola, which visibly diverged from the
        // ballistic curve (the shorter the distance, the larger the chord-vs-curve gap). Replace it
        // with a polyline of ArcSegments segments that traces the parabola itself, a "curve" extending
        // from the launch point to the shell's current position.
        private const int ArcSegments = 24;
        /// <summary>Task108: trail width. 0.4 is too thick relative to the real thing (user feedback),
        /// so make it thinner.</summary>
        private const float ArcTrailWidth = 0.15f;
        private const float ImpactPuffDuration = 0.3f;
        private const float ImpactPuffSize = 3.5f;

        // Task108 (user request "add a muzzle flash to indirect fire too"): previously indirect fire
        // only had the trail start flying, with nothing shown on the firing side (direct fire/gunfire
        // get SpawnTracer's muzzle flash). To convey a cannon's heft, show a flash at the launch point
        // that is a size larger than direct fire's and lingers slightly longer.
        private const float ArcMuzzleFlashSize = 1.8f; // Task108: 3.2→1.8 (matching the flash shrink)
        private const float ArcMuzzleFlashDuration = 0.18f;

        // Fixed warm colors (no faction-color tinting).
        private static readonly Color GunfireColor = new Color(1f, 0.92f, 0.55f);     // warm yellowish white
        private static readonly Color DirectFireColor = new Color(1f, 0.75f, 0.25f);  // deeper orange (the weight of direct fire)
        private static readonly Color FlashColor = new Color(1f, 0.95f, 0.8f);
        // Task43: trail color for indirect-fire shells. A deeper orange than DirectFire so it is easy to
        // distinguish from gunfire/direct fire even from afar (no faction-color tinting, same policy as
        // the other tracers).
        private static readonly Color ArcTrailColor = new Color(1f, 0.55f, 0.15f);
        private static readonly Color PuffColor = new Color(0.55f, 0.5f, 0.45f);

        private enum Phase { Tracer, ArcTravel, ImpactPuff }

        private class Effect
        {
            public GameObject Root;
            public Phase Phase;
            public float Elapsed;
            public float Duration;

            // Tracer (Gunfire/DirectFire) only, and ArcTravel only (Task43: shared as the trail tracer).
            public LineRenderer Line;
            public float InitialWidth;

            // Shared transform pointing to either the Tracer's muzzle flash or the ImpactPuff's smoke
            // (the role is determined by Phase).
            // Task43: no longer used during ArcTravel (the indirect shell's "projectile" representation
            // was replaced with Line).
            public Transform FlashOrShell;
            public float InitialFlashSize;

            // ArcTravel only.
            public Vector3 From;
            public Vector3 To;
            public float ApexHeight;
        }

        private static readonly List<Effect> _effects = new List<Effect>();

        /// <summary>Task43: real-time queue holding the not-yet-fired follow-up rounds of a gunfire
        /// 3-round burst. Advanced without blocking by consuming real time inside Update() (no random
        /// numbers, unrelated to sim ticks — purely a Game-layer, visuals-only flourish).</summary>
        private class PendingBurst
        {
            public Vector3 From;
            public Vector3 To;
            public int RemainingRounds;
            public float TimeUntilNextRound;
        }

        private static readonly List<PendingBurst> _pendingBursts = new List<PendingBurst>();

        private static Shader _shader;
        private static bool _shaderResolved;
        private static Material _gunfireMaterial;
        private static Material _directFireMaterial;
        private static Material _flashMaterial;
        private static Material _arcTrailMaterial;
        private static Material _puffMaterial;

        /// <summary>
        /// Spawns effects from one tick's worth of ShotEvents (main thread only).
        /// Once MaxLiveEffects is reached, the remaining shots are silently ignored (not an exception).
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

        /// <summary>Advances all live effects and the pending burst follow-up rounds (Task43) by real
        /// time (realDeltaTime) (main thread only). When ArcTravel reaches its Duration, it does not
        /// create a new GameObject; the same shell is reincarnated into an impact puff (ImpactPuff) and
        /// continues.</summary>
        public static void Update(float realDeltaTime)
        {
            if (_effects.Count == 0 && _pendingBursts.Count == 0) return;

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

                AdvancePendingBursts(realDeltaTime);
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.Update error: " + e);
            }
        }

        /// <summary>Task43: advances rounds 2/3 of the gunfire 3-round burst purely by real-time
        /// elapsing, without blocking. Spawns one new tracer per GunfireBurstRoundGap via the existing
        /// SpawnTracer. While MaxLiveEffects is reached, only the spawning is silently skipped (queue
        /// consumption itself never stops, so the wait queue cannot keep growing without bound in a
        /// large melee).</summary>
        private static void AdvancePendingBursts(float realDeltaTime)
        {
            if (_pendingBursts.Count == 0) return;

            for (int i = _pendingBursts.Count - 1; i >= 0; i--)
            {
                PendingBurst b = _pendingBursts[i];
                b.TimeUntilNextRound -= realDeltaTime;
                if (b.TimeUntilNextRound > 0f) continue;

                if (_effects.Count < MaxLiveEffects)
                {
                    SpawnTracer(b.From, b.To, GunfireTracerDuration, GunfireTracerWidth, GunfireFlashSize,
                        GetGunfireMaterial());
                }

                b.RemainingRounds--;
                if (b.RemainingRounds <= 0)
                {
                    _pendingBursts.RemoveAt(i);
                }
                else
                {
                    b.TimeUntilNextRound += GunfireBurstRoundGap;
                }
            }
        }

        /// <summary>Destroys all live effects and the waiting burst follow-up rounds (Task43)
        /// (at level unload, main thread only). Cached materials are not destroyed since they are not
        /// GameObjects (same treatment as UnitMaterialFactory; they can be reused in the next session).</summary>
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
                _pendingBursts.Clear(); // Task43: so no follow-up rounds from the old session leak out and fire after level unload.
            }
        }

        private static void SpawnOne(ShotEvent e, Vector3? cameraPos)
        {
            Vector3 from = new Vector3(e.From.X, e.From.Y, e.From.Z);
            Vector3 to = new Vector3(e.To.X, e.To.Y, e.To.Z);

            // Task43: lift the firing/impact positions from ground level up to model-center height. The
            // attacker side (From) uses AttackerId's visual height, the impact side (To) uses TargetId's.
            // TargetId==0 means a base (or an unknown target = not a logical unit), so no visual lookup
            // is attempted and the default is used.
            from.y += ResolveAttackerMuzzleHeight(e.AttackerId, e.Category);
            to.y += ResolveTargetMuzzleHeight(e.TargetId);

            if (cameraPos.HasValue)
            {
                Vector3 mid = (from + to) * 0.5f;
                float distSqr = (mid - cameraPos.Value).sqrMagnitude;
                if (distSqr > MaxSpawnDistanceFromCamera * MaxSpawnDistanceFromCamera) return;
            }

            switch (e.Kind)
            {
                case ShotKind.Gunfire:
                    // Task43: gunfire is a 3-round burst. The first round fires immediately here; rounds
                    // 2/3 are delayed on a real-time basis at GunfireBurstRoundGap intervals
                    // (AdvancePendingBursts, advanced from Update without blocking).
                    SpawnTracer(from, to, GunfireTracerDuration, GunfireTracerWidth, GunfireFlashSize,
                        GetGunfireMaterial());
                    _pendingBursts.Add(new PendingBurst
                    {
                        From = from,
                        To = to,
                        RemainingRounds = GunfireBurstRounds - 1,
                        TimeUntilNextRound = GunfireBurstRoundGap
                    });
                    break;
                case ShotKind.DirectFire:
                    // Task87: bombers get a bomb-drop motion instead of a tracer (BombFx handles the fall
                    // through to the impact explosion).
                    if (e.Category == UnitCategory.TacticalBomber)
                    {
                        BombFx.SpawnDrop(from, to);
                        break;
                    }
                    SpawnTracer(from, to, DirectFireTracerDuration, DirectFireTracerWidth, DirectFireFlashSize,
                        GetDirectFireMaterial());
                    break;
                case ShotKind.IndirectFire:
                    SpawnMuzzleFlash(from, ArcMuzzleFlashSize, ArcMuzzleFlashDuration); // Task108
                    SpawnArc(from, to);
                    break;
                case ShotKind.SamMissile:
                    // Task90: surface-to-air missile. The homing projectile, flares, and evasive
                    // maneuvers are handled entirely by AaMissileFx.
                    AaMissileFx.Spawn(from, to, e.TargetId, e.Missed);
                    break;
            }

            // Task51: per-category shot sound playback (implemented in CombatFxSound.cs, same partial class).
            PlayShotSound(e, from, cameraPos);
        }

        /// <summary>Visual height of the attacker side (From) (Task43). Falls back to
        /// DefaultMuzzleHeight when the visual does not exist yet (pre-spawn/destroyed).
        /// Task108: attackerId of 0 = fire from a fortification structure (FortCombatStep), so the
        /// height matching the structure's model is derived from the unit category (see the constant
        /// comments above).</summary>
        private static float ResolveAttackerMuzzleHeight(uint attackerId, UnitCategory category)
        {
            if (attackerId != 0)
            {
                float offset;
                if (UnitVisuals.TryGetMuzzleOffset(attackerId, out offset)) return offset;
                return DefaultMuzzleHeight;
            }

            if (category == UnitCategory.Artillery) return ArtilleryPostMuzzleHeight; // artillery post
            if (category == UnitCategory.Infantry) return BunkerMuzzleHeight;         // bunker
            return DefaultMuzzleHeight;
        }

        /// <summary>Visual height of the impact side (To) (Task43). targetId==0 means a base (or an
        /// unknown target); no visual lookup is attempted and BaseTargetHeight is used. When targetId!=0
        /// but no visual exists (the target unit is dead, not spawned yet, etc.), falls back to
        /// DefaultMuzzleHeight.</summary>
        private static float ResolveTargetMuzzleHeight(uint targetId)
        {
            if (targetId == 0) return BaseTargetHeight;

            float offset;
            if (UnitVisuals.TryGetMuzzleOffset(targetId, out offset)) return offset;
            return DefaultMuzzleHeight;
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

        /// <summary>Task108: a short-lived flash at the launch point only (with no trail). For indirect
        /// fire (muzzle flash). Reuses the Tracer phase, but since it has no Line, StepTracer only
        /// advances the sphere's shrinking. Silently omitted if the total effect cap is exceeded (the
        /// actual ballistics take priority over visual garnish).</summary>
        private static void SpawnMuzzleFlash(Vector3 at, float size, float duration)
        {
            if (_effects.Count >= MaxLiveEffects) return;

            Material flashMaterial = GetFlashMaterial();
            if (flashMaterial == null) return;

            try
            {
                var go = new GameObject("CSWarfrontMuzzleFlash");
                Transform flash = CreateSmallSphere(go.transform, at, size, flashMaterial);

                _effects.Add(new Effect
                {
                    Root = go,
                    Phase = Phase.Tracer,
                    Elapsed = 0f,
                    Duration = duration,
                    Line = null,
                    FlashOrShell = flash,
                    InitialFlashSize = size
                });
            }
            catch (Exception e)
            {
                ModConfig.LogError("CombatFx.SpawnMuzzleFlash error: " + e);
            }
        }

        /// <summary>Task43: draws the indirect-fire shell as a gunfire-like light trail (tracer) instead
        /// of a sphere model. Advancing the head of a single LineRenderer along the parabola at t while
        /// the tail follows lagging by TrailLagT makes a short streak appear to fly in an arc (see
        /// StepArcTravel).</summary>
        private static void SpawnArc(Vector3 from, Vector3 to)
        {
            Material trailMaterial = GetArcTrailMaterial();
            if (trailMaterial == null) return;

            try
            {
                var go = new GameObject("CSWarfrontShotFxArc");
                var line = go.AddComponent<LineRenderer>();
                line.sharedMaterial = trailMaterial;
                line.useWorldSpace = true;
                line.SetVertexCount(ArcSegments + 1);
                line.SetWidth(ArcTrailWidth, ArcTrailWidth);
                // Fold all vertices onto the launch point so a fully stretched trail is not visible for
                // the one frame before the first StepArcTravel.
                for (int i = 0; i <= ArcSegments; i++) line.SetPosition(i, from);

                float horizontalDist = Mathf.Sqrt((to.x - from.x) * (to.x - from.x) + (to.z - from.z) * (to.z - from.z));
                float apex = Mathf.Clamp(horizontalDist * ArcApexRatio, ArcApexMin, ArcApexMax);

                _effects.Add(new Effect
                {
                    Root = go,
                    Phase = Phase.ArcTravel,
                    Elapsed = 0f,
                    Duration = ArcTravelDuration,
                    Line = line,
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

        /// <summary>Creates a small sphere with its Collider disabled so it does not interfere with the
        /// click-selection raycast (shared by muzzle flashes / tracers / impact puffs).</summary>
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

        /// <summary>Returns the world position at parameter t (0=launch, 1=impact) on the parabola
        /// (Task43: a shared helper so StepArcTravel can call this for both the head and the tail of
        /// the trail).</summary>
        private static Vector3 ArcPositionAt(Effect fx, float t)
        {
            t = Mathf.Clamp01(t);
            Vector3 pos = Vector3.Lerp(fx.From, fx.To, t);
            // 4*apex*t*(1-t): the standard parabolic interpolation — 0 at t=0 (launch) / t=1 (impact),
            // apex height at t=0.5.
            pos.y += 4f * fx.ApexHeight * t * (1f - t);
            return pos;
        }

        /// <summary>Task108: traces the parabola from the launch point (t=0) to the shell's current
        /// position (t) with a polyline of ArcSegments segments (= the ballistic curve itself grows).
        /// Previously two vertices drew "the chord connecting two points on the arc", which visibly
        /// diverged from the ballistic curve (user report "the light trail looks wildly off").</summary>
        private static void StepArcTravel(Effect fx)
        {
            if (fx.Line == null) return;
            float t = fx.Duration > 0f ? Mathf.Clamp01(fx.Elapsed / fx.Duration) : 1f;
            for (int i = 0; i <= ArcSegments; i++)
                fx.Line.SetPosition(i, ArcPositionAt(fx, t * i / ArcSegments));
        }

        private static void StepImpactPuff(Effect fx)
        {
            if (fx.FlashOrShell == null) return;
            float t = fx.Duration > 0f ? Mathf.Clamp01(fx.Elapsed / fx.Duration) : 1f;
            float s = Mathf.Lerp(fx.InitialFlashSize, 0f, t);
            fx.FlashOrShell.localScale = new Vector3(s, s, s);
        }

        /// <summary>Hides the trail (Line) that landed, and instead spawns exactly one small impact puff
        /// and continues (Task43: since the indirect shell's "projectile" representation changed from a
        /// sphere to a tracer, we do not reuse an existing sphere like the old implementation did — the
        /// impact-flash sphere is created for the first time at this point. The shell itself is never a
        /// sphere at any stage). If the reincarnation fails, the effect is destroyed immediately so it
        /// is not left behind.</summary>
        private static void TransitionToImpactPuff(Effect fx)
        {
            try
            {
                if (fx.Line != null)
                {
                    fx.Line.SetWidth(0f, 0f);
                    fx.Line.enabled = false;
                }

                Transform puff = CreateSmallSphere(fx.Root.transform, fx.To, ImpactPuffSize, GetPuffMaterial());
                fx.FlashOrShell = puff;
                fx.InitialFlashSize = ImpactPuffSize;

                fx.Phase = Phase.ImpactPuff;
                fx.Elapsed = 0f;
                fx.Duration = ImpactPuffDuration;
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
                    ModConfig.LogError("CombatFx: failed to resolve shader (Standard/Legacy Shaders/Diffuse all failed), muzzle flash effects will not render");
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

        private static Material GetArcTrailMaterial()
        {
            if (_arcTrailMaterial == null) _arcTrailMaterial = BuildMaterial(ArcTrailColor);
            return _arcTrailMaterial;
        }

        private static Material GetPuffMaterial()
        {
            if (_puffMaterial == null) _puffMaterial = BuildMaterial(PuffColor);
            return _puffMaterial;
        }
    }
}
