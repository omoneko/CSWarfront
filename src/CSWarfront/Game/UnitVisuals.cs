using System;
using System.Collections.Generic;
using CSWarfront.Core;
using CSWarfront.Game.Audio;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// Main-thread-only snapshot. Contains only the minimum information needed to determine the
    /// "appearance" of a single unit. A value type that contains no CS entities (Vehicle etc.) at all.
    /// </summary>
    public struct UnitVisualState
    {
        public uint InstanceId;
        public string TypeKey;
        public byte FactionId;
        public Vector3 Position;
        /// <summary>Core-side UnitType.AssetPrefabName (Workshop asset etc.). If empty, the default
        /// fallback is used.</summary>
        public string AssetPrefabName;

        /// <summary>Task140: horizontal direction to whatever this unit is currently engaging, or zero
        /// when it is engaging nothing. A turret tracks this continuously. Without it the gun only knew
        /// where to point in the moments just after a shot (the Task83 firing-direction hold), so between
        /// shots it swung back to dead ahead and the model read as one rigid piece - which is exactly how
        /// it was reported.</summary>
        public Vector3 AimDirection;
    }

    /// <summary>
    /// Renders unit visuals ourselves as plain Unity GameObjects instead of "real CS vehicles".
    /// Only the mesh is borrowed (VehicleInfo.m_mesh, or m_lodMesh if absent); no AI or
    /// TransferManager integration is inherited at all. This fundamentally avoids crashes caused by
    /// service vehicle AIs such as FireTruckAI (out-of-range array access in
    /// TransferManager.RemoveIncomingOffer).
    /// Because the borrow source is resolved by prefab name, swapping in Workshop custom assets in
    /// the future works safely regardless of whatever AI those assets carry.
    /// Materials are never borrowed from CS vehicles (see <see cref="UnitMaterialFactory"/>).
    /// CS vehicle materials use dedicated shaders that require per-instance data from CS's own
    /// renderer, so assigning them to a plain MeshRenderer makes objects invisible/black (this was
    /// the actual cause of the invisibility bug we hit).
    /// Instead we create and share one standard-shader material of our own per faction, so factions
    /// can be told apart by color.
    ///
    /// Task37: The visibility marker cube and faction color above were narrowed down to be the look
    /// used only for "units with no assigned asset". When the TypeKey has an assigned asset
    /// (fromAssignedProp in UnitMeshSource.TryResolve), no marker is shown and the material uses the
    /// asset's own look (<see cref="UnitMaterialFactory.TryGetAssetMaterial"/>).
    /// Click-selection hit testing is provided by a BoxCollider on the root GameObject itself
    /// instead of the marker's BoxCollider (see CreateVisual/AttachPropCollider). Task41 extended
    /// this beyond props (buildings/vehicles/trees).
    ///
    /// Thread boundary: every public method of this class is "main thread only"
    /// (new GameObject / AddComponent / Destroy / transform writes are Unity main-thread constraints).
    /// Never call from the sim thread (MilitaryManager.OnSimTick).
    /// </summary>
    public static partial class UnitVisuals
    {
        private class VisualEntry
        {
            public GameObject GameObject;
            public Vector3 LastPosition;

            /// <summary>Task43: The "center height" of this unit's model (the root GameObject's
            /// position, i.e. Y relative to the unit's logical coordinates). Computed once at
            /// CreateVisual time and cached (the mesh never changes while the visual is alive).
            /// Used by CombatFx to raise the firing/impact height of muzzle effects from ground level
            /// up to this height (see TryGetMuzzleOffset).</summary>
            public float MuzzleOffsetY;

            /// <summary>Task49: Child GameObject for the faction icon (small sphere). Null while
            /// WarfrontSettings.ShowFactionIcons is OFF, or while creation has not yet succeeded
            /// (the per-frame UpdateFactionIcon handles lazy creation/destruction). Attached to all
            /// units, including fromAssignedProp (assigned-asset) units.</summary>
            public GameObject Icon;

            /// <summary>Height (Y) at which the icon is placed in the root GameObject's local
            /// coordinate space. Computed once at CreateVisual time from mesh.bounds.max.y + gap and
            /// cached (same policy as MuzzleOffsetY).</summary>
            public float IconLocalHeightY;

            /// <summary>Task83 (user request "face the attack direction when attacking"): The firing
            /// direction of the most recent shot (horizontal, normalized). NotifyShots sets this from
            /// shot events, and until FacingHoldUntil, MoveVisual adopts this as the facing instead of
            /// the movement direction.</summary>
            public Vector3 FacingDirection;

            /// <summary>Deadline (real time, Time.time based) until which the unit keeps facing the
            /// firing direction. Refreshed on every shot, so the unit keeps facing the target as long
            /// as the engagement continues, and reverts to movement-direction facing a few seconds
            /// after the engagement ends.</summary>
            public float FacingHoldUntil;

            /// <summary>Task108 (user report "it looks unnatural that a helicopter noses down when
            /// landing"): Whether the facing is decided from the horizontal component only. The nose
            /// stays level even during vertical landing/takeoff movement. True for air units
            /// (helicopters, fighters, bombers). False for kamikaze drones, because the dive-into-target
            /// attitude matters visually (they keep facing the raw movement direction as before).
            /// Also false for land/sea units (the slight pitch on slopes looks like following the
            /// terrain, so it is kept).</summary>
            public bool LevelFlight;

            /// <summary>Task108: Car GameObjects for articulated rendering (military freight train),
            /// ordered front to back. Null = rendered as a single rigid body as before. See
            /// UnitVisualsTrain.cs for details.</summary>
            public GameObject[] Cars;

            /// <summary>How many meters behind the head each car travels (same ordering as Cars).</summary>
            public float[] CarBehindHead;

            /// <summary>Trail traced by the head (old to new). Each car is placed on it.</summary>
            public List<Vector3> Trail;

            /// <summary>Task109: Looping AudioSource for the movement sound (null if this unit type
            /// has no movement sound).</summary>
            public AudioSource Engine;

            /// <summary>Task109: Whether the unit actually moved in the most recent frame (used to
            /// decide whether to play the movement sound).</summary>
            public bool MovedThisFrame;

            /// <summary>Task126: the rotating turret, when this model has one (TurretMeshSplitter).
            /// Null for every other unit, which is also the "render rigid" signal in MoveVisual.</summary>
            public Transform Turret;

            /// <summary>Task126: half a turn is added to the turret's yaw when the model's barrel was
            /// authored pointing -Z, so such a model still aims its gun at the target rather than its
            /// back.</summary>
            public float TurretBarrelSign;

            /// <summary>Task126: the turret's current yaw relative to the hull (degrees). Kept so the
            /// gun traverses smoothly instead of snapping, and returns to dead ahead when idle.</summary>
            public float TurretYaw;

            /// <summary>Task109: Time (Time.time based) at which to retry attaching the movement
            /// sound. Wav loading proceeds asynchronously in a coroutine, so units spawned right
            /// after a load cannot grab the clip at creation time. We retry only a few times with
            /// spacing, then give up (float.MaxValue).</summary>
            public float EngineRetryAt;
            public int EngineRetries;

            /// <summary>Task90: End time (Time.time based) of the evasive maneuver (visual jink) when
            /// an anti-air missile is closing in. Set by AaMissileFx via NotifyEvade. The logical
            /// position (Core) is unchanged; only the display position gets a decaying lateral sway
            /// offset.</summary>
            public float EvadeUntil;

            /// <summary>Lateral direction of the evasive maneuver (horizontal, normalized).
            /// NotifyEvade sets it perpendicular to the direction of travel.</summary>
            public Vector3 EvadeDir;
        }

        /// <summary>Duration of the evasive maneuver (real seconds) and its max sway amplitude (m).</summary>
        private const float EvadeDurationSeconds = 1.2f;
        private const float EvadeAmplitude = 10f;

        /// <summary>Real time (seconds) to keep facing the firing direction after a shot. Made longer
        /// than the shot interval during an engagement so the unit appears to "face the enemy the
        /// whole time it is fighting".</summary>
        private const float FacingHoldSeconds = 4f;

        // Size of the visibility marker (primitive cube) and the lift amount to keep it from sinking
        // into the ground.
        // Task37: No longer used when an assigned prop exists (see the fromAssignedProp branch of
        // AttachVisibilityMarker).
        private const float MarkerSize = 8f;
        private const float MarkerHeight = 5f;

        // Task37: Minimum size of the click-hit-test BoxCollider for assigned props (no marker).
        // A lower bound so that even tiny props remain clickable.
        private const float MinPropColliderSize = 4f;

        private const float MinMoveDeltaForRotation = 0.01f;

        // Task43: Clamp range for the value returned by TryGetMuzzleOffset. A safety band so that
        // muzzle-effect heights do not become unnatural even when the borrowed mesh (whose size
        // varies wildly by asset) is extremely flat/huge.
        private const float MinMuzzleOffsetY = 1f;
        private const float MaxMuzzleOffsetY = 20f;

        // Task49: How far to float the faction icon (small sphere) above the top of the model, plus
        // its safety clamp. Only CreateVisual (below) uses these here. Scale-related constants and
        // the creation/update logic were split into the partial class definition in
        // UnitVisualsFactionIcon.cs (due to the 500-line limit, same policy as
        // MilitaryManagerUnitCommands.cs; private members are still shared across partial class parts).
        private const float IconGapAboveMesh = 1.5f;
        private const float MinIconLocalHeightY = 2f;
        private const float MaxIconLocalHeightY = 25f;

        private static readonly Dictionary<uint, VisualEntry> _visuals = new Dictionary<uint, VisualEntry>();

        // Instance ids whose creation failed, e.g. because the mesh could not be resolved. To avoid
        // per-frame retries and log spam, an id that failed once is recorded here and skipped by
        // Sync() from then on. When it disappears from the snapshot (death, deletion, etc.) it is
        // released in preparation for id reuse (done in the stale handling below, same path as _visuals).
        private static readonly HashSet<uint> _failedInstances = new HashSet<uint>();

        // Work areas reused across Sync() runs (GC avoidance).
        private static readonly HashSet<uint> _seenIds = new HashSet<uint>();
        private static readonly List<uint> _staleIds = new List<uint>();
        private static readonly List<uint> _staleFailedIds = new List<uint>();

        public static int Count { get { return _visuals.Count; } }

        /// <summary>
        /// Resolves the InstanceId of the logical unit that a raycast-hit GameObject (including the
        /// case where it is a child visibility marker) belongs to (Task31: used from
        /// Game/UI/UnitSelection). Returns false for hits that do not belong to this MOD's unit
        /// representation (vanilla buildings, terrain, roads, etc.) — in that case the caller must
        /// leave the selection state unchanged and defer to vanilla click behavior as-is.
        /// </summary>
        public static bool TryGetInstanceId(GameObject go, out uint instanceId)
        {
            instanceId = 0;
            if (go == null) return false;

            UnitVisualTag tag = go.GetComponentInParent<UnitVisualTag>();
            if (tag == null) return false;

            instanceId = tag.InstanceId;
            return true;
        }

        /// <summary>
        /// Returns the "current" world position of the visual for the given id (main thread only).
        /// Task32: Used so that when UnitInfoPanel follows a unit, the authoritative position is the
        /// transform.position of the actually-rendered GameObject rather than snapshot-derived
        /// coordinates (the panel should chase "the thing actually being drawn", which can drift in
        /// timing from the snapshot's source). Returns false if the visual has not been created yet
        /// or has been destroyed.
        /// </summary>
        public static bool TryGetPosition(uint instanceId, out Vector3 position)
        {
            position = default(Vector3);

            VisualEntry entry;
            if (!_visuals.TryGetValue(instanceId, out entry)) return false;
            if (entry == null || entry.GameObject == null) return false;

            position = entry.GameObject.transform.position;
            return true;
        }

        /// <summary>
        /// Returns the "model center height" of the visual for the given id, relative to the unit's
        /// logical position (position.y) (main thread only, Task43). Used by CombatFx to raise the
        /// firing/impact positions of muzzle effects up from ground level. Only returns the value
        /// computed once from mesh.bounds at CreateVisual time and cached (already clamped to
        /// <see cref="MinMuzzleOffsetY"/>–<see cref="MaxMuzzleOffsetY"/>); the mesh is never accessed
        /// again per call. Returns false if the visual has not been created yet or has been destroyed
        /// (the caller must fall back to the Task43 defaults, e.g. DefaultMuzzleHeight/BaseTargetHeight).
        /// </summary>
        public static bool TryGetMuzzleOffset(uint instanceId, out float yOffset)
        {
            yOffset = 0f;

            VisualEntry entry;
            if (!_visuals.TryGetValue(instanceId, out entry)) return false;
            if (entry == null || entry.GameObject == null) return false;

            yOffset = entry.MuzzleOffsetY;
            return true;
        }

        /// <summary>
        /// Declaratively applies creation/movement/destruction based on the snapshot (main thread
        /// only). Ids that are absent from the snapshot (including dead, deleted, and not-yet-loaded)
        /// are destroyed here.
        /// </summary>
        public static void Sync(List<UnitVisualState> snapshot)
        {
            if (snapshot == null) return;

            // Task49: Fetch the camera once for faction-icon distance scaling (Camera.main can
            // involve a tag search, so keep it to once per frame instead of once per unit). If not
            // found, pass null through and UpdateFactionIcon skips the scale computation (icon
            // creation/destruction itself continues).
            Camera mainCamera = Camera.main;
            Vector3? cameraPos = mainCamera != null ? (Vector3?)mainCamera.transform.position : null;
            UnitEngineAudio.BeginFrame(); // Task109: reset the concurrent movement-sound counter
            _simulationPaused = IsGamePaused(); // Task135: hold the turrets still while the game is paused

            _seenIds.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                UnitVisualState s = snapshot[i];
                _seenIds.Add(s.InstanceId);

                try
                {
                    if (_failedInstances.Contains(s.InstanceId))
                    {
                        continue; // Known to be uncreatable. Skip to the next unit to avoid log spam/retries.
                    }

                    VisualEntry entry;
                    if (!_visuals.TryGetValue(s.InstanceId, out entry) || entry.GameObject == null)
                    {
                        entry = CreateVisual(s);
                        if (entry == null)
                        {
                            // Already logged inside CreateVisual (once only). This id is skipped at the top of Sync from now on.
                            _failedInstances.Add(s.InstanceId);
                            continue;
                        }
                        _visuals[s.InstanceId] = entry;
                    }
                    else
                    {
                        MoveVisual(entry, s.Position, s.AimDirection);
                    }

                    // Task49: Called every frame after this point on both the create and move paths
                    // (centralizes tracking of the ON/OFF toggle and distance changes in both paths).
                    // fromAssignedProp (assigned-asset) units are not excluded = works for both
                    // (requirement).
                    UpdateFactionIcon(entry, s.FactionId, mainCamera);

                    // Task109: Movement sound (loop). Individuals that could not grab the clip
                    // because the async wav load was not done in time retry attaching only a few
                    // times, with spacing.
                    if (entry.Engine == null && entry.EngineRetries < 5 && Time.time >= entry.EngineRetryAt)
                    {
                        entry.Engine = UnitEngineAudio.TryAttach(entry.GameObject, s.TypeKey);
                        entry.EngineRetries++;
                        entry.EngineRetryAt = Time.time + 2f;
                    }
                    UnitEngineAudio.Update(entry.Engine, entry.MovedThisFrame, s.Position, cameraPos);
                }
                catch (Exception e)
                {
                    ModConfig.LogError("UnitVisuals.Sync: failed to update instance " + s.InstanceId + ": " + e);
                }
            }

            // Enumerate ids missing from the snapshot and destroy them (two phases to avoid
            // modifying the Dictionary during the loop).
            _staleIds.Clear();
            foreach (var kv in _visuals)
            {
                if (!_seenIds.Contains(kv.Key)) _staleIds.Add(kv.Key);
            }
            for (int i = 0; i < _staleIds.Count; i++)
            {
                DestroyVisual(_staleIds[i]);
            }

            // Also release failed ids missing from the snapshot (so an id is not blocked forever
            // when it gets reused).
            _staleFailedIds.Clear();
            foreach (var failedId in _failedInstances)
            {
                if (!_seenIds.Contains(failedId)) _staleFailedIds.Add(failedId);
            }
            for (int i = 0; i < _staleFailedIds.Count; i++)
            {
                _failedInstances.Remove(_staleFailedIds[i]);
            }
        }

        /// <summary>
        /// Enumerates the InstanceIds and actual render positions of the currently-created visuals
        /// (main thread only, Task48). Used by Game/UI/UnitBoxSelection for box selection (hit testing
        /// the screen rectangle against screen projections of world coordinates). The caller-provided
        /// buffers are Clear()ed and refilled (GC avoidance, same convention as UnitVisuals.Sync).
        /// </summary>
        public static void CollectVisible(List<uint> ids, List<Vector3> positions)
        {
            if (ids == null || positions == null) return;
            ids.Clear();
            positions.Clear();
            foreach (var kv in _visuals)
            {
                if (kv.Value == null || kv.Value.GameObject == null) continue;
                ids.Add(kv.Key);
                positions.Add(kv.Value.GameObject.transform.position);
            }
        }

        /// <summary>Destroys all tracked visuals (on level unload, main thread only).</summary>
        public static void DestroyAll()
        {
            try
            {
                foreach (var kv in _visuals)
                {
                    if (kv.Value != null && kv.Value.GameObject != null)
                    {
                        UnityEngine.Object.Destroy(kv.Value.GameObject);
                    }
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.DestroyAll error: " + e);
            }
            finally
            {
                _visuals.Clear();
                _failedInstances.Clear();
                TurretMeshSplitter.Reset(); // Task126: the split meshes are ours to destroy
            }
        }

        /// <summary>Task126: degrees per second the gun traverses. Deliberately unhurried — a real
        /// turret is slower than the hull it sits on, and a snapping gun looks like a glitch.</summary>
        private const float TurretTraverseDegreesPerSecond = 45f;

        /// <summary>Task135 (user request "stop the turrets while the game is paused"): this runs on the
        /// main thread every frame, which keeps ticking while the simulation is stopped — so a paused
        /// battle had guns still swinging around over motionless tanks. The traverse is the one piece of
        /// unit animation driven by frame time rather than by state, so it is the only one that needs to
        /// be told the game is paused. Same check the engine audio uses.</summary>
        private static bool IsGamePaused()
        {
            try { return SimulationManager.instance.SimulationPaused; }
            catch (Exception) { return false; }
        }

        /// <summary>Task135: whether the simulation was paused when this frame's Sync began. Sampled
        /// once per frame rather than once per turret.</summary>
        private static bool _simulationPaused;

        /// <summary>Task126: turns the turret toward worldAim (zero = return to dead ahead), rate
        /// limited, in the hull's local frame. Main thread only.</summary>
        private static void AimTurret(VisualEntry entry, Vector3 worldAim)
        {
            // Task135: freeze mid-traverse rather than snapping to the target — resuming continues from
            // exactly where the gun was pointing.
            if (_simulationPaused) return;

            float targetYaw = 0f;
            if (worldAim.sqrMagnitude > 1e-6f)
            {
                Vector3 local = entry.GameObject.transform.InverseTransformDirection(worldAim);
                local.y = 0f;
                if (local.sqrMagnitude > 1e-6f)
                {
                    targetYaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                    // A barrel modelled pointing -Z aims correctly once the turret is turned around.
                    if (entry.TurretBarrelSign < 0f) targetYaw += 180f;
                }
            }

            float delta = Mathf.DeltaAngle(entry.TurretYaw, targetYaw);
            float step = TurretTraverseDegreesPerSecond * Time.deltaTime;
            if (Mathf.Abs(delta) <= step) entry.TurretYaw = targetYaw;
            else entry.TurretYaw += Mathf.Sign(delta) * step;

            entry.Turret.localRotation = Quaternion.Euler(0f, entry.TurretYaw, 0f);
        }

        private static VisualEntry CreateVisual(UnitVisualState s)
        {
            try
            {
                Mesh mesh;
                Material[] builtInMaterials;
                bool fromAssignedProp;
                bool fromBuiltInModel;
                AssetKind resolvedKind;
                string resolvedAssetName;
                if (!UnitMeshSource.TryResolve(s.FactionId, s.TypeKey, s.AssetPrefabName, out mesh, out builtInMaterials, out fromAssignedProp, out fromBuiltInModel, out resolvedKind, out resolvedAssetName))
                {
                    ModConfig.LogError("UnitVisuals.CreateVisual: instance " + s.InstanceId + " failed to resolve mesh, skipping visual");
                    return null;
                }

                // Task37: When an assigned asset exists (prop/building/vehicle/tree, extended in
                // Task41), keep the asset's own look (texture) and do not paint it in faction color.
                // Task69: Default (built-in) models, like assigned models, are rendered in their own
                // colors (builtInMaterials, the actual MTL colors of models produced by
                // tools/export_builtin_obj.py) with no faction-color tint (faction identification is
                // unified into the existing faction icon).
                // Only when neither applies is the single faction-color material used, as before.
                bool useBuiltInMaterials = fromBuiltInModel && builtInMaterials != null && builtInMaterials.Length > 0;

                Material material = null;
                bool materialOk;
                if (useBuiltInMaterials)
                {
                    materialOk = true; // materials were already prepared by WarfrontModelProvider.TryGetModel
                }
                else if (fromAssignedProp)
                {
                    materialOk = UnitMaterialFactory.TryGetAssetMaterial(resolvedKind, resolvedAssetName, out material);
                }
                else
                {
                    materialOk = UnitMaterialFactory.TryGetFactionMaterial(s.FactionId, out material);
                }
                if (!materialOk)
                {
                    ModConfig.LogError("UnitVisuals.CreateVisual: instance " + s.InstanceId + " failed to create material, skipping visual");
                    return null;
                }

                var go = new GameObject("CSWarfrontUnit_" + s.InstanceId);

                // Task31: Attach an identification tag to the root GameObject so click selection
                // (UnitSelection) can back-resolve the logical unit from the raycast hit (no separate
                // GameObject-to-InstanceId dictionary is kept).
                UnitVisualTag tag = go.AddComponent<UnitVisualTag>();
                tag.InstanceId = s.InstanceId;

                // Task37: When a mesh's pivot is not at its bottom face, the model can appear half
                // sunk into the road. To keep the root's transform.position exactly the unit's
                // logical position (no vertical offset added at all), this offset is applied only to
                // a mesh-rendering-only child ("Model").
                float pivotOffsetY = -mesh.bounds.min.y;

                // Task43: "Model center height" = pivotOffsetY (the correction that aligns the mesh's
                // bottom to Y=0 relative to the root) + mesh.bounds.center.y (the mesh's own center Y
                // in its local space). Thanks to pivotOffsetY, the mesh is always rendered with its
                // bottom at the root's Y=0, so this sum is always "half the mesh height" = the model
                // center height as seen from the root position (this holds regardless of where the
                // mesh pivot is — bottom/center/anywhere). Clamp to a safety band in case of extreme
                // meshes (flat/huge).
                float muzzleOffsetY = Mathf.Clamp(pivotOffsetY + mesh.bounds.center.y, MinMuzzleOffsetY, MaxMuzzleOffsetY);

                // Task49: Height for placing the faction icon slightly above the model top. Thanks to
                // pivotOffsetY, the mesh is always rendered with its bottom at the root's Y=0, so
                // pivotOffsetY + mesh.bounds.max.y is the root-relative height of the "model top".
                // Add the gap to that and clamp to a safety band (same idea as muzzleOffsetY).
                float iconLocalHeightY = Mathf.Clamp(pivotOffsetY + mesh.bounds.max.y + IconGapAboveMesh, MinIconLocalHeightY, MaxIconLocalHeightY);

                // Task126: split the mesh into hull + rotating turret when the model has one. Only
                // categories that actually traverse a gun are offered to the detector, so a lucky
                // shape on some other unit can never take a model apart.
                TurretParts turretParts = TurretRules.CanHaveTurret(s.TypeKey) ? TurretMeshSplitter.TryGet(mesh) : null;

                GameObject model = new GameObject("Model");
                model.transform.SetParent(go.transform, false);
                model.transform.localPosition = new Vector3(0f, pivotOffsetY, 0f);
                MeshFilter filter = model.AddComponent<MeshFilter>();
                filter.sharedMesh = turretParts != null ? turretParts.Hull : mesh;
                MeshRenderer renderer = model.AddComponent<MeshRenderer>();
                if (useBuiltInMaterials)
                {
                    renderer.sharedMaterials = builtInMaterials;
                }
                else
                {
                    renderer.sharedMaterial = material;
                }

                Transform turretTransform = null;
                if (turretParts != null)
                {
                    // The turret mesh was rebuilt around its ring, so the child sits at the ring and
                    // simply rotates in place. It is parented to the model child, inheriting the same
                    // bottom-to-Y=0 correction as the hull.
                    GameObject turret = new GameObject("Turret");
                    turret.transform.SetParent(model.transform, false);
                    turret.transform.localPosition = turretParts.Pivot;
                    turret.AddComponent<MeshFilter>().sharedMesh = turretParts.Turret;
                    MeshRenderer turretRenderer = turret.AddComponent<MeshRenderer>();
                    if (useBuiltInMaterials) turretRenderer.sharedMaterials = builtInMaterials;
                    else turretRenderer.sharedMaterial = material;
                    turretTransform = turret.transform;
                }

                go.transform.position = s.Position;

                // Task108: A military freight train is rendered as an articulated consist of "the
                // head car (this mesh) + trailing cars" (each car is placed on the trail, so the
                // consist bends on curves. UnitVisualsTrain.cs).
                GameObject[] cars = null;
                float[] carBehindHead = null;
                if (IsArticulatedType(s.TypeKey))
                    TryBuildTrainCars(go, mesh, out cars, out carBehindHead);

                // Task109: Movement sound (attach a stopped looping AudioSource if this type has a sound).
                AudioSource engine = UnitEngineAudio.TryAttach(go, s.TypeKey);

                if (fromAssignedProp || fromBuiltInModel)
                {
                    // Requirement 1: Do not show the visibility marker cube when a prop assignment exists.
                    // Task57: Same for default (built-in) models (they have a real silhouette, so the
                    // safety marker for borrowed meshes is no longer needed). Click-selection hit
                    // testing is instead attached directly to the root (since there is no marker).
                    AttachPropCollider(go, mesh, pivotOffsetY);
                }
                else
                {
                    // Visibility insurance & triage: since a CS-borrowed mesh might not render in some
                    // environments, attach a reliably-rendered primitive as a child (same technique as
                    // MissileDisaster's fallback sphere). If this is visible while the borrowed mesh is
                    // not, the cause is confirmed to be on the mesh side.
                    AttachVisibilityMarker(go, material);
                }

                ModConfig.Log("UnitVisuals: created visual for instance " + s.InstanceId + " type=" + s.TypeKey);

                return new VisualEntry
                {
                    GameObject = go,
                    LastPosition = s.Position,
                    MuzzleOffsetY = muzzleOffsetY,
                    IconLocalHeightY = iconLocalHeightY,
                    LevelFlight = IsLevelFlightType(s.TypeKey), // Task108
                    Cars = cars,
                    CarBehindHead = carBehindHead,
                    Engine = engine, // Task109
                    Turret = turretTransform, // Task126
                    TurretBarrelSign = turretParts != null ? turretParts.BarrelSign : 1f
                };
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.CreateVisual: instance " + s.InstanceId + " error: " + e);
                return null;
            }
        }

        /// <summary>
        /// Task37: Attaches a BoxCollider directly to the root GameObject for assigned props (no
        /// marker). Since the marker cube is gone, this becomes the only means of click-selection hit
        /// testing. The size and center are derived from the mesh bounds (converted into root-relative
        /// coordinates after applying pivotOffsetY to the "Model" child), and a per-axis minimum of
        /// <see cref="MinPropColliderSize"/> is guaranteed so even tiny props remain clickable.
        /// isTrigger stays false and the GameObject's layer is not changed (same reasoning as
        /// AttachVisibilityMarker).
        /// </summary>
        private static void AttachPropCollider(GameObject root, Mesh mesh, float pivotOffsetY)
        {
            try
            {
                BoxCollider col = root.AddComponent<BoxCollider>();
                col.isTrigger = false;

                Vector3 size = mesh.bounds.size;
                size.x = Mathf.Max(size.x, MinPropColliderSize);
                size.y = Mathf.Max(size.y, MinPropColliderSize);
                size.z = Mathf.Max(size.z, MinPropColliderSize);
                col.size = size;

                Vector3 center = mesh.bounds.center;
                center.y += pivotOffsetY; // apply the same offset as the "Model" child, in root-relative coordinates
                col.center = center;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.AttachPropCollider error: " + e);
            }
        }

        /// <summary>
        /// Attaches a reliably-rendered primitive cube as a child of the unit GameObject (main thread
        /// only). Insurance so the unit's position can be seen regardless of whether the borrowed mesh
        /// renders. Task37: No longer called when an assigned prop exists (fromAssignedProp)
        /// (AttachPropCollider provides hit testing only instead). Kept as the visual insurance for
        /// default/unassigned units.
        /// Task31: The BoxCollider that this marker gets at creation is not destroyed but reused as-is
        /// for click-selection hit testing (isTrigger stays false = detectable by Physics.Raycast).
        /// The GameObject's layer is not changed (changing the layer would affect the CS camera's
        /// culling/layer mask and risk re-triggering the already-resolved invisibility bug).
        /// </summary>
        private static void AttachVisibilityMarker(GameObject parent, Material material)
        {
            try
            {
                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                BoxCollider col = marker.GetComponent<BoxCollider>();
                if (col != null) col.isTrigger = false;

                marker.transform.SetParent(parent.transform, false);
                marker.transform.localPosition = new Vector3(0f, MarkerHeight, 0f);
                marker.transform.localScale = new Vector3(MarkerSize, MarkerSize, MarkerSize);

                MeshRenderer markerRenderer = marker.GetComponent<MeshRenderer>();
                if (markerRenderer != null && material != null) markerRenderer.sharedMaterial = material;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.AttachVisibilityMarker error: " + e);
            }
        }

        /// <summary>Task108: Whether units of this TypeKey should "always keep the airframe level"
        /// (= decide facing from the horizontal component of movement only). This exists to avoid the
        /// unnatural look of the nose pointing straight down/up during vertical landing/takeoff
        /// movement; aircraft and helicopters are targeted. Kamikaze drones are excluded because the
        /// dive attitude matters visually. Unparseable TypeKeys behave as before (false).</summary>
        private static bool IsLevelFlightType(string typeKey)
        {
            UnitCategory category;
            byte tier;
            if (!TypeKeyParser.TryParse(typeKey, out category, out tier)) return false;
            if (category.IsKamikaze()) return false;
            return category == UnitCategory.AirSuperiority
                || category == UnitCategory.TacticalBomber
                || category == UnitCategory.AttackHelicopter
                || category == UnitCategory.TransportHelicopter;
        }

        private static void MoveVisual(VisualEntry entry, Vector3 newPosition, Vector3 aimDirection)
        {
            if (entry == null || entry.GameObject == null) return;
            Vector3 delta = newPosition - entry.LastPosition;
            // Task109: The movement-sound check uses the raw movement amount, before the
            // facing-related processing below (removal of the Y component under LevelFlight) — so
            // that a helicopter that is merely descending vertically still counts as "moving" and
            // makes sound.
            float moveSqr = delta.sqrMagnitude;

            // Task90: Evasive maneuver while an anti-air missile closes in. The logical position stays
            // as Core says; only the display position gets a decaying lateral sway (a banking jink to
            // escape).
            Vector3 displayPosition = newPosition;
            if (Time.time < entry.EvadeUntil)
            {
                float remaining = (entry.EvadeUntil - Time.time) / EvadeDurationSeconds; // 1→0
                float progress = 1f - remaining;
                float sway = Mathf.Sin(progress * Mathf.PI * 3f) * EvadeAmplitude * remaining;
                displayPosition += entry.EvadeDir * sway;
            }
            entry.GameObject.transform.position = displayPosition;

            // Task108: Air units decide facing from the horizontal component only (prevents the nose
            // from pointing straight down/up during vertical landing/takeoff movement). If there is
            // almost no horizontal component — i.e. the unit is just descending straight down — the
            // facing is left unchanged (it descends still facing the direction it last flew).
            if (entry.LevelFlight)
            {
                delta.y = 0f;
            }

            // Task83: A unit that fired recently faces the firing direction instead of the movement
            // direction (applied every frame regardless of whether there is a movement delta, so that
            // it faces the enemy even in a stationary engagement).
            // Task126: a unit with a detected turret keeps its hull on the movement direction and
            // turns the gun instead — that is the whole point of a turret. Everything else keeps the
            // Task83 behaviour of turning the entire model toward what it is shooting at.
            bool aiming = Time.time < entry.FacingHoldUntil && entry.FacingDirection.sqrMagnitude > 1e-6f;
            if (aiming && entry.Turret == null)
            {
                entry.GameObject.transform.rotation = Quaternion.LookRotation(entry.FacingDirection);
            }
            else if (delta.sqrMagnitude > MinMoveDeltaForRotation * MinMoveDeltaForRotation)
            {
                entry.GameObject.transform.rotation = Quaternion.LookRotation(delta);
            }

            // Task140: a turret tracks whatever the unit is engaging for as long as it is engaging it,
            // not just for the few seconds after each shot. Between shots the old rule swung the gun back
            // to dead ahead, which is why a correctly split model still read as one rigid piece. The
            // firing direction remains the fallback, so a unit that shoots at something the snapshot has
            // no bearing for still points the right way.
            if (entry.Turret != null)
            {
                Vector3 turretAim = aimDirection.sqrMagnitude > 1e-6f
                    ? aimDirection
                    : (aiming ? entry.FacingDirection : Vector3.zero);
                AimTurret(entry, turretAim);
            }

            // Task126: traverse the gun toward the target (or back to dead ahead), at a fixed rate so
            // it reads as a turret turning rather than a snap.
            // (AimTurret is called above, before the train cars, so the hull rotation it reads is this
            // frame's.)

            // Task108: Re-place the articulated cars (military freight train) along the head's trail.
            if (entry.Cars != null)
                UpdateTrainCars(entry, displayPosition, entry.GameObject.transform.rotation);

            // Task109: Used for the movement-sound check (only frames where the position actually
            // changed count as "moving").
            entry.MovedThisFrame = moveSqr > MinMoveDeltaForRotation * MinMoveDeltaForRotation;

            entry.LastPosition = newPosition;
        }

        /// <summary>Task90: Makes a targeted aircraft start an evasive maneuver (visual jink) when an
        /// anti-air missile has closed in (called from AaMissileFx at the same moment flares are
        /// released. Main thread only). The lateral direction is a horizontal vector perpendicular to
        /// the current nose direction.</summary>
        public static void NotifyEvade(uint instanceId)
        {
            try
            {
                VisualEntry entry;
                if (!_visuals.TryGetValue(instanceId, out entry) || entry.GameObject == null) return;

                Vector3 forward = entry.GameObject.transform.forward;
                forward.y = 0f;
                Vector3 side = forward.sqrMagnitude > 1e-4f
                    ? Vector3.Cross(forward.normalized, Vector3.up)
                    : Vector3.right;
                entry.EvadeDir = side;
                entry.EvadeUntil = Time.time + EvadeDurationSeconds;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.NotifyEvade error: " + e);
            }
        }

        /// <summary>Task83: Picks up firing directions from shot events and instructs the relevant
        /// units' visuals to "face the firing direction for FacingHoldSeconds" (main thread only.
        /// MilitaryManagerVisuals calls this after releasing the lock, with the same snapshot as
        /// CombatFx.Spawn).</summary>
        public static void NotifyShots(System.Collections.Generic.List<ShotEvent> shots)
        {
            try
            {
                for (int i = 0; i < shots.Count; i++)
                {
                    ShotEvent shot = shots[i];
                    if (shot.AttackerId == 0) continue;
                    // Task86: Aircraft always face their direction of travel (facing the firing
                    // direction would make them fly while pointing away from the flight direction — a
                    // "side-slip" look. Maneuvering is expressed through the pass-and-overfly of path
                    // traversal instead).
                    if (shot.Category.IsAircraft()) continue;

                    VisualEntry entry;
                    if (!_visuals.TryGetValue(shot.AttackerId, out entry)) continue;

                    Vector3 dir = new Vector3(shot.To.X - shot.From.X, 0f, shot.To.Z - shot.From.Z);
                    if (dir.sqrMagnitude < 1e-6f) continue; // shots straight up/at the same spot do not change facing

                    entry.FacingDirection = dir.normalized;
                    entry.FacingHoldUntil = Time.time + FacingHoldSeconds;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.NotifyShots error: " + e);
            }
        }

        // Task49: For UpdateFactionIcon/CreateFactionIcon see UnitVisualsFactionIcon.cs (same partial class).

        private static void DestroyVisual(uint instanceId)
        {
            try
            {
                VisualEntry entry;
                if (_visuals.TryGetValue(instanceId, out entry))
                {
                    if (entry != null && entry.GameObject != null)
                    {
                        UnityEngine.Object.Destroy(entry.GameObject);
                    }
                    _visuals.Remove(instanceId);
                    ModConfig.Log("UnitVisuals: destroyed visual for instance " + instanceId);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.DestroyVisual: instance " + instanceId + " error: " + e);
            }
        }
    }
}
