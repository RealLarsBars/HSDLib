using HSDRaw.Common;
using HSDRaw.GX;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkinTool
{
    /// <summary>
    /// Checks that a rebuilt costume only changed what is drawn. Netplay (Slippi) desyncs when
    /// a costume's joints differ from the original, since bones drive hitboxes and item positions.
    /// </summary>
    public static class Validator
    {
        // flags that change how a joint moves; the rest only affect drawing
        const JOBJ_FLAG RenderFlags =
            JOBJ_FLAG.SKELETON | JOBJ_FLAG.SKELETON_ROOT | JOBJ_FLAG.ENVELOPE_MODEL |
            JOBJ_FLAG.LIGHTING | JOBJ_FLAG.SPECULAR | JOBJ_FLAG.TEXGEN |
            JOBJ_FLAG.OPA | JOBJ_FLAG.XLU | JOBJ_FLAG.TEXEDGE |
            JOBJ_FLAG.ROOT_OPA | JOBJ_FLAG.ROOT_XLU | JOBJ_FLAG.ROOT_TEXEDGE;

        public class JointState
        {
            public float[] SRT;
            public JOBJ_FLAG MotionFlags;
        }

        public class State
        {
            public List<JointState> Joints;
            public int Slots;
        }

        public static State Snapshot(HSD_JOBJ root)
        {
            return new State
            {
                Joints = root.TreeList.Select(j => new JointState
                {
                    SRT = new[] { j.SX, j.SY, j.SZ, j.RX, j.RY, j.RZ, j.TX, j.TY, j.TZ },
                    MotionFlags = j.Flags & ~RenderFlags,
                }).ToList(),
                Slots = root.TreeList.Sum(j => j.Dobj?.List.Count ?? 0),
            };
        }

        /// <summary>Same joints, joined the same way (positions may differ).</summary>
        public static bool SameStructure(Skeleton a, Skeleton b)
        {
            return a.Joints.Count == b.Joints.Count && a.Parent.SequenceEqual(b.Parent);
        }

        public static bool SameSkeleton(Skeleton a, Skeleton b)
        {
            if (a.Joints.Count != b.Joints.Count)
                return false;
            for (int i = 0; i < a.Joints.Count; i++)
                if ((a.World[i].Translation - b.World[i].Translation).Length() > 0.001f)
                    return false;
            return true;
        }

        public static List<string> Check(State before, HSD_JOBJ after)
        {
            var problems = new List<string>();
            var now = Snapshot(after);
            var joints = after.TreeList;

            if (now.Joints.Count != before.Joints.Count)
            {
                problems.Add($"joint count changed from {before.Joints.Count} to {now.Joints.Count}");
                return problems;
            }

            for (int i = 0; i < now.Joints.Count; i++)
            {
                // exact comparison on purpose: any drift in a bone can desync netplay
                if (!now.Joints[i].SRT.SequenceEqual(before.Joints[i].SRT))
                    problems.Add($"joint {i} moved");
                if (now.Joints[i].MotionFlags != before.Joints[i].MotionFlags)
                    problems.Add($"joint {i} flags changed ({before.Joints[i].MotionFlags} to {now.Joints[i].MotionFlags})");
            }

            if (now.Slots != before.Slots)
                problems.Add($"mesh slot count changed from {before.Slots} to {now.Slots}");

            foreach (var j in joints)
            {
                if (j.Dobj == null) continue;
                foreach (var d in j.Dobj.List)
                {
                    if (d.Pobj == null) continue;
                    foreach (var p in d.Pobj.List)
                    {
                        var envs = p.EnvelopeWeights;
                        if (envs == null)
                        {
                            problems.Add("a mesh has vertices without weights");
                            continue;
                        }
                        if (envs.Length > 10)
                            problems.Add($"a mesh part uses {envs.Length} weight sets (the GameCube allows 10)");
                        foreach (var e in envs)
                        {
                            float sum = e.Weights.Sum();
                            if (Math.Abs(sum - 1) > 0.01f)
                                problems.Add($"weights add up to {sum:F3} instead of 1");
                            foreach (var b in e.JOBJs)
                                if (b.InverseWorldTransform == null)
                                    problems.Add($"joint {joints.IndexOf(b)} is weighted but has no bind matrix");
                        }

                        // every vertex must point at a real weight set
                        var dl = p.ToDisplayList();
                        if (dl.Vertices.Any(v => v.PNMTXIDX / 3 >= envs.Length))
                            problems.Add("a vertex points past its mesh's weight sets");
                    }
                }
            }

            return problems.Distinct().ToList();
        }
    }
}
