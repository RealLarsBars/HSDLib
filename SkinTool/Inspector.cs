using HSDRaw;
using HSDRaw.Common;
using System;
using System.Linq;

namespace SkinTool
{
    /// <summary>
    /// Prints a costume's joints and mesh slots.
    /// </summary>
    public static class Inspector
    {
        public static int Run(string path)
        {
            var f = new HSDRawFile(path);
            foreach (var r in f.Roots)
                Console.WriteLine($"root {r.Name} {r.Data?.GetType().Name}");

            var root = (HSD_JOBJ)f.Roots[0].Data;
            var list = root.TreeList;
            Console.WriteLine($"joints {list.Count}");

            var sk = new Skeleton(root);
            for (int i = 0; i < list.Count; i++)
            {
                var w = sk.Position(i);
                Console.WriteLine($"joint {i} parent {sk.Parent[i]} at ({w.X:F2}, {w.Y:F2}, {w.Z:F2}) {list[i].Flags & (JOBJ_FLAG.CLASSICAL_SCALING | JOBJ_FLAG.MTX_SCALE_COMPENSATE)}");
            }

            int slot = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var j = list[i];
                if (j.Dobj == null)
                    continue;

                Console.WriteLine($"joint {i} flags={j.Flags}");
                foreach (var d in j.Dobj.List)
                {
                    var texs = d.Mobj?.Textures?.List.Select(t => $"{t.ImageData?.Format} {t.ImageData?.Width}x{t.ImageData?.Height}") ?? Enumerable.Empty<string>();
                    Console.Write($"  slot {slot++}: {d.Mobj?.RenderFlags} [{string.Join(", ", texs)}]");
                    if (d.Pobj == null)
                    {
                        Console.WriteLine(" empty");
                        continue;
                    }

                    var bones = d.Pobj.List
                        .SelectMany(p => p.EnvelopeWeights ?? Array.Empty<HSD_Envelope>())
                        .SelectMany(e => e.JOBJs)
                        .Select(b => list.IndexOf(b))
                        .Distinct()
                        .OrderBy(b => b);
                    int indices = d.Pobj.List.Sum(p => p.ToDisplayList().Primitives.Sum(pr => pr.Indices.Length));
                    Console.WriteLine($" {indices} indices, bones {string.Join(",", bones)}");
                }
            }

            return 0;
        }
    }
}
