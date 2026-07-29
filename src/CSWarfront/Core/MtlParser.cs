using System;
using System.Collections.Generic;
using System.Globalization;

namespace CSWarfront.Core
{
    /// <summary>MTLから読み取ったマテリアルの色と透明度。</summary>
    public class MtlColor
    {
        public float R;
        public float G;
        public float B;
        public float Alpha;
    }

    /// <summary>
    /// MTLテキストを解析する純粋パーサ（UnityEngine非依存）。MissileDisaster.Core.MtlParser を
    /// Task57 でポートしたもの（挙動は変更していない）。CSWarfrontの実行時パスは現状メッシュのみを
    /// 使い（Game/Models/WarfrontModelProvider参照）、マテリアル色はユニット/建物とも自前生成
    /// （UnitMaterialFactory）で塗るため、このパーサ自体はまだ呼び出されていないが、
    /// tools/gen_models.py が出力する .mtl の将来的な読み込み・検証用に MissileDisaster と同じ
    /// 形で移植しておく。
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
