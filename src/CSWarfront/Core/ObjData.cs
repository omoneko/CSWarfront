using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>OBJ から読み取った1マテリアルぶんの三角形群。</summary>
    public class ObjSubmesh
    {
        /// <summary>usemtl 名。最初の usemtl より前に現れた面は "" になる。</summary>
        public string Material;

        /// <summary>0-based頂点位置インデックス。3つで1三角形（Unity巻き順）。</summary>
        public List<int> Triangles;

        public ObjSubmesh()
        {
            Material = "";
            Triangles = new List<int>();
        }
    }

    /// <summary>
    /// 自前生成OBJ（tools/gen_models.py。Blender書き出しでも同じ形式なら通る）の解析結果
    /// （Unity非依存の中間表現）。MissileDisaster.Core.ObjData を Task57 でポートしたもの。
    /// </summary>
    public class ObjData
    {
        /// <summary>頂点ごとの x,y,z をフラットに並べたもの（Xは反転済み）。</summary>
        public List<float> Positions;

        public List<ObjSubmesh> Submeshes;

        public ObjData()
        {
            Positions = new List<float>();
            Submeshes = new List<ObjSubmesh>();
        }

        public int VertexCount { get { return Positions.Count / 3; } }
    }
}
