using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// A Wavefront OBJ model with its MTL materials.
    /// Positions are shared between corners so weights can be computed per position.
    /// </summary>
    public class ObjModel
    {
        public class Corner
        {
            public int Position;
            public int UV = -1;
            public int Normal = -1;
        }

        public class Triangle
        {
            public Corner A, B, C;
            public string Material;
            public string Object;
        }

        public readonly List<Vector3> Positions = new();
        public readonly List<Vector2> UVs = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Triangle> Triangles = new();

        // material name to diffuse texture path
        public Dictionary<string, string> Textures = new();

        public string Directory;

        public static ObjModel Load(string path)
        {
            if (!path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                return ModelImport.Load(path);

            var model = new ObjModel { Directory = Path.GetDirectoryName(Path.GetFullPath(path)) };
            string material = null;
            string obj = null;

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;

                var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0])
                {
                    case "v":
                        model.Positions.Add(new Vector3(F(parts[1]), F(parts[2]), F(parts[3])));
                        break;
                    case "vt":
                        model.UVs.Add(new Vector2(F(parts[1]), parts.Length > 2 ? F(parts[2]) : 0));
                        break;
                    case "vn":
                        model.Normals.Add(new Vector3(F(parts[1]), F(parts[2]), F(parts[3])));
                        break;
                    case "o":
                    case "g":
                        obj = parts.Length > 1 ? parts[1] : null;
                        break;
                    case "usemtl":
                        material = parts.Length > 1 ? parts[1] : null;
                        break;
                    case "mtllib":
                        model.LoadMtl(model.Find(line.Substring(parts[0].Length).Trim()));
                        break;
                    case "f":
                        var corners = new List<Corner>();
                        for (int i = 1; i < parts.Length; i++)
                            corners.Add(model.ParseCorner(parts[i]));
                        // fan triangulate
                        for (int i = 1; i + 1 < corners.Count; i++)
                            model.Triangles.Add(new Triangle { A = corners[0], B = corners[i], C = corners[i + 1], Material = material, Object = obj });
                        break;
                }
            }

            return model;
        }

        private Corner ParseCorner(string s)
        {
            var idx = s.Split('/');
            var c = new Corner { Position = Index(idx[0], Positions.Count) };
            if (idx.Length > 1 && idx[1].Length > 0)
                c.UV = Index(idx[1], UVs.Count);
            if (idx.Length > 2 && idx[2].Length > 0)
                c.Normal = Index(idx[2], Normals.Count);
            return c;
        }

        // OBJ indices start at 1, negative ones count back from the end
        private static int Index(string s, int count)
        {
            int i = int.Parse(s, CultureInfo.InvariantCulture);
            return i < 0 ? count + i : i - 1;
        }

        private void LoadMtl(string path)
        {
            if (!File.Exists(path))
                return;

            string current = null;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("newmtl "))
                    current = line.Substring(7).Trim();
                else if (line.StartsWith("map_Kd ") && current != null)
                {
                    // the file name is the last token; earlier tokens can be options like -s
                    var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    Textures[current] = Find(parts[parts.Length - 1]);
                }
            }
        }

        /// <summary>
        /// Finds a file the OBJ or MTL names. Models exported on Windows often name absolute paths
        /// or use different letter case, so this falls back to the file name anywhere in the model's folder.
        /// </summary>
        internal string Find(string name)
        {
            name = name.Replace('\\', '/');
            var full = Path.Combine(Directory, name);
            if (File.Exists(full))
                return full;

            var fileName = Path.GetFileName(name);
            var match = System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
            return match ?? Path.Combine(Directory, fileName);
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        /// <summary>
        /// A copy that reads each texture from another folder, by file name, when it exists there.
        /// </summary>
        public ObjModel WithTextureFolder(string folder)
        {
            var copy = (ObjModel)MemberwiseClone();
            var textures = new Dictionary<string, string>();
            foreach (var kv in Textures)
            {
                var swap = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(kv.Value));
                textures[kv.Key] = File.Exists(swap) ? swap : kv.Value;
            }
            copy.Textures = textures;
            return copy;
        }

        /// <summary>
        /// Closes small openings, like the open bases of claws and teeth that sit inside the body in the
        /// source model. Once the body bends a little differently from the part, the opening shows as a
        /// hole. Big openings (a mouth, an open cape) are left alone. Returns how many were closed.
        /// </summary>
        public int CloseSmallHoles(int maxCorners = 8, float maxShare = 0.05f)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            float limit = (max - min).Length() * maxShare;

            // positions at the same place count as one, so a texture seam that splits them isn't an opening
            var first = new Dictionary<Vector3, int>();
            var same = Positions.Select((p, i) => first.TryGetValue(p, out int f) ? f : first[p] = i).ToArray();
            (int, int) Key(int a, int b) => same[a] < same[b] ? (same[a], same[b]) : (same[b], same[a]);
            var count = new Dictionary<(int, int), int>();
            foreach (var t in Triangles)
                foreach (var (a, b) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                    count[Key(a.Position, b.Position)] = count.GetValueOrDefault(Key(a.Position, b.Position)) + 1;

            // open edges, each in the direction its triangle runs, so the cap runs the other way
            var next = new Dictionary<int, (Corner from, Corner to, Triangle tri)>();
            foreach (var t in Triangles)
                foreach (var (a, b) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
                    if (count[Key(a.Position, b.Position)] == 1)
                        next[same[b.Position]] = (b, a, t);

            int closed = 0;
            var done = new HashSet<int>();
            foreach (var start in next.Keys.ToList())
            {
                if (done.Contains(start))
                    continue;
                var loop = new List<(Corner from, Corner to, Triangle tri)>();
                int v = start;
                while (next.TryGetValue(v, out var e) && !done.Contains(v) && loop.Count <= maxCorners)
                {
                    done.Add(v);
                    loop.Add(e);
                    v = same[e.to.Position];
                }
                if (v != start || loop.Count < 3 || loop.Count > maxCorners)
                    continue;

                var middle = loop.Aggregate(Vector3.Zero, (s, e) => s + Positions[e.from.Position]) / loop.Count;
                if (loop.Max(e => Vector3.Distance(Positions[e.from.Position], middle)) > limit)
                    continue;

                var normal = Vector3.Zero;
                foreach (var e in loop)
                    normal += Vector3.Cross(Positions[e.to.Position] - Positions[e.from.Position], middle - Positions[e.from.Position]);
                int center = Positions.Count;
                Positions.Add(middle);
                int n = Normals.Count;
                Normals.Add(normal.LengthSquared() > 0 ? Vector3.Normalize(normal) : Vector3.UnitY);
                foreach (var e in loop)
                    Triangles.Add(new Triangle
                    {
                        A = new Corner { Position = e.from.Position, UV = e.to.UV, Normal = n },
                        B = new Corner { Position = e.to.Position, UV = e.to.UV, Normal = n },
                        C = new Corner { Position = center, UV = e.to.UV, Normal = n },
                        Material = e.tri.Material,
                        Object = e.tri.Object,
                    });
                closed++;
            }
            return closed;
        }

        public string TextureOf(string material)
        {
            return material != null && Textures.TryGetValue(material, out var t) && File.Exists(t) ? t : null;
        }
    }
}
