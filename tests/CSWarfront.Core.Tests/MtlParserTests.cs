using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

// Task57: verifies the port of MissileDisaster.Core.MtlParser into CSWarfront.Core.
// The test content is identical to the source MissileDisaster.Core.Tests.MtlParserTests (behavior unchanged).
public class MtlParserTests
{
    [Fact]
    public void Kd_and_d_populate_color_and_alpha()
    {
        string mtl =
            "newmtl Body\n" +
            "Kd 0.1 0.2 0.3\n" +
            "d 0.5\n";

        Dictionary<string, MtlColor> map = MtlParser.Parse(mtl);

        Assert.True(map.ContainsKey("Body"));
        MtlColor c = map["Body"];
        Assert.Equal(0.1f, c.R, 3);
        Assert.Equal(0.2f, c.G, 3);
        Assert.Equal(0.3f, c.B, 3);
        Assert.Equal(0.5f, c.Alpha, 3);
    }

    [Fact]
    public void Missing_Kd_defaults_to_opaque_white()
    {
        Dictionary<string, MtlColor> map = MtlParser.Parse("newmtl Plain\n");

        MtlColor c = map["Plain"];
        Assert.Equal(1f, c.R, 3);
        Assert.Equal(1f, c.G, 3);
        Assert.Equal(1f, c.B, 3);
        Assert.Equal(1f, c.Alpha, 3);
    }

    [Fact]
    public void NonAscii_material_names_are_kept()
    {
        string mtl =
            "newmtl Matériau\n" +
            "Kd 0.035 0.035 0.035\n" +
            "newmtl Matériau.001\n" +
            "Kd 0.536 0.536 0.536\n";

        Dictionary<string, MtlColor> map = MtlParser.Parse(mtl);

        Assert.Equal(2, map.Count);
        Assert.Equal(0.035f, map["Matériau"].R, 3);
        Assert.Equal(0.536f, map["Matériau.001"].R, 3);
    }

    [Fact]
    public void Ka_Ks_Ns_illum_lines_are_ignored_without_throwing()
    {
        string mtl =
            "newmtl M\n" +
            "Ns 159.9\n" +
            "Ka 0.85 0.85 0.85\n" +
            "Kd 0.2 0.2 0.2\n" +
            "Ks 0.5 0.5 0.5\n" +
            "Ni 1.5\n" +
            "illum 3\n";

        Dictionary<string, MtlColor> map = MtlParser.Parse(mtl);

        Assert.Single(map);
        Assert.Equal(0.2f, map["M"].R, 3);
        Assert.Equal(1f, map["M"].Alpha, 3);
    }

    [Fact]
    public void Empty_input_returns_empty_map_without_throwing()
    {
        Assert.Empty(MtlParser.Parse(""));
    }
}
