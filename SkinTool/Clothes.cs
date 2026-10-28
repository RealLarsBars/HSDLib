using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Finds clothes worn over the legs: a coat, apron, skirt or dress that hangs across between them, so
    /// the legs never seem to part from the outside. Cut across at each height, such clothes are a run of
    /// surface crossing the middle with the legs (other runs, one each side) under it: all round them for a
    /// skirt or a coat, in front for an apron. Set aside, they leave the model's real legs.
    /// </summary>
    public class Clothes
    {
        // per position: part of a layer worn over the legs
        public bool[] Over;
        // per position: on the legs under it
        public bool[] Under;
        // the lowest and highest point of that layer, as shares of the model's height
        public float Hem, Top;
        // how far up the model's legs part with the layer set aside (as a share of its height)
        public float LegsTop;
        // triangles of the layer that hang across between the legs
        public int Across;

        public bool Found => Across > 0;

        const int Slices = 50;

        /// <summary>
        /// <paramref name="crotch"/> measures where a set of triangles parts into two legs (AutoRig.Crotch).
        /// </summary>
        public static Clothes Find(Vector3[] pts, IList<(int a, int b, int c)> tris, Vector3 min, Vector3 size,
            Func<IEnumerable<(int, int, int)>, float> crotch)
        {
            float cx = min.X + size.X / 2;
            float minY = min.Y, height = size.Y;
            // each triangle's facing (only which way round it is horizontally matters: a nearly flat one,
            // a hem's underside, makes no outline in a cross-section)
            var face = new Vector3[tris.Count];
            for (int t = 0; t < tris.Count; t++)
            {
                var (a, b, c) = tris[t];
                var n = Vector3.Cross(pts[b] - pts[a], pts[c] - pts[a]);
                face[t] = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.Zero;
            }

            // each slice's cross-section, a set of segments in the floor plane
            var bySlice = new List<int>[Slices];
            for (int s = 0; s < Slices; s++)
                bySlice[s] = new List<int>();
            for (int t = 0; t < tris.Count; t++)
            {
                var (a, b, c) = tris[t];
                float lo = MathF.Min(pts[a].Y, MathF.Min(pts[b].Y, pts[c].Y)), hi = MathF.Max(pts[a].Y, MathF.Max(pts[b].Y, pts[c].Y));
                for (int s = SliceOf(lo, minY, height); s <= SliceOf(hi, minY, height); s++)
                    bySlice[s].Add(t);
            }
            float SliceY(int s) => minY + (s + 0.5f) * height / Slices;

            var segCache = new Dictionary<int, List<(Vector2 p, Vector2 q, int tri)>>();
            List<(Vector2 p, Vector2 q, int tri)> Section(int s)
            {
                if (segCache.TryGetValue(s, out var have))
                    return have;
                float y = SliceY(s);
                var segs = new List<(Vector2, Vector2, int)>();
                foreach (int t in bySlice[s])
                {
                    var (a, b, c) = tris[t];
                    var cut = new List<Vector2>(2);
                    foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                    {
                        float yu = pts[u].Y - y, yv = pts[v].Y - y;
                        if ((yu < 0) == (yv < 0))
                            continue;
                        float k = yu / (yu - yv);
                        var p = pts[u] + (pts[v] - pts[u]) * k;
                        cut.Add(new Vector2(p.X, p.Z));
                    }
                    if (cut.Count == 2 && face[t].X * face[t].X + face[t].Z * face[t].Z > 0.04f)
                        segs.Add((cut[0], cut[1], t));
                }
                segCache[s] = segs;
                return segs;
            }

            // the triangles hanging across between the legs, below the waist. The clothes' middle often has
            // nothing right behind it (an apron in front of the gap between the legs), so it's the
            // cross-section's whole run of surface that counts
            var over = new bool[tris.Count];
            var under = new bool[tris.Count];
            // the slice each triangle was first found hanging across in
            var overIn = new int[tris.Count];
            // how much hangs across in each slice
            var bySliceCount = new int[Slices];
            for (int s = 0; s < Slices * 7 / 10; s++)
            {
                var segs = Section(s);
                // runs: segments of triangles that share an edge
                var parent = Enumerable.Range(0, segs.Count).ToArray();
                int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
                var byEdge = new Dictionary<(int, int), int>();
                for (int k = 0; k < segs.Count; k++)
                {
                    var (a, b, c) = tris[segs[k].tri];
                    foreach (var e in new[] { Key(a, b), Key(b, c), Key(c, a) })
                    {
                        if (byEdge.TryGetValue(e, out int other))
                            parent[Find(k)] = Find(other);
                        else
                            byEdge[e] = k;
                    }
                }
                // a run hanging across the middle is clothes when other runs (legs) on both sides of the
                // middle sit under it: a closed skirt goes all round them, an apron in front
                var runOf = segs.Select((_, k) => Find(k)).ToArray();
                var crossingRuns = Enumerable.Range(0, segs.Count).Where(k => (segs[k].p.X < cx) != (segs[k].q.X < cx)).Select(k => runOf[k]).ToHashSet();
                var others = Enumerable.Range(0, segs.Count).Where(k => !crossingRuns.Contains(runOf[k])).GroupBy(k => runOf[k]).ToList();
                foreach (int run in crossingRuns)
                {
                    var mine = Enumerable.Range(0, segs.Count).Where(k => runOf[k] == run).ToList();
                    bool left = false, right = false;
                    var covered = new List<int>();
                    foreach (var other in others)
                    {
                        var ks = other.ToList();
                        var center = ks.Aggregate(Vector2.Zero, (acc, k) => acc + (segs[k].p + segs[k].q) / 2) / ks.Count;
                        if (Covered(center, mine.Select(k => (segs[k].p, segs[k].q))))
                        {
                            if (center.X < cx) left = true; else right = true;
                            covered.AddRange(ks);
                        }
                    }
                    if (left && right)
                    {
                        bySliceCount[s] += mine.Count;
                        foreach (int k in mine)
                            if (!over[segs[k].tri])
                            {
                                over[segs[k].tri] = true;
                                overIn[segs[k].tri] = s;
                            }
                        foreach (int k in covered)
                            under[segs[k].tri] = true;
                    }
                }
            }

            // the clothes are one stretch of heights: a strap across both feet or a belt buckle further off
            // is something else
            var blocks = new List<(int from, int to, int count)>();
            for (int s = 0; s < Slices; s++)
            {
                if (bySliceCount[s] == 0)
                    continue;
                if (blocks.Count > 0 && s - blocks[^1].to <= 2)
                    blocks[^1] = (blocks[^1].from, s, blocks[^1].count + bySliceCount[s]);
                else
                    blocks.Add((s, s, bySliceCount[s]));
            }
            if (blocks.Count > 1)
            {
                var keep = blocks.MaxBy(b => b.count);
                for (int t = 0; t < tris.Count; t++)
                    if (over[t] && (overIn[t] < keep.from || overIn[t] > keep.to))
                        over[t] = false;
            }

            var result = new Clothes { Over = new bool[pts.Length], Under = new bool[pts.Length] };
            float before = crotch(tris.Select(t => (t.a, t.b, t.c)));
            var acrossTris = Enumerable.Range(0, tris.Count).Where(t => over[t]).ToList();
            result.Across = acrossTris.Count;
            if (acrossTris.Count == 0)
            {
                result.LegsTop = before;
                return result;
            }
            result.LegsTop = crotch(tris.Where((_, t) => !over[t]).Select(t => (t.a, t.b, t.c)));
            // set aside, the layer must uncover legs that part well above where they did before: otherwise
            // it is a belly over a tucked-in crotch, or something inside a closed body
            if (result.LegsTop < before + 0.06f)
            {
                result.Across = 0;
                result.LegsTop = before;
                return result;
            }

            // the clothes are those runs up to the waist, where the uncovered legs end (above, what hangs
            // across is the body, and what it covers is arms or hair)
            float topY = minY + result.LegsTop * height + height * 2f / Slices;
            result.Across = 0;
            for (int t = 0; t < tris.Count; t++)
            {
                var (a, b, c) = tris[t];
                if (!over[t] || MathF.Min(pts[a].Y, MathF.Min(pts[b].Y, pts[c].Y)) > topY)
                    continue;
                result.Over[a] = result.Over[b] = result.Over[c] = true;
                result.Across++;
            }
            for (int t = 0; t < tris.Count; t++)
                if (under[t] && !over[t])
                {
                    var (a, b, c) = tris[t];
                    result.Under[a] = result.Under[b] = result.Under[c] = true;
                }
            var ys = Enumerable.Range(0, pts.Length).Where(i => result.Over[i]).Select(i => pts[i].Y).ToList();
            result.Hem = (ys.Min() - minY) / height;
            result.Top = (ys.Max() - minY) / height;
            return result;
        }

        /// <summary>Whether a run of surface covers a point from at least two of eight directions around it.</summary>
        static bool Covered(Vector2 from, IEnumerable<(Vector2 p, Vector2 q)> run)
        {
            var segs = run.ToList();
            int hits = 0;
            for (int k = 0; k < 8; k++)
            {
                var d = new Vector2(MathF.Cos(k * MathF.PI / 4), MathF.Sin(k * MathF.PI / 4));
                foreach (var (p, q) in segs)
                {
                    var e = q - p;
                    float den = d.X * e.Y - d.Y * e.X;
                    if (MathF.Abs(den) < 1e-12f)
                        continue;
                    var w = p - from;
                    float t = (w.X * e.Y - w.Y * e.X) / den;
                    float u = (w.X * d.Y - w.Y * d.X) / den;
                    if (t > 0 && u >= 0 && u <= 1)
                    {
                        hits++;
                        break;
                    }
                }
            }
            return hits >= 2;
        }

        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

        static int SliceOf(float y, float minY, float height) => Math.Clamp((int)((y - minY) / height * Slices), 0, Slices - 1);
    }
}
