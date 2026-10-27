using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Finds a cartoon outline shell (an "inverted hull"): a dark copy of the model, a little bigger, with its
    /// faces turned inwards. Games draw it from one side only, so it shows as a black edge around the model;
    /// drawn from both sides, as skin meshes are, it covers the whole model in black.
    /// </summary>
    public static class Outlines
    {
        public static int Mark(ObjModel model)
        {
            var tris = model.Triangles;
            if (tris.Count == 0)
                return 0;

            var images = new Dictionary<string, StbImageSharp.ImageResult>();
            float Brightness(ObjModel.Triangle t)
            {
                var path = model.TextureOf(t.Material);
                if (path == null || t.A.UV < 0 || t.B.UV < 0 || t.C.UV < 0)
                    return 1;
                if (!images.TryGetValue(path, out var img))
                {
                    try
                    {
                        using var s = File.OpenRead(path);
                        img = StbImageSharp.ImageResult.FromStream(s, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                    }
                    catch
                    {
                        img = null;
                    }
                    images[path] = img;
                }
                if (img == null)
                    return 1;
                // the middle and the three corners, so a dark triangle has to be dark all over
                var a = model.UVs[t.A.UV]; var b = model.UVs[t.B.UV]; var c = model.UVs[t.C.UV];
                float most = 0;
                foreach (var uv in new[] { (a + b + c) / 3, a, b, c })
                {
                    float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
                    int x = Math.Clamp((int)(u * img.Width), 0, img.Width - 1);
                    int y = Math.Clamp((int)((1 - v) * img.Height), 0, img.Height - 1);
                    int i = (y * img.Width + x) * 4;
                    most = MathF.Max(most, (img.Data[i] * 0.3f + img.Data[i + 1] * 0.59f + img.Data[i + 2] * 0.11f) / 255);
                }
                return most;
            }

            Vector3 Centre(ObjModel.Triangle t) => (model.Positions[t.A.Position] + model.Positions[t.B.Position] + model.Positions[t.C.Position]) / 3;
            Vector3 Facing(ObjModel.Triangle t)
            {
                var a = model.Positions[t.A.Position];
                var n = Vector3.Cross(model.Positions[t.B.Position] - a, model.Positions[t.C.Position] - a);
                return n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.Zero;
            }

            var dark = tris.Where(t => Brightness(t) < 0.12f).ToList();
            var rest = tris.Where(t => !dark.Contains(t)).ToList();
            if (dark.Count < tris.Count * 0.15f || rest.Count == 0)
                return 0;

            // the rest of the model's triangles, in a grid, to find the surface just inside each dark one
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in model.Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            float reach = (max - min).Length() * 0.05f;
            var grid = new Dictionary<(int, int, int), List<Vector3>>();
            (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor((p.X - min.X) / reach), (int)MathF.Floor((p.Y - min.Y) / reach), (int)MathF.Floor((p.Z - min.Z) / reach));
            foreach (var t in rest)
            {
                var c = Centre(t);
                var k = Cell(c);
                if (!grid.TryGetValue(k, out var list))
                    grid[k] = list = new List<Vector3>();
                list.Add(c);
            }

            // a shell triangle faces the surface it wraps
            var shell = new List<ObjModel.Triangle>();
            foreach (var t in dark)
            {
                var c = Centre(t);
                var (x, y, z) = Cell(c);
                Vector3 best = default;
                float bestD = reach * reach;
                bool found = false;
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                            if (grid.TryGetValue((x + dx, y + dy, z + dz), out var list))
                                foreach (var o in list)
                                {
                                    float d = Vector3.DistanceSquared(o, c);
                                    if (d < bestD)
                                    {
                                        bestD = d;
                                        best = o;
                                        found = true;
                                    }
                                }
                if (found && Vector3.Dot(Facing(t), best - c) > 0)
                    shell.Add(t);
            }

            // most of the dark part has to face inwards, or it's just a dark part of the model
            if (shell.Count < dark.Count * 0.7f)
                return 0;
            foreach (var t in shell)
                t.Outline = true;
            return shell.Count;
        }
    }
}
