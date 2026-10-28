using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Super Mario Odyssey rips come with the face's eyes in one expression (Peach's shut in a smile) and
    /// each expression's eye area as its own file beside the model: Name_LEye.SMD and Name_REye.SMD hold the
    /// open eyes. When those sit next to the model, the open eye areas replace the face's own, so the skin
    /// looks out with open eyes.
    /// </summary>
    public static class EyeSwap
    {
        public static string Apply(ObjModel model, string modelPath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(modelPath));
            // the same model exported to Collada is Name_OpenCollada.dae
            var stem = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(modelPath), "_OpenCollada$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var files = new[] { "_LEye.smd", "_REye.smd" }
                .Select(suffix => Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(stem + suffix, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (files.Any(f => f == null) || model.Triangles.Count == 0)
                return null;

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in model.Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            float weld = (max - min).Length() * 0.0005f;

            int swapped = 0;
            foreach (var file in files)
            {
                // a Collada export of the same model may be in other units (Peach's in metres, from inches)
                var area = ReadSmd(file);
                if (area.Count == 0 || !Scales.Any(k => Swap(model, Scaled(area, k), weld)))
                    return swapped > 0 ? "open eyes swapped in for one eye only" : null;
                swapped++;
            }
            return "open eyes: swapped the face's eye areas for the open ones beside the model";
        }

        static readonly float[] Scales = { 1, 0.0254f, 1 / 0.0254f, 0.01f, 100 };

        static List<Corner[]> Scaled(List<Corner[]> area, float k) => k == 1 ? area
            : area.Select(t => t.Select(c => new Corner { Position = c.Position * k, Normal = c.Normal, UV = c.UV }).ToArray()).ToList();

        class Corner
        {
            public Vector3 Position, Normal;
            public Vector2 UV;
        }

        /// <summary>The triangles of an SMD file, turned from its Z-up into the model's Y-up.</summary>
        static List<Corner[]> ReadSmd(string path)
        {
            var tris = new List<Corner[]>();
            var lines = File.ReadAllLines(path);
            int at = Array.FindIndex(lines, l => l.Trim() == "triangles");
            if (at < 0)
                return tris;
            static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
            static Vector3 Up(Vector3 v) => new(v.X, v.Z, -v.Y);
            for (int i = at + 1; i + 3 < lines.Length && lines[i].Trim() != "end"; i += 4)
            {
                var tri = new Corner[3];
                for (int k = 0; k < 3; k++)
                {
                    var f = lines[i + 1 + k].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 9)
                        return new List<Corner[]>();
                    tri[k] = new Corner
                    {
                        Position = Up(new Vector3(F(f[1]), F(f[2]), F(f[3]))),
                        Normal = Up(new Vector3(F(f[4]), F(f[5]), F(f[6]))),
                        UV = new Vector2(F(f[7]), F(f[8])),
                    };
                }
                tris.Add(tri);
            }
            return tris;
        }

        /// <summary>
        /// Cuts the face's own eye area out (everything inside the open edge the new area stitches onto) and
        /// puts the new one in. False when the edge doesn't line up with the face, leaving the model as it was.
        /// </summary>
        static bool Swap(ObjModel model, List<Corner[]> area, float weld)
        {
            // the new area's open edge, and the face's points on it
            var key = new Dictionary<Vector3, int>();
            int Id(Vector3 p) => key.TryGetValue(p, out int id) ? id : key[p] = key.Count;
            var edges = new Dictionary<(int, int), int>();
            foreach (var t in area)
                for (int k = 0; k < 3; k++)
                {
                    int a = Id(t[k].Position), b = Id(t[(k + 1) % 3].Position);
                    var e = (Math.Min(a, b), Math.Max(a, b));
                    edges[e] = edges.GetValueOrDefault(e) + 1;
                }
            var points = key.OrderBy(e => e.Value).Select(e => e.Key).ToArray();
            var rim = edges.Where(e => e.Value == 1).SelectMany(e => new[] { e.Key.Item1, e.Key.Item2 }).Distinct().ToList();
            if (rim.Count < 6)
                return false;

            int Nearest(Vector3 p)
            {
                int best = -1;
                float bestD = weld * weld;
                for (int i = 0; i < model.Positions.Count; i++)
                {
                    float d = Vector3.DistanceSquared(model.Positions[i], p);
                    if (d <= bestD)
                    {
                        bestD = d;
                        best = i;
                    }
                }
                return best;
            }
            var onFace = rim.ToDictionary(r => r, r => Nearest(points[r]));
            if (onFace.Values.Count(v => v >= 0) < rim.Count * 0.9f)
                return false;
            var ring = onFace.Values.Where(v => v >= 0).ToHashSet();

            // the face's material is the one its rim points belong to
            var material = model.Triangles.Where(t => ring.Contains(t.A.Position) || ring.Contains(t.B.Position) || ring.Contains(t.C.Position))
                .GroupBy(t => t.Material).OrderByDescending(g => g.Count()).First().Key;
            var face = model.Triangles.Where(t => t.Material == material).ToList();
            var adj = new Dictionary<int, List<int>>();
            void Link(int a, int b)
            {
                if (!adj.TryGetValue(a, out var l)) adj[a] = l = new List<int>();
                l.Add(b);
            }
            foreach (var t in face)
            {
                Link(t.A.Position, t.B.Position); Link(t.B.Position, t.A.Position);
                Link(t.B.Position, t.C.Position); Link(t.C.Position, t.B.Position);
                Link(t.C.Position, t.A.Position); Link(t.A.Position, t.C.Position);
            }

            // fill the inside of the rim from the face point nearest the middle of the new area
            var middle = points.Aggregate(Vector3.Zero, (s, p) => s + p) / points.Length;
            int seed = adj.Keys.Where(i => !ring.Contains(i)).MinBy(i => Vector3.DistanceSquared(model.Positions[i], middle));
            var inside = new HashSet<int> { seed };
            var queue = new Queue<int>(inside);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                foreach (int j in adj[i])
                    if (!ring.Contains(j) && inside.Add(j))
                        queue.Enqueue(j);
                // the rim doesn't close the area off: leave the face alone
                if (inside.Count > points.Length * 3)
                    return false;
            }

            model.Triangles.RemoveAll(t => t.Material == material
                && new[] { t.A.Position, t.B.Position, t.C.Position }.All(i => inside.Contains(i) || ring.Contains(i))
                && new[] { t.A.Position, t.B.Position, t.C.Position }.Any(inside.Contains));

            // the new area, stitched to the face's rim points
            var index = new int[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                if (onFace.TryGetValue(i, out int f) && f >= 0)
                    index[i] = f;
                else
                {
                    index[i] = model.Positions.Count;
                    model.Positions.Add(points[i]);
                }
            }
            foreach (var t in area)
            {
                ObjModel.Corner C(Corner c)
                {
                    model.UVs.Add(c.UV);
                    model.Normals.Add(c.Normal.LengthSquared() > 0 ? Vector3.Normalize(c.Normal) : Vector3.UnitZ);
                    return new ObjModel.Corner { Position = index[key[c.Position]], UV = model.UVs.Count - 1, Normal = model.Normals.Count - 1 };
                }
                model.Triangles.Add(new ObjModel.Triangle { A = C(t[0]), B = C(t[1]), C = C(t[2]), Material = material, Object = "eye area" });
            }

            // the face's lashes come in a set for every expression, each a small piece of its own: only
            // those on the open eyelids stay, the rest would draw lines across the open eyes
            var lo = points.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var hi = points.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            var parent = new Dictionary<int, int>();
            int Root(int x)
            {
                while (parent.GetValueOrDefault(x, x) != x)
                    x = parent[x];
                return x;
            }
            void Join(int a, int b)
            {
                a = Root(a); b = Root(b);
                if (a != b) parent[a] = b;
            }
            foreach (var t in model.Triangles.Where(t => t.Material == material))
            {
                Join(t.A.Position, t.B.Position);
                Join(t.B.Position, t.C.Position);
            }
            var lashes = model.Triangles.Where(t => t.Material == material && t.Object != "eye area")
                .GroupBy(t => Root(t.A.Position)).Where(g => g.Count() <= 60)
                .Where(g =>
                {
                    var ps = g.SelectMany(t => new[] { t.A.Position, t.B.Position, t.C.Position }).Distinct().Select(i => model.Positions[i]).ToList();
                    var c = ps.Aggregate(Vector3.Zero, (a, b) => a + b) / ps.Count;
                    return c.X >= lo.X && c.X <= hi.X && c.Y >= lo.Y && c.Y <= hi.Y
                        && ps.Min(p => points.Min(q => Vector3.DistanceSquared(p, q))) > weld * weld;
                })
                .SelectMany(g => g).ToHashSet();
            model.Triangles.RemoveAll(lashes.Contains);
            return true;
        }
    }
}
