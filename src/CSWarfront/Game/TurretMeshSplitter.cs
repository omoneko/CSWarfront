using System;
using System.Collections.Generic;
using CSWarfront.Core;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>Hull mesh, turret mesh and the ring the turret turns about (Task126).</summary>
    public class TurretParts
    {
        public Mesh Hull;
        public Mesh Turret;
        /// <summary>Turret ring, in the source mesh's own local space. The turret mesh is rebuilt
        /// around the origin, so the renderer places its GameObject here and rotates it in place.</summary>
        public Vector3 Pivot;
        /// <summary>+1 when the model's barrel points +Z, -1 when it points -Z (see TurretSplit).</summary>
        public float BarrelSign;
    }

    /// <summary>
    /// Task126: splits a vehicle mesh into hull and turret so the turret can be rotated on its own.
    /// The geometric decision is <see cref="TurretDetection"/>'s (pure Core logic, unit-tested); this
    /// class only does the Unity mesh surgery and caches the result.
    ///
    /// Triangles go to the turret when their centroid sits at or above the detected split height —
    /// the same rule the detector counted with, so what it validated is what gets built. Submesh
    /// structure is preserved on both halves, because the renderer feeds them the model's material
    /// array unchanged; an empty submesh is kept as an empty submesh rather than renumbering, so
    /// material indices never shift.
    ///
    /// Results are cached per source mesh: the surgery is O(triangles) and runs once per model, not
    /// once per unit. Cached Unity objects are checked element-wise for the fake-null that a city
    /// reload produces (see the CS modding notes on static Unity caches), and the whole cache is
    /// dropped on level unload.
    ///
    /// Main-thread only (Unity mesh APIs).
    /// </summary>
    internal static class TurretMeshSplitter
    {
        private static readonly Dictionary<Mesh, TurretParts> _cache = new Dictionary<Mesh, TurretParts>();

        /// <summary>Returns null when the mesh has no detectable turret (the caller then renders it as
        /// one rigid model). Never throws.</summary>
        public static TurretParts TryGet(Mesh source)
        {
            try
            {
                if (source == null) return null;

                TurretParts cached;
                if (_cache.TryGetValue(source, out cached))
                {
                    // Fake-null check: a city reload destroys the meshes we built while the dictionary
                    // still holds them. Rebuild rather than hand back destroyed objects.
                    if (cached == null) return null;
                    if (cached.Hull != null && cached.Turret != null) return cached;
                    _cache.Remove(source);
                }

                TurretParts parts = Build(source);
                _cache[source] = parts; // null is cached too: do not re-run detection every spawn
                return parts;
            }
            catch (Exception e)
            {
                ModConfig.LogError("TurretMeshSplitter.TryGet error: " + e);
                return null;
            }
        }

        private static TurretParts Build(Mesh source)
        {
            Vector3[] verts = source.vertices;
            if (verts == null || verts.Length == 0) return null;

            var flat = new float[verts.Length * 3];
            for (int i = 0; i < verts.Length; i++)
            {
                flat[i * 3] = verts[i].x;
                flat[i * 3 + 1] = verts[i].y;
                flat[i * 3 + 2] = verts[i].z;
            }

            int subMeshCount = Mathf.Max(1, source.subMeshCount);
            var subTriangles = new int[subMeshCount][];
            var all = new List<int>();
            for (int sm = 0; sm < subMeshCount; sm++)
            {
                subTriangles[sm] = source.GetTriangles(sm);
                if (subTriangles[sm] != null) all.AddRange(subTriangles[sm]);
            }
            if (all.Count < 12) return null;

            TurretSplit split = TurretDetection.Detect(flat, all.ToArray());
            if (!split.Found) return null;

            var pivot = new Vector3(split.PivotX, split.SplitY, split.PivotZ);

            // Both halves keep the full vertex array (simplest correct split — unused vertices cost a
            // little memory but keep every index, normal, UV and colour aligned with the original).
            Mesh hull = NewMesh(source, verts, Vector3.zero, "Hull");
            Mesh turret = NewMesh(source, verts, pivot, "Turret");
            hull.subMeshCount = subMeshCount;
            turret.subMeshCount = subMeshCount;

            var hullTris = new List<int>();
            var turretTris = new List<int>();
            for (int sm = 0; sm < subMeshCount; sm++)
            {
                int[] tris = subTriangles[sm];
                hullTris.Clear();
                turretTris.Clear();
                if (tris != null)
                {
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        float cy = (verts[tris[t]].y + verts[tris[t + 1]].y + verts[tris[t + 2]].y) / 3f;
                        List<int> target = cy >= split.SplitY ? turretTris : hullTris;
                        target.Add(tris[t]); target.Add(tris[t + 1]); target.Add(tris[t + 2]);
                    }
                }
                hull.SetTriangles(hullTris.ToArray(), sm);
                turret.SetTriangles(turretTris.ToArray(), sm);
            }

            hull.RecalculateBounds();
            turret.RecalculateBounds();

            return new TurretParts { Hull = hull, Turret = turret, Pivot = pivot, BarrelSign = split.BarrelSign };
        }

        /// <summary>Copies the source's vertex data, optionally shifted so the given point becomes the
        /// origin (used for the turret, which must rotate about its ring rather than the model origin).</summary>
        private static Mesh NewMesh(Mesh source, Vector3[] verts, Vector3 offset, string suffix)
        {
            var m = new Mesh();
            m.name = source.name + "_" + suffix;

            if (offset == Vector3.zero)
            {
                m.vertices = verts;
            }
            else
            {
                var shifted = new Vector3[verts.Length];
                for (int i = 0; i < verts.Length; i++) shifted[i] = verts[i] - offset;
                m.vertices = shifted;
            }

            Vector3[] normals = source.normals;
            if (normals != null && normals.Length == verts.Length) m.normals = normals;
            Vector2[] uv = source.uv;
            if (uv != null && uv.Length == verts.Length) m.uv = uv;
            Color[] colors = source.colors;
            if (colors != null && colors.Length == verts.Length) m.colors = colors;

            return m;
        }

        /// <summary>Level unload: drop every built mesh (see the class comment).</summary>
        public static void Reset()
        {
            foreach (KeyValuePair<Mesh, TurretParts> kv in _cache)
            {
                if (kv.Value == null) continue;
                if (kv.Value.Hull != null) UnityEngine.Object.Destroy(kv.Value.Hull);
                if (kv.Value.Turret != null) UnityEngine.Object.Destroy(kv.Value.Turret);
            }
            _cache.Clear();
        }
    }
}
