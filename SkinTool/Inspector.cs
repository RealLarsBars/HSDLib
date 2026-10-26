using HSDRaw;
using HSDRaw.Common;
using HSDRaw.GX;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkinTool
{
    /// <summary>
    /// Prints a costume's joints and mesh slots.
    /// </summary>
    public static class Inspector
    {
        /// <summary>
        /// Share of triangles whose winding points the same way as their vertex normals; the game
        /// culls back faces, so a mesh near 0% shows its inside.
        /// </summary>
        static float Facing(HSD_DOBJ d)
        {
            int agree = 0, total = 0;
            foreach (var pobj in d.Pobj.List)
            {
                var dl = pobj.ToDisplayList();
                int offset = 0;
                foreach (var prim in dl.Primitives)
                {
                    var v = dl.Vertices.GetRange(offset, prim.Count);
                    offset += prim.Count;
                    var tris = new List<(GX_Vertex, GX_Vertex, GX_Vertex)>();
                    if (prim.PrimitiveType == GXPrimitiveType.Triangles)
                        for (int i = 0; i + 2 < v.Count; i += 3) tris.Add((v[i], v[i + 1], v[i + 2]));
                    else if (prim.PrimitiveType == GXPrimitiveType.TriangleStrip)
                        for (int i = 2; i < v.Count; i++) tris.Add(i % 2 == 0 ? (v[i - 2], v[i - 1], v[i]) : (v[i - 1], v[i - 2], v[i]));
                    foreach (var (a, b, c) in tris)
                    {
                        if (a.PNMTXIDX != b.PNMTXIDX || a.PNMTXIDX != c.PNMTXIDX) continue;
                        var pa = new System.Numerics.Vector3(a.POS.X, a.POS.Y, a.POS.Z);
                        var pb = new System.Numerics.Vector3(b.POS.X, b.POS.Y, b.POS.Z);
                        var pc = new System.Numerics.Vector3(c.POS.X, c.POS.Y, c.POS.Z);
                        var n = new System.Numerics.Vector3(a.NRM.X + b.NRM.X + c.NRM.X, a.NRM.Y + b.NRM.Y + c.NRM.Y, a.NRM.Z + b.NRM.Z + c.NRM.Z);
                        var face = System.Numerics.Vector3.Cross(pb - pa, pc - pa);
                        if (face.LengthSquared() < 1e-12f || n.LengthSquared() < 1e-12f) continue;
                        total++;
                        if (System.Numerics.Vector3.Dot(face, n) > 0) agree++;
                    }
                }
            }
            return total == 0 ? float.NaN : agree / (float)total;
        }

        public static int Run(string path, bool verbose = false)
        {
            var f = new HSDRawFile(path);
            foreach (var r in f.Roots)
                Console.WriteLine($"root {r.Name} {r.Data?.GetType().Name}");

            var root = (HSD_JOBJ)f.Roots[0].Data;
            var list = root.TreeList;
            Console.WriteLine($"joints {list.Count}");

            var sk = new Skeleton(root);
            for (int i = 0; i < list.Count; i++)
            {
                var w = sk.Position(i);
                // how far the stored bind matrix is from the one worked out from the joints
                string bind = "";
                if (list[i].InverseWorldTransform != null)
                {
                    var a = Skeleton.FromHsd(list[i].InverseWorldTransform);
                    var b = sk.InverseBind(i);
                    float d = 0;
                    for (int r = 0; r < 4; r++)
                        for (int c = 0; c < 4; c++)
                            d = MathF.Max(d, MathF.Abs(a[r, c] - b[r, c]));
                    bind = $" bind off by {d:G2}";
                }
                Console.WriteLine($"joint {i} parent {sk.Parent[i]} at ({w.X:F2}, {w.Y:F2}, {w.Z:F2}) {list[i].Flags & (JOBJ_FLAG.CLASSICAL_SCALING | JOBJ_FLAG.MTX_SCALE_COMPENSATE)}{bind}");
            }

            int slot = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var j = list[i];
                if (j.Dobj == null)
                    continue;

                Console.WriteLine($"joint {i} flags={j.Flags}");
                foreach (var d in j.Dobj.List)
                {
                    var texs = d.Mobj?.Textures?.List.Select(t => $"{t.ImageData?.Format} {t.ImageData?.Width}x{t.ImageData?.Height}") ?? Enumerable.Empty<string>();
                    Console.Write($"  slot {slot++}: {d.Mobj?.RenderFlags} [{string.Join(", ", texs)}]");
                    if (d.Pobj == null)
                    {
                        Console.WriteLine(" empty");
                        continue;
                    }
                    if (verbose)
                        foreach (var t in d.Mobj?.Textures?.List ?? Enumerable.Empty<HSD_TOBJ>())
                            Console.Write($"\n      tex {t.Flags} wrap {t.WrapS}/{t.WrapT} repeat {t.RepeatS}/{t.RepeatT} scale {t.SX}/{t.SY} rot {t.RX}/{t.RY}/{t.RZ} pos {t.TX}/{t.TY} map {t.TexMapID} src {t.GXTexGenSrc} blend {t.Blending} mag {t.MagFilter} lod {(t.LOD != null)} tev {(t.TEV != null)}");

                    if (verbose)
                        Console.Write($"\n      faces agreeing with their normals: {Facing(d):P0}, drawn sides {string.Join("/", d.Pobj.List.Select(p => p.Flags & (POBJ_FLAG.CULLBACK | POBJ_FLAG.CULLFRONT)).Distinct())}");

                    var bones = d.Pobj.List
                        .SelectMany(p => p.EnvelopeWeights ?? Array.Empty<HSD_Envelope>())
                        .SelectMany(e => e.JOBJs)
                        .Select(b => list.IndexOf(b))
                        .Distinct()
                        .OrderBy(b => b);
                    int indices = d.Pobj.List.Sum(p => p.ToDisplayList().Primitives.Sum(pr => pr.Indices.Length));
                    Console.WriteLine($" {indices} indices, bones {string.Join(",", bones)}");
                }
            }

            return 0;
        }
    }
}
