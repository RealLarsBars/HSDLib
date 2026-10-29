using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    // The tail: following the fighter's tail bones, and bending onto its tail chain.
    public partial class AutoRig
    {
        /// <summary>
        /// Finds the model's tail the way arms are found, but backwards: at the heights where the model reaches
        /// much further behind its middle than it usually does, what sticks out is tail. Its weights run
        /// along the fighter's tail bones by how far out each point is, blending in from the body at the root.
        /// </summary>
        private void TailWeights(float[][] w, int boneCount, int[] chain, bool bend)
        {
            int n = Positions.Length;
            float minY = Positions.Min(p => p.Y), maxY = Positions.Max(p => p.Y);
            float h = MathF.Max(maxY - minY, 1e-4f);
            float cx = (Positions.Min(p => p.X) + Positions.Max(p => p.X)) / 2;
            float halfW = (Positions.Max(p => p.X) - Positions.Min(p => p.X)) / 2;
            var zs = Positions.Select(p => p.Z).OrderBy(z => z).ToArray();
            float cz = zs[zs.Length / 2];

            const int slices = 24;
            int Slice(float y) => Math.Clamp((int)((y - minY) / h * slices), 0, slices - 1);
            // how far behind the middle the model reaches at each height, near the middle (not the arms)
            var reach = new float[slices];
            for (int i = 0; i < n; i++)
                if (MathF.Abs(Positions[i].X - cx) < halfW * 0.5f)
                {
                    int s = Slice(Positions[i].Y);
                    reach[s] = MathF.Max(reach[s], cz - Positions[i].Z);
                }
            var filled = reach.Where(r => r > 0).OrderBy(r => r).ToArray();
            if (filled.Length == 0)
                return;
            float typical = filled[filled.Length / 2];
            float root = typical * 1.1f;
            // a tail doesn't grow out of the head: big ears laid back (Remy's) reach out behind too, and on the
            // tail bones they swing and stretch with every flick of the fighter's tail
            var tail = Enumerable.Range(0, n).Where(i => reach[Slice(Positions[i].Y)] > typical * 1.35f
                && MathF.Abs(Positions[i].X - cx) < halfW * 0.5f && cz - Positions[i].Z > root && BelowNeck(i, 0.04f, h)).ToList();
            // points put in the tail by hand go on it, and points put anywhere else come off it
            if (Fixed != null)
                tail = tail.Where(i => Fixed[i] == null).Concat(Enumerable.Range(0, n).Where(i => Fixed[i] == PartKind.Tail)).ToList();
            if (tail.Count < n * 0.01f)
            {
                Report += "tail: none found behind the model; it follows the body\n";
                return;
            }

            float tip = tail.Max(i => cz - Positions[i].Z);
            var along = tail.ToDictionary(i => i, i => Math.Clamp((cz - Positions[i].Z - root) / MathF.Max(tip - root, 1e-4f), 0, 1));
            if (bend)
                ConformTail(tail, along, chain, tip - root);
            foreach (var i in tail)
            {
                float u = along[i];
                float f = u * (chain.Length - 1);
                int a = (int)MathF.Floor(f), b = Math.Min(a + 1, chain.Length - 1);
                var own = new float[boneCount];
                own[chain[a]] += 1 - (f - a);
                own[chain[b]] += f - a;
                float k = SmoothStep(0, 0.15f, u);
                for (int x = 0; x < boneCount; x++)
                    w[i][x] = (1 - k) * w[i][x] + k * own[x];
                Tail[i] = true;
            }
            Report += $"tail: {tail.Count} vertices behind the model follow the tail bones {string.Join(",", chain)}\n";
        }

        /// <summary>
        /// A tail laid out straight behind (Remy's, along the floor) on a fighter whose tail runs another way
        /// (Pikachu's goes up behind its back) is bent to run along the fighter's tail bones, from where it leaves
        /// the body, keeping its own length and thickness. Left where it is, its tip is far from the bones it
        /// follows: every flick swings it wide and down through the floor, and it never looks like the
        /// fighter's tail. Tails that already run about the fighter's way (Fox's hangs down behind) are left alone,
        /// and so are those of models stood up with --rotate (Spyro on all fours: his tail went stiff and flat).
        /// </summary>
        private void ConformTail(List<int> tail, Dictionary<int, float> along, int[] chain, float length)
        {
            if (Skel == null || chain.Length < 2 || length <= 0)
                return;
            var joints = chain.Select(j => Skel.Position(j)).ToList();
            var fighterDir = Vector3.Normalize(joints[^1] - joints[0]);
            // the model tail's middle line, in a few steps along it
            const int bins = 8;
            var sum = new Vector3[bins];
            var count = new int[bins];
            foreach (var i in tail)
            {
                int b = Math.Min(bins - 1, (int)(along[i] * bins));
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
            var modelDir = middle[bins - 1] - middle[0];
            if (modelDir.LengthSquared() < 1e-8f)
                return;
            modelDir = Vector3.Normalize(modelDir);
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(modelDir, fighterDir), -1, 1));
            if (angle < MathF.PI / 4)
                return;

            // the fighter's tail line from the model tail's root, and on past its last joint for a longer tail
            var line = new List<Vector3> { middle[0] };
            line.AddRange(joints.Skip(1).Select(j => j - joints[0] + middle[0]));
            var lengths = new List<float> { 0 };
            for (int k = 1; k < line.Count; k++)
                lengths.Add(lengths[^1] + Vector3.Distance(line[k - 1], line[k]));
            if (lengths[^1] < length)
            {
                line.Add(line[^1] + Vector3.Normalize(line[^1] - line[^2]) * (length - lengths[^1]));
                lengths.Add(length);
            }
            (Vector3 at, Vector3 dir) Along(float d)
            {
                int k = 1;
                while (k < line.Count - 1 && d > lengths[k])
                    k++;
                var seg = line[k] - line[k - 1];
                float t = Math.Clamp((d - lengths[k - 1]) / MathF.Max(lengths[k] - lengths[k - 1], 1e-4f), 0, 1);
                return (Vector3.Lerp(line[k - 1], line[k], t), Vector3.Normalize(seg));
            }

            var turns = new Dictionary<int, Quaternion>();
            foreach (var i in tail)
            {
                float u = along[i];
                float fb = u * bins - 0.5f;
                int b0 = Math.Clamp((int)MathF.Floor(fb), 0, bins - 1), b1 = Math.Min(bins - 1, b0 + 1);
                var mid = Vector3.Lerp(middle[b0], middle[b1], Math.Clamp(fb - b0, 0, 1));
                var offset = Positions[i] - mid;
                offset -= modelDir * Vector3.Dot(offset, modelDir);
                var (at, dir) = Along(u * length);
                var axis = Vector3.Cross(modelDir, dir);
                var turn = axis.LengthSquared() > 1e-8f
                    ? Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(Math.Clamp(Vector3.Dot(modelDir, dir), -1, 1)))
                    : Quaternion.Identity;
                // the root stays on the body, bending over into the fighter's line
                float k2 = SmoothStep(0, 0.2f, u);
                Positions[i] = Vector3.Lerp(Positions[i], at + Vector3.Transform(offset, turn), k2);
                turns[i] = Quaternion.Slerp(Quaternion.Identity, turn, k2);
            }
            // the normals turn with the tail
            var normalTurn = new Dictionary<int, Quaternion>();
            foreach (var t in Corners ?? new List<ObjModel.Triangle>())
                foreach (var c in new[] { t.A, t.B, t.C })
                    if (c.Normal >= 0 && turns.TryGetValue(c.Position, out var q))
                        normalTurn[c.Normal] = q;
            foreach (var (nrm, q) in normalTurn)
                if (nrm < Normals.Length)
                    Normals[nrm] = Vector3.Transform(Normals[nrm], q);
            Report += $"tail: bent {angle * 180 / MathF.PI:F0} degrees to run along the fighter's tail bones\n";
        }
    }
}
