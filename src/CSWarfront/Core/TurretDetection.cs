namespace CSWarfront.Core
{
    /// <summary>Result of <see cref="TurretDetection.Detect"/>. All values are in the mesh's own model
    /// space (Y up, +Z forward — the convention shared by the bundled OBJ models and CS vehicle
    /// meshes).</summary>
    public struct TurretSplit
    {
        /// <summary>False when the mesh does not look like a turreted vehicle; the caller then renders
        /// it as one rigid model exactly as before.</summary>
        public bool Found;

        /// <summary>Triangles whose centroid sits at or above this Y belong to the turret.</summary>
        public float SplitY;

        /// <summary>Turret ring centre — the axis the turret rotates about.</summary>
        public float PivotX, PivotZ;

        /// <summary>+1 when the barrel points toward +Z (the model's nose), -1 when it points the other
        /// way. Lets a model authored back-to-front still aim correctly: the renderer simply adds half
        /// a turn to the turret's yaw.</summary>
        public float BarrelSign;
    }

    /// <summary>
    /// Task126 (user request "tanks should have rotating turrets, and it has to work for the tank
    /// models I subscribe to as well"): finds the turret of an arbitrary vehicle mesh from its shape
    /// alone, so no per-asset configuration is needed.
    ///
    /// The shape of a turreted AFV, in the user's own terms: a hull that is wide and long, a narrower
    /// body above it (the turret), and a long thin gun barrel reaching out of that body — with the
    /// turret ring somewhere between. The detector looks for exactly that:
    ///  1. slice the mesh horizontally and measure each slice's width (X extent);
    ///  2. the turret ring is the lowest lasting step down in that profile — the height where the hull
    ///     stops and nothing above ever gets that wide again;
    ///  3. the part above must be a real body (a sane share of the triangles, and a sane width relative
    ///     to the hull) — not an antenna or a whole second vehicle;
    ///  4. and it must carry a barrel: a thin protrusion along the length axis reaching well beyond the
    ///     turret's bulk. This last test is what separates a tank from an APC, a truck or a building.
    /// Anything failing a test is reported as "no turret" and rendered rigid — a false negative merely
    /// loses an animation, while a false positive would tear a model in half.
    ///
    /// Pure geometry: no UnityEngine, no CS API, deterministic, fully unit-tested. The Game layer only
    /// supplies vertex/triangle arrays and applies the result.
    ///
    /// Validated offline against every bundled model: it accepts the tank and the SPG, and rejects the
    /// APC, the SPAAG, trucks, infantry, aircraft, helicopters, trains and every building.
    /// </summary>
    public static class TurretDetection
    {
        /// <summary>Horizontal slices used to profile the mesh.</summary>
        public const int Slices = 48;

        /// <summary>The turret ring is a *step* in the width profile, not a fixed ratio: real turrets
        /// are wide (the bundled tank's is 74% of its hull), so any absolute threshold either misses
        /// them or cuts elsewhere. A band qualifies when the profile drops by at least this share of
        /// the hull width from the band below it.
        ///
        /// Calibrated against the Workshop tank assets actually installed here, not just the bundled
        /// models: at 0.12 the K2 MBT was missed by a hair (its ring step measures 11.4%), and at 0.06
        /// a bare "tracks" sub-mesh started qualifying. 0.08 detects every real tank tried — K2, IS-2,
        /// Luchs, Sabrah — while still rejecting the FUCHS APC, water tanks, an anti-tank barrier and
        /// Thomas the Tank Engine.</summary>
        public const float MinRingDrop = 0.08f;

        /// <summary>Above the ring the model must never get wider again (beyond this slack) — that is
        /// what distinguishes "the hull ends here" from a mere notch or a wheel arch.</summary>
        public const float AboveRingTolerance = 1.05f;

        /// <summary>The split must land between these fractions of the model's height.</summary>
        public const float MinSplitFraction = 0.15f;
        public const float MaxSplitFraction = 0.80f;

        /// <summary>The turret must own between these shares of the mesh's triangles.</summary>
        public const float MinTurretTriangleShare = 0.03f;
        public const float MaxTurretTriangleShare = 0.60f;

        /// <summary>The turret's width, as a share of the hull's.</summary>
        public const float MinTurretWidth = 0.30f;
        public const float MaxTurretWidth = 0.90f;

        /// <summary>The outermost share of the turret's length examined as "the barrel tip".</summary>
        public const float BarrelTipFraction = 0.15f;

        /// <summary>The tip must be no wider than this share of the hull width.</summary>
        public const float MaxBarrelWidth = 0.30f;

        /// <summary>The tip must reach at least this share of the model's length beyond the bulk of the
        /// turret.</summary>
        public const float MinBarrelReach = 0.12f;

        /// <summary>
        /// vertices: x,y,z triplets (length = 3 × vertex count). triangles: vertex indices, 3 per
        /// triangle. Returns Found=false for any mesh that does not clearly read as turreted.
        /// </summary>
        public static TurretSplit Detect(float[] vertices, int[] triangles)
        {
            var none = new TurretSplit();
            if (vertices == null || triangles == null) return none;
            int vertexCount = vertices.Length / 3;
            if (vertexCount < 8 || triangles.Length < 12) return none;

            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < vertexCount; i++)
            {
                float y = vertices[i * 3 + 1], z = vertices[i * 3 + 2];
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }
            float height = maxY - minY, length = maxZ - minZ;
            if (height <= 0.001f || length <= 0.001f) return none;

            // 1. width profile per horizontal slice.
            // Measured over triangle spans, not vertices: a flat-sided low-poly model has no vertices
            // between the corners of a face, so a vertex-only profile leaves whole bands empty and can
            // mistake the barrel's own height for the turret ring — which would slice the turret in
            // half. Every triangle contributes its X extent to every band its Y range covers.
            var sliceMin = new float[Slices];
            var sliceMax = new float[Slices];
            var sliceHas = new bool[Slices];
            int triangleCount = triangles.Length / 3;
            for (int t = 0; t < triangleCount; t++)
            {
                int i0 = triangles[t * 3], i1 = triangles[t * 3 + 1], i2 = triangles[t * 3 + 2];
                float x0 = vertices[i0 * 3], x1 = vertices[i1 * 3], x2 = vertices[i2 * 3];
                float y0 = vertices[i0 * 3 + 1], y1 = vertices[i1 * 3 + 1], y2 = vertices[i2 * 3 + 1];

                float xLo = x0 < x1 ? (x0 < x2 ? x0 : x2) : (x1 < x2 ? x1 : x2);
                float xHi = x0 > x1 ? (x0 > x2 ? x0 : x2) : (x1 > x2 ? x1 : x2);
                float yLo = y0 < y1 ? (y0 < y2 ? y0 : y2) : (y1 < y2 ? y1 : y2);
                float yHi = y0 > y1 ? (y0 > y2 ? y0 : y2) : (y1 > y2 ? y1 : y2);

                int bLo = (int)((yLo - minY) / height * Slices);
                int bHi = (int)((yHi - minY) / height * Slices);
                if (bLo < 0) bLo = 0;
                if (bHi >= Slices) bHi = Slices - 1;
                for (int b = bLo; b <= bHi; b++)
                {
                    if (!sliceHas[b]) { sliceHas[b] = true; sliceMin[b] = xLo; sliceMax[b] = xHi; }
                    else
                    {
                        if (xLo < sliceMin[b]) sliceMin[b] = xLo;
                        if (xHi > sliceMax[b]) sliceMax[b] = xHi;
                    }
                }
            }
            float hullWidth = 0f;
            for (int b = 0; b < Slices / 2; b++)
                if (sliceHas[b] && sliceMax[b] - sliceMin[b] > hullWidth) hullWidth = sliceMax[b] - sliceMin[b];
            if (hullWidth <= 0.001f) return none;

            // 2-4. the ring, then the turret it implies. Every band showing a lasting step down is
            // a candidate; the first one whose turret survives the checks below wins.
            //
            // Task141 (playtest "the turret does not come apart on some assets"): this used to commit
            // to the lowest band with a step and give up if the turret above it did not hold up - even
            // when a perfectly good ring sat a few bands higher. A tank with a stowage rack or a skirt
            // has a step low on the hull that is not the ring at all, and that one step was enough to
            // lose the model. Measured over the subscribed armour collection, trying the rest of the
            // bands is what recovers them; nothing that was detected before changes, because a model
            // whose first band works still takes it first.
            int lowBand = (int)(Slices * MinSplitFraction), highBand = (int)(Slices * MaxSplitFraction);
            if (lowBand < 1) lowBand = 1;
            for (int b = lowBand; b < highBand; b++)
            {
                if (!sliceHas[b] || !sliceHas[b - 1]) continue;
                float here = sliceMax[b] - sliceMin[b];
                float below = sliceMax[b - 1] - sliceMin[b - 1];
                if (below - here < hullWidth * MinRingDrop) continue;

                float widestAbove = 0f;
                for (int a = b; a < Slices; a++)
                    if (sliceHas[a] && sliceMax[a] - sliceMin[a] > widestAbove) widestAbove = sliceMax[a] - sliceMin[a];
                if (widestAbove > here * AboveRingTolerance) continue; // the turret widens again: not a ring

                TurretSplit candidate;
                if (TryTurretAbove(vertices, vertexCount, triangles, triangleCount,
                        minY + height * b / Slices, hullWidth, length, out candidate))
                    return candidate;
            }
            return none;
        }

        /// <summary>Task141: everything that decides whether the geometry above splitY is a turret with a
        /// gun on it. Split out of Detect so each candidate ring can be tried in turn; the checks
        /// themselves are unchanged.</summary>
        private static bool TryTurretAbove(float[] vertices, int vertexCount, int[] triangles,
            int triangleCount, float splitY, float hullWidth, float length, out TurretSplit result)
        {
            result = new TurretSplit();

            // 3. the turret must be a real body
            int turretTris = 0;
            for (int t = 0; t < triangleCount; t++)
            {
                float cy = (vertices[triangles[t * 3] * 3 + 1]
                          + vertices[triangles[t * 3 + 1] * 3 + 1]
                          + vertices[triangles[t * 3 + 2] * 3 + 1]) / 3f;
                if (cy >= splitY) turretTris++;
            }
            float share = (float)turretTris / triangleCount;
            if (share < MinTurretTriangleShare || share > MaxTurretTriangleShare) return false;

            float tMinX = float.MaxValue, tMaxX = float.MinValue, tMinZ = float.MaxValue, tMaxZ = float.MinValue;
            int turretVerts = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                if (vertices[i * 3 + 1] < splitY) continue;
                turretVerts++;
                float x = vertices[i * 3], z = vertices[i * 3 + 2];
                if (x < tMinX) tMinX = x;
                if (x > tMaxX) tMaxX = x;
                if (z < tMinZ) tMinZ = z;
                if (z > tMaxZ) tMaxZ = z;
            }
            if (turretVerts < 4) return false;
            float turretWidth = tMaxX - tMinX;
            if (turretWidth < hullWidth * MinTurretWidth || turretWidth > hullWidth * MaxTurretWidth) return false;

            // 4. the barrel: a thin protrusion along Z, reaching beyond the turret's bulk
            float medianZ = MedianTurretZ(vertices, vertexCount, splitY);
            float turretLength = tMaxZ - tMinZ;
            float bestReach = 0f, barrelSign = 0f;
            for (int s = 0; s < 2; s++)
            {
                float sign = s == 0 ? 1f : -1f;
                float cut = sign > 0f ? tMaxZ - turretLength * BarrelTipFraction
                                      : tMinZ + turretLength * BarrelTipFraction;
                float tipMinX = float.MaxValue, tipMaxX = float.MinValue;
                int tipCount = 0;
                for (int i = 0; i < vertexCount; i++)
                {
                    if (vertices[i * 3 + 1] < splitY) continue;
                    float z = vertices[i * 3 + 2];
                    if (sign > 0f ? z < cut : z > cut) continue;
                    tipCount++;
                    float x = vertices[i * 3];
                    if (x < tipMinX) tipMinX = x;
                    if (x > tipMaxX) tipMaxX = x;
                }
                if (tipCount < 3) continue;
                if (tipMaxX - tipMinX > hullWidth * MaxBarrelWidth) continue;
                float reach = sign > 0f ? tMaxZ - medianZ : medianZ - tMinZ;
                if (reach < length * MinBarrelReach) continue;
                if (reach > bestReach) { bestReach = reach; barrelSign = sign; }
            }
            if (barrelSign == 0f) return false;

            result = new TurretSplit
            {
                Found = true,
                SplitY = splitY,
                PivotX = MedianTurretX(vertices, vertexCount, splitY),
                PivotZ = medianZ,
                BarrelSign = barrelSign
            };
            return true;
        }

        // Medians (not means): the barrel is a long thin tail of vertices that would drag a mean
        // forward, putting the rotation axis out in front of the tank.
        private static float MedianTurretX(float[] vertices, int vertexCount, float splitY)
        {
            return MedianComponent(vertices, vertexCount, splitY, 0);
        }

        private static float MedianTurretZ(float[] vertices, int vertexCount, float splitY)
        {
            return MedianComponent(vertices, vertexCount, splitY, 2);
        }

        private static float MedianComponent(float[] vertices, int vertexCount, float splitY, int component)
        {
            int n = 0;
            for (int i = 0; i < vertexCount; i++)
                if (vertices[i * 3 + 1] >= splitY) n++;
            if (n == 0) return 0f;

            var values = new float[n];
            int k = 0;
            for (int i = 0; i < vertexCount; i++)
                if (vertices[i * 3 + 1] >= splitY) values[k++] = vertices[i * 3 + component];
            System.Array.Sort(values);
            return values[n / 2];
        }
    }
}
