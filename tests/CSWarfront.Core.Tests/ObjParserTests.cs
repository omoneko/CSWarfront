using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

// Task57: MissileDisaster.Core.ObjParser を CSWarfront.Core へポートしたものの検証。
// テスト内容は移植元 MissileDisaster.Core.Tests.ObjParserTests と同一（挙動は変更していない）。
public class ObjParserTests
{
    [Fact]
    public void Triangle_position_x_is_negated_and_indices_are_zero_based()
    {
        string obj =
            "v 1.0 2.0 3.0\n" +
            "v 4.0 5.0 6.0\n" +
            "v 7.0 8.0 9.0\n" +
            "usemtl Mat1\n" +
            "f 1 2 3\n";

        ObjData data = ObjParser.Parse(obj);

        Assert.Equal(3, data.VertexCount);
        Assert.Equal(new float[] { -1.0f, 2.0f, 3.0f, -4.0f, 5.0f, 6.0f, -7.0f, 8.0f, 9.0f }, data.Positions);

        Assert.Single(data.Submeshes);
        ObjSubmesh sm = data.Submeshes[0];
        Assert.Equal("Mat1", sm.Material);

        // Winding is reversed: (a,b,c)=(0,1,2) becomes (c,b,a)=(2,1,0).
        Assert.Equal(new List<int> { 2, 1, 0 }, sm.Triangles);
    }

    [Fact]
    public void Quad_face_is_fan_triangulated_with_reversed_winding()
    {
        string obj =
            "v 0 0 0\n" +
            "v 1 0 0\n" +
            "v 1 1 0\n" +
            "v 0 1 0\n" +
            "usemtl Mat1\n" +
            "f 1 2 3 4\n";

        ObjData data = ObjParser.Parse(obj);

        Assert.Single(data.Submeshes);
        ObjSubmesh sm = data.Submeshes[0];

        // Fan: (0,1,2) and (0,2,3), each reversed to (c,b,a).
        Assert.Equal(new List<int> { 2, 1, 0, 3, 2, 0 }, sm.Triangles);
    }

    [Fact]
    public void Hexagon_face_is_fan_triangulated_into_four_triangles()
    {
        // Used by tools/gen_models.py for prism caps (6-sided barrel/canister approximations).
        string obj =
            "v 0 0 0\nv 1 0 0\nv 1 1 0\nv 1 1 1\nv 0 1 1\nv 0 0 1\n" +
            "usemtl Mat1\n" +
            "f 1 2 3 4 5 6\n";

        ObjData data = ObjParser.Parse(obj);

        Assert.Single(data.Submeshes);
        // 6-gon fan-triangulates into (n-2)=4 triangles = 12 indices.
        Assert.Equal(12, data.Submeshes[0].Triangles.Count);
    }

    [Fact]
    public void Multiple_usemtl_groups_create_separate_submeshes()
    {
        string obj =
            "v 0 0 0\n" +
            "v 1 0 0\n" +
            "v 0 1 0\n" +
            "usemtl MatA\n" +
            "f 1 2 3\n" +
            "usemtl MatB\n" +
            "f 1 3 2\n";

        ObjData data = ObjParser.Parse(obj);

        Assert.Equal(2, data.Submeshes.Count);

        Assert.Equal("MatA", data.Submeshes[0].Material);
        Assert.Equal(new List<int> { 2, 1, 0 }, data.Submeshes[0].Triangles);

        Assert.Equal("MatB", data.Submeshes[1].Material);
        Assert.Equal(new List<int> { 1, 2, 0 }, data.Submeshes[1].Triangles);
    }

    [Fact]
    public void Faces_before_any_usemtl_go_into_default_submesh_with_empty_material()
    {
        string obj =
            "v 0 0 0\n" +
            "v 1 0 0\n" +
            "v 0 1 0\n" +
            "f 1 2 3\n" +
            "usemtl Mat1\n" +
            "f 1 2 3\n";

        ObjData data = ObjParser.Parse(obj);

        Assert.Equal(2, data.Submeshes.Count);
        Assert.Equal("", data.Submeshes[0].Material);
        Assert.Equal(new List<int> { 2, 1, 0 }, data.Submeshes[0].Triangles);
        Assert.Equal("Mat1", data.Submeshes[1].Material);
    }

    [Fact]
    public void CRLF_line_endings_parse_identically_to_LF()
    {
        string lf =
            "v 1.0 2.0 3.0\n" +
            "v 4.0 5.0 6.0\n" +
            "v 7.0 8.0 9.0\n" +
            "usemtl Mat1\n" +
            "f 1 2 3\n";
        string crlf = lf.Replace("\n", "\r\n");

        ObjData a = ObjParser.Parse(lf);
        ObjData b = ObjParser.Parse(crlf);

        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Submeshes[0].Material, b.Submeshes[0].Material);
        Assert.Equal(a.Submeshes[0].Triangles, b.Submeshes[0].Triangles);
    }

    [Fact]
    public void NonAscii_material_name_with_dot_is_preserved_exactly()
    {
        string obj =
            "v 0 0 0\n" +
            "v 1 0 0\n" +
            "v 0 1 0\n" +
            "usemtl マテリアル.001\n" +
            "f 1 2 3\n";

        ObjData data = ObjParser.Parse(obj);

        Assert.Single(data.Submeshes);
        Assert.Equal("マテリアル.001", data.Submeshes[0].Material);
    }

    [Fact]
    public void Face_vertex_refs_use_only_the_position_index_before_first_slash()
    {
        string obj =
            "v 0 0 0\n" +
            "v 1 0 0\n" +
            "v 0 1 0\n" +
            "vt 0.5 0.5\n" +
            "vn 0.0 1.0 0.0\n" +
            "usemtl Mat1\n" +
            "f 1/99/5 2/1/1 3/2/2\n";

        ObjData data = ObjParser.Parse(obj);

        Assert.Equal(new List<int> { 2, 1, 0 }, data.Submeshes[0].Triangles);
    }

    [Fact]
    public void Negative_relative_face_indices_resolve_to_the_same_vertices_as_positive_ones()
    {
        string positive =
            "v 0 0 0\n" +
            "v 1 0 0\n" +
            "v 0 1 0\n" +
            "usemtl Mat1\n" +
            "f 1 2 3\n";
        string negative =
            "v 0 0 0\n" +
            "v 1 0 0\n" +
            "v 0 1 0\n" +
            "usemtl Mat1\n" +
            "f -3 -2 -1\n";

        ObjData a = ObjParser.Parse(positive);
        ObjData b = ObjParser.Parse(negative);

        Assert.Equal(a.Submeshes[0].Triangles, b.Submeshes[0].Triangles);
    }

    [Fact]
    public void Empty_input_returns_empty_data_without_throwing()
    {
        ObjData data = ObjParser.Parse("");

        Assert.Empty(data.Positions);
        Assert.Empty(data.Submeshes);
        Assert.Equal(0, data.VertexCount);
    }

    [Fact]
    public void Malformed_numeric_line_is_skipped_without_throwing()
    {
        string obj =
            "v not a number\n" +
            "v 1.0 2.0 3.0\n" +
            "v 4.0 5.0 6.0\n" +
            "v 7.0 8.0 9.0\n" +
            "usemtl Mat1\n" +
            "f 1 2 3\n";

        ObjData data = ObjParser.Parse(obj);

        Assert.Equal(3, data.VertexCount);
        Assert.Equal(-1.0f, data.Positions[0]);
    }
}
