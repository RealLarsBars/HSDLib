using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    // The model's parts (head, body, arms, legs, tail) as the rig decided them, in one place: the part map
    // draws them, and fixes by hand ("this is an arm") override what was found.
    public partial class AutoRig
    {
        public enum PartKind { Body, Head, Arm, Leg, Tail, Clothes }

        /// <summary>
        /// "This is an arm": a model point (or a spot clicked on the part map, resolved to a point first) and
        /// the part it and the points around it belong to. Reach is how far around, along the surface, as a
        /// share of the model's height; it only spreads over points the rig had put in the same part as the
        /// one clicked, so an arm marked by its hand doesn't take the body beside it along.
        /// </summary>
        public class PartFix
        {
            public int Point = -1;
            // a spot on the part map (front or side view, in its pixels) when the point isn't known yet
            public string View;
            public float X, Y;
            public PartKind Kind;
            public float Reach = 0.25f;

            public override string ToString() =>
                FormattableString.Invariant($"{(Point >= 0 ? Point.ToString() : $"{View}:{X:0},{Y:0}")}={Kind.ToString().ToLowerInvariant()}{(Reach != 0.25f ? $"@{Reach:0.##}" : "")}");

            /// <summary>Reads "4512=arm", "front:212,140=head" or "4512=tail@0.4".</summary>
            public static PartFix Parse(string s)
            {
                var fix = new PartFix();
                var (spot, rest) = s.Split('=', 2) is { Length: 2 } kv ? (kv[0], kv[1]) : throw new Exception($"--part takes point=part or front:x,y=part, not '{s}'");
                var at = rest.Split('@');
                if (!Enum.TryParse(at[0], true, out fix.Kind) || fix.Kind == PartKind.Clothes)
                    throw new Exception($"--part: '{at[0]}' isn't a part (body, head, arm, leg or tail)");
                if (at.Length > 1)
                    fix.Reach = Math.Clamp(float.Parse(at[1], System.Globalization.CultureInfo.InvariantCulture), 0.01f, 1);
                if (spot.Contains(':'))
                {
                    var vs = spot.Split(':');
                    var xy = vs[1].Split(',');
                    fix.View = vs[0].ToLowerInvariant();
                    if (fix.View != "front" && fix.View != "side")
                        throw new Exception($"--part: the view is front or side, not '{vs[0]}'");
                    fix.X = float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture);
                    fix.Y = float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                    fix.Point = int.Parse(spot);
                return fix;
            }
        }

        // per model point: the part a fix by hand put it in (null: none), and for arms and legs, which side
        private PartKind?[] Fixed;
        private int[] FixedRegion;

        /// <summary>
        /// Whether a point is below the neck line by more than <paramref name="margin"/> (a share of the model's
        /// height): what follows the head and what doesn't, asked the same way everywhere. Fixes by hand win.
        /// </summary>
        private bool BelowNeck(int i, float margin, float height)
        {
            if (Fixed?[i] is PartKind k)
                return k != PartKind.Head;
            return Positions[i].Y < NeckYAt(Positions[i]) - margin * height;
        }

        // some points were put in the head by hand
        private bool HeadFixed => Fixed != null && Fixed.Any(k => k == PartKind.Head);

        /// <summary>Each point's part, as the rig weighted it (for the part map).</summary>
        public PartKind[] Labels()
        {
            int n = Positions.Length;
            var labels = new PartKind[n];
            float h = Positions.Max(q => q.Y) - Positions.Min(q => q.Y);
            for (int i = 0; i < n; i++)
            {
                int r = ModelRegion[i];
                labels[i] = Fixed?[i] is PartKind k ? k
                    : Tail != null && Tail[i] ? PartKind.Tail
                    : r == ArmLeft || r == ArmRight ? PartKind.Arm
                    : ClothesOver != null && ClothesOver[i] ? PartKind.Clothes
                    : r == LegLeft || r == LegRight ? PartKind.Leg
                    : HeadBone >= 0 && !float.IsNaN(NeckY) && !BelowNeck(i, 0, h) ? PartKind.Head
                    : PartKind.Body;
            }
            return labels;
        }

        /// <summary>
        /// Puts the fixed points in their parts: the first time (the fit, before the arms are moved) it works out
        /// which points each fix covers; after that it only puts back what later steps changed.
        /// </summary>
        private void ApplyFixes(Options opt)
        {
            if (opt.PartFixes == null || opt.PartFixes.Count == 0)
                return;
            int n = Positions.Length;
            if (Fixed == null)
            {
                Fixed = new PartKind?[n];
                FixedRegion = new int[n];
                float h = Positions.Max(q => q.Y) - Positions.Min(q => q.Y);
                float cx = (Positions.Min(q => q.X) + Positions.Max(q => q.X)) / 2;
                var adj = SurfaceNeighbours();
                // the parts as found, before any fix
                var found = new int[n];
                for (int i = 0; i < n; i++)
                    found[i] = ModelRegion[i] == Body && !float.IsNaN(NeckY) && Positions[i].Y >= NeckYAt(Positions[i]) ? -1 : ModelRegion[i];
                foreach (var fix in opt.PartFixes)
                {
                    if (fix.Point < 0 || fix.Point >= n)
                    {
                        Report += $"parts: fix {fix} is off the model; left out\n";
                        continue;
                    }
                    // along the surface from the point, as far as the reach, through points found in its part
                    var dist = new Dictionary<int, float> { [fix.Point] = 0 };
                    var queue = new PriorityQueue<int, float>();
                    queue.Enqueue(fix.Point, 0);
                    float limit = fix.Reach * h;
                    while (queue.TryDequeue(out int i, out float d))
                    {
                        if (d > dist[i]) continue;
                        foreach (int j in adj[i])
                        {
                            if (found[j] != found[fix.Point]) continue;
                            float dj = d + Vector3.Distance(Positions[i], Positions[j]);
                            if (dj > limit || dist.TryGetValue(j, out float old) && old <= dj) continue;
                            dist[j] = dj;
                            queue.Enqueue(j, dj);
                        }
                    }
                    foreach (int i in dist.Keys)
                    {
                        bool plus = Positions[i].X >= cx;
                        Fixed[i] = fix.Kind;
                        FixedRegion[i] = fix.Kind switch
                        {
                            PartKind.Arm => plus ? ArmLeft : ArmRight,
                            PartKind.Leg => plus ? LegLeft : LegRight,
                            _ => Body,
                        };
                    }
                    Report += $"parts: {fix} put {dist.Count} points in the {fix.Kind.ToString().ToLowerInvariant()}\n";
                }
            }
            for (int i = 0; i < n; i++)
                if (Fixed[i] != null && FighterRegion.Contains(FixedRegion[i]))
                    ModelRegion[i] = FixedRegion[i];
        }

        /// <summary>Each point's neighbours along the model's triangles, with points at the same spot (a UV seam) joined.</summary>
        private HashSet<int>[] SurfaceNeighbours()
        {
            int n = Positions.Length;
            var adj = Enumerable.Range(0, n).Select(_ => new HashSet<int>()).ToArray();
            foreach (var t in Corners)
            {
                int a = t.A.Position, b = t.B.Position, c = t.C.Position;
                adj[a].Add(b); adj[a].Add(c);
                adj[b].Add(a); adj[b].Add(c);
                adj[c].Add(a); adj[c].Add(b);
            }
            var byPos = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < n; i++)
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
            return adj;
        }
    }
}
