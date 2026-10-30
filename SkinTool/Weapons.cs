using HSDRaw;
using HSDRaw.Common;
using HSDRaw.GX;
using HSDRaw.Melee.Pl;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Weapons a fighter holds (Peach's pan, Marth's sword, the Ice Climbers' hammers). Each one is a few
    /// rigid mesh slots on a weapon bone, and the fighter's data file switches them on and off with a
    /// visibility group. A group can hold the same weapon in more than one place (a sword in the hand and
    /// in its sheath) or several weapons that take turns (Peach's club, pan and racket).
    /// </summary>
    public static class Weapons
    {
        public class Weapon
        {
            public string Key, Name;
            /// <summary>The visibility group, and which of its choices show this weapon.</summary>
            public int Group;
            public int[] Choices;
        }

        static Weapon W(string key, string name, int group, params int[] choices) => new() { Key = key, Name = name, Group = group, Choices = choices };

        // found by drawing each group's slots on its own (preview --slots)
        static readonly Dictionary<string, Weapon[]> Table = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Pe"] = new[] { W("club", "Golf club", 3, 0), W("pan", "Frying pan", 3, 1), W("racket", "Tennis racket", 3, 2) },
            ["Ms"] = new[] { W("sword", "Sword", 1, 0, 1) },
            ["Fe"] = new[] { W("sword", "Sword", 1, 0, 1) },
            ["Lk"] = new[] { W("sword", "Sword", 1, 0, 1), W("shield", "Shield", 2, 0, 1) },
            ["Cl"] = new[] { W("sword", "Sword", 1, 0, 1), W("shield", "Shield", 2, 0, 1) },
            ["Pp"] = new[] { W("hammer", "Hammer", 1, 0) },
            ["Nn"] = new[] { W("hammer", "Hammer", 1, 0) },
            ["Gn"] = new[] { W("sword", "Sword", 1, 0) },
        };

        // other shapes the fighter turns into, which the game shows instead of the body: Samus curling up
        // and her morph ball, Yoshi's egg, Kirby's stones. A skin keeps the costume's own, as it does a
        // weapon nobody uploaded, or the fighter vanishes while in them
        static readonly Dictionary<string, Weapon[]> Forms = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Ss"] = new[] { W("ball", "Morph ball", 0, 1, 2) },
            ["Ys"] = new[] { W("egg", "Egg", 0, 1) },
            ["Kb"] = new[] { W("stone", "Stone", 0, 2, 3, 4, 5, 6) },
        };

        static IEnumerable<Weapon> WithForms(string code) =>
            For(code).Concat(code != null && Forms.TryGetValue(code, out var f) ? f : Array.Empty<Weapon>());

        public static Weapon[] For(string code) => code != null && Table.TryGetValue(code, out var w) ? w : Array.Empty<Weapon>();

        /// <summary>A replacement weapon model, and whether to turn it end for end.</summary>
        public class Upload
        {
            public ObjModel Model;
            public bool Flip;
        }

        /// <summary>One mesh slot of a weapon, ready for <see cref="SkinBuilder"/>.</summary>
        public class Part
        {
            public int Slot, Joint;
            public HSD_MOBJ Mobj;          // the original material, or null to make one from Texture
            public string Texture;
            public List<GX_Vertex> Vertices = new(); // a triangle list, relative to Joint
            public GXAttribName[] Attrs;
            public POBJ_FLAG Cull;
            // the costume's own mesh (a weapon nobody replaced, Yoshi's egg) goes back in whole
            public HSD_DOBJ Original;
        }

        /// <summary>The high-detail slots of each place a group choice shows, and the low-detail ones.</summary>
        static (List<int[]> high, List<int[]> low) Places(SBM_FighterData data, Weapon w)
        {
            var table = data?.ModelLookupTables?.CostumeVisibilityLookups?.Array?.FirstOrDefault(t => t.HighPoly != null);
            int[] Entry(HSDArrayAccessor<SBM_LookupTable> t, int choice)
            {
                var groups = t?.Array;
                if (groups == null || w.Group >= groups.Length) return Array.Empty<int>();
                var entries = groups[w.Group].LookupEntries?.Array ?? Array.Empty<SBM_LookupEntry>();
                return choice < entries.Length ? entries[choice].Entries.Select(b => (int)b).ToArray() : Array.Empty<int>();
            }
            return (w.Choices.Select(c => Entry(table?.HighPoly, c)).ToList(), w.Choices.Select(c => Entry(table?.LowPoly, c)).ToList());
        }

        /// <summary>The slots of the weapon's first place (in the hand).</summary>
        public static HashSet<int> HeldSlots(SBM_FighterData data, Weapon w) => Places(data, w).high.FirstOrDefault()?.ToHashSet() ?? new HashSet<int>();

        /// <summary>Every slot that belongs to one of the fighter's weapons, high or low detail.</summary>
        public static HashSet<int> AllSlots(SBM_FighterData data, string code)
        {
            var set = new HashSet<int>();
            foreach (var w in WithForms(code))
            {
                var (high, low) = Places(data, w);
                foreach (var s in high.Concat(low))
                    set.UnionWith(s);
            }
            return set;
        }

        /// <summary>
        /// The parts that go in this costume's weapon slots: the costume's own weapon, or an uploaded one
        /// fitted over it. Low-detail slots are left out (the builder fills them with dummies, as it does
        /// for the body).
        /// </summary>
        public static List<Part> Build(HSD_JOBJ root, Skeleton sk, SBM_FighterData data, string code, Dictionary<string, Upload> uploads)
        {
            var dobjs = root.TreeList.Where(j => j.Dobj != null)
                .SelectMany(j => j.Dobj.List.Select(d => (dobj: d, joint: sk.IndexOf(j)))).ToList();
            var parts = new List<Part>();

            foreach (var w in WithForms(code))
            {
                var (high, _) = Places(data, w);
                foreach (var place in high.Where(p => p.Length > 0))
                {
                    var original = place.Where(s => s < dobjs.Count && dobjs[s].dobj.Pobj != null)
                        .Select(s => (s, dobjs[s].dobj, dobjs[s].joint)).ToList();
                    if (uploads != null && uploads.TryGetValue(w.Key, out var upload))
                        parts.AddRange(Fit(upload, w, original, sk));
                    else
                        parts.AddRange(original.Select(o => Keep(o.s, o.dobj, o.joint)));
                }
            }
            return parts;
        }

        /// <summary>The costume's own weapon mesh, which goes back unchanged in its own slot.</summary>
        static Part Keep(int slot, HSD_DOBJ dobj, int joint) => new Part { Slot = slot, Joint = joint, Original = dobj };

        /// <summary>
        /// Places an uploaded weapon where the original sits: its longest side along the original's longest
        /// side, scaled to the same length, its wide end (a pan's head, a sword's guard) at the original's
        /// wide end, and centred on it.
        /// </summary>
        static IEnumerable<Part> Fit(Upload upload, Weapon w, List<(int slot, HSD_DOBJ dobj, int joint)> original, Skeleton sk)
        {
            if (original.Count == 0)
                yield break;

            // the bone most of the original hangs on carries the new one
            var verts = original.SelectMany(o => o.dobj.Pobj.List.SelectMany(Triangles).Select(v => (v, o.joint))).ToList();
            int joint = verts.GroupBy(x => x.joint).OrderByDescending(g => g.Count()).First().Key;
            Matrix4x4.Invert(sk.World[joint], out var toJoint);
            var target = verts.Select(x =>
            {
                var p = new Vector3(x.v.POS.X, x.v.POS.Y, x.v.POS.Z);
                return x.joint == joint ? p : Vector3.Transform(Vector3.Transform(p, sk.World[x.joint]), toJoint);
            }).ToList();

            var model = upload.Model;
            // a model lying at a slant in its file (the cigarette rip lies 45 degrees across) is first turned
            // to lie along its own longest line, or its box's sides aren't its length and width
            var level = Level(model);
            var source = model.Positions.Select(p => Vector3.Transform(p, level)).ToList();
            var (tAxes, tSide, tSkew, tMin, tMax) = Shape(target);
            var (mAxes, mSide, mSkew, mMin, mMax) = Shape(source);

            // model axis mAxes[k] goes to target axis tAxes[k]
            var sign = new float[3];
            sign[0] = tSide * mSide * (upload.Flip ? -1 : 1);
            sign[1] = MathF.Abs(tSkew) > 0.05f && MathF.Abs(mSkew) > 0.05f ? MathF.Sign(tSkew) * MathF.Sign(mSkew) : 1;
            sign[2] = 1;
            var rot = new Matrix4x4();
            void Build()
            {
                rot = new Matrix4x4();
                for (int k = 0; k < 3; k++)
                    Set(ref rot, mAxes[k], tAxes[k], sign[k]);
                rot.M44 = 1;
            }
            Build();
            // a turn, never a mirror image
            if (rot.GetDeterminant() < 0)
            {
                sign[2] = -1;
                Build();
            }

            float tLen = Get(tMax - tMin, tAxes[0]), mLen = Get(mMax - mMin, mAxes[0]);
            float scale = mLen > 1e-6f ? tLen / mLen : 1;
            var mCentre = (mMin + mMax) / 2;
            var tCentre = (tMin + tMax) / 2;
            Vector3 Place(Vector3 p) => Vector3.Transform((p - mCentre) * scale, rot) + tCentre;

            var groups = model.Triangles.GroupBy(t => model.TextureOf(t.Material) ?? "").OrderByDescending(g => g.Count()).ToList();
            // the new meshes go in the place's slots that held one texture, like the body's
            var single = original.Where(o => (o.dobj.Mobj?.Textures?.List.Count ?? 0) == 1).Select(o => o.slot).ToList();
            if (single.Count == 0)
                single = original.Select(o => o.slot).ToList();
            if (groups.Count > single.Count)
                throw new Exception($"the {w.Name.ToLowerInvariant()} model has {groups.Count} textures, but the {w.Name.ToLowerInvariant()} has room for {single.Count}; combine its textures into {single.Count}");

            for (int g = 0; g < groups.Count; g++)
            {
                var part = new Part
                {
                    Slot = single[g],
                    Joint = joint,
                    Texture = groups[g].Key,
                    Attrs = new[] { GXAttribName.GX_VA_PNMTXIDX, GXAttribName.GX_VA_POS, GXAttribName.GX_VA_NRM, GXAttribName.GX_VA_TEX0 },
                };
                foreach (var tri in groups[g])
                {
                    var face = Vector3.Cross(source[tri.B.Position] - source[tri.A.Position], source[tri.C.Position] - source[tri.A.Position]);
                    foreach (var c in new[] { tri.A, tri.B, tri.C })
                    {
                        var pos = Place(source[c.Position]);
                        var nrm = c.Normal >= 0 ? Vector3.TransformNormal(model.Normals[c.Normal], level) : face;
                        nrm = Vector3.TransformNormal(nrm, rot);
                        nrm = nrm.LengthSquared() > 1e-12f ? Vector3.Normalize(nrm) : Vector3.UnitY;
                        var uv = c.UV >= 0 ? model.UVs[c.UV] : Vector2.Zero;
                        part.Vertices.Add(new GX_Vertex
                        {
                            POS = new GXVector3(pos.X, pos.Y, pos.Z),
                            NRM = new GXVector3(nrm.X, nrm.Y, nrm.Z),
                            TEX0 = new GXVector2(uv.X, 1 - uv.Y),
                        });
                    }
                }
                yield return part;
            }
        }

        /// <summary>
        /// A turn that lays the model's longest line (its surface's main axis) along a box side, or none when it
        /// already lies within 10 degrees of one (most models: then the box is already a good fit).
        /// </summary>
        static Matrix4x4 Level(ObjModel model)
        {
            // the surface's spread, each triangle by its area
            var p = model.Positions;
            double total = 0;
            var mean = Vector3.Zero;
            var tris = model.Triangles.Select(t => (a: p[t.A.Position], b: p[t.B.Position], c: p[t.C.Position]))
                .Select(t => (t.a, t.b, t.c, area: Vector3.Cross(t.b - t.a, t.c - t.a).Length() / 2)).Where(t => t.area > 0).ToList();
            foreach (var t in tris)
            {
                mean += (t.a + t.b + t.c) / 3 * t.area;
                total += t.area;
            }
            if (total <= 0)
                return Matrix4x4.Identity;
            mean /= (float)total;
            var cov = new double[3, 3];
            foreach (var t in tris)
                foreach (var v in new[] { t.a, t.b, t.c })
                {
                    var d = v - mean;
                    var dv = new double[] { d.X, d.Y, d.Z };
                    for (int i = 0; i < 3; i++)
                        for (int j = 0; j < 3; j++)
                            cov[i, j] += dv[i] * dv[j] * t.area;
                }
            var (values, vectors) = Eigen(cov);
            var order = new[] { 0, 1, 2 }.OrderByDescending(i => values[i]).ToArray();
            var axis = new Vector3[3];
            for (int k = 0; k < 3; k++)
                axis[k] = Vector3.Normalize(new Vector3((float)vectors[0, order[k]], (float)vectors[1, order[k]], (float)vectors[2, order[k]]));
            var longest = axis[0];
            if (MathF.Max(MathF.Abs(longest.X), MathF.Max(MathF.Abs(longest.Y), MathF.Abs(longest.Z))) > MathF.Cos(10 * MathF.PI / 180))
                return Matrix4x4.Identity;
            // each axis keeps its sign towards the box side it lies nearest, and the three make a turn
            for (int k = 0; k < 3; k++)
            {
                int side = Enumerable.Range(0, 3).OrderByDescending(a => MathF.Abs(Get(axis[k], a))).First();
                if (Get(axis[k], side) < 0)
                    axis[k] = -axis[k];
            }
            axis[2] = Vector3.Cross(axis[0], axis[1]);
            // row-vector matrix: axis k goes to x, y, z in turn, then the model is a box along x
            var m = new Matrix4x4(
                axis[0].X, axis[1].X, axis[2].X, 0,
                axis[0].Y, axis[1].Y, axis[2].Y, 0,
                axis[0].Z, axis[1].Z, axis[2].Z, 0,
                0, 0, 0, 1);
            return m;
        }

        /// <summary>Eigenvalues and eigenvectors (columns) of a symmetric 3x3 matrix, by Jacobi turns.</summary>
        static (double[] values, double[,] vectors) Eigen(double[,] a)
        {
            a = (double[,])a.Clone();
            var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
                if (off < 1e-15 * (Math.Abs(a[0, 0]) + Math.Abs(a[1, 1]) + Math.Abs(a[2, 2]) + 1e-30))
                    break;
                for (int p = 0; p < 2; p++)
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-30)
                            continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
            }
            return (new[] { a[0, 0], a[1, 1], a[2, 2] }, v);
        }

        /// <summary>
        /// The axes from longest to shortest, which end of the longest is wider (+1 the far end), how far
        /// the middle axis leans off centre, and the box.
        /// </summary>
        static (int[] axes, float side, float skew, Vector3 min, Vector3 max) Shape(IList<Vector3> points)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in points)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var size = max - min;
            var axes = new[] { 0, 1, 2 }.OrderByDescending(a => Get(size, a)).ToArray();

            // width across the other two axes, in the first and last 30% along the longest
            int a0 = axes[0];
            float len = Math.Max(Get(size, a0), 1e-6f);
            float Width(Func<float, bool> inBand)
            {
                var band = points.Where(p => inBand((Get(p, a0) - Get(min, a0)) / len)).ToList();
                if (band.Count == 0) return 0;
                float w1 = band.Max(p => Get(p, axes[1])) - band.Min(p => Get(p, axes[1]));
                float w2 = band.Max(p => Get(p, axes[2])) - band.Min(p => Get(p, axes[2]));
                return w1 + w2;
            }
            float near = Width(t => t <= 0.3f), far = Width(t => t >= 0.7f);
            float side = far >= near ? 1 : -1;

            int a1 = axes[1];
            float mean = points.Average(p => Get(p, a1));
            float skew = (mean - (Get(min, a1) + Get(max, a1)) / 2) / Math.Max(Get(size, a1), 1e-6f);
            return (axes, side, skew, min, max);
        }

        static float Get(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        // row-vector matrices: row `from` sends that model axis to the `to` axis
        static void Set(ref Matrix4x4 m, int from, int to, float sign)
        {
            var row = new Vector3(to == 0 ? sign : 0, to == 1 ? sign : 0, to == 2 ? sign : 0);
            switch (from)
            {
                case 0: m.M11 = row.X; m.M12 = row.Y; m.M13 = row.Z; break;
                case 1: m.M21 = row.X; m.M22 = row.Y; m.M23 = row.Z; break;
                case 2: m.M31 = row.X; m.M32 = row.Y; m.M33 = row.Z; break;
            }
        }

        /// <summary>A mesh part's triangles, as a list of three vertices each.</summary>
        public static IEnumerable<GX_Vertex> Triangles(HSD_POBJ pobj)
        {
            var dl = pobj.ToDisplayList();
            int offset = 0;
            foreach (var prim in dl.Primitives)
            {
                var vs = dl.Vertices.GetRange(offset, prim.Count);
                offset += prim.Count;
                switch (prim.PrimitiveType)
                {
                    case GXPrimitiveType.Triangles:
                        for (int i = 0; i + 2 < vs.Count; i += 3)
                        { yield return vs[i]; yield return vs[i + 1]; yield return vs[i + 2]; }
                        break;
                    case GXPrimitiveType.TriangleStrip:
                        // every other strip triangle runs the other way round
                        for (int i = 2; i < vs.Count; i++)
                        {
                            if (i % 2 == 0) { yield return vs[i - 2]; yield return vs[i - 1]; }
                            else { yield return vs[i - 1]; yield return vs[i - 2]; }
                            yield return vs[i];
                        }
                        break;
                    case GXPrimitiveType.Quads:
                        for (int i = 0; i + 3 < vs.Count; i += 4)
                        {
                            yield return vs[i]; yield return vs[i + 1]; yield return vs[i + 2];
                            yield return vs[i + 2]; yield return vs[i + 3]; yield return vs[i];
                        }
                        break;
                }
            }
        }
    }
}
