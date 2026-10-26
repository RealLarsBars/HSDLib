using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
                        model.LoadMtl(Path.Combine(model.Directory, line.Substring(parts[0].Length).Trim()));
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
                    var file = parts[parts.Length - 1].Replace('\\', '/');
                    var full = Path.Combine(Directory, file);
                    if (!File.Exists(full))
                        full = Path.Combine(Directory, Path.GetFileName(file));
                    Textures[current] = full;
                }
            }
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

        public string TextureOf(string material)
        {
            return material != null && Textures.TryGetValue(material, out var t) && File.Exists(t) ? t : null;
        }
    }
}
