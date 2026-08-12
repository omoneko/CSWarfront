using System.Collections.Generic;
using System.IO;
using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task126: shape-based turret detection. Besides synthetic shapes, this runs against the
/// project's real bundled models — the detector has to accept the tank and reject the trucks,
/// aircraft and buildings a subscriber might assign.</summary>
public class TurretDetectionTests
{
    // --- synthetic builders (a box is enough: the detector only looks at extents) ---

    private class MeshBuilder
    {
        public readonly List<float> V = new List<float>();
        public readonly List<int> T = new List<int>();

        public void Box(float cx, float cy, float cz, float sx, float sy, float sz)
        {
            int b = V.Count / 3;
            for (int i = 0; i < 8; i++)
            {
                V.Add(cx + ((i & 1) == 0 ? -sx : sx) / 2f);
                V.Add(cy + ((i & 2) == 0 ? -sy : sy) / 2f);
                V.Add(cz + ((i & 4) == 0 ? -sz : sz) / 2f);
            }
            int[] faces = { 0,1,2, 1,3,2, 4,6,5, 5,6,7, 0,2,4, 2,6,4, 1,5,3, 3,5,7, 0,4,1, 1,4,5, 2,3,6, 3,7,6 };
            foreach (int f in faces) T.Add(b + f);
        }

        public TurretSplit Detect() { return TurretDetection.Detect(V.ToArray(), T.ToArray()); }
    }

    private static MeshBuilder Tank()
    {
        var m = new MeshBuilder();
        m.Box(0f, 1f, 0f, 3.6f, 2f, 10f);      // hull: wide and long
        m.Box(-1.4f, 0.6f, 0f, 0.8f, 1.2f, 9f); // tracks - what makes a tank hull wide, and most of
        m.Box(1.4f, 0.6f, 0f, 0.8f, 1.2f, 9f);  // its geometry (the turret must not dominate)
        m.Box(0f, 3f, 0.5f, 2f, 2f, 4f);       // turret: clearly narrower, above
        m.Box(0f, 3f, 4.5f, 0.4f, 0.4f, 5f);   // barrel: thin, reaching forward
        return m;
    }

    [Fact]
    public void Detects_the_turret_of_a_tank_shaped_mesh()
    {
        TurretSplit split = Tank().Detect();

        Assert.True(split.Found);
        Assert.InRange(split.SplitY, 1.8f, 2.4f);   // at the hull roof, between hull and turret
        Assert.InRange(split.PivotX, -0.2f, 0.2f);  // on the centreline
        Assert.InRange(split.PivotZ, -1f, 2.5f);    // near the turret ring, not out at the muzzle
        Assert.Equal(1f, split.BarrelSign);
    }

    [Fact]
    public void Reports_a_rear_facing_barrel_so_the_renderer_can_compensate()
    {
        var m = new MeshBuilder();
        m.Box(0f, 1f, 0f, 3.6f, 2f, 10f);
        m.Box(-1.4f, 0.6f, 0f, 0.8f, 1.2f, 9f);
        m.Box(1.4f, 0.6f, 0f, 0.8f, 1.2f, 9f);
        m.Box(0f, 3f, -0.5f, 2f, 2f, 4f);
        m.Box(0f, 3f, -4.5f, 0.4f, 0.4f, 5f);   // barrel toward -Z

        TurretSplit split = m.Detect();

        Assert.True(split.Found);
        Assert.Equal(-1f, split.BarrelSign);
    }

    [Fact]
    public void Rejects_a_hull_without_a_barrel()
    {
        var m = new MeshBuilder();
        m.Box(0f, 1f, 0f, 3.6f, 2f, 10f);     // hull
        m.Box(0f, 3f, 0f, 2f, 2f, 4f);        // superstructure, no gun: an APC
        Assert.False(m.Detect().Found);
    }

    [Fact]
    public void Rejects_a_plain_box_and_a_thin_mast()
    {
        var box = new MeshBuilder();
        box.Box(0f, 1.5f, 0f, 3f, 3f, 8f);
        Assert.False(box.Detect().Found);

        var mast = new MeshBuilder();
        mast.Box(0f, 1f, 0f, 3.6f, 2f, 10f);
        mast.Box(0f, 3f, 0f, 0.2f, 2f, 0.2f);   // antenna, far too thin to be a turret
        mast.Box(0f, 3f, 3f, 0.1f, 0.1f, 5f);
        Assert.False(mast.Detect().Found);
    }

    [Fact]
    public void Handles_degenerate_input_without_throwing()
    {
        Assert.False(TurretDetection.Detect(null, null).Found);
        Assert.False(TurretDetection.Detect(new float[0], new int[0]).Found);
        Assert.False(TurretDetection.Detect(new float[] { 0, 0, 0 }, new int[] { 0, 0, 0 }).Found);
    }

    // --- the real bundled models ---

    private static string ModelsDir()
    {
        // tests/<proj>/bin/<cfg>/<tfm> -> repo root
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, Path.Combine("src", Path.Combine("CSWarfront", "Models")));
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool TryLoadObj(string path, out float[] vertices, out int[] triangles)
    {
        var v = new List<float>();
        var t = new List<int>();
        foreach (string line in File.ReadAllLines(path))
        {
            string[] p = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            if (p[0] == "v" && p.Length >= 4)
            {
                v.Add(float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
                v.Add(float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture));
                v.Add(float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (p[0] == "f" && p.Length >= 4)
            {
                var idx = new List<int>();
                for (int i = 1; i < p.Length; i++)
                    idx.Add(int.Parse(p[i].Split('/')[0], System.Globalization.CultureInfo.InvariantCulture) - 1);
                for (int i = 1; i < idx.Count - 1; i++) { t.Add(idx[0]); t.Add(idx[i]); t.Add(idx[i + 1]); }
            }
        }
        vertices = v.ToArray();
        triangles = t.ToArray();
        return vertices.Length > 0 && triangles.Length > 0;
    }

    private static bool DetectModel(string name)
    {
        string dir = ModelsDir();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, name + ".obj");
        Assert.True(File.Exists(path), path + " is missing");
        float[] v; int[] t;
        Assert.True(TryLoadObj(path, out v, out t));
        return TurretDetection.Detect(v, t).Found;
    }

    [Theory]
    [InlineData("Unit_Tank")]
    [InlineData("Unit_Artillery")] // an SPG turns its gun too
    public void Accepts_the_turreted_vehicles_among_the_bundled_models(string model)
    {
        Assert.True(DetectModel(model), model + " should be detected as turreted");
    }

    [Theory]
    [InlineData("Unit_Apc")]
    [InlineData("Unit_SupplyTruck")]
    [InlineData("Unit_MechInfantry")]
    [InlineData("Unit_Infantry")]
    [InlineData("Unit_Fighter")]
    [InlineData("Unit_TransportHeli")]
    [InlineData("Unit_MilitaryTrain")]
    [InlineData("Building_MilitaryBase")]
    [InlineData("Fort_Bunker")]
    public void Rejects_everything_that_has_no_turret(string model)
    {
        Assert.False(DetectModel(model), model + " should not be detected as turreted");
    }

    [Fact]
    public void Only_gun_traversing_categories_are_offered_to_the_detector()
    {
        // The gate that stops an unlucky silhouette on some other unit from being taken apart.
        Assert.True(TurretRules.CanHaveTurret(UnitCategory.Tank));
        Assert.True(TurretRules.CanHaveTurret(UnitCategory.Artillery));
        Assert.True(TurretRules.CanHaveTurret(UnitCategory.AntiAir));

        Assert.False(TurretRules.CanHaveTurret(UnitCategory.Apc));
        Assert.False(TurretRules.CanHaveTurret(UnitCategory.SupplyTruck));
        Assert.False(TurretRules.CanHaveTurret(UnitCategory.Infantry));
        Assert.False(TurretRules.CanHaveTurret(UnitCategory.AirSuperiority));
        Assert.False(TurretRules.CanHaveTurret(UnitCategory.MilitaryTrain));

        Assert.True(TurretRules.CanHaveTurret("Tank_T3"));
        Assert.False(TurretRules.CanHaveTurret("SupplyTruck_T1"));
        Assert.False(TurretRules.CanHaveTurret("nonsense"));
        Assert.False(TurretRules.CanHaveTurret(null));
    }

    [Fact]
    public void The_split_keeps_the_barrel_with_the_turret_and_the_tracks_with_the_hull()
    {
        // What the renderer relies on: every triangle lands on exactly one side, the gun turns with
        // the turret, and the running gear stays put.
        MeshBuilder m = Tank();
        TurretSplit split = m.Detect();
        Assert.True(split.Found);

        int hull = 0, turret = 0;
        float barrelMaxY = float.MinValue, trackMaxY = float.MinValue;
        for (int t = 0; t + 2 < m.T.Count; t += 3)
        {
            float cy = (m.V[m.T[t] * 3 + 1] + m.V[m.T[t + 1] * 3 + 1] + m.V[m.T[t + 2] * 3 + 1]) / 3f;
            float cz = (m.V[m.T[t] * 3 + 2] + m.V[m.T[t + 1] * 3 + 2] + m.V[m.T[t + 2] * 3 + 2]) / 3f;
            if (cy >= split.SplitY) { turret++; if (cz > 4f) barrelMaxY = cy; }
            else { hull++; if (cy < 1.5f) trackMaxY = cy; }
        }

        Assert.True(hull > 0 && turret > 0);
        Assert.Equal(m.T.Count / 3, hull + turret);   // nothing lost, nothing duplicated
        Assert.True(barrelMaxY > float.MinValue, "the barrel must travel with the turret");
        Assert.True(trackMaxY > float.MinValue, "the running gear must stay with the hull");
    }
}
}
