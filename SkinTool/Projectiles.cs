using HSDRaw;
using HSDRaw.Common;
using HSDRaw.GX;
using HSDRaw.Melee.Pl;
using HSDRaw.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Things a fighter throws, fires or brings out (Yoshi's egg, Fox's and Falco's laser, blaster and side special afterimage). The
    /// game calls them articles: each has its own small model in the fighter's shared data file (PlYs.dat),
    /// not in the costume files, so one change shows for every costume. A projectile can get a new model
    /// (fitted over the old one, like a weapon) or a new colour (the old model, its hue turned).
    /// </summary>
    public static class Projectiles
    {
        public class Projectile
        {
            public string Key, Name;
            public int Article;
            /// <summary>Whether a new model makes sense (a laser is a glow, so it only takes a colour).</summary>
            public bool Model = true;
        }

        static Projectile P(string key, string name, int article, bool model = true) => new() { Key = key, Name = name, Article = article, Model = model };

        // found by drawing each article (skintool articles --picture)
        static readonly Dictionary<string, Projectile[]> Table = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Ys"] = new[] { P("egg", "Egg (thrown)", 0), P("star", "Stars (down special)", 1, false), P("egg-lay", "Egg (Egg Lay)", 2) },
            ["Fx"] = new[] { P("laser", "Laser", 0, false), P("blaster", "Blaster", 1), P("illusion", "Illusion afterimage (side special)", 2, false) },
            ["Fc"] = new[] { P("laser", "Laser", 0, false), P("blaster", "Blaster", 1), P("phantasm", "Phantasm afterimage (side special)", 3, false) },
            ["Lk"] = new[] { P("bomb", "Bomb", 0), P("boomerang", "Boomerang", 1), P("hookshot", "Hookshot", 2), P("arrow", "Arrow", 3), P("bow", "Bow", 4) },
            ["Cl"] = new[] { P("bomb", "Bomb", 0), P("boomerang", "Boomerang", 1), P("hookshot", "Hookshot", 2), P("arrow", "Fire arrow", 3), P("bow", "Bow", 4) },
            ["Ss"] = new[] { P("bomb", "Bomb", 0), P("grapple", "Grapple beam", 1), P("missile", "Missile", 2), P("charge-shot", "Charge shot", 3, false) },
            ["Pe"] = new[] { P("turnip", "Turnip", 1), P("parasol", "Parasol", 2), P("toad", "Toad", 3) },
            ["Ns"] = new[] { P("pk-fire", "PK Fire bolt", 0, false), P("pk-fire-pillar", "PK Fire pillar", 1, false), P("pk-flash", "PK Flash", 2, false), P("pk-thunder", "PK Thunder", 3, false), P("bat", "Bat", 9) },
            ["Pk"] = new[] { P("thunder", "Thunder", 0, false), P("thunder-jolt", "Thunder Jolt", 2, false) },
            ["Pc"] = new[] { P("thunder", "Thunder", 0, false), P("thunder-jolt", "Thunder Jolt", 2, false) },
            ["Mr"] = new[] { P("fireball", "Fireball", 0, false), P("cape", "Cape", 2) },
            ["Dr"] = new[] { P("pill", "Megavitamin", 1), P("cape", "Super Sheet", 3) },
            ["Lg"] = new[] { P("fireball", "Fireball", 0, false) },
            ["Mt"] = new[] { P("shadow-ball", "Shadow Ball", 1, false) },
            ["Kb"] = new[] { P("final-cutter", "Final Cutter wave", 0, false), P("hammer", "Hammer", 1) },
            ["Sk"] = new[] { P("needle", "Needles", 0, false) },
            ["Pp"] = new[] { P("ice", "Ice Shot", 0) },
        };

        public static SBM_FighterData Data(HSDRawFile file) =>
            file.Roots.Select(r => r.Data).OfType<SBM_FighterData>().FirstOrDefault();

        public static SBM_Article[] Articles(SBM_FighterData data) => data?.Articles?.Articles ?? Array.Empty<SBM_Article>();

        /// <summary>The named projectiles of a fighter (by its two letter code), then every other article with a model as articleN.</summary>
        public static List<Projectile> For(string code, SBM_FighterData data)
        {
            var list = code != null && Table.TryGetValue(code, out var t) ? t.ToList() : new List<Projectile>();
            var arts = Articles(data);
            list = list.Where(p => p.Article < arts.Length && arts[p.Article]?.Model?.RootModelJoint != null).ToList();
            for (int i = 0; i < arts.Length; i++)
                if (arts[i]?.Model?.RootModelJoint != null && list.All(p => p.Article != i))
                    list.Add(P("article" + i, "Article " + i, i));
            return list;
        }

        static Projectile Find(string code, SBM_FighterData data, string key) =>
            For(code, data).FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception($"{code} has no projectile called {key} (it has: {string.Join(", ", For(code, data).Select(p => p.Key).DefaultIfEmpty("none"))})");

        /// <summary>What to change: a new model (with its flip) and/or a colour.</summary>
        public class Change
        {
            public ObjModel Model;
            public bool Flip;
            public Vector3? Colour;
        }

        public static Vector3 ParseColour(string s)
        {
            s = s.Trim().TrimStart('#');
            if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                throw new Exception($"colours are written like #3080ff, not {s}");
            return new Vector3((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF) / 255f;
        }

        /// <summary>
        /// Applies the changes to a copy of the fighter file. Everything but the changed projectiles' models is
        /// checked to be the same afterwards (the moves, hitboxes and timings the game plays by), so it stays
        /// safe for Slippi.
        /// </summary>
        public static List<string> Apply(string fighterDat, string output, Dictionary<string, Change> changes, string picture = null)
        {
            var said = new List<string>();
            var file = new HSDRawFile(fighterDat);
            var data = Data(file) ?? throw new Exception($"{Path.GetFileName(fighterDat)} is not a fighter's data file (Pl**.dat)");
            var code = BoneMaps.Code(fighterDat);
            var arts = Articles(data);
            var changed = new List<Projectile>();
            var before = new Dictionary<int, List<PTri>>();

            foreach (var (key, change) in changes)
            {
                var p = Find(code, data, key);
                var art = arts[p.Article];
                // the original, for the picture (a copy of the joints would lose which joints its skin follows)
                if (picture != null)
                    before[p.Article] = Decode(art.Model.RootModelJoint);
                if (change.Model != null)
                {
                    if (!p.Model)
                        throw new Exception($"the {p.Name.ToLowerInvariant()} is a glow, not a solid model: give it a colour instead");
                    said.AddRange(Replace(art, change.Model, change.Flip, p.Name));
                }
                if (change.Colour is Vector3 c)
                    said.Add(Recolour(art, c, p.Name));
                changed.Add(p);
            }

            file.Save(output);

            // read back: the changed models load, and nothing else moved
            var back = new HSDRawFile(output);
            var problems = Compare(new HSDRawFile(fighterDat), back, changed.Select(p => p.Article).ToHashSet());
            if (problems.Count > 0)
                throw new Exception("the fighter file changed outside the projectile models:\n  " + string.Join("\n  ", problems.Take(10)));
            long grew = new FileInfo(output).Length - new FileInfo(fighterDat).Length;
            said.Add(FormattableString.Invariant($"{Path.GetFileName(output)}: {changed.Count} projectile(s) changed, file {(grew >= 0 ? "+" : "")}{grew / 1024.0:0} KB; the rest of the fighter is unchanged"));
            if (grew > 200 * 1024)
                said.Add("warning: the fighter file grew by more than 200 KB. The game keeps it in memory all match, so use a simpler model or smaller textures if it crashes on loading");

            if (picture != null)
            {
                var backArts = Articles(Data(back));
                DrawSheet(changed.Select(p => (before[p.Article], Decode(backArts[p.Article].Model.RootModelJoint))).ToList(), picture);
                said.Add($"picture: {picture} (each projectile, before and after, front and side)");
            }
            return said;
        }

        // ---------------------------------------------------------------- new model

        static bool SeeThrough(HSD_DOBJ d) => d.Mobj != null && (d.Mobj.RenderFlags.HasFlag(RENDER_MODE.XLU) || d.Mobj.Material?.Alpha < 0.99f);

        static int TriangleCount(HSD_DOBJ d) => d.Pobj?.List.Sum(p => Weapons.Triangles(p).Count() / 3) ?? 0;

        /// <summary>
        /// Puts a new model where the projectile's solid meshes were. See-through ones (a blaster's muzzle flash)
        /// stay, unless every mesh is see-through. The model is fitted like a weapon when the old one is long
        /// (a blaster: longest side along the old one's, wide end at its wide end) and kept upright when it's round
        /// (an egg), in both cases as big as the old one and centred on it.
        /// </summary>
        static List<string> Replace(SBM_Article art, ObjModel model, bool flip, string name)
        {
            var said = new List<string>();
            var root = art.Model.RootModelJoint;
            var sk = new Skeleton(root);
            var meshes = new List<(int joint, HSD_DOBJ dobj)>();
            for (int j = 0; j < sk.Joints.Count; j++)
                if (sk.Joints[j].Dobj != null)
                    foreach (var d in sk.Joints[j].Dobj.List)
                        if (d.Pobj != null)
                            meshes.Add((j, d));
            var solid = meshes.Where(m => !SeeThrough(m.dobj)).ToList();
            if (solid.Count == 0)
                solid = meshes;
            if (solid.Count == 0)
                throw new Exception($"the {name.ToLowerInvariant()} has no mesh to replace");

            // the joint carrying most of it carries the new one
            int joint = solid.GroupBy(m => m.joint).OrderByDescending(g => g.Sum(m => TriangleCount(m.dobj))).First().Key;
            Matrix4x4.Invert(sk.World[joint], out var toJoint);
            var target = new List<Vector3>();
            foreach (var (j, d) in solid)
                foreach (var pobj in d.Pobj.List)
                    foreach (var v in Weapons.Triangles(pobj))
                        target.Add(Vector3.Transform(World(sk, pobj, j, v), toJoint));

            var level = Weapons.Level(model);
            var source = model.Positions.Select(p => Vector3.Transform(p, level)).ToList();
            var (tAxes, tSide, tSkew, tMin, tMax) = Weapons.Shape(target);
            var (mAxes, mSide, mSkew, mMin, mMax) = Weapons.Shape(source);
            var tSize = tMax - tMin;
            float tLong = Weapons.Get(tSize, tAxes[0]), tMid = Weapons.Get(tSize, tAxes[1]);
            bool longShape = tLong > 1.4f * Math.Max(tMid, 1e-6f);

            Matrix4x4 rot = Matrix4x4.Identity;
            float scale;
            if (longShape)
            {
                var sign = new float[3];
                sign[0] = tSide * mSide * (flip ? -1 : 1);
                sign[1] = MathF.Abs(tSkew) > 0.05f && MathF.Abs(mSkew) > 0.05f ? MathF.Sign(tSkew) * MathF.Sign(mSkew) : 1;
                sign[2] = 1;
                void Make()
                {
                    rot = new Matrix4x4();
                    for (int k = 0; k < 3; k++)
                        Weapons.Set(ref rot, mAxes[k], tAxes[k], sign[k]);
                    rot.M44 = 1;
                }
                Make();
                if (rot.GetDeterminant() < 0)
                {
                    sign[2] = -1;
                    Make();
                }
                float mLong = Weapons.Get(mMax - mMin, mAxes[0]);
                scale = mLong > 1e-6f ? tLong / mLong : 1;
            }
            else
            {
                // round: the model's own up stays up, its biggest side as big as the old one's
                source = model.Positions.ToList();
                (_, _, _, mMin, mMax) = Weapons.Shape(source);
                level = Matrix4x4.Identity;
                if (flip)
                    rot = Matrix4x4.CreateRotationX(MathF.PI);
                var mSize = mMax - mMin;
                float mBig = MathF.Max(mSize.X, MathF.Max(mSize.Y, mSize.Z));
                scale = mBig > 1e-6f ? MathF.Max(tSize.X, MathF.Max(tSize.Y, tSize.Z)) / mBig : 1;
            }
            var mCentre = (mMin + mMax) / 2;
            var tCentre = (tMin + tMax) / 2;
            Vector3 Place(Vector3 p) => Vector3.Transform((p - mCentre) * scale, rot) + tCentre;

            var generator = new POBJ_Generator { UseTriangleStrips = true };
            var textures = new Dictionary<string, HSD_TOBJ>();
            var attrs = new[] { GXAttribName.GX_VA_POS, GXAttribName.GX_VA_NRM, GXAttribName.GX_VA_TEX0 };
            var made = new List<HSD_DOBJ>();
            var groups = model.Triangles.Where(t => !t.Hidden).GroupBy(t => model.TextureOf(t.Material) ?? "").OrderByDescending(g => g.Count()).ToList();
            bool anyAlpha = false;
            foreach (var g in groups)
            {
                var verts = new List<GX_Vertex>();
                foreach (var tri in g)
                {
                    var face = Vector3.Cross(source[tri.B.Position] - source[tri.A.Position], source[tri.C.Position] - source[tri.A.Position]);
                    foreach (var c in new[] { tri.A, tri.B, tri.C })
                    {
                        var pos = Place(source[c.Position]);
                        var nrm = c.Normal >= 0 ? Vector3.TransformNormal(model.Normals[c.Normal], level) : face;
                        nrm = Vector3.TransformNormal(nrm, rot);
                        nrm = nrm.LengthSquared() > 1e-12f ? Vector3.Normalize(nrm) : Vector3.UnitY;
                        var uv = c.UV >= 0 ? model.UVs[c.UV] : Vector2.Zero;
                        verts.Add(new GX_Vertex
                        {
                            POS = new GXVector3(pos.X, pos.Y, pos.Z),
                            NRM = new GXVector3(nrm.X, nrm.Y, nrm.Z),
                            TEX0 = new GXVector2(uv.X, 1 - uv.Y),
                        });
                    }
                }
                // projectiles are small on screen and the file stays in memory all match: 256 is plenty
                var mobj = SkinBuilder.CreateMaterial(string.IsNullOrEmpty(g.Key) ? null : g.Key, textures, false, 256);
                anyAlpha |= mobj.RenderFlags.HasFlag(RENDER_MODE.XLU);
                var dobj = new HSD_DOBJ { Mobj = mobj, Pobj = generator.CreatePOBJsFromTriangleList(verts, attrs, null, null) };
                // drawn from both sides, like the skins
                for (var pobj = dobj.Pobj; pobj != null; pobj = pobj.Next)
                    pobj.Flags &= ~(POBJ_FLAG.CULLFRONT | POBJ_FLAG.CULLBACK);
                made.Add(dobj);
            }
            generator.SaveChanges();

            // material animations point at meshes by their place on the joint, so a projectile that has them keeps its count
            bool animated = (art.ItemState?.Array ?? Array.Empty<SBM_ItemState>()).Any(s => s.MatAnimJoint != null);
            var mainOld = solid.Where(m => m.joint == joint).Select(m => m.dobj).ToHashSet();
            if (animated && made.Count > mainOld.Count)
                throw new Exception($"the {name.ToLowerInvariant()} model has {made.Count} textures, but the {name.ToLowerInvariant()} has room for {mainOld.Count}; combine its textures into {mainOld.Count}");

            // the main joint: kept meshes stay in their places, the new ones take the old solid ones' places (extra
            // ones go after); solid meshes on other joints become empty placeholders
            var placeholders = new HashSet<HSD_DOBJ>(solid.Where(m => m.joint != joint).Select(m => m.dobj));
            for (int j = 0; j < sk.Joints.Count; j++)
            {
                var jo = sk.Joints[j];
                if (jo.Dobj == null) continue;
                var list = jo.Dobj.List;
                var next = new List<HSD_DOBJ>();
                int k = 0;
                foreach (var d in list)
                {
                    if (j == joint && mainOld.Contains(d))
                        next.Add(k < made.Count ? made[k++] : Empty());
                    else if (placeholders.Contains(d))
                        next.Add(Empty());
                    else
                        next.Add(d);
                }
                if (j == joint)
                    while (k < made.Count)
                        next.Add(made[k++]);
                for (int i = 0; i < next.Count; i++)
                    next[i].Next = i + 1 < next.Count ? next[i + 1] : null;
                jo.Dobj = next[0];
            }

            // the joint draws solid (and see-through, for textures with holes) meshes, lit
            var mainJoint = sk.Joints[joint];
            mainJoint.Flags |= JOBJ_FLAG.OPA | JOBJ_FLAG.LIGHTING | (anyAlpha ? JOBJ_FLAG.XLU : 0);
            root.Flags |= JOBJ_FLAG.ROOT_OPA | (anyAlpha ? JOBJ_FLAG.ROOT_XLU : 0);

            int tris = groups.Sum(g => g.Count());
            said.Add(FormattableString.Invariant($"{name.ToLowerInvariant()}: new model, {tris} triangles in {made.Count} mesh(es), {(longShape ? "laid along the old one" : "kept upright")}, scaled {scale:0.###}"));
            int old = solid.Sum(m => TriangleCount(m.dobj));
            if (tris > Math.Max(3000, old * 4))
                said.Add($"warning: the {name.ToLowerInvariant()} model has {tris} triangles (the original {old}); several on screen at once can slow the game down, so a simpler one is safer");
            return said;
        }

        /// <summary>A mesh slot with nothing in it, like the costumes' dummies.</summary>
        static HSD_DOBJ Empty() => new HSD_DOBJ
        {
            Mobj = new HSD_MOBJ
            {
                RenderFlags = RENDER_MODE.CONSTANT,
                Material = new HSD_Material { DiffuseColor = System.Drawing.Color.White, AmbientColor = System.Drawing.Color.White, SpecularColor = System.Drawing.Color.White, Shininess = 50, Alpha = 1 },
            }
        };

        // ---------------------------------------------------------------- colour

        static Vector3 ToHsv(Vector3 c)
        {
            float max = MathF.Max(c.X, MathF.Max(c.Y, c.Z)), min = MathF.Min(c.X, MathF.Min(c.Y, c.Z)), d = max - min;
            float h = 0;
            if (d > 1e-6f)
            {
                if (max == c.X) h = ((c.Y - c.Z) / d % 6 + 6) % 6;
                else if (max == c.Y) h = (c.Z - c.X) / d + 2;
                else h = (c.X - c.Y) / d + 4;
                h /= 6;
            }
            return new Vector3(h, max > 1e-6f ? d / max : 0, max);
        }

        static Vector3 FromHsv(Vector3 hsv)
        {
            float h = (hsv.X % 1 + 1) % 1 * 6, s = hsv.Y, v = hsv.Z;
            float c = v * s, x = c * (1 - MathF.Abs(h % 2 - 1)), m = v - c;
            var rgb = (int)h switch
            {
                0 => new Vector3(c, x, 0),
                1 => new Vector3(x, c, 0),
                2 => new Vector3(0, c, x),
                3 => new Vector3(0, x, c),
                4 => new Vector3(x, 0, c),
                _ => new Vector3(c, 0, x),
            };
            return rgb + new Vector3(m);
        }

        /// <summary>
        /// Turns the projectile's colour to the one given: every coloured thing in it (vertex colours, material
        /// colours, texture pixels) takes the new hue, with its saturation and brightness moved by as much as the
        /// new colour differs from the projectile's main colour. White and grey stay as they are, so a laser keeps
        /// its white core and an egg its white shell.
        /// </summary>
        static string Recolour(SBM_Article art, Vector3 colour, string name)
        {
            var root = art.Model.RootModelJoint;
            // the main colour: the most colourful things, weighted by how much of them there is
            var samples = new List<(Vector3 hsv, float weight)>();
            var dobjs = root.TreeList.Where(j => j.Dobj != null).SelectMany(j => j.Dobj.List).Where(d => d.Pobj != null).ToList();
            foreach (var d in dobjs)
            {
                var m = d.Mobj;
                var tobjs = m?.Textures?.List ?? new List<HSD_TOBJ>();
                if (d.Pobj.HasAttribute(GXAttribName.GX_VA_CLR0))
                    foreach (var v in d.Pobj.List.SelectMany(p => p.ToDisplayList().Vertices))
                        samples.Add((ToHsv(new Vector3(v.CLR0.R, v.CLR0.G, v.CLR0.B)), 1));
                else if (tobjs.Count == 0 && m?.Material != null)
                    samples.Add((ToHsv(Rgb(m.Material.DiffuseColor)), TriangleCount(d)));
                foreach (var t in tobjs.Where(t => t.ImageData != null))
                {
                    var px = t.GetDecodedImageData();
                    for (int i = 0; i < px.Length; i += 16)
                        samples.Add((ToHsv(new Vector3(px[i + 2], px[i + 1], px[i]) / 255f), 0.25f));
                }
            }
            var coloured = samples.Where(s => s.hsv.Y > 0.25f && s.hsv.Z > 0.15f).ToList();
            if (coloured.Count == 0)
            {
                // nothing coloured (a grey blaster): everything takes the colour, shaded by its own brightness
                coloured = samples;
            }
            // the main hue by a circular mean, weighted by saturation
            double sx = 0, sy = 0, sw = 0, ss = 0, sv = 0;
            foreach (var (hsv, w) in coloured)
            {
                double ww = w * Math.Max(hsv.Y, 0.05);
                sx += Math.Cos(hsv.X * 2 * Math.PI) * ww;
                sy += Math.Sin(hsv.X * 2 * Math.PI) * ww;
                ss += hsv.Y * ww;
                sv += hsv.Z * ww;
                sw += ww;
            }
            var main = sw > 0 ? new Vector3((float)((Math.Atan2(sy, sx) / (2 * Math.PI) + 1) % 1), (float)(ss / sw), (float)(sv / sw)) : new Vector3(0, 0, 1);
            var want = ToHsv(colour);
            bool grey = main.Y < 0.25f;
            float sMul = main.Y > 0.05f ? want.Y / main.Y : 1, vMul = main.Z > 0.05f ? want.Z / main.Z : 1;

            Vector3 Turn(Vector3 rgb)
            {
                var hsv = ToHsv(rgb);
                // whites and greys stay, unless the whole thing is grey
                float weight = grey ? 1 : Math.Clamp((hsv.Y - 0.08f) / 0.17f, 0, 1);
                if (weight <= 0) return rgb;
                var turned = grey
                    ? FromHsv(new Vector3(want.X, want.Y, Math.Clamp(hsv.Z * vMul, 0, 1)))
                    : FromHsv(new Vector3(want.X, Math.Clamp(hsv.Y * sMul, 0, 1), Math.Clamp(hsv.Z * vMul, 0, 1)));
                return Vector3.Lerp(rgb, turned, weight);
            }

            int verts = 0, mats = 0, texs = 0;
            foreach (var d in dobjs)
            {
                var m = d.Mobj;
                if (m?.Material != null)
                {
                    m.Material.DiffuseColor = Colour(Turn(Rgb(m.Material.DiffuseColor)), m.Material.DiffuseColor.A);
                    m.Material.AmbientColor = Colour(Turn(Rgb(m.Material.AmbientColor)), m.Material.AmbientColor.A);
                    mats++;
                }
                if (d.Pobj.HasAttribute(GXAttribName.GX_VA_CLR0))
                {
                    // the colours live in the mesh's vertex data: build each part again with the new ones
                    var gen = new POBJ_Generator { UseTriangleStrips = false, VertexColorFormat = (GXCompType)GXCompTypeClr.RGBA8 };
                    HSD_POBJ first = null, last = null;
                    var built = new List<(HSD_POBJ from, HSD_POBJ to)>();
                    for (var pobj = d.Pobj; pobj != null; pobj = pobj.Next)
                    {
                        var names = pobj.ToGXAttributes().Select(a => a.AttributeName).Where(n => n != GXAttribName.GX_VA_NULL).ToArray();
                        var tris = Weapons.Triangles(pobj).Select(v =>
                        {
                            var c = Turn(new Vector3(v.CLR0.R, v.CLR0.G, v.CLR0.B));
                            v.CLR0 = new GXColor4(c.X, c.Y, c.Z, v.CLR0.A);
                            verts++;
                            return v;
                        }).ToList();
                        built.Add((pobj, gen.CreatePOBJsFromTriangleList(tris, names, null, null)));
                    }
                    gen.SaveChanges();
                    foreach (var (from, to) in built)
                        for (var q = to; q != null; q = q.Next)
                        {
                            q.Flags = from.Flags;
                            if (first == null) first = q; else last.Next = q;
                            last = q;
                        }
                    d.Pobj = first;
                }
            }

            // textures: the model's own and the ones its animations swap in (Pikachu's thunder flickers through
            // several). Palette textures get their palette turned, the rest their pixels
            // each is copied first, so a texture shared with something else (Samus's charge shot and missile) only
            // changes here
            var images = new Dictionary<HSDStruct, HSD_Image>();
            var tluts = new Dictionary<HSDStruct, HSD_Tlut>();
            HSD_Image Own(HSD_Image i)
            {
                if (i == null) return null;
                if (!images.TryGetValue(i._s, out var c))
                {
                    c = new HSD_Image { ImageData = (byte[])i.ImageData.Clone(), Width = i.Width, Height = i.Height, Format = i.Format, MipMap = i.MipMap, MinLOD = i.MinLOD, MaxLOD = i.MaxLOD };
                    images[i._s] = c;
                    images[c._s] = c;
                }
                return c;
            }
            HSD_Tlut OwnTlut(HSD_Tlut t)
            {
                if (t == null) return null;
                if (!tluts.TryGetValue(t._s, out var c))
                {
                    c = new HSD_Tlut { TlutData = (byte[])t.TlutData.Clone(), Format = t.Format, GXTlut = t.GXTlut, ColorCount = t.ColorCount };
                    tluts[t._s] = c;
                    tluts[c._s] = c;
                }
                return c;
            }
            foreach (var t in dobjs.SelectMany(d => d.Mobj?.Textures?.List ?? new List<HSD_TOBJ>()))
            {
                if (t.ImageData != null) t.ImageData = Own(t.ImageData);
                if (t.TlutData != null) t.TlutData = OwnTlut(t.TlutData);
            }
            foreach (var state in art.ItemState?.Array ?? Array.Empty<SBM_ItemState>())
                foreach (var mj in state.MatAnimJoint?.TreeList ?? new List<HSDRaw.Common.Animation.HSD_MatAnimJoint>())
                    foreach (var ma in mj.MaterialAnimation?.List ?? new List<HSDRaw.Common.Animation.HSD_MatAnim>())
                        foreach (var ta in ma.TextureAnimation?.List ?? new List<HSDRaw.Common.Animation.HSD_TexAnim>())
                        {
                            foreach (var b in ta.ImageBuffers?.Array ?? Array.Empty<HSDRaw.Common.Animation.HSD_TexBuffer>())
                                if (b.Data != null) b.Data = Own(b.Data);
                            foreach (var b in ta.TlutBuffers?.Array ?? Array.Empty<HSDRaw.Common.Animation.HSD_TlutBuffer>())
                                if (b.Data != null) b.Data = OwnTlut(b.Data);
                        }
            var doneS = new HashSet<HSDStruct>();
            foreach (var tl in tluts.Values)
            {
                if (!doneS.Add(tl._s)) continue;
                TurnPalette(tl, Turn);
                texs++;
            }
            foreach (var img in images.Values)
            {
                if (!doneS.Add(img._s) || IsPalette(img.Format)) continue;
                if (img.MipMap > 1)
                    continue; // none of the projectiles has these; their smaller copies would need turning too
                var px = GXImageConverter.DecodeTPL(img.Format, img.Width, img.Height, img.ImageData);
                for (int i = 0; i < px.Length; i += 4)
                {
                    var c = Turn(new Vector3(px[i + 2], px[i + 1], px[i]) / 255f);
                    px[i + 2] = (byte)Math.Clamp(c.X * 255 + 0.5f, 0, 255);
                    px[i + 1] = (byte)Math.Clamp(c.Y * 255 + 0.5f, 0, 255);
                    px[i] = (byte)Math.Clamp(c.Z * 255 + 0.5f, 0, 255);
                }
                img.ImageData = GXImageConverter.EncodeImage(px, img.Width, img.Height, img.Format, GXTlutFmt.IA8, out _);
                texs++;
            }
            return FormattableString.Invariant($"{name.ToLowerInvariant()}: colour #{Hex(colour)} (its main colour was #{Hex(FromHsv(main))}): {verts} vertex colours, {mats} materials, {texs} textures");
        }

        static bool IsPalette(GXTexFmt f) => f == GXTexFmt.CI4 || f == GXTexFmt.CI8 || f == GXTexFmt.CI14X2;

        /// <summary>Turns each colour of a palette (RGB565, RGB5A3 or IA8; grey IA8 palettes stay grey).</summary>
        static void TurnPalette(HSD_Tlut tlut, Func<Vector3, Vector3> turn)
        {
            var d = tlut.TlutData;
            if (d == null || tlut.Format == GXTlutFmt.IA8) return;
            for (int i = 0; i + 1 < d.Length; i += 2)
            {
                int v = d[i] << 8 | d[i + 1];
                Vector3 c;
                int a = -1;
                if (tlut.Format == GXTlutFmt.RGB565)
                    c = new Vector3((v >> 11) & 31, (v >> 5) & 63, v & 31) / new Vector3(31, 63, 31);
                else if ((v & 0x8000) != 0)
                    c = new Vector3((v >> 10) & 31, (v >> 5) & 31, v & 31) / 31f;
                else
                {
                    a = (v >> 12) & 7;
                    c = new Vector3((v >> 8) & 15, (v >> 4) & 15, v & 15) / 15f;
                }
                c = Vector3.Clamp(turn(c), Vector3.Zero, Vector3.One);
                if (tlut.Format == GXTlutFmt.RGB565)
                    v = (int)MathF.Round(c.X * 31) << 11 | (int)MathF.Round(c.Y * 63) << 5 | (int)MathF.Round(c.Z * 31);
                else if (a < 0)
                    v = 0x8000 | (int)MathF.Round(c.X * 31) << 10 | (int)MathF.Round(c.Y * 31) << 5 | (int)MathF.Round(c.Z * 31);
                else
                    v = a << 12 | (int)MathF.Round(c.X * 15) << 8 | (int)MathF.Round(c.Y * 15) << 4 | (int)MathF.Round(c.Z * 15);
                d[i] = (byte)(v >> 8);
                d[i + 1] = (byte)v;
            }
            tlut.TlutData = d;
        }

        static Vector3 Rgb(System.Drawing.Color c) => new Vector3(c.R, c.G, c.B) / 255f;

        static System.Drawing.Color Colour(Vector3 c, byte a) => System.Drawing.Color.FromArgb(a,
            (int)Math.Clamp(c.X * 255 + 0.5f, 0, 255), (int)Math.Clamp(c.Y * 255 + 0.5f, 0, 255), (int)Math.Clamp(c.Z * 255 + 0.5f, 0, 255));

        static string Hex(Vector3 c) => $"{(int)Math.Clamp(c.X * 255 + 0.5f, 0, 255):x2}{(int)Math.Clamp(c.Y * 255 + 0.5f, 0, 255):x2}{(int)Math.Clamp(c.Z * 255 + 0.5f, 0, 255):x2}";

        // ---------------------------------------------------------------- check

        /// <summary>
        /// Walks both files from their roots and lists every difference, except inside the changed articles'
        /// models. Reading the file back like this also proves the new models load.
        /// </summary>
        static List<string> Compare(HSDRawFile a, HSDRawFile b, HashSet<int> skip)
        {
            var problems = new List<string>();
            if (a.Roots.Count != b.Roots.Count)
                problems.Add($"{a.Roots.Count} roots became {b.Roots.Count}");
            var skipA = new HashSet<HSDStruct>();
            var skipB = new HashSet<HSDStruct>();
            var artsA = Articles(Data(a));
            var artsB = Articles(Data(b));
            // a changed projectile's model, and its material animations (they swap in its textures, which turn with its colour)
            static IEnumerable<HSDStruct> Drawn(SBM_Article art)
            {
                if (art?.Model?.RootModelJoint != null) yield return art.Model.RootModelJoint._s;
                foreach (var st in art?.ItemState?.Array ?? Array.Empty<SBM_ItemState>())
                    if (st.MatAnimJoint != null) yield return st.MatAnimJoint._s;
            }
            foreach (var i in skip)
            {
                skipA.UnionWith(Drawn(artsA[i]));
                if (i < artsB.Length) skipB.UnionWith(Drawn(artsB[i]));
            }
            var seen = new HashSet<HSDStruct>();
            void Walk(HSDStruct x, HSDStruct y, string path)
            {
                if (problems.Count > 20) return;
                if (x == null || y == null)
                {
                    if (x != y) problems.Add($"{path}: a pointer appeared or vanished");
                    return;
                }
                if (skipA.Contains(x) || skipB.Contains(y)) return;
                if (!seen.Add(x)) return;
                var dx = x.GetData();
                var dy = y.GetData();
                // a struct's size is read as the gap to the next one, so the same struct can read longer or
                // shorter when the file is laid out again: only zeros may differ past the shorter one
                int n = Math.Min(dx.Length, dy.Length);
                var longer = dx.Length > n ? dx : dy;
                for (int i = n; i < longer.Length; i++)
                    if (longer[i] != 0)
                    {
                        problems.Add($"{path}: size {dx.Length} became {dy.Length}");
                        return;
                    }
                for (int i = 0; i < n; i++)
                    if (dx[i] != dy[i] && !x.References.ContainsKey(i & ~3))
                    {
                        problems.Add($"{path}: byte 0x{i:X} changed");
                        break;
                    }
                var keys = x.References.Keys.Union(y.References.Keys).OrderBy(k => k);
                foreach (var k in keys)
                    Walk(x.References.GetValueOrDefault(k), y.References.GetValueOrDefault(k), $"{path}+0x{k:X}");
            }
            for (int r = 0; r < Math.Min(a.Roots.Count, b.Roots.Count); r++)
                Walk(a.Roots[r].Data._s, b.Roots[r].Data._s, a.Roots[r].Name);
            return problems;
        }

        // ---------------------------------------------------------------- pictures

        class Tex { public int W, H; public byte[] Bgra; }

        class PTri
        {
            public Vector3[] P = new Vector3[3];
            public Vector2[] UV = new Vector2[3];
            public Vector4[] C = new Vector4[3];
            public Tex Tex;
            public bool Lit, SeeThrough, Add;
        }

        /// <summary>Where a mesh vertex sits in the projectile's bind pose.</summary>
        static Vector3 World(Skeleton sk, HSD_POBJ pobj, int joint, GX_Vertex v)
        {
            var p = new Vector3(v.POS.X, v.POS.Y, v.POS.Z);
            var envs = pobj.EnvelopeWeights;
            if (envs != null && pobj.HasAttribute(GXAttribName.GX_VA_PNMTXIDX))
            {
                var e = envs[Math.Min(v.PNMTXIDX / 3, envs.Length - 1)];
                // several bones: already in place; one: relative to it
                int bone = sk.IndexOf(e.JOBJs[0]);
                return e.EnvelopeCount > 1 || bone < 0 ? p : Vector3.Transform(p, sk.World[bone]);
            }
            int owner = pobj.SingleBoundJOBJ != null ? sk.IndexOf(pobj.SingleBoundJOBJ) : joint;
            return Vector3.Transform(p, sk.World[owner < 0 ? joint : owner]);
        }

        static List<PTri> Decode(HSD_JOBJ root)
        {
            var sk = new Skeleton(root);
            var tris = new List<PTri>();
            var cache = new Dictionary<HSD_TOBJ, Tex>();
            for (int j = 0; j < sk.Joints.Count; j++)
            {
                var jo = sk.Joints[j];
                if (jo.Dobj == null) continue;
                foreach (var d in jo.Dobj.List)
                {
                    if (d.Pobj == null) continue;
                    var m = d.Mobj;
                    var mat = m?.Material;
                    var baseCol = mat != null ? new Vector4(Rgb(mat.DiffuseColor), mat.Alpha) : Vector4.One;
                    bool vertexColour = m != null && m.RenderFlags.HasFlag(RENDER_MODE.VERTEX);
                    Tex tex = null;
                    var tobj = m?.Textures;
                    if (tobj?.ImageData != null && !cache.TryGetValue(tobj, out tex))
                        cache[tobj] = tex = new Tex { W = tobj.ImageData.Width, H = tobj.ImageData.Height, Bgra = tobj.GetDecodedImageData() };
                    bool see = SeeThrough(d);
                    // the laser's glow layers add light rather than cover what's behind
                    bool add = see && m?.PEDesc != null;
                    foreach (var pobj in d.Pobj.List)
                    {
                        var vs = Weapons.Triangles(pobj).ToList();
                        for (int i = 0; i + 2 < vs.Count; i += 3)
                        {
                            var t = new PTri { Tex = tex, Lit = m == null || !m.RenderFlags.HasFlag(RENDER_MODE.CONSTANT), SeeThrough = see, Add = add };
                            for (int k = 0; k < 3; k++)
                            {
                                var v = vs[i + k];
                                var p = new Vector3(v.POS.X, v.POS.Y, v.POS.Z);
                                t.P[k] = World(sk, pobj, j, v);
                                t.UV[k] = new Vector2(v.TEX0.X, v.TEX0.Y);
                                var c = baseCol;
                                if (vertexColour && pobj.HasAttribute(GXAttribName.GX_VA_CLR0))
                                    c = new Vector4(v.CLR0.R, v.CLR0.G, v.CLR0.B, v.CLR0.A) * new Vector4(1, 1, 1, baseCol.W);
                                t.C[k] = c;
                            }
                            tris.Add(t);
                        }
                    }
                }
            }
            return tris;
        }

        static Bitmap Render(List<PTri> tris, float yaw, int size, (Vector3 centre, float radius) frame)
        {
            var img = new Bitmap(size, size);
            var depth = new float[size * size];
            Array.Fill(depth, float.MinValue);
            var view = Matrix4x4.CreateTranslation(-frame.centre) * Matrix4x4.CreateRotationY(yaw * MathF.PI / 180) * Matrix4x4.CreateRotationX(15 * MathF.PI / 180);
            float scale = size * 0.42f / Math.Max(frame.radius, 1e-4f);
            var light = Vector3.Normalize(new Vector3(0.4f, 0.6f, 1f));
            Vector2 S(Vector3 p) => new(size / 2f + p.X * scale, size / 2f - p.Y * scale);
            static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

            var placed = tris.Select(t => (t, v: t.P.Select(p => Vector3.Transform(p, view)).ToArray())).ToList();
            // solid first with depth, then see-through ones back to front over them
            var order = placed.Where(x => !x.t.SeeThrough)
                .Concat(placed.Where(x => x.t.SeeThrough).OrderBy(x => x.v.Average(p => p.Z)));
            foreach (var (t, v) in order)
            {
                var nrm = Vector3.Cross(v[1] - v[0], v[2] - v[0]);
                if (nrm.LengthSquared() < 1e-14f) continue;
                nrm = Vector3.Normalize(nrm);
                float shade = t.Lit ? 0.45f + 0.55f * MathF.Abs(Vector3.Dot(nrm, light)) : 1;
                var s = v.Select(S).ToArray();
                float area = Edge(s[0], s[1], s[2]);
                if (MathF.Abs(area) < 1e-6f) continue;
                int minX = Math.Max(0, (int)MathF.Floor(s.Min(q => q.X))), maxX = Math.Min(size - 1, (int)MathF.Ceiling(s.Max(q => q.X)));
                int minY = Math.Max(0, (int)MathF.Floor(s.Min(q => q.Y))), maxY = Math.Min(size - 1, (int)MathF.Ceiling(s.Max(q => q.Y)));
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        var p = new Vector2(x + 0.5f, y + 0.5f);
                        float w0 = Edge(s[1], s[2], p) / area, w1 = Edge(s[2], s[0], p) / area, w2 = Edge(s[0], s[1], p) / area;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        float z = w0 * v[0].Z + w1 * v[1].Z + w2 * v[2].Z;
                        int idx = y * size + x;
                        if (z <= depth[idx]) continue;
                        var c = w0 * t.C[0] + w1 * t.C[1] + w2 * t.C[2];
                        if (t.Tex != null)
                        {
                            var uv = w0 * t.UV[0] + w1 * t.UV[1] + w2 * t.UV[2];
                            float u = uv.X - MathF.Floor(uv.X), vv = uv.Y - MathF.Floor(uv.Y);
                            int tx = Math.Clamp((int)(u * t.Tex.W), 0, t.Tex.W - 1), ty = Math.Clamp((int)(vv * t.Tex.H), 0, t.Tex.H - 1);
                            int ti = (ty * t.Tex.W + tx) * 4;
                            c *= new Vector4(t.Tex.Bgra[ti + 2], t.Tex.Bgra[ti + 1], t.Tex.Bgra[ti], t.Tex.Bgra[ti + 3]) / 255f;
                        }
                        var rgb = new Vector3(c.X, c.Y, c.Z) * shade;
                        float alpha = t.SeeThrough ? Math.Clamp(c.W, 0, 1) : 1;
                        int bi = idx * 4;
                        var under = new Vector3(img.Rgba[bi], img.Rgba[bi + 1], img.Rgba[bi + 2]) / 255f;
                        var outc = t.Add ? under + rgb * alpha : Vector3.Lerp(under, rgb, alpha);
                        img.Set(x, y, outc);
                        if (!t.SeeThrough) depth[idx] = z;
                    }
            }
            return img;
        }

        static (Vector3, float) Frame(IEnumerable<PTri> tris)
        {
            var pts = tris.SelectMany(t => t.P).ToList();
            if (pts.Count == 0) return (Vector3.Zero, 1);
            var min = pts.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            var max = pts.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            var c = (min + max) / 2;
            return (c, pts.Max(p => Vector3.Distance(p, c)));
        }

        /// <summary>One row per projectile: before front and side, after front and side, at the same scale.</summary>
        static void DrawSheet(List<(List<PTri> before, List<PTri> after)> rows, string output, int cell = 200)
        {
            bool pairs = rows.Any(r => r.after != null);
            var sheet = new Bitmap(cell * (pairs ? 4 : 2), cell * Math.Max(rows.Count, 1));
            for (int r = 0; r < rows.Count; r++)
            {
                var a = rows[r].before;
                var b = rows[r].after;
                var frame = Frame(b != null ? a.Concat(b) : a);
                sheet.Draw(Render(a, -30, cell, frame), 0, r * cell);
                sheet.Draw(Render(a, 60, cell, frame), cell, r * cell);
                if (b != null)
                {
                    sheet.Draw(Render(b, -30, cell, frame), 2 * cell, r * cell);
                    sheet.Draw(Render(b, 60, cell, frame), 3 * cell, r * cell);
                }
            }
            sheet.SavePng(output);
        }

        // ---------------------------------------------------------------- commands

        /// <summary>
        /// skintool projectiles PlYs.dat [--details] [--picture out.png]: lists the fighter's projectiles by the
        /// names --projectile takes; --details shows each one's joints, meshes and textures, --picture draws them.
        /// </summary>
        public static int List(string[] args)
        {
            var file = new HSDRawFile(args[1]);
            var data = Data(file) ?? throw new Exception($"{Path.GetFileName(args[1])} is not a fighter's data file (Pl**.dat)");
            var arts = Articles(data);
            var list = For(BoneMaps.Code(args[1]), data);
            foreach (var p in list)
                Console.WriteLine($"{p.Key,-10} {p.Name} (article {p.Article}{(p.Model ? "" : ", colour only")})");
            if (list.Count == 0)
                Console.WriteLine("no projectiles with a model (what this fighter fires is drawn by its effect file)");
            if (args.Contains("--details"))
                foreach (var p in list)
                    Details(arts[p.Article], p);
            int pi = Array.IndexOf(args, "--picture");
            if (pi >= 0)
            {
                DrawSheet(list.Select(p => (Decode(arts[p.Article].Model.RootModelJoint), (List<PTri>)null)).ToList(), args[pi + 1]);
                Console.WriteLine($"wrote {args[pi + 1]} (one row each, in the order above)");
            }
            return 0;
        }

        static void Details(SBM_Article a, Projectile p)
        {
            var root = a.Model.RootModelJoint;
            var states = a.ItemState?.Array ?? Array.Empty<SBM_ItemState>();
            Console.WriteLine($"{p.Key}: {states.Length} states, {states.Count(s => s.AnimJoint != null)} joint anims, {states.Count(s => s.MatAnimJoint != null)} material anims" +
                (a.Parameters != null ? FormattableString.Invariant($", scale {a.Parameters.ModelScale}") : ""));
            var joints = root.TreeList;
            for (int j = 0; j < joints.Count; j++)
            {
                var jo = joints[j];
                Console.WriteLine(FormattableString.Invariant($"  joint {j} {jo.Flags} at ({jo.TX:0.##},{jo.TY:0.##},{jo.TZ:0.##})"));
                if (jo.Dobj == null) continue;
                foreach (var d in jo.Dobj.List)
                {
                    var m = d.Mobj;
                    var mat = m?.Material;
                    Console.WriteLine(FormattableString.Invariant($"    mesh: {TriangleCount(d)} triangles, {m?.RenderFlags}, colour #{(mat != null ? Hex(Rgb(mat.DiffuseColor)) : "-")} alpha {mat?.Alpha:0.##}"));
                    foreach (var t in m?.Textures?.List ?? new List<HSD_TOBJ>())
                        Console.WriteLine($"      texture {t.ImageData?.Width}x{t.ImageData?.Height} {t.ImageData?.Format}");
                }
            }
        }

        /// <summary>
        /// skintool projectile PlYs.dat out/PlYs.dat --projectile egg=model.zip [--projectile-color egg=#3080ff]
        /// [--projectile-flip egg] [--projectile-pick egg=name] [--picture out.png]
        /// </summary>
        public static int Run(string[] args)
        {
            string input = args[1], output = args[2], picture = null;
            var opts = new Options();
            for (int i = 3; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new Exception($"{args[i]} needs a value");
                if (args[i] == "--picture") picture = Next();
                else if (!opts.Take(args[i], Next)) throw new Exception($"unknown option {args[i]}");
            }
            var scratch = Directory.CreateTempSubdirectory("skintool-projectile").FullName;
            try
            {
                foreach (var line in Apply(input, output, opts.Load(scratch), picture))
                    Console.WriteLine(line);
                Console.WriteLine($"wrote {output}");
            }
            finally
            {
                try { Directory.Delete(scratch, true); } catch { }
            }
            return 0;
        }

        /// <summary>The --projectile options, shared by build and projectile.</summary>
        public class Options
        {
            public readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Picks = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Vector3> Colours = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Flips = new(StringComparer.OrdinalIgnoreCase);

            public bool Any => Files.Count > 0 || Colours.Count > 0;

            static (string, string) Pair(string option, string value)
            {
                var kv = value.Split('=', 2);
                if (kv.Length != 2) throw new Exception($"{option} takes name=value");
                return (kv[0], kv[1]);
            }

            public bool Take(string option, Func<string> next)
            {
                switch (option)
                {
                    case "--projectile": { var (k, v) = Pair(option, next()); Files[k] = v; return true; }
                    case "--projectile-pick": { var (k, v) = Pair(option, next()); Picks[k] = v; return true; }
                    case "--projectile-color":
                    case "--projectile-colour": { var (k, v) = Pair(option, next()); Colours[k] = ParseColour(v); return true; }
                    case "--projectile-flip": Flips.Add(next()); return true;
                }
                return false;
            }

            public Dictionary<string, Change> Load(string scratch)
            {
                var changes = new Dictionary<string, Change>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, file) in Files)
                {
                    var path = ModelZip.Find(file, Path.Combine(scratch, "projectile-" + key), Picks.GetValueOrDefault(key));
                    var obj = ObjModel.Load(path);
                    if (obj.Triangles.Count == 0)
                        throw new Exception($"the {key} model has no triangles");
                    changes[key] = new Change { Model = obj, Flip = Flips.Contains(key) };
                }
                foreach (var (key, c) in Colours)
                    (changes.TryGetValue(key, out var ch) ? ch : changes[key] = new Change()).Colour = c;
                return changes;
            }
        }
    }
}
