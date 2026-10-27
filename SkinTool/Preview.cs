using HSDRaw;
using HSDRaw.Common;
using HSDRaw.GX;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Renders a costume in a few test poses so weighting problems show up without opening the game.
    /// The skinning here matches the game's: each vertex blends (inverse bind * posed bone) by its weights.
    /// </summary>
    public static class Preview
    {
        class Vert
        {
            public Vector3 Pos;
            public Vector2 UV;
            public int[] Bones;
            public float[] Weights;
        }

        class Tex
        {
            public int W, H;
            public byte[] Bgra;
            public bool Mirror;
        }

        class Tri
        {
            public Vert A, B, C;
            public Tex Tex;
            public int Slot;
            // drawn from both sides in game (the mesh's cull flags are clear), as skin meshes are
            public bool TwoSided;
        }

        // joint index -> extra rotation in degrees (x, y, z), per pose
        public class Pose
        {
            public string Name;

            // extra rotation in degrees, added to the bind pose
            public Dictionary<int, Vector3> Rotations = new();

            // values taken from an animation: scale xyz, rotation xyz, translation xyz (null keeps the bind value)
            public Dictionary<int, float?[]> Values = new();
        }

        static HashSet<int> onlySlots;

        /// <summary>
        /// The fitted model laid over the original costume, front and side, in the bind pose, to see how its
        /// size and turn compare before building.
        /// </summary>
        public static void Overlay(string costume, HSDRaw.Melee.Pl.SBM_FighterData fighter, ObjModel model, Vector3[] positions, string output, float neckY = float.NaN)
        {
            const int cell = 320;
            var root = (HSD_JOBJ)new HSDRawFile(costume).Roots[0].Data;
            var sk = new Skeleton(root);
            var hidden = fighter != null ? Slots.HiddenByDefault(fighter) : new HashSet<int>();
            var original = Decode(sk).Where(t => !hidden.Contains(t.Slot)).ToList();

            var textures = new Dictionary<string, Tex>();
            var tris = new List<Tri>();
            foreach (var t in model.Triangles)
            {
                Vert V(ObjModel.Corner c) => new Vert
                {
                    Pos = positions[c.Position],
                    UV = c.UV >= 0 ? new Vector2(model.UVs[c.UV].X, 1 - model.UVs[c.UV].Y) : Vector2.Zero,
                    Bones = new[] { 0 },
                    Weights = new[] { 1f },
                };
                tris.Add(new Tri { A = V(t.A), B = V(t.B), C = V(t.C), Tex = LoadTexture(model.TextureOf(t.Material), textures) });
            }

            var bind = new Pose { Name = "bind" };
            // the blend, plus the character and the model on their own (the model on a see-through
            // background), so the panel can fade one over the other
            var sheet = new Bitmap(cell * 2, cell);
            var fighterSheet = new Bitmap(cell * 2, cell);
            var modelSheet = new Bitmap(cell * 2, cell, clear: true);
            for (int v = 0; v < 2; v++)
            {
                var img = Render(sk, original, bind, v * 90f, cell);
                var mine = Render(sk, tris, bind, v * 90f, cell, cull: false, clear: true);
                fighterSheet.Draw(img, v * cell, 0);
                img.Blend(mine, 0.6f);
                // where the head starts: a dashed line, placed the way Render places heights
                if (!float.IsNaN(neckY))
                {
                    int hip = Math.Min(4, sk.Joints.Count - 1);
                    int y = (int)MathF.Round(cell * 0.92f - (neckY - sk.Position(hip).Y + 8.3f) * cell / 26f);
                    for (int x = 0; x < cell && y >= 0 && y < cell - 1; x++)
                        if (x / 6 % 2 == 0)
                            for (int dy = 0; dy < 2; dy++)
                                foreach (var b in new[] { img, mine })
                                    b.Set(x, y + dy, new Vector3(1f, 0.85f, 0.2f));
                }
                sheet.Draw(img, v * cell, 0);
                modelSheet.Draw(mine, v * cell, 0);
            }
            var stem = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(output)), System.IO.Path.GetFileNameWithoutExtension(output));
            fighterSheet.SavePng(stem + "-fighter.png");
            modelSheet.SavePng(stem + "-model.png");
            sheet.SavePng(output);
            Console.WriteLine($"wrote {output}");
        }

        static Tex LoadTexture(string path, Dictionary<string, Tex> cache)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            if (cache.TryGetValue(path, out var t)) return t;
            try
            {
                using var stream = System.IO.File.OpenRead(path);
                var img = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                var bgra = new byte[img.Data.Length];
                for (int i = 0; i < bgra.Length; i += 4)
                {
                    bgra[i] = img.Data[i + 2]; bgra[i + 1] = img.Data[i + 1]; bgra[i + 2] = img.Data[i]; bgra[i + 3] = img.Data[i + 3];
                }
                t = new Tex { W = img.Width, H = img.Height, Bgra = bgra };
            }
            catch
            {
                t = null;
            }
            return cache[path] = t;
        }

        /// <summary>Draws an uploaded model from the front and the side, filling the picture, to pick it from others.</summary>
        public static void Thumbnail(ObjModel model, string output)
        {
            const int cell = 320;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in model.Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var size = Vector3.Max(max - min, new Vector3(1e-6f));
            // the renderer frames about 26 units, with the floor near the bottom
            float scale = Math.Min(20 / size.Y, 22 / Math.Max(size.X, size.Z));
            var centre = (min + max) / 2;
            Vector3 Fit(Vector3 p) => new Vector3((p.X - centre.X) * scale, (p.Y - min.Y) * scale - 7.3f, (p.Z - centre.Z) * scale);

            var textures = new Dictionary<string, Tex>();
            var tris = new List<Tri>();
            foreach (var t in model.Triangles)
            {
                var tex = LoadTexture(model.TextureOf(t.Material), textures);
                Vert V(ObjModel.Corner c) => new Vert
                {
                    Pos = Fit(model.Positions[c.Position]),
                    UV = c.UV >= 0 ? new Vector2(model.UVs[c.UV].X, 1 - model.UVs[c.UV].Y) : Vector2.Zero,
                    Bones = new[] { 0 },
                    Weights = new[] { 1f },
                };
                tris.Add(new Tri { A = V(t.A), B = V(t.B), C = V(t.C), Tex = tex });
            }

            var sk = new Skeleton(new HSD_JOBJ { SX = 1, SY = 1, SZ = 1 });
            var sheet = new Bitmap(cell * 2, cell);
            for (int v = 0; v < 2; v++)
                sheet.Draw(Render(sk, tris, new Pose { Name = "bind" }, v * 90f, cell, cull: false), v * cell, 0);
            sheet.SavePng(output);
        }

        public static int Run(string[] args)
        {
            string output = args[1];
            var dats = args.Skip(2).ToList();

            // --anims Pl**.dat Pl**AJ.dat uses the fighter's real animations instead of made-up poses
            var poses = DefaultPoses();
            int a = dats.IndexOf("--anims");
            if (a >= 0)
            {
                poses = new List<Pose> { new Pose { Name = "bind" } };
                poses.AddRange(Animations.Load(dats[a + 1], dats[a + 2], Animations.TestPoses));
                dats.RemoveRange(a, 3);
            }
            // --bones colours each vertex by the bones it follows instead of its texture
            showBones = dats.Remove("--bones");
            // --fighter Pl**.dat draws only what the game shows by default (no far-away model, one choice
            // per switchable group) and adds a row with each weapon in the hand
            HSDRaw.Melee.Pl.SBM_FighterData fighter = null;
            int fo = dats.IndexOf("--fighter");
            if (fo >= 0)
            {
                fighter = Slots.Load(dats[fo + 1]);
                dats.RemoveRange(fo, 2);
            }
            // --slots 55,56 draws only those mesh slots, to see what a slot is
            int so = dats.IndexOf("--slots");
            if (so >= 0)
            {
                onlySlots = dats[so + 1].Split(',').Select(int.Parse).ToHashSet();
                dats.RemoveRange(so, 2);
            }

            const int cell = 320;
            int views = 2;
            var code = BoneMaps.Code(dats[0]);
            var weapons = fighter != null ? Weapons.For(code) : Array.Empty<Weapons.Weapon>();
            int columns = Math.Max(poses.Count, weapons.Length);
            var sheet = new Bitmap(cell * columns * views, cell * (dats.Count + (weapons.Length > 0 ? 1 : 0)));

            var hidden = fighter != null ? Slots.HiddenByDefault(fighter) : new HashSet<int>();
            for (int row = 0; row < dats.Count; row++)
            {
                var file = new HSDRawFile(dats[row]);
                var root = (HSD_JOBJ)file.Roots[0].Data;
                var sk = new Skeleton(root);
                var all = Decode(sk);
                var tris = all.Where(t => !hidden.Contains(t.Slot)).ToList();

                for (int p = 0; p < poses.Count; p++)
                    for (int v = 0; v < views; v++)
                    {
                        var img = Render(sk, tris, poses[p], v * 90f, cell);
                        sheet.Draw(img, (p * views + v) * cell, row * cell);
                    }

                // the first costume holding each weapon, front and side
                if (row == 0 && weapons.Length > 0)
                {
                    var weaponSlots = Weapons.AllSlots(fighter, code);
                    var body = all.Where(t => !hidden.Contains(t.Slot) && !weaponSlots.Contains(t.Slot)).ToList();
                    for (int w = 0; w < weapons.Length; w++)
                    {
                        var held = Weapons.HeldSlots(fighter, weapons[w]);
                        var withWeapon = body.Concat(all.Where(t => held.Contains(t.Slot))).ToList();
                        for (int v = 0; v < views; v++)
                            sheet.Draw(Render(sk, withWeapon, poses[0], v * 90f, cell), (w * views + v) * cell, dats.Count * cell);
                    }
                }
            }

            sheet.SavePng(output);
            Console.WriteLine($"poses: {string.Join(", ", poses.Select(p => p.Name))} (front and side each)");
            if (weapons.Length > 0)
                Console.WriteLine($"weapons row: {string.Join(", ", weapons.Select(w => w.Key))}");
            Console.WriteLine($"wrote {output}");
            return 0;
        }

        /// <summary>
        /// Poses the skin in each test pose and finds where it gets pulled apart: triangles whose longest
        /// edge grows to several times its length at rest, the sign of a part following bones it doesn't
        /// belong to (a head fanning out into wings). Returns a line to show the user, or null when the
        /// skin holds together.
        /// </summary>
        public static string StretchCheck(string dat, HSDRaw.Melee.Pl.SBM_FighterData fighter, List<Pose> poses)
        {
            var file = new HSDRawFile(dat);
            var sk = new Skeleton((HSD_JOBJ)file.Roots[0].Data);
            var hidden = fighter != null ? Slots.HiddenByDefault(fighter) : new HashSet<int>();
            var weaponSlots = fighter != null ? Weapons.AllSlots(fighter, BoneMaps.Code(dat)) : new HashSet<int>();
            var tris = Decode(sk).Where(t => !hidden.Contains(t.Slot) && !weaponSlots.Contains(t.Slot)).ToList();
            if (tris.Count == 0)
                return null;

            static float Longest(Vector3 a, Vector3 b, Vector3 c) => MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
            static float Area(Tri t) => Vector3.Cross(t.B.Pos - t.A.Pos, t.C.Pos - t.A.Pos).Length() / 2;
            var rest = tris.Select(t => Longest(t.A.Pos, t.B.Pos, t.C.Pos)).ToArray();
            var worst = new float[tris.Count];
            var worstPose = new string[tris.Count];
            foreach (var pose in poses)
            {
                var skin = PosedSkinning(sk, pose);
                var posed = new Dictionary<Vert, Vector3>();
                Vector3 P(Vert v)
                {
                    if (posed.TryGetValue(v, out var p)) return p;
                    p = Vector3.Zero;
                    for (int i = 0; i < v.Bones.Length; i++)
                        p += Vector3.Transform(v.Pos, skin[v.Bones[i]]) * v.Weights[i];
                    return posed[v] = p;
                }
                for (int i = 0; i < tris.Count; i++)
                {
                    if (rest[i] < 1e-5f) continue;
                    float r = Longest(P(tris[i].A), P(tris[i].B), P(tris[i].C)) / rest[i];
                    if (r > worst[i]) { worst[i] = r; worstPose[i] = pose.Name; }
                }
            }

            // by area, so big pieces count and slivers at elbows and knees don't
            float total = tris.Sum(Area);
            var bad = Enumerable.Range(0, tris.Count).Where(i => worst[i] > StretchLimit).ToList();
            float share = total > 0 ? bad.Sum(i => Area(tris[i])) / total : 0;
            Console.WriteLine(FormattableString.Invariant($"stretch check: {share:P1} of the skin's area pulled to more than {StretchLimit}x in some pose"));
            if (share < StretchWarn)
                return null;

            // where: the bone the most bad area follows, named from the fighter's bone map
            var roles = BoneMaps.Get(BoneMaps.Code(dat)) ?? new Dictionary<string, int>();
            int Main(Vert v) => v.Bones[Array.IndexOf(v.Weights, v.Weights.Max())];
            int bone = bad.GroupBy(i => Main(tris[i].A)).OrderByDescending(g => g.Sum(i => Area(tris[i]))).First().Key;
            string where = roles.FirstOrDefault(r => r.Value == bone).Key switch
            {
                "face" => "the head",
                "body" or "butt" or "chest" => "the body",
                "lsh" or "lel" or "lha" or "rsh" or "rel" or "rha" => "an arm",
                "lle" or "lkn" or "lfb" or "lff" or "rle" or "rkn" or "rfb" or "rff" => "a leg",
                _ => $"the part on bone {bone}",
            };
            string move = bad.GroupBy(i => worstPose[i]).OrderByDescending(g => g.Sum(i => Area(tris[i]))).First().Key;
            return FormattableString.Invariant($"{share:P0} of the skin gets pulled apart in some moves, mostly around {where} (worst in {move}). It may look torn in game: check the preview, and try moving the head line or another Arms setting.");
        }

        // Past 4x by area: Fox's own skin 0.5 %, Monty on 7 fighters 0.1 to 1.5 %, Squidward with his face
        // read as arms 2.9 % and with his head as arms 7.2 %
        const float StretchLimit = 4f, StretchWarn = 0.025f;

        /// <summary>
        /// A character select portrait (136x188, see-through background) of a costume: the fighter in a
        /// pose, turned towards the camera, cropped to the head and upper body, with the soft drop
        /// shadow community portraits use.
        /// </summary>
        public static void Portrait(string costume, HSDRaw.Melee.Pl.SBM_FighterData fighter, Pose pose, string output, float yaw = -30)
        {
            const int W = 136, H = 188, Over = 4;
            var sk = new Skeleton((HSD_JOBJ)new HSDRawFile(costume).Roots[0].Data);
            var hidden = fighter != null ? Slots.HiddenByDefault(fighter) : new HashSet<int>();
            // Peach's club, pan and racket and Ganondorf's sword only come out for some attacks
            var code = BoneMaps.Code(costume);
            if (fighter != null && (code == "Pe" || code == "Gn"))
                hidden.UnionWith(Weapons.AllSlots(fighter, code));
            var tris = Decode(sk).Where(t => !hidden.Contains(t.Slot)).ToList();

            // where the posed fighter is, seen from the camera (turned, looking slightly down)
            var view = Matrix4x4.CreateRotationY(yaw * MathF.PI / 180) * Matrix4x4.CreateRotationX(8 * MathF.PI / 180);
            var skin = PosedSkinning(sk, pose);
            var pts = tris.SelectMany(t => new[] { t.A, t.B, t.C }).Distinct()
                .Select(v =>
                {
                    var p = Vector3.Zero;
                    for (int i = 0; i < v.Bones.Length; i++)
                        p += Vector3.Transform(v.Pos, skin[v.Bones[i]]) * v.Weights[i];
                    return Vector3.Transform(p, view);
                }).ToList();
            if (pts.Count == 0)
                return;
            float top = pts.Max(p => p.Y), bottom = pts.Min(p => p.Y), tall = MathF.Max(top - bottom, 1e-3f);

            // the head and upper body fill the picture: the top 70% of the fighter, a little room above,
            // centred on the upper half
            float window = tall * 0.7f;
            var upper = pts.Where(p => p.Y > top - tall * 0.45f).ToList();
            float cx = (upper.Min(p => p.X) + upper.Max(p => p.X)) / 2;
            // a short, wide fighter (Pikachu) would only show the middle of its head: widen to fit most of it
            var shown = pts.Where(p => p.Y > top - window).Select(p => MathF.Abs(p.X - cx)).OrderBy(x => x).ToList();
            window = MathF.Max(window, shown[shown.Count * 9 / 10] * 2 * 1.05f * H / W);
            float scale = H * Over / window;
            var framing = new Framing
            {
                View = view,
                Scale = scale,
                Center = new Vector2(W * Over / 2f - cx * scale, (top + window * 0.04f) * scale),
                Width = W * Over,
                Height = H * Over,
                Flat = true,
            };
            var big = Render(sk, tris, pose, 0, 0, cull: true, clear: true, framing: framing);

            // shrink, averaging colour by coverage so edges stay clean
            var img = new Bitmap(W, H, clear: true);
            var alpha = new float[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var sum = Vector3.Zero;
                    float a = 0;
                    for (int dy = 0; dy < Over; dy++)
                        for (int dx = 0; dx < Over; dx++)
                        {
                            int i = ((y * Over + dy) * big.Width + x * Over + dx) * 4;
                            float pa = big.Rgba[i + 3] / 255f;
                            sum += new Vector3(big.Rgba[i], big.Rgba[i + 1], big.Rgba[i + 2]) * pa;
                            a += pa;
                        }
                    alpha[y * W + x] = a / (Over * Over);
                    if (a > 0)
                        img.Set(x, y, sum / a / 255f);
                }

            // the shadow: the same shape, up and to the left, in see-through black
            const int sx = -6, sy = -4;
            const float shadow = 109 / 255f;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    float a = alpha[y * W + x];
                    int ox = x - sx, oy = y - sy;
                    float s = ox >= 0 && ox < W && oy >= 0 && oy < H ? alpha[oy * W + ox] * shadow : 0;
                    // the fighter over its shadow
                    float outA = a + s * (1 - a);
                    if (outA <= 0)
                    {
                        img.Rgba[i + 3] = 0;
                        continue;
                    }
                    for (int c = 0; c < 3; c++)
                        img.Rgba[i + c] = (byte)Math.Clamp(img.Rgba[i + c] * a / outA + 0.5f, 0, 255);
                    img.Rgba[i + 3] = (byte)Math.Clamp(outA * 255 + 0.5f, 0, 255);
                }
            img.SavePng(output);
        }

        public static List<Pose> DefaultPoses()
        {
            // joint numbers are Fox's; most fighters share the leg and arm layout closely enough for a smoke test
            return new List<Pose>
            {
                new Pose { Name = "bind" },
                new Pose { Name = "run", Rotations = {
                    [6] = new(0, 0, 45), [7] = new(0, 0, -70),
                    [12] = new(0, 0, -35), [13] = new(0, 0, -20),
                    [25] = new(0, 50, 60), [26] = new(0, 60, 0),
                    [55] = new(0, -50, -60), [56] = new(0, -60, 0),
                    [22] = new(0, 0, -15), [41] = new(0, 0, 15) } },
                new Pose { Name = "arms down", Rotations = {
                    [25] = new(0, 0, -70), [55] = new(0, 0, -70),
                    [26] = new(0, 0, -30), [56] = new(0, 0, -30) } },
                new Pose { Name = "crouch", Rotations = {
                    [4] = new(0, 0, -40),
                    [6] = new(0, 0, 80), [12] = new(0, 0, 80),
                    [7] = new(0, 0, -110), [13] = new(0, 0, -110),
                    [22] = new(0, 0, 30), [41] = new(30, 0, -20) } },
            };
        }

        static bool showBones;

        // the screen winding of a back face, checked against Fox's own meshes
        const float CullSign = -1;

        static Vector3 BoneColour(int bone)
        {
            // spread neighbouring bone numbers far apart around the hue circle
            float h = (bone * 0.618034f) % 1f * 6;
            float x = 1 - MathF.Abs(h % 2 - 1);
            var c = (int)h switch
            {
                0 => new Vector3(1, x, 0),
                1 => new Vector3(x, 1, 0),
                2 => new Vector3(0, 1, x),
                3 => new Vector3(0, x, 1),
                4 => new Vector3(x, 0, 1),
                _ => new Vector3(1, 0, x),
            };
            return c * 0.8f + new Vector3(0.15f);
        }

        static Vector3 BoneColour(Vert v)
        {
            var c = Vector3.Zero;
            for (int i = 0; i < v.Bones.Length; i++)
                c += BoneColour(v.Bones[i]) * v.Weights[i];
            return c;
        }

        static List<Tri> Decode(Skeleton sk)
        {
            var tris = new List<Tri>();
            var texCache = new Dictionary<HSD_TOBJ, Tex>();
            int slot = -1;

            for (int ji = 0; ji < sk.Joints.Count; ji++)
            {
                var joint = sk.Joints[ji];
                if (joint.Dobj == null) continue;

                foreach (var dobj in joint.Dobj.List)
                {
                    slot++;
                    if (dobj.Pobj == null || (onlySlots != null && !onlySlots.Contains(slot))) continue;

                    Tex tex = null;
                    var tobj = dobj.Mobj?.Textures;
                    if (tobj != null && tobj.ImageData != null)
                    {
                        if (!texCache.TryGetValue(tobj, out tex))
                        {
                            tex = new Tex { W = tobj.ImageData.Width, H = tobj.ImageData.Height, Bgra = tobj.GetDecodedImageData(), Mirror = tobj.WrapS == GXWrapMode.MIRROR };
                            texCache[tobj] = tex;
                        }
                    }

                    foreach (var pobj in dobj.Pobj.List)
                    {
                        var dl = pobj.ToDisplayList();
                        bool twoSided = (pobj.Flags & (POBJ_FLAG.CULLBACK | POBJ_FLAG.CULLFRONT)) == 0;
                        var envs = pobj.EnvelopeWeights;
                        bool env = envs != null && pobj.HasAttribute(GXAttribName.GX_VA_PNMTXIDX);
                        int owner = pobj.SingleBoundJOBJ != null ? sk.IndexOf(pobj.SingleBoundJOBJ) : ji;

                        int offset = 0;
                        foreach (var prim in dl.Primitives)
                        {
                            var vs = dl.Vertices.GetRange(offset, prim.Count);
                            offset += prim.Count;

                            var conv = vs.Select(gv =>
                            {
                                var v = new Vert { Pos = new Vector3(gv.POS.X, gv.POS.Y, gv.POS.Z), UV = new Vector2(gv.TEX0.X, gv.TEX0.Y) };
                                if (env)
                                {
                                    var e = envs[gv.PNMTXIDX / 3];
                                    v.Bones = e.JOBJs.Select(sk.IndexOf).ToArray();
                                    v.Weights = e.Weights;
                                    if (e.EnvelopeCount == 1)
                                        v.Pos = Vector3.Transform(v.Pos, sk.World[v.Bones[0]]);
                                }
                                else
                                {
                                    v.Bones = new[] { owner };
                                    v.Weights = new[] { 1f };
                                    v.Pos = Vector3.Transform(v.Pos, sk.World[owner]);
                                }
                                return v;
                            }).ToList();

                            switch (prim.PrimitiveType)
                            {
                                case GXPrimitiveType.Triangles:
                                    for (int i = 0; i + 2 < conv.Count; i += 3)
                                        tris.Add(new Tri { A = conv[i], B = conv[i + 1], C = conv[i + 2], Tex = tex, Slot = slot, TwoSided = twoSided });
                                    break;
                                case GXPrimitiveType.TriangleStrip:
                                    // every other strip triangle runs the other way round
                                    for (int i = 2; i < conv.Count; i++)
                                        tris.Add(i % 2 == 0
                                            ? new Tri { A = conv[i - 2], B = conv[i - 1], C = conv[i], Tex = tex, Slot = slot, TwoSided = twoSided }
                                            : new Tri { A = conv[i - 1], B = conv[i - 2], C = conv[i], Tex = tex, Slot = slot, TwoSided = twoSided });
                                    break;
                                case GXPrimitiveType.Quads:
                                    for (int i = 0; i + 3 < conv.Count; i += 4)
                                    {
                                        tris.Add(new Tri { A = conv[i], B = conv[i + 1], C = conv[i + 2], Tex = tex, Slot = slot, TwoSided = twoSided });
                                        tris.Add(new Tri { A = conv[i + 2], B = conv[i + 3], C = conv[i], Tex = tex, Slot = slot, TwoSided = twoSided });
                                    }
                                    break;
                            }
                        }
                    }
                }
            }

            return tris;
        }

        static Matrix4x4[] PosedSkinning(Skeleton sk, Pose pose)
        {
            int n = sk.Joints.Count;
            var world = new Matrix4x4[n];
            for (int i = 0; i < n; i++)
            {
                var j = sk.Joints[i];
                var v = new[] { j.SX, j.SY, j.SZ, j.RX, j.RY, j.RZ, j.TX, j.TY, j.TZ };
                if (pose.Values.TryGetValue(i, out var anim))
                    for (int k = 0; k < 9; k++)
                        v[k] = anim[k] ?? v[k];
                if (pose.Rotations.TryGetValue(i, out var d))
                {
                    v[3] += d.X * MathF.PI / 180;
                    v[4] += d.Y * MathF.PI / 180;
                    v[5] += d.Z * MathF.PI / 180;
                }
                var local = Matrix4x4.CreateScale(v[0], v[1], v[2]) * Skeleton.Euler(v[3], v[4], v[5]) * Matrix4x4.CreateTranslation(v[6], v[7], v[8]);
                world[i] = sk.Parent[i] < 0 ? local : local * world[sk.Parent[i]];
            }

            var skin = new Matrix4x4[n];
            for (int i = 0; i < n; i++)
                skin[i] = sk.InverseBind(i) * world[i];
            return skin;
        }

        /// <summary>A camera other than the preview's fixed one: where to look, how big, and the picture's size.</summary>
        class Framing
        {
            public Matrix4x4 View;
            public float Scale;
            public Vector2 Center;
            public int Width, Height;
            // flat, cartoon-like light (as character select portraits are drawn)
            public bool Flat;
        }

        static Bitmap Render(Skeleton sk, List<Tri> tris, Pose pose, float yaw, int size, bool cull = true, bool clear = false, Framing framing = null)
        {
            var skin = PosedSkinning(sk, pose);
            var posed = new Dictionary<Vert, Vector3>();
            Vector3 P(Vert v)
            {
                if (posed.TryGetValue(v, out var p)) return p;
                p = Vector3.Zero;
                for (int i = 0; i < v.Bones.Length; i++)
                    p += Vector3.Transform(v.Pos, skin[v.Bones[i]]) * v.Weights[i];
                posed[v] = p;
                return p;
            }

            var view = Matrix4x4.CreateRotationY(yaw * MathF.PI / 180);

            // follow the hips so moves that travel stay in frame
            var hips = Vector3.Transform(sk.Position(Math.Min(4, sk.Joints.Count - 1)), skin[Math.Min(4, sk.Joints.Count - 1)]);
            view = Matrix4x4.CreateTranslation(-hips.X, -hips.Y + 8.3f, -hips.Z) * view;

            // frame: a fixed box around the fighter so every pose uses the same scale
            float scale = size / 26f;
            var center = new Vector2(size / 2f, size * 0.92f);
            int width = size, height = size;
            if (framing != null)
            {
                view = framing.View;
                scale = framing.Scale;
                center = framing.Center;
                (width, height) = (framing.Width, framing.Height);
            }

            var img = new Bitmap(width, height, clear);
            var depth = new float[width * height];
            Array.Fill(depth, float.MinValue);
            var light = Vector3.Normalize(new Vector3(0.4f, 0.6f, 1f));

            foreach (var t in tris)
            {
                var a = Vector3.Transform(P(t.A), view);
                var b = Vector3.Transform(P(t.B), view);
                var c = Vector3.Transform(P(t.C), view);
                var nrm = Vector3.Cross(b - a, c - a);
                if (nrm.LengthSquared() < 1e-12f) continue;
                nrm = Vector3.Normalize(nrm);
                float shade = framing?.Flat == true
                    ? 0.8f + 0.2f * MathF.Round(Math.Abs(Vector3.Dot(nrm, light)) * 2) / 2
                    : 0.35f + 0.65f * Math.Abs(Vector3.Dot(nrm, light));

                Vector2 S(Vector3 p) => new(center.X + p.X * scale, center.Y - p.Y * scale);
                var sa = S(a); var sb = S(b); var sc = S(c);

                int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.X, MathF.Min(sb.X, sc.X))));
                int maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(sa.X, MathF.Max(sb.X, sc.X))));
                int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.Y, MathF.Min(sb.Y, sc.Y))));
                int maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(sa.Y, MathF.Max(sb.Y, sc.Y))));
                float area = Edge(sa, sb, sc);
                if (MathF.Abs(area) < 1e-6f) continue;
                // the game doesn't draw back faces of meshes that cull them, so neither does the preview
                // (an inside-out mesh shows); skin meshes are drawn from both sides
                if (cull && !t.TwoSided && area * CullSign > 0) continue;

                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        var p = new Vector2(x + 0.5f, y + 0.5f);
                        float w0 = Edge(sb, sc, p) / area, w1 = Edge(sc, sa, p) / area, w2 = Edge(sa, sb, p) / area;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                        int idx = y * width + x;
                        if (z <= depth[idx]) continue;
                        depth[idx] = z;

                        var col = new Vector3(0.75f, 0.75f, 0.78f);
                        if (showBones)
                            col = w0 * BoneColour(t.A) + w1 * BoneColour(t.B) + w2 * BoneColour(t.C);
                        else if (t.Tex != null)
                        {
                            var uv = w0 * t.A.UV + w1 * t.B.UV + w2 * t.C.UV;
                            col = Sample(t.Tex, uv);
                        }
                        col *= shade;
                        img.Set(x, y, col);
                    }
            }

            return img;
        }

        static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

        static Vector3 Sample(Tex t, Vector2 uv)
        {
            float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
            int x = Math.Clamp((int)(u * t.W), 0, t.W - 1);
            int y = Math.Clamp((int)(v * t.H), 0, t.H - 1);
            int i = (y * t.W + x) * 4;
            return new Vector3(t.Bgra[i + 2], t.Bgra[i + 1], t.Bgra[i]) / 255f;
        }
    }
}
