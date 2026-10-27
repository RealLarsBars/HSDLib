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
            foreach (var w in For(code))
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

            foreach (var w in For(code))
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

        /// <summary>The costume's own weapon mesh, unchanged but bound to its bone so it can sit with the rest.</summary>
        static Part Keep(int slot, HSD_DOBJ dobj, int joint)
        {
            var pobj = dobj.Pobj;
            var attrs = pobj.ToGXAttributes().Select(a => a.AttributeName)
                .Where(a => a != GXAttribName.GX_VA_NULL && a != GXAttribName.GX_VA_PNMTXIDX);
            return new Part
            {
                Slot = slot,
                Joint = joint,
                Mobj = dobj.Mobj,
                Vertices = dobj.Pobj.List.SelectMany(Triangles).ToList(),
                Attrs = new[] { GXAttribName.GX_VA_PNMTXIDX }.Concat(attrs).ToArray(),
                Cull = pobj.Flags & (POBJ_FLAG.CULLFRONT | POBJ_FLAG.CULLBACK),
            };
        }

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
            var source = model.Positions;
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
                        var nrm = c.Normal >= 0 ? model.Normals[c.Normal] : face;
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
