using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    // The model's separate pieces: which points belong together, and which pieces touch.
    public partial class AutoRig
    {
        private void WeldCoincident(HashSet<int>[] adj)
        {
            var byPos = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < Positions.Length; i++)
            {
                var p = Positions[i] * 1000;
                var key = ((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));
                if (!byPos.TryGetValue(key, out var l))
                    byPos[key] = l = new List<int>();
                l.Add(i);
            }

            foreach (var l in byPos.Values)
                foreach (var a in l)
                    foreach (var b in l)
                        if (a != b)
                            adj[a].Add(b);
        }

        private void FindParts(HashSet<int>[] adj, Options opt, List<ObjModel.Triangle> triangles)
        {
            int n = Positions.Length;
            Part = Enumerable.Repeat(-1, n).ToArray();
            int count = 0;
            var sizes = new List<int>();

            for (int s = 0; s < n; s++)
            {
                if (Part[s] != -1)
                    continue;

                int size = 0;
                var stack = new Stack<int>();
                stack.Push(s);
                Part[s] = count;
                while (stack.Count > 0)
                {
                    int v = stack.Pop();
                    size++;
                    foreach (var u in adj[v])
                        if (Part[u] == -1)
                        {
                            Part[u] = count;
                            stack.Push(u);
                        }
                }
                sizes.Add(size);
                count++;
            }

            RigidPart = new bool[n];
            BodyPiece = new bool[n];
            int largest = sizes.Count > 0 ? sizes.Max() : 0;
            int small = Enumerable.Range(0, n).Count(i => sizes[Part[i]] < n * opt.RigidPartShare);
            if (largest >= n * opt.RigidPartShare && small * 2 < n)
            {
                // an arm laid along the fighter's arm that is a piece of its own (Squidward's) bends at the
                // elbow like the fighter's, rather than staying stiff with the body at the shoulder
                var armPart = new int[sizes.Count];
                var armLow = Enumerable.Repeat(float.MaxValue, sizes.Count).ToArray();
                var armHigh = Enumerable.Repeat(float.MinValue, sizes.Count).ToArray();
                for (int i = 0; i < n; i++)
                {
                    if (Conformed.Contains(ModelRegion[i]))
                        armPart[Part[i]]++;
                    armLow[Part[i]] = MathF.Min(armLow[Part[i]], Positions[i].Y);
                    armHigh[Part[i]] = MathF.Max(armHigh[Part[i]], Positions[i].Y);
                }
                float tall = Positions.Max(q => q.Y) - Positions.Min(q => q.Y);
                bool Arm(int part) => armPart[part] * 2 > sizes[part] && armHigh[part] - armLow[part] > tall * 0.15f;
                for (int i = 0; i < n; i++)
                    RigidPart[i] = sizes[Part[i]] < n * opt.RigidPartShare && !Arm(Part[i]);
                return;
            }

            // no piece is big enough to be the body, or most of the model is pieces too small to count: it
            // is built from pieces that touch (BMO's box, face plate, arms and legs; Peggy's arms, shorts
            // and shins). Each point that touches another piece is joined to it, so weights
            // blend across the joins and an arm stays on the box when it bends; only the small pieces
            // (buttons, screws) move as one
            JoinTouchingParts(adj, triangles);
            var bodyCount = new int[count];
            var lowCount = new int[count];
            for (int i = 0; i < n; i++)
            {
                if (ModelRegion[i] == Body)
                    bodyCount[Part[i]]++;
                float legTop = float.IsNaN(HipY) ? LegTopY : HipY;
                if (!float.IsNaN(legTop) && Positions[i].Y < legTop)
                    lowCount[Part[i]]++;
            }
            // a small piece is rigid (a button), but not a leg or arm that reaches a good way along the
            // limb (Peggy's legs below her shorts): that bends at the knee
            var limbCount = new int[count];
            var low = Enumerable.Repeat(float.MaxValue, count).ToArray();
            var high = Enumerable.Repeat(float.MinValue, count).ToArray();
            float height = Positions.Max(q => q.Y) - Positions.Min(q => q.Y);
            for (int i = 0; i < n; i++)
            {
                if (ModelRegion[i] != Body)
                    limbCount[Part[i]]++;
                low[Part[i]] = MathF.Min(low[Part[i]], Positions[i].Y);
                high[Part[i]] = MathF.Max(high[Part[i]], Positions[i].Y);
            }
            bool Limb(int part) => limbCount[part] * 2 > sizes[part] && high[part] - low[part] > height * 0.15f;
            for (int i = 0; i < n; i++)
            {
                RigidPart[i] = sizes[Part[i]] < largest * 0.25f && !Limb(Part[i]);
                // a body piece keeps its shape only when it sits above the legs (BMO's box); one reaching
                // down into them (Peggy's shorts) bends with them, or the legs come out from under it
                // and one that carries the arms (a coat with its sleeves, Linguini's) bends at the shoulders and
                // elbows like any body, or the arms stay stuck straight out in every move
                BodyPiece[i] = !RigidPart[i] && bodyCount[Part[i]] * 2 > sizes[Part[i]] && lowCount[Part[i]] * 10 <= sizes[Part[i]]
                    && limbCount[Part[i]] * 5 <= sizes[Part[i]];
            }
            Report += $"the model is {count} separate pieces: pieces that touch bend together\n";
        }

        private void JoinTouchingParts(HashSet<int>[] adj, List<ObjModel.Triangle> triangles)
        {
            foreach (var (i, join) in TouchingPairs(triangles))
            {
                adj[i].Add(join);
                adj[join].Add(i);
            }
        }

        /// <summary>
        /// Points of one piece that touch another piece (within 1% of the model's size), each with the
        /// nearest corner of the triangle it touches.
        /// </summary>
        private List<(int, int)> TouchingPairs(List<ObjModel.Triangle> triangles)
        {
            var pairs = new List<(int, int)>();
            int n = Positions.Length;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            float reach = (max - min).Length() * 0.01f;

            // triangles in a grid of cells as wide as the reach, so each point only checks those near it
            var grid = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor((p.X - min.X) / reach), (int)MathF.Floor((p.Y - min.Y) / reach), (int)MathF.Floor((p.Z - min.Z) / reach));
            for (int t = 0; t < triangles.Count; t++)
            {
                var tri = triangles[t];
                var a = Positions[tri.A.Position]; var b = Positions[tri.B.Position]; var c = Positions[tri.C.Position];
                var (x0, y0, z0) = Cell(Vector3.Min(a, Vector3.Min(b, c)));
                var (x1, y1, z1) = Cell(Vector3.Max(a, Vector3.Max(b, c)));
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            if (!grid.TryGetValue((x, y, z), out var list))
                                grid[(x, y, z)] = list = new List<int>();
                            list.Add(t);
                        }
            }

            for (int i = 0; i < n; i++)
            {
                var p = Positions[i];
                var (cx, cy, cz) = Cell(p);
                float best = reach;
                int join = -1;
                for (int x = cx - 1; x <= cx + 1; x++)
                    for (int y = cy - 1; y <= cy + 1; y++)
                        for (int z = cz - 1; z <= cz + 1; z++)
                        {
                            if (!grid.TryGetValue((x, y, z), out var list))
                                continue;
                            foreach (int t in list)
                            {
                                var tri = triangles[t];
                                if (Part[tri.A.Position] == Part[i])
                                    continue;
                                var corners = new[] { tri.A.Position, tri.B.Position, tri.C.Position };
                                float d = DistanceToTriangle(p, Positions[corners[0]], Positions[corners[1]], Positions[corners[2]]);
                                if (d >= best)
                                    continue;
                                best = d;
                                join = corners.MinBy(k => Vector3.DistanceSquared(p, Positions[k]));
                            }
                        }
                if (join >= 0)
                    pairs.Add((i, join));
            }
            return pairs;
        }

        private static float DistanceToTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            // the closest point on the triangle (Ericson, Real-Time Collision Detection 5.1.5)
            var ab = b - a; var ac = c - a; var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return ap.Length();
            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return bp.Length();
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return (p - (a + d1 / (d1 - d3) * ab)).Length();
            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return cp.Length();
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return (p - (a + d2 / (d2 - d6) * ac)).Length();
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return (p - (b + (d4 - d3) / ((d4 - d3) + (d5 - d6)) * (c - b))).Length();
            float den = 1 / (va + vb + vc);
            return (p - (a + ab * (vb * den) + ac * (vc * den))).Length();
        }
    }
}
