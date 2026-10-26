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

            const int cell = 320;
            int views = 2;
            var sheet = new Bitmap(cell * poses.Count * views, cell * dats.Count);

            for (int row = 0; row < dats.Count; row++)
            {
                var file = new HSDRawFile(dats[row]);
                var root = (HSD_JOBJ)file.Roots[0].Data;
                var sk = new Skeleton(root);
                var tris = Decode(sk);

                for (int p = 0; p < poses.Count; p++)
                    for (int v = 0; v < views; v++)
                    {
                        var img = Render(sk, tris, poses[p], v * 90f, cell);
                        sheet.Draw(img, (p * views + v) * cell, row * cell);
                    }
            }

            sheet.SavePng(output);
            Console.WriteLine($"poses: {string.Join(", ", poses.Select(p => p.Name))} (front and side each)");
            Console.WriteLine($"wrote {output}");
            return 0;
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

            for (int ji = 0; ji < sk.Joints.Count; ji++)
            {
                var joint = sk.Joints[ji];
                if (joint.Dobj == null) continue;

                foreach (var dobj in joint.Dobj.List)
                {
                    if (dobj.Pobj == null) continue;

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
                                        tris.Add(new Tri { A = conv[i], B = conv[i + 1], C = conv[i + 2], Tex = tex });
                                    break;
                                case GXPrimitiveType.TriangleStrip:
                                    for (int i = 2; i < conv.Count; i++)
                                        tris.Add(new Tri { A = conv[i - 2], B = conv[i - 1], C = conv[i], Tex = tex });
                                    break;
                                case GXPrimitiveType.Quads:
                                    for (int i = 0; i + 3 < conv.Count; i += 4)
                                    {
                                        tris.Add(new Tri { A = conv[i], B = conv[i + 1], C = conv[i + 2], Tex = tex });
                                        tris.Add(new Tri { A = conv[i + 2], B = conv[i + 3], C = conv[i], Tex = tex });
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

        static Bitmap Render(Skeleton sk, List<Tri> tris, Pose pose, float yaw, int size)
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

            var img = new Bitmap(size, size);
            var depth = new float[size * size];
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
                float shade = 0.35f + 0.65f * Math.Abs(Vector3.Dot(nrm, light));

                Vector2 S(Vector3 p) => new(center.X + p.X * scale, center.Y - p.Y * scale);
                var sa = S(a); var sb = S(b); var sc = S(c);

                int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.X, MathF.Min(sb.X, sc.X))));
                int maxX = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(sa.X, MathF.Max(sb.X, sc.X))));
                int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(sa.Y, MathF.Min(sb.Y, sc.Y))));
                int maxY = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(sa.Y, MathF.Max(sb.Y, sc.Y))));
                float area = Edge(sa, sb, sc);
                if (MathF.Abs(area) < 1e-6f) continue;

                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        var p = new Vector2(x + 0.5f, y + 0.5f);
                        float w0 = Edge(sb, sc, p) / area, w1 = Edge(sc, sa, p) / area, w2 = Edge(sa, sb, p) / area;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                        int idx = y * size + x;
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
