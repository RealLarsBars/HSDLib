using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Gives a model with no real legs (a round body on little feet) a pair of legs, as a modder would:
    /// the bottom of the body is cut down the middle into two halves, each half is closed off on the
    /// inside, and later stretched down the fighter's leg so kicks and runs show a leg.
    /// </summary>
    public static class Legs
    {
        /// <summary>
        /// Splits the model below <paramref name="cut"/> (a share of its height) down the middle.
        /// Changes the model in place and returns, per position, which leg it belongs to:
        /// +1 for the +x side, -1 for the other, 0 for the body above the cut.
        /// </summary>
        public static int[] Split(ObjModel model, Matrix4x4 rot, float cut)
        {
            var pts = model.Positions.Select(p => Vector3.Transform(p, rot)).ToList();
            float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            float cx = (pts.Min(p => p.X) + pts.Max(p => p.X)) / 2;
            float cutY = minY + cut * (maxY - minY);
            int Natural(int v) => pts[v].X >= cx ? 1 : -1;

            // edges that are open in the source (a model's own holes) are left alone
            var before = EdgeCounts(model.Triangles.Select(t => (t.A.Position, t.B.Position, t.C.Position)));

            var side = new List<int>(pts.Select(p => p.Y < cutY ? (p.X >= cx ? 1 : -1) : 0));
            var copies = new Dictionary<(int, int), int>();
            var original = new Dictionary<int, int>();
            var legTris = new List<(ObjModel.Triangle tri, int side)>();

            ObjModel.Corner Move(ObjModel.Corner c, int s)
            {
                if (pts[c.Position].Y >= cutY || Natural(c.Position) == s)
                    return c;
                if (!copies.TryGetValue((c.Position, s), out int copy))
                {
                    copy = model.Positions.Count;
                    model.Positions.Add(model.Positions[c.Position]);
                    pts.Add(pts[c.Position]);
                    side.Add(s);
                    copies[(c.Position, s)] = copy;
                    original[copy] = c.Position;
                }
                return new ObjModel.Corner { Position = copy, UV = c.UV, Normal = c.Normal };
            }

            foreach (var t in model.Triangles)
            {
                var corners = new[] { t.A, t.B, t.C };
                if (corners.Any(c => pts[c.Position].Y >= cutY))
                    continue;
                int s = corners.Sum(c => pts[c.Position].X - cx) >= 0 ? 1 : -1;
                t.A = Move(t.A, s);
                t.B = Move(t.B, s);
                t.C = Move(t.C, s);
                legTris.Add((t, s));
            }

            // the cut: edges of a leg that only one triangle uses now, but two did before
            var after = EdgeCounts(model.Triangles.Select(t => (t.A.Position, t.B.Position, t.C.Position)));
            int Orig(int v) => original.TryGetValue(v, out var o) ? o : v;
            var inverse = Matrix4x4.Transpose(rot);
            foreach (int s in new[] { 1, -1 })
            {
                var open = new List<(ObjModel.Corner a, ObjModel.Corner b, ObjModel.Triangle tri)>();
                foreach (var (t, ts) in legTris.Where(e => e.side == s))
                {
                    var c = new[] { t.A, t.B, t.C };
                    for (int k = 0; k < 3; k++)
                    {
                        var a = c[k];
                        var b = c[(k + 1) % 3];
                        if (after[Key(a.Position, b.Position)] == 1 && before.GetValueOrDefault(Key(Orig(a.Position), Orig(b.Position))) > 1)
                            open.Add((a, b, t));
                    }
                }
                if (open.Count < 2)
                    continue;

                // close it with a fan from the middle of the cut, facing the other leg
                var ids = open.SelectMany(e => new[] { e.a.Position, e.b.Position }).Distinct().ToList();
                var middle = ids.Aggregate(Vector3.Zero, (acc, v) => acc + model.Positions[v]) / ids.Count;
                int center = model.Positions.Count;
                model.Positions.Add(middle);
                pts.Add(Vector3.Transform(middle, rot));
                side.Add(s);
                int normal = model.Normals.Count;
                model.Normals.Add(Vector3.TransformNormal(new Vector3(-s, 0, 0), inverse));
                foreach (var (a, b, t) in open)
                    model.Triangles.Add(new ObjModel.Triangle
                    {
                        A = new ObjModel.Corner { Position = b.Position, UV = a.UV, Normal = normal },
                        B = new ObjModel.Corner { Position = a.Position, UV = a.UV, Normal = normal },
                        C = new ObjModel.Corner { Position = center, UV = a.UV, Normal = normal },
                        Material = t.Material,
                        Object = t.Object,
                    });
            }

            return side.ToArray();
        }

        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

        static Dictionary<(int, int), int> EdgeCounts(IEnumerable<(int, int, int)> tris)
        {
            var counts = new Dictionary<(int, int), int>();
            foreach (var (a, b, c) in tris)
                foreach (var e in new[] { Key(a, b), Key(b, c), Key(c, a) })
                    counts[e] = counts.GetValueOrDefault(e) + 1;
            return counts;
        }
    }
}
