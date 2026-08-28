using System;
using System.Collections.Generic;
using System.Text;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task109 (user request "if the published building assets are subscribed, assign them as
    /// defaults automatically"): scans the loaded building prefabs and auto-assigns building assets
    /// published for CS:WARFRONT to their base types.
    ///
    /// Matching is done purely by normalized prefab-name comparison (Workshop asset prefab names have
    /// the form "&lt;itemId&gt;.&lt;asset name&gt;_Data", so the leading id, trailing "_Data",
    /// punctuation, and letter case are stripped before comparison). The candidate names match the
    /// asset names used in models.blend / the Asset Editor FBX export (tools/export_asset_editor.py).
    ///
    /// This is strictly a "default when nothing is specified"; a manual assignment in Options always
    /// takes precedence (see <see cref="BaseBuildingDesignation"/>). The auto-assignment results are
    /// logged so it can be verified that no unintended assets were picked up.
    /// </summary>
    internal static class BaseBuildingAutoAssign
    {
        /// <summary>Candidate names per base type (already normalized). The first match wins.</summary>
        private static readonly KeyValuePair<BaseType, string[]>[] Candidates =
        {
            // Task112 (Workshop report by StarfleetPups: "auto detect says none"): the published base
            // assets are actually named MilitaryBase_Army / _Navy / _AirForce / _Missile — none of which
            // matched the original candidate lists, so all four base types failed to auto-assign while
            // the fortifications worked. The real names lead each list now.
            new KeyValuePair<BaseType, string[]>(BaseType.Army,
                new[] { "militarybasearmy", "basearmy", "armybase", "warfrontarmybase", "militarybase" }),
            new KeyValuePair<BaseType, string[]>(BaseType.Navy,
                new[] { "militarybasenavy", "basenavy", "navybase", "navalbase", "warfrontnavalbase" }),
            new KeyValuePair<BaseType, string[]>(BaseType.AirForce,
                new[] { "militarybaseairforce", "baseair", "airbase", "airforcebase", "warfrontairbase" }),
            new KeyValuePair<BaseType, string[]>(BaseType.MissileBase,
                new[] { "militarybasemissile", "basemissile", "missilebase", "warfrontmissilebase" }),
            new KeyValuePair<BaseType, string[]>(BaseType.Bunker,
                new[] { "bunker", "warfrontbunker" }),
            new KeyValuePair<BaseType, string[]>(BaseType.ArtilleryPost,
                new[] { "artilleryposition", "artillerypost", "warfrontartilleryposition" }),
            new KeyValuePair<BaseType, string[]>(BaseType.SupplyDepot,
                new[] { "supplypoint", "supplydepot", "warfrontsupplypoint" }),
            new KeyValuePair<BaseType, string[]>(BaseType.Trench,
                new[] { "trench", "warfronttrench" }),
            new KeyValuePair<BaseType, string[]>(BaseType.CargoStation,
                new[] { "railyard", "cargostation", "militarycargostation", "warfrontrailyard" }),
            // Task117: candidate names for the future published assets of the two new emplacements.
            new KeyValuePair<BaseType, string[]>(BaseType.AtPillbox,
                new[] { "atpillbox", "antitankpillbox", "pillbox", "warfrontatpillbox" }),
            new KeyValuePair<BaseType, string[]>(BaseType.AaPosition,
                new[] { "aaposition", "antiairposition", "aadefenseposition", "warfrontaaposition" })
        };

        /// <summary>Task156: Workshop ids of the DLC-free rebuilds. The originals were built in the
        /// asset editor on top of a Natural Disasters building and a Mass Transit ferry depot, so they
        /// never appeared for anyone who did not own those (Workshop report by siddyskylines1989: "no
        /// wonder why i havent been able to see them at all"). The rebuilds carry the same asset names,
        /// so a player with both subscribed would otherwise get whichever the game happened to load
        /// first. The rebuild wins: it is the one that works for everybody.</summary>
        private static readonly string[] PreferredWorkshopIds =
        {
            "3791623137", // AAPosition (NoDLC)
            "3791623651", // ATPillbox (NoDLC)
            "3791624027"  // MilitaryBase_Navy (NoDLC)
        };

        /// <summary>Scans the loaded building prefabs and returns the auto-assignment of base type to
        /// prefab name (empty if nothing is found). Main thread only (called from OnLevelLoaded).</summary>
        public static Dictionary<BaseType, string> Detect()
        {
            var found = new Dictionary<BaseType, string>();
            // Task156: how good the match already held for each type is - lower is better. See Rank.
            var bestRank = new Dictionary<BaseType, int>();

            try
            {
                int count = PrefabCollection<BuildingInfo>.LoadedCount();
                for (int i = 0; i < count; i++)
                {
                    BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded((uint)i);
                    if (info == null || string.IsNullOrEmpty(info.name)) continue;

                    string key = NormalizeAssetName(info.name);
                    if (key.Length == 0) continue;

                    for (int c = 0; c < Candidates.Length; c++)
                    {
                        BaseType type = Candidates[c].Key;
                        string[] names = Candidates[c].Value;
                        for (int n = 0; n < names.Length; n++)
                        {
                            if (key != names[n]) continue;

                            // Task156: pick the best match rather than the first one the game happens
                            // to hand us. Prefab order used to decide, which is fine while only one
                            // asset can match but not once a DLC-free rebuild shares its name with the
                            // original. Ties keep the earlier prefab, exactly as before.
                            int rank = Rank(info.name, n);
                            int held;
                            if (!bestRank.TryGetValue(type, out held) || rank < held)
                            {
                                found[type] = info.name;
                                bestRank[type] = rank;
                            }
                            break;
                        }
                    }
                }

                if (found.Count == 0)
                {
                    ModConfig.Log("BaseBuildingAutoAssign: no matching building assets found " +
                        "(subscribe to the CS:WARFRONT building assets, or assign them manually in Options)");
                }
                else
                {
                    var sb = new StringBuilder("BaseBuildingAutoAssign: detected");
                    foreach (KeyValuePair<BaseType, string> kv in found)
                        sb.Append(" | ").Append(kv.Key).Append("='").Append(kv.Value).Append("'");
                    ModConfig.Log(sb.ToString());
                }
            }
            catch (Exception e)
            {
                ModConfig.LogError("BaseBuildingAutoAssign.Detect error (continuing with manual assignments only): " + e);
            }

            return found;
        }

        /// <summary>Task156: how good a match is - lower wins. A preferred Workshop id beats every
        /// ordinary asset; within each of those two bands, the earlier candidate name wins.</summary>
        private static int Rank(string prefabName, int nameIndex)
        {
            return (IsPreferred(prefabName) ? 0 : 1000) + nameIndex;
        }

        /// <summary>Whether this prefab came from one of the PreferredWorkshopIds. Workshop prefabs are
        /// named "&lt;itemId&gt;.&lt;asset name&gt;_Data"; anything without an id prefix (a local asset
        /// in the Addons folder) is simply not one of them.</summary>
        private static bool IsPreferred(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            int dot = prefabName.IndexOf('.');
            if (dot <= 0) return false;
            string id = prefabName.Substring(0, dot);
            for (int i = 0; i < PreferredWorkshopIds.Length; i++)
                if (id == PreferredWorkshopIds[i]) return true;
            return false;
        }

        /// <summary>"1234567890.Bunker_Data" → "bunker". Absorbs the Workshop id prefix, the "_Data"
        /// suffix, punctuation, and letter-case differences.</summary>
        private static string NormalizeAssetName(string prefabName)
        {
            string name = prefabName;

            int dot = name.IndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) name = name.Substring(dot + 1);

            if (name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 5);

            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
                else if (ch >= 'A' && ch <= 'Z') sb.Append((char)(ch + 32));
            }
            return sb.ToString();
        }
    }
}
