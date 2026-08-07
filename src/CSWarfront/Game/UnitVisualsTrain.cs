using System;
using System.Collections.Generic;
using CSWarfront.Core;
using CSWarfront.Game.Models;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task108 (user request "when moving on rails, the body should bend at the couplings to follow
    /// the track"): "Articulated rendering" for the military freight train.
    ///
    /// The model side has been split into independent per-car objects (updated by the user on
    /// 2026-08-05: Unit_MilitaryTrain = locomotive + the 4 cars Coach/Van/TankWagon/Flat). Here we
    ///   1. load the trailing car models at creation time and assign each a "distance from the head"
    ///      in consist order (the distance is derived from each car's actual size + coupling gap, so
    ///      it follows automatically if the models are swapped)
    ///   2. record the trail traced by the head (Trail), and place each car at "the point on the
    ///      trail that lies its own distance behind the head", oriented along the tangent at that
    ///      point
    /// so the consist bends along the shape of the track it actually traveled.
    ///
    /// If none of the trailing car models can be loaded, do nothing (= the previous head-car-only
    /// rendering; safe fallback). Everything is main thread only (same convention as UnitVisuals).
    /// </summary>
    public static partial class UnitVisuals
    {
        /// <summary>Consist order (coupled behind the head car Unit_MilitaryTrain in this order).</summary>
        private static readonly string[] TrailingCarModels =
        {
            "Unit_MilitaryTrainCoach",
            "Unit_MilitaryTrainVan",
            "Unit_MilitaryTrainTankWagon",
            "Unit_MilitaryTrainFlat"
        };

        /// <summary>Coupler gap (m). Keeps the cars from looking glued together.</summary>
        private const float CouplingGap = 1.0f;

        /// <summary>Trail sampling interval (m). Finer means smoother curve reproduction but more points.</summary>
        private const float TrailSampleSpacing = 2f;

        /// <summary>Maximum number of trail points kept (TrailSampleSpacing x this = the upper bound
        /// of consist length that can be reproduced).</summary>
        private const int MaxTrailPoints = 200;

        /// <summary>Whether this TypeKey is subject to articulated rendering (currently only the
        /// military freight train).</summary>
        private static bool IsArticulatedType(string typeKey)
        {
            UnitCategory category;
            byte tier;
            if (!TypeKeyParser.TryParse(typeKey, out category, out tier)) return false;
            return category == UnitCategory.MilitaryTrain;
        }

        /// <summary>Creates the trailing cars as children of root and returns each car's "distance
        /// from the head". Returns false if not even one car could be built (= the previous
        /// head-car-only rendering).</summary>
        private static bool TryBuildTrainCars(GameObject root, Mesh headMesh,
            out GameObject[] cars, out float[] behindHead)
        {
            cars = null;
            behindHead = null;

            try
            {
                var builtCars = new List<GameObject>();
                var offsets = new List<float>();

                // Starting from the distance between the head car's center and its rear end,
                // accumulate backwards.
                float cursor = headMesh.bounds.size.z * 0.5f;

                for (int i = 0; i < TrailingCarModels.Length; i++)
                {
                    Mesh mesh;
                    Material[] materials;
                    if (!WarfrontModelProvider.TryGetModel(TrailingCarModels[i], out mesh, out materials)) continue;
                    if (mesh == null) continue;

                    float length = mesh.bounds.size.z;
                    cursor += CouplingGap + length * 0.5f;

                    var carGo = new GameObject("Car_" + TrailingCarModels[i]);
                    carGo.transform.SetParent(root.transform, false);

                    // The car GameObject is placed on the trail in world coordinates every frame.
                    // The model's vertical pivot correction (aligning the bottom to Y=0) is absorbed
                    // by the child "Model", same as the head car.
                    var model = new GameObject("Model");
                    model.transform.SetParent(carGo.transform, false);
                    model.transform.localPosition = new Vector3(0f, -mesh.bounds.min.y, 0f);
                    MeshFilter filter = model.AddComponent<MeshFilter>();
                    filter.sharedMesh = mesh;
                    MeshRenderer renderer = model.AddComponent<MeshRenderer>();
                    if (materials != null && materials.Length > 0) renderer.sharedMaterials = materials;

                    builtCars.Add(carGo);
                    offsets.Add(cursor);
                    cursor += length * 0.5f;
                }

                if (builtCars.Count == 0) return false;

                cars = builtCars.ToArray();
                behindHead = offsets.ToArray();
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.TryBuildTrainCars: falling back to the locomotive only: " + e);
                return false;
            }
        }

        /// <summary>Every frame (from MoveVisual): records the head's trail and places each car on
        /// it. Where the trail is not yet long enough (e.g. right after spawn), falls back to lining
        /// the cars up straight along the current facing.</summary>
        private static void UpdateTrainCars(VisualEntry entry, Vector3 headPosition, Quaternion headRotation)
        {
            if (entry.Cars == null || entry.Cars.Length == 0) return;

            if (entry.Trail == null) entry.Trail = new List<Vector3>();
            if (entry.Trail.Count == 0 ||
                (entry.Trail[entry.Trail.Count - 1] - headPosition).sqrMagnitude >= TrailSampleSpacing * TrailSampleSpacing)
            {
                entry.Trail.Add(headPosition);
                if (entry.Trail.Count > MaxTrailPoints) entry.Trail.RemoveAt(0);
            }

            Vector3 headForward = headRotation * Vector3.forward;
            for (int i = 0; i < entry.Cars.Length; i++)
            {
                GameObject car = entry.Cars[i];
                if (car == null) continue;

                Vector3 pos;
                Vector3 forward;
                if (!TrySampleTrail(entry.Trail, headPosition, entry.CarBehindHead[i], out pos, out forward))
                {
                    pos = headPosition - headForward * entry.CarBehindHead[i];
                    forward = headForward;
                }
                car.transform.position = pos;
                if (forward.sqrMagnitude > 1e-6f) car.transform.rotation = Quaternion.LookRotation(forward);
            }
        }

        /// <summary>Returns the point reached by walking distance back from the head along the trail
        /// (ordered old to new), and the direction of travel at that point. Returns false if the
        /// trail is shorter than distance.</summary>
        private static bool TrySampleTrail(List<Vector3> trail, Vector3 head, float distance,
            out Vector3 position, out Vector3 forward)
        {
            position = head;
            forward = Vector3.forward;
            if (distance <= 0.01f) return false;
            if (trail == null || trail.Count < 2) return false;

            float remaining = distance;
            Vector3 current = head;
            for (int i = trail.Count - 1; i >= 0; i--)
            {
                Vector3 previous = trail[i];
                Vector3 delta = current - previous;
                float len = delta.magnitude;
                if (len <= 1e-4f) { current = previous; continue; }

                if (len >= remaining)
                {
                    position = current - delta / len * remaining;
                    forward = delta / len;
                    return true;
                }
                remaining -= len;
                current = previous;
            }
            return false; // trail not long enough (right after spawn)
        }
    }
}
