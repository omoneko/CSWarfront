using System;
using System.Collections.Generic;
using CSWarfront.Core;
using CSWarfront.Game.Models;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task108（ユーザー要望「線路上を移動するときは車体の連結部で線路に沿うように曲がってほしい」）:
    /// 軍用貨物列車の「連接表示」。
    ///
    /// モデル側が車両ごとの独立オブジェクトへ分割されている（2026-08-05にユーザーが更新。
    /// Unit_MilitaryTrain＝機関車＋Coach/Van/TankWagon/Flatの4両）。ここでは、
    ///   1. 生成時に後続車両のモデルを読み込み、編成順に「先頭からの距離」を割り当てる
    ///      （距離＝各車両の実寸＋連結間隔から算出。モデルを差し替えれば自動的に追従する）
    ///   2. 先頭が通った軌跡（Trail）を記録し、各車両を「先頭から自分の距離だけ後ろの軌跡上の点」へ、
    ///      その地点の接線方向で置く
    /// ことで、実際に走った線路の形に沿って編成が折れ曲がる。
    ///
    /// 後続車両のモデルが1つも読めない場合は何もしない（＝先頭車だけの従来表示。安全側フォールバック）。
    /// すべてメインスレッド専用（UnitVisualsと同じ規約）。
    /// </summary>
    public static partial class UnitVisuals
    {
        /// <summary>編成順（先頭車Unit_MilitaryTrainの後ろに、この順で連結する）。</summary>
        private static readonly string[] TrailingCarModels =
        {
            "Unit_MilitaryTrainCoach",
            "Unit_MilitaryTrainVan",
            "Unit_MilitaryTrainTankWagon",
            "Unit_MilitaryTrainFlat"
        };

        /// <summary>連結器のすき間（m）。車両どうしが密着して見えないようにする。</summary>
        private const float CouplingGap = 1.0f;

        /// <summary>軌跡を記録する間隔（m）。細かいほど曲線再現が滑らかだが点が増える。</summary>
        private const float TrailSampleSpacing = 2f;

        /// <summary>保持する軌跡点の最大数（TrailSampleSpacing×これ＝再現できる編成長の上限）。</summary>
        private const int MaxTrailPoints = 200;

        /// <summary>このTypeKeyは連接表示の対象か（現状は軍用貨物列車のみ）。</summary>
        private static bool IsArticulatedType(string typeKey)
        {
            UnitCategory category;
            byte tier;
            if (!TypeKeyParser.TryParse(typeKey, out category, out tier)) return false;
            return category == UnitCategory.MilitaryTrain;
        }

        /// <summary>後続車両をrootの子として生成し、各車両の「先頭からの距離」を返す。
        /// 1両も作れなければfalse（＝先頭車だけの従来表示）。</summary>
        private static bool TryBuildTrainCars(GameObject root, Mesh headMesh,
            out GameObject[] cars, out float[] behindHead)
        {
            cars = null;
            behindHead = null;

            try
            {
                var builtCars = new List<GameObject>();
                var offsets = new List<float>();

                // 先頭車の中心から後端までの距離を起点に、後ろへ積み上げていく。
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

                    // 車両GameObjectは毎フレーム軌跡上へワールド座標で置く。モデルの上下ピボット補正
                    // （底面をY=0に合わせる）は先頭車と同じく子の"Model"側で吸収する。
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

        /// <summary>毎フレーム（MoveVisualから）: 先頭の軌跡を記録し、各車両を軌跡上へ配置する。
        /// 軌跡がまだ足りない（出現直後など）ぶんは、現在の向きにまっすぐ並べてフォールバックする。</summary>
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

        /// <summary>軌跡（古い→新しい順）を先頭からdistanceだけ遡った点と、その地点での進行方向を返す。
        /// 軌跡がdistanceに満たなければfalse。</summary>
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
            return false; // 軌跡が足りない（出現直後）
        }
    }
}
