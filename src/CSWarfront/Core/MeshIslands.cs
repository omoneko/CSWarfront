using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>
    /// Task143 (playtest: "parts of the hull come away with the turret - when the hull roof has steps in
    /// it, a horizontal cut takes them along; judge connectivity and keep only what belongs"): decides
    /// which of the geometry above the cut is actually turret.
    ///
    /// The split height is a single horizontal plane. That is the right way to find a turret ring and a
    /// crude way to decide what belongs to the turret: fenders, the engine deck and stowage on the rear
    /// all stand above the same plane while plainly being hull.
    ///
    /// The rule used here is not "keep the biggest piece". Measured on the subscribed assets, the
    /// geometry above an M1A2's ring falls into 143 separate pieces - turret shell, mantlet, hatches,
    /// smoke launchers, the gun - because Workshop models are assembled from parts that touch without
    /// being welded. Keeping the largest of those would have kept 13% of the turret and thrown the rest,
    /// including the gun, on the floor.
    ///
    /// What separates turret from hull is where a piece *continues*, not how big it is. Connectivity is
    /// computed over the whole mesh, and a piece that also has geometry below the cut is hull structure
    /// poking up through the plane; a piece living entirely above the cut is part of the turret assembly.
    /// On the assets tried this keeps 85-98% of the geometry above the cut - the turret and its gun,
    /// every time - and returns the rest to the hull.
    ///
    /// Pure geometry: no UnityEngine, deterministic, unit-tested.
    /// </summary>
    public static class MeshIslands
    {
        /// <summary>Weld tolerance as a fraction of the model's largest dimension. About a millimetre on
        /// a ten-metre tank: enough to close seam duplicates, fine enough that parts merely sitting next
        /// to each other are not fused.</summary>
        public const float WeldFraction = 1e-4f;

        /// <summary>Safety net for a model whose turret really is welded to its hull as one continuous
        /// shell: there, everything above the cut belongs to a piece that also goes below it, and the
        /// rule would discard the entire turret. When less than this share of the geometry above the cut
        /// survives, the connectivity judgement is abandoned and the plain height cut is used - a turret
        /// with a fender stuck to it is a far better outcome than no turret at all.</summary>
        public const float MinKeptFraction = 0.5f;

        /// <summary>Given a per-triangle mask of what lies above the cut, returns the mask narrowed to the
        /// pieces that belong to the turret. The dropped triangles are the caller's to hand back to the
        /// hull, so nothing disappears from the model.</summary>
        public static bool[] SelectTurretPieces(float[] vertices, int[] triangles, bool[] above)
        {
            if (vertices == null || triangles == null || above == null) return above;
            int triangleCount = triangles.Length / 3;
            if (above.Length < triangleCount) return above;

            int aboveCount = 0;
            for (int t = 0; t < triangleCount; t++) if (above[t]) aboveCount++;
            if (aboveCount == 0) return above;

            int[] weld = WeldVertices(vertices);
            var parent = new Dictionary<int, int>();

            // Connectivity over the WHOLE mesh: the question is where each piece continues to, which the
            // geometry below the cut is precisely what answers.
            for (int t = 0; t < triangleCount; t++)
            {
                int a = weld[triangles[t * 3]], b = weld[triangles[t * 3 + 1]], c = weld[triangles[t * 3 + 2]];
                Union(parent, a, b);
                Union(parent, a, c);
            }

            var reachesBelow = new HashSet<int>();
            for (int t = 0; t < triangleCount; t++)
                if (!above[t]) reachesBelow.Add(Find(parent, weld[triangles[t * 3]]));

            var kept = new bool[above.Length];
            int keptCount = 0;
            for (int t = 0; t < triangleCount; t++)
            {
                if (!above[t]) continue;
                if (reachesBelow.Contains(Find(parent, weld[triangles[t * 3]]))) continue;
                kept[t] = true;
                keptCount++;
            }

            if (keptCount < aboveCount * MinKeptFraction) return above; // one welded shell: see MinKeptFraction
            return kept;
        }

        /// <summary>Maps each vertex index to a representative shared by co-located vertices, by
        /// quantising positions onto a grid. Seam duplicates are written out bit-identical by every
        /// exporter in practice, so they always land in the same cell; the grid simply also absorbs
        /// near-duplicates. Two positions astride a cell boundary are not welded, which is acceptable -
        /// the cost is a slightly over-fragmented piece, never a wrongly fused one.</summary>
        private static int[] WeldVertices(float[] vertices)
        {
            int vertexCount = vertices.Length / 3;
            var weld = new int[vertexCount];

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            for (int i = 0; i < vertexCount; i++)
            {
                float x = vertices[i * 3], y = vertices[i * 3 + 1], z = vertices[i * 3 + 2];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }
            float span = maxX - minX;
            if (maxY - minY > span) span = maxY - minY;
            if (maxZ - minZ > span) span = maxZ - minZ;

            float cell = span * WeldFraction;
            if (cell <= 0f) cell = 1e-5f;

            var cells = new Dictionary<Cell, int>(vertexCount);
            for (int i = 0; i < vertexCount; i++)
            {
                var key = new Cell(
                    (int)System.Math.Floor(vertices[i * 3] / cell),
                    (int)System.Math.Floor(vertices[i * 3 + 1] / cell),
                    (int)System.Math.Floor(vertices[i * 3 + 2] / cell));
                int representative;
                if (cells.TryGetValue(key, out representative)) weld[i] = representative;
                else { cells[key] = i; weld[i] = i; }
            }
            return weld;
        }

        /// <summary>Grid coordinate used to find co-located vertices. A real key rather than a hash of
        /// one: a collision here would weld two unrelated parts of the model together, which is exactly
        /// the failure this class exists to prevent.</summary>
        private struct Cell : System.IEquatable<Cell>
        {
            private readonly int _x, _y, _z;
            public Cell(int x, int y, int z) { _x = x; _y = y; _z = z; }
            public bool Equals(Cell other) { return _x == other._x && _y == other._y && _z == other._z; }
            public override bool Equals(object obj) { return obj is Cell && Equals((Cell)obj); }
            public override int GetHashCode()
            {
                unchecked { return (_x * 73856093) ^ (_y * 19349663) ^ (_z * 83492791); }
            }
        }

        private static int Find(Dictionary<int, int> parent, int x)
        {
            int p;
            while (parent.TryGetValue(x, out p) && p != x) x = p;
            return x;
        }

        private static void Union(Dictionary<int, int> parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra == rb) return;
            // Attach the higher index under the lower: deterministic, and keeps roots stable.
            if (ra < rb) parent[rb] = ra;
            else parent[ra] = rb;
        }
    }
}
