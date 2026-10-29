using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Where the head starts: a cut across the neck that can lean forward or back with the head. Many
    /// models hold their head tipped forward (Remy's snout down, Spyro's neck reaching up and out), so a
    /// level line takes the chin with the chest or the back of the chest with the head. The model is cut
    /// across at each height, for each lean, and the cross-section around the head's middle measured
    /// (the outline it makes, ears and hair hanging beside it left out): the neck is a narrow spot with a
    /// wider head above and body below, so a short neck that only narrows a little still shows up. The cut
    /// then turns about the narrowest spot to cross the neck straight (one straight line, leaning all the
    /// way across), and a straight neck stays level.
    /// </summary>
    public class Neck
    {
        // where the cut crosses the middle of the neck, in the model's own space
        public Vector3 Center;
        // the cut's lean in degrees: positive dips it towards the front (+Z), for a head tipped forward
        public float Tilt;
        // its height at the middle of the neck, as a share of the model's height
        public float Share;
        // how much wider the head and the body are than the neck (cross-section areas, 1 = no narrowing)
        public float Score;
        // how much smaller the cut's cross-section is than a level one's at the same spot
        public float Gain = 1;

        const int Slices = 64;

        /// <summary>
        /// The best neck over leans from -35 to 35 degrees, or null when there's none (a round body with a
        /// face on the front). <paramref name="crotch"/> is where the legs part, as a share of the height:
        /// the neck is well above it.
        /// </summary>
        public static Neck Find(Vector3[] pts, IList<(int a, int b, int c)> tris, Vector3 min, Vector3 size, float crotch)
        {
            var weld = Weld(pts, size);
            // each lean on its own core
            var scans = new Scanned[15];
            System.Threading.Tasks.Parallel.For(0, scans.Length, i => scans[i] = Scan(pts, tris, weld, min, size, crotch, -35 + 5 * i));
            var found = scans.Where(k => k.Score >= 1.5f).ToList();
            if (found.Count == 0)
                return null;
            // the clearest neck says where the neck is; the cut then turns about its narrowest spot to cross
            // it straight (the smallest cross-section there), level unless a lean is clearly smaller
            var clearest = found.MaxBy(k => k.Score * (1 - 0.004f * Math.Abs(k.Tilt)));
            var pivot = clearest.Point(clearest.Narrowest);
            float radius = MathF.Sqrt(clearest.Area[clearest.Narrowest] / MathF.PI);
            // which way the head leans from there: the middle of the outlines above, weighted by their size
            var head = Enumerable.Range(clearest.Narrowest + 1, clearest.Top - clearest.Narrowest);
            var headMiddle = head.Aggregate(Vector3.Zero, (a, s) => a + clearest.Point(s) * clearest.Area[s]) / MathF.Max(head.Sum(s => clearest.Area[s]), 1e-12f);
            var lean = headMiddle - pivot;
            float leanTilt = MathF.Atan2(lean.Z, lean.Y) * 180 / MathF.PI;
            var across = scans.ToDictionary(k => k, k => k.AreaAt(pivot, radius));
            // (only cuts roughly square to that: one along the neck can slip between it and a wing or a collar)
            var best = scans.Where(k => across[k] > 0 && Math.Abs(k.Tilt - leanTilt) <= 20 || k == clearest)
                .MinBy(k => across[k] * (1 + 0.006f * Math.Abs(k.Tilt)));
            int from = best.SliceOf(pivot);
            // the head starts where the cross-section widens again above the narrowest spot
            float narrow = across[best];
            int cut = from;
            while (cut < best.Top && best.Area[cut + 1] <= narrow * 1.56f)
                cut++;
            // (straight up the cut's own direction from the neck's middle: the outline followed higher up can
            // be a hand or a sword held out at that height)
            var center = pivot + (cut - from) * best.Step * best.N;
            return new Neck
            {
                Center = center,
                Tilt = best.Tilt,
                Share = (center.Y - min.Y) / size.Y,
                // across the head's width (the chin of a head tipped forward hangs well in front of the neck)
                Score = clearest.Score,
                Gain = scans.First(k => k.Tilt == 0) is var level && across[level] > 0 ? across[level] / narrow : 1,
            };
        }

        /// <summary>
        /// A cut set by hand: at <paramref name="share"/> of the height through the middle of the model's
        /// upper body there, leaning <paramref name="tilt"/> degrees.
        /// </summary>
        public static Neck At(Vector3[] pts, Vector3 min, Vector3 size, float share, float tilt)
        {
            float y = min.Y + share * size.Y;
            float cx = min.X + size.X / 2;
            // the middle of the neck: the middle of the model's points around that height, near the middle
            var at = new Vector3(cx, y, min.Z + size.Z / 2);
            var near = pts.Where(p => MathF.Abs(p.Y - y) < size.Y * 0.03f && MathF.Abs(p.X - cx) < size.X * 0.15f).ToList();
            if (near.Count > 0)
                at.Z = near.Average(p => p.Z);
            return new Neck { Center = at, Tilt = tilt, Share = share };
        }

        // one lean: the model's cross-sections along it, and the profile of the one around the head's
        // middle, top down
        class Scanned
        {
            public int Tilt;
            public List<Contour>[] Sections;
            public Vector3 N, E;
            public float DLo, Step;
            public int Top;
            public float[] Area;
            public Vector2[] Mid;
            // the narrowest spot with a head above and a body below, and how much wider those are (0: none)
            public int Narrowest = -1;
            public float Score;

            public Vector3 Point(int s) => Mid[s].X * Vector3.UnitX + (DLo + (s + 0.5f) * Step) * N + Mid[s].Y * E;
            public int SliceOf(Vector3 p) => Math.Clamp((int)((Vector3.Dot(p, N) - DLo) / Step), 0, Slices - 1);
            public Vector3[] Pts;
            public IList<(int a, int b, int c)> Tris;
            public int[] Weld;
            // the cross-section through a point, along this lean
            public float AreaAt(Vector3 p, float radius) =>
                Around(Cut(Pts, Tris, Weld, N, E, Vector3.Dot(p, N)), new Vector2(p.X, Vector3.Dot(p, E)), radius)?.Area ?? 0;
        }

        static Scanned Scan(Vector3[] pts, IList<(int a, int b, int c)> tris, int[] weld, Vector3 min, Vector3 size, float crotch, int tilt)
        {
            var k = new Scanned { Tilt = tilt, Pts = pts, Tris = tris, Weld = weld };
            var sec = k.Sections = Sections(pts, tris, weld, min, size, tilt, out k.N, out k.E, out k.DLo, out k.Step);
            int top = Slices - 1;
            while (top > 0 && sec[top].Count == 0) top--;
            k.Top = top;
            var area = k.Area = new float[Slices];
            var mid = k.Mid = new Vector2[Slices];

            // start on the biggest outline in the top third (the head, not an ear or a hat's tip), then follow
            // the outline around that middle down the model
            float cx = min.X + size.X / 2;
            var start = sec.Skip(top - Slices / 3).SelectMany(c => c).Where(c => MathF.Abs(c.Middle.X - cx) < size.X * 0.25f)
                .DefaultIfEmpty().MaxBy(c => c?.Area ?? 0);
            if (start == null)
                return k;
            var axis = start.Middle;
            float radius = MathF.Sqrt(start.Area / MathF.PI);
            for (int s = top; s >= 0; s--)
            {
                // (only outlines around the model's middle: not a hand or a sword held out)
                var pick = Around(sec[s].Where(c => MathF.Abs(c.Middle.X - cx) < size.X * 0.25f).ToList(), axis, radius);
                area[s] = pick?.Area ?? 0;
                if (area[s] > 0)
                {
                    axis = pick.Middle;
                    radius = MathF.Sqrt(area[s] / MathF.PI);
                }
                mid[s] = axis;
            }

            // the narrowest spot with a head above it and a body below it, both clearly wider; the head well
            // clear of the top (not the band of a hat), the neck in the upper half, well clear of the legs
            var points = new int[Slices];
            foreach (var p in pts)
                points[Math.Clamp((int)((Vector3.Dot(p, k.N) - k.DLo) / k.Step), 0, Slices - 1)]++;
            int lowest = Math.Max((int)(Slices * (crotch + 0.1f)), Slices / 2);
            int below = Slices / 5;
            for (int s = top - Slices / 8; s >= lowest; s--)
            {
                if (area[s] <= 0) continue;
                float head = area.Skip(s + 1).Take(top - s).Max();
                float body = area.Skip(Math.Max(0, s - below)).Take(Math.Min(below, s)).DefaultIfEmpty(0).Max();
                // a head, not a pair of horns or a hat's tip over a narrow gap: as wide as a good part of the
                // body, and with a good part of the model's detail (a face, eyes and a mouth are fine work)
                if (head < body * 0.2f || points.Skip(s + 1).Sum() < pts.Length * 0.1f) continue;
                // (a neck far thinner than both counts as a tenth of them: past that, it's clear either way)
                float score = MathF.Min(head, body) / MathF.Max(area[s], MathF.Min(head, body) * 0.1f);
                if (score > k.Score)
                {
                    k.Score = score;
                    k.Narrowest = s;
                }
            }
            return k;
        }

        class Contour
        {
            public Vector2 Middle;
            public float Area;
            public List<Vector2> Hull;
        }

        // the cross-sections at or near a point (within a quarter of the last one's radius: a neck's outline
        // can come in a few pieces), or the one nearest it, as one outline
        static Contour Around(List<Contour> sec, Vector2 p, float radius)
        {
            if (sec.Count == 0)
                return null;
            var dist = sec.Select(c => Distance(c.Hull, p)).ToList();
            float near = dist.Min();
            var pick = sec.Where((c, i) => dist[i] <= near + radius * 0.25f).ToList();
            return pick.Count == 1 ? pick[0] : MakeContour(pick.SelectMany(c => c.Hull).ToList());
        }

        static float Distance(List<Vector2> hull, Vector2 p)
        {
            if (Inside(hull, p))
                return 0;
            float best = float.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                var a = hull[i];
                var b = hull[(i + 1) % hull.Count];
                var ab = b - a;
                float k = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
                best = MathF.Min(best, Vector2.Distance(p, a + ab * k));
            }
            return best;
        }

        // the model cut across along the lean: per slice, each closed outline (as a convex hull) in the
        // plane's own coordinates (x, and along the plane front to back)
        static List<Contour>[] Sections(Vector3[] pts, IList<(int a, int b, int c)> tris, int[] weld, Vector3 min, Vector3 size, float tilt,
            out Vector3 n, out Vector3 e, out float dLo, out float step)
        {
            Axes(tilt, out n, out e);
            var nn = n;
            var d = pts.Select(p => Vector3.Dot(p, nn)).ToArray();
            dLo = float.MaxValue;
            float dHi = float.MinValue;
            foreach (var (a, b, c) in tris)
                foreach (int i in new[] { a, b, c })
                {
                    dLo = MathF.Min(dLo, d[i]);
                    dHi = MathF.Max(dHi, d[i]);
                }
            step = MathF.Max(dHi - dLo, 1e-6f) / Slices;

            var segs = new List<(long, long, Vector2, Vector2)>[Slices];
            for (int s = 0; s < Slices; s++)
                segs[s] = new List<(long, long, Vector2, Vector2)>();
            foreach (var (a, b, c) in tris)
            {
                float lo = MathF.Min(d[a], MathF.Min(d[b], d[c])), hi = MathF.Max(d[a], MathF.Max(d[b], d[c]));
                int s0 = Math.Max(0, (int)MathF.Ceiling((lo - dLo) / step - 0.5f));
                int s1 = Math.Min(Slices - 1, (int)MathF.Floor((hi - dLo) / step - 0.5f));
                for (int s = s0; s <= s1; s++)
                {
                    float plane = dLo + (s + 0.5f) * step;
                    var hits = new List<(long, Vector2)>(2);
                    foreach (var (i, j) in new[] { (a, b), (b, c), (c, a) })
                    {
                        if ((d[i] < plane) == (d[j] < plane)) continue;
                        float k = (plane - d[i]) / (d[j] - d[i]);
                        var p = Vector3.Lerp(pts[i], pts[j], k);
                        int wi = weld[i], wj = weld[j];
                        hits.Add(((long)Math.Min(wi, wj) << 32 | (uint)Math.Max(wi, wj), new Vector2(p.X, Vector3.Dot(p, e))));
                    }
                    if (hits.Count == 2)
                        segs[s].Add((hits[0].Item1, hits[1].Item1, hits[0].Item2, hits[1].Item2));
                }
            }

            return segs.Select(Outlines).ToArray();
        }

        // a lean's cut direction (square to the cut) and its front-to-back direction along the cut
        static void Axes(float tilt, out Vector3 n, out Vector3 e)
        {
            float r = tilt * MathF.PI / 180;
            n = new Vector3(0, MathF.Cos(r), MathF.Sin(r));
            e = new Vector3(0, -MathF.Sin(r), MathF.Cos(r));
        }

        // the cross-section through one plane, square to n at d
        static List<Contour> Cut(Vector3[] pts, IList<(int a, int b, int c)> tris, int[] weld, Vector3 n, Vector3 e, float plane)
        {
            var segs = new List<(long, long, Vector2, Vector2)>();
            var hits = new List<(long, Vector2)>(3);
            foreach (var (a, b, c) in tris)
            {
                hits.Clear();
                foreach (var (i, j) in new[] { (a, b), (b, c), (c, a) })
                {
                    float di = Vector3.Dot(pts[i], n), dj = Vector3.Dot(pts[j], n);
                    if ((di < plane) == (dj < plane)) continue;
                    var p = Vector3.Lerp(pts[i], pts[j], (plane - di) / (dj - di));
                    hits.Add(((long)Math.Min(weld[i], weld[j]) << 32 | (uint)Math.Max(weld[i], weld[j]), new Vector2(p.X, Vector3.Dot(p, e))));
                }
                if (hits.Count == 2)
                    segs.Add((hits[0].Item1, hits[1].Item1, hits[0].Item2, hits[1].Item2));
            }
            return Outlines(segs);
        }

        // outlines: segments joined where they share a cut edge
        static List<Contour> Outlines(List<(long, long, Vector2, Vector2)> segs)
        {
            var parent = new Dictionary<long, long>();
            long Find(long x)
            {
                if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; }
                while (p != x) { var g = parent[p]; parent[x] = g; x = p; p = g; }
                return x;
            }
            foreach (var (ka, kb, _, _) in segs)
            {
                long ra = Find(ka), rb = Find(kb);
                if (ra != rb) parent[ra] = rb;
            }
            return segs.GroupBy(g => Find(g.Item1))
                .Select(g => MakeContour(g.SelectMany(x => new[] { x.Item3, x.Item4 }).ToList()))
                .Where(c => c.Area > 0)
                .ToList();
        }

        static Contour MakeContour(List<Vector2> points)
        {
            var hull = Hull(points);
            float area = 0;
            var c = Vector2.Zero;
            for (int i = 0; i < hull.Count; i++)
            {
                var p = hull[i];
                var q = hull[(i + 1) % hull.Count];
                float x = p.X * q.Y - q.X * p.Y;
                area += x;
                c += (p + q) * x;
            }
            area /= 2;
            return new Contour { Hull = hull, Area = MathF.Abs(area), Middle = MathF.Abs(area) > 1e-12f ? c / (6 * area) : points.Aggregate(Vector2.Zero, (a, b) => a + b) / points.Count };
        }

        // convex hull, counter-clockwise (monotone chain)
        static List<Vector2> Hull(List<Vector2> points)
        {
            var p = points.Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ToList();
            if (p.Count < 3)
                return p;
            static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var h = new Vector2[2 * p.Count];
            int k = 0;
            for (int i = 0; i < p.Count; i++)
            {
                while (k >= 2 && Cross(h[k - 2], h[k - 1], p[i]) <= 0) k--;
                h[k++] = p[i];
            }
            for (int i = p.Count - 2, t = k + 1; i >= 0; i--)
            {
                while (k >= t && Cross(h[k - 2], h[k - 1], p[i]) <= 0) k--;
                h[k++] = p[i];
            }
            return h.Take(k - 1).ToList();
        }

        static bool Inside(List<Vector2> hull, Vector2 p)
        {
            if (hull.Count < 3) return false;
            for (int i = 0; i < hull.Count; i++)
            {
                var a = hull[i];
                var b = hull[(i + 1) % hull.Count];
                if ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X) < 0) return false;
            }
            return true;
        }

        // points at the same place (a UV seam splits a vertex) count as one, so outlines join across seams
        static int[] Weld(Vector3[] pts, Vector3 size)
        {
            float q = MathF.Max(size.Y, 1e-6f) * 1e-4f;
            var at = new Dictionary<(int, int, int), int>();
            var weld = new int[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                var key = ((int)MathF.Round(pts[i].X / q), (int)MathF.Round(pts[i].Y / q), (int)MathF.Round(pts[i].Z / q));
                if (!at.TryGetValue(key, out weld[i]))
                    weld[i] = at[key] = i;
            }
            return weld;
        }
    }
}
