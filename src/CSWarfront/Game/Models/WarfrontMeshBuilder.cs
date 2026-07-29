using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game.Models
{
    /// <summary>
    /// Core が解析した ObjData から、実行時に Unity の単一サブメッシュ Mesh を構築する
    /// （Task57、MissileDisaster.Game.Models.MissileMeshBuilder.TryBuildMergedMesh を縮小移植）。
    /// マテリアルはこのビルダーでは作らない: ユニットは <see cref="UnitMaterialFactory"/> の勢力色
    /// マテリアルを、建物（軍事基地）も同ファクトリの自前マテリアルを、それぞれ呼び出し側が別途
    /// 生成して割り当てる（<see cref="UnitVisuals"/> は MeshRenderer.sharedMaterial を1枚しか
    /// 割り当てないため、そもそも複数サブメッシュ/複数マテリアルの構成にする意味がない）。
    /// tools/gen_models.py が出力する src/CSWarfront/Models/*.obj は usemtl を1回しか使わない
    /// （＝ObjParserの結果は常に単一サブメッシュ）ため、全三角形を1サブメッシュへ結合するだけで
    /// 元データを失わない。
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
        /// tools/export_builtin_obj.py が書き出す Blender 由来モデルは複数の usemtl ブロック
        /// （＝複数サブメッシュ）を持つため、<see cref="TryBuildMergedMesh"/>（全サブメッシュを1つに
        /// 潰す、tools/gen_models.py 由来の単色モデル/建物用）とは別に用意する。
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

        /// <summary>ObjData の全サブメッシュの三角形を単一サブメッシュへ統合した Mesh を構築する。
        /// 失敗時は false を返し mesh は null。</summary>
        public static bool TryBuildMergedMesh(ObjData obj, out Mesh mesh)
        {
            mesh = null;
            try
            {
                if (obj == null || obj.Positions == null || obj.Submeshes == null) return false;
                int vertexCount = obj.VertexCount;
                if (vertexCount <= 0) return false;

                var vertices = new Vector3[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    vertices[i] = new Vector3(
                        obj.Positions[i * 3],
                        obj.Positions[i * 3 + 1],
                        obj.Positions[i * 3 + 2]);
                }

                var allTris = new List<int>();
                for (int s = 0; s < obj.Submeshes.Count; s++)
                {
                    ObjSubmesh sub = obj.Submeshes[s];
                    allTris.AddRange(FilterValidTriangles(sub != null ? sub.Triangles : null, vertexCount));
                }
                if (allTris.Count == 0) return false;

                var built = new Mesh();
                built.vertices = vertices;
                built.subMeshCount = 1;
                built.SetTriangles(allTris, 0);
                // Task71: BuildingInfoBase.CalculateGeneratedInfo/InitMeshData（ゲーム本体、ilspycmdで
                // 逆コンパイルして確認済み）は Mesh.uv / Mesh.tangents が頂点数と同じ長さの配列である
                // ことを無条件に前提にしている（無いと IndexOutOfRangeException や
                // PrefabException("LOD has no tangents") になる）。この単一マテリアルの単色モデルでは
                // UV自体の値に意味は無い（マテリアルにテクスチャを貼らない）ため、長さだけ揃える
                // ゼロ埋めUVで十分。
                built.uv = new Vector2[vertices.Length];
                built.RecalculateNormals();
                built.RecalculateTangents();
                built.RecalculateBounds();

                mesh = built;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("WarfrontMeshBuilder.TryBuildMergedMesh error: " + e);
                mesh = null;
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
