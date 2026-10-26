using HSDRaw;
using HSDRaw.Common;
using HSDRaw.Common.Animation;
using HSDRaw.GX;
using HSDRaw.Tools;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
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

            // one mesh per texture keeps the slot count low
            var groups = model.Triangles
                .GroupBy(t => model.TextureOf(t.Material) ?? "")
                .OrderByDescending(g => g.Count())
                .ToList();

            var freeSlots = Slots.Preferred(FighterData, slotCount).Where(i => !animated.Contains(i)).ToList();
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
                    // GX culls the opposite winding from OBJ
                    foreach (var c in new[] { tri.A, tri.C, tri.B })
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
                slots[i] ??= CreateDummy();

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
            using var image = Image.Load<Bgra32>(path);

            // GX repeat wrapping needs power of two sizes, and the hardware tops out at 1024
            int w = PowerOfTwo(image.Width), h = PowerOfTwo(image.Height);
            if (w != image.Width || h != image.Height)
                image.Mutate(x => x.Resize(w, h));

            var bgra = new byte[w * h * 4];
            image.CopyPixelDataTo(bgra);

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

        private static int PowerOfTwo(int v)
        {
            int p = 8;
            while (p < v && p < 1024)
                p *= 2;
            return p;
        }

        /// <summary>
        /// Same placeholder HSDRaw makes: no geometry, a constant material and an 8x8 blank texture.
        /// </summary>
        private static HSD_DOBJ CreateDummy()
        {
            return new HSD_DOBJ()
            {
                Mobj = new HSD_MOBJ()
                {
                    RenderFlags = RENDER_MODE.CONSTANT | RENDER_MODE.TEX0,
                    Material = new HSD_Material()
                    {
                        DiffuseColor = System.Drawing.Color.White,
                        SpecularColor = System.Drawing.Color.White,
                        AmbientColor = System.Drawing.Color.White,
                        Shininess = 50
                    },
                    Textures = new HSD_TOBJ()
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
                        Blending = 1,
                        ImageData = new HSD_Image()
                        {
                            Format = GXTexFmt.I4,
                            Width = 8,
                            Height = 8,
                            ImageData = new byte[32]
                        }
                    }
                }
            };
        }
    }
}
