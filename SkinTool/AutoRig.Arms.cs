using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    // Arms: finding the angle they hang at, turning them, and moving them onto the fighter's arms.
    public partial class AutoRig
    {
        /// <summary>
        /// Turns the model's arms at the shoulder, before they're weighted. Arms modelled hanging down (Mr.
        /// Incredible's A-pose) on a fighter whose arms are held out (Falcon's T-pose) sat far below the fighter's
        /// arms, so they only half followed them and stretched whenever the fighter lifted its arms. They're lifted
        /// to the fighter's angle instead, and then moved onto its arms like any arm held out. <see cref="Options.ArmAngle"/>
        /// sets the angle by hand. Each arm turns about the top of the arm, where it leaves the shoulder, easing in
        /// over its first stretch so the shoulder bends rather than tears; its hand turns with it. <paramref name="k"/>
        /// takes the model's units to the fitted ones (sizes and stretches), so the angles are the fitted model's.
        /// </summary>
        private void TurnArms(Vector3[] pts, Skeleton skeleton, Options opt, Vector3 k)
        {
            var grow = k / k.X;
            var arms = new List<(int region, List<int> members, Vector3 pivot, float angle, float tmin, float tmax, Vector3 dir)>();
            foreach (int region in new[] { ArmLeft, ArmRight })
            {
                var members = Enumerable.Range(0, pts.Length).Where(i => ModelRegion[i] == region).ToList();
                if (members.Count < 8)
                    continue;
                float sign = region == ArmLeft ? 1 : -1;
                var g = members.Select(i => pts[i] * grow).ToList();
                var mean = g.Aggregate(Vector3.Zero, (a, q) => a + q) / g.Count;
                // the arm's main line, the way its points spread the most (power iteration on their spread)
                float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
                foreach (var q in g)
                {
                    var d = q - mean;
                    xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
                }
                var dir = new Vector3(sign, -1, 0);
                for (int it = 0; it < 32; it++)
                {
                    var n = new Vector3(xx * dir.X + xy * dir.Y + xz * dir.Z, xy * dir.X + yy * dir.Y + yz * dir.Z, xz * dir.X + yz * dir.Y + zz * dir.Z);
                    if (n.LengthSquared() < 1e-12f)
                        break;
                    dir = Vector3.Normalize(n);
                }
                if (dir.X * sign < 0)
                    dir = -dir;
                var t = g.Select(q => Vector3.Dot(q - mean, dir)).ToList();
                float tmin = t.Min(), tmax = t.Max();
                if (tmax - tmin < 1e-5f)
                    continue;
                // where the arm leaves the shoulder: the middle of its innermost stretch
                var inner = Enumerable.Range(0, g.Count).Where(j => t[j] < tmin + 0.15f * (tmax - tmin)).ToList();
                var pivot = inner.Aggregate(Vector3.Zero, (a, j) => a + g[j]) / inner.Count;
                float angle = MathF.Atan2(-dir.Y, MathF.Abs(dir.X)) * 180 / MathF.PI;
                arms.Add((region, members, pivot, angle, tmin - Vector3.Dot(pivot - mean, dir), tmax - Vector3.Dot(pivot - mean, dir), dir));
            }
            if (arms.Count == 0)
                return;

            // the fighter's arms, shoulder to hand, in its bind pose
            var theirs = new List<float>();
            foreach (var side in new[] { "l", "r" })
                if (opt.Roles != null && opt.Roles.TryGetValue(side + "sh", out int sh) && opt.Roles.TryGetValue(side + "ha", out int ha))
                {
                    var d = skeleton.Position(ha) - skeleton.Position(sh);
                    if (MathF.Abs(d.X) + MathF.Abs(d.Y) > 1e-4f)
                        theirs.Add(MathF.Atan2(-d.Y, MathF.Abs(d.X)) * 180 / MathF.PI);
                }
            float mine = arms.Average(a => a.angle);
            float fighterAngle = theirs.Count > 0 ? theirs.Average() : float.NaN;
            static int Deg(float a) => (int)MathF.Round(a) + 0;
            string found = $"arms: the model's point {Deg(mine)} degrees down" + (theirs.Count > 0 ? $", the fighter's {Deg(fighterAngle)}" : "");
            // only arms clearly hanging below the fighter's are lifted: a T-pose a little off stays as it is, and
            // a fighter whose own arms hang (Ganondorf) has them bent down along its arms later (ConformArm).
            // And only a pair that looks like arms: one each side, hanging alike, running across the front rather
            // than back (Rex's big tail, sticking out sideways behind him, was taken for arms and swung up)
            bool pair = arms.Count == 2 && MathF.Abs(arms[0].angle - arms[1].angle) < 15 && arms.All(a => MathF.Abs(a.dir.Z) < 0.5f);
            float want = !float.IsNaN(opt.ArmAngle) ? opt.ArmAngle
                : pair && opt.Arms != "body" && opt.Conform && !float.IsNaN(fighterAngle) && mine >= 25 && mine <= 75 && mine - fighterAngle >= 20 ? fighterAngle : float.NaN;
            if (float.IsNaN(want) || MathF.Abs(want - mine) < 1)
            {
                Report += found + "\n";
                return;
            }
            Report += found + $"; turned to {Deg(want)} at the shoulder ({(float.IsNaN(opt.ArmAngle) ? "lifted to the fighter's" : "set by hand")})\n";

            // normals are kept per corner: each turns with its point
            var turns = new Dictionary<int, Matrix4x4>();
            foreach (var a in arms)
            {
                float sign = a.region == ArmLeft ? 1 : -1;
                // lifting a +x arm turns it about +z, a -x arm the other way
                float turn = (a.angle - want) * MathF.PI / 180 * sign;
                foreach (int i in a.members)
                {
                    var q = pts[i] * grow;
                    float u = Math.Clamp(Vector3.Dot(q - a.pivot, a.dir) / MathF.Max(a.tmax, 1e-5f), 0, 1);
                    var r = Matrix4x4.CreateRotationZ(turn * SmoothStep(0, 0.3f, u));
                    var moved = (a.pivot + Vector3.Transform(q - a.pivot, r)) / grow;
                    Positions[i] += (moved - pts[i]) * k;
                    pts[i] = moved;
                    turns[i] = r;
                }
            }
            var done = new HashSet<int>();
            foreach (var t in Corners)
                foreach (var c in new[] { t.A, t.B, t.C })
                    if (c.Normal >= 0 && turns.TryGetValue(c.Position, out var r) && done.Add(c.Normal))
                        Normals[c.Normal] = Vector3.TransformNormal(Normals[c.Normal], r);
        }

        /// <summary>
        /// Whether the model's body above its crotch is slim for its height: the typical half-width of its
        /// slices (arms stick out of only a few) under a fifth of its height.
        /// </summary>
        private static bool Slim(Vector3[] pts, float cx, float minY, float height, float crotch)
        {
            const int slices = 50;
            var reach = new float[slices];
            foreach (var q in pts)
            {
                float t = (q.Y - minY) / height;
                if (t < crotch)
                    continue;
                int s = Math.Clamp((int)(t * slices), 0, slices - 1);
                reach[s] = MathF.Max(reach[s], MathF.Abs(q.X - cx));
            }
            var used = reach.Where(r => r > 0).OrderBy(r => r).ToList();
            return used.Count > 0 && used[used.Count / 2] < height * 0.2f;
        }

        /// <summary>
        /// Moves an arm onto the fighter's arm: each point keeps its distance from the arm's middle line,
        /// and its place along the arm becomes the same share of the way along the fighter's
        /// shoulder-elbow-hand-fingertip line. Near the shoulder it blends back into the body.
        /// </summary>
        private void ConformArm(int region, List<Vector3> chain, Vector3[] pts, float cx, Func<Vector3, int, float> bodyReach,
            Func<float, int> slice, Vector3 min, Vector3 size, Vector3 origin, float scale, Vector3 target)
        {
            var members = Enumerable.Range(0, pts.Length).Where(i => ModelRegion[i] == region).ToList();
            // An arm hanging apart from the body, held well down from the shoulder (Kronk's A-pose), runs along its
            // own slant, not sideways: its hand hangs and its fingers curl back in, so how far out sideways a point
            // is says little about how far along the arm it is (the fingertips counted as the top of the arm and
            // went to the shoulder, stretching into spikes). Its place along the arm is measured along that slant
            // instead, from the top of the shoulder to the hand
            float Out(int i) => MathF.Abs(pts[i].X - cx) - bodyReach(pts[i], slice((pts[i].Y - min.Y) / size.Y));
            var top = members.OrderByDescending(i => pts[i].Y).Take(Math.Max(1, members.Count / 10)).ToList();
            var armRoot = top.Aggregate(Vector3.Zero, (a, i) => a + pts[i]) / top.Count;
            var slant = pts[members.MaxBy(Out)] - armRoot;
            bool slanted = HangingArms && slant.LengthSquared() > 1e-8f && -slant.Y > 0.5f * slant.Length();
            float U(int i)
            {
                if (slanted)
                    return Math.Clamp(Vector3.Dot(pts[i] - armRoot, slant) / slant.LengthSquared(), 0, 1);
                float root = bodyReach(pts[i], slice((pts[i].Y - min.Y) / size.Y));
                return Math.Clamp((MathF.Abs(pts[i].X - cx) - root) / MathF.Max(tipOf(), 1e-4f), 0, 1);
            }
            float tipCache = -1;
            float tipOf()
            {
                if (tipCache < 0)
                    tipCache = slanted ? slant.Length() : members.Max(i => MathF.Abs(pts[i].X - cx) - bodyReach(pts[i], slice((pts[i].Y - min.Y) / size.Y)));
                return tipCache;
            }

            // the model arm's middle line, in a few steps along it
            const int bins = 8;
            var sum = new Vector3[bins];
            var count = new int[bins];
            foreach (var i in members)
            {
                int b = Math.Min(bins - 1, (int)(U(i) * bins));
                sum[b] += Positions[i];
                count[b]++;
            }
            var middle = new Vector3[bins];
            for (int b = 0; b < bins; b++)
                middle[b] = count[b] > 0 ? sum[b] / count[b] : Vector3.Zero;
            for (int b = 1; b < bins; b++)
                if (count[b] == 0) middle[b] = middle[b - 1];
            for (int b = bins - 2; b >= 0; b--)
                if (count[b] == 0) middle[b] = middle[b + 1];

            var lengths = new List<float> { 0 };
            for (int k = 1; k < chain.Count; k++)
                lengths.Add(lengths[^1] + Vector3.Distance(chain[k - 1], chain[k]));
            Vector3 Along(float u)
            {
                float d = u * lengths[^1];
                for (int k = 1; k < chain.Count; k++)
                    if (d <= lengths[k] || k == chain.Count - 1)
                        return Vector3.Lerp(chain[k - 1], chain[k], Math.Clamp((d - lengths[k - 1]) / MathF.Max(lengths[k] - lengths[k - 1], 1e-4f), 0, 1));
                return chain[^1];
            }

            // which way the fighter's arm runs at a share of the way along it
            Vector3 Direction(float u)
            {
                float d = u * lengths[^1];
                int k = 1;
                while (k < chain.Count - 1 && d > lengths[k])
                    k++;
                var dir = chain[k] - chain[k - 1];
                return dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : Vector3.UnitX * MathF.Sign(chain[^1].X - chain[0].X);
            }

            // an arm hanging down from the fighter's shoulder (Ganondorf's) starts where the model's arm
            // leaves its body instead: a wide body's arm would otherwise run in over its shoulder to the
            // fighter's and drop from there, a flat ledge sticking out of the body. Its weights still come
            // from the fighter's own arm, where it hangs.
            bool hangs = Direction(0).Y < -0.5f;
            var shift = hangs ? middle[0] - chain[0] : Vector3.Zero;
            // and keeps its own length: a short arm (Gusteau's) stretched to the fighter's fingertips reaches
            // far past where its own hand would be, its weights still spread along the whole fighter's arm
            float own = tipOf() * scale;
            float keep = hangs ? Math.Clamp(own / MathF.Max(lengths[^1], 1e-4f), 0.5f, 1) : 1;
            if (hangs)
                Report += $"{RegionNames[region]}: {own:F1} long next to the fighter's {lengths[^1]:F1}, keeps {keep:P0} of its length\n";

            foreach (var i in members)
            {
                float u = U(i);
                float fb = u * bins - 0.5f;
                int b0 = Math.Clamp((int)MathF.Floor(fb), 0, bins - 1), b1 = Math.Min(bins - 1, b0 + 1);
                var mid = Vector3.Lerp(middle[b0], middle[b1], Math.Clamp(fb - b0, 0, 1));
                var offset = Positions[i] - mid;
                // the model's arm runs sideways (or down its slant); where the fighter's points down (Ganondorf's
                // hang at his sides) the thickness turns with it, or the arm would be squashed flat along its own length
                var across = slanted ? Vector3.Normalize(slant) : new Vector3(MathF.Sign(Positions[i].X - cx), 0, 0);
                // the arm's length is the fighter's now; keep only its thickness
                offset -= Vector3.Dot(offset, across) * across;
                var to = Direction(u);
                var axis = Vector3.Cross(across, to);
                if (hangs && axis.LengthSquared() > 1e-8f)
                    offset = Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(Math.Clamp(Vector3.Dot(across, to), -1, 1))));
                var moved = chain[0] + (Along(u) - chain[0]) * keep + shift + offset;
                if (hangs)
                {
                    Hung.Add(region);
                    ArmLookup[i] = Along(u) + offset;
                    ArmU[i] = u;
                }
                float blend = SmoothStep(0, 0.35f, u);
                Positions[i] = Vector3.Lerp(Positions[i], moved, blend);
            }
        }
    }
}
