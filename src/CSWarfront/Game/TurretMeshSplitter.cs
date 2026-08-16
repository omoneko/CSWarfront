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
            // Task140 (playtest "the turret does not rotate on some assets"): the outcome of this was
            // invisible from outside — a model with no turret and a model whose geometry could not be
            // read look exactly the same on screen. One line per model, once (results are cached), says
            // which of the two happened and where the gun was cut.
            string label = source.name != null ? source.name : "(unnamed)";

            Vector3[] verts = source.vertices;
            if (verts == null || verts.Length == 0)
            {
                // Almost always a mesh Unity will not let us read back. Nothing can be done with it here,
                // but knowing that is the difference between "this model has no turret" and "we never got
                // to look at this model".
                ModConfig.Log("TurretMeshSplitter: '" + label + "' exposes no vertices (unreadable mesh); rendered as one piece");
                return null;
            }

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
            if (all.Count < 12)
            {
                ModConfig.Log("TurretMeshSplitter: '" + label + "' has only " + all.Count
                    + " triangle indices; rendered as one piece");
                return null;
            }

            int[] allTriangles = all.ToArray();
            TurretSplit split = TurretDetection.Detect(flat, allTriangles);
            if (!split.Found)
            {
                ModConfig.Log("TurretMeshSplitter: '" + label + "' verts=" + verts.Length
                    + " tris=" + (all.Count / 3) + " -> no turret found; rendered as one piece");
                return null;
            }
            ModConfig.Log("TurretMeshSplitter: '" + label + "' verts=" + verts.Length
                + " tris=" + (all.Count / 3) + " -> turret at y=" + split.SplitY.ToString("F2")
                + " pivot=(" + split.PivotX.ToString("F2") + "," + split.PivotZ.ToString("F2")
                + ") barrel=" + (split.BarrelSign < 0f ? "-Z" : "+Z"));

            var pivot = new Vector3(split.PivotX, split.SplitY, split.PivotZ);

            // Both halves keep the full vertex array (simplest correct split — unused vertices cost a
            // little memory but keep every index, normal, UV and colour aligned with the original).
            Mesh hull = NewMesh(source, verts, Vector3.zero, "Hull");
            Mesh turret = NewMesh(source, verts, pivot, "Turret");
            hull.subMeshCount = subMeshCount;
            turret.subMeshCount = subMeshCount;

            // Task143 (playtest "parts of the hull come away with the turret"): the cut is a single
            // horizontal plane, so anything else standing above it — fenders, the engine deck, stowage on
            // the rear — is swept up with the turret. Classify by height first, then let MeshIslands drop
            // the pieces that are hull structure poking through the plane. Whatever it drops goes back to
            // the hull, so no triangle is ever lost from the model.
            var above = new bool[allTriangles.Length / 3];
            for (int t = 0; t < above.Length; t++)
            {
                float cy = (verts[allTriangles[t * 3]].y
                          + verts[allTriangles[t * 3 + 1]].y
                          + verts[allTriangles[t * 3 + 2]].y) / 3f;
                above[t] = cy >= split.SplitY;
            }
            bool[] island = MeshIslands.SelectTurretPieces(flat, allTriangles, above);

            int returnedToHull = 0;
            for (int t = 0; t < above.Length; t++) if (above[t] && !island[t]) returnedToHull++;
            if (returnedToHull > 0)
                ModConfig.Log("TurretMeshSplitter: '" + label + "' returned " + returnedToHull
                    + " loose triangle(s) above the cut to the hull (not part of the turret)");

            // The island mask is indexed over the flattened triangle list, in the order the submeshes
            // were concatenated, so walk them in that same order.
            var hullTris = new List<int>();
            var turretTris = new List<int>();
            int flatIndex = 0;
            for (int sm = 0; sm < subMeshCount; sm++)
            {
                int[] tris = subTriangles[sm];
                hullTris.Clear();
                turretTris.Clear();
                if (tris != null)
                {
                    for (int t = 0; t + 2 < tris.Length; t += 3, flatIndex++)
                    {
                        List<int> target = island[flatIndex] ? turretTris : hullTris;
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
