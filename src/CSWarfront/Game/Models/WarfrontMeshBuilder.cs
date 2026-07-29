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
                built.RecalculateNormals();
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
    }
}
