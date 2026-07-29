using System;
using System.Collections.Generic;
using System.Globalization;

namespace CSWarfront.Core
{
    /// <summary>
    /// OBJテキストを解析する純粋パーサ（UnityEngine非依存）。MissileDisaster.Core.ObjParser を
    /// Task57 でポートしたもの（挙動は変更していない）。
    /// OBJは右手座標系・Unityは左手座標系のため、X座標の反転と三角形の巻き順反転を
    /// セットで行うことで、モデルが裏返らずに正しく描画されるようにしている
    /// （src/CSWarfront/Models/*.obj は tools/gen_models.py がこの規約に合わせて出力している）。
    /// </summary>
    public static class ObjParser
    {
        public static ObjData Parse(string objText)
        {
            var data = new ObjData();
            if (string.IsNullOrEmpty(objText)) return data;

            ObjSubmesh currentSubmesh = null;
            string[] lines = objText.Split('\n');

            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li].Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                string[] tokens = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;

                string keyword = tokens[0];

                if (keyword == "v")
                {
                    if (tokens.Length < 4) continue;
                    float x, y, z;
                    if (!TryParseFloat(tokens[1], out x)) continue;
                    if (!TryParseFloat(tokens[2], out y)) continue;
                    if (!TryParseFloat(tokens[3], out z)) continue;

                    data.Positions.Add(-x);
                    data.Positions.Add(y);
                    data.Positions.Add(z);
                }
                else if (keyword == "usemtl")
                {
                    int spaceIndex = line.IndexOf(' ');
                    string name = spaceIndex >= 0 ? line.Substring(spaceIndex + 1).Trim() : "";

                    currentSubmesh = new ObjSubmesh();
                    currentSubmesh.Material = name;
                    data.Submeshes.Add(currentSubmesh);
                }
                else if (keyword == "f")
                {
                    if (tokens.Length < 4) continue;

                    var faceIndices = new List<int>(tokens.Length - 1);
                    bool ok = true;
                    for (int i = 1; i < tokens.Length; i++)
                    {
                        int index0;
                        if (!TryParseFaceVertex(tokens[i], data.VertexCount, out index0))
                        {
                            ok = false;
                            break;
                        }
                        faceIndices.Add(index0);
                    }
                    if (!ok) continue;

                    if (currentSubmesh == null)
                    {
                        currentSubmesh = new ObjSubmesh();
                        currentSubmesh.Material = "";
                        data.Submeshes.Add(currentSubmesh);
                    }

                    // Fan-triangulate (v0,v1,v2), (v0,v2,v3), ... and reverse winding (c,b,a).
                    for (int i = 1; i < faceIndices.Count - 1; i++)
                    {
                        int a = faceIndices[0];
                        int b = faceIndices[i];
                        int c = faceIndices[i + 1];

                        currentSubmesh.Triangles.Add(c);
                        currentSubmesh.Triangles.Add(b);
                        currentSubmesh.Triangles.Add(a);
                    }
                }
                // vn, vt, o, g, s, mtllib and anything else are ignored.
            }

            return data;
        }

        private static bool TryParseFaceVertex(string token, int currentVertexCount, out int index0)
        {
            index0 = 0;
            if (string.IsNullOrEmpty(token)) return false;

            int slash = token.IndexOf('/');
            string posPart = slash >= 0 ? token.Substring(0, slash) : token;

            int raw;
            if (!int.TryParse(posPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw)) return false;
            if (raw == 0) return false;

            if (raw > 0)
            {
                index0 = raw - 1;
            }
            else
            {
                index0 = currentVertexCount + raw;
            }
            if (index0 < 0) return false;

            return true;
        }

        private static bool TryParseFloat(string token, out float value)
        {
            return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
