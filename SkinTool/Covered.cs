using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// The body under a piece of clothing (Tom Nook's fur under his shirt) is drawn too, and wherever the two
    /// bend a little differently in a move it pokes through. The parts of the body a piece of clothing covers
    /// are left out of the skin: a triangle is covered when, from each of its corners and its middle, the
    /// clothing lies close in front of it (along the way it faces). The fit and the weights still use the whole
    /// model, so only what's drawn changes. Clothing is told by name, as for <see cref="Outfits"/> (tops, pants,
    /// hat, ...); --hide-under says which kinds hide what's under them.
    /// </summary>
    public static class Covered
    {
        /// <summary>The kinds of clothing the model has pieces of (tops, bottoms), in the file's order.</summary>
        public static List<string> Kinds(ObjModel model) =>
            model.Triangles.Where(t => t.Object != null && !t.Outline).Select(t => t.Object).Distinct()
                .Select(Outfits.KindOf).Where(k => k != null).Distinct().ToList();

        /// <summary>
        /// Marks the triangles that clothing of the given kinds (null: every kind) covers as hidden. Returns a
        /// line for the build report per kind that hid something.
        /// </summary>
        public static List<string> Mark(ObjModel model, ICollection<string> kinds = null)
        {
            var said = new List<string>();
            var clothing = model.Triangles.Where(t => t.Object != null && !t.Outline && Outfits.KindOf(t.Object) is string k && (kinds == null || kinds.Contains(k))).ToList();
            if (clothing.Count == 0)
                return said;
            var clothes = clothing.ToHashSet();
            var rest = model.Triangles.Where(t => !t.Outline && !clothes.Contains(t)).ToList();
            if (rest.Count == 0)
                return said;

            float low = model.Positions.Min(p => p.Y), high = model.Positions.Max(p => p.Y);
            // how far in front the clothing may be: clothes sit close over the body
            float reach = (high - low) * 0.06f;

            // the clothing's triangles in a grid of cells the size of the reach, to look up near a point
            var cell = reach;
            (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
            var tris = clothing.Select(t => (a: model.Positions[t.A.Position], b: model.Positions[t.B.Position], c: model.Positions[t.C.Position], kind: Outfits.KindOf(t.Object))).ToList();
            var grid = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < tris.Count; i++)
            {
                var lo = Cell(Vector3.Min(tris[i].a, Vector3.Min(tris[i].b, tris[i].c)));
                var hi = Cell(Vector3.Max(tris[i].a, Vector3.Max(tris[i].b, tris[i].c)));
                for (int x = lo.Item1; x <= hi.Item1; x++)
                    for (int y = lo.Item2; y <= hi.Item2; y++)
                        for (int z = lo.Item3; z <= hi.Item3; z++)
                        {
                            if (!grid.TryGetValue((x, y, z), out var l))
                                grid[(x, y, z)] = l = new List<int>();
                            l.Add(i);
                        }
            }

            // the clothing triangle hit first going from p along dir, within the reach (-1: none)
            int Hit(Vector3 p, Vector3 dir)
            {
                var seen = new HashSet<int>();
                int best = -1;
                float bestT = reach;
                for (float s = 0; s <= reach + cell; s += cell * 0.5f)
                {
                    if (!grid.TryGetValue(Cell(p + dir * s), out var l))
                        continue;
                    foreach (var i in l)
                        if (seen.Add(i) && Ray(p, dir, tris[i].a, tris[i].b, tris[i].c) is float t && t < bestT)
                        {
                            bestT = t;
                            best = i;
                        }
                }
                return best;
            }

            // outwards: each corner's normal, turned to point away from the middle on the whole (some models'
            // normals point in)
            var middle = model.Positions.Aggregate(Vector3.Zero, (a, p) => a + p) / model.Positions.Count;
            float facing = rest.Sum(t => Vector3.Dot(Normal(model, t.A), model.Positions[t.A.Position] - middle));
            float sign = facing < 0 ? -1 : 1;

            var hidden = new Dictionary<string, int>();
            foreach (var t in rest)
            {
                string by = null;
                bool covered = true;
                var corners = new[] { t.A, t.B, t.C };
                var points = corners.Select(c => model.Positions[c.Position]).ToArray();
                var normals = corners.Select(c => Normal(model, c) * sign).ToArray();
                var centre = (points[0] + points[1] + points[2]) / 3;
                var centreNormal = normals[0] + normals[1] + normals[2];
                foreach (var (p, n) in points.Zip(normals).Append((centre, centreNormal)))
                {
                    if (n.LengthSquared() < 1e-12f) { covered = false; break; }
                    int hit = Hit(p, Vector3.Normalize(n));
                    if (hit < 0) { covered = false; break; }
                    by ??= tris[hit].kind;
                }
                if (!covered)
                    continue;
                t.Hidden = true;
                hidden[by] = hidden.GetValueOrDefault(by) + 1;
            }
            foreach (var (kind, count) in hidden)
                said.Add($"under clothes: {count} triangles covered by the {kind} left out, so they can't poke through");
            return said;
        }

        static Vector3 Normal(ObjModel model, ObjModel.Corner c) => c.Normal >= 0 ? model.Normals[c.Normal] : Vector3.Zero;

        // Möller–Trumbore: how far along dir from p the ray meets triangle abc (either side), or null
        static float? Ray(Vector3 p, Vector3 dir, Vector3 a, Vector3 b, Vector3 c)
        {
            var e1 = b - a;
            var e2 = c - a;
            var h = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, h);
            if (MathF.Abs(det) < 1e-12f)
                return null;
            float f = 1 / det;
            var s = p - a;
            float u = f * Vector3.Dot(s, h);
            if (u < 0 || u > 1)
                return null;
            var q = Vector3.Cross(s, e1);
            float v = f * Vector3.Dot(dir, q);
            if (v < 0 || u + v > 1)
                return null;
            float t = f * Vector3.Dot(e2, q);
            return t > 1e-6f ? t : null;
        }
    }
}
