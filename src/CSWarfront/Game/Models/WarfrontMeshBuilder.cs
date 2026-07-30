using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Models
{
    /// <summary>
    /// Core が解析した ObjData から、実行時に Unity のマルチサブメッシュ Mesh + サブメッシュごとの
    /// .mtl 色マテリアル配列を構築する（Task69、MissileDisaster.Game.Models.MissileMeshBuilder.
    /// TryBuild を縮小移植）。呼び出し元は <see cref="WarfrontModelProvider.TryGetModel"/> のみ
    /// （ユニットの既定モデル解決経路、<see cref="UnitMeshSource"/>参照）。
    /// Task82: 拠点（電力タブの複製プレハブ、WarfrontBasePrefab）専用だった単一サブメッシュ統合版
    /// TryBuildMergedMesh は、複製プレハブ機構自体の完全撤去に伴い呼び出し元が無くなったため削除した。
    /// Mesh の生成は Unity のメインスレッドでのみ許可されるため、必ずメインスレッド
    /// （GameObject を生成する箇所と同じスレッド）から呼ぶこと。
    /// </summary>
    internal static class WarfrontMeshBuilder
    {
        /// <summary>
        /// Task69: ObjData の各サブメッシュ（.obj の usemtl ブロック単位）をそのまま Unity の
        /// サブメッシュへ対応させ、サブメッシュごとに .mtl の Kd 色を塗った自前の Standard シェーダ
        /// マテリアルを1枚ずつ生成する（MissileDisaster.Game.Models.MissileMeshBuilder.TryBuild を
        /// そのまま移植。挙動変更なし、namespace のみ変更）。
        /// 呼び出し側（<see cref="WarfrontModelProvider.TryGetModel"/>）がキャッシュするため、
        /// ここでは生成のみ行いキャッシュはしない。
        /// </summary>
        public static bool TryBuild(ObjData obj, Dictionary<string, MtlColor> mtl, Color fallbackColor, out Mesh mesh, out Material[] materials)
        {
            mesh = null;
            materials = null;

            try
            {
                if (obj == null || obj.Positions == null || obj.Submeshes == null) return false;
                int vertexCount = obj.VertexCount;
                if (vertexCount <= 0 || obj.Submeshes.Count == 0) return false;

                var vertices = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    vertices[i] = new Vector3(
                        obj.Positions[i * 3],
                        obj.Positions[i * 3 + 1],
                        obj.Positions[i * 3 + 2]);
                }

                var builtMesh = new Mesh();
                builtMesh.vertices = vertices;
                builtMesh.subMeshCount = obj.Submeshes.Count;

                var mats = new Material[obj.Submeshes.Count];

                for (int s = 0; s < obj.Submeshes.Count; s++)
                {
                    ObjSubmesh sub = obj.Submeshes[s];
                    List<int> validTriangles = FilterValidTriangles(sub != null ? sub.Triangles : null, vertexCount);
                    builtMesh.SetTriangles(validTriangles, s);
                    mats[s] = BuildMaterial(sub != null ? sub.Material : null, mtl, fallbackColor);
                }

                builtMesh.RecalculateNormals();
                builtMesh.RecalculateBounds();

                mesh = builtMesh;
                materials = mats;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontMeshBuilder.TryBuild error: " + e);
                mesh = null;
                materials = null;
                return false;
            }
        }

        /// <summary>破損/範囲外インデックスの三角形を除去する。Unity の SetTriangles は範囲外
        /// インデックスがあると例外を投げるため、必ずこのフィルタを通してから渡す。</summary>
        private static List<int> FilterValidTriangles(List<int> triangles, int vertexCount)
        {
            if (triangles == null || triangles.Count == 0) return new List<int>();

            var valid = new List<int>(triangles.Count);
            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];
                if (a < 0 || a >= vertexCount) continue;
                if (b < 0 || b >= vertexCount) continue;
                if (c < 0 || c >= vertexCount) continue;

                valid.Add(a);
                valid.Add(b);
                valid.Add(c);
            }
            return valid;
        }

        /// <summary>Task69: MissileMeshBuilder.BuildMaterial を移植。1サブメッシュぶんの .mtl 色を
        /// 自前の Standard シェーダマテリアルへ塗る。materialName が .mtl に無い/mtl自体がnullなら
        /// fallbackColor を使う（tools/gen_models.py 由来の単色モデルにも .mtl は必ず付くため、
        /// このフォールバックは理論上のみ・実運用では常に .mtl の Kd がヒットする）。</summary>
        private static Material BuildMaterial(string materialName, Dictionary<string, MtlColor> mtl, Color fallbackColor)
        {
            Material mat = CreateBaseMaterial();
            if (mat == null) return null;

            try
            {
                float r = fallbackColor.r, g = fallbackColor.g, b = fallbackColor.b;

                MtlColor found;
                if (mtl != null && !string.IsNullOrEmpty(materialName) && mtl.TryGetValue(materialName, out found) && found != null)
                {
                    r = found.R;
                    g = found.G;
                    b = found.B;
                }

                Color color = new Color(r, g, b, 1f);
                mat.color = color;
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", ModConfig.ObjMetallic);
                if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", ModConfig.ObjGlossiness);
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontMeshBuilder.BuildMaterial error: " + e);
            }

            return mat;
        }

        private static Material CreateBaseMaterial()
        {
            try
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                if (shader == null) shader = Shader.Find("Diffuse");
                if (shader == null) return null;
                return new Material(shader);
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontMeshBuilder.CreateBaseMaterial error: " + e);
                return null;
            }
        }
    }
}
