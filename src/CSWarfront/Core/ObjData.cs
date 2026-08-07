using System.Collections.Generic;

namespace CSWarfront.Core
{
    /// <summary>One material's worth of triangles read from an OBJ.</summary>
    public class ObjSubmesh
    {
        /// <summary>The usemtl name. Faces appearing before the first usemtl get "".</summary>
        public string Material;

        /// <summary>0-based vertex-position indices. Three per triangle (Unity winding).</summary>
        public List<int> Triangles;

        public ObjSubmesh()
        {
            Material = "";
            Triangles = new List<int>();
        }
    }

    /// <summary>
    /// The parse result of our self-generated OBJ files (tools/gen_models.py; Blender exports in the
    /// same format pass too) — a Unity-free intermediate representation. MissileDisaster.Core.ObjData
    /// ported in Task57.
    /// </summary>
    public class ObjData
    {
        /// <summary>Per-vertex x,y,z laid out flat (X already flipped).</summary>
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
