using HSDRaw.Common;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Bind pose of a fighter's joint tree, in the same order as <see cref="HSD_JOBJ.TreeList"/>.
    /// Matrices use the row-vector convention (v * M), matching HSDRawViewer's OpenTK code.
    /// </summary>
    public class Skeleton
    {
        public readonly List<HSD_JOBJ> Joints;
        public readonly Matrix4x4[] World;
        public readonly int[] Parent;

        public Skeleton(HSD_JOBJ root)
        {
            Joints = root.TreeList;
            World = new Matrix4x4[Joints.Count];
            Parent = new int[Joints.Count];

            var index = new Dictionary<HSD_JOBJ, int>();
            for (int i = 0; i < Joints.Count; i++)
                index[Joints[i]] = i;

            for (int i = 0; i < Joints.Count; i++)
                Parent[i] = -1;
            for (int i = 0; i < Joints.Count; i++)
                foreach (var c in Joints[i].Children)
                    Parent[index[c]] = i;

            // TreeList is depth first, so parents always come before children
            for (int i = 0; i < Joints.Count; i++)
            {
                var j = Joints[i];
                var local = Local(j);
                if (Parent[i] == -1)
                {
                    World[i] = local;
                }
                else if (j.Flags.HasFlag(JOBJ_FLAG.MTX_SCALE_COMPENSATE))
                {
                    var ps = new Vector3(Joints[Parent[i]].SX, Joints[Parent[i]].SY, Joints[Parent[i]].SZ);
                    local = Matrix4x4.CreateScale(j.SX, j.SY, j.SZ) * Euler(j.RX, j.RY, j.RZ) *
                            Matrix4x4.CreateTranslation(new Vector3(j.TX, j.TY, j.TZ) * ps);
                    World[i] = local * Matrix4x4.CreateScale(1 / ps.X, 1 / ps.Y, 1 / ps.Z) * World[Parent[i]];
                }
                else
                {
                    World[i] = local * World[Parent[i]];
                }
            }
        }

        public int IndexOf(HSD_JOBJ j) => Joints.IndexOf(j);

        public Vector3 Position(int i) => World[i].Translation;

        public static Matrix4x4 Local(HSD_JOBJ j)
        {
            return Matrix4x4.CreateScale(j.SX, j.SY, j.SZ) *
                   Euler(j.RX, j.RY, j.RZ) *
                   Matrix4x4.CreateTranslation(j.TX, j.TY, j.TZ);
        }

        /// <summary>
        /// HSD rotation order (X then Y then Z), same as Math3D.CreateMatrix4FromEuler.
        /// </summary>
        public static Matrix4x4 Euler(float x, float y, float z)
        {
            float sx = MathF.Sin(x), cx = MathF.Cos(x);
            float sy = MathF.Sin(y), cy = MathF.Cos(y);
            float sz = MathF.Sin(z), cz = MathF.Cos(z);

            return new Matrix4x4(
                cy * cz, cy * sz, -sy, 0,
                cz * sx * sy - cx * sz, sz * sx * sy + cx * cz, sx * cy, 0,
                cz * cx * sy + sx * sz, sz * cx * sy - sx * cz, cx * cy, 0,
                0, 0, 0, 1);
        }

        public static HSD_Matrix4x3 ToHsd(Matrix4x4 m)
        {
            return new HSD_Matrix4x3()
            {
                M11 = m.M11, M21 = m.M12, M31 = m.M13,
                M12 = m.M21, M22 = m.M22, M32 = m.M23,
                M13 = m.M31, M23 = m.M32, M33 = m.M33,
                M14 = m.M41, M24 = m.M42, M34 = m.M43,
            };
        }

        public static Matrix4x4 FromHsd(HSD_Matrix4x3 m)
        {
            return new Matrix4x4(
                m.M11, m.M21, m.M31, 0,
                m.M12, m.M22, m.M32, 0,
                m.M13, m.M23, m.M33, 0,
                m.M14, m.M24, m.M34, 1);
        }

        public Matrix4x4 InverseBind(int i)
        {
            Matrix4x4.Invert(World[i], out var inv);
            return inv;
        }
    }
}
