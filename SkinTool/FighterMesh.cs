using HSDRaw.Common;
using HSDRaw.GX;
using HSDRaw.Tools;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// The original fighter's skinned surface in bind pose, used as the source for weight transfer.
    /// </summary>
    public class FighterMesh
    {
        public class Vertex
        {
            public Vector3 Position;
            public int[] Bones;
            public float[] Weights;
        }

        public readonly List<Vertex> Vertices = new();

        // every three vertices make a triangle
        public readonly List<int> Triangles = new();

        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);

        /// <summary>
        /// Per bone, the size of the area it mostly drives. Small ones (eyes, fingers, jaw) are detail
        /// bones: a different model's surface shouldn't follow them.
        /// </summary>
        public readonly Dictionary<int, float> BoneExtent = new();

        /// <summary>
        /// Bones the original model is actually skinned to. Weighting only to these avoids
        /// the helper joints that the game never animates the way a mesh expects.
        /// </summary>
        public readonly HashSet<int> SkinBones = new();

        public FighterMesh(HSD_JOBJ root, Skeleton skeleton)
        {
            for (int ji = 0; ji < skeleton.Joints.Count; ji++)
            {
                var joint = skeleton.Joints[ji];
                if (joint.Dobj == null)
                    continue;

                foreach (var dobj in joint.Dobj.List)
                {
                    if (dobj.Pobj == null)
                        continue;

                    foreach (var pobj in dobj.Pobj.List)
                        AddPobj(pobj, ji, skeleton);
                }
            }

            foreach (var g in Vertices.Where(v => v.Bones.Length > 0).GroupBy(v => v.Bones[System.Array.IndexOf(v.Weights, v.Weights.Max())]))
            {
                var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
                foreach (var v in g) { lo = Vector3.Min(lo, v.Position); hi = Vector3.Max(hi, v.Position); }
                BoneExtent[g.Key] = (hi - lo).Length();
            }

            foreach (var v in Vertices)
            {
                Min = Vector3.Min(Min, v.Position);
                Max = Vector3.Max(Max, v.Position);
            }
        }

        private void AddPobj(HSD_POBJ pobj, int jointIndex, Skeleton skeleton)
        {
            var dl = pobj.ToDisplayList();
            var envelopes = pobj.EnvelopeWeights;
            bool hasEnvelopes = pobj.HasAttribute(GXAttribName.GX_VA_PNMTXIDX) && envelopes != null;

            int owner = pobj.SingleBoundJOBJ != null ? skeleton.IndexOf(pobj.SingleBoundJOBJ) : jointIndex;

            int offset = 0;
            foreach (var prim in dl.Primitives)
            {
                var verts = dl.Vertices.GetRange(offset, prim.Count);
                offset += prim.Count;

                switch (prim.PrimitiveType)
                {
                    case GXPrimitiveType.Quads: verts = Quads(verts); break;
                    case GXPrimitiveType.TriangleStrip: verts = Strip(verts); break;
                    case GXPrimitiveType.Triangles: break;
                    default: continue;
                }

                foreach (var gv in verts)
                {
                    var pos = new Vector3(gv.POS.X, gv.POS.Y, gv.POS.Z);
                    int[] bones;
                    float[] weights;

                    if (hasEnvelopes)
                    {
                        var en = envelopes[gv.PNMTXIDX / 3];
                        bones = en.JOBJs.Select(skeleton.IndexOf).ToArray();
                        weights = en.Weights;

                        // single weights are stored relative to their bone, blended ones in world space
                        if (en.EnvelopeCount == 1)
                            pos = Vector3.Transform(pos, skeleton.World[bones[0]]);

                        foreach (var b in bones)
                            SkinBones.Add(b);
                    }
                    else
                    {
                        bones = new[] { owner };
                        weights = new[] { 1f };
                        pos = Vector3.Transform(pos, skeleton.World[owner]);
                    }

                    Triangles.Add(Vertices.Count);
                    Vertices.Add(new Vertex { Position = pos, Bones = bones, Weights = weights });
                }
            }
        }
    
        private static List<GX_Vertex> Quads(List<GX_Vertex> v)
        {
            var o = new List<GX_Vertex>();
            for (int i = 0; i + 3 < v.Count; i += 4)
                o.AddRange(new[] { v[i], v[i + 1], v[i + 2], v[i + 2], v[i + 3], v[i] });
            return o;
        }

        // winding doesn't matter here, only which vertices form a triangle
        private static List<GX_Vertex> Strip(List<GX_Vertex> v)
        {
            var o = new List<GX_Vertex>();
            for (int i = 2; i < v.Count; i++)
                o.AddRange(new[] { v[i - 2], v[i - 1], v[i] });
            return o;
        }
    }
}
