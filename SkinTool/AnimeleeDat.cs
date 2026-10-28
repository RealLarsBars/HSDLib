using HSDRaw;
using HSDRaw.Common;
using HSDRaw.GX;
using HSDRaw.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Gives a finished costume file (any skin, made by hand or by this tool) the Animelee look, without
    /// adding or moving a single mesh slot, so the game's visibility tables and the skeleton stay as they
    /// were. Materials lose the stage's lighting and their colours are flattened. Each mesh gets its
    /// outline inside its own slot: a shell drawn from one side only, whose texture coordinates all sit on
    /// a patch of the mesh's texture painted black (a patch no part of the mesh uses). A part the game
    /// hides takes its outline with it.
    /// </summary>
    public static class AnimeleeDat
    {
        const float Thickness = 0.006f;

        class Corner
        {
            public Vector3 Pos, Nrm;
            public int[] Bones;
            public float[] Weights;
            public Vector2 UV;
        }

        public static List<string> Convert(HSDRawFile file)
        {
            var report = new List<string>();
            var root = (HSD_JOBJ)file.Roots[0].Data;
            var sk = new Skeleton(root);
            var animated = AnimatedSlots(file);

            // every slot, in the game's order, with the joint it hangs on
            var slots = new List<(HSD_DOBJ Dobj, int Joint)>();
            for (int j = 0; j < sk.Joints.Count; j++)
                if (sk.Joints[j].Dobj != null)
                    foreach (var d in sk.Joints[j].Dobj.List)
                        slots.Add((d, j));

            // the meshes' triangles, posed as they're bound
            var tris = slots.Select(s => Triangles(s.Dobj, s.Joint, sk)).ToList();
            var all = tris.SelectMany(t => t).SelectMany(t => t).ToList();
            if (all.Count == 0)
                throw new Exception("this costume has no meshes");
            float height = all.Max(c => c.Pos.Y) - all.Min(c => c.Pos.Y);
            float thick = height * Thickness;

            // images can be shared between materials: each is handled once
            var images = new Dictionary<HSDStruct, Image>();
            for (int s = 0; s < slots.Count; s++)
            {
                var mobj = slots[s].Dobj.Mobj;
                if (mobj == null)
                    continue;
                mobj.RenderFlags = (mobj.RenderFlags & ~(RENDER_MODE.DIFFUSE | RENDER_MODE.SPECULAR)) | RENDER_MODE.CONSTANT;
                var tobj = mobj.Textures;
                if (tobj?.ImageData == null)
                    continue;
                if (tris[s].Count == 0)
                    continue;
                if (!images.TryGetValue(tobj.ImageData._s, out var img))
                    images[tobj.ImageData._s] = img = new Image(tobj);
                img.Users.Add(tobj);
                img.Slots.Add(s);
                if (animated.Contains(s))
                    img.Animated = true;
                foreach (var t in tris[s])
                    img.Cover(t.Select(c => c.UV).ToArray(), tobj);
            }

            int outlined = 0, flattened = 0;
            var generator = new POBJ_Generator { UseTriangleStrips = true };
            var enveloped = new HashSet<int>();
            foreach (var img in images.Values)
            {
                if (img.Animated)
                    continue;
                img.Flatten();
                flattened++;
                // a shell needs one texture it can paint a black patch on (not a cut-out, not one the game
                // swaps for blinking)
                if (img.SeeThrough || img.Slots.Any(s => (slots[s].Dobj.Mobj.Textures?.List.Count ?? 0) != 1) || !img.PaintBlack())
                    continue;
                foreach (int s in img.Slots.Distinct())
                {
                    var shell = Shell(tris[s], thick, img.Black);
                    if (shell.Count == 0)
                        continue;
                    var vertices = new List<GX_Vertex>();
                    var bones = new List<HSD_JOBJ[]>();
                    var weights = new List<float[]>();
                    foreach (var c in shell)
                    {
                        var pos = c.Pos;
                        var nrm = c.Nrm;
                        // single weights are stored relative to their bone, as SkinBuilder does
                        if (c.Bones.Length == 1)
                        {
                            var inv = sk.InverseBind(c.Bones[0]);
                            pos = Vector3.Transform(pos, inv);
                            nrm = Vector3.TransformNormal(nrm, inv);
                        }
                        nrm = nrm.LengthSquared() > 0 ? Vector3.Normalize(nrm) : Vector3.UnitY;
                        vertices.Add(new GX_Vertex
                        {
                            POS = new GXVector3(pos.X, pos.Y, pos.Z),
                            NRM = new GXVector3(nrm.X, nrm.Y, nrm.Z),
                            TEX0 = new GXVector2(c.UV.X, c.UV.Y),
                        });
                        bones.Add(c.Bones.Select(b => sk.Joints[b]).ToArray());
                        weights.Add(c.Weights);
                        enveloped.UnionWith(c.Bones);
                    }
                    var attrs = new[] { GXAttribName.GX_VA_PNMTXIDX, GXAttribName.GX_VA_POS, GXAttribName.GX_VA_NRM, GXAttribName.GX_VA_TEX0 };
                    var added = generator.CreatePOBJsFromTriangleList(vertices, attrs, bones, weights);
                    // drawn from one side only, so just its rim shows (as SkinBuilder's outline shells)
                    for (var p = added; p != null; p = p.Next)
                        p.Flags = (p.Flags & ~(POBJ_FLAG.CULLFRONT | POBJ_FLAG.CULLBACK)) | POBJ_FLAG.CULLBACK;
                    var last = slots[s].Dobj.Pobj;
                    while (last.Next != null)
                        last = last.Next;
                    last.Next = added;
                    outlined++;
                }
            }
            generator.SaveChanges();
            foreach (var b in enveloped)
                if (sk.Joints[b].InverseWorldTransform == null)
                    sk.Joints[b].InverseWorldTransform = Skeleton.ToHsd(sk.InverseBind(b));
            root.UpdateFlags();

            report.Add($"animelee: flat lighting on {slots.Count(s => s.Dobj.Mobj != null)} materials, flattened colours in {flattened} textures, outlined {outlined} meshes");
            int skipped = slots.Select((s, i) => i).Count(i => tris[i].Count > 0 && !images.Values.Any(img => img.Black != null && img.Slots.Contains(i)));
            if (skipped > 0)
                report.Add($"  {skipped} meshes have no outline (blinking eyes, cut-outs, two textures, or no free spot on their texture)");
            return report;
        }

        /// <summary>The outline shell of a mesh: pushed out along its normals, wound inwards, all on the black patch.</summary>
        static List<Corner> Shell(List<Corner[]> mesh, float thick, Vector2? black)
        {
            var shell = new List<Corner>();
            if (black == null)
                return shell;
            // one push per place, so the shell stays closed across seams
            var push = new Dictionary<(int, int, int), Vector3>();
            (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X / thick * 4), (int)MathF.Round(p.Y / thick * 4), (int)MathF.Round(p.Z / thick * 4));
            foreach (var t in mesh)
                foreach (var c in t)
                    push[Key(c.Pos)] = push.GetValueOrDefault(Key(c.Pos)) + (c.Nrm.LengthSquared() > 0 ? Vector3.Normalize(c.Nrm) : Vector3.Zero);
            Corner Out(Corner c)
            {
                var n = push[Key(c.Pos)];
                n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.Zero;
                return new Corner { Pos = c.Pos + n * thick, Nrm = -n, Bones = c.Bones, Weights = c.Weights, UV = black.Value };
            }
            foreach (var t in mesh)
            {
                var a = Out(t[0]); var b = Out(t[1]); var c = Out(t[2]);
                // wound against the way the mesh faces there
                bool outwards = Vector3.Dot(Vector3.Cross(b.Pos - a.Pos, c.Pos - a.Pos), -(a.Nrm + b.Nrm + c.Nrm)) > 0;
                shell.AddRange(outwards ? new[] { a, c, b } : new[] { a, b, c });
            }
            return shell;
        }

        /// <summary>A mesh's triangles in the bind pose, with each corner's bones.</summary>
        static List<Corner[]> Triangles(HSD_DOBJ dobj, int joint, Skeleton sk)
        {
            var tris = new List<Corner[]>();
            if (dobj.Pobj == null)
                return tris;
            foreach (var pobj in dobj.Pobj.List)
            {
                if (!pobj.HasAttribute(GXAttribName.GX_VA_POS))
                    continue;
                var dl = pobj.ToDisplayList();
                var envs = pobj.EnvelopeWeights;
                bool env = envs != null && pobj.HasAttribute(GXAttribName.GX_VA_PNMTXIDX);
                int owner = pobj.SingleBoundJOBJ != null ? sk.IndexOf(pobj.SingleBoundJOBJ) : joint;
                Corner C(GX_Vertex gv)
                {
                    var c = new Corner { Pos = new Vector3(gv.POS.X, gv.POS.Y, gv.POS.Z), Nrm = new Vector3(gv.NRM.X, gv.NRM.Y, gv.NRM.Z), UV = new Vector2(gv.TEX0.X, gv.TEX0.Y) };
                    int bound = -1;
                    if (env)
                    {
                        var e = envs[gv.PNMTXIDX / 3];
                        c.Bones = e.JOBJs.Select(sk.IndexOf).ToArray();
                        c.Weights = e.Weights.Take(c.Bones.Length).ToArray();
                        if (e.EnvelopeCount == 1)
                            bound = c.Bones[0];
                    }
                    else
                    {
                        c.Bones = new[] { owner };
                        c.Weights = new[] { 1f };
                        bound = owner;
                    }
                    if (bound >= 0)
                    {
                        c.Pos = Vector3.Transform(c.Pos, sk.World[bound]);
                        c.Nrm = Vector3.TransformNormal(c.Nrm, sk.World[bound]);
                    }
                    return c;
                }
                int offset = 0;
                foreach (var prim in dl.Primitives)
                {
                    var vs = dl.Vertices.GetRange(offset, prim.Count).Select(C).ToList();
                    offset += prim.Count;
                    switch (prim.PrimitiveType)
                    {
                        case GXPrimitiveType.Triangles:
                            for (int i = 0; i + 2 < vs.Count; i += 3)
                                tris.Add(new[] { vs[i], vs[i + 1], vs[i + 2] });
                            break;
                        case GXPrimitiveType.TriangleStrip:
                            for (int i = 2; i < vs.Count; i++)
                                tris.Add(i % 2 == 0 ? new[] { vs[i - 2], vs[i - 1], vs[i] } : new[] { vs[i - 1], vs[i - 2], vs[i] });
                            break;
                        case GXPrimitiveType.Quads:
                            for (int i = 0; i + 3 < vs.Count; i += 4)
                            {
                                tris.Add(new[] { vs[i], vs[i + 1], vs[i + 2] });
                                tris.Add(new[] { vs[i + 2], vs[i + 3], vs[i] });
                            }
                            break;
                    }
                }
            }
            return tris.Where(t => t.All(c => c.Bones.Length > 0 && c.Bones.All(b => b >= 0))).ToList();
        }

        static HashSet<int> AnimatedSlots(HSDRawFile file)
        {
            var slots = new HashSet<int>();
            var mat = file.Roots.Select(r => r.Data).OfType<HSDRaw.Common.Animation.HSD_MatAnimJoint>().FirstOrDefault();
            if (mat == null)
                return slots;
            int index = 0;
            foreach (var mj in mat.TreeList)
            {
                if (mj.MaterialAnimation == null)
                    continue;
                foreach (var ma in mj.MaterialAnimation.List)
                {
                    if (ma.TextureAnimation != null || ma.AnimationObject != null)
                        slots.Add(index);
                    index++;
                }
            }
            return slots;
        }

        /// <summary>One texture image and the materials that show it.</summary>
        class Image
        {
            public readonly List<HSD_TOBJ> Users = new();
            public readonly List<int> Slots = new();
            public bool Animated;
            public Vector2? Black;
            readonly int w, h;
            readonly byte[] bgra;
            readonly bool[] used;
            readonly GXTexFmt format;
            readonly GXTlutFmt tlut;

            public Image(HSD_TOBJ tobj)
            {
                w = tobj.ImageData.Width;
                h = tobj.ImageData.Height;
                format = tobj.ImageData.Format;
                tlut = tobj.TlutData?.Format ?? GXTlutFmt.IA8;
                bgra = tobj.GetDecodedImageData();
                used = new bool[w * h];
            }

            public bool SeeThrough
            {
                get
                {
                    int clear = 0;
                    for (int i = 3; i < bgra.Length; i += 4)
                        if (bgra[i] < 128)
                            clear++;
                    return clear > w * h / 50;
                }
            }

            /// <summary>Marks the texels a triangle covers (and two around them, for filtering).</summary>
            public void Cover(Vector2[] uv, HSD_TOBJ tobj)
            {
                // the material may scale the texture over the mesh
                var p = uv.Select(t => new Vector2(t.X * tobj.RepeatS * w, t.Y * tobj.RepeatT * h)).ToArray();
                float minX = p.Min(q => q.X), maxX = p.Max(q => q.X), minY = p.Min(q => q.Y), maxY = p.Max(q => q.Y);
                // a triangle spread over the whole texture or more: it uses all of it
                if (maxX - minX > w * 2 || maxY - minY > h * 2)
                {
                    Array.Fill(used, true);
                    return;
                }
                float area = Edge(p[0], p[1], p[2]);
                for (int y = (int)MathF.Floor(minY) - 2; y <= (int)MathF.Ceiling(maxY) + 2; y++)
                    for (int x = (int)MathF.Floor(minX) - 2; x <= (int)MathF.Ceiling(maxX) + 2; x++)
                    {
                        var q = new Vector2(x + 0.5f, y + 0.5f);
                        bool inside = MathF.Abs(area) < 1e-6f || Near(p, q, area);
                        if (inside)
                            used[Mod(y, h) * w + Mod(x, w)] = true;
                    }
            }

            static bool Near(Vector2[] p, Vector2 q, float area)
            {
                // inside, or within two texels of an edge
                float s = MathF.Sign(area);
                for (int k = 0; k < 3; k++)
                {
                    var a = p[k]; var b = p[(k + 1) % 3];
                    float len = MathF.Max((b - a).Length(), 1e-6f);
                    if (Edge(a, b, q) * s / len < -2.5f)
                        return false;
                }
                return true;
            }

            static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            static int Mod(int v, int m) => ((v % m) + m) % m;

            public void Flatten()
            {
                // Animelee.Flatten takes RGBA; BGRA only swaps two channels, which the flattening treats alike
                // but for the grey weights, so swap around it
                Swap();
                Animelee.Flatten(bgra);
                Swap();
                Write();
            }

            void Swap()
            {
                for (int i = 0; i < bgra.Length; i += 4)
                    (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            }

            /// <summary>Paints an 8x8 patch no mesh uses black, and returns false when there's none.</summary>
            public bool PaintBlack()
            {
                const int n = 8;
                for (int by = 0; by + n <= h; by += 4)
                    for (int bx = 0; bx + n <= w; bx += 4)
                    {
                        bool free = true;
                        for (int y = by; y < by + n && free; y++)
                            for (int x = bx; x < bx + n; x++)
                                if (used[y * w + x]) { free = false; break; }
                        if (!free)
                            continue;
                        for (int y = by; y < by + n; y++)
                            for (int x = bx; x < bx + n; x++)
                            {
                                int i = (y * w + x) * 4;
                                bgra[i] = bgra[i + 1] = bgra[i + 2] = 0;
                                bgra[i + 3] = 255;
                            }
                        Write();
                        // the middle of the patch, in the texture's own coordinates (before the material's repeat)
                        var tobj = Users[0];
                        Black = new Vector2((bx + n / 2f) / w / tobj.RepeatS, (by + n / 2f) / h / tobj.RepeatT);
                        return true;
                    }
                return false;
            }

            void Write()
            {
                var first = Users[0];
                first.EncodeImageData(bgra, w, h, format, tlut);
                foreach (var u in Users.Skip(1))
                {
                    u.ImageData = first.ImageData;
                    u.TlutData = first.TlutData;
                }
            }
        }
    }
}
