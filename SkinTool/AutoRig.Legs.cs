using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    // Legs and feet: where the legs part, legs made from a body, feet on the fighter's feet.
    public partial class AutoRig
    {
        /// <summary>
        /// Places a model whose legs were made by <see cref="Legs.Split"/>: the body from the fighter's
        /// hips to the top of its head, each leg narrowed to a leg's thickness and run down the fighter's
        /// hip, knee and ankle to the ground. Returns the body's scale.
        /// </summary>
        private float MakeLegs(int[] legSide, Vector3[] pts, Vector3 min, Vector3 size, FighterMesh fighter, Vector3 fighterSize, float zMid, Skeleton sk, Options opt)
        {
            var roles = opt.Roles;
            float cutY = min.Y + opt.LegCut * size.Y;
            float hipY = (sk.Position(roles["lle"]).Y + sk.Position(roles["rle"]).Y) / 2;
            HipY = hipY;
            FloorY = fighter.Min.Y;
            float scale = (fighter.Max.Y - hipY) / ((1 - opt.LegCut) * size.Y) * opt.Scale;
            float cx = (min.X + max(pts, q => q.X)) / 2;
            static float max(Vector3[] a, Func<Vector3, float> f) => a.Max(f);
            float cz = pts.Average(q => q.Z);
            float fcx = (fighter.Min.X + fighter.Max.X) / 2;

            var origin = new Vector3(cx, cutY, cz);
            var target = new Vector3(fcx, hipY, zMid);
            Positions = pts.Select(q => (q - origin) * scale * new Vector3(opt.Width, opt.Height, 1) + target).ToArray();

            float radius = 0.1f * fighterSize.Y * opt.Scale;
            foreach (int s in new[] { 1, -1 })
            {
                string r = s > 0 ? "l" : "r";
                var mine = Enumerable.Range(0, pts.Length).Where(i => legSide[i] == s).ToList();
                if (mine.Count == 0)
                    continue;

                // the fighter's leg, top to bottom: hip, knee, ankle, and the ground under the ankle
                var chain = new List<Vector3> { sk.Position(roles[r + "le"]) };
                if (roles.TryGetValue(r + "kn", out int kn)) chain.Add(sk.Position(kn));
                if (roles.TryGetValue(r + "fb", out int fb) || roles.TryGetValue(r + "ff", out fb)) chain.Add(sk.Position(fb));
                chain.Add(new Vector3(chain[^1].X, fighter.Min.Y, chain[^1].Z));
                chain = chain.OrderByDescending(q => q.Y).ToList();
                Vector3 At(float y)
                {
                    for (int k = 1; k < chain.Count; k++)
                        if (y >= chain[k].Y || k == chain.Count - 1)
                            return Vector3.Lerp(chain[k - 1], chain[k], Math.Clamp((chain[k - 1].Y - y) / MathF.Max(chain[k - 1].Y - chain[k].Y, 1e-4f), 0, 1));
                    return chain[^1];
                }

                var middle = new Vector2(mine.Average(i => pts[i].X), mine.Average(i => pts[i].Z));
                var dx = mine.Select(i => MathF.Abs(pts[i].X - middle.X)).OrderBy(d => d).ToList();
                var dz = mine.Select(i => MathF.Abs(pts[i].Z - middle.Y)).OrderBy(d => d).ToList();
                float sx = radius / MathF.Max(dx[dx.Count * 9 / 10], 1e-4f);
                // a little deeper than wide, so the feet still point forwards
                float sz = radius * 1.4f / MathF.Max(dz[dz.Count * 9 / 10], 1e-4f);

                // the feet keep their shape at the bottom; only the part above them is stretched into a leg
                float footTop = MathF.Min(0.06f * size.Y, (cutY - min.Y) * 0.5f);
                float footHeight = footTop * scale;
                foreach (var i in mine)
                {
                    float h = pts[i].Y - min.Y;
                    float y = h < footTop
                        ? fighter.Min.Y + h * scale
                        : fighter.Min.Y + footHeight + (h - footTop) / MathF.Max(cutY - min.Y - footTop, 1e-4f) * (hipY - fighter.Min.Y - footHeight);
                    float foot = h < footTop ? 1 - h / footTop : 0;
                    var c = At(y);
                    float kx = sx + (MathF.Min(scale, sx * 1.6f) - sx) * foot, kz = sz + (MathF.Min(scale, sz * 1.6f) - sz) * foot;
                    Positions[i] = new Vector3(c.X + (pts[i].X - middle.X) * kx, y, c.Z + (pts[i].Z - middle.Y) * kz);
                }
            }
            return scale;
        }

        /// <summary>
        /// Slides each foot sideways and forwards onto the fighter's foot, fading out up the body.
        /// </summary>
        private void ConformFeet(Dictionary<string, int> roles, Skeleton sk, Vector3[] pts, float cx, Vector3 min, Vector3 size, float crotch)
        {
            foreach (var side in new[] { "l", "r" })
            {
                var feet = new[] { "fb", "ff" }.Where(r => roles.ContainsKey(side + r)).Select(r => sk.Position(roles[side + r])).ToList();
                int region = side == "l" ? LegLeft : LegRight;
                var members = Enumerable.Range(0, pts.Length).Where(i => ModelRegion[i] == region).ToList();
                if (feet.Count == 0 || members.Count == 0)
                    continue;

                var theirs = feet.Aggregate(Vector3.Zero, (a, q) => a + q) / feet.Count;
                var mine = members.Aggregate(Vector3.Zero, (a, i) => a + Positions[i]) / members.Count;
                var shift = new Vector3(theirs.X - mine.X, 0, theirs.Z - mine.Z);
                float reachX = MathF.Abs(mine.X - Positions.Average(q => q.X));

                for (int i = 0; i < pts.Length; i++)
                {
                    float t = (pts[i].Y - min.Y) / size.Y;
                    bool onSide = side == "l" ? pts[i].X >= cx : pts[i].X < cx;
                    if (!onSide || t > crotch + 0.15f)
                        continue;
                    float up = t <= crotch ? 1 : 1 - (t - crotch) / 0.15f;
                    float across = ModelRegion[i] == region ? 1 : Math.Clamp(MathF.Abs(pts[i].X - cx) * (Positions[i].X - cx == 0 ? 0 : 1) / MathF.Max(reachX, 1e-4f), 0, 1);
                    Positions[i] += shift * up * across;
                }
            }
        }

        /// <summary>
        /// How far up (as a share of the height) the shape stays split into two legs: heights where
        /// there is surface on both sides but no edge crosses the middle.
        /// </summary>
        private static float Crotch(Vector3[] pts, IEnumerable<(int, int, int)> triangles, float centerX, float minY, float height, bool edges = false)
        {
            const int slices = 50;
            int Slice(float y) => Math.Clamp((int)((y - minY) / height * slices), 0, slices - 1);

            var left = new int[slices];
            var right = new int[slices];
            var crossings = new int[slices];
            foreach (var p in pts)
            {
                if (p.X < centerX) left[Slice(p.Y)]++;
                else right[Slice(p.Y)]++;
            }
            // a low-poly leg (Moe's) has heights with no point on them, which read as the legs ending: its
            // edges count at every height they pass too (only for the model's own legs, not when looking for
            // clothes over them: Cartman's stubs under his jacket aren't legs under a coat)
            foreach (var (a, b, c) in edges ? triangles : Enumerable.Empty<(int, int, int)>())
                foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                {
                    int from = Slice(pts[u].Y), to = Slice(pts[v].Y);
                    for (int s = Math.Min(from, to) + 1; s < Math.Max(from, to); s++)
                    {
                        float y = minY + (s + 0.5f) / slices * height;
                        float k = Math.Clamp((y - pts[u].Y) / (pts[v].Y - pts[u].Y), 0, 1);
                        if (pts[u].X + (pts[v].X - pts[u].X) * k < centerX) left[s]++;
                        else right[s]++;
                    }
                }
            // a triangle that reaches across the middle joins the two sides at the heights where it crosses
            // the middle (only there: a long low-poly triangle down the inside of a pair of shorts crosses at
            // the crotch, not at the hem); so does an edge lying on the middle, the seam of a mirrored model
            // (a single vertex there is just a toe pointing inward)
            float eps = (pts.Max(p => p.X) - pts.Min(p => p.X)) * 0.002f;
            foreach (var (a, b, c) in triangles)
            {
                float xa = pts[a].X - centerX, xb = pts[b].X - centerX, xc = pts[c].X - centerX;
                float lo = MathF.Min(xa, MathF.Min(xb, xc)), hi = MathF.Max(xa, MathF.Max(xb, xc));
                int onSeam = (MathF.Abs(xa) <= eps ? 1 : 0) + (MathF.Abs(xb) <= eps ? 1 : 0) + (MathF.Abs(xc) <= eps ? 1 : 0);
                if (!(lo < -eps && hi > eps) && onSeam < 2)
                    continue;
                float y0 = float.MaxValue, y1 = float.MinValue;
                foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                {
                    float xu = pts[u].X - centerX, xv = pts[v].X - centerX;
                    if (MathF.Abs(xu) <= eps)
                    {
                        y0 = MathF.Min(y0, pts[u].Y);
                        y1 = MathF.Max(y1, pts[u].Y);
                    }
                    if ((xu < 0) != (xv < 0))
                    {
                        float y = pts[u].Y + (pts[v].Y - pts[u].Y) * (xu / (xu - xv));
                        y0 = MathF.Min(y0, y);
                        y1 = MathF.Max(y1, y);
                    }
                }
                for (int s = Slice(y0); s <= Slice(y1); s++)
                    crossings[s]++;
            }

            int top = 0, misses = 0;
            bool started = false;
            for (int s = 0; s < slices; s++)
            {
                bool split = left[s] > 0 && right[s] > 0 && crossings[s] == 0;

                if (split)
                {
                    started = true;
                    top = s + 1;
                    misses = 0;
                }
                // toes that turn inward can touch at the very bottom, so the split may start a little up,
                // and knee pads can touch for a slice or two
                else if ((started && ++misses > 2) || (!started && s >= slices / 10))
                    break;
            }

            // no legs at all (a dress or a blob): treat just the bottom as feet
            return Math.Clamp(top / (float)slices, 0.05f, 0.6f);
        }

        /// <summary>
        /// Legs pressed together are one surface where they touch. Below the knees, where a stride parts
        /// them furthest, the triangles reaching from one leg to the other go: they're hidden between the
        /// legs while they touch, and once parted the legs are whole without them.
        /// </summary>
        private int UnjoinLegs(ObjModel model)
        {
            var legs = Enumerable.Range(0, Positions.Length).Where(i => ModelRegion[i] == LegLeft || ModelRegion[i] == LegRight).ToList();
            if (legs.Count == 0)
                return 0;
            float floor = legs.Min(i => Positions[i].Y), top = legs.Max(i => Positions[i].Y);
            float knee = floor + (top - floor) * 0.5f;
            bool Low(int i) => Positions[i].Y < knee;
            return model.Triangles.RemoveAll(t =>
            {
                var c = new[] { t.A.Position, t.B.Position, t.C.Position };
                return c.All(Low) && c.Any(i => ModelRegion[i] == LegLeft) && c.Any(i => ModelRegion[i] == LegRight);
            });
        }

        /// <summary>
        /// For a model whose legs never part: how far up its slim lower body runs before it widens into the
        /// hips, as a share of its height, a quarter below where it widens; 0 when the lower body isn't slim.
        /// </summary>
        private static float LegsTogether(Vector3[] pts, float centerX, float minY, float height)
        {
            const int slices = 50;
            var reach = new float[slices];
            var any = new bool[slices];
            foreach (var p in pts)
            {
                int s = Math.Clamp((int)((p.Y - minY) / height * slices), 0, slices - 1);
                reach[s] = MathF.Max(reach[s], MathF.Abs(p.X - centerX));
                any[s] = true;
            }
            // both legs together are about a quarter of the height across (a slice may hold nothing at all)
            int top = 0;
            for (int s = 0; s < slices; s++)
            {
                if (!any[s])
                    continue;
                if (reach[s] > height * 0.12f)
                    break;
                top = s + 1;
            }
            float slim = top / (float)slices;
            return slim >= 0.3f ? MathF.Min(slim * 0.75f, 0.6f) : 0;
        }
    }
}
