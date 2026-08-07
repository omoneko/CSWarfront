using System;
using System.Collections.Generic;
using UnityEngine;
namespace CSWarfront.Game
{
    /// <summary>
    /// Small helper that generates unit-rendering materials in-house.
    /// CS vehicle materials (VehicleInfo.m_material / m_lodMaterial) assume dedicated shaders, and
    /// those shaders require per-instance data (color arrays, transform matrices, lighting state)
    /// supplied by CS's custom renderer. Assigned to a plain MeshRenderer they render
    /// invisible/black (a bug that actually occurred), so CS-derived materials are never borrowed.
    /// Instead we create our own Material with a standard shader and color-code it per faction.
    /// Materials are cached and shared per faction id (up to <see cref="WarfrontSettings.MaxFactions"/>
    /// kinds) and assigned as sharedMaterial (never instantiated per-instance, so nothing leaks).
    /// Main thread only (involves Material/Shader creation).
    ///
    /// Task37: bound assets are not tinted with the faction color; the asset's own look
    /// (texture) is preserved. However, the CS-side Material object itself is still not
    /// borrowed (for the same reason as above). <see cref="TryGetAssetMaterial"/> uses only
    /// AssetCatalog.TryGetTexture (which internally reads m_material.mainTexture of
    /// PropInfo/BuildingInfo/VehicleInfo/TreeInfo) and re-applies it to our own standard-shader
    /// Material (Material.mainTexture / Material(Shader) were verified via reflection against
    /// UnityEngine.dll. Each asset type's m_material was verified via reflection against
    /// Assembly-CSharp.dll; see Task36 task-36-report.md / Task41 task-41-report.md).
    ///
    /// Task41: extended the material cache key from name (string) alone to the (AssetKind, name)
    /// pair. With a name-only key, if for example a building and a prop with the same name are both
    /// loaded, one's texture would be incorrectly reused for the other (PrefabCollection keeps an
    /// independent namespace per kind, so a name match guarantees nothing across kinds). Using
    /// AssetKey as a composite key that includes the kind prevents this.
    /// </summary>
    internal static class UnitMaterialFactory
    {
        // Identification colors for faction ids 0..5: 0=red, 1=blue, 2=green, 3=yellow, 4=magenta,
        // 5=moss green (Task95: the external-assault Invader faction; a dark, muted moss color so it
        // is distinguishable from pure-green Green).
        private static readonly Color[] FactionColors =
        {
            Color.red, Color.blue, Color.green, Color.yellow, Color.magenta,
            new Color(0.42f, 0.49f, 0.25f)
        };

        private static readonly Color FallbackColor = Color.white;

        private static readonly Dictionary<byte, Material> _cache = new Dictionary<byte, Material>();

        /// <summary>Task41: composite (kind, name) key. Same name with different kinds counts as
        /// separate entries.</summary>
        private struct AssetKey : IEquatable<AssetKey>
        {
            public AssetKind Kind;
            public string Name;

            public bool Equals(AssetKey other)
            {
                return Kind == other.Kind && string.Equals(Name, other.Name, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is AssetKey && Equals((AssetKey)obj);
            }

            public override int GetHashCode()
            {
                int hash = (int)Kind;
                if (Name != null) hash = (hash * 397) ^ Name.GetHashCode();
                return hash;
            }
        }

        // Per (kind, name) material cache (introduced in Task37, key extended to include the kind
        // in Task41). Used exclusively by TryGetAssetMaterial.
        private static readonly Dictionary<AssetKey, Material> _assetCache = new Dictionary<AssetKey, Material>();

        private static Shader _shader;
        private static bool _shaderResolved;
        private static bool _loggedShaderFailure;

        /// <summary>
        /// Gets the material for a faction id (creating and caching it if absent).
        /// Returns false in environments where no shader can be found at all (theoretical only).
        /// </summary>
        public static bool TryGetFactionMaterial(byte factionId, out Material material)
        {
            Material cached;
            if (_cache.TryGetValue(factionId, out cached) && cached != null)
            {
                material = cached;
                return true;
            }

            Shader shader = ResolveShader();
            if (shader == null)
            {
                material = null;
                return false;
            }

            try
            {
                Material mat = new Material(shader);
                mat.color = ColorForFaction(factionId);
                _cache[factionId] = mat;
                material = mat;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMaterialFactory.TryGetFactionMaterial(" + factionId + ") error: " + e);
                material = null;
                return false;
            }
        }

        /// <summary>
        /// Task37: gets the material for a bound asset (creating and caching it if absent).
        /// Task41: extended coverage beyond props (buildings/vehicles/trees) and changed the cache
        /// key to (kind, name).
        /// Creates our own standard-shader Material, keeps the color white (no tint = no faction
        /// coloring), and borrows only the mainTexture from the asset's own material (via
        /// AssetCatalog.TryGetTexture).
        /// The CS Material object itself is never assigned (same reason as TryGetFactionMaterial).
        /// If no texture can be obtained, we fall back to a plain white standard material
        /// (never to the faction color — honoring requirement 2, "stop tinting with faction colors").
        /// </summary>
        public static bool TryGetAssetMaterial(AssetKind kind, string assetName, out Material material)
        {
            if (string.IsNullOrEmpty(assetName))
            {
                material = null;
                return false;
            }

            AssetKey key = new AssetKey { Kind = kind, Name = assetName };

            Material cached;
            if (_assetCache.TryGetValue(key, out cached) && cached != null)
            {
                material = cached;
                return true;
            }

            Shader shader = ResolveShader();
            if (shader == null)
            {
                material = null;
                return false;
            }

            try
            {
                Texture mainTexture;
                AssetCatalog.TryGetTexture(kind, assetName, out mainTexture);

                Material mat = new Material(shader);
                mat.color = Color.white; // no tint; preserve the asset's own look.
                if (mainTexture != null) mat.mainTexture = mainTexture;

                _assetCache[key] = mat;
                material = mat;
                return true;
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMaterialFactory.TryGetAssetMaterial(" + kind + "," + assetName + ") error: " + e);
                material = null;
                return false;
            }
        }

        // TryGetSolidColorMaterial, added in Task57 (fixed-color material generation dedicated to
        // the default model of the military base prefab = WarfrontBasePrefab), was deleted because
        // Task82 completely removed the electricity-tab cloned-prefab mechanism itself, leaving no
        // callers.

        private static Color ColorForFaction(byte factionId)
        {
            return factionId < FactionColors.Length ? FactionColors[factionId] : FallbackColor;
        }

        private static Shader ResolveShader()
        {
            if (_shaderResolved) return _shader;
            _shaderResolved = true;

            try
            {
                Shader shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
                if (shader == null) shader = Shader.Find("Diffuse");
                _shader = shader;

                if (shader == null && !_loggedShaderFailure)
                {
                    _loggedShaderFailure = true;
                    ModConfig.LogError("UnitMaterialFactory: failed to resolve shader (Standard/Legacy Shaders/Diffuse all failed), units will not render");
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("UnitMaterialFactory.ResolveShader error: " + e);
                _shader = null;
            }

            return _shader;
        }
    }
}
