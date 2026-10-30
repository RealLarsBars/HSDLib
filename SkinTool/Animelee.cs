using System;
using System.Collections.Generic;
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
            int Shell(int i, float t)
            {
                if (shell[i] < 0)
                {
                    var n = push[i].LengthSquared() > 1e-12f ? Vector3.Normalize(push[i]) : Vector3.Zero;
                    shell[i] = model.Positions.Count;
                    model.Positions.Add(model.Positions[i] + n * t);
                }
                return shell[i];
            }

            // cut-out parts (lashes, hair cards drawn on see-through squares) would get a black square
            var cutouts = model.Triangles.Select(t => t.Material).Distinct().Where(m => SeeThrough(model.TextureOf(m))).ToHashSet();
            const string material = "animelee outline";
            model.Textures[material] = BlackTexture(model.Directory);
            var outlined = model.Triangles.Where(t => !cutouts.Contains(t.Material)).ToList();
            // thin parts (glasses frames) get a thinner outline, flat ones none
            var limit = PieceThickness(outlined.Select(t => (model.Positions[t.A.Position], model.Positions[t.B.Position], model.Positions[t.C.Position])).ToList(),
                outlined.Select(t => (t.A.Position, t.B.Position, t.C.Position)).ToList(),
                outlined.Select(t => push[t.A.Position] + push[t.B.Position] + push[t.C.Position]).ToList(), thick);
            for (int ti = 0; ti < outlined.Count; ti++)
            {
                var t = outlined[ti];
                if (limit[ti] <= 0)
                    continue;
                // turned inwards: wound against the way the model faces there (models aren't all wound the
                // same way round, their normals say which way is out)
                var a = model.Positions[t.A.Position]; var b = model.Positions[t.B.Position]; var c = model.Positions[t.C.Position];
                bool outwards = Vector3.Dot(Vector3.Cross(b - a, c - a), push[t.A.Position] + push[t.B.Position] + push[t.C.Position]) > 0;
                ObjModel.Corner C(ObjModel.Corner k) => new() { Position = Shell(k.Position, limit[ti]), UV = k.UV, Normal = k.Normal };
                model.Triangles.Add(outwards
                    ? new ObjModel.Triangle { A = C(t.A), B = C(t.C), C = C(t.B), Material = material, Object = material, Outline = true }
                    : new ObjModel.Triangle { A = C(t.A), B = C(t.B), C = C(t.C), Material = material, Object = material, Outline = true });
            }
            return true;
        }

        /// <summary>
        /// How far the outline may stand off each triangle: a small, thin part (glasses frames, a strap,
        /// lashes) would be swallowed by a full-width outline, so its piece gets one in proportion to how thick it
        /// is (three times its volume over its surface area: a ball's radius, about a bar's half-width), or
        /// none when it is a flat sheet. <paramref name="ids"/> are the corners' places, which join the
        /// triangles into pieces; <paramref name="outward"/> is each triangle's outward normal.
        /// </summary>
        public static float[] PieceThickness(IList<(Vector3 A, Vector3 B, Vector3 C)> tris, IList<(int A, int B, int C)> ids, IList<Vector3> outward, float thick)
        {
            int places = ids.Count == 0 ? 0 : ids.Max(t => Math.Max(t.A, Math.Max(t.B, t.C))) + 1;
            var parent = Enumerable.Range(0, places).ToArray();
            int Root(int x)
            {
                while (parent[x] != x)
                    x = parent[x] = parent[parent[x]];
                return x;
            }
            foreach (var t in ids)
            {
                parent[Root(t.A)] = Root(t.B);
                parent[Root(t.B)] = Root(t.C);
            }
            var piece = ids.Select(t => Root(t.A)).ToArray();

            // volume and area per piece, each triangle wound to face out
            var centre = new Dictionary<int, (Vector3 Sum, int N)>();
            for (int i = 0; i < tris.Count; i++)
            {
                var c = centre.GetValueOrDefault(piece[i]);
                centre[piece[i]] = (c.Sum + tris[i].A + tris[i].B + tris[i].C, c.N + 3);
            }
            var volume = new Dictionary<int, float>();
            var area = new Dictionary<int, float>();
            for (int i = 0; i < tris.Count; i++)
            {
                var o = centre[piece[i]].Sum / centre[piece[i]].N;
                var (a, b, c) = (tris[i].A - o, tris[i].B - o, tris[i].C - o);
                var cross = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(cross, outward[i]) < 0)
                    (b, c, cross) = (c, b, -cross);
                volume[piece[i]] = volume.GetValueOrDefault(piece[i]) + Vector3.Dot(a, Vector3.Cross(b, c)) / 6;
                area[piece[i]] = area.GetValueOrDefault(piece[i]) + cross.Length() / 2;
            }
            // only small parts are judged by their thickness: a big one (a body, a head of hair) is often an
            // open surface, whose volume says nothing
            var lo = new Dictionary<int, Vector3>();
            var hi = new Dictionary<int, Vector3>();
            for (int i = 0; i < tris.Count; i++)
                foreach (var q in new[] { tris[i].A, tris[i].B, tris[i].C })
                {
                    lo[piece[i]] = lo.TryGetValue(piece[i], out var l) ? Vector3.Min(l, q) : q;
                    hi[piece[i]] = hi.TryGetValue(piece[i], out var h) ? Vector3.Max(h, q) : q;
                }
            var limit = new Dictionary<int, float>();
            foreach (var (p, a) in area)
            {
                if ((hi[p] - lo[p]).Length() > thick * 20)
                {
                    limit[p] = thick;
                    continue;
                }
                float depth = a > 0 ? 3 * MathF.Abs(volume[p]) / a : 0;
                float t = MathF.Min(thick, depth * 0.3f);
                limit[p] = t < thick * 0.15f ? 0 : t;
            }
            return piece.Select(p => limit[p]).ToArray();
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

        /// <summary>
        /// Makes room for an outline inside the model's own textures: each one doubles in height, the
        /// picture in its top half and the bottom half black, and the model's texture coordinates move into
        /// the top half. A weapon goes in the game's few weapon slots, one texture each, so its outline can't
        /// take a material of its own as the body's does; this way the outline can be drawn on the black half
        /// (see <see cref="AnimeleeDat"/>). Textures repeated over the model (coordinates outside 0 to 1) are
        /// left as they are, and that part gets no outline. Returns how many textures were changed.
        /// </summary>
        public static int MakeOutlineRoom(ObjModel model, string folder)
        {
            int changed = 0;
            var moved = new Dictionary<int, int>();
            foreach (var group in model.Triangles.Where(t => !t.Outline).GroupBy(t => t.Material).ToList())
            {
                var texture = model.TextureOf(group.Key);
                var corners = group.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct().ToList();
                if (texture == null || corners.Any(c => c.UV < 0)
                    || corners.Any(c => model.UVs[c.UV].X < -0.001f || model.UVs[c.UV].X > 1.001f || model.UVs[c.UV].Y < -0.001f || model.UVs[c.UV].Y > 1.001f))
                    continue;
                StbImageSharp.ImageResult image;
                try
                {
                    using var stream = File.OpenRead(texture);
                    image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                }
                catch
                {
                    continue;
                }
                var room = new Bitmap(image.Width, image.Height * 2);
                Buffer.BlockCopy(image.Data, 0, room.Rgba, 0, image.Data.Length);
                for (int i = image.Data.Length; i < room.Rgba.Length; i += 4)
                {
                    room.Rgba[i] = room.Rgba[i + 1] = room.Rgba[i + 2] = 0;
                    room.Rgba[i + 3] = 255;
                }
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, $"outline-room-{changed}-{Path.GetFileNameWithoutExtension(texture)}.png");
                room.SavePng(path);
                model.Textures[group.Key] = path;
                // v runs up from the bottom of the picture: the old picture is now its top half
                foreach (var c in corners)
                {
                    if (!moved.TryGetValue(c.UV, out int uv))
                    {
                        uv = moved[c.UV] = model.UVs.Count;
                        model.UVs.Add(new Vector2(model.UVs[c.UV].X, 0.5f + model.UVs[c.UV].Y * 0.5f));
                    }
                    c.UV = uv;
                }
                changed++;
            }
            return changed;
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
        /// Flattens a texture's colours (RGBA bytes): brightness falls into a few bands, so soft painted
        /// shading reads as flat cartoon colour, while each colour keeps its hue (skin stays skin-coloured),
        /// a little livelier.
        /// </summary>
        public static void Flatten(byte[] rgba)
        {
            const int bands = 6;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                float r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                float grey = r * 0.3f + g * 0.59f + b * 0.11f;
                r = grey + (r - grey) * 1.1f;
                g = grey + (g - grey) * 1.1f;
                b = grey + (b - grey) * 1.1f;
                float v = MathF.Max(r, MathF.Max(g, b));
                if (v <= 0)
                    continue;
                float flat = MathF.Max(MathF.Round(Math.Clamp(v, 0, 255) / 255 * (bands - 1)), 0.5f) / (bands - 1) * 255;
                float k = flat / v;
                rgba[i] = (byte)Math.Clamp(r * k, 0, 255);
                rgba[i + 1] = (byte)Math.Clamp(g * k, 0, 255);
                rgba[i + 2] = (byte)Math.Clamp(b * k, 0, 255);
            }
        }
    }
}
