using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Finds shading layers: a small part with a plain grey texture lying right on top of another part, like
    /// the shadow over Peach's eyes in Super Mario Odyssey. The game it came from blends it into what's under
    /// it; drawn solid it covers that part in grey (her eyes looked half shut), so it is left out.
    /// </summary>
    public static class ShadeLayers
    {
        public static List<string> Remove(ObjModel model)
        {
            var removed = new List<string>();
            var tris = model.Triangles;
            if (tris.Count == 0)
                return removed;

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in model.Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            float near = (max - min).Length() * 0.01f;

            foreach (var group in tris.GroupBy(t => t.Material).ToList())
            {
                var mine = group.ToList();
                if (group.Key == null || mine.Count > tris.Count * 0.05f || !Grey(model.TextureOf(group.Key)))
                    continue;

                // lying on top of another part: nearly every point of it close to another material's surface
                var others = tris.Where(t => t.Material != group.Key).SelectMany(Corners).Distinct().Select(i => model.Positions[i]).ToList();
                var points = mine.SelectMany(Corners).Distinct().Select(i => model.Positions[i]).ToList();
                if (others.Count == 0)
                    continue;
                int covering = points.Count(p => others.Any(o => Vector3.DistanceSquared(o, p) < near * near));
                if (covering < points.Count * 0.9f)
                    continue;

                tris.RemoveAll(t => t.Material == group.Key);
                removed.Add(group.Key);
            }
            return removed;
        }

        static IEnumerable<int> Corners(ObjModel.Triangle t) => new[] { t.A.Position, t.B.Position, t.C.Position };

        /// <summary>
        /// A texture with no colour in it at all, in middling greys (not white: a white part is a real part,
        /// like the whites of the eyes).
        /// </summary>
        static bool Grey(string path)
        {
            if (path == null || !File.Exists(path))
                return false;
            StbImageSharp.ImageResult img;
            try
            {
                using var s = File.OpenRead(path);
                img = StbImageSharp.ImageResult.FromStream(s, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            }
            catch
            {
                return false;
            }
            long count = 0, coloured = 0, light = 0;
            for (int i = 0; i < img.Data.Length; i += 4)
            {
                if (img.Data[i + 3] < 128)
                    continue;
                int r = img.Data[i], g = img.Data[i + 1], b = img.Data[i + 2];
                count++;
                if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 16)
                    coloured++;
                if (r + g + b > 3 * 235)
                    light++;
            }
            return count > 0 && coloured < count / 100 && light < count / 2;
        }
    }
}
