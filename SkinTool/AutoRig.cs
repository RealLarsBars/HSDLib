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

            // the model's arms: "auto", "fighter" (always moved onto the fighter's arms) or "body"
            // (mostly stay with the body)
            public string Arms = "auto";

            // parts with fewer vertices than this share of the model move as one piece with the body around them
            public float RigidPartShare = 0.15f;

            // where the head starts, as a share of the model's height from the bottom: everything above
            // follows the head bone alone, so a wide head or face is never read as arms and moves as one
            // piece. NaN works it out (a neck narrower than the head above it), 0 or less: no such line
            public float Neck = float.NaN;

            // the model has a tail: the part sticking out behind it follows the fighter's tail bones, root
            // to tip (BoneMaps.Tails); otherwise everything behind follows the body
            public int[] TailBones;
            public bool HasTail;
        }

        public Vector3[] Positions;

        // positions used only to find matching fighter vertices
        public Vector3[] Lookup;
        public Vector3[] Normals;
        public SkinBuilder.Influence[][] Weights;

        // per model position, the part it belongs to and whether that part is rigid
        public int[] Part;
        public bool[] RigidPart;

        public string Report = "";

        // the fitted height of the neck line (NaN when there's none)
        public float NeckY = float.NaN;
        private int HeadBone = -1;

        // per model position, whether it's on the model's tail
        private bool[] Tail;

        // detail bone -> the bone that takes its weight
        private int[] Remap;

        // Body regions. Weights are only copied between matching regions, so a wide body never picks up
        // the fighter's arms and a round belly never picks up its thighs.
        public const int Body = 0, ArmLeft = 1, ArmRight = 2, LegLeft = 3, LegRight = 4, Other = 5;
        private static readonly string[] RegionNames = { "body", "arm (+x)", "arm (-x)", "leg (+x)", "leg (-x)", "other" };

        // per region, how fully the limb follows its own bones rather than the body (1 = fully)
        private readonly float[] LimbFollow = { 1, 1, 1, 1, 1, 1 };

        // per bone, per fighter vertex and per model position
        // for a model with no real legs: where its legs end at the top and the bottom, and its middle
        // (NaN when it has real legs)
        private float HipY = float.NaN, FloorY, MiddleX, HalfWidth, MiddleZ, HalfDepth;

        private int[] BoneRegion;
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
                rig.Remap = ChosenBoneRemap(fighter, skeleton, opt.Bones, head);
                rig.Report += $"bones from the fighter's bone map: {string.Join(",", opt.Bones)}\n";
            }
            else
                rig.Report += $"detail bones folded into their parents: {string.Join(",", merged)}\n";
            rig.HeadBone = head;
            rig.FighterRegions(fighter, skeleton, head, opt.Roles);
            rig.Fit(model, fighter, skeleton, opt);
            rig.Transfer(model, fighter, opt);
            return rig;
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

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in pts)
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
            var modelTris = model.Triangles.Select(t => (t.A.Position, t.B.Position, t.C.Position));
            var fighterTris = Enumerable.Range(0, fighter.Triangles.Count / 3).Select(t => (3 * t, 3 * t + 1, 3 * t + 2));
            float modelCrotch = Crotch(pts, modelTris, (min.X + max.X) / 2, min.Y, size.Y);
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
            bool stubby = opt.Legs switch { "separate" => false, "body" or "cut" => true, _ => modelCrotch < fighterCrotch * 0.5f };
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
            foreach (var q in pts)
            {
                int s = Slice((q.Y - min.Y) / size.Y);
                reach[s] = MathF.Max(reach[s], MathF.Abs(q.X - cx));
            }
            var bodySlices = Enumerable.Range(Slice(modelCrotch), slices - Slice(modelCrotch)).Where(s => reach[s] > 0).Select(s => reach[s]).OrderBy(r => r).ToList();
            float typical = bodySlices.Count > 0 ? bodySlices[bodySlices.Count / 2] : size.X / 2;
            var bodyReach = new float[slices];
            int top = Enumerable.Range(0, slices).Last(s => reach[s] > 0);

            // the neck: above it is all head
            float neck = opt.Neck;
            if (float.IsNaN(neck))
                neck = FindNeck(reach, typical, top, modelCrotch);
            int neckSlice = neck > 0 ? Slice(neck) : slices;
            if (neck > 0)
            {
                NeckY = target.Y + neck * size.Y * scale * opt.Height;
                Report += $"head: everything above {neck:P0} of the model's height follows the head ({(float.IsNaN(opt.Neck) ? "found a neck" : "set by hand")})\n";
            }
            for (int s = 0; s < slices; s++)
            {
                if (reach[s] <= typical * 1.35f || s >= neckSlice)
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
                var sides = new[] { reach[lo], reach[hi] }.Where(r => r > 0 && r <= typical * 1.35f).ToList();
                bodyReach[s] = sides.Count > 0 ? sides.Min() : typical;
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
            foreach (var (region, a) in arm)
            {
                string side = region == ArmLeft ? "l" : "r";
                // only an arm already near the fighter's is moved onto it: lifting a low arm up to the
                // shoulder bends it into a W that flaps about when running
                float fittedY = (a.y - origin.Y) * scale * opt.Height + target.Y;
                bool near = opt.Arms switch { "fighter" => true, "body" => false, _ => MathF.Abs(fittedY - a.fy) < 0.15f * fighterSize.Y };
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
                    Report += $"{RegionNames[region]}: moved onto the fighter's arm\n";
                    continue;
                }
                float fitted = (a.y - origin.Y) * scale * opt.Height + target.Y;
                float off = MathF.Abs(fitted - a.fy) / MathF.Max(a.fTip - a.fRoot, 1e-4f);
                LimbFollow[region] = Math.Clamp(1 - off, 0.2f, 1);
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

            Report += "model regions: " + string.Join(", ", Enumerable.Range(0, RegionNames.Length)
                .Select(r => (r, c: ModelRegion.Count(m => m == r))).Where(e => e.c > 0).Select(e => $"{RegionNames[e.r]} {e.c}")) + "\n";

            Report += $"legs: the model's split at {modelCrotch:P0} of its height lines up with the top of the fighter's legs at {fighterCrotch:P0}\n";

            Report += $"fitted size x{scale:F2}: {size.Y * scale * opt.Height:F1} tall next to the fighter's {fighterSize.Y:F1}, {size.X * scale * opt.Width:F1} wide next to its {fighterSize.X:F1}\n";
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
            float U(int i)
            {
                float root = bodyReach(pts[i], slice((pts[i].Y - min.Y) / size.Y));
                return Math.Clamp((MathF.Abs(pts[i].X - cx) - root) / MathF.Max(tipOf(), 1e-4f), 0, 1);
            }
            float tipCache = -1;
            float tipOf()
            {
                if (tipCache < 0)
                    tipCache = members.Max(i => MathF.Abs(pts[i].X - cx) - bodyReach(pts[i], slice((pts[i].Y - min.Y) / size.Y)));
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

            foreach (var i in members)
            {
                float u = U(i);
                float fb = u * bins - 0.5f;
                int b0 = Math.Clamp((int)MathF.Floor(fb), 0, bins - 1), b1 = Math.Min(bins - 1, b0 + 1);
                var mid = Vector3.Lerp(middle[b0], middle[b1], Math.Clamp(fb - b0, 0, 1));
                var offset = Positions[i] - mid;
                offset.X = 0; // the arm's length is the fighter's now; keep only its thickness
                var moved = Along(u) + offset;
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
        private void TailWeights(float[][] w, int boneCount, int[] chain)
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
            var tail = Enumerable.Range(0, n).Where(i => reach[Slice(Positions[i].Y)] > typical * 1.35f
                && MathF.Abs(Positions[i].X - cx) < halfW * 0.5f && cz - Positions[i].Z > root).ToList();
            if (tail.Count < n * 0.01f)
            {
                Report += "tail: none found behind the model; it follows the body\n";
                return;
            }

            float tip = tail.Max(i => cz - Positions[i].Z);
            foreach (var i in tail)
            {
                float u = Math.Clamp((cz - Positions[i].Z - root) / MathF.Max(tip - root, 1e-4f), 0, 1);
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

        private static float SmoothStep(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        /// <summary>
        /// How far up (as a share of the height) the shape stays split into two legs: heights where
        /// there is surface on both sides but no edge crosses the middle.
        /// </summary>
        private static float Crotch(Vector3[] pts, IEnumerable<(int, int, int)> triangles, float centerX, float minY, float height)
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
            // a triangle that reaches across the middle joins the two sides over its whole height;
            // so does one with an edge lying on the middle, the seam of a mirrored model
            // (a single vertex there is just a toe pointing inward)
            float eps = (pts.Max(p => p.X) - pts.Min(p => p.X)) * 0.002f;
            foreach (var (a, b, c) in triangles)
            {
                float xa = pts[a].X - centerX, xb = pts[b].X - centerX, xc = pts[c].X - centerX;
                float lo = MathF.Min(xa, MathF.Min(xb, xc)), hi = MathF.Max(xa, MathF.Max(xb, xc));
                int onSeam = (MathF.Abs(xa) <= eps ? 1 : 0) + (MathF.Abs(xb) <= eps ? 1 : 0) + (MathF.Abs(xc) <= eps ? 1 : 0);
                if (!(lo < -eps && hi > eps) && onSeam < 2)
                    continue;
                int s0 = Slice(MathF.Min(pts[a].Y, MathF.Min(pts[b].Y, pts[c].Y)));
                int s1 = Slice(MathF.Max(pts[a].Y, MathF.Max(pts[b].Y, pts[c].Y)));
                for (int s = s0; s <= s1; s++)
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

            FindParts(adj, opt);

            // 1. blend the weights of the nearest fighter vertices
            var w = new float[n][];
            var fv = fighter.Vertices;
            var pools = Enumerable.Range(0, RegionNames.Length)
                .Select(r => Enumerable.Range(0, fv.Count).Where(k => FighterRegion[k] == r).ToArray())
                .ToArray();
            for (int i = 0; i < n; i++)
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

                // only the region's own bones and the body's (where a limb joins it)
                var acc = new float[boneCount];
                var any = new float[boneCount];
                foreach (var (d, k) in nearest)
                {
                    float iw = 1f / (d + 1e-4f);
                    for (int b = 0; b < fv[k].Bones.Length; b++)
                    {
                        int bone = Remap[fv[k].Bones[b]];
                        any[bone] += fv[k].Weights[b] * iw;
                        if (BoneRegion[bone] == region || BoneRegion[bone] == Body)
                            acc[bone] += fv[k].Weights[b] * iw;
                    }
                }
                if (acc.Sum() <= 0)
                    acc = any;
                Normalize(acc);
                w[i] = acc;
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
            Smooth(w, adj, boneCount, opt.SmoothPasses, i => !Foot(i));
            Smooth(w, adj, boneCount, opt.BodySmoothPasses, i => ModelRegion[i] == Body);

            // 2b. above the neck everything follows the head alone, blending in over a short band
            if (!float.IsNaN(NeckY) && HeadBone >= 0)
            {
                int head = Remap[HeadBone];
                float h = Positions.Max(q => q.Y) - Positions.Min(q => q.Y);
                for (int i = 0; i < n; i++)
                {
                    float k = SmoothStep(NeckY - h * 0.04f, NeckY + h * 0.02f, Positions[i].Y);
                    if (k <= 0) continue;
                    for (int b = 0; b < boneCount; b++)
                        w[i][b] *= 1 - k;
                    w[i][head] += k;
                }
            }

            // 2c. the model's tail follows the fighter's tail bones, root to tip
            Tail = new bool[n];
            if (opt.TailBones != null && opt.TailBones.Length > 0)
                TailWeights(w, boneCount, opt.TailBones);

            // 3. small separate parts (claws, teeth, whiskers, eyes) move as one piece with the
            // body around them, so they stay attached to it
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
                    var at = members.Aggregate(Vector3.Zero, (a, i) => a + Positions[i]) / members.Count;
                    Report += $"part {part}: {members.Count} vertices around ({at.X:F1}, {at.Y:F1}, {at.Z:F1}) are a foot, follow bone {Array.IndexOf(own, own.Max())}\n";
                    foreach (var i in members)
                        w[i] = (float[])own.Clone();
                    continue;
                }

                // the part follows the body where it's attached: weights come from around its base (the
                // quarter of it nearest the body), not its tip, so it moves exactly as the spot it sits in
                float Gap(int i) => body.Count > 0 ? body.Min(j => Vector3.DistanceSquared(Positions[i], Positions[j])) : 0;
                var basePoints = members.OrderBy(Gap).Take(Math.Max(1, members.Count / 4)).ToList();
                var sum = new float[boneCount];
                foreach (var i in basePoints)
                {
                    var near = body.Count > 0
                        ? body.OrderBy(j => Vector3.DistanceSquared(Positions[i], Positions[j])).Take(4)
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

        private void Smooth(float[][] w, HashSet<int>[] adj, int boneCount, int passes, Func<int, bool> include)
        {
            for (int pass = 0; pass < passes; pass++)
            {
                var next = new float[w.Length][];
                for (int i = 0; i < w.Length; i++)
                {
                    var near = adj[i].Where(include).ToList();
                    if (RigidPart[i] || !include(i) || near.Count == 0)
                    {
                        next[i] = w[i];
                        continue;
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
                }
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

        private void FindParts(HashSet<int>[] adj, Options opt)
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
            for (int i = 0; i < n; i++)
                RigidPart[i] = sizes[Part[i]] < n * opt.RigidPartShare;
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
            var byBranch = fighter.Vertices.Where(v => branch[Remap[Dominant(v)]] >= 0).GroupBy(v => branch[Remap[Dominant(v)]]);
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
            FighterRegion = fighter.Vertices.Select(v => BoneRegion[Remap[Dominant(v)]]).ToArray();

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
                return role is "face" or "body" or "butt" or "chest" ? Body : Other;
            }

            var byBone = new Dictionary<int, int>();
            foreach (var (role, bone) in roles)
                byBone[bone] = Region(role);

            int n = sk.Joints.Count;
            BoneRegion = new int[n];
            for (int b = 0; b < n; b++)
                BoneRegion[b] = byBone.GetValueOrDefault(Remap[b], Other);
            FighterRegion = fighter.Vertices.Select(v => BoneRegion[Remap[Dominant(v)]]).ToArray();

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
        /// </summary>
        private static int[] ChosenBoneRemap(FighterMesh fighter, Skeleton sk, int[] bones, int head)
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

