using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    // Fitting the model onto the fighter: size, place, neck, arms, legs, feet and tail.
    public partial class AutoRig
    {
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

        private static float SmoothStep(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }
    }
}
