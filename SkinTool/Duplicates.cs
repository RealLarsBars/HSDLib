using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Finds parts that come twice: two versions of the same part on top of each other, like the gameplay
    /// and cutscene bodies and heads of Gusteau's Ratatouille rip. The game showed one at a time; drawn
    /// together they fight over which is in front and bend apart in moves, so only one is kept: the one not
    /// named for cutscenes, or else the most detailed (remy_lod0 over remy_lod1), or else the first.
    /// </summary>
    public static class Duplicates
    {
        public static List<string> Remove(ObjModel model)
        {
            var removed = new List<string>();
            var tris = model.Triangles;
            // an outline shell sits just outside its part, and isn't a copy of it
            var parts = tris.Where(t => t.Object != null && !t.Outline).GroupBy(t => t.Object)
                .Select(g => (name: g.Key, points: g.SelectMany(t => new[] { t.A.Position, t.B.Position, t.C.Position }).Distinct().Select(i => model.Positions[i]).ToList()))
                .ToList();
            if (parts.Count < 2)
                return removed;

            float near = 1;
            (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / near), (int)MathF.Floor(p.Y / near), (int)MathF.Floor(p.Z / near));
            Dictionary<(int, int, int), List<Vector3>> Grid(List<Vector3> points)
            {
                var grid = new Dictionary<(int, int, int), List<Vector3>>();
                foreach (var p in points)
                {
                    var c = Cell(p);
                    if (!grid.TryGetValue(c, out var l))
                        grid[c] = l = new List<Vector3>();
                    l.Add(p);
                }
                return grid;
            }
            bool Close(Vector3 p, Dictionary<(int, int, int), List<Vector3>> grid)
            {
                var (x, y, z) = Cell(p);
                for (int i = -1; i <= 1; i++)
                    for (int j = -1; j <= 1; j++)
                        for (int k = -1; k <= 1; k++)
                            if (grid.TryGetValue((x + i, y + j, z + k), out var l) && l.Any(q => Vector3.DistanceSquared(p, q) < near * near))
                                return true;
                return false;
            }
            // most of one part's points lie on the other, and the other way round
            float Covered(List<Vector3> a, Dictionary<(int, int, int), List<Vector3>> b) => a.Count(p => Close(p, b)) / (float)a.Count;

            (Vector3 lo, Vector3 hi) Box(List<Vector3> points) =>
                (points.Aggregate(new Vector3(float.MaxValue), Vector3.Min), points.Aggregate(new Vector3(float.MinValue), Vector3.Max));
            var boxes = parts.Select(p => Box(p.points)).ToList();
            var gone = new HashSet<int>();
            for (int a = 0; a < parts.Count; a++)
                for (int b = a + 1; b < parts.Count; b++)
                {
                    if (gone.Contains(a) || gone.Contains(b))
                        continue;
                    // the same size and place on every axis
                    var sa = boxes[a].hi - boxes[a].lo;
                    var sb = boxes[b].hi - boxes[b].lo;
                    float size = MathF.Min(sa.Length(), sb.Length());
                    if (size <= 0 || (sa - sb).Length() > 0.1f * size
                        || Vector3.Distance(boxes[a].lo + boxes[a].hi, boxes[b].lo + boxes[b].hi) / 2 > 0.1f * size)
                        continue;
                    near = 0.05f * size;
                    if (Covered(parts[a].points, Grid(parts[b].points)) < 0.8f || Covered(parts[b].points, Grid(parts[a].points)) < 0.8f)
                        continue;
                    int drop = Cutscene(parts[a].name) != Cutscene(parts[b].name) ? (Cutscene(parts[a].name) ? a : b)
                        : Lod(parts[a].name) > Lod(parts[b].name) ? a : b;
                    gone.Add(drop);
                    removed.Add($"{parts[drop].name} (a second copy of {parts[drop == a ? b : a].name})");
                }

            var names = gone.Select(i => parts[i].name).ToHashSet();
            if (names.Count > 0)
            {
                tris.RemoveAll(t => t.Object != null && names.Contains(t.Object));
                model.DropUnusedPositions();
            }
            return removed;
        }

        // the detail level in a name like remy_lod1 (0 is the most detailed), or -1
        static int Lod(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(name, @"lod_?(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        static bool Cutscene(string name) =>
            new[] { "cutscene", "cinematic", "movie", "event" }.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
    }
}
