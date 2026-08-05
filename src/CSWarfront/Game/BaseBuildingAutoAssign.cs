using System;
using System.Collections.Generic;
using System.Text;
using CSWarfront.Core;

namespace CSWarfront.Game
{
    /// <summary>
    /// Task109（ユーザー要望「公開している建物アセットをサブスクライブしていれば自動でデフォルト割り当てに」）:
    /// ロード済みの建物プレハブを走査し、CS:WARFRONT用として公開されている建物アセットを基地種別へ
    /// 自動割り当てする。
    ///
    /// 判定はプレハブ名の正規化一致のみ（Workshopアセットのプレハブ名は "&lt;itemId&gt;.&lt;アセット名&gt;_Data"
    /// という形式なので、先頭のid・末尾の"_Data"・記号・大小文字を落として比較する）。候補名は
    /// models.blend／Asset Editor向けFBX（tools/export_asset_editor.py）で使っているアセット名に合わせてある。
    ///
    /// これはあくまで「未指定のときの既定値」であり、Optionsでの手動指定が常に優先される
    /// （<see cref="BaseBuildingDesignation"/>参照）。自動割り当ての結果はログに出すので、
    /// 意図しないアセットを拾っていないか確認できる。
    /// </summary>
    internal static class BaseBuildingAutoAssign
    {
        /// <summary>基地種別ごとの候補名（正規化済み）。先に一致したものを採用する。</summary>
        private static readonly KeyValuePair<BaseType, string[]>[] Candidates =
        {
            new KeyValuePair<BaseType, string[]>(BaseType.Army,
                new[] { "basearmy", "armybase", "warfrontarmybase", "militarybase" }),
            new KeyValuePair<BaseType, string[]>(BaseType.Navy,
                new[] { "basenavy", "navybase", "navalbase", "warfrontnavalbase" }),
            new KeyValuePair<BaseType, string[]>(BaseType.AirForce,
                new[] { "baseair", "airbase", "airforcebase", "warfrontairbase" }),
            new KeyValuePair<BaseType, string[]>(BaseType.MissileBase,
                new[] { "basemissile", "missilebase", "warfrontmissilebase" }),
            new KeyValuePair<BaseType, string[]>(BaseType.Bunker,
                new[] { "bunker", "warfrontbunker" }),
            new KeyValuePair<BaseType, string[]>(BaseType.ArtilleryPost,
                new[] { "artilleryposition", "artillerypost", "warfrontartilleryposition" }),
            new KeyValuePair<BaseType, string[]>(BaseType.SupplyDepot,
                new[] { "supplypoint", "supplydepot", "warfrontsupplypoint" }),
            new KeyValuePair<BaseType, string[]>(BaseType.Trench,
                new[] { "trench", "warfronttrench" }),
            new KeyValuePair<BaseType, string[]>(BaseType.CargoStation,
                new[] { "railyard", "cargostation", "militarycargostation", "warfrontrailyard" })
        };

        /// <summary>ロード済み建物プレハブを走査し、種別→プレハブ名の自動割り当てを返す
        /// （1件も見つからなければ空）。メインスレッド専用（OnLevelLoadedから呼ぶ）。</summary>
        public static Dictionary<BaseType, string> Detect()
        {
            var found = new Dictionary<BaseType, string>();

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
                        if (found.ContainsKey(type)) continue; // 先に見つかったものを優先（決定的）

                        string[] names = Candidates[c].Value;
                        for (int n = 0; n < names.Length; n++)
                        {
                            if (key != names[n]) continue;
                            found[type] = info.name;
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

        /// <summary>"1234567890.Bunker_Data" → "bunker"。Workshopのidプレフィックス・"_Data"接尾・
        /// 記号・大小文字の違いを吸収する。</summary>
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
