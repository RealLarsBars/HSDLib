using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// The Animelee look, for any model: a black cartoon outline around it (an inverted hull, the way
    /// Animelee and cel-shaded games draw one), flat lighting, and its colours flattened into bands.
    /// </summary>
    public static class Animelee
    {
        // how far the outline stands off the model, as a share of the model's height
        const float Thickness = 0.006f;

        /// <summary>
        /// Adds a black outline shell: a copy of the model pushed out along its normals with its faces
        /// turned inwards, which the game draws from one side only so just its rim shows. A model that
        /// comes with its own outline (BMO) keeps that one. Returns whether one was added.
        /// </summary>
        public static bool AddOutline(ObjModel model)
        {
            if (model.Triangles.Count == 0 || model.Triangles.Any(t => t.Outline))
                return false;

            // each point moves out along the average of its corners' normals (one per point, so the
            // shell stays closed across hard edges and UV seams)
            var push = new Vector3[model.Positions.Count];
            foreach (var t in model.Triangles)
                foreach (var c in new[] { t.A, t.B, t.C })
                    push[c.Position] += c.Normal >= 0 ? model.Normals[c.Normal] : Vector3.Zero;
            // some models' normals point inwards: outwards is away from the middle, on the whole
            var middle = model.Positions.Aggregate(Vector3.Zero, (a, p) => a + p) / model.Positions.Count;
            if (Enumerable.Range(0, push.Length).Sum(i => Vector3.Dot(push[i], model.Positions[i] - middle)) < 0)
                for (int i = 0; i < push.Length; i++)
                    push[i] = -push[i];
            float low = model.Positions.Min(p => p.Y), high = model.Positions.Max(p => p.Y);
            float thick = (high - low) * Thickness;

            var shell = new int[model.Positions.Count];
            Array.Fill(shell, -1);
            int Shell(int i)
            {
                if (shell[i] < 0)
                {
                    var n = push[i].LengthSquared() > 1e-12f ? Vector3.Normalize(push[i]) : Vector3.Zero;
                    shell[i] = model.Positions.Count;
                    model.Positions.Add(model.Positions[i] + n * thick);
                }
                return shell[i];
            }

            // cut-out parts (lashes, hair cards drawn on see-through squares) would get a black square
            var cutouts = model.Triangles.Select(t => t.Material).Distinct().Where(m => SeeThrough(model.TextureOf(m))).ToHashSet();
            const string material = "animelee outline";
            model.Textures[material] = BlackTexture(model.Directory);
            foreach (var t in model.Triangles.Where(t => !cutouts.Contains(t.Material)).ToList())
            {
                // turned inwards: wound against the way the model faces there (models aren't all wound the
                // same way round, their normals say which way is out)
                var a = model.Positions[t.A.Position]; var b = model.Positions[t.B.Position]; var c = model.Positions[t.C.Position];
                bool outwards = Vector3.Dot(Vector3.Cross(b - a, c - a), push[t.A.Position] + push[t.B.Position] + push[t.C.Position]) > 0;
                ObjModel.Corner C(ObjModel.Corner k) => new() { Position = Shell(k.Position), UV = k.UV, Normal = k.Normal };
                model.Triangles.Add(outwards
                    ? new ObjModel.Triangle { A = C(t.A), B = C(t.C), C = C(t.B), Material = material, Object = material, Outline = true }
                    : new ObjModel.Triangle { A = C(t.A), B = C(t.B), C = C(t.C), Material = material, Object = material, Outline = true });
            }
            return true;
        }

        static bool SeeThrough(string path)
        {
            if (path == null)
                return false;
            try
            {
                using var s = File.OpenRead(path);
                var img = StbImageSharp.ImageResult.FromStream(s, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                int clear = 0;
                for (int i = 3; i < img.Data.Length; i += 4)
                    if (img.Data[i] < 128)
                        clear++;
                return clear > img.Width * img.Height / 50;
            }
            catch
            {
                return false;
            }
        }

        static string BlackTexture(string folder)
        {
            var path = Path.Combine(folder ?? Path.GetTempPath(), "animelee-outline.png");
            if (!File.Exists(path))
            {
                var img = new Bitmap(8, 8);
                for (int i = 0; i < img.Rgba.Length; i += 4)
                {
                    img.Rgba[i] = img.Rgba[i + 1] = img.Rgba[i + 2] = 0;
                    img.Rgba[i + 3] = 255;
                }
                img.SavePng(path);
            }
            return path;
        }

        /// <summary>
        /// Flattens a texture's colours (RGBA bytes) into a few bands per channel and livens them a
        /// little, so soft painted shading reads as flat cartoon colour.
        /// </summary>
        public static void Flatten(byte[] rgba)
        {
            const int bands = 6;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                float r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                float grey = r * 0.3f + g * 0.59f + b * 0.11f;
                for (int c = 0; c < 3; c++)
                {
                    float v = grey + (rgba[i + c] - grey) * 1.15f;
                    v = MathF.Round(Math.Clamp(v, 0, 255) / 255 * (bands - 1)) / (bands - 1) * 255;
                    rgba[i + c] = (byte)v;
                }
            }
        }
    }
}
