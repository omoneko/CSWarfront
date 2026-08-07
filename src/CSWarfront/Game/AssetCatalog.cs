using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using UnityEngine;

namespace CSWarfront.Game
{
    /// <summary>
    /// Helper that enumerates/resolves loaded assets (props, buildings, vehicles, trees; including
    /// subscribed Workshop assets) by kind (<see cref="AssetKind"/>) x name (introduced as
    /// PropCatalog in Task36, generalized to AssetCatalog in Task41 to also cover
    /// buildings/vehicles/trees).
    ///
    /// Only m_mesh (rendering) and m_material.mainTexture (texture, via UnitMaterialFactory) are
    /// ever borrowed; AIs and the CS-side Material objects themselves are never borrowed (same
    /// policy as UnitMeshSource/UnitMaterialFactory). This is a safety guarantee shared by all 4
    /// kinds, and also the reason any kind can safely be used as a unit model: since no AI is
    /// instantiated (only the mesh is read), side effects and crashes from building AI / vehicle AI
    /// / tree growth logic etc. are impossible by construction.
    ///
    /// m_mesh of PropInfo/BuildingInfo/VehicleInfo/TreeInfo is not on the common base class
    /// (PrefabInfo) but declared separately per type, so both the sweep and one-shot resolution are
    /// unified through small generic helpers, Scan&lt;T&gt;/TryGetField&lt;T,TResult&gt;, that take
    /// a per-kind selector (Func&lt;T,TResult&gt;) (avoiding 4 copies of duplicated code without
    /// using reflection = zero runtime cost).
    /// m_isCustomContent/m_Atlas/m_Thumbnail are shared on the PrefabInfo base across all kinds, so
    /// thumbnail resolution (TryGetThumbnail) needs just one kind-independent code path.
    ///
    /// Verified against Assembly-CSharp.dll via reflection (see .superpowers/sdd/task-41-report.md):
    ///   PropInfo.m_mesh/m_material                      ... declared directly
    ///   BuildingInfo.m_mesh/m_material                   ... declared on BuildingInfoBase (base)
    ///   VehicleInfo.m_mesh/m_material                    ... declared on VehicleInfoBase (base)
    ///   TreeInfo.m_mesh/m_material                       ... declared directly (has no m_lodMesh, so it is not used)
    ///   PrefabInfo.m_isCustomContent/m_Atlas/m_Thumbnail ... base shared by all 4 kinds
    ///   PrefabCollection&lt;T&gt;.LoadedCount()/GetLoaded(uint)/FindLoaded(string) ... identical signatures for all 4 kinds
    ///
    /// Main thread only (involves PrefabCollection access).
    /// </summary>
    internal static class AssetCatalog
    {
        private struct Entry
        {
            public string Name;
            public bool IsCustomContent;
        }

        private const int KindCount = 4; // AssetKind.Prop/Building/Vehicle/Tree

        // Per-kind cache of the full prefab sweep results. null is the sentinel for "not scanned
        // yet". Scanning is expensive, so it only happens when Rescan() is explicitly called
        // (= when the UI panel is opened). The total number of buildings/vehicles can be orders of
        // magnitude larger than props, so each kind is cached separately and only the kind actually
        // shown in the list is scanned on demand (no bulk scan of all 4 kinds at once).
        private static readonly List<Entry>[] _all = new List<Entry>[KindCount];

        /// <summary>Discards the sweep results for all kinds so the next GetNames call re-scans.
        /// Called every time AssetAssignPanel is opened, so the list reflects "the assets currently
        /// subscribed".</summary>
        public static void Rescan()
        {
            for (int i = 0; i < KindCount; i++) _all[i] = null;
        }

        /// <summary>
        /// Returns the list of asset names of the given kind that have a usable mesh (m_mesh),
        /// sorted by name ascending.
        /// If customOnly=true, narrows to m_isCustomContent==true (Workshop/custom content) only.
        /// If filter is non-empty, further narrows by case-insensitive substring match.
        /// </summary>
        public static List<string> GetNames(AssetKind kind, bool customOnly, string filter)
        {
            EnsureScanned(kind);

            List<string> result = new List<string>();
            List<Entry> list = _all[(int)kind];
            if (list == null) return result;

            for (int i = 0; i < list.Count; i++)
            {
                Entry e = list[i];
                if (customOnly && !e.IsCustomContent) continue;
                if (!string.IsNullOrEmpty(filter) &&
                    e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add(e.Name);
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>Resolves a mesh directly from a name (a one-shot name lookup that calls
        /// FindLoaded each time, independent of the sweep cache. This is the path UnitMeshSource
        /// calls when creating a unit's visual; it is intentionally uncached so that binding changes
        /// can never be hidden by cache state. Keeps the policy from the PropCatalog era).</summary>
        public static bool TryGetMesh(AssetKind kind, string name, out Mesh mesh)
        {
            mesh = null;
            if (string.IsNullOrEmpty(name)) return false;

            try
            {
                switch (kind)
                {
                    case AssetKind.Prop: return TryGetField<PropInfo, Mesh>(name, p => p.m_mesh, out mesh);
                    case AssetKind.Building: return TryGetField<BuildingInfo, Mesh>(name, b => b.m_mesh, out mesh);
                    case AssetKind.Vehicle: return TryGetField<VehicleInfo, Mesh>(name, v => v.m_mesh, out mesh);
                    case AssetKind.Tree: return TryGetField<TreeInfo, Mesh>(name, t => t.m_mesh, out mesh);
                    default: return false;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetCatalog.TryGetMesh(" + kind + "," + name + ") error: " + e);
                mesh = null;
                return false;
            }
        }

        /// <summary>Resolves the main texture (m_material.mainTexture) directly from a name (like
        /// TryGetMesh, a one-shot lookup that calls FindLoaded each time, uncached).
        /// UnitMaterialFactory calls this as the texture source for material creation. The CS-side
        /// Material object itself is never returned (texture only).</summary>
        public static bool TryGetTexture(AssetKind kind, string name, out Texture texture)
        {
            texture = null;
            if (string.IsNullOrEmpty(name)) return false;

            try
            {
                switch (kind)
                {
                    case AssetKind.Prop: return TryGetField<PropInfo, Texture>(name, p => p.m_material != null ? p.m_material.mainTexture : null, out texture);
                    case AssetKind.Building: return TryGetField<BuildingInfo, Texture>(name, b => b.m_material != null ? b.m_material.mainTexture : null, out texture);
                    case AssetKind.Vehicle: return TryGetField<VehicleInfo, Texture>(name, v => v.m_material != null ? v.m_material.mainTexture : null, out texture);
                    case AssetKind.Tree: return TryGetField<TreeInfo, Texture>(name, t => t.m_material != null ? t.m_material.mainTexture : null, out texture);
                    default: return false;
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetCatalog.TryGetTexture(" + kind + "," + name + ") error: " + e);
                texture = null;
                return false;
            }
        }

        /// <summary>
        /// Resolves the thumbnail of the asset with the given kind and name (PrefabInfo.m_Atlas /
        /// m_Thumbnail; declared on the base shared by all 4 kinds, so the kind branching is fully
        /// contained inside FindLoadedByKind).
        /// Many assets have no thumbnail (m_Atlas==null or m_Thumbnail empty), in which case this
        /// returns false. The caller (AssetAssignPanel) must hide the thumbnail UISprite on false.
        /// </summary>
        public static bool TryGetThumbnail(AssetKind kind, string name, out UITextureAtlas atlas, out string spriteName)
        {
            atlas = null;
            spriteName = null;
            if (string.IsNullOrEmpty(name)) return false;

            try
            {
                PrefabInfo info = FindLoadedByKind(kind, name);
                if (info == null || info.m_Atlas == null || string.IsNullOrEmpty(info.m_Thumbnail)) return false;

                atlas = info.m_Atlas;
                spriteName = info.m_Thumbnail;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetCatalog.TryGetThumbnail(" + kind + "," + name + ") error: " + e);
                atlas = null;
                spriteName = null;
                return false;
            }
        }

        private static PrefabInfo FindLoadedByKind(AssetKind kind, string name)
        {
            switch (kind)
            {
                case AssetKind.Prop: return PrefabCollection<PropInfo>.FindLoaded(name);
                case AssetKind.Building: return PrefabCollection<BuildingInfo>.FindLoaded(name);
                case AssetKind.Vehicle: return PrefabCollection<VehicleInfo>.FindLoaded(name);
                case AssetKind.Tree: return PrefabCollection<TreeInfo>.FindLoaded(name);
                default: return null;
            }
        }

        /// <summary>Common helper that reads the field designated by selector (m_mesh or
        /// m_material.mainTexture) from the instance found via PrefabCollection&lt;T&gt;.FindLoaded(name).
        /// Returns false when selector returns null (no mesh/texture).</summary>
        private static bool TryGetField<T, TResult>(string name, Func<T, TResult> selector, out TResult result)
            where T : PrefabInfo
            where TResult : class
        {
            result = null;
            T info = PrefabCollection<T>.FindLoaded(name);
            if (info == null) return false;

            TResult value = selector(info);
            if (value == null) return false;

            result = value;
            return true;
        }

        private static void EnsureScanned(AssetKind kind)
        {
            int idx = (int)kind;
            if (idx < 0 || idx >= KindCount) return;
            if (_all[idx] != null) return;

            List<Entry> list;
            switch (kind)
            {
                case AssetKind.Prop: list = Scan<PropInfo>(p => p.m_mesh); break;
                case AssetKind.Building: list = Scan<BuildingInfo>(b => b.m_mesh); break;
                case AssetKind.Vehicle: list = Scan<VehicleInfo>(v => v.m_mesh); break;
                case AssetKind.Tree: list = Scan<TreeInfo>(t => t.m_mesh); break;
                default: list = new List<Entry>(); break;
            }

            // Found during the Task66 bug investigation: previously this logged only once per kind
            // per process (a _loggedScanOnce guard), so only the main-menu-time sweep (0 entries)
            // was recorded, and even after Rescan() triggered a re-scan post city load, the updated
            // count could not be traced from the log (which made the "assigned asset does not take
            // effect" investigation extremely difficult). Rescan() is only called when the UI panel
            // is opened, a low-frequency path (not every frame), so always logging never becomes spam.
            ModConfig.Log("AssetCatalog: " + kind + " scan complete, " + list.Count + " with mesh");

            _all[idx] = list;
        }

        /// <summary>Common implementation that sweeps the whole PrefabCollection&lt;T&gt; and keeps
        /// only entries for which meshSelector returns non-null as candidates (the only per-kind
        /// difference is the meshSelector delegate).</summary>
        private static List<Entry> Scan<T>(Func<T, Mesh> meshSelector) where T : PrefabInfo
        {
            List<Entry> list = new List<Entry>();
            try
            {
                int count = PrefabCollection<T>.LoadedCount();
                for (uint i = 0; i < (uint)count; i++)
                {
                    T info = PrefabCollection<T>.GetLoaded(i);
                    if (info == null) continue;

                    Mesh mesh = meshSelector(info);
                    if (mesh == null) continue; // only things with a usable mesh become candidates

                    string name = info.name;
                    if (string.IsNullOrEmpty(name)) continue;

                    list.Add(new Entry { Name = name, IsCustomContent = info.m_isCustomContent });
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("AssetCatalog.Scan<" + typeof(T).Name + "> error: " + e);
            }

            return list;
        }
    }
}
