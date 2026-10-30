using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    // Sorting the fighter's own points into regions, and which of its bones the skin may follow.
    public partial class AutoRig
    {
        /// <summary>
        /// Sorts the fighter's bones into body (the chain from the root up to the head), arms, legs and
        /// other limbs (tails, capes), by where each branch off the body reaches.
        /// </summary>
        private void FighterRegions(FighterMesh fighter, Skeleton sk, int head, Dictionary<string, int> roles)
        {
            int n = sk.Joints.Count;
            if (roles != null)
            {
                RolesToRegions(fighter, sk, roles);
                return;
            }

            var body = new bool[n];
            for (int b = Math.Max(head, 0); b >= 0; b = sk.Parent[b])
                body[b] = true;

            var branch = new int[n];
            for (int b = 0; b < n; b++)
            {
                int t = b;
                while (!body[t] && !body[sk.Parent[t]])
                    t = sk.Parent[t];
                branch[b] = body[t] ? -1 : t;
            }

            float height = fighter.Max.Y - fighter.Min.Y, width = fighter.Max.X - fighter.Min.X;
            float cx = (fighter.Min.X + fighter.Max.X) / 2;
            var byBranch = fighter.Vertices.Where(v => branch[RegionRemap[Dominant(v)]] >= 0).GroupBy(v => branch[RegionRemap[Dominant(v)]]);
            var branchRegion = new Dictionary<int, int>();
            foreach (var g in byBranch)
            {
                bool plus = g.Average(v => v.Position.X) >= cx;
                if (g.Min(v => v.Position.Y) < fighter.Min.Y + height * 0.25f)
                    branchRegion[g.Key] = plus ? LegLeft : LegRight;
                else if (g.Max(v => MathF.Abs(v.Position.X - cx)) > width * 0.25f)
                    branchRegion[g.Key] = plus ? ArmLeft : ArmRight;
                else
                    branchRegion[g.Key] = Other;
            }

            BoneRegion = new int[n];
            for (int b = 0; b < n; b++)
                BoneRegion[b] = branch[b] < 0 ? Body : branchRegion.GetValueOrDefault(branch[b], Other);
            FighterRegion = fighter.Vertices.Select(v => BoneRegion[RegionRemap[Dominant(v)]]).ToArray();

            Report += "fighter regions: " + string.Join(", ", Enumerable.Range(0, RegionNames.Length)
                .Select(r => (r, bones: Enumerable.Range(0, n).Where(b => BoneRegion[b] == r && fighter.BoneExtent.ContainsKey(b)).ToList()))
                .Where(e => e.bones.Count > 0).Select(e => $"{RegionNames[e.r]} {string.Join(",", e.bones)}")) + "\n";
        }

        private void RolesToRegions(FighterMesh fighter, Skeleton sk, Dictionary<string, int> roles)
        {
            static int Region(string role)
            {
                string part = role.Length == 3 && (role[0] == 'l' || role[0] == 'r') ? role.Substring(1) : null;
                if (part is "sh" or "el" or "ha")
                    return role[0] == 'l' ? ArmLeft : ArmRight;
                if (part is "le" or "kn" or "fb" or "ff")
                    return role[0] == 'l' ? LegLeft : LegRight;
                return role is "face" or "body" or "butt" or "chest" or "hips" ? Body : Other;
            }

            var byBone = new Dictionary<int, int>();
            foreach (var (role, bone) in roles)
                byBone[bone] = Region(role);

            int n = sk.Joints.Count;
            BoneRegion = new int[n];
            for (int b = 0; b < n; b++)
                BoneRegion[b] = byBone.GetValueOrDefault(RegionRemap[b], Other);
            FighterRegion = fighter.Vertices.Select(v => BoneRegion[RegionRemap[Dominant(v)]]).ToArray();

            Report += "fighter regions from the bone map: " + string.Join(", ", Enumerable.Range(0, RegionNames.Length)
                .Select(r => (r, bones: roles.Values.Distinct().Where(b => byBone[b] == r).OrderBy(b => b).ToList()))
                .Where(e => e.bones.Count > 0).Select(e => $"{RegionNames[e.r]} {string.Join(",", e.bones)}")) + "\n";
        }

        private static int Dominant(FighterMesh.Vertex v) => v.Bones[Array.IndexOf(v.Weights, v.Weights.Max())];

        /// <summary>
        /// Bones a different body shouldn't follow: small ones (eyes, fingers) and everything hanging
        /// off the head (ears, jaw, hair), which would drag a face around. Each hands its weight to
        /// the nearest ancestor that isn't one of these.
        /// </summary>
        private static int[] DetailBoneRemap(FighterMesh fighter, Skeleton sk, out List<int> merged, out int head)
        {
            int n = sk.Joints.Count;
            float height = fighter.Max.Y - fighter.Min.Y;
            var detail = new bool[n];

            foreach (var kv in fighter.BoneExtent)
                if (kv.Value < height * 0.12f)
                    detail[kv.Key] = true;

            // the head: the bone driving the biggest area among those up in the top third
            head = fighter.BoneExtent.Keys
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

        /// <summary>
        /// Moves every bone's weight onto the bones a bone map lists. A bone that sits on top of a
        /// listed child (a wrist helper on the hand, a neck on the head) goes to that child; anything
        /// else goes the way its parent goes, and bones with no listed parent (hips, tail) join the body.
        /// A helper hung off the collarbone right at a listed shoulder (Falcon's 37, 43 and 60, which
        /// shrug on their own in his moves) goes to that shoulder, so the top of the arm moves with the arm.
        /// </summary>
        private static int[] ChosenBoneRemap(FighterMesh fighter, Skeleton sk, int[] bones, int head, int[] shoulders = null)
        {
            int n = sk.Joints.Count;
            float near = (fighter.Max.Y - fighter.Min.Y) * 0.05f;
            var allowed = bones.Where(b => b >= 0 && b < n).ToHashSet();

            // the body: the listed bone on the way from the head down to the root
            int body = -1;
            for (int b = head; b >= 0 && body < 0; b = sk.Parent[b])
                if (allowed.Contains(b) && b != head) body = b;
            if (body < 0)
                body = allowed.OrderBy(b => sk.Position(b).Y).FirstOrDefault(b => b != head);

            var remap = new int[n];
            for (int b = 0; b < n; b++)
            {
                if (allowed.Contains(b))
                {
                    remap[b] = b;
                    continue;
                }

                int under = Enumerable.Range(0, n)
                    .Where(c => allowed.Contains(c) && IsAncestor(sk, b, c) && Vector3.Distance(sk.Position(c), sk.Position(b)) < near)
                    .OrderBy(c => Vector3.Distance(sk.Position(c), sk.Position(b)))
                    .DefaultIfEmpty(-1).First();
                if (under < 0 && shoulders != null && sk.Parent[b] >= 0)
                    under = shoulders
                        .Where(c => c != b && IsAncestor(sk, sk.Parent[b], c) && Vector3.Distance(sk.Position(c), sk.Position(b)) < near)
                        .OrderBy(c => Vector3.Distance(sk.Position(c), sk.Position(b)))
                        .DefaultIfEmpty(-1).First();
                // parents come before children in the joint list, so the parent's answer is already known
                remap[b] = under >= 0 ? under : sk.Parent[b] >= 0 ? remap[sk.Parent[b]] : body;
                if (remap[b] < 0 || !allowed.Contains(remap[b]))
                    remap[b] = body;
            }
            return remap;
        }

        private static bool IsAncestor(Skeleton sk, int ancestor, int b)
        {
            for (int a = sk.Parent[b]; a >= 0; a = sk.Parent[a])
                if (a == ancestor) return true;
            return false;
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
