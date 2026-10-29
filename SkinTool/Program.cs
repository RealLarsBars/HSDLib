using HSDRaw;
using HSDRaw.Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SkinTool
{
    static class Program
    {
        const string Usage =
@"skintool build --model <model.obj/.fbx/.dae|model.zip> --dat <costume.dat> [--dat <costume.dat> ...] --out <folder|out.zip>
               [--fighter <Pl**.dat>] [--bones 41,22,...] [--scale 1.0] [--width 1.0] [--height 1.0] [--rotate 0 | x,y,z] [--move forward[,up]] [--textures <costume.dat>=<folder> ...]
               [--legs auto|separate|body|cut|dress] [--clothes outside|legs] [--arms auto|fighter|body] [--arm-angle auto|40] [--neck auto|none|0.6 [--neck-tilt 0]] [--tail yes|no] [--attach yes|no] [--style normal|animelee] [--eyes <key>] [--leg-band 0.3] [--keep-shape]
               [--pick <model in the zip or folder>] [--weapon <name>=<model|zip> [--weapon-pick <name>=<model>] [--weapon-flip <name>] ...]
               [--preview <out.png> [--anims <Pl**AJ.dat>]] [--overlay <out.png> [--fit-only]] [--portraits <folder>]
skintool models <zip|folder> --out <folder>
skintool weapons <costume.dat>
skintool stretch <costume.dat> <Pl**.dat> <Pl**AJ.dat>
skintool compare <out.png> <old.dat> <new.dat> <Pl**.dat> <Pl**AJ.dat> [--original <costume.dat>]
skintool portrait <out.png> <costume.dat> <Pl**.dat> <Pl**AJ.dat> [--anim Wait1] [--at 0] [--yaw -30]
skintool inspect <costume.dat>
skintool check <original.dat> <skin.dat>
skintool export <out.obj> <skin.dat> [--fighter <Pl**.dat>]
skintool animelee <skin.dat|skins.zip|folder> --out <out.dat|out.zip|folder>
skintool preview <out.png> <costume.dat> [<costume.dat> ...] [--anims <Pl**.dat> <Pl**AJ.dat> [--poses AttackAirB@0.25,...]] [--bones]
skintool bonemap <costume.dat>
skintool weights <costume.dat> [<Pl**.dat>]
skintool floor <costume.dat> <Pl**.dat> <Pl**AJ.dat>

build    Swaps the model into every costume given, keeping each costume's skeleton.
         The model is fitted and weighted once, against the first costume.
         --fighter reads which mesh slots the game shows, so the model never lands in a hidden one.
         --bones 41,22,... picks the bones to weight to; by default the fighter's bone map, if it has one.
         --textures swaps in PNGs from another folder for one costume (matched by file name).
         A .zip model (OBJ, MTL and textures) is unpacked first; an --out ending in .zip collects the costumes in one zip.
         --width and --height stretch the fitted model sideways or upwards only (1 = as fitted).
         --move slides the fitted model forward (or back, negative) and up or down, as shares of the
         fighter's height (--move -0.1 puts it a tenth of the fighter's height further back). Forward goes up to
         0.5 either way, up up to 1 either way; it says when the model ends up floating or in the floor (a ""position:"" line).
         --legs says how the model's legs work: separate legs, a body that works as legs (the bottom
         bends with the fighter's legs), or a bottom cut into two legs; auto picks from its shape.
         --clothes is for clothes found hanging across between the legs (a coat, apron or dress with legs
         under it): outside (the default) keeps them one sheet over the legs, legs splits them to follow
         each leg like trousers.
         --arms fighter always moves its arms onto the fighter's, body keeps them mostly with the body.
         --arm-angle turns the model's arms at the shoulder before weighting, to point that many degrees
         below level (negative: above; -30 to 80). auto lifts arms that hang well below the fighter's
         (an A-pose on a T-pose fighter) to the fighter's angle; the report says the angle found (""arms: ..."").
         --neck is where the head starts, as a share of the model's height: everything above follows the
         head bone alone, so a wide head or face moves as one piece. auto finds a neck, none turns it off.
         The line found leans with a head held tipped forward or back; --neck-tilt leans one set by hand,
         in degrees (positive: lower at the front, for a head tipped forward; -45 to 45).
         --tail yes says the model has a tail: what sticks out behind it follows the fighter's tail bones
         (Fox, Falco, Pikachu, Pichu, Mewtwo, Yoshi); without it, everything behind follows the body.
         --attach (yes by default) keeps separate pieces that touch (a sleeve on a shirt) joined in every
         pose; no lets loose pieces swing on their own.
         --style animelee gives the skin the Animelee look: a black outline, flat lighting and flat colours.
         --eyes picks the eyes when the model comes with more than one state of them (open and closed eye
         files beside an Odyssey rip, a numbered set of eye textures, eye meshes per state); models lists
         the keys. Left out, the skin gets the open eyes, or the texture the model's materials name.
         The same rules can come with the model in a skin.txt next to the OBJ (""legs = body"").
         --overlay draws the fitted model over the first costume, to check its size; with --fit-only
         that's all it does (no costumes are written, --out isn't needed).
         --pick chooses the model when a zip or folder holds more than one.
         --weapon swaps one of the fighter's weapons (pan, sword, hammer; see weapons) for another model,
         fitted over the original; --weapon-flip turns it end for end. Weapons not given keep the costume's own.
         --preview renders the first costume, in the fighter's real animations with --anims (needs --fighter).
         --portraits draws a character select portrait of each costume (136x188, <costume>_csp.png) into
         the folder and next to the costumes (needs --fighter and --anims).
models   Lists the models in a zip or folder and draws each one (<folder>/<n>.png), to pick from. A model
         with more than one state of eyes gets a line per state (""eyes <n> <key> default|choice <label>"")
         and a close-up of each (<folder>/<n>-eyes-<key>.png).
weapons  Lists the weapons a fighter holds, by the names --weapon takes.
stretch  Poses a finished costume in the fighter's moves and warns about parts that get pulled apart
         (build does the same whenever it has --anims and --fighter).
compare  Draws two builds of a skin (and the original costume, with --original) in the fighter's test
         poses, one row each, and says how much of the picture changed and how the checks moved
         (stretch, thin, floor), to see what a change to skintool did to a skin.
inspect  Lists a costume's joints and meshes.
check    Compares a finished skin with the original costume, as the build does.
preview  Renders each costume in a few test poses, one row per costume.
export   Writes a finished skin's model back out as an OBJ, to build it again.
         --anims uses the fighter's real animations; --bones colours the model by the bones it follows.
bonemap  Shows where the fighter's bone map bones sit, to check the map.
weights  How much of a costume's skin follows each bone (to compare a skin with a hand-made one).
floor    How far below the floor a costume goes in the fighter's low moves.";

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
                    case "export": return Preview.Export(args);
                    case "slots": return Slots.Dump(args);
                    case "bonemap": return BoneMaps.Dump(args[1]);
                    case "floor":
                        foreach (var (pose, low, bone) in Preview.FloorDepth(args[1], Slots.Load(args[2]), Animations.Load(args[2], args[3], Animations.FloorPoses)))
                            Console.WriteLine(FormattableString.Invariant($"{pose}: {low:0.0} (bone {bone})"));
                        return 0;
                    case "weights":
                        foreach (var kv in Preview.BoneShares(args[1], args.Length > 2 ? Slots.Load(args[2]) : null).OrderBy(kv => kv.Key))
                            Console.WriteLine(FormattableString.Invariant($"bone {kv.Key}: {kv.Value:P1}"));
                        return 0;
                    case "check": return Check(args[1], args[2]);
                    case "animelee": return AnimeleeFiles(args);
                    case "models": return Models(args.Skip(1).ToArray());
                    case "weapons": return ListWeapons(args[1]);
                    case "stretch": return Stretch(args);
                    case "compare": return Compare(args);
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

        // forward[,up], shares of the fighter's height
        static void Move(AutoRig.Options opt, string value)
        {
            var parts = value.Split(',').Select(v => float.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
            if (parts.Length < 1 || parts.Length > 2 || MathF.Abs(parts[0]) > 0.5f || (parts.Length > 1 && MathF.Abs(parts[1]) > 1))
                throw new Exception($"move takes forward or forward,up: forward between -0.5 and 0.5, up between -1 and 1, not {value}");
            opt.Forward = parts[0];
            opt.Up = parts.Length > 1 ? parts[1] : 0;
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

        // the neck line's lean in degrees, positive down at the front (-45 to 45)
        static float NeckTilt(string value)
        {
            float v = float.Parse(value.Trim(), CultureInfo.InvariantCulture);
            return v >= -45 && v <= 45 ? v : throw new Exception($"neck-tilt must be between -45 and 45 degrees, not {value}");
        }

        // the angle the model's arms are turned to at the shoulder, degrees below level (NaN: auto)
        static float ArmAngle(string value)
        {
            value = value.Trim();
            if (value == "auto") return float.NaN;
            float v = float.Parse(value, CultureInfo.InvariantCulture);
            return v >= -30 && v <= 80 ? v : throw new Exception($"arm-angle must be auto or between -30 and 80 degrees, not {value}");
        }

        static string Rule(string name, string value, params string[] allowed)
        {
            value = value.Trim().ToLowerInvariant();
            return allowed.Contains(value) ? value : throw new Exception($"{name} must be one of {string.Join(", ", allowed)}, not {value}");
        }

        /// <summary>
        /// Rules that come with a model, in a skin.txt next to its OBJ, one "name = value" per line:
        /// legs, arms, neck, style, scale, rotate, move, leg-band. Options given on the command line win.
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
                    case "legs": opt.Legs = Rule(name, value, "auto", "separate", "body", "cut", "dress"); break;
                    case "clothes": opt.Clothes = Rule(name, value, "outside", "legs"); break;
                    case "arms": opt.Arms = Rule(name, value, "auto", "fighter", "body"); break;
                    case "arm-angle": opt.ArmAngle = ArmAngle(value); break;
                    case "scale": opt.Scale = float.Parse(value, CultureInfo.InvariantCulture); break;
                    case "width": opt.Width = Stretch(name, value); break;
                    case "height": opt.Height = Stretch(name, value); break;
                    case "rotate": Rotate(opt, value); break;
                    case "move": Move(opt, value); break;
                    case "style": opt.Style = Rule(name, value, "normal", "animelee"); break;
                    case "eyes": opt.Eyes = value; break;
                    case "leg-band": opt.LegBand = float.Parse(value, CultureInfo.InvariantCulture); break;
                    case "neck": opt.Neck = Neck(value); break;
                    case "neck-tilt": opt.NeckTilt = NeckTilt(value); break;
                    case "tail": opt.HasTail = YesNo("tail", value); break;
                    case "attach": opt.Attach = YesNo("attach", value); break;
                    default: throw new Exception($"skin.txt: unknown rule {name} (legs, arms, arm-angle, neck, neck-tilt, tail, attach, style, eyes, scale, width, height, rotate, move, leg-band)");
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
                        var raw = ObjModel.LoadRaw(models[i]);
                        var eyes = Eyes.Find(raw, models[i]);
                        var obj = raw.Copy();
                        Eyes.Apply(obj, models[i], null, eyes);
                        Preview.Thumbnail(obj, Path.Combine(outDir, $"{i}.png"));
                        Console.WriteLine($"model {i} {obj.Triangles.Count} {name}");
                        if (eyes != null && Eyes.Pictures(raw, models[i], eyes, Path.Combine(outDir, i.ToString())))
                        {
                            foreach (var c in eyes.Choices)
                                Console.WriteLine($"eyes {i} {c.Key} {(c.Key == eyes.Default ? "default" : "choice")} {c.Label}");
                        }
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

        /// <summary>
        /// skintool animelee in.dat|in.zip|folder --out out.dat|out.zip|folder: gives finished costume files the
        /// Animelee look. A zip or folder keeps its layout; files that aren't costumes are copied as they are.
        /// </summary>
        static int AnimeleeFiles(string[] args)
        {
            string input = args.Length > 1 ? args[1] : throw new Exception("animelee needs a .dat, .zip or folder");
            int o = Array.IndexOf(args, "--out");
            string output = o > 0 && o + 1 < args.Length ? args[o + 1] : throw new Exception("animelee needs --out");

            bool oneDat = input.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && File.Exists(input);
            if (oneDat && output.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
                return AnimeleeOne(input, output) ? 0 : 1;
            }

            // a zip unpacks into a folder of its own first (a single .dat goes in one alone)
            string folder = input, temp = null;
            if (File.Exists(input))
            {
                temp = folder = Path.Combine(Path.GetTempPath(), "animelee-" + Guid.NewGuid().ToString("N"));
                if (oneDat)
                {
                    Directory.CreateDirectory(folder);
                    File.Copy(input, Path.Combine(folder, Path.GetFileName(input)));
                }
                else
                    System.IO.Compression.ZipFile.ExtractToDirectory(input, folder);
            }
            bool zipOut = output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            string outFolder = zipOut ? Path.Combine(Path.GetTempPath(), "animelee-out-" + Guid.NewGuid().ToString("N")) : output;
            try
            {
                int converted = 0;
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f))
                {
                    var rel = Path.GetRelativePath(folder, file);
                    if (rel.StartsWith("__MACOSX") || Path.GetFileName(rel).StartsWith("._"))
                        continue;
                    var dest = Path.Combine(outFolder, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (!rel.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) || !AnimeleeOne(file, dest, rel))
                        File.Copy(file, dest, true);
                    else
                        converted++;
                }
                if (converted == 0)
                    throw new Exception("no costume .dat files found");
                if (zipOut)
                {
                    if (File.Exists(output))
                        File.Delete(output);
                    System.IO.Compression.ZipFile.CreateFromDirectory(outFolder, output);
                }
                Console.WriteLine($"converted {converted} costumes");
                return 0;
            }
            finally
            {
                if (temp != null) Directory.Delete(temp, true);
                if (zipOut && Directory.Exists(outFolder)) Directory.Delete(outFolder, true);
            }
        }

        static bool AnimeleeOne(string input, string output, string name = null)
        {
            name ??= Path.GetFileName(input);
            HSDRawFile file;
            try
            {
                file = new HSDRawFile(input);
            }
            catch
            {
                Console.WriteLine($"{name}: not a Melee file, copied as it is");
                return false;
            }
            if (file.Roots.Count == 0 || file.Roots[0].Data is not HSD_JOBJ root || root.TreeList.All(j => j.Dobj == null))
            {
                Console.WriteLine($"{name}: not a costume, copied as it is");
                return false;
            }
            var before = Validator.Snapshot(root);
            // a hand-made costume may already break a rule this tool's own skins keep (the game's own rigid
            // meshes have no weights): only what the conversion changes counts
            var already = Validator.Check(before, root).ToHashSet();
            foreach (var line in AnimeleeDat.Convert(file))
                Console.WriteLine($"{name}: {line.TrimStart()}");
            file.Save(output);
            var problems = Validator.Check(before, (HSD_JOBJ)new HSDRawFile(output).Roots[0].Data).Where(p => !already.Contains(p)).ToList();
            if (problems.Count > 0)
                throw new Exception($"{name} failed checks:\n  " + string.Join("\n  ", problems));
            return true;
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
                    case "--legs": opt.Legs = Rule("legs", Next(), "auto", "separate", "body", "cut", "dress"); break;
                    case "--clothes": opt.Clothes = Rule("clothes", Next(), "outside", "legs"); break;
                    case "--arms": opt.Arms = Rule("arms", Next(), "auto", "fighter", "body"); break;
                    case "--arm-angle": opt.ArmAngle = ArmAngle(Next()); break;
                    case "--neck": opt.Neck = Neck(Next()); break;
                    case "--neck-tilt": opt.NeckTilt = NeckTilt(Next()); break;
                    case "--tail": opt.HasTail = YesNo("--tail", Next()); break;
                    case "--attach": opt.Attach = YesNo("--attach", Next()); break;
                    case "--leg-band": opt.LegBand = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--cut-legs": opt.LegCut = float.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--width": opt.Width = Stretch("width", Next()); break;
                    case "--height": opt.Height = Stretch("height", Next()); break;
                    case "--rotate": Rotate(opt, Next()); break;
                    case "--move": Move(opt, Next()); break;
                    case "--style": opt.Style = Rule("style", Next(), "normal", "animelee"); break;
                    case "--eyes": opt.Eyes = Next(); break;
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
                BuildCostumes(model, dats, outDir, fighterDat, textureFolders, opt, uploads, overlayPng, fitOnly, animDat);

                // the preview, the stretch check and the portraits only read the costumes, so they're drawn
                // side by side; what they say prints in the same order as before
                var said = new List<string>();
                var drawPreview = Task.Run(() =>
                {
                    if (previewPng == null || fitOnly)
                        return;
                    var previewArgs = new List<string> { "preview", previewPng, Path.Combine(outDir, Path.GetFileName(dats[0])) };
                    if (animDat != null && fighterDat != null)
                        previewArgs.AddRange(new[] { "--anims", fighterDat, animDat });
                    if (fighterDat != null)
                        previewArgs.AddRange(new[] { "--fighter", fighterDat });
                    Preview.Run(previewArgs.ToArray(), said.Add);
                });

                // pose the skin in the fighter's moves and warn about parts that tear or smear
                var stretchSaid = new List<string>();
                var checkStretch = Task.Run(() =>
                {
                    if (animDat == null || fighterDat == null || fitOnly)
                        return null;
                    var poses = Animations.Load(fighterDat, animDat, Animations.TestPoses);
                    return Preview.StretchCheck(Path.Combine(outDir, Path.GetFileName(dats[0])), Slots.Load(fighterDat), poses, stretchSaid.Add);
                });

                // a character select portrait of each costume, next to it (PlFxNr_csp.png) and in --portraits
                var portraits = new List<string>();
                try
                {
                    if (portraitDir != null && animDat != null && fighterDat != null && !fitOnly)
                    {
                        Directory.CreateDirectory(portraitDir);
                        var pose = PortraitPose(fighterDat, animDat);
                        var data = Slots.Load(fighterDat);
                        portraits = dats.Select(dat => Path.GetFileNameWithoutExtension(dat) + "_csp.png").ToList();
                        Each(dats.Count, i =>
                        {
                            Preview.Portrait(Path.Combine(outDir, Path.GetFileName(dats[i])), data, pose, Path.Combine(portraitDir, portraits[i]));
                            File.Copy(Path.Combine(portraitDir, portraits[i]), Path.Combine(outDir, portraits[i]), true);
                        });
                    }
                }
                finally
                {
                    // the scratch folder goes once this returns, so let the others finish first (their own errors come below)
                    try { Task.WaitAll(drawPreview, checkStretch); } catch (AggregateException) { }
                }

                drawPreview.GetAwaiter().GetResult();
                foreach (var line in said)
                    Console.WriteLine(line);
                var warning = checkStretch.GetAwaiter().GetResult();
                foreach (var line in stretchSaid)
                    Console.WriteLine(line);
                if (warning != null)
                    Console.WriteLine($"warning: {warning}");
                foreach (var name in portraits)
                    Console.WriteLine($"portrait {name}");

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
            var all = Animations.Load(args[2], args[3], Animations.AllPoses(args[2], 0, 0.25f, 0.5f, 0.75f));
            var (share, pose, bone) = Preview.ThinCheck(args[1], Slots.Load(args[2]), all);
            Console.WriteLine(FormattableString.Invariant($"thin check: {share:P1} of the skin's points squashed to under {Preview.ThinLimit:0%} of their depth in some of {all.Count} poses") + (pose != null ? $" (most in {pose}, mostly on bone {bone})" : ""));
            return 0;
        }

        /// <summary>skintool compare out.png old.dat new.dat Pl**.dat Pl**AJ.dat [--original costume.dat]</summary>
        static int Compare(string[] args)
        {
            string output = args[1], before = args[2], after = args[3], fighterDat = args[4], animDat = args[5];
            int o = Array.IndexOf(args, "--original");
            var rows = new List<string> { before, after };
            if (o > 0)
                rows.Add(args[o + 1]);
            Preview.Run(new[] { "preview", output }.Concat(rows).Concat(new[] { "--anims", fighterDat, animDat }).ToArray(), _ => { });

            // the two builds' rows, pixel by pixel: a pixel counts as changed when a colour moved by more than
            // shading noise; the share is of the pixels either build draws on
            StbImageSharp.ImageResult sheet;
            using (var s = File.OpenRead(output))
                sheet = StbImageSharp.ImageResult.FromStream(s, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            int rowH = sheet.Height / rows.Count, cell = rowH, cells = sheet.Width / cell;
            var changed = new int[cells];
            var drawn = new int[cells];
            bool Background(int i) => Math.Abs(sheet.Data[i] - 40) + Math.Abs(sheet.Data[i + 1] - 44) + Math.Abs(sheet.Data[i + 2] - 52) < 6;
            for (int y = 0; y < rowH; y++)
                for (int x = 0; x < sheet.Width; x++)
                {
                    int a = (y * sheet.Width + x) * 4, b = ((y + rowH) * sheet.Width + x) * 4;
                    if (Background(a) && Background(b))
                        continue;
                    drawn[x / cell]++;
                    int d = Math.Abs(sheet.Data[a] - sheet.Data[b]) + Math.Abs(sheet.Data[a + 1] - sheet.Data[b + 1]) + Math.Abs(sheet.Data[a + 2] - sheet.Data[b + 2]);
                    if (d > 48)
                        changed[x / cell]++;
                }
            float share = changed.Sum() / (float)Math.Max(drawn.Sum(), 1);
            int worst = Enumerable.Range(0, cells).MaxBy(c => changed[c] / (float)Math.Max(drawn[c], 1));
            var poses = new[] { "bind" }.Concat(Animations.Load(fighterDat, animDat, Animations.TestPoses).Select(p => p.Name)).ToArray();
            Console.WriteLine(FormattableString.Invariant($"pictures: {share:P1} of the drawn pixels changed") +
                (changed[worst] > 0 ? FormattableString.Invariant($" (most in {poses[Math.Min(worst / 2, poses.Length - 1)]} {(worst % 2 == 0 ? "front" : "side")}, {changed[worst] / (float)Math.Max(drawn[worst], 1):P0})") : ""));

            // the checks the build runs, before and after
            var fighter = Slots.Load(fighterDat);
            var test = Animations.Load(fighterDat, animDat, Animations.TestPoses);
            var all = Animations.Load(fighterDat, animDat, Animations.AllPoses(fighterDat, 0, 0.25f, 0.5f, 0.75f));
            var low = Animations.Load(fighterDat, animDat, Animations.FloorPoses);
            foreach (var (name, dat) in new[] { ("old", before), ("new", after) })
            {
                var stretch = Preview.StretchCheck(dat, fighter, test, _ => { });
                var (thin, _, _) = Preview.ThinCheck(dat, fighter, all);
                var floor = Preview.FloorDepth(dat, fighter, low);
                float deepest = floor.Count > 0 ? floor.Min(f => f.Low) : 0;
                Console.WriteLine(FormattableString.Invariant($"{name}: stretch {stretch ?? "ok"}; thin {thin:P1}; floor {deepest:0.0}"));
            }
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

        static void BuildCostumes(string model, List<string> dats, string outDir, string fighterDat, Dictionary<string, string> textureFolders, AutoRig.Options opt, Dictionary<string, Weapons.Upload> uploads, string overlayPng = null, bool fitOnly = false, string animDat = null)
        {
            if (!fitOnly)
                Directory.CreateDirectory(outDir);
            var fighterData = fighterDat != null ? Slots.Load(fighterDat) : null;
            var obj = ObjModel.Load(model, opt.Eyes);
            int holes = obj.CloseSmallHoles();
            if (holes > 0)
                Console.WriteLine($"closed {holes} small openings (open claw or tooth bases)");
            foreach (var note in obj.Notes)
                Console.WriteLine(note);
            foreach (var copy in Duplicates.Remove(obj))
                Console.WriteLine($"left out {copy}: two versions of one part drawn together fight and come apart");
            foreach (var layer in ShadeLayers.Remove(obj))
                Console.WriteLine($"left out {layer}: a grey shading layer over another part, which would cover it");
            if (opt.Style == "animelee")
                Console.WriteLine(Animelee.AddOutline(obj) ? "animelee: black outline added, flat lighting and colours"
                    : "animelee: flat lighting and colours (the model has its own outline)");
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
            opt.OwnDress = BoneMaps.Dress.Contains(BoneMaps.Code(dats[0]) ?? "");
            opt.Dress = opt.OwnDress || opt.Legs == "dress";
            if (opt.Legs == "dress")
                opt.Legs = "separate";
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
            // the fighter's moves, to keep the skin out of the floor in its low ones. Only the weights
            // change, so the size check (which draws the bind pose) leaves it out
            animDat ??= fighterDat != null ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fighterDat)), Path.GetFileNameWithoutExtension(fighterDat) + "AJ.dat") : null;
            if (fighterData != null && File.Exists(animDat) && !fitOnly)
            {
                var poses = Animations.Load(fighterDat, animDat, Animations.FloorPoses);
                var lowest = Preview.FloorDepth(dats[0], fighterData, poses);
                var moves = Animations.Load(fighterDat, animDat, Animations.AllPoses(fighterDat, 0, 0.5f));
                rig.FloorGuard(fighter, skeleton, poses, lowest.Select(l => l.Low).ToList(), fighter.SkinBones.Concat(rig.Weights.SelectMany(w => w.Select(e => e.Bone))).ToHashSet(), moves);
            }
            Console.Write(rig.Report);
            if (overlayPng != null)
                Preview.Overlay(dats[0], fighterData, obj, rig.Positions, overlayPng, float.IsNaN(rig.NeckY) ? null : rig.NeckYAt, rig.NeckSlope != 0 ? rig.NeckZ : null);
            if (fitOnly)
                return;

            // the costumes only read the fit, so they're built side by side; what each says prints in order
            var said = new List<string>[dats.Count];
            Each(dats.Count, c => said[c] = BuildCostume(dats[c], dats[0], first, skeleton, rig, obj, outDir, fighterData, textureFolders, opt, uploads));
            foreach (var line in said.SelectMany(l => l))
                Console.WriteLine(line);
        }

        /// <summary>Runs body(0..count-1) side by side; a failure throws the first one's own error, as a plain loop would.</summary>
        static void Each(int count, Action<int> body)
        {
            var failed = new Exception[count];
            Parallel.For(0, count, i =>
            {
                try { body(i); }
                catch (Exception e) { failed[i] = e; }
            });
            var first = failed.FirstOrDefault(e => e != null);
            if (first != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
        }

        /// <summary>Puts the fitted model into one costume and saves it; returns what to print about it.</summary>
        static List<string> BuildCostume(string dat, string firstDat, HSDRawFile first, Skeleton skeleton, AutoRig rig, ObjModel obj, string outDir, HSDRaw.Melee.Pl.SBM_FighterData fighterData, Dictionary<string, string> textureFolders, AutoRig.Options opt, Dictionary<string, Weapons.Upload> uploads)
        {
            var file = dat == firstDat ? first : new HSDRawFile(dat);
            var root = (HSD_JOBJ)file.Roots[0].Data;
            var sk = dat == firstDat ? skeleton : new Skeleton(root);

            if (!Validator.SameStructure(skeleton, sk))
                throw new Exception($"{Path.GetFileName(dat)} has a different skeleton from {Path.GetFileName(firstDat)}; give each character its own run");

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
            var builder = new SkinBuilder(file, sk) { FighterData = fighterData, Flat = opt.Style == "animelee" };
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

            builder.Notes.Add($"wrote {Path.GetFileName(output)} ({new FileInfo(output).Length / 1024} KB), skeleton unchanged");
            return builder.Notes;
        }
    }
}

