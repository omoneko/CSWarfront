using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSWarfront.Game.UI
{
    /// <summary>Which kind of order this destination belongs to (Task62). Used only for visual color coding.</summary>
    public enum OrderMarkerKind { Advance, Rally }

    /// <summary>One main-thread-only snapshot entry = the destination for one selected unit
    /// (OrderTargetPos during free advance/AI delegation, RallyPoint while waiting to rally).
    /// Units that are on Hold or have no destination set are simply never generated (the caller,
    /// MilitaryManager.OnMainVisualUpdate, filters them out).</summary>
    public struct OrderDestinationState
    {
        public Vector3 Position;
        public OrderMarkerKind Kind;
    }

    /// <summary>
    /// Task62 (Mount&amp;Blade-style order feedback 1/2): displays an improvised "circle + short pole"
    /// marker in world space at the advance/rally destinations of the selected units. Uses the same
    /// declarative reconcile pattern (create/move/destroy) as UnitVisuals/BaseVisuals: any marker
    /// not present in the snapshot passed to Sync(list) is destroyed automatically.
    ///
    /// Merging: destinations of the same order kind that cluster within roughly 10 units are merged
    /// into a single marker. The implementation is a simple approximation that buckets into a grid
    /// (floor(x/r), floor(z/r)) with MergeRadius as the cell edge (in rare cases straddling a
    /// rounding boundary two markers may appear, but that is sufficient for a visual-hint purpose).
    /// By reusing this grid key itself as the dictionary key, the same destination cluster resolves
    /// to the same key every frame — playing the same role as UnitVisuals' InstanceId — so a
    /// cross-frame "move the same marker" reconcile can be written straightforwardly.
    ///
    /// Appearance: a ground ring (approximated by a thin cylinder, same technique as
    /// UnitBoxSelection's highlight marker) plus a short pole (a tall thin cylinder). Materials
    /// borrow nothing from CS vehicles/buildings; exactly one home-made material per order kind is
    /// created via Shader.Find("Standard") and reused (same policy as
    /// UnitBoxSelection._highlightMaterial). All Colliders attached at primitive creation are
    /// destroyed (so they do not interfere with the Physics.Raycast click tests for unit selection
    /// and rally-point designation, keeping the existing Task31/Task48 raycast paths uncontaminated).
    ///
    /// Visibility conditions: while PanelChrome.IsGameReadyForUi()==false or
    /// PanelChrome.IsGameMenuOpen()==true, all markers are hidden (not destroyed — avoiding the
    /// cost of recreating them on re-show). If the selection is empty (empty snapshot), Sync
    /// naturally destroys all markers (that is declarative reconcile itself; no special-casing needed).
    ///
    /// Thread boundary: all methods are main-thread only (because they call Unity APIs). The caller
    /// (MilitaryManager.OnMainVisualUpdate) builds the list of OrderDestinationState inside
    /// _stateLock and passes it to Sync() after releasing the lock (exactly the same contract as
    /// UnitVisuals.Sync).
    /// </summary>
    public static class OrderDestinationMarkers
    {
        /// <summary>Destinations of the same order kind that fall into a grid cell of this edge length (map units) are merged into one marker.</summary>
        private const float MergeRadius = 10f;

        private const float RingDiameter = 7f;
        private const float RingThinHeight = 0.25f;
        private const float RingYOffset = 0.25f;
        private const float PoleHeight = 9f;
        private const float PoleDiameter = 0.6f;

        private struct MarkerKey : IEquatable<MarkerKey>
        {
            public readonly OrderMarkerKind Kind;
            public readonly int CellX;
            public readonly int CellZ;

            public MarkerKey(OrderMarkerKind kind, int cellX, int cellZ)
            {
                Kind = kind; CellX = cellX; CellZ = cellZ;
            }

            public bool Equals(MarkerKey other)
            {
                return Kind == other.Kind && CellX == other.CellX && CellZ == other.CellZ;
            }

            public override bool Equals(object obj) { return obj is MarkerKey && Equals((MarkerKey)obj); }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = (int)Kind;
                    h = h * 486187739 + CellX;
                    h = h * 486187739 + CellZ;
                    return h;
                }
            }
        }

        private class MarkerEntry
        {
            public GameObject Root;
            public Vector3 LastPosition;
        }

        private static readonly Dictionary<MarkerKey, MarkerEntry> _markers = new Dictionary<MarkerKey, MarkerEntry>();

        // Work areas reused on every Sync() run (GC avoidance, same policy as UnitVisuals.Sync).
        private static readonly Dictionary<MarkerKey, Vector3> _sums = new Dictionary<MarkerKey, Vector3>();
        private static readonly Dictionary<MarkerKey, int> _counts = new Dictionary<MarkerKey, int>();
        private static readonly List<MarkerKey> _staleKeys = new List<MarkerKey>();

        private static Material _advanceMaterial;
        private static Material _rallyMaterial;
        private static bool _hiddenLastSync; // whether the previous Sync was "hidden due to menu open / not ready" (used to control re-showing on recovery)

        /// <summary>Declaratively applies create/move/destroy based on the snapshot (main thread only).</summary>
        public static void Sync(List<OrderDestinationState> snapshot)
        {
            try
            {
                if (!PanelChrome.IsGameReadyForUi() || PanelChrome.IsGameMenuOpen())
                {
                    SetAllVisible(false);
                    _hiddenLastSync = true;
                    return;
                }
                if (_hiddenLastSync)
                {
                    SetAllVisible(true);
                    _hiddenLastSync = false;
                }

                if (snapshot == null) snapshot = _emptySnapshot;

                // 1st pass: sum positions per grid key (merging).
                _sums.Clear();
                _counts.Clear();
                for (int i = 0; i < snapshot.Count; i++)
                {
                    OrderDestinationState s = snapshot[i];
                    MarkerKey key = new MarkerKey(
                        s.Kind,
                        FloorDiv(s.Position.x, MergeRadius),
                        FloorDiv(s.Position.z, MergeRadius));

                    Vector3 sum;
                    int count;
                    _sums.TryGetValue(key, out sum);
                    _counts.TryGetValue(key, out count);
                    _sums[key] = sum + s.Position;
                    _counts[key] = count + 1;
                }

                // 2nd pass: reconcile (create/move) at the averaged position.
                foreach (var kv in _sums)
                {
                    MarkerKey key = kv.Key;
                    Vector3 avg = kv.Value / _counts[key];

                    MarkerEntry entry;
                    if (!_markers.TryGetValue(key, out entry) || entry.Root == null)
                    {
                        entry = CreateMarker(key.Kind, avg);
                        if (entry == null) continue; // already logged inside CreateMarker
                        _markers[key] = entry;
                    }
                    else if ((entry.LastPosition - avg).sqrMagnitude > 0.0001f)
                    {
                        entry.Root.transform.position = avg;
                        entry.LastPosition = avg;
                    }
                }

                // 3rd pass: destroy keys absent from the snapshot.
                _staleKeys.Clear();
                foreach (var kv in _markers)
                {
                    if (!_sums.ContainsKey(kv.Key)) _staleKeys.Add(kv.Key);
                }
                for (int i = 0; i < _staleKeys.Count; i++)
                {
                    DestroyMarker(_staleKeys[i]);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OrderDestinationMarkers.Sync error: " + e);
            }
        }

        private static readonly List<OrderDestinationState> _emptySnapshot = new List<OrderDestinationState>();

        /// <summary>Destroys all tracked markers (on level unload, main thread only).</summary>
        public static void DestroyAll()
        {
            try
            {
                foreach (var kv in _markers)
                {
                    if (kv.Value != null && kv.Value.Root != null) UnityEngine.Object.Destroy(kv.Value.Root);
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("OrderDestinationMarkers.DestroyAll error: " + e);
            }
            finally
            {
                _markers.Clear();
                _hiddenLastSync = false;
            }
        }

        private static void SetAllVisible(bool visible)
        {
            foreach (var kv in _markers)
            {
                if (kv.Value != null && kv.Value.Root != null) kv.Value.Root.SetActive(visible);
            }
        }

        private static int FloorDiv(float value, float cellSize)
        {
            return Mathf.FloorToInt(value / cellSize);
        }

        private static MarkerEntry CreateMarker(OrderMarkerKind kind, Vector3 position)
        {
            try
            {
                Material material = GetMaterial(kind);
                if (material == null) return null;

                GameObject root = new GameObject("CSWarfrontOrderMarker");
                root.transform.position = position;

                GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                ring.name = "Ring";
                StripCollider(ring);
                ring.transform.SetParent(root.transform, false);
                ring.transform.localPosition = new Vector3(0f, RingYOffset, 0f);
                ring.transform.localScale = new Vector3(RingDiameter, RingThinHeight, RingDiameter);
                MeshRenderer ringRenderer = ring.GetComponent<MeshRenderer>();
                if (ringRenderer != null) ringRenderer.sharedMaterial = material;

                GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pole.name = "Pole";
                StripCollider(pole);
                pole.transform.SetParent(root.transform, false);
                pole.transform.localPosition = new Vector3(0f, PoleHeight * 0.5f, 0f);
                pole.transform.localScale = new Vector3(PoleDiameter, PoleHeight * 0.5f, PoleDiameter);
                MeshRenderer poleRenderer = pole.GetComponent<MeshRenderer>();
                if (poleRenderer != null) poleRenderer.sharedMaterial = material;

                return new MarkerEntry { Root = root, LastPosition = position };
            }
            catch (Exception e)
            {
                ModConfig.LogError("OrderDestinationMarkers.CreateMarker error: " + e);
                return null;
            }
        }

        private static void StripCollider(GameObject go)
        {
            Collider col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
        }

        private static void DestroyMarker(MarkerKey key)
        {
            MarkerEntry entry;
            if (_markers.TryGetValue(key, out entry))
            {
                if (entry != null && entry.Root != null) UnityEngine.Object.Destroy(entry.Root);
                _markers.Remove(key);
            }
        }

        private static Material GetMaterial(OrderMarkerKind kind)
        {
            if (kind == OrderMarkerKind.Rally)
            {
                if (_rallyMaterial == null) _rallyMaterial = CreateMaterial(new Color(0.25f, 0.9f, 0.95f, 1f)); // cyan-ish
                return _rallyMaterial;
            }

            if (_advanceMaterial == null) _advanceMaterial = CreateMaterial(new Color(0.95f, 0.25f, 0.2f, 1f)); // red-ish
            return _advanceMaterial;
        }

        private static Material CreateMaterial(Color color)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Diffuse");
            if (shader == null) return null;
            Material m = new Material(shader);
            m.color = color;
            return m;
        }
    }
}
