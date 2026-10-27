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
@"skintool build --model <model.obj/.fbx/.dae|model.zip> --dat <costume.dat> [--dat <costume.dat> ...] --out <folder|out.zip>
               [--fighter <Pl**.dat>] [--bones 41,22,...] [--scale 1.0] [--width 1.0] [--height 1.0] [--rotate 0 | x,y,z] [--textures <costume.dat>=<folder> ...]
               [--legs auto|separate|body|cut] [--arms auto|fighter|body] [--neck auto|none|0.6] [--tail yes|no] [--attach yes|no] [--leg-band 0.3] [--keep-shape]
               [--pick <model in the zip or folder>] [--weapon <name>=<model|zip> [--weapon-pick <name>=<model>] [--weapon-flip <name>] ...]
               [--preview <out.png> [--anims <Pl**AJ.dat>]] [--overlay <out.png> [--fit-only]] [--portraits <folder>]
skintool models <zip|folder> --out <folder>
skintool weapons <costume.dat>
skintool stretch <costume.dat> <Pl**.dat> <Pl**AJ.dat>
skintool portrait <out.png> <costume.dat> <Pl**.dat> <Pl**AJ.dat> [--anim Wait1] [--at 0] [--yaw -30]
skintool inspect <costume.dat>
skintool check <original.dat> <skin.dat>
skintool preview <out.png> <costume.dat> [<costume.dat> ...] [--anims <Pl**.dat> <Pl**AJ.dat> [--poses AttackAirB@0.25,...]] [--bones]
skintool bonemap <costume.dat>

build    Swaps the model into every costume given, keeping each costume's skeleton.
         The model is fitted and weighted once, against the first costume.
         --fighter reads which mesh slots the game shows, so the model never lands in a hidden one.
         --bones 41,22,... picks the bones to weight to; by default the fighter's bone map, if it has one.
         --textures swaps in PNGs from another folder for one costume (matched by file name).
         A .zip model (OBJ, MTL and textures) is unpacked first; an --out ending in .zip collects the costumes in one zip.
         --width and --height stretch the fitted model sideways or upwards only (1 = as fitted).
         --legs says how the model's legs work: separate legs, a body that works as legs (the bottom
         bends with the fighter's legs), or a bottom cut into two legs; auto picks from its shape.
         --arms fighter always moves its arms onto the fighter's, body keeps them mostly with the body.
         --neck is where the head starts, as a share of the model's height: everything above follows the
         head bone alone, so a wide head or face moves as one piece. auto finds a neck, none turns it off.
         --tail yes says the model has a tail: what sticks out behind it follows the fighter's tail bones
         (Fox, Falco, Pikachu, Pichu, Mewtwo, Yoshi); without it, everything behind follows the body.
         --attach (yes by default) keeps separate pieces that touch (a sleeve on a shirt) joined in every
         pose; no lets loose pieces swing on their own.
         The same rules can come with the model in a skin.txt next to the OBJ (""legs = body"").
         --overlay draws the fitted model over the first costume, to check its size; with --fit-only
         that's all it does (no costumes are written, --out isn't needed).
         --pick chooses the model when a zip or folder holds more than one.
         --weapon swaps one of the fighter's weapons (pan, sword, hammer; see weapons) for another model,
         fitted over the original; --weapon-flip turns it end for end. Weapons not given keep the costume's own.
         --preview renders the first costume, in the fighter's real animations with --anims (needs --fighter).
         --portraits draws a character select portrait of each costume (136x188, <costume>_csp.png) into
         the folder and next to the costumes (needs --fighter and --anims).
models   Lists the models in a zip or folder and draws each one (<folder>/<n>.png), to pick from.
weapons  Lists the weapons a fighter holds, by the names --weapon takes.
stretch  Poses a finished costume in the fighter's moves and warns about parts that get pulled apart
         (build does the same whenever it has --anims and --fighter).
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
                    case "models": return Models(args.Skip(1).ToArray());
                    case "weapons": return ListWeapons(args[1]);
                    case "stretch": return Stretch(args);
                    case "portrait": return Portrait(args);
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

        static float Stretch(string name, string value)
        {
            float v = float.Parse(value, CultureInfo.InvariantCulture);
            return v >= 0.5f && v <= 2f ? v : throw new Exception($"{name} must be between 0.5 and 2, not {value}");
        }

        static bool YesNo(string name, string value) => value.ToLowerInvariant() switch
        {
            "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => throw new Exception($"{name} is yes or no, not {value}"),
        };

        // degrees to turn the model: one number turns it round (facing), x,y,z also tips it over
        static void Rotate(AutoRig.Options opt, string value)
        {
            var parts = value.Split(',').Select(v => float.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
            if (parts.Length == 1)
                opt.RotateY = parts[0];
            else if (parts.Length == 3)
                (opt.RotateX, opt.RotateY, opt.RotateZ) = (parts[0], parts[1], parts[2]);
            else
                throw new Exception($"rotate takes one angle or x,y,z, not {value}");
        }

        // where the head starts: auto (find the neck), none, or a share of the model's height (0.2 to 0.95)
        static float Neck(string value)
        {
            value = value.Trim().ToLowerInvariant();
            if (value == "auto") return float.NaN;
            if (value == "none") return 0;
            float v = float.Parse(value, CultureInfo.InvariantCulture);
            return v >= 0.2f && v <= 0.95f ? v : throw new Exception($"neck must be auto, none or between 0.2 and 0.95, not {value}");
        }

        static string Rule(string name, string value, params string[] allowed)
        {
            value = value.Trim().ToLowerInvariant();
            return allowed.Contains(value) ? value : throw new Exception($"{name} must be one of {string.Join(", ", allowed)}, not {value}");
        }

        /// <summary>
        /// Rules that come with a model, in a skin.txt next to its OBJ, one "name = value" per line:
        /// legs, arms, neck, scale, rotate, leg-band. Options given on the command line win.
        /// </summary>
        static void ReadRules(string model, AutoRig.Options opt, HashSet<string> given)
        {
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(model)), "skin.txt");
            if (!File.Exists(path))
                return;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Split('#')[0].Trim();
                if (line.Length == 0)
                    continue;
                var kv = line.Split('=', 2);
                if (kv.Length != 2)
                    throw new Exception($"skin.txt: \"{line}\" should look like name = value");
                string name = kv[0].Trim().ToLowerInvariant(), value = kv[1].Trim();
                if (given.Contains("--" + name))
                    continue;
                switch (name)
                {
                    case "legs": opt.Legs = Rule(name, value, "auto", "separate", "body", "cut"); break;
                    case "arms": opt.Arms = Rule(name, value, "auto", "fighter", "body"); break;
                    case "scale": opt.Scale = float.Parse(value, CultureInfo.InvariantCulture); break;
                    case "width": opt.Width = Stretch(name, value); break;
                    case "height": opt.Height = Stretch(name, value); break;
                    case "rotate": Rotate(opt, value); break;
                    case "leg-band": opt.LegBand = float.Parse(value, CultureInfo.InvariantCulture); break;
                    case "neck": opt.Neck = Neck(value); break;
                    case "tail": opt.HasTail = YesNo("tail", value); break;
                    case "attach": opt.Attach = YesNo("attach", value); break;
                    default: throw new Exception($"skin.txt: unknown rule {name} (legs, arms, neck, tail, attach, scale, width, height, rotate, leg-band)");
                }
                Console.WriteLine($"rule from skin.txt: {name} = {value}");
            }
        }

        static int ListWeapons(string costume)
        {
            var list = Weapons.For(BoneMaps.Code(costume));
            foreach (var w in list)
                Console.WriteLine($"weapon {w.Key} {w.Name}");
            if (list.Length == 0)
                Console.WriteLine("no weapons");
            return 0;
        }

        /// <summary>Lists every model in a zip or folder with a picture of each, so one can be picked.</summary>
        static int Models(string[] args)
        {
            int o = Array.IndexOf(args, "--out");
            if (args.Length < 3 || o < 0 || o + 1 >= args.Length)
            {
                Console.WriteLine(Usage);
                return 1;
            }
            string input = args.Where((_, i) => i != o && i != o + 1).First(), outDir = args[o + 1];
            Directory.CreateDirectory(outDir);
            string scratch = null;
            try
            {
                string folder = input;
                if (input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    scratch = Directory.CreateTempSubdirectory("skintool").FullName;
                    folder = scratch;
                    ModelZip.Unpack(input, folder);
                }
                else if (!Directory.Exists(folder))
                    folder = Path.GetDirectoryName(Path.GetFullPath(folder));

                var models = ModelZip.ModelsIn(folder);
                for (int i = 0; i < models.Count; i++)
                {
                    var name = Path.GetRelativePath(folder, models[i]).Replace('\\', '/');
                    try
                    {
                        var obj = ObjModel.Load(models[i]);
                        Preview.Thumbnail(obj, Path.Combine(outDir, $"{i}.png"));
                        Console.WriteLine($"model {i} {obj.Triangles.Count} {name}");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"model {i} unreadable {name}: {e.Message}");
                    }
                }
                if (models.Count == 0)
                    Console.WriteLine("no models");
            }
            finally
            {
                if (scratch != null)
                    Directory.Delete(scratch, true);
            }
            return 0;
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
            string model = null, outDir = null, fighterDat = null, previewPng = null, animDat = null, pick = null, overlayPng = null, portraitDir = null;
            bool fitOnly = false;
            var weaponFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var weaponPicks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var weaponFlips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    case "--legs": opt.Legs = Rule("legs", Next(), "auto", "separate", "body", "cut"); break;
                    case "--arms": opt.Arms = Rule("arms", Next(), "auto", "fighter", "body"); break;
                    case "--neck": opt.Neck = Neck(Next()); break;
                    case "--tail": opt.HasTail = YesNo("--tail", Next()); break;
                    case "--attach": opt.Attach = YesNo("--attach", Next()); break;
                    case "--leg-band": opt.LegBand = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--cut-legs": opt.LegCut = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--width": opt.Width = Stretch("width", Next()); break;
                    case "--height": opt.Height = Stretch("height", Next()); break;
                    case "--rotate": Rotate(opt, Next()); break;
                    case "--pick": pick = Next(); break;
                    case "--overlay": overlayPng = Next(); break;
                    case "--fit-only": fitOnly = true; break;
                    case "--portraits": portraitDir = Next(); break;
                    case "--weapon":
                        var w = Next().Split('=', 2);
                        if (w.Length != 2) throw new Exception("--weapon takes name=model");
                        weaponFiles[w[0]] = w[1];
                        break;
                    case "--weapon-pick":
                        var wp = Next().Split('=', 2);
                        if (wp.Length != 2) throw new Exception("--weapon-pick takes name=model");
                        weaponPicks[wp[0]] = wp[1];
                        break;
                    case "--weapon-flip": weaponFlips.Add(Next()); break;
                    case "--textures":
                        var kv = Next().Split('=', 2);
                        textureFolders[Path.GetFileName(kv[0])] = kv[1];
                        break;
                    default: throw new Exception($"unknown option {args[i]}");
                }
            }

            if (fitOnly)
                outDir ??= Path.Combine(Path.GetTempPath(), "skintool-unused");
            if (model == null || dats.Count == 0 || outDir == null)
            {
                Console.WriteLine(Usage);
                return 1;
            }

            // a zip in, a zip out: unpack the model and collect the costumes in a scratch folder
            string scratch = null;
            string zipOut = null;
            if (model.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || outDir.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || weaponFiles.Values.Any(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                scratch = Directory.CreateTempSubdirectory("skintool").FullName;
            try
            {
                model = ModelZip.Find(model, Path.Combine(scratch ?? "", "model"), pick);

                var code = BoneMaps.Code(dats[0]);
                var uploads = new Dictionary<string, Weapons.Upload>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, file) in weaponFiles)
                {
                    var weapon = Weapons.For(code).FirstOrDefault(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new Exception($"{Path.GetFileName(dats[0])} has no weapon called {name} (it has: {string.Join(", ", Weapons.For(code).Select(x => x.Key).DefaultIfEmpty("none"))})");
                    var path = ModelZip.Find(file, Path.Combine(scratch ?? "", "weapon-" + weapon.Key), weaponPicks.GetValueOrDefault(name));
                    var obj = ObjModel.Load(path);
                    if (obj.Triangles.Count == 0)
                        throw new Exception($"the {weapon.Name.ToLowerInvariant()} model has no triangles");
                    uploads[weapon.Key] = new Weapons.Upload { Model = obj, Flip = weaponFlips.Contains(name) };
                    Console.WriteLine($"weapon {weapon.Key}: {Path.GetFileName(path)}, {obj.Triangles.Count} triangles");
                }
                if (fighterDat == null && uploads.Count > 0)
                    throw new Exception("--weapon needs --fighter, to find the weapon's slots");
                if (outDir.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipOut = outDir;
                    outDir = Path.Combine(scratch, "out");
                }

                ReadRules(model, opt, args.Where(a => a.StartsWith("--")).ToHashSet());
                BuildCostumes(model, dats, outDir, fighterDat, textureFolders, opt, uploads, overlayPng, fitOnly);

                if (previewPng != null && !fitOnly)
                {
                    var previewArgs = new List<string> { "preview", previewPng, Path.Combine(outDir, Path.GetFileName(dats[0])) };
                    if (animDat != null && fighterDat != null)
                        previewArgs.AddRange(new[] { "--anims", fighterDat, animDat });
                    if (fighterDat != null)
                        previewArgs.AddRange(new[] { "--fighter", fighterDat });
                    Preview.Run(previewArgs.ToArray());
                }

                // pose the skin in the fighter's moves and warn about parts that tear or smear
                if (animDat != null && fighterDat != null && !fitOnly)
                {
                    var poses = Animations.Load(fighterDat, animDat, Animations.TestPoses);
                    var warning = Preview.StretchCheck(Path.Combine(outDir, Path.GetFileName(dats[0])), Slots.Load(fighterDat), poses);
                    if (warning != null)
                        Console.WriteLine($"warning: {warning}");
                }

                // a character select portrait of each costume, next to it (PlFxNr_csp.png) and in --portraits
                if (portraitDir != null && animDat != null && fighterDat != null && !fitOnly)
                {
                    Directory.CreateDirectory(portraitDir);
                    var pose = PortraitPose(fighterDat, animDat);
                    var data = Slots.Load(fighterDat);
                    foreach (var dat in dats)
                    {
                        var name = Path.GetFileNameWithoutExtension(dat) + "_csp.png";
                        Preview.Portrait(Path.Combine(outDir, Path.GetFileName(dat)), data, pose, Path.Combine(portraitDir, name));
                        File.Copy(Path.Combine(portraitDir, name), Path.Combine(outDir, name), true);
                        Console.WriteLine($"portrait {name}");
                    }
                }

                if (zipOut != null && !fitOnly)
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

        /// <summary>skintool stretch Pl**Nr.dat Pl**.dat Pl**AJ.dat: the stretch check on a finished costume</summary>
        static int Stretch(string[] args)
        {
            var poses = Animations.Load(args[2], args[3], Animations.TestPoses);
            var warning = Preview.StretchCheck(args[1], Slots.Load(args[2]), poses);
            Console.WriteLine(warning != null ? $"warning: {warning}" : "no bad stretching");
            return 0;
        }

        // the pose character select portraits are drawn in, unless one is given: standing (Pikachu and the
        // Ice Climbers call it Wait)
        static readonly (string Name, float At)[] PortraitPoses = { ("Wait1", 0f), ("Wait", 0f) };

        static Preview.Pose PortraitPose(string fighterDat, string animDat) =>
            PortraitPoses.Select(p => Animations.Load(fighterDat, animDat, new[] { p }).FirstOrDefault()).FirstOrDefault(p => p != null)
            ?? new Preview.Pose { Name = "bind" };

        /// <summary>skintool portrait out.png costume.dat Pl**.dat Pl**AJ.dat [--anim Wait1] [--at 0] [--yaw -30]</summary>
        static int Portrait(string[] args)
        {
            string name = null;
            float at = 0, yaw = -30;
            for (int i = 5; i + 1 < args.Length; i += 2)
                switch (args[i])
                {
                    case "--anim": name = args[i + 1]; break;
                    case "--at": at = float.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                    case "--yaw": yaw = float.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
                }
            var pose = name == null ? PortraitPose(args[3], args[4])
                : Animations.Load(args[3], args[4], new[] { (name, at) }).FirstOrDefault() ?? throw new Exception($"no animation called {name}");
            Preview.Portrait(args[2], Slots.Load(args[3]), pose, args[1], yaw);
            Console.WriteLine($"wrote {args[1]}");
            return 0;
        }

        static void BuildCostumes(string model, List<string> dats, string outDir, string fighterDat, Dictionary<string, string> textureFolders, AutoRig.Options opt, Dictionary<string, Weapons.Upload> uploads, string overlayPng = null, bool fitOnly = false)
        {
            if (!fitOnly)
                Directory.CreateDirectory(outDir);
            var fighterData = fighterDat != null ? Slots.Load(fighterDat) : null;
            var obj = ObjModel.Load(model);
            int holes = obj.CloseSmallHoles();
            if (holes > 0)
                Console.WriteLine($"closed {holes} small openings (open claw or tooth bases)");
            Console.WriteLine($"model: {obj.Positions.Count} vertices, {obj.Triangles.Count} triangles, {obj.Textures.Count} materials");

            // fit and weight once against the first costume; every costume shares the skeleton
            var first = new HSDRawFile(dats[0]);
            var firstRoot = (HSD_JOBJ)first.Roots[0].Data;
            var skeleton = new Skeleton(firstRoot);
            var fighter = new FighterMesh(firstRoot, skeleton);
            // weight to the bones modders use for this fighter, when there's a map for it
            var map = BoneMaps.Get(BoneMaps.Code(dats[0]), firstRoot);
            if (opt.Bones == null && map != null)
            {
                opt.Bones = map.Values.Distinct().ToArray();
                opt.Head = map.GetValueOrDefault("face", map.GetValueOrDefault("body", -1));
                opt.Roles = map;
            }
            opt.StretchyFeet = BoneMaps.StretchyFeet.Contains(BoneMaps.Code(dats[0]) ?? "");
            opt.Dress = BoneMaps.Dress.Contains(BoneMaps.Code(dats[0]) ?? "");
            if (opt.HasTail)
            {
                var weighted = BoneMaps.Weighted(firstRoot);
                var tail = BoneMaps.Tails.GetValueOrDefault(BoneMaps.Code(dats[0]) ?? "", Array.Empty<int>()).Where(weighted.Contains).ToArray();
                if (tail.Length == 0)
                    Console.WriteLine("tail: this fighter has no tail to move it with; it follows the body");
                else
                {
                    opt.TailBones = tail;
                    if (opt.Bones != null)
                        opt.Bones = opt.Bones.Concat(tail).Distinct().ToArray();
                }
            }
            var rig = AutoRig.Run(obj, fighter, skeleton, opt);
            Console.Write(rig.Report);
            if (overlayPng != null)
                Preview.Overlay(dats[0], fighterData, obj, rig.Positions, overlayPng, rig.NeckY);
            if (fitOnly)
                return;

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
                var builder = new SkinBuilder(file, sk) { FighterData = fighterData };
                if (fighterData != null)
                {
                    var code = BoneMaps.Code(dat);
                    builder.WeaponSlots = Weapons.AllSlots(fighterData, code);
                    builder.WeaponParts = Weapons.Build(root, sk, fighterData, code, uploads);
                }
                builder.Replace(costumeModel, positions, normals, rig.Weights);

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
