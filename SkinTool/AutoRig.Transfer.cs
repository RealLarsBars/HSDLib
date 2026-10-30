using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    // Copying the fighter's weights onto the fitted model, and the passes after (clothes, attach, tuck, smoothing).
    public partial class AutoRig
    {
        private void Transfer(ObjModel model, FighterMesh fighter, Options opt)
        {
            int n = Positions.Length;
            int boneCount = fighter.Vertices.Max(v => v.Bones.Max()) + 1;

            // neighbours on the model's surface
            var adj = new HashSet<int>[n];
            for (int i = 0; i < n; i++)
                adj[i] = new HashSet<int>();
            foreach (var t in model.Triangles)
            {
                int a = t.A.Position, b = t.B.Position, c = t.C.Position;
                adj[a].Add(b); adj[a].Add(c);
                adj[b].Add(a); adj[b].Add(c);
                adj[c].Add(a); adj[c].Add(b);
            }

            // positions that are welded in the source but split by UV seams still sit on top of each other
            WeldCoincident(adj);

            FindParts(adj, opt, model.Triangles);

            // 1. blend the weights of the nearest fighter vertices
            float tallest = Positions.Max(q => q.Y) - Positions.Min(q => q.Y);
            int headBone = (!float.IsNaN(NeckY) || HeadFixed) && HeadBone >= 0 ? Remap[HeadBone] : -1;
            // the bone the head hangs from
            int bodyBone = -1;
            for (int a = HeadBone >= 0 ? Skel.Parent[HeadBone] : -1; a >= 0 && bodyBone < 0; a = Skel.Parent[a])
                if (Remap[a] != headBone) bodyBone = Remap[a];
            if (bodyBone < 0) headBone = -1;
            var w = new float[n][];
            var fv = fighter.Vertices;
            var pools = Enumerable.Range(0, RegionNames.Length)
                .Select(r => Enumerable.Range(0, fv.Count).Where(k => FighterRegion[k] == r).ToArray())
                .ToArray();
            // each point only reads the fighter, so the points are shared out across the cores
            System.Threading.Tasks.Parallel.For(0, n, i =>
            {
                var p = Lookup[i];
                int region = ModelRegion[i];
                var nearest = new List<(float d, int idx)>(opt.Neighbours + 1);
                foreach (int k in pools[region])
                {
                    float d = Vector3.DistanceSquared(p, fv[k].Position);
                    if (nearest.Count < opt.Neighbours || d < nearest[^1].d)
                    {
                        int at = nearest.FindIndex(e => e.d > d);
                        if (at < 0) nearest.Add((d, k)); else nearest.Insert(at, (d, k));
                        if (nearest.Count > opt.Neighbours)
                            nearest.RemoveAt(nearest.Count - 1);
                    }
                }

                // only the region's own bones and the body's (where a limb joins it); below the neck, not
                // the head's: a model's chest can land on a fighter's big low head (Spyro's on Yoshi's), and
                // then the whole body bobs and dips with the head
                bool headless = region == Body && headBone >= 0 && BelowNeck(i, 0.04f, tallest);
                var acc = new float[boneCount];
                var any = new float[boneCount];
                foreach (var (d, k) in nearest)
                {
                    float iw = 1f / (d + 1e-4f);
                    for (int b = 0; b < fv[k].Bones.Length; b++)
                    {
                        int bone = Remap[fv[k].Bones[b]];
                        any[bone] += fv[k].Weights[b] * iw;
                        if (headless && bone == headBone)
                            bone = bodyBone;
                        if (BoneRegion[bone] == region || BoneRegion[bone] == Body)
                            acc[bone] += fv[k].Weights[b] * iw;
                    }
                }
                if (acc.Sum() <= 0)
                    acc = any;
                Normalize(acc);
                w[i] = acc;
            });

            // 1a. a hand hung along the fighter's arm (Kronk's open hand) bends its fingers with the fighter's finger
            // joints, which close into a fist or a grip: on the hand joint alone it stayed a stiff flat paddle in
            // every move. Only arms found hanging apart from the body: other skins keep their hands as they were
            if (HangingArms)
                foreach (var (region, wrist) in Wrist)
                {
                    int hand = wrist.Bone;
                    var fingers = Enumerable.Range(0, fv.Count).Where(k => fv[k].Bones.Any(b => IsAncestor(Skel, hand, b))).ToArray();
                    if (fingers.Length == 0)
                        continue;
                    for (int i = 0; i < n; i++)
                    {
                        if (ModelRegion[i] != region || ArmLookup[i] == null)
                            continue;
                        float k = FingerShare * SmoothStep(wrist.At, wrist.At + 0.5f * (1 - wrist.At), ArmU[i]);
                        if (k <= 0)
                            continue;
                        var acc = new float[boneCount];
                        foreach (int f in fingers.OrderBy(f => Vector3.DistanceSquared(Lookup[i], fv[f].Position)).Take(opt.Neighbours))
                        {
                            float iw = 1f / (Vector3.DistanceSquared(Lookup[i], fv[f].Position) + 1e-4f);
                            for (int b = 0; b < fv[f].Bones.Length; b++)
                            {
                                int bone = fv[f].Bones[b];
                                acc[bone == hand || IsAncestor(Skel, hand, bone) ? bone : Remap[bone]] += fv[f].Weights[b] * iw;
                            }
                        }
                        Normalize(acc);
                        for (int b = 0; b < boneCount; b++)
                            w[i][b] = (1 - k) * w[i][b] + k * acc[b];
                    }
                    Report += $"{RegionNames[region]}: the hand bends with the fighter's fingers\n";
                }

            // 1b. limbs that don't line up with the fighter's partly stay with the body around them
            for (int region = 0; region < LimbFollow.Length; region++)
            {
                if (LimbFollow[region] >= 1)
                    continue;
                var limb = Enumerable.Range(0, n).Where(i => ModelRegion[i] == region).ToList();
                if (limb.Count == 0)
                    continue;
                float y = limb.Average(i => Positions[i].Y);
                float h = Positions.Max(q => q.Y) - Positions.Min(q => q.Y);
                var near = Enumerable.Range(0, n).Where(i => ModelRegion[i] == Body && MathF.Abs(Positions[i].Y - y) < h * 0.1f).ToList();
                if (near.Count == 0)
                    continue;
                var around = new float[boneCount];
                foreach (var i in near)
                    for (int b = 0; b < boneCount; b++)
                        around[b] += w[i][b] / near.Count;
                float k = LimbFollow[region];
                foreach (var i in limb)
                    for (int b = 0; b < boneCount; b++)
                        w[i][b] = k * w[i][b] + (1 - k) * around[b];
            }

            // a fighter that kicks by stretching its foot bones far out (Jigglypuff): the bottom of the
            // model's legs, off to the side (not the middle of a round belly), is the foot, and follows the
            // foot bones alone, or a kick barely moves it
            bool feetOnly = opt.StretchyFeet;
            bool Foot(int i) => feetOnly && !float.IsNaN(HipY) && (ModelRegion[i] == LegLeft || ModelRegion[i] == LegRight)
                && Positions[i].Y < FloorY + (HipY - FloorY) * 0.25f && MathF.Abs(Positions[i].X - MiddleX) > HalfWidth * 0.25f;

            // 1c. no real legs: the top of each leg, and the middle where the two meet, stay mostly with the
            // hips, so a kick bends the bottom of the body instead of pulling it out into a sheet
            if (!float.IsNaN(HipY))
            {
                float span = (HipY - FloorY) * (opt.LegCut > 0 ? 0.4f : 0.6f);
                var hips = Enumerable.Range(0, n).Where(i => ModelRegion[i] == Body && Positions[i].Y < HipY + span).ToList();
                if (hips.Count > 0)
                {
                    var around = new float[boneCount];
                    foreach (var i in hips)
                        for (int b = 0; b < boneCount; b++)
                            around[b] += w[i][b] / hips.Count;
                    for (int i = 0; i < n; i++)
                    {
                        if (ModelRegion[i] != LegLeft && ModelRegion[i] != LegRight)
                            continue;
                        float k = 1 - (opt.LegCut > 0 ? 0.7f : 1f) * SmoothStep(HipY - span, HipY, Positions[i].Y);
                        if (opt.LegCut <= 0 && !Foot(i))
                        {
                            k *= SmoothStep(0, HalfWidth * 0.3f, MathF.Abs(Positions[i].X - MiddleX));
                            // the back of a round body is far behind the hips, so a leg swinging back
                            // would sweep it out into a sheet: it stays with the body
                            k *= SmoothStep(-0.6f, -0.1f, (Positions[i].Z - MiddleZ) / MathF.Max(HalfDepth, 1e-4f));
                        }
                        for (int b = 0; b < boneCount; b++)
                            w[i][b] = k * w[i][b] + (1 - k) * around[b];
                    }
                }
            }

            // 2. smooth the body across its surface so joints bend instead of tearing, then smooth the
            // body region on its own some more: a different shape bending at the fighter's exact joints
            // (waist, neck) folds like paper, a gradual blend bends like a body
            // legs pressed together (Peach's overalls and boots) are one surface down the middle; smoothing
            // across it would blend the two legs into each other, and a stride pulls the back boot out into
            // a sheet towards the front one
            bool Leg(int i, int side) => ModelRegion[i] == side;
            var sides = !LegsPressed ? adj : adj.Select((near, i) => Leg(i, LegLeft) || Leg(i, LegRight)
                ? near.Where(j => !(Leg(i, LegLeft) && Leg(j, LegRight) || Leg(i, LegRight) && Leg(j, LegLeft))).ToHashSet()
                : near).ToArray();
            // an arm hung along the fighter's arm keeps the fighter's arm weights past where it joins the body:
            // a low-poly arm (Gusteau's) is only a few points long, so smoothing reaches all the way down it
            // and mixes the body into the hand, and the arm hardly bends
            bool ArmEnd(int i) => ArmLookup[i] != null && ArmU[i] > 0.3f;
            Smooth(w, sides, boneCount, opt.SmoothPasses, i => !Foot(i) && !ArmEnd(i));
            // the head is left out: on a model with few points and a big head (Spyro) the head's weights
            // would otherwise soak down through the whole body, and it all bobs with the head
            bool belowNeck(int i) => HeadBone < 0 || BelowNeck(i, 0.04f, tallest);
            Smooth(w, adj, boneCount, opt.BodySmoothPasses, i => ModelRegion[i] == Body && belowNeck(i));

            // 2b. above the neck everything follows the head alone, blending in over a short band
            var headShare = new float[n];
            if ((!float.IsNaN(NeckY) || HeadFixed) && HeadBone >= 0)
            {
                int head = Remap[HeadBone];
                float h = Positions.Max(q => q.Y) - Positions.Min(q => q.Y);
                for (int i = 0; i < n; i++)
                {
                    // arms held out at the fighter's shoulders can reach above a low neck line (Linguini's):
                    // they are still arms
                    float neckY = NeckYAt(Positions[i]);
                    float k = Fixed?[i] is PartKind fixedAs ? (fixedAs == PartKind.Head ? 1 : 0) : SmoothStep(neckY - h * 0.04f, neckY + h * 0.02f, Positions[i].Y);
                    if (k <= 0 || ModelRegion[i] == ArmLeft || ModelRegion[i] == ArmRight) continue;
                    headShare[i] = k;
                    for (int b = 0; b < boneCount; b++)
                        w[i][b] *= 1 - k;
                    w[i][head] += k;
                }
            }

            // 2c. the model's tail follows the fighter's tail bones, root to tip
            Tail = new bool[n];
            if (opt.TailBones != null && opt.TailBones.Length > 0)
                TailWeights(w, boneCount, opt.TailBones, opt.RotateX == 0 && opt.RotateZ == 0);

            // 3. small separate parts (claws, teeth, whiskers, eyes) move as one piece with the
            // body around them, so they stay attached to it
            // the body pieces of a model built from pieces keep their shape (a box doesn't bend) and move
            // together, so a face plate can't slide over the box under it: the pieces above the neck as one
            // with the head, all others as one with the bones they follow on average, the head left out
            // (a face that dips a little below the neck line is still the head; BMO's box, a good part of it
            // below the line, is the body)
            bool aboveNeck(List<int> members) => (!float.IsNaN(NeckY) || HeadFixed) && members.Count(i => BelowNeck(i, 0, 1)) * 4 < members.Count;
            var pieces = Enumerable.Range(0, Part.Max() + 1)
                .Select(part => Enumerable.Range(0, n).Where(i => Part[i] == part).ToList())
                .Where(members => members.Count > 0 && BodyPiece[members[0]])
                .ToList();
            // the points of limb pieces (bendy, not body) touching a body piece
            float reach = (Positions.Max(q => q.Y) - Positions.Min(q => q.Y)) * 0.06f;
            var joins = Enumerable.Range(0, n).Where(i => !BodyPiece[i] && !RigidPart[i] && adj[i].Any(j => BodyPiece[j])).ToList();
            // a body piece that runs on up into the head (Dale's: neck, face and all one mesh, only the cap and
            // glasses separate) keeps its head above the neck line: it turns with the head bone like the cap on it,
            // where moving as one with the chest left the cap turning away from the face in game
            bool withHead(List<int> members) => members.Count(i => headShare[i] >= 1) * 10 >= members.Count;
            foreach (var group in pieces.GroupBy(aboveNeck))
            {
                var own = new float[boneCount];
                foreach (var i in group.SelectMany(m => m))
                    for (int b = 0; b < boneCount; b++)
                        own[b] += w[i][b];
                var keepHead = new HashSet<int>(group.Key ? Enumerable.Empty<int>() : group.Where(withHead).SelectMany(m => m));
                if (!group.Key && HeadBone >= 0)
                    own[Remap[HeadBone]] = 0;
                if (own.Sum() <= 0)
                    continue;
                Normalize(own);
                Report += $"{group.Count()} body pieces{(group.Key ? " above the neck" : "")} move as one, follow bone {Array.IndexOf(own, own.Max())}\n";
                // except where an arm or leg piece joins on: there the piece keeps the weights it shares with
                // the limb, fading into the one blend, or the limb comes away from it (Peggy's arms from her
                // shirt's armholes)
                foreach (var i in group.SelectMany(m => m))
                {
                    float k = joins.Count > 0 ? SmoothStep(0, reach, MathF.Sqrt(joins.Min(j => Vector3.DistanceSquared(Positions[i], Positions[j])))) : 1;
                    if (keepHead.Contains(i))
                        k *= 1 - headShare[i];
                    for (int b = 0; b < boneCount; b++)
                        w[i][b] = k * own[b] + (1 - k) * w[i][b];
                }
            }

            // a head modelled as a piece of its own (Linguini's, sitting in his collar) is the head, not a
            // detail on the body: it follows the head bone alone, so it turns and nods, and the hat, eyes
            // and hair sitting on it follow it, instead of all staying fixed to the collar
            if (HeadBone >= 0 && Remap[HeadBone] >= 0)
            {
                int head = Remap[HeadBone];
                float bodyTop = Enumerable.Range(0, n).Where(i => !RigidPart[i]).Select(i => Positions[i].Y).DefaultIfEmpty(float.MaxValue).Max();
                foreach (var part in Enumerable.Range(0, Part.Max() + 1))
                {
                    var members = Enumerable.Range(0, n).Where(i => Part[i] == part).ToList();
                    if (members.Count < n * 0.03f || !RigidPart[members[0]] || members.Any(i => Tail[i]))
                        continue;
                    // it sits on top of the model, above the fighter's shoulders (a big head sunk in a high
                    // collar picks up the chest's weights), not on the body's surface (Monty's eyes, with his
                    // body going on above them)
                    bool onTop = !float.IsNaN(ShoulderY) && members.Max(i => Positions[i].Y) > bodyTop
                        && members.Count(i => Positions[i].Y > ShoulderY) * 4 > members.Count * 3;
                    if (!onTop)
                        continue;
                    foreach (var i in members)
                    {
                        w[i] = new float[boneCount];
                        w[i][head] = 1;
                        RigidPart[i] = false;
                    }
                    Report += $"part {part}: {members.Count} vertices are the head, follow bone {head}\n";
                }
            }

            var body = Enumerable.Range(0, n).Where(i => !RigidPart[i]).ToList();
            foreach (var part in Enumerable.Range(0, Part.Max() + 1))
            {
                var members = Enumerable.Range(0, n).Where(i => Part[i] == part).ToList();
                if (members.Count == 0 || !RigidPart[members[0]])
                    continue;

                // a separate tail keeps its weights along the tail bones, so it bends as it swings
                if (members.Count(i => Tail[i]) * 2 > members.Count)
                {
                    Report += $"part {part}: {members.Count} vertices are the tail\n";
                    continue;
                }

                // a foot on its own (a model whose feet are separate pieces under the body) is a leg, not a
                // detail on the body: it moves as one piece with its own leg bones, so kicks reach out
                if (members.Count(i => ModelRegion[i] == LegLeft || ModelRegion[i] == LegRight) * 2 > members.Count)
                {
                    // across both legs it's not a foot but a pair of shorts or trousers (Tom Nook's): moved as one
                    // piece it stays put while the legs inside it kick out of it, so each point moves as the body
                    // right under it (its own weights, found for the clothes alone, came out ragged). Not a small detail
                    // sitting between the legs (a buckle), which stays one piece
                    int left = members.Count(i => ModelRegion[i] == LegLeft), right = members.Count(i => ModelRegion[i] == LegRight);
                    if (Math.Min(left, right) * 4 > members.Count && members.Count >= n * 0.02f)
                    {
                        var inside = body.Where(j => Part[j] != part).ToList();
                        foreach (var i in members)
                        {
                            RigidPart[i] = false;
                            if (inside.Count == 0)
                                continue;
                            var below = new float[boneCount];
                            foreach (var j in inside.OrderBy(j => Vector3.DistanceSquared(Positions[i], Positions[j])).Take(4))
                                for (int b = 0; b < boneCount; b++)
                                    below[b] += w[j][b];
                            Normalize(below);
                            w[i] = below;
                        }
                        Report += $"part {part}: {members.Count} vertices are clothes over both legs, bend with them\n";
                        continue;
                    }
                    var own = new float[boneCount];
                    foreach (var i in members)
                        for (int b = 0; b < boneCount; b++)
                            own[b] += w[i][b];
                    Normalize(own);
                    // a fighter that kicks by stretching its feet: the whole shoe goes with the foot, or half of
                    // it stays on the body and it smears out from under the body
                    if (feetOnly)
                    {
                        int foot = -1;
                        for (int b = 0; b < boneCount; b++)
                            if (BoneRegion[b] == LegLeft || BoneRegion[b] == LegRight)
                                if (foot < 0 || own[b] > own[foot]) foot = b;
                        if (foot >= 0 && own[foot] > 0)
                        {
                            own = new float[boneCount];
                            own[foot] = 1;
                        }
                    }
                    var at = members.Aggregate(Vector3.Zero, (a, i) => a + Positions[i]) / members.Count;
                    Report += $"part {part}: {members.Count} vertices around ({at.X:F1}, {at.Y:F1}, {at.Z:F1}) are a foot, follow bone {Array.IndexOf(own, own.Max())}\n";
                    foreach (var i in members)
                        w[i] = (float[])own.Clone();
                    continue;
                }

                // a hand on its own at the end of an arm laid along the fighter's arm sits in the sleeve:
                // it takes the weights of the arm around it, or it takes the belly's it hangs next to
                // (Gusteau's) and stays behind
                var sleeve = members.Count(i => Conformed.Contains(ModelRegion[i])) * 2 > members.Count
                    ? body.Where(j => ModelRegion[j] == ModelRegion[members[0]]).ToList() : null;
                // the part follows the body where it's attached: weights come from around its base (the
                // quarter of it nearest the body), not its tip, so it moves exactly as the spot it sits in
                // on a model built from pieces, a button follows the one piece it sits on (BMO's box), not an
                // arm or leg that happens to reach near it, or it sinks into the box when that limb moves
                var under = sleeve != null && sleeve.Count > 0 ? sleeve : body;
                if (sleeve == null && BodyPiece.Any(b => b) && body.Count > 0)
                {
                    int closest = body.MinBy(j => members.Min(i => Vector3.DistanceSquared(Positions[i], Positions[j])));
                    under = body.Where(j => Part[j] == Part[closest]).ToList();
                }
                float Gap(int i) => under.Count > 0 ? under.Min(j => Vector3.DistanceSquared(Positions[i], Positions[j])) : 0;
                var basePoints = members.OrderBy(Gap).Take(Math.Max(1, members.Count / 4)).ToList();
                var sum = new float[boneCount];
                foreach (var i in basePoints)
                {
                    var near = under.Count > 0
                        ? under.OrderBy(j => Vector3.DistanceSquared(Positions[i], Positions[j])).Take(4)
                        : new[] { i }.AsEnumerable();
                    foreach (var j in near)
                        for (int b = 0; b < boneCount; b++)
                            sum[b] += w[j][b];
                }

                // the whole part takes the same blend, so it keeps its shape and stays on the body
                Normalize(sum);
                var c = members.Aggregate(Vector3.Zero, (a, i) => a + Positions[i]) / members.Count;
                Report += $"part {part}: {members.Count} vertices around ({c.X:F1}, {c.Y:F1}, {c.Z:F1}) follow bone {Array.IndexOf(sum, sum.Max())}\n";
                foreach (var i in members)
                    w[i] = (float[])sum.Clone();
            }

            // 3a. clothes over the legs
            if (ClothesOver != null && ClothesOver.Length == n && opt.Clothes == "outside")
                ClothesOutside(w, boneCount, model.Triangles, fighter, adj);

            // 3b. an outline shell is a slightly bigger copy of the model around it: each of its points moves
            // exactly as the nearest point of the model, or in a pose it slides across the model and covers it
            var shell = new bool[n];
            var inModel = new bool[n];
            foreach (var t in model.Triangles)
                foreach (var c in new[] { t.A.Position, t.B.Position, t.C.Position })
                    if (t.Outline) shell[c] = true; else inModel[c] = true;
            var modelPoints = Enumerable.Range(0, n).Where(i => inModel[i]).ToArray();
            if (modelPoints.Length > 0)
                for (int i = 0; i < n; i++)
                    if (shell[i] && !inModel[i])
                    {
                        int near = modelPoints.MinBy(j => Vector3.DistanceSquared(Positions[i], Positions[j]));
                        w[i] = (float[])w[near].Clone();
                    }

            // 3d. pieces that touch (a sleeve on a shirt, a leg in a pair of shorts) take the same weights
            // where they meet, so the join can't open up in any pose; the weights around it ease into that
            if (opt.Attach)
            {
                Attach(w, adj, boneCount, model.Triangles);
                Tuck(w, boneCount, model.Triangles);
            }

            // 4. keep the strongest few influences, as the game expects
            Weights = new SkinBuilder.Influence[n][];
            for (int i = 0; i < n; i++)
            {
                var top = w[i]
                    .Select((v, b) => new SkinBuilder.Influence { Bone = b, Weight = v })
                    .OrderByDescending(e => e.Weight)
                    .Take(opt.MaxInfluences)
                    .Where(e => e.Weight >= opt.MinWeight)
                    .ToList();

                if (top.Count == 0)
                    top.Add(new SkinBuilder.Influence { Bone = Array.IndexOf(w[i], w[i].Max()), Weight = 1 });

                float s = top.Sum(e => e.Weight);
                foreach (var e in top)
                    e.Weight /= s;

                // weights are stored at limited precision; make sure they still add up to exactly 1
                for (int k = 0; k < top.Count; k++)
                    top[k].Weight = MathF.Round(top[k].Weight, 2);
                top[0].Weight += 1 - top.Sum(e => e.Weight);
                top.RemoveAll(e => e.Weight <= 0);

                Weights[i] = top.ToArray();
            }

            int rigid = RigidPart.Count(r => r);
            Report += $"{Part.Max() + 1} parts, {rigid} of {n} vertices in small parts that move as one piece\n";
            Report += $"bones used: {string.Join(",", Weights.SelectMany(x => x.Select(e => e.Bone)).Distinct().OrderBy(b => b))}\n";
        }

        /// <summary>
        /// Clothes hanging across between the legs (a coat, apron or dress) stay one sheet over them, as cloth
        /// does: from the waist down each point follows the hips less and the thighs more, the side over
        /// a leg mostly that leg and the middle both legs alike, so the sheet stretches between the legs in a
        /// stride instead of tearing down the middle. The legs under it are drawn in a little, so they stay
        /// under it rather than poking through.
        /// </summary>
        private void ClothesOutside(float[][] w, int boneCount, List<ObjModel.Triangle> triangles, FighterMesh fighter, HashSet<int>[] adj)
        {
            var (hipJoint, leftJoint, rightJoint, leftPlus) = LegJoints;
            if (hipJoint < 0 || Remap[hipJoint] < 0 || Remap[leftJoint] < 0 || Remap[rightJoint] < 0)
                return;
            int hip = Remap[hipJoint], plus = Remap[leftPlus ? leftJoint : rightJoint], minus = Remap[leftPlus ? rightJoint : leftJoint];
            int n = Positions.Length;
            // an outline shell's points go with the model point they stand off (the nearest)
            var shell = new bool[n];
            var inModel = new bool[n];
            foreach (var t in triangles)
                foreach (var c in new[] { t.A.Position, t.B.Position, t.C.Position })
                    if (t.Outline) shell[c] = true; else inModel[c] = true;
            // the clothes go on up to where the legs start (the hips, under a coat): what's above the
            // pieces hanging across, joined to them and still on a leg, is the same clothes
            var near = new List<int>[n];
            foreach (var t in triangles.Where(t => !t.Outline))
                foreach (var (u, v) in new[] { (t.A.Position, t.B.Position), (t.B.Position, t.C.Position), (t.C.Position, t.A.Position) })
                {
                    (near[u] ??= new List<int>()).Add(v);
                    (near[v] ??= new List<int>()).Add(u);
                }
            bool Leg(int i) => ModelRegion[i] == LegLeft || ModelRegion[i] == LegRight;
            float acrossTop = Enumerable.Range(0, n).Where(i => ClothesOver[i]).Select(i => Positions[i].Y).DefaultIfEmpty(float.MinValue).Max();
            float acrossHem = Enumerable.Range(0, n).Where(i => ClothesOver[i]).Select(i => Positions[i].Y).DefaultIfEmpty(float.MaxValue).Min();
            // and the rest of what hangs round the legs at those heights (a petticoat, a frill), whatever isn't
            // the legs themselves
            for (int i = 0; i < n; i++)
                if (inModel[i] && Leg(i) && !ClothesUnder[i] && Positions[i].Y >= acrossHem && Positions[i].Y <= acrossTop)
                    ClothesOver[i] = true;
            var grow = new Queue<int>(Enumerable.Range(0, n).Where(i => ClothesOver[i]));
            while (grow.Count > 0)
                foreach (int j in near[grow.Dequeue()] ?? new List<int>())
                    if (!ClothesOver[j] && Leg(j) && Positions[j].Y >= acrossTop)
                    {
                        ClothesOver[j] = true;
                        grow.Enqueue(j);
                    }
            var modelPoints = Enumerable.Range(0, n).Where(i => inModel[i]).ToArray();
            var source = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
                if (shell[i] && !inModel[i])
                {
                    source[i] = modelPoints.MinBy(j => Vector3.DistanceSquared(Positions[i], Positions[j]));
                    ClothesOver[i] = ClothesOver[source[i]];
                }
            var over = Enumerable.Range(0, n).Where(i => ClothesOver[i]).ToList();
            if (over.Count == 0)
                return;
            float hem = over.Min(i => Positions[i].Y), top = over.Max(i => Positions[i].Y);
            float lo = over.Min(i => Positions[i].X), hi = over.Max(i => Positions[i].X);
            float mid = (lo + hi) / 2, half = MathF.Max((hi - lo) / 2, 1e-4f);
            // a fighter in a dress of its own (Peach) swings it with skirt joints in every move: the
            // clothes take the weights of the nearest points of that dress
            var skirt = Skirt == null ? new List<FighterMesh.Vertex>()
                : fighter.Vertices.Where(v => Skirt.Contains(v.Bones[Array.IndexOf(v.Weights, v.Weights.Max())])).ToList();
            if (skirt.Count >= 50)
            {
                foreach (int i in over.Where(i => !source.ContainsKey(i)))
                {
                    var acc = new float[boneCount];
                    foreach (var v in skirt.OrderBy(v => Vector3.DistanceSquared(Lookup[i], v.Position)).Take(6))
                    {
                        float iw = 1f / (Vector3.DistanceSquared(Lookup[i], v.Position) + 1e-4f);
                        for (int b = 0; b < v.Bones.Length; b++)
                            acc[v.Bones[b]] += v.Weights[b] * iw;
                    }
                    Normalize(acc);
                    w[i] = acc;
                }
                // neighbours may have found different chains of the dress: evened out over the surface
                for (int pass = 0; pass < 8; pass++)
                {
                    var next = (float[][])w.Clone();
                    foreach (int i in over.Where(i => !source.ContainsKey(i)))
                    {
                        var round = adj[i].Where(j => ClothesOver[j]).ToList();
                        if (round.Count == 0)
                            continue;
                        var r = new float[boneCount];
                        foreach (int j in round)
                            for (int b = 0; b < boneCount; b++)
                                r[b] += w[j][b] / round.Count;
                        for (int b = 0; b < boneCount; b++)
                            r[b] = 0.5f * w[i][b] + 0.5f * r[b];
                        next[i] = r;
                    }
                    Array.Copy(next, w, w.Length);
                }
                Report += $"clothes over the legs swing with the fighter's own dress (joints {string.Join(",", Skirt.OrderBy(j => j))})\n";
            }
            else foreach (int i in over)
            {
                var p = Positions[i];
                // down the clothes: the hips at the waist, mostly the thighs at the hem
                float down = Math.Clamp((top - p.Y) / MathF.Max(top - hem, 1e-4f), 0, 1);
                float legs = SkirtToHips ? 0.95f * SmoothStep(0, 1, down) : 0.35f + 0.6f * down;
                // across: the side over a leg follows it most, the middle both alike
                float side = 0.5f + 0.45f * Math.Clamp((p.X - mid) / (half * 0.6f), -1, 1);
                w[i] = new float[boneCount];
                w[i][hip] += 1 - legs;
                w[i][plus] += legs * side;
                w[i][minus] += legs * (1 - side);
            }

            // the legs under the clothes, drawn in towards their own middle, more the higher up under the clothes
            int drawn = 0;
            float span = MathF.Max(top - hem, 1e-4f);
            var before = (Vector3[])Positions.Clone();
            var under = Enumerable.Range(0, n).Where(i => !ClothesOver[i] && !source.ContainsKey(i) && (ModelRegion[i] == LegLeft || ModelRegion[i] == LegRight)
                && Positions[i].Y > hem - span * 0.05f && Positions[i].Y < top).ToList();
            const int bands = 12;
            foreach (int region in new[] { LegLeft, LegRight })
                foreach (var band in under.Where(i => ModelRegion[i] == region).GroupBy(i => Math.Clamp((int)((Positions[i].Y - hem) / span * bands), 0, bands - 1)))
                {
                    var c = band.Aggregate(Vector3.Zero, (a, i) => a + Positions[i]) / band.Count();
                    foreach (int i in band)
                    {
                        float k = 0.3f * SmoothStep(hem - span * 0.05f, hem + span * 0.15f, Positions[i].Y);
                        var to = new Vector3(c.X, Positions[i].Y, c.Z);
                        Positions[i] += (to - Positions[i]) * k;
                        drawn++;
                    }
                }
            foreach (var (i, j) in source)
                Positions[i] += Positions[j] - before[j];

            // and they move with the clothes over them, so a thigh swinging forward takes the clothes with it
            // rather than coming out through them; at the hem they ease back into their own leg
            var overModel = over.Where(i => !source.ContainsKey(i)).ToArray();
            foreach (int i in under)
            {
                float k = SmoothStep(hem - span * 0.05f, hem + span * 0.2f, before[i].Y);
                if (k <= 0)
                    continue;
                int j = overModel.MinBy(o => Vector3.DistanceSquared(before[i], before[o]));
                for (int b = 0; b < boneCount; b++)
                    w[i][b] = w[i][b] * (1 - k) + w[j][b] * k;
            }
            Report += $"clothes over the legs stay outside them: {over.Count} points blend the hips and both thighs; {drawn} leg points under them drawn in and moving with them\n";
        }

        /// <summary>
        /// The joints a fighter's own skirt or dress hangs from: below its hips, the main joint of fighter
        /// points that are neither on a leg nor on a bone the skins use (Peach's six chains round her dress).
        /// </summary>
        private static HashSet<int> SkirtJoints(FighterMesh fighter, Skeleton sk, int[] remap, int left, int right)
        {
            float hipY = (sk.Position(left).Y + sk.Position(right).Y) / 2;
            var joints = new HashSet<int>();
            foreach (var v in fighter.Vertices.Where(v => v.Position.Y < hipY))
            {
                int d = v.Bones[Array.IndexOf(v.Weights, v.Weights.Max())];
                bool leg = d == left || d == right || IsAncestor(sk, left, d) || IsAncestor(sk, right, d);
                if (!leg && remap[d] != d && !IsAncestor(sk, d, left) && !IsAncestor(sk, d, right))
                    joints.Add(d);
            }
            return joints.Count >= 4 ? joints : null;
        }

        private void Attach(float[][] w, HashSet<int>[] adj, int boneCount, List<ObjModel.Triangle> triangles)
        {
            // only a piece's open edge counts (a sleeve's end at the shoulder, a shorts' hem): a closed piece
            // stuck into another (BMO's arms in his box) or tucked inside one (legs up inside shorts) only
            // overlaps it, and an outline shell lies over everything
            var edges = new Dictionary<(int, int), int>();
            var outline = new bool[Positions.Length];
            foreach (var t in triangles)
            {
                var c = new[] { t.A.Position, t.B.Position, t.C.Position };
                for (int k = 0; k < 3; k++)
                {
                    var e = (Math.Min(c[k], c[(k + 1) % 3]), Math.Max(c[k], c[(k + 1) % 3]));
                    edges[e] = edges.GetValueOrDefault(e) + 1;
                    if (t.Outline) outline[c[k]] = true;
                }
            }
            var open = new bool[Positions.Length];
            foreach (var (e, count) in edges)
                if (count == 1)
                    open[e.Item1] = open[e.Item2] = true;
            // rigid details (buttons, claws) move as one piece on purpose; a tail keeps its own bones
            bool Joinable(int i) => !RigidPart[i] && !Tail[i] && !outline[i];
            var pairs = TouchingPairs(triangles).Where(p => (open[p.Item1] || open[p.Item2])
                && Joinable(p.Item1) && Joinable(p.Item2)).ToList();
            if (pairs.Count == 0)
                return;
            void Weld()
            {
                for (int pass = 0; pass < 4; pass++)
                    foreach (var (i, j) in pairs)
                        for (int b = 0; b < boneCount; b++)
                            w[i][b] = w[j][b] = (w[i][b] + w[j][b]) / 2;
            }
            Weld();
            var near = new HashSet<int>(pairs.SelectMany(p => new[] { p.Item1, p.Item2 }));
            for (int ring = 0; ring < 2; ring++)
                foreach (var i in near.ToList())
                    near.UnionWith(adj[i].Where(j => !RigidPart[j]));
            var seam = new HashSet<int>(pairs.SelectMany(p => new[] { p.Item1, p.Item2 }));
            Smooth(w, adj, boneCount, 2, i => near.Contains(i) && !seam.Contains(i));
            Weld();
            Report += $"kept {pairs.Count} points where pieces touch attached\n";
        }

        /// <summary>
        /// A piece whose open end is tucked into another without touching it (Kronk's legs, open tubes ending
        /// up under his skirt; a hand in a sleeve, a neck in a collar) came out of it when the two followed
        /// different bones: in a kick his thigh swung its top out from under the skirt and the leg hung off the
        /// body. The open end takes the weights of the nearest point of the piece it's tucked into, easing back
        /// into its own over a short way, so the end stays where the other piece goes and can't show.
        /// </summary>
        private void Tuck(float[][] w, int boneCount, List<ObjModel.Triangle> triangles)
        {
            int n = Positions.Length;
            var edges = new Dictionary<(int, int), int>();
            var outline = new bool[n];
            foreach (var t in triangles)
            {
                var c = new[] { t.A.Position, t.B.Position, t.C.Position };
                for (int k = 0; k < 3; k++)
                {
                    var e = (Math.Min(c[k], c[(k + 1) % 3]), Math.Max(c[k], c[(k + 1) % 3]));
                    edges[e] = edges.GetValueOrDefault(e) + 1;
                    if (t.Outline) outline[c[k]] = true;
                }
            }
            bool Joinable(int i) => !RigidPart[i] && !Tail[i] && !outline[i];
            var open = new bool[n];
            foreach (var (e, count) in edges)
                if (count == 1)
                    open[e.Item1] = open[e.Item2] = true;
            var ends = Enumerable.Range(0, n).Where(i => open[i] && Joinable(i)).ToList();
            float tall = Positions.Max(q => q.Y) - Positions.Min(q => q.Y);
            float reach = 0.06f * tall;
            // the other pieces' points in a grid as wide as the reach
            var grid = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / reach), (int)MathF.Floor(p.Y / reach), (int)MathF.Floor(p.Z / reach));
            for (int i = 0; i < n; i++)
                if (Joinable(i) && !open[i])
                {
                    var c = Cell(Positions[i]);
                    if (!grid.TryGetValue(c, out var list)) grid[c] = list = new List<int>();
                    list.Add(i);
                }
            float Differ(float[] a, float[] b) => a.Zip(b, (x, y) => MathF.Abs(x - y)).Sum() / 2;
            var into = new Dictionary<int, int>();
            foreach (int i in ends)
            {
                var (cx, cy, cz) = Cell(Positions[i]);
                int best = -1;
                float bestD = reach * reach;
                for (int x = cx - 1; x <= cx + 1; x++)
                    for (int y = cy - 1; y <= cy + 1; y++)
                        for (int z = cz - 1; z <= cz + 1; z++)
                            if (grid.TryGetValue((x, y, z), out var list))
                                foreach (int j in list)
                                {
                                    if (Part[j] == Part[i]) continue;
                                    float d = Vector3.DistanceSquared(Positions[i], Positions[j]);
                                    if (d < bestD) { bestD = d; best = j; }
                                }
                // only where the two move apart: following other bones by a good share
                if (best >= 0 && Differ(w[i], w[best]) > 0.25f)
                    into[i] = best;
            }
            // a piece's end counts as tucked when most of its open edge is (a whole leg top, not one point of a hem
            // that happens to pass near the body)
            var byPart = into.Keys.GroupBy(i => Part[i]).Where(g => g.Count() >= 0.5f * ends.Count(e => Part[e] == g.Key) && g.Count() >= 3).SelectMany(g => g).ToList();
            // a closed piece pushed down into another (Rex's head and neck, stuck into the top of his body) has no
            // open end, but the points of it inside the other piece are tucked all the same: when the two follow
            // different bones, the neck slid up out of the body and the head came away from it
            foreach (var (i, other) in Inside(triangles, Joinable))
            {
                if (!into.ContainsKey(i) && Differ(w[i], w[other]) > 0.25f)
                {
                    into[i] = other;
                    byPart.Add(i);
                }
            }
            // a piece of the head reaching down below the head line onto the body (Rex's head and neck, one piece
            // sitting on his chest): there it's the body's, as a head that isn't a piece of its own is below the line
            if ((!float.IsNaN(NeckY) || HeadFixed) && HeadBone >= 0)
            {
                var below = Enumerable.Range(0, n).Where(i => BelowNeck(i, 0, 1)).ToHashSet();
                var headParts = Enumerable.Range(0, n).GroupBy(i => Part[i]).Where(g => g.Count(below.Contains) * 4 < g.Count()).Select(g => g.Key).ToHashSet();
                var body = Enumerable.Range(0, n).Where(i => Joinable(i) && below.Contains(i) && !headParts.Contains(Part[i])).ToArray();
                float touch = 0.03f * tall;
                foreach (int i in below)
                {
                    if (!headParts.Contains(Part[i]) || !Joinable(i) || into.ContainsKey(i) || body.Length == 0)
                        continue;
                    int other = body.MinBy(j => Vector3.DistanceSquared(Positions[i], Positions[j]));
                    if (Vector3.Distance(Positions[i], Positions[other]) < touch && Differ(w[i], w[other]) > 0.25f)
                    {
                        into[i] = other;
                        byPart.Add(i);
                    }
                }
            }
            byPart = byPart.GroupBy(i => Part[i]).Where(g => g.Count() >= 3).SelectMany(g => g).ToList();
            if (byPart.Count == 0)
                return;
            var target = byPart.ToDictionary(i => i, i => (float[])w[into[i]].Clone());
            // eased back into the piece's own weights over a little more than the gap, up to a tenth of the height
            var parts = byPart.Select(i => Part[i]).ToHashSet();
            int eased = 0;
            for (int i = 0; i < n; i++)
            {
                if (!parts.Contains(Part[i]) || !Joinable(i))
                    continue;
                int end = -1;
                float d2 = float.MaxValue;
                foreach (int e in byPart)
                    if (Part[e] == Part[i])
                    {
                        float d = Vector3.DistanceSquared(Positions[i], Positions[e]);
                        if (d < d2) { d2 = d; end = e; }
                    }
                float span = MathF.Max(2 * Vector3.Distance(Positions[end], Positions[into[end]]), 0.04f * tall);
                float k = 1 - SmoothStep(0, MathF.Min(span, 0.1f * tall), MathF.Sqrt(d2));
                if (k <= 0)
                    continue;
                for (int b = 0; b < boneCount; b++)
                    w[i][b] = (1 - k) * w[i][b] + k * target[end][b];
                eased++;
            }
            Report += $"tucked: {byPart.Count} points of {parts.Count} pieces end inside another piece; {eased} points ease into its weights, so they can't come out of it\n";
        }

        /// <summary>
        /// The points of each piece that lie inside another, closed piece (one with no open edge), with the
        /// nearest point of that piece. A point is inside when a ray from it crosses the other piece's surface
        /// an odd number of times.
        /// </summary>
        private List<(int Point, int Other)> Inside(List<ObjModel.Triangle> triangles, Func<int, bool> joinable)
        {
            int n = Positions.Length;
            var found = new List<(int, int)>();
            // open edges with points split by UV seams counted as one, or every textured piece looks open
            var same = new int[n];
            var byPos = new Dictionary<(int, int, int), int>();
            for (int i = 0; i < n; i++)
            {
                var q = Positions[i] * 1000;
                var key = ((int)MathF.Round(q.X), (int)MathF.Round(q.Y), (int)MathF.Round(q.Z));
                same[i] = byPos.TryAdd(key, i) ? i : byPos[key];
            }
            var edges = new Dictionary<(int, int), int>();
            foreach (var t in triangles.Where(t => !t.Outline))
            {
                var c = new[] { same[t.A.Position], same[t.B.Position], same[t.C.Position] };
                for (int k = 0; k < 3; k++)
                {
                    var e = (Math.Min(c[k], c[(k + 1) % 3]), Math.Max(c[k], c[(k + 1) % 3]));
                    edges[e] = edges.GetValueOrDefault(e) + 1;
                }
            }
            var open = new bool[n];
            foreach (var (e, count) in edges)
                if (count == 1)
                    open[e.Item1] = open[e.Item2] = true;
            for (int i = 0; i < n; i++)
                open[i] = open[same[i]];
            var byPart = triangles.Where(t => !t.Outline).GroupBy(t => Part[t.A.Position]).ToDictionary(g => g.Key, g => g.ToList());
            var closed = byPart.Where(kv => kv.Value.All(t => !open[t.A.Position] && !open[t.B.Position] && !open[t.C.Position]))
                .Select(kv => kv.Key).ToList();
            if (closed.Count == 0)
                return found;
            // an odd direction, so the ray doesn't run along an edge of a model built square to the axes
            var dir = Vector3.Normalize(new Vector3(0.31f, 0.83f, 0.47f));
            var boxes = closed.ToDictionary(c => c, c =>
            {
                var pts = byPart[c].SelectMany(t => new[] { t.A.Position, t.B.Position, t.C.Position }).Select(k => Positions[k]).ToList();
                return (Min: pts.Aggregate(Vector3.Min), Max: pts.Aggregate(Vector3.Max));
            });
            var members = closed.ToDictionary(c => c, c => Enumerable.Range(0, n).Where(i => Part[i] == c && joinable(i)).ToArray());
            for (int i = 0; i < n; i++)
            {
                if (!joinable(i))
                    continue;
                var p = Positions[i];
                foreach (int c in closed)
                {
                    var (lo, hi) = boxes[c];
                    if (c == Part[i] || members[c].Length == 0 || Vector3.Clamp(p, lo, hi) != p)
                        continue;
                    int hits = 0;
                    foreach (var t in byPart[c])
                        if (RayHits(p, dir, Positions[t.A.Position], Positions[t.B.Position], Positions[t.C.Position]))
                            hits++;
                    if (hits % 2 == 1)
                    {
                        found.Add((i, members[c].MinBy(j => Vector3.DistanceSquared(p, Positions[j]))));
                        break;
                    }
                }
            }
            return found;
        }

        // Moller-Trumbore, forward along the ray only
        private static bool RayHits(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
        {
            var e1 = b - a;
            var e2 = c - a;
            var h = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, h);
            if (MathF.Abs(det) < 1e-12f)
                return false;
            float f = 1 / det;
            var s = o - a;
            float u = f * Vector3.Dot(s, h);
            if (u < 0 || u > 1)
                return false;
            var q = Vector3.Cross(s, e1);
            float v = f * Vector3.Dot(d, q);
            if (v < 0 || u + v > 1)
                return false;
            return f * Vector3.Dot(e2, q) > 1e-6f;
        }

        private void Smooth(float[][] w, HashSet<int>[] adj, int boneCount, int passes, Func<int, bool> include)
        {
            // who smooths with whom stays the same every pass
            var nears = new List<int>[w.Length];
            System.Threading.Tasks.Parallel.For(0, w.Length, i =>
                nears[i] = RigidPart[i] || !include(i) ? null : adj[i].Where(include).ToList());
            for (int pass = 0; pass < passes; pass++)
            {
                // each point reads only the last pass, so the points are shared out across the cores
                var next = new float[w.Length][];
                System.Threading.Tasks.Parallel.For(0, w.Length, i =>
                {
                    var near = nears[i];
                    if (near == null || near.Count == 0)
                    {
                        next[i] = w[i];
                        return;
                    }

                    var avg = new float[boneCount];
                    foreach (var j in near)
                        for (int b = 0; b < boneCount; b++)
                            avg[b] += w[j][b];
                    float inv = 1f / near.Count;
                    var r = new float[boneCount];
                    for (int b = 0; b < boneCount; b++)
                        r[b] = 0.5f * w[i][b] + 0.5f * avg[b] * inv;
                    next[i] = r;
                });
                Array.Copy(next, w, w.Length);
            }
        }
    }
}
