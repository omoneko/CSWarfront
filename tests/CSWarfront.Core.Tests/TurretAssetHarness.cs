using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task141 verification harness: runs the shipped detector over real subscribed assets that
/// have been exported to OBJ, and writes the split it produces to disk so the geometry can be drawn and
/// looked at. This is how a rendering change gets checked before anyone is asked to load a city.
///
/// It is skipped silently when the export directory is absent, so it costs nothing in a normal run and
/// never fails a build on a machine without those assets.</summary>
public class TurretAssetHarness
{
    private static string AssetsDir()
    {
        string fromEnv = Environment.GetEnvironmentVariable("CSWARFRONT_ASSET_OBJ_DIR");
        return string.IsNullOrEmpty(fromEnv) ? null : fromEnv;
    }

    [Fact]
    public void Split_real_subscribed_assets_and_write_the_result()
    {
        string dir = AssetsDir();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return; // not an asset-equipped machine

        string outDir = Path.Combine(dir, "split");
        Directory.CreateDirectory(outDir);
        var report = new List<string>();

        foreach (string path in Directory.GetFiles(dir, "*.obj"))
        {
            float[] vertices;
            int[] triangles;
            if (!TryLoadObj(path, out vertices, out triangles)) continue;

            TurretSplit split = TurretDetection.Detect(vertices, triangles);
            string name = Path.GetFileNameWithoutExtension(path);
            if (!split.Found)
            {
                report.Add(name + "\tNONE");
                continue;
            }

            report.Add(string.Format(CultureInfo.InvariantCulture,
                "{0}\tSPLIT\t{1}\t{2}\t{3}\t{4}",
                name, split.SplitY, split.PivotX, split.PivotZ, split.BarrelSign));

            // The same partition TurretMeshSplitter performs: a triangle belongs to the turret when its
            // centroid is at or above the split, and the turret's vertices are re-based on the pivot so
            // the part rotates about its ring.
            WriteHalves(outDir, name, vertices, triangles, split);
        }

        File.WriteAllLines(Path.Combine(outDir, "report.tsv"), report.ToArray());
        Assert.NotEmpty(report);
    }

    private static void WriteHalves(string outDir, string name, float[] v, int[] t, TurretSplit split)
    {
        // Exactly what TurretMeshSplitter does: classify by height, then keep only the largest
        // connected island of that set and hand the rest back to the hull (Task143).
        var above = new bool[t.Length / 3];
        for (int i = 0; i < above.Length; i++)
        {
            float cy = (v[t[i * 3] * 3 + 1] + v[t[i * 3 + 1] * 3 + 1] + v[t[i * 3 + 2] * 3 + 1]) / 3f;
            above[i] = cy >= split.SplitY;
        }
        bool[] island = MeshIslands.SelectTurretPieces(v, t, above);

        var hull = new List<int>();
        var turret = new List<int>();
        int loose = 0;
        for (int i = 0; i < above.Length; i++)
        {
            List<int> target = island[i] ? turret : hull;
            target.Add(t[i * 3]); target.Add(t[i * 3 + 1]); target.Add(t[i * 3 + 2]);
            if (above[i] && !island[i]) loose++;
        }
        Console.WriteLine(name + ": returned " + loose + " loose triangle(s) above the cut to the hull");

        WriteObj(Path.Combine(outDir, name + ".hull.obj"), v, hull, 0f, 0f, 0f);
        WriteObj(Path.Combine(outDir, name + ".turret.obj"), v, turret,
            split.PivotX, split.SplitY, split.PivotZ);
    }

    private static void WriteObj(string path, float[] v, List<int> tris, float ox, float oy, float oz)
    {
        using (var w = new StreamWriter(path))
        {
            int count = v.Length / 3;
            for (int i = 0; i < count; i++)
                w.WriteLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}",
                    v[i * 3] - ox, v[i * 3 + 1] - oy, v[i * 3 + 2] - oz));
            for (int i = 0; i + 2 < tris.Count; i += 3)
                w.WriteLine("f " + (tris[i] + 1) + " " + (tris[i + 1] + 1) + " " + (tris[i + 2] + 1));
        }
    }

    private static bool TryLoadObj(string path, out float[] vertices, out int[] triangles)
    {
        vertices = null;
        triangles = null;
        var v = new List<float>();
        var t = new List<int>();
        foreach (string line in File.ReadAllLines(path))
        {
            string[] p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            if (p[0] == "v" && p.Length >= 4)
            {
                v.Add(float.Parse(p[1], CultureInfo.InvariantCulture));
                v.Add(float.Parse(p[2], CultureInfo.InvariantCulture));
                v.Add(float.Parse(p[3], CultureInfo.InvariantCulture));
            }
            else if (p[0] == "f" && p.Length >= 4)
            {
                var idx = new List<int>();
                for (int i = 1; i < p.Length; i++)
                    idx.Add(int.Parse(p[i].Split('/')[0], CultureInfo.InvariantCulture) - 1);
                for (int i = 1; i < idx.Count - 1; i++)
                {
                    t.Add(idx[0]); t.Add(idx[i]); t.Add(idx[i + 1]);
                }
            }
        }
        if (v.Count < 24 || t.Count < 12) return false;
        vertices = v.ToArray();
        triangles = t.ToArray();
        return true;
    }
}
}
