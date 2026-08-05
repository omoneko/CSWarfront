using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task108（ユーザー要望「線路上を移動するときは車体の連結部で線路に沿うように曲がってほしい」）:
    /// 軍用貨物列車の「連接表示」。
    ///
    /// 列車モデルは全長約100mの一体メッシュなので、そのまま1つの剛体として置くとカーブで線路から
    /// 大きくはみ出す（機関車と最後尾が同じ向きを向いてしまう）。ここでは、
    ///   1. 生成時にメッシュを「車両ごと」に切り分ける（連結部＝ジオメトリが存在しないZ方向の隙間で分割）
    ///   2. 先頭が通った軌跡（Trail）を記録し、各車両を「先頭から自分の距離だけ後ろの軌跡上の点」へ置く
    /// ことで、実際に走った線路の形に沿って車体が折れ曲がるようにする。
    ///
    /// メッシュが読めない（isReadable=false のCSアセット等）／車両の切れ目が見つからない場合は、
    /// 何もせず従来どおり一体の剛体として描画する（安全側フォールバック）。
    /// すべてメインスレッド専用（UnitVisualsと同じ規約）。
    /// </summary>
    public static partial class UnitVisuals
    {
        /// <summary>軌跡を記録する間隔（m）。細かいほど曲線再現が滑らかだが点が増える。</summary>
        private const float TrailSampleSpacing = 2f;

        /// <summary>保持する軌跡点の最大数（TrailSampleSpacing×これ＝再現できる編成長の上限）。</summary>
        private const int MaxTrailPoints = 200;

        /// <summary>車両の切れ目とみなすZ方向の空白の長さ（m）。連結部の隙間はこれ以上あるとみなす。</summary>
        private const float MinCarGap = 0.8f;

        /// <summary>車両分割の走査に使うZ方向のビン幅（m）。</summary>
        private const float CarBinSize = 0.5f;

        /// <summary>分割後の車両数の上限（極端なメッシュでの暴発防止）。</summary>
        private const int MaxCars = 16;

        /// <summary>このTypeKeyは連接表示の対象か（現状は軍用貨物列車のみ）。</summary>
        private static bool IsArticulatedType(string typeKey)
        {
            UnitCategory category;
            byte tier;
            if (!TypeKeyParser.TryParse(typeKey, out category, out tier)) return false;
            return category == UnitCategory.MilitaryTrain;
        }

        /// <summary>メッシュを車両ごとに切り分け、各車両のGameObjectをrootの子として生成する。
        /// 成功したらtrueを返し、entryへ車両と「先頭からの距離」を格納する（呼び出し側は一体表示の
        /// レンダラーを止めること）。分割できなければfalse（従来どおり一体表示のまま）。</summary>
        private static bool TryBuildTrainCars(GameObject root, Mesh mesh, Material[] materials, Material single,
            float pivotOffsetY, out GameObject[] cars, out float[] behindHead)
        {
            cars = null;
            behindHead = null;

            try
            {
                Vector3[] vertices = mesh.vertices; // isReadable=falseならここで例外→フォールバック
                if (vertices == null || vertices.Length == 0) return false;

                List<float[]> slices = FindCarSlices(mesh, vertices);
                if (slices == null || slices.Count < 2) return false;

                float frontZ = mesh.bounds.max.z;
                var builtCars = new List<GameObject>();
                var offsets = new List<float>();

                for (int i = 0; i < slices.Count; i++)
                {
                    float minZ = slices[i][0], maxZ = slices[i][1];
                    float centreZ = (minZ + maxZ) * 0.5f;

                    Mesh carMesh = BuildSliceMesh(mesh, vertices, minZ, maxZ, pivotOffsetY, centreZ);
                    if (carMesh == null) continue;

                    var carGo = new GameObject("Car" + i);
                    carGo.transform.SetParent(root.transform, false);
                    MeshFilter filter = carGo.AddComponent<MeshFilter>();
                    filter.sharedMesh = carMesh;
                    MeshRenderer renderer = carGo.AddComponent<MeshRenderer>();
                    if (materials != null && materials.Length > 0) renderer.sharedMaterials = materials;
                    else if (single != null) renderer.sharedMaterial = single;

                    builtCars.Add(carGo);
                    offsets.Add(frontZ - centreZ); // 先頭からこの距離だけ後ろを走る
                }

                if (builtCars.Count < 2)
                {
                    for (int i = 0; i < builtCars.Count; i++) UnityEngine.Object.Destroy(builtCars[i]);
                    return false;
                }

                cars = builtCars.ToArray();
                behindHead = offsets.ToArray();
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitVisuals.TryBuildTrainCars: falling back to a rigid body: " + e.Message);
                return false;
            }
        }

        /// <summary>三角形の重心Zのヒストグラムから「ジオメトリが無いZ帯＝連結部」を見つけ、
        /// 車両ごとの[minZ, maxZ]の並びを返す（前から後ろの順）。</summary>
        private static List<float[]> FindCarSlices(Mesh mesh, Vector3[] vertices)
        {
            float minZ = mesh.bounds.min.z, maxZ = mesh.bounds.max.z;
            float span = maxZ - minZ;
            if (span <= MinCarGap * 2f) return null;

            int binCount = Mathf.Clamp(Mathf.CeilToInt(span / CarBinSize), 1, 4096);
            var occupied = new bool[binCount];

            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                int[] tris = mesh.GetTriangles(sub);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    float z = (vertices[tris[t]].z + vertices[tris[t + 1]].z + vertices[tris[t + 2]].z) / 3f;
                    int bin = Mathf.Clamp((int)((z - minZ) / span * binCount), 0, binCount - 1);
                    occupied[bin] = true;
                }
            }

            int gapBins = Mathf.Max(1, Mathf.CeilToInt(MinCarGap / (span / binCount)));
            var slices = new List<float[]>();
            int runStart = -1;
            int emptyRun = 0;
            for (int b = 0; b < binCount; b++)
            {
                if (occupied[b])
                {
                    if (runStart < 0) runStart = b;
                    emptyRun = 0;
                }
                else if (runStart >= 0)
                {
                    emptyRun++;
                    if (emptyRun >= gapBins)
                    {
                        AddSlice(slices, minZ, span, binCount, runStart, b - emptyRun);
                        runStart = -1;
                        emptyRun = 0;
                    }
                }
            }
            if (runStart >= 0) AddSlice(slices, minZ, span, binCount, runStart, binCount - 1);

            if (slices.Count > MaxCars) return null;
            slices.Reverse(); // 前（+Z）から後ろの順に並べ替える
            return slices;
        }

        private static void AddSlice(List<float[]> slices, float minZ, float span, int binCount, int firstBin, int lastBin)
        {
            float binSize = span / binCount;
            slices.Add(new[] { minZ + firstBin * binSize, minZ + (lastBin + 1) * binSize });
        }

        /// <summary>指定Z範囲の三角形だけを含むメッシュを作る。頂点配列は全体を使い回し
        /// （未参照頂点があっても描画に影響しない）、原点が車両の中心＆底面になるよう平行移動する。
        /// サブメッシュ構成は元のまま保つ＝マテリアル割り当てがそのまま通る。</summary>
        private static Mesh BuildSliceMesh(Mesh source, Vector3[] vertices, float minZ, float maxZ,
            float pivotOffsetY, float centreZ)
        {
            var shifted = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                shifted[i] = new Vector3(vertices[i].x, vertices[i].y + pivotOffsetY, vertices[i].z - centreZ);

            var carMesh = new Mesh();
            carMesh.vertices = shifted;
            Vector3[] normals = source.normals;
            if (normals != null && normals.Length == vertices.Length) carMesh.normals = normals;
            Vector2[] uv = source.uv;
            if (uv != null && uv.Length == vertices.Length) carMesh.uv = uv;
            carMesh.subMeshCount = source.subMeshCount;

            bool any = false;
            for (int sub = 0; sub < source.subMeshCount; sub++)
            {
                int[] tris = source.GetTriangles(sub);
                var kept = new List<int>();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    float z = (vertices[tris[t]].z + vertices[tris[t + 1]].z + vertices[tris[t + 2]].z) / 3f;
                    if (z < minZ || z > maxZ) continue;
                    kept.Add(tris[t]); kept.Add(tris[t + 1]); kept.Add(tris[t + 2]);
                }
                if (kept.Count > 0) any = true;
                carMesh.SetTriangles(kept.ToArray(), sub);
            }

            if (!any) { UnityEngine.Object.Destroy(carMesh); return null; }
            carMesh.RecalculateBounds();
            if (source.normals == null || source.normals.Length != vertices.Length) carMesh.RecalculateNormals();
            return carMesh;
        }

        /// <summary>ビジュアル破棄時: 車両ごとに生成したMeshを解放する（GameObjectを消しても
        /// Meshは自動では解放されないため）。</summary>
        private static void DestroyTrainCarMeshes(VisualEntry entry)
        {
            if (entry == null || entry.Cars == null) return;
            for (int i = 0; i < entry.Cars.Length; i++)
            {
                if (entry.Cars[i] == null) continue;
                MeshFilter filter = entry.Cars[i].GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null) UnityEngine.Object.Destroy(filter.sharedMesh);
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
