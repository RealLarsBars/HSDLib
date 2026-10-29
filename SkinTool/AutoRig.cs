using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// Fits a model onto the fighter's bind pose and copies the fighter's skin weights onto it.
    /// </summary>
    public partial class AutoRig
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
    }
}
