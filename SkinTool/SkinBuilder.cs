using HSDRaw;
using HSDRaw.Common;
using HSDRaw.Common.Animation;
using HSDRaw.GX;
using HSDRaw.Tools;
using StbImageSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SkinTool
{
    /// <summary>
    /// Replaces the meshes of a fighter costume file with a new model while keeping the
    /// skeleton untouched, so the costume stays safe for netplay.
    /// </summary>
    public class SkinBuilder
    {
        public class Influence
        {
            public int Bone;
            public float Weight;
        }

        private readonly HSDRawFile file;
        private readonly HSD_JOBJ root;
        private readonly Skeleton skeleton;

        // the fighter's data file, when known, to pick slots the game shows
        public HSDRaw.Melee.Pl.SBM_FighterData FighterData;

        public SkinBuilder(HSDRawFile file, Skeleton skeleton)
        {
            this.file = file;
            this.root = (HSD_JOBJ)file.Roots[0].Data;
            this.skeleton = skeleton;
        }

        /// <summary>
        /// Builds the new meshes and swaps them in.
        /// <paramref name="weights"/> holds the influences of each model position, already normalized.
        /// <paramref name="positions"/> are the fitted model positions in the fighter's bind pose.
        /// </summary>
        public void Replace(ObjModel model, Vector3[] positions, Vector3[] normals, Influence[][] weights)
        {
            int slotCount = root.TreeList.Sum(j => j.Dobj?.List.Count ?? 0);
            var animated = AnimatedSlots();

            // Some fighters' meshes have two textures or none, and the game expects each slot to keep
            // that count (modders note these per character), so dummies copy it and the new model
            // only goes into single-texture slots.
            var textureCounts = root.TreeList.Where(j => j.Dobj != null).SelectMany(j => j.Dobj.List)
                .Select(d => d.Mobj?.Textures?.List.Count ?? 0).ToArray();

            // one mesh per texture keeps the slot count low
            var groups = model.Triangles
                .GroupBy(t => model.TextureOf(t.Material) ?? "")
                .OrderByDescending(g => g.Count())
                .ToList();

            var shown = Slots.Preferred(FighterData, slotCount).Where(i => !animated.Contains(i)).ToList();
            var freeSlots = shown.Where(i => textureCounts[i] == 1).ToList();
            if (groups.Count > freeSlots.Count && shown.Count >= groups.Count)
            {
                // fighters without single-texture meshes (Game & Watch) have nowhere better to go
                Console.WriteLine($"warning: only {freeSlots.Count} single-texture slots for {groups.Count} meshes; also using slots that had other texture counts");
                freeSlots.AddRange(shown.Where(i => textureCounts[i] != 1));
            }
            if (groups.Count > freeSlots.Count)
                throw new Exception($"The model needs {groups.Count} meshes but the costume only has {freeSlots.Count} free slots.");

            var generator = new POBJ_Generator()
            {
                UseTriangleStrips = true,
            };

            var textures = new Dictionary<string, HSD_TOBJ>();
            var enveloped = new HashSet<int>();
            var real = new List<HSD_DOBJ>();

            foreach (var group in groups)
            {
                var vertices = new List<GX_Vertex>();
                var bones = new List<HSD_JOBJ[]>();
                var boneWeights = new List<float[]>();

                foreach (var tri in group)
                {
                    // same winding as OBJ: the generator's strips come out facing the way the game draws
                    // (checked against Fox's own meshes with inspect --textures)
                    foreach (var c in new[] { tri.A, tri.B, tri.C })
                    {
                        var inf = weights[c.Position];
                        var pos = positions[c.Position];
                        var nrm = c.Normal >= 0 ? normals[c.Normal] : Vector3.UnitY;

                        // single weights are stored relative to their bone
                        if (inf.Length == 1)
                        {
                            var inv = skeleton.InverseBind(inf[0].Bone);
                            pos = Vector3.Transform(pos, inv);
                            nrm = Vector3.TransformNormal(nrm, inv);
                        }
                        nrm = Vector3.Normalize(nrm);

                        var uv = c.UV >= 0 ? model.UVs[c.UV] : Vector2.Zero;

                        vertices.Add(new GX_Vertex()
                        {
                            POS = new GXVector3(pos.X, pos.Y, pos.Z),
                            NRM = new GXVector3(nrm.X, nrm.Y, nrm.Z),
                            TEX0 = new GXVector2(uv.X, 1 - uv.Y),
                        });
                        bones.Add(inf.Select(e => skeleton.Joints[e.Bone]).ToArray());
                        boneWeights.Add(inf.Select(e => e.Weight).ToArray());

                        foreach (var e in inf)
                            enveloped.Add(e.Bone);
                    }
                }

                var attrs = new[] { GXAttribName.GX_VA_PNMTXIDX, GXAttribName.GX_VA_POS, GXAttribName.GX_VA_NRM, GXAttribName.GX_VA_TEX0 };
                var dobj = new HSD_DOBJ()
                {
                    Mobj = CreateMaterial(group.Key, textures),
                    Pobj = generator.CreatePOBJsFromTriangleList(vertices, attrs, bones, boneWeights),
                };
                real.Add(dobj);
            }

            generator.SaveChanges();

            // lay out slots: real meshes first, skipping the ones the game animates (eyes),
            // and pad the rest with invisible dummies so the fighter's visibility tables still line up
            var slots = new HSD_DOBJ[slotCount];
            for (int i = 0; i < real.Count; i++)
                slots[freeSlots[i]] = real[i];
            for (int i = 0; i < slotCount; i++)
                slots[i] ??= CreateDummy(textureCounts[i]);

            foreach (var j in root.TreeList)
                j.Dobj = null;
            for (int i = 1; i < slots.Length; i++)
                slots[i - 1].Next = slots[i];
            root.Dobj = slots[0];

            // every bone a vertex blends with needs a bind matrix; existing ones are left alone
            foreach (var b in enveloped)
            {
                var j = skeleton.Joints[b];
                if (j.InverseWorldTransform == null)
                    j.InverseWorldTransform = Skeleton.ToHsd(skeleton.InverseBind(b));
            }

            root.UpdateFlags();
        }

        /// <summary>
        /// Mesh slots with a texture animation, such as blinking eyes.
        /// A real mesh placed there would get its texture swapped for an eye.
        /// </summary>
        private HashSet<int> AnimatedSlots()
        {
            var slots = new HashSet<int>();
            var mat = file.Roots.Select(r => r.Data).OfType<HSD_MatAnimJoint>().FirstOrDefault();
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

        private static HSD_MOBJ CreateMaterial(string texturePath, Dictionary<string, HSD_TOBJ> cache)
        {
            var mobj = new HSD_MOBJ()
            {
                RenderFlags = RENDER_MODE.DIFFUSE,
                Material = new HSD_Material()
                {
                    AMB_A = 0xFF,
                    AMB_R = 0x7F,
                    AMB_G = 0x7F,
                    AMB_B = 0x7F,
                    DiffuseColor = System.Drawing.Color.White,
                    SpecularColor = System.Drawing.Color.White,
                    Shininess = 50,
                    Alpha = 1
                }
            };

            if (string.IsNullOrEmpty(texturePath))
                return mobj;

            if (!cache.TryGetValue(texturePath, out var tobj))
            {
                tobj = LoadTexture(texturePath);
                cache[texturePath] = tobj;
            }
            else
            {
                tobj = HSDAccessor.DeepClone<HSD_TOBJ>(tobj);
            }

            mobj.RenderFlags |= RENDER_MODE.TEX0;
            mobj.Textures = tobj;

            if (tobj.Flags.HasFlag(TOBJ_FLAGS.ALPHAMAP_MODULATE))
                mobj.RenderFlags |= RENDER_MODE.XLU;

            return mobj;
        }

        private static HSD_TOBJ LoadTexture(string path)
        {
            ImageResult image;
            using (var stream = File.OpenRead(path))
                image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            // GX repeat wrapping needs power of two sizes, and the hardware tops out at 1024
            int w = PowerOfTwo(image.Width), h = PowerOfTwo(image.Height);
            var rgba = Resize(image.Data, image.Width, image.Height, w, h);

            var bgra = new byte[w * h * 4];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = rgba[i + 2];
                bgra[i + 1] = rgba[i + 1];
                bgra[i + 2] = rgba[i];
                bgra[i + 3] = rgba[i + 3];
            }

            bool alpha = false;
            for (int i = 3; i < bgra.Length; i += 4)
                if (bgra[i] < 250) { alpha = true; break; }

            var tobj = new HSD_TOBJ()
            {
                MagFilter = GXTexFilter.GX_LINEAR,
                Flags = TOBJ_FLAGS.COORD_UV | TOBJ_FLAGS.LIGHTMAP_DIFFUSE | TOBJ_FLAGS.COLORMAP_MODULATE,
                RepeatS = 1,
                RepeatT = 1,
                WrapS = GXWrapMode.REPEAT,
                WrapT = GXWrapMode.REPEAT,
                SX = 1,
                SY = 1,
                SZ = 1,
                GXTexGenSrc = GXTexGenSrc.GX_TG_TEX0,
                TexMapID = GXTexMapID.GX_TEXMAP0,
                Blending = 1,
            };

            if (alpha)
                tobj.Flags |= TOBJ_FLAGS.ALPHAMAP_MODULATE;

            // CMP is what the game's own costumes use; it has no smooth alpha, so cutouts use RGB5A3
            var fmt = alpha ? GXTexFmt.RGB5A3 : GXTexFmt.CMP;
            tobj.EncodeImageData(bgra, w, h, fmt, GXTlutFmt.IA8);

            return tobj;
        }

        /// <summary>
        /// Resizes 4-channel pixels one axis at a time: averages the covered pixels when shrinking
        /// (so detail doesn't alias) and blends the two nearest when growing.
        /// </summary>
        private static byte[] Resize(byte[] src, int sw, int sh, int dw, int dh)
        {
            if (sw == dw && sh == dh)
                return src;

            var pixels = new float[src.Length];
            for (int i = 0; i < src.Length; i++)
                pixels[i] = src[i];

            pixels = ResizeAxis(pixels, sw, sh, dw, true);
            pixels = ResizeAxis(pixels, dw, sh, dh, false);

            var dst = new byte[dw * dh * 4];
            for (int i = 0; i < dst.Length; i++)
                dst[i] = (byte)Math.Clamp(pixels[i] + 0.5f, 0, 255);
            return dst;
        }

        private static float[] ResizeAxis(float[] src, int w, int h, int size, bool horizontal)
        {
            int from = horizontal ? w : h;
            int lines = horizontal ? h : w;
            var dst = new float[(horizontal ? size * h : w * size) * 4];
            float step = from / (float)size;

            int Index(int line, int at) => horizontal ? (line * w + at) * 4 : (at * w + line) * 4;
            int DstIndex(int line, int at) => horizontal ? (line * size + at) * 4 : (at * w + line) * 4;

            for (int line = 0; line < lines; line++)
                for (int i = 0; i < size; i++)
                {
                    int d = DstIndex(line, i);
                    if (step > 1)
                    {
                        // box: weight each source pixel by how much of it the target pixel covers
                        float start = i * step, end = start + step, total = 0;
                        for (int s = (int)start; s < Math.Min(from, (int)MathF.Ceiling(end)); s++)
                        {
                            float cover = MathF.Min(end, s + 1) - MathF.Max(start, s);
                            int si = Index(line, s);
                            for (int c = 0; c < 4; c++)
                                dst[d + c] += src[si + c] * cover;
                            total += cover;
                        }
                        for (int c = 0; c < 4; c++)
                            dst[d + c] /= total;
                    }
                    else
                    {
                        float at = Math.Clamp((i + 0.5f) * step - 0.5f, 0, from - 1);
                        int s0 = (int)at, s1 = Math.Min(from - 1, s0 + 1);
                        float t = at - s0;
                        int i0 = Index(line, s0), i1 = Index(line, s1);
                        for (int c = 0; c < 4; c++)
                            dst[d + c] = src[i0 + c] * (1 - t) + src[i1 + c] * t;
                    }
                }

            return dst;
        }

        private static int PowerOfTwo(int v)
        {
            int p = 8;
            while (p < v && p < 1024)
                p *= 2;
            return p;
        }

        /// <summary>
        /// Same placeholder HSDRaw makes: no geometry, a constant material and an 8x8 blank texture,
        /// with as many textures as the slot had.
        /// </summary>
        private static HSD_DOBJ CreateDummy(int textures)
        {
            var mobj = new HSD_MOBJ()
            {
                RenderFlags = RENDER_MODE.CONSTANT,
                Material = new HSD_Material()
                {
                    DiffuseColor = System.Drawing.Color.White,
                    SpecularColor = System.Drawing.Color.White,
                    AmbientColor = System.Drawing.Color.White,
                    Shininess = 50
                },
            };

            HSD_TOBJ last = null;
            for (int t = 0; t < textures; t++)
            {
                var tobj = new HSD_TOBJ()
                {
                    MagFilter = GXTexFilter.GX_LINEAR,
                    Flags = TOBJ_FLAGS.COORD_UV | TOBJ_FLAGS.LIGHTMAP_DIFFUSE | TOBJ_FLAGS.COLORMAP_MODULATE | TOBJ_FLAGS.ALPHAMAP_MODULATE,
                    RepeatT = 1,
                    RepeatS = 1,
                    WrapS = GXWrapMode.CLAMP,
                    WrapT = GXWrapMode.CLAMP,
                    ColorOperation = COLORMAP.PASS,
                    AlphaOperation = ALPHAMAP.PASS,
                    SX = 1,
                    SY = 1,
                    SZ = 1,
                    GXTexGenSrc = GXTexGenSrc.GX_TG_TEX0,
                    TexMapID = GXTexMapID.GX_TEXMAP0 + t,
                    Blending = 1,
                    ImageData = new HSD_Image()
                    {
                        Format = GXTexFmt.I4,
                        Width = 8,
                        Height = 8,
                        ImageData = new byte[32]
                    }
                };
                if (last == null) mobj.Textures = tobj; else last.Next = tobj;
                last = tobj;
                mobj.RenderFlags |= RENDER_MODE.TEX0 + t * (RENDER_MODE.TEX1 - RENDER_MODE.TEX0);
            }

            return new HSD_DOBJ() { Mobj = mobj };
        }
    }
}
