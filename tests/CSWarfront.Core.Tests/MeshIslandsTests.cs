using System.Collections.Generic;
using CSWarfront.Core;
using Xunit;

namespace CSWarfront.Core.Tests
{
/// <summary>Task143 (playtest: "parts of the hull come away with the turret - the hull roof has steps in
/// it and a horizontal cut takes them along"). The cut is one horizontal plane; anything else standing
/// above it comes too. Connectivity is what tells them apart.</summary>
public class MeshIslandsTests
{
    private class MeshBuilder
    {
        public readonly List<float> V = new List<float>();
        public readonly List<int> T = new List<int>();

        /// <summary>Adds a closed box. Each call makes its own vertices, so separate boxes are separate
        /// pieces unless they share exact corner positions - which is how real assets are built.</summary>
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

        public bool[] Above(float splitY)
        {
            var v = V.ToArray();
            var t = T.ToArray();
            var above = new bool[t.Length / 3];
            for (int i = 0; i < above.Length; i++)
            {
                float cy = (v[t[i * 3] * 3 + 1] + v[t[i * 3 + 1] * 3 + 1] + v[t[i * 3 + 2] * 3 + 1]) / 3f;
                above[i] = cy >= splitY;
            }
            return above;
        }

        public bool[] Select(float splitY)
        {
            return MeshIslands.SelectTurretPieces(V.ToArray(), T.ToArray(), Above(splitY));
        }

        public int Count(bool[] mask)
        {
            int n = 0;
            foreach (bool b in mask) if (b) n++;
            return n;
        }

        /// <summary>Whether any selected triangle reaches the given Z - used to prove the gun survived.</summary>
        public bool Reaches(bool[] mask, float z)
        {
            var v = V.ToArray();
            var t = T.ToArray();
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i]) continue;
                for (int k = 0; k < 3; k++)
                    if (v[t[i * 3 + k] * 3 + 2] >= z - 0.01f) return true;
            }
            return false;
        }
    }

    private const float Split = 1.5f;

    /// <summary>A fender that stands proud of the hull roof crosses the cut, so part of it lands in the
    /// turret half. It is hull: its own geometry continues below the cut, and it is dropped.</summary>
    [Fact]
    public void Hull_structure_poking_above_the_cut_is_not_taken_by_the_turret()
    {
        var m = new MeshBuilder();
        m.Box(0f, 0.75f, 0f, 3.6f, 1.5f, 10f);     // hull, entirely below the cut
        m.Box(1.9f, 1.4f, 0f, 0.4f, 1.6f, 8f);     // fender: straddles the cut, continues down the side
        m.Box(0f, 2.3f, 0.5f, 2f, 1.6f, 4f);       // turret, resting on the hull roof
        m.Box(0f, 2.3f, 4.5f, 0.4f, 0.4f, 5f);     // gun

        bool[] above = m.Above(Split);
        bool[] selected = m.Select(Split);

        Assert.True(m.Count(selected) < m.Count(above), "nothing was returned to the hull");
        Assert.True(m.Reaches(selected, 6.5f), "the gun was thrown away with the fender");
        // The fender reaches out to x = 2.1; nothing that wide should still be turning.
        var v = m.V.ToArray();
        var t = m.T.ToArray();
        for (int i = 0; i < selected.Length; i++)
        {
            if (!selected[i]) continue;
            for (int k = 0; k < 3; k++)
                Assert.True(v[t[i * 3 + k] * 3] < 1.5f, "a fender triangle is still on the turret");
        }
    }

    /// <summary>The turret and its gun are separate pieces in most Workshop models. Both live entirely
    /// above the cut, so both are kept - "keep the largest piece" would have kept one and dropped the
    /// other.</summary>
    [Fact]
    public void Separate_turret_parts_are_all_kept()
    {
        var m = new MeshBuilder();
        m.Box(0f, 0.75f, 0f, 3.6f, 1.5f, 10f);
        m.Box(0f, 2.3f, 0.5f, 2f, 1.6f, 4f);       // turret shell
        m.Box(0f, 2.3f, 4.5f, 0.4f, 0.4f, 5f);     // gun: a piece of its own
        m.Box(0.6f, 3.3f, 0f, 0.5f, 0.4f, 0.5f);   // commander's hatch: another one

        bool[] selected = m.Select(Split);

        // Three boxes of twelve triangles each. The hull's top face sits exactly on the cut and counts as
        // above it, so it is the one thing handed back - which is right, it is the hull roof.
        Assert.Equal(36, m.Count(selected));
        Assert.True(m.Count(m.Above(Split)) > m.Count(selected));
        Assert.True(m.Reaches(selected, 6.5f), "the gun, a piece of its own, was dropped");
    }

    /// <summary>A model whose turret is welded to its hull as one shell has nothing that lives entirely
    /// above the cut. Rather than discard the whole turret, the plain height cut is kept.</summary>
    [Fact]
    public void A_single_welded_shell_falls_back_to_the_plain_cut()
    {
        var m = new MeshBuilder();
        m.Box(0f, 1.5f, 0f, 3f, 3f, 10f); // one box spanning the cut: every triangle is one piece

        bool[] above = m.Above(Split);
        bool[] selected = m.Select(Split);

        Assert.Equal(m.Count(above), m.Count(selected));
    }

    [Fact]
    public void Handles_degenerate_input_without_throwing()
    {
        Assert.Null(MeshIslands.SelectTurretPieces(null, null, null));
        var empty = new bool[0];
        Assert.Same(empty, MeshIslands.SelectTurretPieces(new float[0], new int[0], empty));
    }
}
}
