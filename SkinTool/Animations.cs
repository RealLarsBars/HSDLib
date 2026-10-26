using HSDRaw;
using HSDRaw.Common.Animation;
using HSDRaw.Melee.Pl;
using HSDRaw.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SkinTool
{
    /// <summary>
    /// Reads poses from a fighter's own animations (Pl**AJ.dat), using the action table in Pl**.dat.
    /// </summary>
    public static class Animations
    {
        // animation name and how far into it to sample (0 to 1)
        public static readonly (string Name, float At)[] TestPoses =
        {
            ("Wait1", 0f),
            ("Run", 0.3f),
            ("SquatWait", 0f),
            ("AttackS4", 0.4f),
            ("AttackHi4", 0.35f),
            ("AttackAirB", 0.3f),
            ("DamageFlyHi", 0.2f),
            ("Guard", 0f),
        };

        public static List<Preview.Pose> Load(string fighterDat, string animDat, (string Name, float At)[] wanted)
        {
            var fighter = new HSDRawFile(fighterDat);
            var data = fighter.Roots.Select(r => r.Data).OfType<SBM_FighterData>().FirstOrDefault()
                ?? fighter.Roots[0].Data as SBM_FighterData
                ?? new SBM_FighterData() { _s = fighter.Roots[0].Data._s };

            var actions = data.FighterActionTable.Commands;
            var poses = new List<Preview.Pose>();
            var bytes = File.ReadAllBytes(animDat);

            foreach (var (name, at) in wanted)
            {
                var action = actions.FirstOrDefault(a => a.Name != null && a.Name.EndsWith($"_{name}_figatree"));
                if (action == null || action.AnimationSize == 0)
                    continue;

                var tree = new HSDRawFile(bytes.AsSpan(action.AnimationOffset, action.AnimationSize).ToArray()).Roots[0].Data as HSD_FigaTree;
                if (tree == null)
                    continue;

                float frame = MathF.Floor(Math.Max(0, tree.FrameCount - 1) * at);
                var pose = new Preview.Pose { Name = name };
                var nodes = tree.Nodes;
                for (int j = 0; j < nodes.Count; j++)
                {
                    foreach (var track in nodes[j].Tracks)
                    {
                        var player = new FOBJ_Player(track.ToFOBJ());
                        int slot = player.JointTrackType switch
                        {
                            JointTrackType.HSD_A_J_SCAX => 0,
                            JointTrackType.HSD_A_J_SCAY => 1,
                            JointTrackType.HSD_A_J_SCAZ => 2,
                            JointTrackType.HSD_A_J_ROTX => 3,
                            JointTrackType.HSD_A_J_ROTY => 4,
                            JointTrackType.HSD_A_J_ROTZ => 5,
                            JointTrackType.HSD_A_J_TRAX => 6,
                            JointTrackType.HSD_A_J_TRAY => 7,
                            JointTrackType.HSD_A_J_TRAZ => 8,
                            _ => -1,
                        };
                        if (slot < 0)
                            continue;

                        if (!pose.Values.TryGetValue(j, out var v))
                            pose.Values[j] = v = new float?[9];
                        v[slot] = player.GetValue(frame);
                    }
                }

                poses.Add(pose);
            }

            return poses;
        }
    }
}
