using HSDRaw;
using HSDRaw.Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SkinTool
{
    static class Program
    {
        const string Usage =
@"skintool build --model <model.obj> --dat <costume.dat> [--dat <costume.dat> ...] --out <folder>
               [--fighter <Pl**.dat>] [--scale 1.0] [--rotate 0] [--textures <costume.dat>=<folder> ...]
skintool inspect <costume.dat>
skintool check <original.dat> <skin.dat>
skintool preview <out.png> <costume.dat> [<costume.dat> ...]

build    Swaps the model into every costume given, keeping each costume's skeleton.
         The model is fitted and weighted once, against the first costume.
         --fighter reads which mesh slots the game shows, so the model never lands in a hidden one.
         --textures swaps in PNGs from another folder for one costume (matched by file name).
inspect  Lists a costume's joints and meshes.
check    Compares a finished skin with the original costume, as the build does.
preview  Renders each costume in a few test poses, one row per costume.";

        static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    Console.WriteLine(Usage);
                    return 1;
                }

                switch (args[0])
                {
                    case "build": return Build(args.Skip(1).ToArray());
                    case "inspect": return Inspector.Run(args[1]);
                    case "preview": return Preview.Run(args);
                    case "slots": return Slots.Dump(args);
                    case "check": return Check(args[1], args[2]);
                    default:
                        Console.WriteLine(Usage);
                        return 1;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"error: {e.Message}");
                return 2;
            }
        }

        static int Check(string original, string skin)
        {
            var before = Validator.Snapshot((HSD_JOBJ)new HSDRawFile(original).Roots[0].Data);
            var problems = Validator.Check(before, (HSD_JOBJ)new HSDRawFile(skin).Roots[0].Data);
            foreach (var p in problems)
                Console.WriteLine(p);
            Console.WriteLine(problems.Count == 0 ? "ok: same skeleton and slot count, weights valid" : $"{problems.Count} problems");
            return problems.Count == 0 ? 0 : 3;
        }

        static int Build(string[] args)
        {
            string model = null, outDir = null, fighterDat = null;
            var dats = new List<string>();
            var textureFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var opt = new AutoRig.Options();

            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new Exception($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--model": model = Next(); break;
                    case "--dat": dats.Add(Next()); break;
                    case "--out": outDir = Next(); break;
                    case "--fighter": fighterDat = Next(); break;
                    case "--scale": opt.Scale = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--rotate": opt.RotateY = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--textures":
                        var kv = Next().Split('=', 2);
                        textureFolders[Path.GetFileName(kv[0])] = kv[1];
                        break;
                    default: throw new Exception($"unknown option {args[i]}");
                }
            }

            if (model == null || dats.Count == 0 || outDir == null)
            {
                Console.WriteLine(Usage);
                return 1;
            }

            Directory.CreateDirectory(outDir);
            var fighterData = fighterDat != null ? Slots.Load(fighterDat) : null;
            var obj = ObjModel.Load(model);
            Console.WriteLine($"model: {obj.Positions.Count} vertices, {obj.Triangles.Count} triangles, {obj.Textures.Count} materials");

            // fit and weight once against the first costume; every costume shares the skeleton
            var first = new HSDRawFile(dats[0]);
            var firstRoot = (HSD_JOBJ)first.Roots[0].Data;
            var skeleton = new Skeleton(firstRoot);
            var fighter = new FighterMesh(firstRoot, skeleton);
            var rig = AutoRig.Run(obj, fighter, skeleton, opt);
            Console.Write(rig.Report);

            foreach (var dat in dats)
            {
                var file = dat == dats[0] ? first : new HSDRawFile(dat);
                var root = (HSD_JOBJ)file.Roots[0].Data;
                var sk = dat == dats[0] ? skeleton : new Skeleton(root);

                if (!Validator.SameSkeleton(skeleton, sk))
                    throw new Exception($"{Path.GetFileName(dat)} has a different skeleton from {Path.GetFileName(dats[0])}; give each character its own run");

                var costumeModel = obj;
                if (textureFolders.TryGetValue(Path.GetFileName(dat), out var folder))
                    costumeModel = obj.WithTextureFolder(folder);

                var before = Validator.Snapshot(root);
                new SkinBuilder(file, sk) { FighterData = fighterData }.Replace(costumeModel, rig.Positions, rig.Normals, rig.Weights);

                var output = Path.Combine(outDir, Path.GetFileName(dat));
                file.Save(output);

                // read the result back and check the parts netplay depends on
                var saved = (HSD_JOBJ)new HSDRawFile(output).Roots[0].Data;
                var problems = Validator.Check(before, saved);
                if (problems.Count > 0)
                    throw new Exception($"{Path.GetFileName(output)} failed checks:\n  " + string.Join("\n  ", problems));

                Console.WriteLine($"wrote {output} ({new FileInfo(output).Length / 1024} KB), skeleton unchanged");
            }

            return 0;
        }
    }
}
