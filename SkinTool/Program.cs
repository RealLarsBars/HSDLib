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
@"skintool build --model <model.obj|model.zip> --dat <costume.dat> [--dat <costume.dat> ...] --out <folder|out.zip>
               [--fighter <Pl**.dat>] [--bones 41,22,...] [--scale 1.0] [--rotate 0] [--textures <costume.dat>=<folder> ...]
               [--preview <out.png> [--anims <Pl**AJ.dat>]]
skintool inspect <costume.dat>
skintool check <original.dat> <skin.dat>
skintool preview <out.png> <costume.dat> [<costume.dat> ...] [--anims <Pl**.dat> <Pl**AJ.dat>] [--bones]
skintool bonemap <costume.dat>

build    Swaps the model into every costume given, keeping each costume's skeleton.
         The model is fitted and weighted once, against the first costume.
         --fighter reads which mesh slots the game shows, so the model never lands in a hidden one.
         --bones 41,22,... picks the bones to weight to; by default the fighter's bone map, if it has one.
         --textures swaps in PNGs from another folder for one costume (matched by file name).
         A .zip model (OBJ, MTL and textures) is unpacked first; an --out ending in .zip collects the costumes in one zip.
         --preview renders the first costume, in the fighter's real animations with --anims (needs --fighter).
inspect  Lists a costume's joints and meshes.
check    Compares a finished skin with the original costume, as the build does.
preview  Renders each costume in a few test poses, one row per costume.
         --anims uses the fighter's real animations; --bones colours the model by the bones it follows.
bonemap  Shows where the fighter's bone map bones sit, to check the map.";

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
                    case "inspect": return Inspector.Run(args[1], args.Contains("--textures"));
                    case "preview": return Preview.Run(args);
                    case "slots": return Slots.Dump(args);
                    case "bonemap": return BoneMaps.Dump(args[1]);
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

        static (System.Numerics.Vector3[], System.Numerics.Vector3[]) Retarget(Skeleton from, Skeleton to, AutoRig rig, ObjModel obj)
        {
            var move = new System.Numerics.Matrix4x4[from.Joints.Count];
            for (int b = 0; b < move.Length; b++)
                move[b] = from.InverseBind(b) * to.World[b];

            var positions = new System.Numerics.Vector3[rig.Positions.Length];
            for (int i = 0; i < positions.Length; i++)
                foreach (var e in rig.Weights[i])
                    positions[i] += System.Numerics.Vector3.Transform(rig.Positions[i], move[e.Bone]) * e.Weight;

            // normals belong to OBJ corners, not positions: turn each by its corner's position's bones
            var normals = (System.Numerics.Vector3[])rig.Normals.Clone();
            var done = new bool[normals.Length];
            foreach (var t in obj.Triangles)
                foreach (var c in new[] { t.A, t.B, t.C })
                {
                    if (c.Normal < 0 || done[c.Normal]) continue;
                    done[c.Normal] = true;
                    var n = System.Numerics.Vector3.Zero;
                    foreach (var e in rig.Weights[c.Position])
                        n += System.Numerics.Vector3.TransformNormal(rig.Normals[c.Normal], move[e.Bone]) * e.Weight;
                    normals[c.Normal] = n;
                }
            return (positions, normals);
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
            string model = null, outDir = null, fighterDat = null, previewPng = null, animDat = null;
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
                    case "--preview": previewPng = Next(); break;
                    case "--anims": animDat = Next(); break;
                    case "--scale": opt.Scale = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--bones": opt.Bones = Next().Split(',').Select(int.Parse).ToArray(); break;
                    case "--keep-shape": opt.Conform = false; break;
                    case "--leg-band": opt.LegBand = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--cut-legs": opt.LegCut = float.Parse(Next(), CultureInfo.InvariantCulture); break;
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

            // a zip in, a zip out: unpack the model and collect the costumes in a scratch folder
            string scratch = null;
            string zipOut = null;
            if (model.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || outDir.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                scratch = Directory.CreateTempSubdirectory("skintool").FullName;
            try
            {
                if (model.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    model = ModelZip.Extract(model, Path.Combine(scratch, "model"));
                if (outDir.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipOut = outDir;
                    outDir = Path.Combine(scratch, "out");
                }

                BuildCostumes(model, dats, outDir, fighterDat, textureFolders, opt);

                if (previewPng != null)
                {
                    var previewArgs = new List<string> { "preview", previewPng, Path.Combine(outDir, Path.GetFileName(dats[0])) };
                    if (animDat != null && fighterDat != null)
                        previewArgs.AddRange(new[] { "--anims", fighterDat, animDat });
                    Preview.Run(previewArgs.ToArray());
                }

                if (zipOut != null)
                {
                    ModelZip.Pack(outDir, zipOut);
                    Console.WriteLine($"wrote {zipOut}");
                }
            }
            finally
            {
                if (scratch != null)
                    Directory.Delete(scratch, true);
            }

            return 0;
        }

        static void BuildCostumes(string model, List<string> dats, string outDir, string fighterDat, Dictionary<string, string> textureFolders, AutoRig.Options opt)
        {
            Directory.CreateDirectory(outDir);
            var fighterData = fighterDat != null ? Slots.Load(fighterDat) : null;
            var obj = ObjModel.Load(model);
            Console.WriteLine($"model: {obj.Positions.Count} vertices, {obj.Triangles.Count} triangles, {obj.Textures.Count} materials");

            // fit and weight once against the first costume; every costume shares the skeleton
            var first = new HSDRawFile(dats[0]);
            var firstRoot = (HSD_JOBJ)first.Roots[0].Data;
            var skeleton = new Skeleton(firstRoot);
            var fighter = new FighterMesh(firstRoot, skeleton);
            // weight to the bones modders use for this fighter, when there's a map for it
            var map = BoneMaps.Get(BoneMaps.Code(dats[0]));
            if (opt.Bones == null && map != null)
            {
                opt.Bones = map.Values.ToArray();
                opt.Head = map.GetValueOrDefault("face", map.GetValueOrDefault("body", -1));
                opt.Roles = map;
            }
            var rig = AutoRig.Run(obj, fighter, skeleton, opt);
            Console.Write(rig.Report);

            foreach (var dat in dats)
            {
                var file = dat == dats[0] ? first : new HSDRawFile(dat);
                var root = (HSD_JOBJ)file.Roots[0].Data;
                var sk = dat == dats[0] ? skeleton : new Skeleton(root);

                if (!Validator.SameStructure(skeleton, sk))
                    throw new Exception($"{Path.GetFileName(dat)} has a different skeleton from {Path.GetFileName(dats[0])}; give each character its own run");

                // some costumes pose a few bones slightly differently (a longer arm, a bigger hat):
                // move each vertex with its bones so it sits on this costume's skeleton the same way
                var positions = rig.Positions;
                var normals = rig.Normals;
                if (!Validator.SameSkeleton(skeleton, sk))
                    (positions, normals) = Retarget(skeleton, sk, rig, obj);

                var costumeModel = obj;
                if (textureFolders.TryGetValue(Path.GetFileName(dat), out var folder))
                    costumeModel = obj.WithTextureFolder(folder);

                var before = Validator.Snapshot(root);
                new SkinBuilder(file, sk) { FighterData = fighterData }.Replace(costumeModel, positions, normals, rig.Weights);

                var output = Path.Combine(outDir, Path.GetFileName(dat));
                file.Save(output);

                // read the result back and check the parts netplay depends on
                var saved = (HSD_JOBJ)new HSDRawFile(output).Roots[0].Data;
                var problems = Validator.Check(before, saved);
                if (problems.Count > 0)
                    throw new Exception($"{Path.GetFileName(output)} failed checks:\n  " + string.Join("\n  ", problems));

                Console.WriteLine($"wrote {Path.GetFileName(output)} ({new FileInfo(output).Length / 1024} KB), skeleton unchanged");
            }
        }
    }
}
