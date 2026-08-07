using System;
using System.Collections.Generic;
using System.Globalization;

namespace CSWarfront.Core
{
    /// <summary>A material's color and opacity read from MTL.</summary>
    public class MtlColor
    {
        public float R;
        public float G;
        public float B;
        public float Alpha;
    }

    /// <summary>
    /// A pure parser for MTL text (no UnityEngine dependency). MissileDisaster.Core.MtlParser ported
    /// in Task57 (behavior unchanged). CSWarfront's runtime path currently uses only the meshes (see
    /// Game/Models/WarfrontModelProvider) and paints material colors itself for both units and
    /// buildings (UnitMaterialFactory), so this parser is not yet called — it is carried over in the
    /// same MissileDisaster shape for future reading/validation of the .mtl files
    /// tools/gen_models.py emits.
    /// </summary>
    public static class MtlParser
    {
        public static Dictionary<string, MtlColor> Parse(string mtlText)
        {
            var result = new Dictionary<string, MtlColor>();
            if (string.IsNullOrEmpty(mtlText)) return result;

            MtlColor current = null;
            string[] lines = mtlText.Split('\n');

            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                string[] tokens = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;

                string keyword = tokens[0];

                if (keyword == "newmtl")
                {
                    int spaceIndex = line.IndexOf(' ');
                    string name = spaceIndex >= 0 ? line.Substring(spaceIndex + 1).Trim() : "";
                    if (name.Length == 0) continue;

                    // Default: opaque white until Kd/d override it.
                    current = new MtlColor { R = 1f, G = 1f, B = 1f, Alpha = 1f };
                    result[name] = current;
                }
                else if (keyword == "Kd")
                {
                    if (current == null || tokens.Length < 4) continue;
                    float r, g, b;
                    if (!TryParseFloat(tokens[1], out r)) continue;
                    if (!TryParseFloat(tokens[2], out g)) continue;
                    if (!TryParseFloat(tokens[3], out b)) continue;

                    current.R = r;
                    current.G = g;
                    current.B = b;
                }
                else if (keyword == "d")
                {
                    if (current == null || tokens.Length < 2) continue;
                    float alpha;
                    if (!TryParseFloat(tokens[1], out alpha)) continue;

                    current.Alpha = alpha;
                }
                // Everything else (Ka, Ks, Ns, illum, map_*, etc.) is ignored.
            }

            return result;
        }

        private static bool TryParseFloat(string token, out float value)
        {
            return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
