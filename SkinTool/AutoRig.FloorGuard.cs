using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    // Keeping the skin out of the floor in the fighter's low moves.
    public partial class AutoRig
    {
        /// <summary>
        /// Keeps the skin out of the floor in the fighter's low moves (a crouch, a landing, a low kick).
        /// A model shaped differently from the fighter can hang below the bones it follows: Spyro's snout
        /// sits far in front of Yoshi's head bone, so when Yoshi ducks his head to the floor the snout
        /// swings into it. A bone whose part goes lower than the fighter itself does in some pose (by more
        /// than a little) hands part of its pull to a bone further up its chain, the least that keeps the
        /// part out. The whole part changes alike, so the surface can't tear. <paramref name="lowest"/> is
        /// how low the fighter itself goes in each pose; <paramref name="usable"/> are the bones the
        /// fighter's own skin follows. A part never takes a share that squashes it in any of <paramref name="moves"/>
        /// (all the fighter's moves): a point blended between two bones that turn very differently, like a
        /// head and the hips, loses its depth and goes paper thin, which is worse than dipping into the floor.
        /// </summary>
        public void FloorGuard(FighterMesh fighter, Skeleton skeleton, List<Preview.Pose> poses, List<float> lowest, HashSet<int> usable, List<Preview.Pose> moves)
        {
            int n = Positions.Length;
            float tall = Positions.Max(p => p.Y) - Positions.Min(p => p.Y);
            var allowed = lowest.Select(l => MathF.Min(l, 0) - 0.03f * tall - Lowered).ToArray();
            var skins = poses.Select(p => Preview.PosedSkinning(skeleton, p)).ToArray();
            var all = moves.Select(p => Preview.PosedSkinning(skeleton, p)).ToArray();
            // a point's weights with bone `from` handing `share` to `to` (at most three, as everywhere else)
            SkinBuilder.Influence[] Handed(int i, int from, int to, float share)
            {
                if (share <= 0 || !Weights[i].Any(e => e.Bone == from))
                    return Weights[i];
                var sum = new Dictionary<int, float>();
                foreach (var e in Weights[i])
                {
                    sum[e.Bone] = sum.GetValueOrDefault(e.Bone) + (e.Bone == from ? e.Weight * (1 - share) : e.Weight);
                    if (e.Bone == from)
                        sum[to] = sum.GetValueOrDefault(to) + e.Weight * share;
                }
                var top = sum.Where(kv => kv.Value > 1e-4f).OrderByDescending(kv => kv.Value).Take(3).ToList();
                float total = top.Sum(kv => kv.Value);
                return top.Select(kv => new SkinBuilder.Influence { Bone = kv.Key, Weight = kv.Value / total }).ToArray();
            }
            // in how many of the moves a point goes flat
            int Flat(SkinBuilder.Influence[] w)
            {
                if (w.Length < 2)
                    return 0;
                var bones = w.Select(e => e.Bone).ToArray();
                var weights = w.Select(e => e.Weight).ToArray();
                return all.Count(m => Preview.Kept(bones, weights, m) < KeepDepth);
            }
            // each point's place in each pose under each bone, worked out once
            var at = new Dictionary<(int, int), float[]>();
            float[] Y(int i, int bone)
            {
                if (!at.TryGetValue((i, bone), out var ys))
                    at[(i, bone)] = ys = skins.Select(m => Vector3.Transform(Positions[i], m[bone]).Y).ToArray();
                return ys;
            }
            // how far a point goes below what's allowed, over all the poses, with bone `from` handing `share` to `to`
            float Depth(int i, int from = -1, int to = -1, float share = 0, float slack = 0)
            {
                float sum = 0;
                for (int p = 0; p < skins.Length; p++)
                {
                    float y = 0;
                    foreach (var e in Weights[i])
                        y += e.Bone == from ? e.Weight * ((1 - share) * Y(i, from)[p] + share * Y(i, to)[p]) : e.Weight * Y(i, e.Bone)[p];
                    sum += MathF.Max(0, allowed[p] - slack - y);
                }
                return sum;
            }

            var done = new HashSet<int>();
            // never all of it: the part still moves with its own bone some
            var steps = new[] { 0.15f, 0.3f, 0.45f, 0.6f, 0.8f, 1f };
            for (int round = 0; round < 16; round++)
            {
                var sinking = Enumerable.Range(0, n).Select(i => (i, d: Depth(i))).Where(x => x.d > 0).ToList();
                if (sinking.Count == 0)
                    break;
                // the bone pulling the most of it down
                var pull = new Dictionary<int, float>();
                foreach (var (i, d) in sinking)
                    foreach (var e in Weights[i])
                        if (!done.Contains(e.Bone))
                            pull[e.Bone] = pull.GetValueOrDefault(e.Bone) + d * e.Weight;
                if (pull.Count == 0)
                    break;
                int bone = pull.OrderByDescending(kv => kv.Value).First().Key;
                done.Add(bone);
                // an arm laid along the fighter's own arm goes where the fighter's hand goes: handing it
                // to the shoulder or the hips to keep the hand off the floor stiffens it into a T-pose
                // (Gusteau on Ganondorf); a hand brushing the floor in a crouch is the lesser evil
                if (Hung.Contains(BoneRegion[bone]))
                    continue;
                // so does an arm moved onto the fighter's arm, when the fighter's arms are short (Pikachu's) and
                // the model's reach past them: let go to the hips, Remy's arms stayed out stiff in every move.
                // A four-legged model's front legs do let go: as paws they swing down through the floor
                if (Conformed.Contains(BoneRegion[bone]) && !FourLegs)
                    continue;
                var part = Enumerable.Range(0, n).Where(i => Weights[i].Any(e => e.Bone == bone)).ToList();
                float before = part.Sum(i => Depth(i));

                // a leg or tail that reaches out past the fighter's own (a four-legged model's front legs,
                // a long tail) swings much further than the fighter's does, down through the floor in a
                // stride or a tail sweep: only the points past it let go, the further out the more, to the
                // body, or failing that to the joints that carry the whole fighter (they move with it but
                // don't bend into a crouch or swing in a stride)
                bool limb = BoneRegion[bone] != Body;
                Func<int, float> reach = _ => 1;
                var to = new List<int>();
                for (int a = skeleton.Parent[bone]; a > 0; a = skeleton.Parent[a])
                    if (usable.Contains(a) && (!limb || BoneRegion[a] == Body))
                        to.Add(a);
                if (limb)
                {
                    var theirs = Enumerable.Range(0, fighter.Vertices.Count).Where(k => FighterRegion[k] == BoneRegion[bone]).Select(k => fighter.Vertices[k].Position).ToList();
                    if (theirs.Count == 0 || to.Count == 0)
                        continue;
                    var lo = theirs.Aggregate(Vector3.Min) - new Vector3(0.02f * tall) - new Vector3(0, Lowered, 0);
                    var hi = theirs.Aggregate(Vector3.Max) + new Vector3(0.02f * tall) - new Vector3(0, Lowered, 0);
                    float ramp = 0.1f * tall;
                    reach = i => Math.Clamp((Vector3.Max(Vector3.Max(lo - Positions[i], Positions[i] - hi), Vector3.Zero)).Length() / ramp, 0, 1);
                    for (int a = skeleton.Parent[to[0]]; a > 0; a = skeleton.Parent[a])
                        to.Add(a);
                    to = to.Distinct().Take(4).ToList();
                }

                // a limb only lets go when it goes well under (feet a little wider than the fighter's
                // dipping a bit in a kick is fine, and was before)
                float slack = (limb ? 0.07f : 0.05f) * tall;
                before = part.Sum(i => Depth(i, slack: slack));
                if (before <= 0)
                    continue;
                (int To, float Share, float After) best = (-1, 0, before);
                int flatBefore = part.Sum(i => Flat(Weights[i]));
                foreach (var a in to)
                {
                    foreach (var share in limb ? new[] { 0.25f, 0.5f, 0.75f, 1f } : steps)
                    {
                        // never at the cost of flattening the part (a few more flat moments among
                        // hundreds of moves don't show). A limb's far end may flatten some in tumbles
                        // and rolls, where the limb turns right round against the joints it lets go
                        // to: that's quick and small, a head going flat in a landing isn't
                        if (part.Sum(i => Flat(Handed(i, bone, a, share * reach(i)))) > flatBefore + (limb ? 0.1f : 0.002f) * part.Count * all.Length)
                            continue;
                        float after = part.Sum(i => Depth(i, bone, a, share * reach(i), slack));
                        if (after < best.After - 1e-3f)
                            best = (a, share, after);
                        if (after <= before * 0.2f)
                            break;
                    }
                    // the nearest bone that gets it well out: it moves most like the part's own
                    if (best.After <= before * 0.5f)
                        break;
                }
                // only worth it when it gets the part well out
                if (best.To < 0 || best.After > before * 0.5f)
                    continue;
                foreach (var i in part)
                    Weights[i] = Handed(i, bone, best.To, best.Share * reach(i));
                Report += limb
                    ? $"floor: bone {bone} swung parts reaching out past the fighter's own into the floor; those follow bone {best.To} instead ({best.Share:0%} at the far end)\n"
                    : best.Share >= 1
                    ? $"floor: bone {bone} went into the floor in low moves; its part now follows bone {best.To} alone (a blend of the two would squash it flat)\n"
                    : $"floor: bone {bone} went into the floor in low moves; it now pulls {1 - best.Share:0%} and bone {best.To} the rest\n";
            }
        }
    }
}
