using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Fits a model onto the fighter's bind pose and copies the fighter's skin weights onto it.
    /// </summary>
    public class AutoRig
    {
        public class Options
        {
            // multiplies the automatic size
            public float Scale = 1;

            // degrees to turn the model around the vertical axis before fitting
            public float RotateY = 0;

            // fighter vertices blended per model vertex
            public int Neighbours = 8;

            // passes of weight smoothing over the model's surface
            public int SmoothPasses = 12;

            // influences kept per vertex, and the smallest one kept
            public int MaxInfluences = 3;
            public float MinWeight = 0.08f;

            // parts with fewer vertices than this share of the model move rigidly with one bone
            public float RigidPartShare = 0.15f;
        }

        public Vector3[] Positions;

        // positions used only to find matching fighter vertices
        public Vector3[] Lookup;
        public Vector3[] Normals;
        public SkinBuilder.Influence[][] Weights;

        // per model position, the part it belongs to and whether that part is rigid
        public int[] Part;
        public bool[] RigidPart;

        public string Report = "";

        // detail bone -> the bone that takes its weight
        private int[] Remap;

        public static AutoRig Run(ObjModel model, FighterMesh fighter, Skeleton skeleton, Options opt)
        {
            var rig = new AutoRig();
            rig.Remap = DetailBoneRemap(fighter, skeleton, out var merged);
            rig.Report += $"detail bones folded into their parents: {string.Join(",", merged)}\n";
            rig.Fit(model, fighter, opt);
            rig.Transfer(model, fighter, opt);
            return rig;
        }

        private void Fit(ObjModel model, FighterMesh fighter, Options opt)
        {
            var rot = Matrix4x4.CreateRotationY(opt.RotateY * MathF.PI / 180);
            var pts = model.Positions.Select(p => Vector3.Transform(p, rot)).ToArray();

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in pts)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var size = Vector3.Max(max - min, new Vector3(1e-6f));

            // the fighter's body depth, ignoring tails and capes that stick out behind
            var zs = fighter.Vertices.Select(v => v.Position.Z).OrderBy(z => z).ToList();
            float zLow = zs[zs.Count / 10], zHigh = zs[zs.Count * 9 / 10], zMid = zs[zs.Count / 2];
            var fighterSize = new Vector3(fighter.Max.X - fighter.Min.X, fighter.Max.Y - fighter.Min.Y, zHigh - zLow);

            // Size: as tall as the fighter would make a round model huge, and as wide as its arm span
            // would make it tiny, so meet in the middle. For a humanoid in a T-pose the two agree.
            float byHeight = fighterSize.Y / size.Y;
            float byWidth = fighterSize.X / size.X;
            float scale = MathF.Sqrt(byHeight * byWidth) * opt.Scale;

            var target = new Vector3((fighter.Min.X + fighter.Max.X) / 2, fighter.Min.Y, zMid);
            var origin = new Vector3((min.X + max.X) / 2, min.Y, (min.Z + max.Z) / 2);

            Positions = pts.Select(p => (p - origin) * scale + target).ToArray();
            Normals = model.Normals.Select(n => Vector3.TransformNormal(n, rot)).ToArray();

            // Weights are looked up with the model stretched over the fighter's whole body, so its
            // outermost hands land on the fighter's hands, its feet on the feet and its head on the
            // head, whatever the two shapes' proportions are.
            // The legs get their own stretch: the model's crotch lines up with the fighter's, so a
            // model with stubby feet under a round body doesn't drag its belly along with the thighs.
            var stretch = fighterSize / size;
            var modelTris = model.Triangles.Select(t => (t.A.Position, t.B.Position, t.C.Position));
            var fighterTris = Enumerable.Range(0, fighter.Triangles.Count / 3).Select(t => (3 * t, 3 * t + 1, 3 * t + 2));
            float modelCrotch = Crotch(pts, modelTris, (min.X + max.X) / 2, min.Y, size.Y);
            float fighterCrotch = Crotch(fighter.Vertices.Select(v => v.Position).ToArray(), fighterTris, (fighter.Min.X + fighter.Max.X) / 2, fighter.Min.Y, fighterSize.Y);

            // the leg bones' influence reaches past where the legs visibly part (thighs run up to the hips),
            // so line the model's crotch up with the top of the area the leg bones drive
            float splitY = fighter.Min.Y + fighterCrotch * fighterSize.Y;
            var legBones = fighter.Vertices.Where(v => v.Position.Y < splitY).Select(Dominant).ToHashSet();
            var legTop = fighter.Vertices.Where(v => legBones.Contains(Dominant(v))).Select(v => v.Position.Y).OrderBy(y => y).ToList();
            if (legTop.Count > 0)
                fighterCrotch = Math.Clamp((legTop[legTop.Count * 95 / 100] - fighter.Min.Y) / fighterSize.Y, fighterCrotch, 0.7f);
            // A model with stubby legs (a blob on feet) has nothing to put along the fighter's thighs and
            // shins: its feet follow the fighter's feet and its body starts at the hips.
            float lowerTop = fighterCrotch;
            if (modelCrotch < fighterCrotch * 0.5f)
            {
                float lowY = fighter.Min.Y + fighterSize.Y * 0.08f;
                var footBones = fighter.Vertices.Where(v => v.Position.Y < lowY).Select(v => Remap[Dominant(v)]).ToHashSet();
                var footTop = fighter.Vertices.Where(v => footBones.Contains(Remap[Dominant(v)])).Select(v => v.Position.Y).OrderBy(y => y).ToList();
                if (footTop.Count > 0)
                    lowerTop = Math.Min(fighterCrotch, (footTop[footTop.Count * 9 / 10] - fighter.Min.Y) / fighterSize.Y);
                Report += $"short legs: the model's feet follow the fighter's feet (bottom {lowerTop:P0}) and its body the hips\n";
            }

            Lookup = pts.Select(p =>
            {
                var l = (p - origin) * stretch + target;
                float t = (p.Y - min.Y) / size.Y;
                float y = t < modelCrotch
                    ? t / modelCrotch * lowerTop
                    : fighterCrotch + (t - modelCrotch) / (1 - modelCrotch) * (1 - fighterCrotch);
                l.Y = fighter.Min.Y + y * fighterSize.Y;
                return l;
            }).ToArray();

            Report += $"legs: the model's split at {modelCrotch:P0} of its height lines up with the top of the fighter's legs at {fighterCrotch:P0}\n";

            Report += $"fitted size x{scale:F2}: {size.Y * scale:F1} tall next to the fighter's {fighterSize.Y:F1}, {size.X * scale:F1} wide next to its {fighterSize.X:F1}\n";
        }

        /// <summary>
        /// How far up (as a share of the height) the shape stays split into two legs: heights where
        /// there is surface on both sides but no edge crosses the middle.
        /// </summary>
        private static float Crotch(Vector3[] pts, IEnumerable<(int, int, int)> triangles, float centerX, float minY, float height)
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
            // a triangle that reaches across the middle joins the two sides over its whole height;
            // so does one with an edge lying on the middle, the seam of a mirrored model
            // (a single vertex there is just a toe pointing inward)
            float eps = (pts.Max(p => p.X) - pts.Min(p => p.X)) * 0.002f;
            foreach (var (a, b, c) in triangles)
            {
                float xa = pts[a].X - centerX, xb = pts[b].X - centerX, xc = pts[c].X - centerX;
                float lo = MathF.Min(xa, MathF.Min(xb, xc)), hi = MathF.Max(xa, MathF.Max(xb, xc));
                int onSeam = (MathF.Abs(xa) <= eps ? 1 : 0) + (MathF.Abs(xb) <= eps ? 1 : 0) + (MathF.Abs(xc) <= eps ? 1 : 0);
                if (!(lo < -eps && hi > eps) && onSeam < 2)
                    continue;
                int s0 = Slice(MathF.Min(pts[a].Y, MathF.Min(pts[b].Y, pts[c].Y)));
                int s1 = Slice(MathF.Max(pts[a].Y, MathF.Max(pts[b].Y, pts[c].Y)));
                for (int s = s0; s <= s1; s++)
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

        private void Transfer(ObjModel model, FighterMesh fighter, Options opt)
        {
            int n = Positions.Length;
            int boneCount = fighter.Vertices.Max(v => v.Bones.Max()) + 1;

            // neighbours on the model's surface
            var adj = new HashSet<int>[n];
            for (int i = 0; i < n; i++)
                adj[i] = new HashSet<int>();
            foreach (var t in model.Triangles)
            {
                int a = t.A.Position, b = t.B.Position, c = t.C.Position;
                adj[a].Add(b); adj[a].Add(c);
                adj[b].Add(a); adj[b].Add(c);
                adj[c].Add(a); adj[c].Add(b);
            }

            // positions that are welded in the source but split by UV seams still sit on top of each other
            WeldCoincident(adj);

            FindParts(adj, opt);

            // 1. blend the weights of the nearest fighter vertices
            var w = new float[n][];
            var fv = fighter.Vertices;
            for (int i = 0; i < n; i++)
            {
                var p = Lookup[i];
                var nearest = new List<(float d, int idx)>(opt.Neighbours + 1);
                for (int k = 0; k < fv.Count; k++)
                {
                    float d = Vector3.DistanceSquared(p, fv[k].Position);
                    if (nearest.Count < opt.Neighbours || d < nearest[^1].d)
                    {
                        int at = nearest.FindIndex(e => e.d > d);
                        if (at < 0) nearest.Add((d, k)); else nearest.Insert(at, (d, k));
                        if (nearest.Count > opt.Neighbours)
                            nearest.RemoveAt(nearest.Count - 1);
                    }
                }

                var acc = new float[boneCount];
                foreach (var (d, k) in nearest)
                {
                    float iw = 1f / (d + 1e-4f);
                    for (int b = 0; b < fv[k].Bones.Length; b++)
                        acc[Remap[fv[k].Bones[b]]] += fv[k].Weights[b] * iw;
                }
                Normalize(acc);
                w[i] = acc;
            }

            // 2. smooth the body across its surface so joints bend instead of tearing
            for (int pass = 0; pass < opt.SmoothPasses; pass++)
            {
                var next = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    if (RigidPart[i] || adj[i].Count == 0)
                    {
                        next[i] = w[i];
                        continue;
                    }

                    var avg = new float[boneCount];
                    foreach (var j in adj[i])
                        for (int b = 0; b < boneCount; b++)
                            avg[b] += w[j][b];
                    float inv = 1f / adj[i].Count;
                    var r = new float[boneCount];
                    for (int b = 0; b < boneCount; b++)
                        r[b] = 0.5f * w[i][b] + 0.5f * avg[b] * inv;
                    next[i] = r;
                }
                w = next;
            }

            // 3. small separate parts (claws, teeth, whiskers, eyes) move rigidly with one bone,
            // the one the body around them mostly follows, so they stay attached to it
            var body = Enumerable.Range(0, n).Where(i => !RigidPart[i]).ToList();
            foreach (var part in Enumerable.Range(0, Part.Max() + 1))
            {
                var members = Enumerable.Range(0, n).Where(i => Part[i] == part).ToList();
                if (members.Count == 0 || !RigidPart[members[0]])
                    continue;

                var sum = new float[boneCount];
                foreach (var i in members)
                {
                    var near = body.Count > 0
                        ? body.OrderBy(j => Vector3.DistanceSquared(Positions[i], Positions[j])).Take(4)
                        : new[] { i }.AsEnumerable();
                    foreach (var j in near)
                        for (int b = 0; b < boneCount; b++)
                            sum[b] += w[j][b];
                }

                int best = Array.IndexOf(sum, sum.Max());
                var c = members.Aggregate(Vector3.Zero, (a, i) => a + Positions[i]) / members.Count;
                Report += $"part {part}: {members.Count} vertices around ({c.X:F1}, {c.Y:F1}, {c.Z:F1}) locked to bone {best}\n";
                foreach (var i in members)
                {
                    w[i] = new float[boneCount];
                    w[i][best] = 1;
                }
            }

            // 4. keep the strongest few influences, as the game expects
            Weights = new SkinBuilder.Influence[n][];
            for (int i = 0; i < n; i++)
            {
                var top = w[i]
                    .Select((v, b) => new SkinBuilder.Influence { Bone = b, Weight = v })
                    .OrderByDescending(e => e.Weight)
                    .Take(opt.MaxInfluences)
                    .Where(e => e.Weight >= opt.MinWeight)
                    .ToList();

                if (top.Count == 0)
                    top.Add(new SkinBuilder.Influence { Bone = Array.IndexOf(w[i], w[i].Max()), Weight = 1 });

                float s = top.Sum(e => e.Weight);
                foreach (var e in top)
                    e.Weight /= s;

                // weights are stored at limited precision; make sure they still add up to exactly 1
                for (int k = 0; k < top.Count; k++)
                    top[k].Weight = MathF.Round(top[k].Weight, 2);
                top[0].Weight += 1 - top.Sum(e => e.Weight);
                top.RemoveAll(e => e.Weight <= 0);

                Weights[i] = top.ToArray();
            }

            int rigid = RigidPart.Count(r => r);
            Report += $"{Part.Max() + 1} parts, {rigid} of {n} vertices locked to one bone\n";
            Report += $"bones used: {string.Join(",", Weights.SelectMany(x => x.Select(e => e.Bone)).Distinct().OrderBy(b => b))}\n";
        }

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

        private void FindParts(HashSet<int>[] adj, Options opt)
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
            for (int i = 0; i < n; i++)
                RigidPart[i] = sizes[Part[i]] < n * opt.RigidPartShare;
        }

        private static int Dominant(FighterMesh.Vertex v) => v.Bones[Array.IndexOf(v.Weights, v.Weights.Max())];

        /// <summary>
        /// Bones a different body shouldn't follow: small ones (eyes, fingers) and everything hanging
        /// off the head (ears, jaw, hair), which would drag a face around. Each hands its weight to
        /// the nearest ancestor that isn't one of these.
        /// </summary>
        private static int[] DetailBoneRemap(FighterMesh fighter, Skeleton sk, out List<int> merged)
        {
            int n = sk.Joints.Count;
            float height = fighter.Max.Y - fighter.Min.Y;
            var detail = new bool[n];

            foreach (var kv in fighter.BoneExtent)
                if (kv.Value < height * 0.12f)
                    detail[kv.Key] = true;

            // the head: the bone driving the biggest area among those up in the top third
            int head = fighter.BoneExtent.Keys
                .Where(b => sk.Position(b).Y > fighter.Min.Y + height * 0.6f)
                .OrderByDescending(b => fighter.BoneExtent[b])
                .DefaultIfEmpty(-1).First();
            if (head >= 0)
                for (int b = 0; b < n; b++)
                    for (int a = sk.Parent[b]; a >= 0; a = sk.Parent[a])
                        if (a == head) { detail[b] = true; break; }

            var remap = new int[n];
            merged = new List<int>();
            for (int b = 0; b < n; b++)
            {
                int t = b;
                while (t >= 0 && (detail[t] || !fighter.BoneExtent.ContainsKey(t)) && sk.Parent[t] >= 0 && t != head)
                    t = sk.Parent[t];
                remap[b] = t < 0 ? b : t;
                if (remap[b] != b && fighter.BoneExtent.ContainsKey(b))
                    merged.Add(b);
            }
            return remap;
        }

        private static void Normalize(float[] a)
        {
            float s = a.Sum();
            if (s <= 0)
                return;
            for (int i = 0; i < a.Length; i++)
                a[i] /= s;
        }
    }
}
