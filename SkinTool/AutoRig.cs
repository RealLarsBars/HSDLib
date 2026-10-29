using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Fits a model onto the fighter's bind pose and copies the fighter's skin weights onto it.
    /// </summary>
    public class AutoRig
    {
        public class Options
        {
            // multiplies the automatic size
            public float Scale = 1;

            // stretch the fitted model sideways or upwards only (from the floor, about its middle)
            public float Width = 1, Height = 1;

            // degrees to turn the model around the vertical axis before fitting, and to tip it over
            // first (a model lying down or upside down): X leans it forward, Z sideways
            public float RotateY = 0, RotateX = 0, RotateZ = 0;

            // moves the fitted model forward (towards where the fighter faces) and up, as shares of the
            // fighter's height, after it is weighted: it only changes where it sits on the fighter
            public float Forward = 0, Up = 0;

            // which of the model's eye states it gets (see Eyes), by key; null: the default ones
            public string Eyes;

            // the look: normal, or animelee (black outline, flat lighting, flat colours)
            public string Style = "normal";

            // fighter vertices blended per model vertex
            public int Neighbours = 8;

            // passes of weight smoothing over the model's surface
            public int SmoothPasses = 12;

            // extra passes over the body alone (not the limbs)
            public int BodySmoothPasses = 40;

            // influences kept per vertex, and the smallest one kept
            public int MaxInfluences = 3;
            public float MinWeight = 0.08f;

            // the bones custom skins for this fighter use (from a bone map); null works them out
            public int[] Bones;

            // the face bone from the bone map; -1 finds the head by itself
            public int Head = -1;

            // the bone map's roles (face, body, lsh, rha, ...), which say outright which bones are arms and legs
            public Dictionary<string, int> Roles;
            // the fighter kicks by stretching its foot bones far out (BoneMaps.StretchyFeet)
            public bool StretchyFeet;
            // the fighter wears a long dress over its legs (BoneMaps.Dress), or the model is said to
            public bool Dress;
            // the fighter itself wears a dress, with skirt joints its animations swing (BoneMaps.Dress)
            public bool OwnDress;
            // pieces of the model that touch keep the same weights where they meet, so they stay attached
            public bool Attach = true;

            // move the model's arms and feet onto the fighter's, as modders do by hand, so it holds
            // things and steps where the fighter does (needs a bone map); false keeps the model's shape
            public bool Conform = true;

            // for a model with no real legs: the share of its height, from the bottom, that bends with
            // the fighter's legs, left half with the left leg and right with the right, fading into the body
            public float LegBand = 0.3f;

            // instead, cut that share into two separate legs stretched down the fighter's (0 = don't)
            public float LegCut = 0;

            // the model's legs: "auto" works it out, "separate" (it has real legs), "body" (its body works
            // as legs) or "cut" (its bottom is cut into two legs down the fighter's)
            public string Legs = "auto";

            // clothes found over the legs (a coat, apron or dress hanging across between them): "outside"
            // keeps them over the legs as one sheet, "legs" lets them split and follow each leg like trousers
            public string Clothes = "outside";

            // the model's arms: "auto", "fighter" (always moved onto the fighter's arms) or "body"
            // (mostly stay with the body)
            public string Arms = "auto";

            // the angle the model's arms are turned to at the shoulder, before weighting, in degrees below
            // level (negative: above). NaN works it out: arms hanging well below the fighter's are lifted to its angle
            public float ArmAngle = float.NaN;

            // parts with fewer vertices than this share of the model move as one piece with the body around them
            public float RigidPartShare = 0.15f;

            // where the head starts, as a share of the model's height from the bottom: everything above
            // follows the head bone alone, so a wide head or face is never read as arms and moves as one
            // piece. NaN works it out (a neck narrower than the head above it), 0 or less: no such line
            public float Neck = float.NaN;
            // the neck line's lean, set by hand with Neck, in degrees (positive: down at the front, for a head
            // tipped forward); a neck found on its own leans as much as the head does
            public float NeckTilt = 0;

            // the model has a tail: the part sticking out behind it follows the fighter's tail bones, root
            // to tip (BoneMaps.Tails); otherwise everything behind follows the body
            public int[] TailBones;
            public bool HasTail;
        }

        public Vector3[] Positions;

        // positions used only to find matching fighter vertices
        public Vector3[] Lookup;

        // where a moved arm's points take their weights from, on the fighter's arm (null: from Lookup's usual place)
        private Vector3?[] ArmLookup;

        // how far along such an arm each point is, 0 at the body, 1 at the tip
        private float[] ArmU;
        public Vector3[] Normals;
        public SkinBuilder.Influence[][] Weights;

        // per model position, the part it belongs to and whether that part is rigid
        public int[] Part;
        public bool[] RigidPart;
        // a model built from pieces: the pieces that make up its body (BMO's box), which move as one
        public bool[] BodyPiece;

        public string Report = "";

        // the fitted height of the neck line (NaN when there's none)
        public float NeckY = float.NaN;
        // the line leans with the head: it dips NeckSlope for each unit forward of NeckZ (NeckTilt is the
        // lean in degrees, positive down at the front)
        public float NeckZ, NeckSlope, NeckTilt;

        /// <summary>The height of the neck line at a point (float.MaxValue when there is none).</summary>
        public float NeckYAt(Vector3 p) => float.IsNaN(NeckY) ? float.MaxValue : NeckY - NeckSlope * (p.Z - NeckZ);
        private int HeadBone = -1;

        // per model position, whether it's on the model's tail
        private bool[] Tail;

        // detail bone -> the bone that takes its weight
        private int[] Remap;
        // the remap that sorts the fighter's own points into regions: shoulder helpers stay with the body
        // there, so the fitting (where the fighter's arms start) isn't moved by where their weights go
        private int[] RegionRemap;

        // Body regions. Weights are only copied between matching regions, so a wide body never picks up
        // the fighter's arms and a round belly never picks up its thighs.
        public const int Body = 0, ArmLeft = 1, ArmRight = 2, LegLeft = 3, LegRight = 4, Other = 5;
        private static readonly string[] RegionNames = { "body", "arm (+x)", "arm (-x)", "leg (+x)", "leg (-x)", "other" };

        // per region, how fully the limb follows its own bones rather than the body (1 = fully)
        private readonly float[] LimbFollow = { 1, 1, 1, 1, 1, 1 };

        // per bone, per fighter vertex and per model position
        // for a model with no real legs: where its legs end at the top and the bottom, and its middle
        // (NaN when it has real legs)
        // the fighter's hip joints, where its legs start under a dress
        private float LegTopY = float.NaN;
        // the model's legs are pressed together all the way up (see LegsTogether)
        private bool LegsPressed;
        // per model position: part of clothes worn over the legs (null: none)
        public bool[] ClothesOver;
        // the clothes run up to the fighter's hip joints (a skirt on a fighter whose legs part low): they ease in
        // from none of the legs there, where they meet the body above
        private bool SkirtToHips;
        // per model position: on the legs under those clothes
        private bool[] ClothesUnder;
        // the hips' bone and the two thigh bones (lle, rle), and whether lle is the +x one
        private (int hip, int left, int right, bool leftPlus) LegJoints = (-1, -1, -1, true);
        // the fighter's own skirt or dress joints (Peach's, Zelda's), which its animations swing; null: none
        private HashSet<int> Skirt;
        private float HipY = float.NaN, FloorY, MiddleX, HalfWidth, MiddleZ, HalfDepth;
        // the height of the fighter's shoulder joints (NaN without a bone map)
        private float ShoulderY = float.NaN;

        private int[] BoneRegion;
        private Skeleton Skel;
        // the model's triangles, to find each position's normals
        private List<ObjModel.Triangle> Corners;

        // arms moved onto the fighter's own arms (ConformArm): they bend where its arms bend
        private readonly HashSet<int> Conformed = new();

        // those of them bent down to hang along a fighter whose arms hang (Ganondorf)
        private readonly HashSet<int> Hung = new();
        // per hung arm: the fighter's hand joint, and how far along the arm (0 to 1) its wrist is
        private readonly Dictionary<int, (int Bone, float At)> Wrist = new();
        // fingers follow the fighter's finger joints only half way: in full, a big open hand folded into a box
        const float FingerShare = 0.5f;
        // arms found hanging apart from the body, beside it (Kronk's), rather than sticking out from it
        private bool HangingArms;

        // the model stands on four legs, its front legs held up as the fighter's arms (Spyro)
        private bool FourLegs;
        private int[] FighterRegion;
        public int[] ModelRegion;

        public static AutoRig Run(ObjModel model, FighterMesh fighter, Skeleton skeleton, Options opt)
        {
            var rig = new AutoRig();
            rig.Remap = DetailBoneRemap(fighter, skeleton, out var merged, out int head);
            if (opt.Head >= 0)
                head = opt.Head;
            if (opt.Bones != null)
            {
                var shoulders = opt.Roles == null ? new int[0] : new[] { "lsh", "rsh" }.Where(opt.Roles.ContainsKey).Select(r => opt.Roles[r]).ToArray();
                rig.Remap = ChosenBoneRemap(fighter, skeleton, opt.Bones, head, shoulders);
                rig.RegionRemap = ChosenBoneRemap(fighter, skeleton, opt.Bones, head);
                rig.Report += $"bones from the fighter's bone map: {string.Join(",", opt.Bones)}\n";
                var helpers = Enumerable.Range(0, rig.Remap.Length).Where(b => shoulders.Contains(rig.Remap[b]) && !opt.Bones.Contains(b) && fighter.BoneExtent.ContainsKey(b)).ToList();
                if (helpers.Count > 0)
                    rig.Report += $"shoulder helpers {string.Join(",", helpers)} go to the arms\n";
            }
            else
                rig.Report += $"detail bones folded into their parents: {string.Join(",", merged)}\n";
            rig.RegionRemap ??= rig.Remap;
            rig.HeadBone = head;
            rig.Skel = skeleton;
            rig.Corners = model.Triangles;
            rig.FighterRegions(fighter, skeleton, head, opt.Roles);
            if (opt.Roles != null && opt.Roles.TryGetValue("lsh", out int lsh) && opt.Roles.TryGetValue("rsh", out int rsh))
                rig.ShoulderY = (skeleton.Position(lsh).Y + skeleton.Position(rsh).Y) / 2;
            rig.Fit(model, fighter, skeleton, opt);
            if (opt.Roles != null && new[] { "lle", "rle" }.All(opt.Roles.ContainsKey))
            {
                rig.LegJoints = (opt.Roles.GetValueOrDefault("butt", opt.Roles.GetValueOrDefault("body", -1)), opt.Roles["lle"], opt.Roles["rle"],
                    skeleton.Position(opt.Roles["lle"]).X > skeleton.Position(opt.Roles["rle"]).X);
                if (opt.OwnDress)
                    rig.Skirt = SkirtJoints(fighter, skeleton, rig.Remap, opt.Roles["lle"], opt.Roles["rle"]);
            }
            rig.Transfer(model, fighter, opt);
            if (rig.LegsPressed)
                rig.Report += $"left out {rig.UnjoinLegs(model)} triangles joining the two lower legs, which would stretch into a sheet in a stride\n";
            rig.Move(fighter, opt);
            return rig;
        }

        /// <summary>
        /// Slides the weighted model to where the user wants it on the fighter (a model with a long neck
        /// and tail, like Spyro, can sit too far forward). Each part keeps its bones. It goes as far up or
        /// down as asked, with a warning when that leaves it floating or sunk into the floor.
        /// </summary>
        private void Move(FighterMesh fighter, Options opt)
        {
            if (opt.Forward == 0 && opt.Up == 0)
                return;
            float tall = fighter.Max.Y - fighter.Min.Y;
            float up = opt.Up * tall;
            var by = new Vector3(0, up, opt.Forward * tall);
            for (int i = 0; i < Positions.Length; i++)
                Positions[i] += by;
            NeckY += up;
            NeckZ += opt.Forward * tall;
            // moved down on purpose: the floor guard lets it go that much lower
            Lowered = MathF.Max(-up, 0);
            Report += $"moved {opt.Forward:+0%;-0%;0%} forward and {opt.Up:+0%;-0%;0%} up\n";
            if (up == 0)
                return;
            float gap = (Positions.Min(p => p.Y) - fighter.Min.Y) / tall;
            if (gap > 0.01f)
                Report += $"position: the model's lowest point is {gap:0%} of the character's height above the floor, so it floats\n";
            else if (gap < -0.03f)
                Report += $"position: the model's lowest point is {-gap:0%} of the character's height below the floor, so its feet sink in\n";
        }

        // how far Move put the model down (it is let go that much lower before the floor guard steps in)
        public float Lowered;

        // a blended point counts as flat under half its depth
        const float KeepDepth = 0.5f;

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

        /// <summary>
        /// A neck in the model's outline: a narrow spot in its upper half with a head above it at least
        /// half as wide again and as wide as the body, taking up a good part of the height. Returns the share of the height where
        /// the head starts, or 0 when there's no clear neck (a round body with a face on the front).
        /// </summary>
        private static float FindNeck(float[] reach, float typical, int top, float crotch)
        {
            int slices = reach.Length;
            // above the arms held out, if they are: shoulders are below the neck
            int from = Math.Max(slices / 2, (int)(crotch * slices) + 1);
            for (int s = top; s >= from; s--)
                if (reach[s] > typical * 2f) { from = s + 1; break; }
            int best = -1;
            for (int s = from; s <= top - slices / 8; s++)
            {
                if (reach[s] <= 0) continue;
                float above = Enumerable.Range(s + 1, top - s).Max(k => reach[k]);
                // and the head is a real one, at least as wide as the body (not ears or a tuft on top)
                if (above >= reach[s] * 1.5f && above >= typical && (best < 0 || reach[s] < reach[best]))
                    best = s;
            }
            if (best < 0)
                return 0;
            // the head starts where the outline widens again above the narrowest spot
            int start = best;
            while (start < top && reach[start + 1] <= reach[best] * 1.25f)
                start++;
            return (float)start / slices;
        }

        private void Fit(ObjModel model, FighterMesh fighter, Skeleton skeleton, Options opt)
        {
            var rot = Matrix4x4.CreateRotationX(opt.RotateX * MathF.PI / 180)
                * Matrix4x4.CreateRotationZ(opt.RotateZ * MathF.PI / 180)
                * Matrix4x4.CreateRotationY(opt.RotateY * MathF.PI / 180);
            var pts = model.Positions.Select(p => Vector3.Transform(p, rot)).ToArray();
            // the shape is measured without an outline shell (Animelee's, or one the model came with): a
            // slightly bigger copy of the model, it closes the gap between the legs and fills in the neck
            var shapeTris = model.Triangles.Where(t => !t.Outline).ToList();
            if (shapeTris.Count == 0)
                shapeTris = model.Triangles;
            var inShape = new bool[pts.Length];
            foreach (var t in shapeTris)
                inShape[t.A.Position] = inShape[t.B.Position] = inShape[t.C.Position] = true;
            var shape = pts.Where((_, i) => inShape[i]).ToArray();

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in shape)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var size = Vector3.Max(max - min, new Vector3(1e-6f));

            // the fighter's body depth, ignoring tails and capes that stick out behind
            var zs = fighter.Vertices.Select(v => v.Position.Z).OrderBy(z => z).ToList();
            float zLow = zs[zs.Count / 10], zHigh = zs[zs.Count * 9 / 10], zMid = zs[zs.Count / 2];
            var fighterSize = new Vector3(fighter.Max.X - fighter.Min.X, fighter.Max.Y - fighter.Min.Y, zHigh - zLow);

            // Size: as tall as the fighter would make a round model huge, and as wide as its arm span
            // would make it tiny, so meet in the middle. For a humanoid in a T-pose the two agree.
            float byHeight = fighterSize.Y / size.Y;
            float byWidth = fighterSize.X / size.X;
            // Never wider than the arm span though: hands past the fighter's hands swing far out
            // whenever the fighter turns a wrist.
            float scale = MathF.Min(MathF.Sqrt(byHeight * byWidth), byWidth) * opt.Scale;

            var target = new Vector3((fighter.Min.X + fighter.Max.X) / 2, fighter.Min.Y, zMid);
            var origin = new Vector3((min.X + max.X) / 2, min.Y, (min.Z + max.Z) / 2);
            // a long tail laid out behind (Remy's, as long as he is tall) puts the middle of the box far behind
            // the body, and the body lands in front of the fighter's bones: they swing it about from behind,
            // and its arms, moved onto the fighter's, end up inside it. The body's middle is taken instead, the
            // way the fighter's is, leaving out what sticks out behind or in front. Not for a model stood up from
            // lying or on all fours (Spyro, turned 90 degrees): its long neck and tail aren't a body's front and back,
            // and its placement is left to --move
            var mzs = shape.Select(p => p.Z).OrderBy(z => z).ToArray();
            float bodyMid = (mzs[mzs.Length / 10] + mzs[mzs.Length * 9 / 10]) / 2;
            if (opt.RotateX == 0 && opt.RotateZ == 0 && MathF.Abs(origin.Z - bodyMid) > 0.15f * size.Z)
            {
                Report += $"depth: the body's middle is {MathF.Abs(origin.Z - bodyMid) / size.Z:P0} of the depth {(bodyMid > origin.Z ? "in front of" : "behind")} the middle of the whole model (a tail or snout sticking out); the body goes on the fighter's\n";
                origin.Z = bodyMid;
            }

            var grow = new Vector3(opt.Width, opt.Height, 1);
            Positions = pts.Select(p => (p - origin) * scale * grow + target).ToArray();
            // a stretch tips normals the other way
            Normals = model.Normals.Select(n => Vector3.TransformNormal(n, rot) / grow).ToArray();

            // Weights are looked up with the model stretched over the fighter's whole body, so its
            // outermost hands land on the fighter's hands, its feet on the feet and its head on the
            // head, whatever the two shapes' proportions are.
            // The legs get their own stretch: the model's crotch lines up with the fighter's, so a
            // model with stubby feet under a round body doesn't drag its belly along with the thighs.
            var stretch = fighterSize / size;
            var modelTris = shapeTris.Select(t => (t.A.Position, t.B.Position, t.C.Position));
            var fighterTris = Enumerable.Range(0, fighter.Triangles.Count / 3).Select(t => (3 * t, 3 * t + 1, 3 * t + 2));
            float modelCrotch = Crotch(pts, modelTris, (min.X + max.X) / 2, min.Y, size.Y, edges: true);
            // clothes worn over the legs (a coat, apron or dress hanging across between them)
            var clothes = Clothes.Find(pts, shapeTris.Select(t => (t.A.Position, t.B.Position, t.C.Position)).ToList(), min, size,
                tris => Crotch(pts, tris, (min.X + max.X) / 2, min.Y, size.Y));
            Report += clothes.Found
                ? $"clothes over the legs: {clothes.Across} triangles hang across between them, from {clothes.Hem:P0} to {clothes.Top:P0} of the height; under them the legs part up to {clothes.LegsTop:P0} (outside {modelCrotch:P0})\n"
                : $"clothes over the legs: none (legs part up to {modelCrotch:P0})\n";
            ClothesOver = clothes.Found && opt.Legs is "auto" or "separate" ? clothes.Over : null;
            ClothesUnder = clothes.Under;
            float fighterCrotch = Crotch(fighter.Vertices.Select(v => v.Position).ToArray(), fighterTris, (fighter.Min.X + fighter.Max.X) / 2, fighter.Min.Y, fighterSize.Y);

            // the leg bones' influence reaches past where the legs visibly part (thighs run up to the hips),
            // so line the model's crotch up with the top of the area the leg bones drive
            float splitY = fighter.Min.Y + fighterCrotch * fighterSize.Y;
            var legBones = fighter.Vertices.Where(v => v.Position.Y < splitY).Select(Dominant).ToHashSet();
            var legTop = fighter.Vertices.Where(v => legBones.Contains(Dominant(v))).Select(v => v.Position.Y).OrderBy(y => y).ToList();
            if (legTop.Count > 0)
                fighterCrotch = Math.Clamp((legTop[legTop.Count * 95 / 100] - fighter.Min.Y) / fighterSize.Y, fighterCrotch, 0.7f);
            // A model with stubby legs (a blob on feet) has nothing to put along the fighter's thighs and
            // shins: its feet follow the fighter's feet and its body starts at the hips.
            float lowerTop = fighterCrotch;
            int[] legSide = null;
            // legs pressed together (Peach's overalls) touch all the way up, so they never part; but they
            // make a slim column under the body, where a blob on feet (Monty) is wide right down to them
            if (opt.Legs == "auto" && modelCrotch < fighterCrotch * 0.5f)
            {
                float together = LegsTogether(shape, (min.X + max.X) / 2, min.Y, size.Y);
                if (together > 0)
                {
                    LegsPressed = true;
                    modelCrotch = together;
                    Report += $"legs pressed together: the model's legs run up to {together:P0} of its height\n";
                }
            }
            // a slim model whose legs part well up but still low on its body (Linguini's shins under a long
            // coat) has real legs under it: they run up to the hips like under a dress, rather than the coat
            // being read as a body with no legs. A wide one with short legs (BMO's box) isn't
            // so do legs found under clothes that hang across between them (Peach's dress, Linguini's apron)
            bool coat = opt.Legs == "auto" && !opt.Dress && (clothes.Found || modelCrotch >= 0.15f && modelCrotch < fighterCrotch * 0.5f
                && Slim(shape, (min.X + max.X) / 2, min.Y, size.Y, modelCrotch));
            if (coat)
                Report += "long coat: the model's legs run up under it\n";
            bool stubby = opt.Legs switch { "separate" => false, "body" or "cut" => true, _ => modelCrotch < fighterCrotch * 0.5f && !coat && !clothes.Found };
            // a long dress hides where the fighter's legs part (Zelda, Peach): its legs run up to the hip
            // joints under the dress. The model's legs are whatever is below those joints once it stands in
            // the fighter's place, so a pair of shorts bends with the thighs inside it and bare shins with
            // the knees and shins, rather than the shins stretching over the fighter's whole leg
            if (!stubby && (opt.Dress || coat) && opt.Roles != null && new[] { "lle", "rle" }.All(opt.Roles.ContainsKey))
            {
                float hipHeight = (skeleton.Position(opt.Roles["lle"]).Y + skeleton.Position(opt.Roles["rle"]).Y) / 2 - fighter.Min.Y;
                if (hipHeight / fighterSize.Y > fighterCrotch)
                {
                    fighterCrotch = lowerTop = Math.Min(hipHeight / fighterSize.Y, 0.7f);
                    modelCrotch = Math.Clamp(hipHeight / (size.Y * scale * opt.Height), modelCrotch, 0.7f);
                    LegTopY = fighter.Min.Y + hipHeight;
                    Report += $"dress: the fighter's legs run up to its hips under the dress, at {fighterCrotch:P0} of its height\n";
                }
            }
            if (opt.Legs == "cut" && opt.LegCut <= 0)
                opt.LegCut = 0.2f;
            if (stubby && opt.Conform && opt.LegCut > 0 && opt.Roles != null && new[] { "lle", "rle" }.All(opt.Roles.ContainsKey))
            {
                legSide = Legs.Split(model, rot, opt.LegCut);
                pts = model.Positions.Select(p => Vector3.Transform(p, rot)).ToArray();
                Normals = model.Normals.Select(n => Vector3.TransformNormal(n, rot)).ToArray();
                modelCrotch = opt.LegCut;
                scale = MakeLegs(legSide, pts, min, size, fighter, fighterSize, zMid, skeleton, opt);
                Report += $"no real legs: the bottom {opt.LegCut:P0} is cut into two legs down the fighter's legs, the rest sits on the hips\n";
            }
            else if (stubby && opt.LegBand > 0)
            {
                // Its body works as legs where it has to: the bottom bends with the fighter's legs, left half
                // with the left leg, and fades into the body towards the top and the middle. Big enough to
                // fill most of the fighter's frame, so it gets hit where the fighter does.
                modelCrotch = opt.LegBand;
                scale = MathF.Max(scale, 0.75f * byHeight * opt.Scale);
                Positions = pts.Select(p => (p - origin) * scale * grow + target).ToArray();
                HipY = target.Y + opt.LegBand * size.Y * scale * opt.Height;
                FloorY = target.Y;
                MiddleX = target.X;
                HalfWidth = size.X * scale * opt.Width / 2;
                MiddleZ = target.Z;
                HalfDepth = size.Z * scale / 2;
                Report += $"no real legs: the bottom {opt.LegBand:P0} of the body bends with the fighter's legs\n";
            }
            else if (stubby)
            {
                float lowY = fighter.Min.Y + fighterSize.Y * 0.08f;
                var footBones = fighter.Vertices.Where(v => v.Position.Y < lowY).Select(v => Remap[Dominant(v)]).ToHashSet();
                var footTop = fighter.Vertices.Where(v => footBones.Contains(Remap[Dominant(v)])).Select(v => v.Position.Y).OrderBy(y => y).ToList();
                if (footTop.Count > 0)
                    lowerTop = Math.Min(fighterCrotch, (footTop[footTop.Count * 9 / 10] - fighter.Min.Y) / fighterSize.Y);
                Report += $"short legs: the model's feet follow the fighter's feet (bottom {lowerTop:P0}) and its body the hips\n";
            }

            float HeightOnFighter(float t) => fighter.Min.Y + fighterSize.Y * (t < modelCrotch
                ? t / modelCrotch * lowerTop
                : fighterCrotch + (t - modelCrotch) / (1 - modelCrotch) * (1 - fighterCrotch));

            // the model's regions: below its crotch are legs, and whatever sticks out sideways past the
            // body's outline (found where the outline suddenly widens) is arms
            const int slices = 50;
            int Slice(float t) => Math.Clamp((int)(t * slices), 0, slices - 1);
            float cx = (min.X + max.X) / 2;
            var reach = new float[slices];
            foreach (var q in pts.Where((_, i) => inShape[i]))
            {
                int s = Slice((q.Y - min.Y) / size.Y);
                reach[s] = MathF.Max(reach[s], MathF.Abs(q.X - cx));
            }
            // the neck is found on the points alone, as before (Spyro's wings and horns between his points
            // would hide his neck)
            var pointReach = (float[])reach.Clone();
            // a low-poly model (Gusteau) has heights with no point on them, and its few points sit where the
            // outline bulges, so those heights looked wider than the rest and counted as arms: the outline is
            // also taken along the edges, at every slice they cross
            foreach (var t in model.Triangles)
                foreach (var (a, b) in new[] { (t.A.Position, t.B.Position), (t.B.Position, t.C.Position), (t.C.Position, t.A.Position) })
                {
                    int from = Slice((pts[a].Y - min.Y) / size.Y), to = Slice((pts[b].Y - min.Y) / size.Y);
                    if (Math.Abs(from - to) < 2)
                        continue;
                    for (int s = Math.Min(from, to) + 1; s < Math.Max(from, to); s++)
                    {
                        float y = min.Y + (s + 0.5f) / slices * size.Y;
                        float k = Math.Clamp((y - pts[a].Y) / (pts[b].Y - pts[a].Y), 0, 1);
                        reach[s] = MathF.Max(reach[s], MathF.Abs(pts[a].X + (pts[b].X - pts[a].X) * k - cx));
                    }
                }
            float Typical(float[] r)
            {
                var body = Enumerable.Range(Slice(modelCrotch), slices - Slice(modelCrotch)).Where(s => r[s] > 0).Select(s => r[s]).OrderBy(x => x).ToList();
                return body.Count > 0 ? body[body.Count / 2] : size.X / 2;
            }
            float typical = Typical(reach);
            var bodyReach = new float[slices];
            int top = Enumerable.Range(0, slices).Last(s => reach[s] > 0);

            // the neck: above it is all head. The cut leans with a head held tipped forward or back (Kermit's,
            // Spyro's); a level neck keeps the line found on the outline, as before
            float neck = opt.Neck;
            Neck cut = null;
            float armsPast = float.MaxValue;
            var cutTris = shapeTris.Select(t => (t.A.Position, t.B.Position, t.C.Position)).ToList();
            if (float.IsNaN(neck))
            {
                neck = FindNeck(pointReach, Typical(pointReach), Enumerable.Range(0, slices).Last(s => pointReach[s] > 0), modelCrotch);
                var found = Neck.Find(pts, cutTris, min, size, modelCrotch);
                // a short neck under a head no wider than the body (Kermit's) has no narrow spot in the outline
                // seen from the front, but its cross-section still narrows. A neck found both ways keeps its
                // level line unless the head clearly leans there (a cut along it is much smaller), and on a model stood
                // up with --rotate (Spyro), whose neck reaches out between wings and horns, it stays level
                // Found only this way, it has to stand out clearly: a tiny head sunk in a big collar (Gusteau's)
                // bends better with the body than turning on its own.
                if (found != null && (neck <= 0 ? found.Score >= 2.5f : Math.Abs(found.Tilt) > 5 && found.Gain >= 1.25f && Math.Abs(found.Share - neck) < 0.1f && opt.RotateX == 0 && opt.RotateZ == 0))
                {
                    cut = found;
                    neck = found.Share;
                }
                // a thick neck under a head no wider than it (Kronk's) never narrows, but the head and neck still
                // stand on the shoulders as a narrow column: going down from the top, the outline jumps out to
                // the shoulders at once. Everything above the shoulders is head, or the face smears between the
                // chest and the head bone in every move
                if (neck <= 0 && opt.RotateX == 0 && opt.RotateZ == 0)
                {
                    int headTop = Enumerable.Range(0, slices).Last(s => pointReach[s] > 0);
                    float widest = 0;
                    for (int s = headTop; s > slices / 2; s--)
                    {
                        if (pointReach[s] <= 0)
                            continue;
                        // past the dome of the head's top first, then a step out to shoulders twice as wide
                        // as the head, and still that wide a little lower (not an ear or a hat brim)
                        if (headTop - s >= slices / 8 && pointReach[s] > widest * 1.5f
                            && Enumerable.Range(Math.Max(s - 3, 0), 3).All(k => pointReach[k] > widest * 2))
                        {
                            neck = (s + 1f) / slices;
                            cut = Neck.At(pts, min, size, neck, 0);
                            Report += $"head: it stands on the shoulders as a column {widest / pointReach[s - 1]:P0} as wide as them\n";
                            break;
                        }
                        widest = MathF.Max(widest, pointReach[s]);
                    }
                }
            }
            else if (neck > 0)
                cut = Neck.At(pts, min, size, neck, opt.NeckTilt);
            // a short neck, or a line set by hand, can sit as low as arms held out: what reaches well past the
            // head (as wide as its top quarter gets) stays arms above the line too (the outline's own neck is
            // always above them)
            if (cut != null)
            {
                int headTop = Enumerable.Range(0, slices).Last(s => pointReach[s] > 0);
                int headFrom = Math.Min(Math.Max(Slice(neck), slices * 3 / 4), headTop);
                // arms held out right at the line (Moe's, a hunched head sitting low between his shoulders)
                // aren't the head: it starts above them, unless that wide stretch runs up to the top (a big head)
                int wideTo = headFrom;
                while (wideTo < headTop && pointReach[wideTo] > typical * 1.35f)
                    wideTo++;
                if (wideTo > headFrom && wideTo < headTop - slices / 10)
                    headFrom = wideTo;
                armsPast = Enumerable.Range(headFrom, headTop - headFrom + 1).Max(s => pointReach[s]) * 1.5f;
            }
            int neckSlice = neck > 0 ? Slice(neck) : slices;
            if (neck > 0)
            {
                NeckY = target.Y + neck * size.Y * scale * opt.Height;
                if (cut != null)
                {
                    NeckZ = target.Z + (cut.Center.Z - origin.Z) * scale;
                    NeckSlope = MathF.Tan(cut.Tilt * MathF.PI / 180) * opt.Height;
                    NeckTilt = cut.Tilt;
                }
                string lean = NeckTilt == 0 ? "level" : $"leaning {Math.Abs(NeckTilt):0} degrees {(NeckTilt > 0 ? "down at the front" : "up at the front")}";
                Report += $"head: everything above {neck:P0} of the model's height follows the head, {lean} ({(float.IsNaN(opt.Neck) ? "found a neck" : "set by hand")})\n";
            }
            for (int s = 0; s < slices; s++)
            {
                if (reach[s] <= typical * 1.35f || s >= neckSlice && reach[s] <= armsPast)
                {
                    bodyReach[s] = reach[s];
                    continue;
                }
                int lo = s, hi = s;
                while (lo > 0 && reach[lo] > typical * 1.35f) lo--;
                while (hi < slices - 1 && reach[hi] > typical * 1.35f) hi++;
                // a tall wide stretch up at the top is a big head (Squidward), not arms: arms held out
                // are only an arm's thickness tall, and arms held lower start at the shoulders
                int runTop = reach[hi] > typical * 1.35f ? hi : hi - 1;
                int runBottom = reach[lo] > typical * 1.35f ? lo : lo + 1;
                if (runTop >= top - slices / 5 && runTop - runBottom + 1 >= 4)
                {
                    bodyReach[s] = reach[s];
                    continue;
                }
                // arms here: use the body's outline just below or above them, whichever is narrower,
                // since the arms' own thickness tapers into the slices next to them
                // (not a neck right above arms held out at shoulder height: that would make the chest arms)
                var sides = new[] { reach[lo], reach[hi] }.Where(r => r > typical * 0.6f && r <= typical * 1.35f).ToList();
                bodyReach[s] = sides.Count > 0 ? sides.Min() : typical;
            }

            // Arms hanging down beside the body (Kronk's, held low in an A-pose) make the outline as wide as the
            // shoulders all the way down to the hands, so it never widens where they start and they passed for
            // the body's sides: only the fingers moved with the fighter's arms. Cut across, though, they are
            // outlines of their own on each side of the body's. Where they are, for a good stretch of the height,
            // the body ends at its own outline, and above them (the shoulders) it goes on as wide as it is
            // under the armpits, so the top of the arm goes with the arm
            if (opt.Arms != "body")
            {
                var across = Neck.Across(pts, cutTris, min, size, slices);
                var own = new float[slices];
                int bottom = Math.Max(Slice(modelCrotch), 0), roof = Math.Min(neckSlice, top);
                for (int s = bottom; s < roof; s++)
                {
                    var runs = across[s];
                    if (runs.Count < 3)
                        continue;
                    var body = runs.MinBy(r => r.Lo <= cx && cx <= r.Hi ? 0 : MathF.Min(MathF.Abs(r.Lo - cx), MathF.Abs(r.Hi - cx)));
                    if (runs.Any(r => r.Lo > body.Hi) && runs.Any(r => r.Hi < body.Lo))
                        own[s] = MathF.Max(cx - body.Lo, body.Hi - cx);
                }
                // the longest stretch with arms apart on both sides
                int bestFrom = -1, bestLen = 0;
                for (int s = bottom; s < roof; s++)
                {
                    int e = s;
                    while (e < roof && own[e] > 0) e++;
                    if (e - s > bestLen) { bestLen = e - s; bestFrom = s; }
                    s = e;
                }
                if (bestLen >= slices * 3 / 20)
                {
                    int armpit = bestFrom + bestLen - 1;
                    for (int s = bestFrom; s <= armpit; s++)
                        bodyReach[s] = MathF.Min(bodyReach[s], own[s]);
                    for (int s = armpit + 1; s < roof && reach[s] > own[armpit] * 1.05f; s++)
                        bodyReach[s] = MathF.Min(bodyReach[s], own[armpit]);
                    HangingArms = true;
                    Report += $"arms hang apart from the body from {(float)bestFrom / slices:P0} up to the armpits at {(armpit + 1f) / slices:P0} of the height\n";
                }
            }

            ModelRegion = new int[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                var q = pts[i];
                float t = (q.Y - min.Y) / size.Y;
                bool plus = q.X >= cx;
                if (legSide != null ? legSide[i] != 0 : t < modelCrotch)
                    ModelRegion[i] = (legSide != null ? legSide[i] > 0 : plus) ? LegLeft : LegRight;
                else if (MathF.Abs(q.X - cx) > bodyReach[Slice(t)] * 1.05f)
                    ModelRegion[i] = plus ? ArmLeft : ArmRight;
                else
                    ModelRegion[i] = Body;
            }

            // a region the fighter doesn't have (no arms to copy from) falls back to the body
            for (int i = 0; i < pts.Length; i++)
                if (!FighterRegion.Contains(ModelRegion[i]))
                    ModelRegion[i] = Body;

            // an outline shell sits a little outside the shape it was measured without: each of its points
            // goes with the nearest point of the shape (or the body's sides come out as arms)
            var shapeIdx = Enumerable.Range(0, pts.Length).Where(i => inShape[i]).ToArray();
            if (shapeIdx.Length < pts.Length)
                for (int i = 0; i < pts.Length; i++)
                    if (!inShape[i])
                        ModelRegion[i] = ModelRegion[shapeIdx.MinBy(j => Vector3.DistanceSquared(pts[i], pts[j]))];

            TurnArms(pts, skeleton, opt, scale * grow);

            // A fighter whose legs part well below its hip joints (Ganondorf, under his tabard) swings its thighs
            // from up there. A model whose legs show only below a skirt or kilt (Kronk's) has its leg tops lined
            // up with where the fighter's legs part, but the skirt above them went with the hips alone: in a kick
            // the thigh swung out from under it and the leg came off the body. The skirt, from the hem up to the
            // hip joints, is worn over the legs: it takes them along as clothes do
            if (ClothesOver == null && !stubby && !coat && !opt.Dress && legSide == null && opt.Legs == "auto" && opt.Clothes == "outside"
                && opt.Roles != null && new[] { "lle", "rle" }.All(opt.Roles.ContainsKey))
            {
                float hip = ((skeleton.Position(opt.Roles["lle"]).Y + skeleton.Position(opt.Roles["rle"]).Y) / 2 - fighter.Min.Y) / fighterSize.Y;
                float skirtTop = modelCrotch + (hip - fighterCrotch) / (1 - fighterCrotch) * (1 - modelCrotch);
                if (hip - fighterCrotch > 0.12f)
                {
                    var skirt = new bool[pts.Length];
                    for (int i = 0; i < pts.Length; i++)
                    {
                        float t = (pts[i].Y - min.Y) / size.Y;
                        skirt[i] = ModelRegion[i] == Body && t >= modelCrotch && t <= skirtTop;
                    }
                    if (skirt.Any(b => b))
                    {
                        ClothesOver = skirt;
                        ClothesUnder = new bool[pts.Length];
                        SkirtToHips = true;
                        Report += $"skirt: the fighter's legs part at {fighterCrotch:P0} of its height but swing from its hips at {hip:P0}; the model from its crotch up to {skirtTop:P0} goes with the legs as clothes over them\n";
                    }
                }
            }

            // the fighter's body outline at each height, to line the two bodies' sides up
            float fcx = (fighter.Min.X + fighter.Max.X) / 2;
            var fighterReach = new float[slices];
            for (int k = 0; k < fighter.Vertices.Count; k++)
                if (FighterRegion[k] == Body)
                {
                    var q = fighter.Vertices[k].Position;
                    int s = Slice((q.Y - fighter.Min.Y) / fighterSize.Y);
                    fighterReach[s] = MathF.Max(fighterReach[s], MathF.Abs(q.X - fcx));
                }
            for (int s = 1; s < slices; s++)
                if (fighterReach[s] <= 0) fighterReach[s] = fighterReach[s - 1];

            // each arm runs from the body's side to its tip, on the model and on the fighter
            var arm = new Dictionary<int, (float root, float tip, float y, float z, float fRoot, float fTip, float fy, float fz)>();
            foreach (int region in new[] { ArmLeft, ArmRight })
            {
                var mine = Enumerable.Range(0, pts.Length).Where(i => ModelRegion[i] == region).ToList();
                var theirs = Enumerable.Range(0, fighter.Vertices.Count).Where(k => FighterRegion[k] == region).Select(k => fighter.Vertices[k].Position).ToList();
                if (mine.Count == 0 || theirs.Count == 0)
                    continue;
                var fx = theirs.Select(q => MathF.Abs(q.X - fcx)).OrderBy(x => x).ToList();
                arm[region] = (
                    mine.Average(i => bodyReach[Slice((pts[i].Y - min.Y) / size.Y)]),
                    mine.Max(i => MathF.Abs(pts[i].X - cx)),
                    mine.Average(i => pts[i].Y),
                    mine.Average(i => pts[i].Z),
                    fx[fx.Count / 50], fx[^1],
                    theirs.Average(q => q.Y),
                    theirs.Average(q => q.Z));
            }

            // An arm that sits far below or above the fighter's arm swings around the fighter's shoulder
            // and wrist from a distance, flinging it about. With a bone map the arm is moved onto the
            // fighter's arm instead; without one, the further off it is, the more it stays with the body.
            ArmLookup = new Vector3?[pts.Length];
            ArmU = new float[pts.Length];
            foreach (var (region, a) in arm)
            {
                string side = region == ArmLeft ? "l" : "r";
                // only an arm already near the fighter's is moved onto it: lifting a low arm up to the
                // shoulder bends it into a W that flaps about when running
                float fittedY = (a.y - origin.Y) * scale * opt.Height + target.Y;
                // a fighter whose arms hang down in its bind pose (Ganondorf) has its arm's middle down at its
                // waist, so a model's arm held out higher up (a T-pose) counts as near the fighter's shoulder too
                float off = MathF.Abs(fittedY - a.fy);
                if (fittedY > a.fy && opt.Roles != null && opt.Roles.TryGetValue(side + "sh", out int shoulder)
                    && opt.Roles.TryGetValue(side + "ha", out int hand))
                {
                    var hang = skeleton.Position(hand) - skeleton.Position(shoulder);
                    if (hang.Y < -0.5f * hang.Length())
                        off = MathF.Min(off, MathF.Abs(fittedY - skeleton.Position(shoulder).Y));
                }
                bool near = opt.Arms switch { "fighter" => true, "body" => false, _ => off < 0.15f * fighterSize.Y };
                if (opt.Conform && near && opt.Roles != null && opt.Roles.TryGetValue(side + "sh", out int sh) && opt.Roles.TryGetValue(side + "ha", out int ha))
                {
                    var chain = new List<Vector3> { skeleton.Position(sh) };
                    if (opt.Roles.TryGetValue(side + "el", out int el))
                        chain.Add(skeleton.Position(el));
                    chain.Add(skeleton.Position(ha));
                    // a hand that's the elbow itself (Samus's cannon) adds no length
                    if (chain.Count > 2 && Vector3.Distance(chain[^1], chain[^2]) < 1e-3f)
                        chain.RemoveAt(chain.Count - 1);
                    // past the wrist to the fingertips, as far as the fighter's arm reaches
                    float past = MathF.Max(a.fTip - MathF.Abs(chain[^1].X - fcx), 0);
                    var dir = Vector3.Normalize(chain[^1] - chain[^2]);
                    chain.Add(chain[^1] + dir * (past / MathF.Max(MathF.Abs(dir.X), 0.3f)));
                    ConformArm(region, chain, pts, cx, (q, s) => bodyReach[s], Slice, min, size, origin, scale, target);
                    if (Hung.Contains(region))
                        Wrist[region] = (ha, Vector3.Distance(chain[0], chain[^2]) / MathF.Max(Enumerable.Range(1, chain.Count - 1).Sum(k => Vector3.Distance(chain[k - 1], chain[k])), 1e-4f));
                    Conformed.Add(region);
                    Report += $"{RegionNames[region]}: moved onto the fighter's arm\n";
                    continue;
                }
                float fitted = (a.y - origin.Y) * scale * opt.Height + target.Y;
                float apart = MathF.Abs(fitted - a.fy) / MathF.Max(a.fTip - a.fRoot, 1e-4f);
                // "arms = fighter" means the arms are the fighter's arms, even where the model keeps its shape
                LimbFollow[region] = opt.Arms == "fighter" ? 1 : Math.Clamp(1 - apart, 0.2f, 1);
                Report += $"{RegionNames[region]}: {fitted:F1} high next to the fighter's {a.fy:F1}, follows the arm bones {LimbFollow[region]:P0}\n";
            }

            if (opt.Conform && opt.Roles != null && legSide == null && float.IsNaN(HipY))
                ConformFeet(opt.Roles, skeleton, pts, cx, min, size, modelCrotch);

            Lookup = new Vector3[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                var q = pts[i];
                var l = (q - origin) * stretch + target;
                float t = (q.Y - min.Y) / size.Y;
                l.Y = HeightOnFighter(t);

                int region = ModelRegion[i];
                if (legSide != null && legSide[i] != 0)
                    l = Positions[i]; // already on the fighter's leg
                else if (region == Body)
                {
                    // the body's sides go to the fighter's sides at the matching height
                    float side = MathF.Max(bodyReach[Slice(t)], 1e-4f);
                    float fside = fighterReach[Slice((l.Y - fighter.Min.Y) / fighterSize.Y)];
                    l.X = fcx + (q.X - cx) / side * fside;
                }
                else if (ArmLookup[i] is Vector3 onArm)
                    l = onArm;
                else if (arm.TryGetValue(region, out var a))
                {
                    // along the arm from shoulder to tip, at the same share of the way on the fighter
                    float u = Math.Clamp((MathF.Abs(q.X - cx) - a.root) / MathF.Max(a.tip - a.root, 1e-4f), 0, 1);
                    float k = (a.fTip - a.fRoot) / MathF.Max(a.tip - a.root, 1e-4f);
                    l.X = fcx + MathF.Sign(q.X - cx) * (a.fRoot + u * (a.fTip - a.fRoot));
                    l.Y = a.fy + (q.Y - a.y) * k;
                    l.Z = a.fz + (q.Z - a.z) * k;
                }
                Lookup[i] = l;
            }

            // Four legs: a model on all fours stood up (Spyro sitting like a dog) has its front paws on the
            // floor in front of its hind ones. Those front legs work as the fighter's arms, as they would
            // on a hand-made skin: they follow shoulder, elbow and hand from the top of the leg down to the
            // paw, so they reach out in the fighter's attacks instead of dragging along with its strides.
            if (legSide == null && !float.IsNaN(HipY) && opt.Arms != "body" && opt.Roles != null
                && new[] { "lsh", "lel", "lha", "rsh", "rel", "rha" }.All(opt.Roles.ContainsKey)
                && FighterRegion.Contains(ArmLeft) && FighterRegion.Contains(ArmRight))
            {
                var paws = Enumerable.Range(0, pts.Length).Where(i => (pts[i].Y - min.Y) / size.Y < 0.06f).Select(i => pts[i].Z).OrderBy(z => z).ToList();
                // the widest gap between the paws on the floor, front to back
                float gap = 0, split = float.NaN;
                for (int k = 1; k < paws.Count; k++)
                    if (paws[k] - paws[k - 1] > gap) { gap = paws[k] - paws[k - 1]; split = (paws[k] + paws[k - 1]) / 2; }
                int front = paws.Count(z => z > split), back = paws.Count - front;
                // a real gap (a good part of the body's depth), with feet on both sides of it
                float depth = paws.Count > 0 ? paws[^1] - paws[0] : 0;
                // and no foot across it: a long shoe (Gusteau's) has a gap between heel and toe points too,
                // but the sole joins them low down, where a front paw and a hind one only meet up in the body
                var low = new int[pts.Length];
                for (int i = 0; i < low.Length; i++) low[i] = i;
                int Root(int i) { while (low[i] != i) i = low[i] = low[low[i]]; return i; }
                bool Low(int i) => (pts[i].Y - min.Y) / size.Y < 0.12f;
                foreach (var t in model.Triangles)
                    if (Low(t.A.Position) && Low(t.B.Position) && Low(t.C.Position))
                    {
                        low[Root(t.A.Position)] = Root(t.B.Position);
                        low[Root(t.B.Position)] = Root(t.C.Position);
                    }
                var frontFeet = Enumerable.Range(0, pts.Length).Where(i => (pts[i].Y - min.Y) / size.Y < 0.06f && pts[i].Z > split).Select(Root).ToHashSet();
                bool shoe = Enumerable.Range(0, pts.Length).Any(i => (pts[i].Y - min.Y) / size.Y < 0.06f && pts[i].Z < split && frontFeet.Contains(Root(i)));
                if (gap > 0.15f * depth && front >= 4 && back >= 4 && !shoe)
                {
                    // the front legs: in front of the gap, up to where they join the chest
                    float frontTop = Math.Min(modelCrotch * 1.3f, 0.45f);
                    var legs = Enumerable.Range(0, pts.Length).Where(i => pts[i].Z > split && (pts[i].Y - min.Y) / size.Y < frontTop).ToList();
                    foreach (bool left in new[] { true, false })
                    {
                        var leg = legs.Where(i => pts[i].X >= cx == left).ToList();
                        if (leg.Count == 0)
                            continue;
                        string side = left ? "l" : "r";
                        var chain = new[] { "sh", "el", "ha" }.Select(r => skeleton.Position(opt.Roles[side + r])).ToList();
                        // past the wrist, half the forearm again, for the paw
                        chain.Add(chain[2] + (chain[2] - chain[1]) * 0.5f);
                        var lengths = Enumerable.Range(1, chain.Count - 1).Select(k => Vector3.Distance(chain[k - 1], chain[k])).ToArray();
                        float total = lengths.Sum();
                        Vector3 Along(float u)
                        {
                            float d = u * total;
                            for (int k = 0; k < lengths.Length; k++)
                            {
                                if (d <= lengths[k] || k == lengths.Length - 1)
                                    return Vector3.Lerp(chain[k], chain[k + 1], Math.Clamp(d / MathF.Max(lengths[k], 1e-4f), 0, 1));
                                d -= lengths[k];
                            }
                            return chain[^1];
                        }
                        // the leg's own middle line, from its top down to the paw
                        float legHigh = leg.Max(i => Positions[i].Y), legLow = leg.Min(i => Positions[i].Y);
                        float U(int i) => Math.Clamp((legHigh - Positions[i].Y) / MathF.Max(legHigh - legLow, 1e-4f), 0, 1);
                        Vector3 Middle(float u)
                        {
                            var near = leg.OrderBy(i => MathF.Abs(U(i) - u)).Take(Math.Max(3, leg.Count / 5)).ToList();
                            return near.Aggregate(Vector3.Zero, (acc, i) => acc + Positions[i]) / near.Count;
                        }
                        foreach (int i in leg)
                        {
                            ModelRegion[i] = left ? ArmLeft : ArmRight;
                            float u = U(i);
                            // the leg is bent up along the arm, the top staying where it joins the chest, so the
                            // paws are held in front like hands (and stay out of the floor when the arms swing)
                            var bent = Along(u) + (Positions[i] - Middle(u)) * 0.8f;
                            float k = SmoothStep(0, 0.35f, u);
                            Positions[i] = Vector3.Lerp(Positions[i], bent, k);
                            Lookup[i] = Vector3.Lerp(Lookup[i], Along(u), k);
                        }
                    }
                    LimbFollow[ArmLeft] = LimbFollow[ArmRight] = 1;
                    FourLegs = true;
                    Report += $"four legs: the front legs ({legs.Count} points, up to {frontTop:P0} of the height) are held up as the fighter's arms\n";
                }
            }

            Report += "model regions: " + string.Join(", ", Enumerable.Range(0, RegionNames.Length)
                .Select(r => (r, c: ModelRegion.Count(m => m == r))).Where(e => e.c > 0).Select(e => $"{RegionNames[e.r]} {e.c}")) + "\n";

            Report += $"legs: the model's split at {modelCrotch:P0} of its height lines up with the top of the fighter's legs at {fighterCrotch:P0}\n";

            Report += $"fitted size x{scale:F2}: {size.Y * scale * opt.Height:F1} tall next to the fighter's {fighterSize.Y:F1}, {size.X * scale * opt.Width:F1} wide next to its {fighterSize.X:F1}\n";
        }

        /// <summary>
        /// Turns the model's arms at the shoulder, before they're weighted. Arms modelled hanging down (Mr.
        /// Incredible's A-pose) on a fighter whose arms are held out (Falcon's T-pose) sat far below the fighter's
        /// arms, so they only half followed them and stretched whenever the fighter lifted its arms. They're lifted
        /// to the fighter's angle instead, and then moved onto its arms like any arm held out. <see cref="Options.ArmAngle"/>
        /// sets the angle by hand. Each arm turns about the top of the arm, where it leaves the shoulder, easing in
        /// over its first stretch so the shoulder bends rather than tears; its hand turns with it. <paramref name="k"/>
        /// takes the model's units to the fitted ones (sizes and stretches), so the angles are the fitted model's.
        /// </summary>
        private void TurnArms(Vector3[] pts, Skeleton skeleton, Options opt, Vector3 k)
        {
            var grow = k / k.X;
            var arms = new List<(int region, List<int> members, Vector3 pivot, float angle, float tmin, float tmax, Vector3 dir)>();
            foreach (int region in new[] { ArmLeft, ArmRight })
            {
                var members = Enumerable.Range(0, pts.Length).Where(i => ModelRegion[i] == region).ToList();
                if (members.Count < 8)
                    continue;
                float sign = region == ArmLeft ? 1 : -1;
                var g = members.Select(i => pts[i] * grow).ToList();
                var mean = g.Aggregate(Vector3.Zero, (a, q) => a + q) / g.Count;
                // the arm's main line, the way its points spread the most (power iteration on their spread)
                float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
                foreach (var q in g)
                {
                    var d = q - mean;
                    xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
                }
                var dir = new Vector3(sign, -1, 0);
                for (int it = 0; it < 32; it++)
                {
                    var n = new Vector3(xx * dir.X + xy * dir.Y + xz * dir.Z, xy * dir.X + yy * dir.Y + yz * dir.Z, xz * dir.X + yz * dir.Y + zz * dir.Z);
                    if (n.LengthSquared() < 1e-12f)
                        break;
                    dir = Vector3.Normalize(n);
                }
                if (dir.X * sign < 0)
                    dir = -dir;
                var t = g.Select(q => Vector3.Dot(q - mean, dir)).ToList();
                float tmin = t.Min(), tmax = t.Max();
                if (tmax - tmin < 1e-5f)
                    continue;
                // where the arm leaves the shoulder: the middle of its innermost stretch
                var inner = Enumerable.Range(0, g.Count).Where(j => t[j] < tmin + 0.15f * (tmax - tmin)).ToList();
                var pivot = inner.Aggregate(Vector3.Zero, (a, j) => a + g[j]) / inner.Count;
                float angle = MathF.Atan2(-dir.Y, MathF.Abs(dir.X)) * 180 / MathF.PI;
                arms.Add((region, members, pivot, angle, tmin - Vector3.Dot(pivot - mean, dir), tmax - Vector3.Dot(pivot - mean, dir), dir));
            }
            if (arms.Count == 0)
                return;

            // the fighter's arms, shoulder to hand, in its bind pose
            var theirs = new List<float>();
            foreach (var side in new[] { "l", "r" })
                if (opt.Roles != null && opt.Roles.TryGetValue(side + "sh", out int sh) && opt.Roles.TryGetValue(side + "ha", out int ha))
                {
                    var d = skeleton.Position(ha) - skeleton.Position(sh);
                    if (MathF.Abs(d.X) + MathF.Abs(d.Y) > 1e-4f)
                        theirs.Add(MathF.Atan2(-d.Y, MathF.Abs(d.X)) * 180 / MathF.PI);
                }
            float mine = arms.Average(a => a.angle);
            float fighterAngle = theirs.Count > 0 ? theirs.Average() : float.NaN;
            static int Deg(float a) => (int)MathF.Round(a) + 0;
            string found = $"arms: the model's point {Deg(mine)} degrees down" + (theirs.Count > 0 ? $", the fighter's {Deg(fighterAngle)}" : "");
            // only arms clearly hanging below the fighter's are lifted: a T-pose a little off stays as it is, and
            // a fighter whose own arms hang (Ganondorf) has them bent down along its arms later (ConformArm).
            // And only a pair that looks like arms: one each side, hanging alike, running across the front rather
            // than back (Rex's big tail, sticking out sideways behind him, was taken for arms and swung up)
            bool pair = arms.Count == 2 && MathF.Abs(arms[0].angle - arms[1].angle) < 15 && arms.All(a => MathF.Abs(a.dir.Z) < 0.5f);
            float want = !float.IsNaN(opt.ArmAngle) ? opt.ArmAngle
                : pair && opt.Arms != "body" && opt.Conform && !float.IsNaN(fighterAngle) && mine >= 25 && mine <= 75 && mine - fighterAngle >= 20 ? fighterAngle : float.NaN;
            if (float.IsNaN(want) || MathF.Abs(want - mine) < 1)
            {
                Report += found + "\n";
                return;
            }
            Report += found + $"; turned to {Deg(want)} at the shoulder ({(float.IsNaN(opt.ArmAngle) ? "lifted to the fighter's" : "set by hand")})\n";

            // normals are kept per corner: each turns with its point
            var turns = new Dictionary<int, Matrix4x4>();
            foreach (var a in arms)
            {
                float sign = a.region == ArmLeft ? 1 : -1;
                // lifting a +x arm turns it about +z, a -x arm the other way
                float turn = (a.angle - want) * MathF.PI / 180 * sign;
                foreach (int i in a.members)
                {
                    var q = pts[i] * grow;
                    float u = Math.Clamp(Vector3.Dot(q - a.pivot, a.dir) / MathF.Max(a.tmax, 1e-5f), 0, 1);
                    var r = Matrix4x4.CreateRotationZ(turn * SmoothStep(0, 0.3f, u));
                    var moved = (a.pivot + Vector3.Transform(q - a.pivot, r)) / grow;
                    Positions[i] += (moved - pts[i]) * k;
                    pts[i] = moved;
                    turns[i] = r;
                }
            }
            var done = new HashSet<int>();
            foreach (var t in Corners)
                foreach (var c in new[] { t.A, t.B, t.C })
                    if (c.Normal >= 0 && turns.TryGetValue(c.Position, out var r) && done.Add(c.Normal))
                        Normals[c.Normal] = Vector3.TransformNormal(Normals[c.Normal], r);
        }

        /// <summary>
        /// Whether the model's body above its crotch is slim for its height: the typical half-width of its
        /// slices (arms stick out of only a few) under a fifth of its height.
        /// </summary>
        private static bool Slim(Vector3[] pts, float cx, float minY, float height, float crotch)
        {
            const int slices = 50;
            var reach = new float[slices];
            foreach (var q in pts)
            {
                float t = (q.Y - minY) / height;
                if (t < crotch)
                    continue;
                int s = Math.Clamp((int)(t * slices), 0, slices - 1);
                reach[s] = MathF.Max(reach[s], MathF.Abs(q.X - cx));
            }
            var used = reach.Where(r => r > 0).OrderBy(r => r).ToList();
            return used.Count > 0 && used[used.Count / 2] < height * 0.2f;
        }

        /// <summary>
        /// Moves an arm onto the fighter's arm: each point keeps its distance from the arm's middle line,
        /// and its place along the arm becomes the same share of the way along the fighter's
        /// shoulder-elbow-hand-fingertip line. Near the shoulder it blends back into the body.
        /// </summary>
        private void ConformArm(int region, List<Vector3> chain, Vector3[] pts, float cx, Func<Vector3, int, float> bodyReach,
            Func<float, int> slice, Vector3 min, Vector3 size, Vector3 origin, float scale, Vector3 target)
        {
            var members = Enumerable.Range(0, pts.Length).Where(i => ModelRegion[i] == region).ToList();
            // An arm hanging apart from the body, held well down from the shoulder (Kronk's A-pose), runs along its
            // own slant, not sideways: its hand hangs and its fingers curl back in, so how far out sideways a point
            // is says little about how far along the arm it is (the fingertips counted as the top of the arm and
            // went to the shoulder, stretching into spikes). Its place along the arm is measured along that slant
            // instead, from the top of the shoulder to the hand
            float Out(int i) => MathF.Abs(pts[i].X - cx) - bodyReach(pts[i], slice((pts[i].Y - min.Y) / size.Y));
            var top = members.OrderByDescending(i => pts[i].Y).Take(Math.Max(1, members.Count / 10)).ToList();
            var armRoot = top.Aggregate(Vector3.Zero, (a, i) => a + pts[i]) / top.Count;
            var slant = pts[members.MaxBy(Out)] - armRoot;
            bool slanted = HangingArms && slant.LengthSquared() > 1e-8f && -slant.Y > 0.5f * slant.Length();
            float U(int i)
            {
                if (slanted)
                    return Math.Clamp(Vector3.Dot(pts[i] - armRoot, slant) / slant.LengthSquared(), 0, 1);
                float root = bodyReach(pts[i], slice((pts[i].Y - min.Y) / size.Y));
                return Math.Clamp((MathF.Abs(pts[i].X - cx) - root) / MathF.Max(tipOf(), 1e-4f), 0, 1);
            }
            float tipCache = -1;
            float tipOf()
            {
                if (tipCache < 0)
                    tipCache = slanted ? slant.Length() : members.Max(i => MathF.Abs(pts[i].X - cx) - bodyReach(pts[i], slice((pts[i].Y - min.Y) / size.Y)));
                return tipCache;
            }

            // the model arm's middle line, in a few steps along it
            const int bins = 8;
            var sum = new Vector3[bins];
            var count = new int[bins];
            foreach (var i in members)
            {
                int b = Math.Min(bins - 1, (int)(U(i) * bins));
                sum[b] += Positions[i];
                count[b]++;
            }
            var middle = new Vector3[bins];
            for (int b = 0; b < bins; b++)
                middle[b] = count[b] > 0 ? sum[b] / count[b] : Vector3.Zero;
            for (int b = 1; b < bins; b++)
                if (count[b] == 0) middle[b] = middle[b - 1];
            for (int b = bins - 2; b >= 0; b--)
                if (count[b] == 0) middle[b] = middle[b + 1];

            var lengths = new List<float> { 0 };
            for (int k = 1; k < chain.Count; k++)
                lengths.Add(lengths[^1] + Vector3.Distance(chain[k - 1], chain[k]));
            Vector3 Along(float u)
            {
                float d = u * lengths[^1];
                for (int k = 1; k < chain.Count; k++)
                    if (d <= lengths[k] || k == chain.Count - 1)
                        return Vector3.Lerp(chain[k - 1], chain[k], Math.Clamp((d - lengths[k - 1]) / MathF.Max(lengths[k] - lengths[k - 1], 1e-4f), 0, 1));
                return chain[^1];
            }

            // which way the fighter's arm runs at a share of the way along it
            Vector3 Direction(float u)
            {
                float d = u * lengths[^1];
                int k = 1;
                while (k < chain.Count - 1 && d > lengths[k])
                    k++;
                var dir = chain[k] - chain[k - 1];
                return dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : Vector3.UnitX * MathF.Sign(chain[^1].X - chain[0].X);
            }

            // an arm hanging down from the fighter's shoulder (Ganondorf's) starts where the model's arm
            // leaves its body instead: a wide body's arm would otherwise run in over its shoulder to the
            // fighter's and drop from there, a flat ledge sticking out of the body. Its weights still come
            // from the fighter's own arm, where it hangs.
            bool hangs = Direction(0).Y < -0.5f;
            var shift = hangs ? middle[0] - chain[0] : Vector3.Zero;
            // and keeps its own length: a short arm (Gusteau's) stretched to the fighter's fingertips reaches
            // far past where its own hand would be, its weights still spread along the whole fighter's arm
            float own = tipOf() * scale;
            float keep = hangs ? Math.Clamp(own / MathF.Max(lengths[^1], 1e-4f), 0.5f, 1) : 1;
            if (hangs)
                Report += $"{RegionNames[region]}: {own:F1} long next to the fighter's {lengths[^1]:F1}, keeps {keep:P0} of its length\n";

            foreach (var i in members)
            {
                float u = U(i);
                float fb = u * bins - 0.5f;
                int b0 = Math.Clamp((int)MathF.Floor(fb), 0, bins - 1), b1 = Math.Min(bins - 1, b0 + 1);
                var mid = Vector3.Lerp(middle[b0], middle[b1], Math.Clamp(fb - b0, 0, 1));
                var offset = Positions[i] - mid;
                // the model's arm runs sideways (or down its slant); where the fighter's points down (Ganondorf's
                // hang at his sides) the thickness turns with it, or the arm would be squashed flat along its own length
                var across = slanted ? Vector3.Normalize(slant) : new Vector3(MathF.Sign(Positions[i].X - cx), 0, 0);
                // the arm's length is the fighter's now; keep only its thickness
                offset -= Vector3.Dot(offset, across) * across;
                var to = Direction(u);
                var axis = Vector3.Cross(across, to);
                if (hangs && axis.LengthSquared() > 1e-8f)
                    offset = Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(Math.Clamp(Vector3.Dot(across, to), -1, 1))));
                var moved = chain[0] + (Along(u) - chain[0]) * keep + shift + offset;
                if (hangs)
                {
                    Hung.Add(region);
                    ArmLookup[i] = Along(u) + offset;
                    ArmU[i] = u;
                }
                float blend = SmoothStep(0, 0.35f, u);
                Positions[i] = Vector3.Lerp(Positions[i], moved, blend);
            }
        }

        /// <summary>
        /// Places a model whose legs were made by <see cref="Legs.Split"/>: the body from the fighter's
        /// hips to the top of its head, each leg narrowed to a leg's thickness and run down the fighter's
        /// hip, knee and ankle to the ground. Returns the body's scale.
        /// </summary>
        private float MakeLegs(int[] legSide, Vector3[] pts, Vector3 min, Vector3 size, FighterMesh fighter, Vector3 fighterSize, float zMid, Skeleton sk, Options opt)
        {
            var roles = opt.Roles;
            float cutY = min.Y + opt.LegCut * size.Y;
            float hipY = (sk.Position(roles["lle"]).Y + sk.Position(roles["rle"]).Y) / 2;
            HipY = hipY;
            FloorY = fighter.Min.Y;
            float scale = (fighter.Max.Y - hipY) / ((1 - opt.LegCut) * size.Y) * opt.Scale;
            float cx = (min.X + max(pts, q => q.X)) / 2;
            static float max(Vector3[] a, Func<Vector3, float> f) => a.Max(f);
            float cz = pts.Average(q => q.Z);
            float fcx = (fighter.Min.X + fighter.Max.X) / 2;

            var origin = new Vector3(cx, cutY, cz);
            var target = new Vector3(fcx, hipY, zMid);
            Positions = pts.Select(q => (q - origin) * scale * new Vector3(opt.Width, opt.Height, 1) + target).ToArray();

            float radius = 0.1f * fighterSize.Y * opt.Scale;
            foreach (int s in new[] { 1, -1 })
            {
                string r = s > 0 ? "l" : "r";
                var mine = Enumerable.Range(0, pts.Length).Where(i => legSide[i] == s).ToList();
                if (mine.Count == 0)
                    continue;

                // the fighter's leg, top to bottom: hip, knee, ankle, and the ground under the ankle
                var chain = new List<Vector3> { sk.Position(roles[r + "le"]) };
                if (roles.TryGetValue(r + "kn", out int kn)) chain.Add(sk.Position(kn));
                if (roles.TryGetValue(r + "fb", out int fb) || roles.TryGetValue(r + "ff", out fb)) chain.Add(sk.Position(fb));
                chain.Add(new Vector3(chain[^1].X, fighter.Min.Y, chain[^1].Z));
                chain = chain.OrderByDescending(q => q.Y).ToList();
                Vector3 At(float y)
                {
                    for (int k = 1; k < chain.Count; k++)
                        if (y >= chain[k].Y || k == chain.Count - 1)
                            return Vector3.Lerp(chain[k - 1], chain[k], Math.Clamp((chain[k - 1].Y - y) / MathF.Max(chain[k - 1].Y - chain[k].Y, 1e-4f), 0, 1));
                    return chain[^1];
                }

                var middle = new Vector2(mine.Average(i => pts[i].X), mine.Average(i => pts[i].Z));
                var dx = mine.Select(i => MathF.Abs(pts[i].X - middle.X)).OrderBy(d => d).ToList();
                var dz = mine.Select(i => MathF.Abs(pts[i].Z - middle.Y)).OrderBy(d => d).ToList();
                float sx = radius / MathF.Max(dx[dx.Count * 9 / 10], 1e-4f);
                // a little deeper than wide, so the feet still point forwards
                float sz = radius * 1.4f / MathF.Max(dz[dz.Count * 9 / 10], 1e-4f);

                // the feet keep their shape at the bottom; only the part above them is stretched into a leg
                float footTop = MathF.Min(0.06f * size.Y, (cutY - min.Y) * 0.5f);
                float footHeight = footTop * scale;
                foreach (var i in mine)
                {
                    float h = pts[i].Y - min.Y;
                    float y = h < footTop
                        ? fighter.Min.Y + h * scale
                        : fighter.Min.Y + footHeight + (h - footTop) / MathF.Max(cutY - min.Y - footTop, 1e-4f) * (hipY - fighter.Min.Y - footHeight);
                    float foot = h < footTop ? 1 - h / footTop : 0;
                    var c = At(y);
                    float kx = sx + (MathF.Min(scale, sx * 1.6f) - sx) * foot, kz = sz + (MathF.Min(scale, sz * 1.6f) - sz) * foot;
                    Positions[i] = new Vector3(c.X + (pts[i].X - middle.X) * kx, y, c.Z + (pts[i].Z - middle.Y) * kz);
                }
            }
            return scale;
        }

        /// <summary>
        /// Slides each foot sideways and forwards onto the fighter's foot, fading out up the body.
        /// </summary>
        private void ConformFeet(Dictionary<string, int> roles, Skeleton sk, Vector3[] pts, float cx, Vector3 min, Vector3 size, float crotch)
        {
            foreach (var side in new[] { "l", "r" })
            {
                var feet = new[] { "fb", "ff" }.Where(r => roles.ContainsKey(side + r)).Select(r => sk.Position(roles[side + r])).ToList();
                int region = side == "l" ? LegLeft : LegRight;
                var members = Enumerable.Range(0, pts.Length).Where(i => ModelRegion[i] == region).ToList();
                if (feet.Count == 0 || members.Count == 0)
                    continue;

                var theirs = feet.Aggregate(Vector3.Zero, (a, q) => a + q) / feet.Count;
                var mine = members.Aggregate(Vector3.Zero, (a, i) => a + Positions[i]) / members.Count;
                var shift = new Vector3(theirs.X - mine.X, 0, theirs.Z - mine.Z);
                float reachX = MathF.Abs(mine.X - Positions.Average(q => q.X));

                for (int i = 0; i < pts.Length; i++)
                {
                    float t = (pts[i].Y - min.Y) / size.Y;
                    bool onSide = side == "l" ? pts[i].X >= cx : pts[i].X < cx;
                    if (!onSide || t > crotch + 0.15f)
                        continue;
                    float up = t <= crotch ? 1 : 1 - (t - crotch) / 0.15f;
                    float across = ModelRegion[i] == region ? 1 : Math.Clamp(MathF.Abs(pts[i].X - cx) * (Positions[i].X - cx == 0 ? 0 : 1) / MathF.Max(reachX, 1e-4f), 0, 1);
                    Positions[i] += shift * up * across;
                }
            }
        }

        /// <summary>
        /// Finds the model's tail the way arms are found, but backwards: at the heights where the model reaches
        /// much further behind its middle than it usually does, what sticks out is tail. Its weights run
        /// along the fighter's tail bones by how far out each point is, blending in from the body at the root.
        /// </summary>
        private void TailWeights(float[][] w, int boneCount, int[] chain, bool bend)
        {
            int n = Positions.Length;
            float minY = Positions.Min(p => p.Y), maxY = Positions.Max(p => p.Y);
            float h = MathF.Max(maxY - minY, 1e-4f);
            float cx = (Positions.Min(p => p.X) + Positions.Max(p => p.X)) / 2;
            float halfW = (Positions.Max(p => p.X) - Positions.Min(p => p.X)) / 2;
            var zs = Positions.Select(p => p.Z).OrderBy(z => z).ToArray();
            float cz = zs[zs.Length / 2];

            const int slices = 24;
            int Slice(float y) => Math.Clamp((int)((y - minY) / h * slices), 0, slices - 1);
            // how far behind the middle the model reaches at each height, near the middle (not the arms)
            var reach = new float[slices];
            for (int i = 0; i < n; i++)
                if (MathF.Abs(Positions[i].X - cx) < halfW * 0.5f)
                {
                    int s = Slice(Positions[i].Y);
                    reach[s] = MathF.Max(reach[s], cz - Positions[i].Z);
                }
            var filled = reach.Where(r => r > 0).OrderBy(r => r).ToArray();
            if (filled.Length == 0)
                return;
            float typical = filled[filled.Length / 2];
            float root = typical * 1.1f;
            // a tail doesn't grow out of the head: big ears laid back (Remy's) reach out behind too, and on the
            // tail bones they swing and stretch with every flick of the fighter's tail
            var tail = Enumerable.Range(0, n).Where(i => reach[Slice(Positions[i].Y)] > typical * 1.35f
                && MathF.Abs(Positions[i].X - cx) < halfW * 0.5f && cz - Positions[i].Z > root && Positions[i].Y < NeckYAt(Positions[i]) - h * 0.04f).ToList();
            if (tail.Count < n * 0.01f)
            {
                Report += "tail: none found behind the model; it follows the body\n";
                return;
            }

            float tip = tail.Max(i => cz - Positions[i].Z);
            var along = tail.ToDictionary(i => i, i => Math.Clamp((cz - Positions[i].Z - root) / MathF.Max(tip - root, 1e-4f), 0, 1));
            if (bend)
                ConformTail(tail, along, chain, tip - root);
            foreach (var i in tail)
            {
                float u = along[i];
                float f = u * (chain.Length - 1);
                int a = (int)MathF.Floor(f), b = Math.Min(a + 1, chain.Length - 1);
                var own = new float[boneCount];
                own[chain[a]] += 1 - (f - a);
                own[chain[b]] += f - a;
                float k = SmoothStep(0, 0.15f, u);
                for (int x = 0; x < boneCount; x++)
                    w[i][x] = (1 - k) * w[i][x] + k * own[x];
                Tail[i] = true;
            }
            Report += $"tail: {tail.Count} vertices behind the model follow the tail bones {string.Join(",", chain)}\n";
        }

        /// <summary>
        /// A tail laid out straight behind (Remy's, along the floor) on a fighter whose tail runs another way
        /// (Pikachu's goes up behind its back) is bent to run along the fighter's tail bones, from where it leaves
        /// the body, keeping its own length and thickness. Left where it is, its tip is far from the bones it
        /// follows: every flick swings it wide and down through the floor, and it never looks like the
        /// fighter's tail. Tails that already run about the fighter's way (Fox's hangs down behind) are left alone,
        /// and so are those of models stood up with --rotate (Spyro on all fours: his tail went stiff and flat).
        /// </summary>
        private void ConformTail(List<int> tail, Dictionary<int, float> along, int[] chain, float length)
        {
            if (Skel == null || chain.Length < 2 || length <= 0)
                return;
            var joints = chain.Select(j => Skel.Position(j)).ToList();
            var fighterDir = Vector3.Normalize(joints[^1] - joints[0]);
            // the model tail's middle line, in a few steps along it
            const int bins = 8;
            var sum = new Vector3[bins];
            var count = new int[bins];
            foreach (var i in tail)
            {
                int b = Math.Min(bins - 1, (int)(along[i] * bins));
                sum[b] += Positions[i];
                count[b]++;
            }
            var middle = new Vector3[bins];
            for (int b = 0; b < bins; b++)
                middle[b] = count[b] > 0 ? sum[b] / count[b] : Vector3.Zero;
            for (int b = 1; b < bins; b++)
                if (count[b] == 0) middle[b] = middle[b - 1];
            for (int b = bins - 2; b >= 0; b--)
                if (count[b] == 0) middle[b] = middle[b + 1];
            var modelDir = middle[bins - 1] - middle[0];
            if (modelDir.LengthSquared() < 1e-8f)
                return;
            modelDir = Vector3.Normalize(modelDir);
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(modelDir, fighterDir), -1, 1));
            if (angle < MathF.PI / 4)
                return;

            // the fighter's tail line from the model tail's root, and on past its last joint for a longer tail
            var line = new List<Vector3> { middle[0] };
            line.AddRange(joints.Skip(1).Select(j => j - joints[0] + middle[0]));
            var lengths = new List<float> { 0 };
            for (int k = 1; k < line.Count; k++)
                lengths.Add(lengths[^1] + Vector3.Distance(line[k - 1], line[k]));
            if (lengths[^1] < length)
            {
                line.Add(line[^1] + Vector3.Normalize(line[^1] - line[^2]) * (length - lengths[^1]));
                lengths.Add(length);
            }
            (Vector3 at, Vector3 dir) Along(float d)
            {
                int k = 1;
                while (k < line.Count - 1 && d > lengths[k])
                    k++;
                var seg = line[k] - line[k - 1];
                float t = Math.Clamp((d - lengths[k - 1]) / MathF.Max(lengths[k] - lengths[k - 1], 1e-4f), 0, 1);
                return (Vector3.Lerp(line[k - 1], line[k], t), Vector3.Normalize(seg));
            }

            var turns = new Dictionary<int, Quaternion>();
            foreach (var i in tail)
            {
                float u = along[i];
                float fb = u * bins - 0.5f;
                int b0 = Math.Clamp((int)MathF.Floor(fb), 0, bins - 1), b1 = Math.Min(bins - 1, b0 + 1);
                var mid = Vector3.Lerp(middle[b0], middle[b1], Math.Clamp(fb - b0, 0, 1));
                var offset = Positions[i] - mid;
                offset -= modelDir * Vector3.Dot(offset, modelDir);
                var (at, dir) = Along(u * length);
                var axis = Vector3.Cross(modelDir, dir);
                var turn = axis.LengthSquared() > 1e-8f
                    ? Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(Math.Clamp(Vector3.Dot(modelDir, dir), -1, 1)))
                    : Quaternion.Identity;
                // the root stays on the body, bending over into the fighter's line
                float k2 = SmoothStep(0, 0.2f, u);
                Positions[i] = Vector3.Lerp(Positions[i], at + Vector3.Transform(offset, turn), k2);
                turns[i] = Quaternion.Slerp(Quaternion.Identity, turn, k2);
            }
            // the normals turn with the tail
            var normalTurn = new Dictionary<int, Quaternion>();
            foreach (var t in Corners ?? new List<ObjModel.Triangle>())
                foreach (var c in new[] { t.A, t.B, t.C })
                    if (c.Normal >= 0 && turns.TryGetValue(c.Position, out var q))
                        normalTurn[c.Normal] = q;
            foreach (var (nrm, q) in normalTurn)
                if (nrm < Normals.Length)
                    Normals[nrm] = Vector3.Transform(Normals[nrm], q);
            Report += $"tail: bent {angle * 180 / MathF.PI:F0} degrees to run along the fighter's tail bones\n";
        }

        private static float SmoothStep(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        /// <summary>
        /// How far up (as a share of the height) the shape stays split into two legs: heights where
        /// there is surface on both sides but no edge crosses the middle.
        /// </summary>
        private static float Crotch(Vector3[] pts, IEnumerable<(int, int, int)> triangles, float centerX, float minY, float height, bool edges = false)
        {
            const int slices = 50;
            int Slice(float y) => Math.Clamp((int)((y - minY) / height * slices), 0, slices - 1);

            var left = new int[slices];
            var right = new int[slices];
            var crossings = new int[slices];
            foreach (var p in pts)
            {
                if (p.X < centerX) left[Slice(p.Y)]++;
                else right[Slice(p.Y)]++;
            }
            // a low-poly leg (Moe's) has heights with no point on them, which read as the legs ending: its
            // edges count at every height they pass too (only for the model's own legs, not when looking for
            // clothes over them: Cartman's stubs under his jacket aren't legs under a coat)
            foreach (var (a, b, c) in edges ? triangles : Enumerable.Empty<(int, int, int)>())
                foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                {
                    int from = Slice(pts[u].Y), to = Slice(pts[v].Y);
                    for (int s = Math.Min(from, to) + 1; s < Math.Max(from, to); s++)
                    {
                        float y = minY + (s + 0.5f) / slices * height;
                        float k = Math.Clamp((y - pts[u].Y) / (pts[v].Y - pts[u].Y), 0, 1);
                        if (pts[u].X + (pts[v].X - pts[u].X) * k < centerX) left[s]++;
                        else right[s]++;
                    }
                }
            // a triangle that reaches across the middle joins the two sides at the heights where it crosses
            // the middle (only there: a long low-poly triangle down the inside of a pair of shorts crosses at
            // the crotch, not at the hem); so does an edge lying on the middle, the seam of a mirrored model
            // (a single vertex there is just a toe pointing inward)
            float eps = (pts.Max(p => p.X) - pts.Min(p => p.X)) * 0.002f;
            foreach (var (a, b, c) in triangles)
            {
                float xa = pts[a].X - centerX, xb = pts[b].X - centerX, xc = pts[c].X - centerX;
                float lo = MathF.Min(xa, MathF.Min(xb, xc)), hi = MathF.Max(xa, MathF.Max(xb, xc));
                int onSeam = (MathF.Abs(xa) <= eps ? 1 : 0) + (MathF.Abs(xb) <= eps ? 1 : 0) + (MathF.Abs(xc) <= eps ? 1 : 0);
                if (!(lo < -eps && hi > eps) && onSeam < 2)
                    continue;
                float y0 = float.MaxValue, y1 = float.MinValue;
                foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
                {
                    float xu = pts[u].X - centerX, xv = pts[v].X - centerX;
                    if (MathF.Abs(xu) <= eps)
                    {
                        y0 = MathF.Min(y0, pts[u].Y);
                        y1 = MathF.Max(y1, pts[u].Y);
                    }
                    if ((xu < 0) != (xv < 0))
                    {
                        float y = pts[u].Y + (pts[v].Y - pts[u].Y) * (xu / (xu - xv));
                        y0 = MathF.Min(y0, y);
                        y1 = MathF.Max(y1, y);
                    }
                }
                for (int s = Slice(y0); s <= Slice(y1); s++)
                    crossings[s]++;
            }

            int top = 0, misses = 0;
            bool started = false;
            for (int s = 0; s < slices; s++)
            {
                bool split = left[s] > 0 && right[s] > 0 && crossings[s] == 0;

                if (split)
                {
                    started = true;
                    top = s + 1;
                    misses = 0;
                }
                // toes that turn inward can touch at the very bottom, so the split may start a little up,
                // and knee pads can touch for a slice or two
                else if ((started && ++misses > 2) || (!started && s >= slices / 10))
                    break;
            }

            // no legs at all (a dress or a blob): treat just the bottom as feet
            return Math.Clamp(top / (float)slices, 0.05f, 0.6f);
        }

        /// <summary>
        /// Legs pressed together are one surface where they touch. Below the knees, where a stride parts
        /// them furthest, the triangles reaching from one leg to the other go: they're hidden between the
        /// legs while they touch, and once parted the legs are whole without them.
        /// </summary>
        private int UnjoinLegs(ObjModel model)
        {
            var legs = Enumerable.Range(0, Positions.Length).Where(i => ModelRegion[i] == LegLeft || ModelRegion[i] == LegRight).ToList();
            if (legs.Count == 0)
                return 0;
            float floor = legs.Min(i => Positions[i].Y), top = legs.Max(i => Positions[i].Y);
            float knee = floor + (top - floor) * 0.5f;
            bool Low(int i) => Positions[i].Y < knee;
            return model.Triangles.RemoveAll(t =>
            {
                var c = new[] { t.A.Position, t.B.Position, t.C.Position };
                return c.All(Low) && c.Any(i => ModelRegion[i] == LegLeft) && c.Any(i => ModelRegion[i] == LegRight);
            });
        }

        /// <summary>
        /// For a model whose legs never part: how far up its slim lower body runs before it widens into the
        /// hips, as a share of its height, a quarter below where it widens; 0 when the lower body isn't slim.
        /// </summary>
        private static float LegsTogether(Vector3[] pts, float centerX, float minY, float height)
        {
            const int slices = 50;
            var reach = new float[slices];
            var any = new bool[slices];
            foreach (var p in pts)
            {
                int s = Math.Clamp((int)((p.Y - minY) / height * slices), 0, slices - 1);
                reach[s] = MathF.Max(reach[s], MathF.Abs(p.X - centerX));
                any[s] = true;
            }
            // both legs together are about a quarter of the height across (a slice may hold nothing at all)
            int top = 0;
            for (int s = 0; s < slices; s++)
            {
                if (!any[s])
                    continue;
                if (reach[s] > height * 0.12f)
                    break;
                top = s + 1;
            }
            float slim = top / (float)slices;
            return slim >= 0.3f ? MathF.Min(slim * 0.75f, 0.6f) : 0;
        }

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
            int headBone = !float.IsNaN(NeckY) && HeadBone >= 0 ? Remap[HeadBone] : -1;
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
                bool headless = region == Body && headBone >= 0 && Positions[i].Y < NeckYAt(Positions[i]) - tallest * 0.04f;
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
            bool belowNeck(int i) => float.IsNaN(NeckY) || HeadBone < 0 || Positions[i].Y < NeckYAt(Positions[i]) - tallest * 0.04f;
            Smooth(w, adj, boneCount, opt.BodySmoothPasses, i => ModelRegion[i] == Body && belowNeck(i));

            // 2b. above the neck everything follows the head alone, blending in over a short band
            if (!float.IsNaN(NeckY) && HeadBone >= 0)
            {
                int head = Remap[HeadBone];
                float h = Positions.Max(q => q.Y) - Positions.Min(q => q.Y);
                for (int i = 0; i < n; i++)
                {
                    // arms held out at the fighter's shoulders can reach above a low neck line (Linguini's):
                    // they are still arms
                    float neckY = NeckYAt(Positions[i]);
                    float k = SmoothStep(neckY - h * 0.04f, neckY + h * 0.02f, Positions[i].Y);
                    if (k <= 0 || ModelRegion[i] == ArmLeft || ModelRegion[i] == ArmRight) continue;
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
            bool aboveNeck(List<int> members) => !float.IsNaN(NeckY) && members.Count(i => Positions[i].Y < NeckYAt(Positions[i])) * 4 < members.Count;
            var pieces = Enumerable.Range(0, Part.Max() + 1)
                .Select(part => Enumerable.Range(0, n).Where(i => Part[i] == part).ToList())
                .Where(members => members.Count > 0 && BodyPiece[members[0]])
                .ToList();
            // the points of limb pieces (bendy, not body) touching a body piece
            float reach = (Positions.Max(q => q.Y) - Positions.Min(q => q.Y)) * 0.06f;
            var joins = Enumerable.Range(0, n).Where(i => !BodyPiece[i] && !RigidPart[i] && adj[i].Any(j => BodyPiece[j])).ToList();
            foreach (var group in pieces.GroupBy(aboveNeck))
            {
                var own = new float[boneCount];
                foreach (var i in group.SelectMany(m => m))
                    for (int b = 0; b < boneCount; b++)
                        own[b] += w[i][b];
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
            if (ends.Count == 0)
                return;
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

        private void WeldCoincident(HashSet<int>[] adj)
        {
            var byPos = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < Positions.Length; i++)
            {
                var p = Positions[i] * 1000;
                var key = ((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));
                if (!byPos.TryGetValue(key, out var l))
                    byPos[key] = l = new List<int>();
                l.Add(i);
            }

            foreach (var l in byPos.Values)
                foreach (var a in l)
                    foreach (var b in l)
                        if (a != b)
                            adj[a].Add(b);
        }

        private void FindParts(HashSet<int>[] adj, Options opt, List<ObjModel.Triangle> triangles)
        {
            int n = Positions.Length;
            Part = Enumerable.Repeat(-1, n).ToArray();
            int count = 0;
            var sizes = new List<int>();

            for (int s = 0; s < n; s++)
            {
                if (Part[s] != -1)
                    continue;

                int size = 0;
                var stack = new Stack<int>();
                stack.Push(s);
                Part[s] = count;
                while (stack.Count > 0)
                {
                    int v = stack.Pop();
                    size++;
                    foreach (var u in adj[v])
                        if (Part[u] == -1)
                        {
                            Part[u] = count;
                            stack.Push(u);
                        }
                }
                sizes.Add(size);
                count++;
            }

            RigidPart = new bool[n];
            BodyPiece = new bool[n];
            int largest = sizes.Count > 0 ? sizes.Max() : 0;
            int small = Enumerable.Range(0, n).Count(i => sizes[Part[i]] < n * opt.RigidPartShare);
            if (largest >= n * opt.RigidPartShare && small * 2 < n)
            {
                // an arm laid along the fighter's arm that is a piece of its own (Squidward's) bends at the
                // elbow like the fighter's, rather than staying stiff with the body at the shoulder
                var armPart = new int[sizes.Count];
                var armLow = Enumerable.Repeat(float.MaxValue, sizes.Count).ToArray();
                var armHigh = Enumerable.Repeat(float.MinValue, sizes.Count).ToArray();
                for (int i = 0; i < n; i++)
                {
                    if (Conformed.Contains(ModelRegion[i]))
                        armPart[Part[i]]++;
                    armLow[Part[i]] = MathF.Min(armLow[Part[i]], Positions[i].Y);
                    armHigh[Part[i]] = MathF.Max(armHigh[Part[i]], Positions[i].Y);
                }
                float tall = Positions.Max(q => q.Y) - Positions.Min(q => q.Y);
                bool Arm(int part) => armPart[part] * 2 > sizes[part] && armHigh[part] - armLow[part] > tall * 0.15f;
                for (int i = 0; i < n; i++)
                    RigidPart[i] = sizes[Part[i]] < n * opt.RigidPartShare && !Arm(Part[i]);
                return;
            }

            // no piece is big enough to be the body, or most of the model is pieces too small to count: it
            // is built from pieces that touch (BMO's box, face plate, arms and legs; Peggy's arms, shorts
            // and shins). Each point that touches another piece is joined to it, so weights
            // blend across the joins and an arm stays on the box when it bends; only the small pieces
            // (buttons, screws) move as one
            JoinTouchingParts(adj, triangles);
            var bodyCount = new int[count];
            var lowCount = new int[count];
            for (int i = 0; i < n; i++)
            {
                if (ModelRegion[i] == Body)
                    bodyCount[Part[i]]++;
                float legTop = float.IsNaN(HipY) ? LegTopY : HipY;
                if (!float.IsNaN(legTop) && Positions[i].Y < legTop)
                    lowCount[Part[i]]++;
            }
            // a small piece is rigid (a button), but not a leg or arm that reaches a good way along the
            // limb (Peggy's legs below her shorts): that bends at the knee
            var limbCount = new int[count];
            var low = Enumerable.Repeat(float.MaxValue, count).ToArray();
            var high = Enumerable.Repeat(float.MinValue, count).ToArray();
            float height = Positions.Max(q => q.Y) - Positions.Min(q => q.Y);
            for (int i = 0; i < n; i++)
            {
                if (ModelRegion[i] != Body)
                    limbCount[Part[i]]++;
                low[Part[i]] = MathF.Min(low[Part[i]], Positions[i].Y);
                high[Part[i]] = MathF.Max(high[Part[i]], Positions[i].Y);
            }
            bool Limb(int part) => limbCount[part] * 2 > sizes[part] && high[part] - low[part] > height * 0.15f;
            for (int i = 0; i < n; i++)
            {
                RigidPart[i] = sizes[Part[i]] < largest * 0.25f && !Limb(Part[i]);
                // a body piece keeps its shape only when it sits above the legs (BMO's box); one reaching
                // down into them (Peggy's shorts) bends with them, or the legs come out from under it
                // and one that carries the arms (a coat with its sleeves, Linguini's) bends at the shoulders and
                // elbows like any body, or the arms stay stuck straight out in every move
                BodyPiece[i] = !RigidPart[i] && bodyCount[Part[i]] * 2 > sizes[Part[i]] && lowCount[Part[i]] * 10 <= sizes[Part[i]]
                    && limbCount[Part[i]] * 5 <= sizes[Part[i]];
            }
            Report += $"the model is {count} separate pieces: pieces that touch bend together\n";
        }

        private void JoinTouchingParts(HashSet<int>[] adj, List<ObjModel.Triangle> triangles)
        {
            foreach (var (i, join) in TouchingPairs(triangles))
            {
                adj[i].Add(join);
                adj[join].Add(i);
            }
        }

        /// <summary>
        /// Points of one piece that touch another piece (within 1% of the model's size), each with the
        /// nearest corner of the triangle it touches.
        /// </summary>
        private List<(int, int)> TouchingPairs(List<ObjModel.Triangle> triangles)
        {
            var pairs = new List<(int, int)>();
            int n = Positions.Length;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            float reach = (max - min).Length() * 0.01f;

            // triangles in a grid of cells as wide as the reach, so each point only checks those near it
            var grid = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor((p.X - min.X) / reach), (int)MathF.Floor((p.Y - min.Y) / reach), (int)MathF.Floor((p.Z - min.Z) / reach));
            for (int t = 0; t < triangles.Count; t++)
            {
                var tri = triangles[t];
                var a = Positions[tri.A.Position]; var b = Positions[tri.B.Position]; var c = Positions[tri.C.Position];
                var (x0, y0, z0) = Cell(Vector3.Min(a, Vector3.Min(b, c)));
                var (x1, y1, z1) = Cell(Vector3.Max(a, Vector3.Max(b, c)));
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            if (!grid.TryGetValue((x, y, z), out var list))
                                grid[(x, y, z)] = list = new List<int>();
                            list.Add(t);
                        }
            }

            for (int i = 0; i < n; i++)
            {
                var p = Positions[i];
                var (cx, cy, cz) = Cell(p);
                float best = reach;
                int join = -1;
                for (int x = cx - 1; x <= cx + 1; x++)
                    for (int y = cy - 1; y <= cy + 1; y++)
                        for (int z = cz - 1; z <= cz + 1; z++)
                        {
                            if (!grid.TryGetValue((x, y, z), out var list))
                                continue;
                            foreach (int t in list)
                            {
                                var tri = triangles[t];
                                if (Part[tri.A.Position] == Part[i])
                                    continue;
                                var corners = new[] { tri.A.Position, tri.B.Position, tri.C.Position };
                                float d = DistanceToTriangle(p, Positions[corners[0]], Positions[corners[1]], Positions[corners[2]]);
                                if (d >= best)
                                    continue;
                                best = d;
                                join = corners.MinBy(k => Vector3.DistanceSquared(p, Positions[k]));
                            }
                        }
                if (join >= 0)
                    pairs.Add((i, join));
            }
            return pairs;
        }

        private static float DistanceToTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            // the closest point on the triangle (Ericson, Real-Time Collision Detection 5.1.5)
            var ab = b - a; var ac = c - a; var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return ap.Length();
            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return bp.Length();
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return (p - (a + d1 / (d1 - d3) * ab)).Length();
            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return cp.Length();
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return (p - (a + d2 / (d2 - d6) * ac)).Length();
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return (p - (b + (d4 - d3) / ((d4 - d3) + (d5 - d6)) * (c - b))).Length();
            float den = 1 / (va + vb + vc);
            return (p - (a + ab * (vb * den) + ac * (vc * den))).Length();
        }

        /// <summary>
        /// Sorts the fighter's bones into body (the chain from the root up to the head), arms, legs and
        /// other limbs (tails, capes), by where each branch off the body reaches.
        /// </summary>
        private void FighterRegions(FighterMesh fighter, Skeleton sk, int head, Dictionary<string, int> roles)
        {
            int n = sk.Joints.Count;
            if (roles != null)
            {
                RolesToRegions(fighter, sk, roles);
                return;
            }

            var body = new bool[n];
            for (int b = Math.Max(head, 0); b >= 0; b = sk.Parent[b])
                body[b] = true;

            var branch = new int[n];
            for (int b = 0; b < n; b++)
            {
                int t = b;
                while (!body[t] && !body[sk.Parent[t]])
                    t = sk.Parent[t];
                branch[b] = body[t] ? -1 : t;
            }

            float height = fighter.Max.Y - fighter.Min.Y, width = fighter.Max.X - fighter.Min.X;
            float cx = (fighter.Min.X + fighter.Max.X) / 2;
            var byBranch = fighter.Vertices.Where(v => branch[RegionRemap[Dominant(v)]] >= 0).GroupBy(v => branch[RegionRemap[Dominant(v)]]);
            var branchRegion = new Dictionary<int, int>();
            foreach (var g in byBranch)
            {
                bool plus = g.Average(v => v.Position.X) >= cx;
                if (g.Min(v => v.Position.Y) < fighter.Min.Y + height * 0.25f)
                    branchRegion[g.Key] = plus ? LegLeft : LegRight;
                else if (g.Max(v => MathF.Abs(v.Position.X - cx)) > width * 0.25f)
                    branchRegion[g.Key] = plus ? ArmLeft : ArmRight;
                else
                    branchRegion[g.Key] = Other;
            }

            BoneRegion = new int[n];
            for (int b = 0; b < n; b++)
                BoneRegion[b] = branch[b] < 0 ? Body : branchRegion.GetValueOrDefault(branch[b], Other);
            FighterRegion = fighter.Vertices.Select(v => BoneRegion[RegionRemap[Dominant(v)]]).ToArray();

            Report += "fighter regions: " + string.Join(", ", Enumerable.Range(0, RegionNames.Length)
                .Select(r => (r, bones: Enumerable.Range(0, n).Where(b => BoneRegion[b] == r && fighter.BoneExtent.ContainsKey(b)).ToList()))
                .Where(e => e.bones.Count > 0).Select(e => $"{RegionNames[e.r]} {string.Join(",", e.bones)}")) + "\n";
        }

        private void RolesToRegions(FighterMesh fighter, Skeleton sk, Dictionary<string, int> roles)
        {
            static int Region(string role)
            {
                string part = role.Length == 3 && (role[0] == 'l' || role[0] == 'r') ? role.Substring(1) : null;
                if (part is "sh" or "el" or "ha")
                    return role[0] == 'l' ? ArmLeft : ArmRight;
                if (part is "le" or "kn" or "fb" or "ff")
                    return role[0] == 'l' ? LegLeft : LegRight;
                return role is "face" or "body" or "butt" or "chest" or "hips" ? Body : Other;
            }

            var byBone = new Dictionary<int, int>();
            foreach (var (role, bone) in roles)
                byBone[bone] = Region(role);

            int n = sk.Joints.Count;
            BoneRegion = new int[n];
            for (int b = 0; b < n; b++)
                BoneRegion[b] = byBone.GetValueOrDefault(RegionRemap[b], Other);
            FighterRegion = fighter.Vertices.Select(v => BoneRegion[RegionRemap[Dominant(v)]]).ToArray();

            Report += "fighter regions from the bone map: " + string.Join(", ", Enumerable.Range(0, RegionNames.Length)
                .Select(r => (r, bones: roles.Values.Distinct().Where(b => byBone[b] == r).OrderBy(b => b).ToList()))
                .Where(e => e.bones.Count > 0).Select(e => $"{RegionNames[e.r]} {string.Join(",", e.bones)}")) + "\n";
        }

        private static int Dominant(FighterMesh.Vertex v) => v.Bones[Array.IndexOf(v.Weights, v.Weights.Max())];

        /// <summary>
        /// Bones a different body shouldn't follow: small ones (eyes, fingers) and everything hanging
        /// off the head (ears, jaw, hair), which would drag a face around. Each hands its weight to
        /// the nearest ancestor that isn't one of these.
        /// </summary>
        private static int[] DetailBoneRemap(FighterMesh fighter, Skeleton sk, out List<int> merged, out int head)
        {
            int n = sk.Joints.Count;
            float height = fighter.Max.Y - fighter.Min.Y;
            var detail = new bool[n];

            foreach (var kv in fighter.BoneExtent)
                if (kv.Value < height * 0.12f)
                    detail[kv.Key] = true;

            // the head: the bone driving the biggest area among those up in the top third
            head = fighter.BoneExtent.Keys
                .Where(b => sk.Position(b).Y > fighter.Min.Y + height * 0.6f)
                .OrderByDescending(b => fighter.BoneExtent[b])
                .DefaultIfEmpty(-1).First();
            if (head >= 0)
                for (int b = 0; b < n; b++)
                    for (int a = sk.Parent[b]; a >= 0; a = sk.Parent[a])
                        if (a == head) { detail[b] = true; break; }

            var remap = new int[n];
            merged = new List<int>();
            for (int b = 0; b < n; b++)
            {
                int t = b;
                while (t >= 0 && (detail[t] || !fighter.BoneExtent.ContainsKey(t)) && sk.Parent[t] >= 0 && t != head)
                    t = sk.Parent[t];
                remap[b] = t < 0 ? b : t;
                if (remap[b] != b && fighter.BoneExtent.ContainsKey(b))
                    merged.Add(b);
            }
            return remap;
        }

        /// <summary>
        /// Moves every bone's weight onto the bones a bone map lists. A bone that sits on top of a
        /// listed child (a wrist helper on the hand, a neck on the head) goes to that child; anything
        /// else goes the way its parent goes, and bones with no listed parent (hips, tail) join the body.
        /// A helper hung off the collarbone right at a listed shoulder (Falcon's 37, 43 and 60, which
        /// shrug on their own in his moves) goes to that shoulder, so the top of the arm moves with the arm.
        /// </summary>
        private static int[] ChosenBoneRemap(FighterMesh fighter, Skeleton sk, int[] bones, int head, int[] shoulders = null)
        {
            int n = sk.Joints.Count;
            float near = (fighter.Max.Y - fighter.Min.Y) * 0.05f;
            var allowed = bones.Where(b => b >= 0 && b < n).ToHashSet();

            // the body: the listed bone on the way from the head down to the root
            int body = -1;
            for (int b = head; b >= 0 && body < 0; b = sk.Parent[b])
                if (allowed.Contains(b) && b != head) body = b;
            if (body < 0)
                body = allowed.OrderBy(b => sk.Position(b).Y).FirstOrDefault(b => b != head);

            var remap = new int[n];
            for (int b = 0; b < n; b++)
            {
                if (allowed.Contains(b))
                {
                    remap[b] = b;
                    continue;
                }

                int under = Enumerable.Range(0, n)
                    .Where(c => allowed.Contains(c) && IsAncestor(sk, b, c) && Vector3.Distance(sk.Position(c), sk.Position(b)) < near)
                    .OrderBy(c => Vector3.Distance(sk.Position(c), sk.Position(b)))
                    .DefaultIfEmpty(-1).First();
                if (under < 0 && shoulders != null && sk.Parent[b] >= 0)
                    under = shoulders
                        .Where(c => c != b && IsAncestor(sk, sk.Parent[b], c) && Vector3.Distance(sk.Position(c), sk.Position(b)) < near)
                        .OrderBy(c => Vector3.Distance(sk.Position(c), sk.Position(b)))
                        .DefaultIfEmpty(-1).First();
                // parents come before children in the joint list, so the parent's answer is already known
                remap[b] = under >= 0 ? under : sk.Parent[b] >= 0 ? remap[sk.Parent[b]] : body;
                if (remap[b] < 0 || !allowed.Contains(remap[b]))
                    remap[b] = body;
            }
            return remap;
        }

        private static bool IsAncestor(Skeleton sk, int ancestor, int b)
        {
            for (int a = sk.Parent[b]; a >= 0; a = sk.Parent[a])
                if (a == ancestor) return true;
            return false;
        }

        private static void Normalize(float[] a)
        {
            float s = a.Sum();
            if (s <= 0)
                return;
            for (int i = 0; i < a.Length; i++)
                a[i] /= s;
        }
    }
}

