using HSDRaw;
using HSDRaw.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SkinTool
{
    /// <summary>
    /// The bones custom skins are weighted to, per fighter, as modders weight them by hand: one bone
    /// for the face, one or two for the body, and shoulder, elbow, hand, leg, knee and two foot bones a side.
    /// Everything else (fingers, eyes, hair, cape, ears) follows one of these.
    /// Transcribed from the community weight maps (the "MAP" images that come with the AutoModder scripts).
    /// </summary>
    public static class BoneMaps
    {
        // l/r = the fighter's left (+x) and right; sh = shoulder, el = elbow, ha = hand,
        // le = leg, kn = knee, fb = foot (back), ff = foot (front)
        private static readonly Dictionary<string, string> Maps = new()
        {
            ["Ca"] = "face=39 body=19 butt=18 lsh=23 lel=24 lha=25 rsh=45 rel=46 rha=47 lle=7 lkn=8 lfb=10 lff=11 rle=13 rkn=14 rfb=16 rff=17",
            ["Cl"] = "face=42 body=18 butt=17 lsh=20 lel=22 lha=25 sword=27 rsh=54 rel=56 rha=59 shield=71 lle=6 lkn=7 lfb=8 lff=10 rle=12 rkn=13 rfb=15 rff=16",
            ["Dk"] = "face=44 chest=26 body=25 lsh=29 lel=30 lha=31 rsh=50 rel=52 rha=53 lle=6 lkn=7 lfb=9 rle=16 rkn=17 rfb=18",
            ["Dr"] = "face=23 body=3 butt=4 lsh=7 lel=9 lha=10 rsh=31 rel=32 rha=33 lle=52 lkn=54 lfb=55 lff=57 rle=68 rkn=70 rfb=72 rff=73",
            ["Fc"] = "face=39 body=19 lsh=23 lel=24 lha=26 rsh=48 rel=50 rha=52 lle=6 lkn=7 lfb=9 lff=10 rle=12 rkn=13 rfb=15 rff=16",
            ["Fe"] = "face=62 body=24 butt=23 lsh=29 lel=31 lha=33 rsh=71 rel=73 rha=75 lle=6 lkn=7 lfb=9 lff=10 rle=13 rkn=15 rfb=17 rff=18",
            ["Fx"] = "face=41 body=22 lsh=25 lel=26 lha=36 rsh=55 rel=56 rha=66 lle=6 lkn=7 lfb=9 lff=10 rle=12 rkn=13 rfb=15 rff=16",
            ["Gn"] = "face=53 body=18 butt=17 lsh=22 lel=23 lha=25 rsh=65 rel=67 rha=69 lle=6 lkn=7 lfb=9 lff=10 rle=12 rkn=13 rfb=15 rff=16",
            ["Kb"] = "body=6 lsh=24 lel=25 lha=26 rsh=29 rel=30 rha=31 lfb=36 lff=37 rfb=42 rff=43",
            ["Kp"] = "face=48 body=30 butt=29 lsh=33 lel=34 lha=35 rsh=60 rel=61 rha=62 lle=6 lkn=7 lfb=9 lff=10 rle=15 rkn=16 rfb=18 rff=19",
            ["Lg"] = "face=23 body=46 lsh=8 lel=9 lha=10 rsh=30 rel=32 rha=33 lle=48 lkn=49 lfb=51 lff=52 rle=54 rkn=55 rfb=57 rff=58",
            ["Lk"] = "face=40 body=18 butt=17 lsh=20 lel=22 lha=23 sword=24 rsh=52 rel=54 rha=64 shield=67 lle=6 lkn=7 lfb=9 lff=10 rle=12 rkn=13 rfb=15 rff=16",
            ["Mr"] = "face=23 body=46 lsh=8 lel=9 lha=10 rsh=31 rel=32 rha=33 lle=48 lkn=49 lfb=50 lff=52 rle=53 rkn=55 rfb=57 rff=58",
            ["Ms"] = "face=60 body=22 butt=21 lsh=27 lel=29 lha=31 rsh=69 rel=71 rha=73 sword=75 lle=5 lkn=7 lfb=9 lff=10 rle=13 rkn=14 rfb=16 rff=17",
            ["Mt"] = "face=43 body=28 lsh=31 lel=32 lha=35 rsh=54 rel=55 rha=58 lle=8 lkn=11 lff=12 rle=14 rkn=17 rff=18 tail1=20 tail2=21 tail3=22 tail4=23 tail5=24 tail6=25 tail7=26",
            ["Nn"] = "face=19 body=3 lsh=9 lel=10 lha=11 hammer=15 rsh=24 rel=25 rha=26 lle=36 lkn=37 lfb=39 lff=40 rle=41 rkn=43 rfb=45 rff=46",
            ["Ns"] = "face=24 body=3 lsh=8 lel=10 lha=11 rsh=30 rel=32 rha=33 lle=47 lkn=49 lfb=51 lff=52 rle=54 rkn=56 rfb=57 rff=59",
            ["Pc"] = "body=5 chest=6 butt=4 lsh=9 lel=10 lha=12 rsh=25 rel=26 rha=28 lfb=35 lff=36 rfb=41 rff=42",
            ["Pe"] = "face=87 body=67 butt=66 lsh=71 lel=72 lha=74 rsh=97 rel=98 rha=100 lle=6 lkn=7 lfb=9 lff=10 rle=12 rkn=13 rfb=15 rff=16",
            ["Pk"] = "face=14 body=5 butt=30 lsh=8 lel=9 lha=10 rsh=23 rel=25 rha=26 lle=31 lkn=33 lfb=35 lff=36 rle=40 rkn=41 rfb=43 rff=44 tail1=48 tail2=49 tail3=50",
            ["Pp"] = "face=19 body=3 lsh=9 lel=10 lha=11 hammer=15 rsh=24 rel=25 rha=26 lle=36 lkn=37 lfb=39 lff=40 rle=41 rkn=43 rfb=45 rff=46",
            ["Pr"] = "body=6 lsh=29 lha=30 rsh=33 rha=35 lfb=40 lff=41 rfb=45 rff=47",
            ["Sk"] = "face=35 body=18 butt=17 lsh=20 lel=22 lha=23 rsh=40 rel=42 rha=43 lle=6 lkn=7 lfb=9 lff=10 rle=12 rkn=13 rfb=15 rff=16",
            ["Ss"] = "face=42 body=20 butt=4 lpad=22 rpad=44 lsh=26 lel=27 lha=38 rsh=48 rel=49 rha=55 lle=6 lkn=7 lfb=9 lff=11 rle=13 rkn=14 rfb=16 rff=18",
            ["Ys"] = "face=38 body=22 butt=19 tail1=20 tail2=21 lsh=26 lel=27 lha=28 rsh=56 rel=57 rha=58 lle=6 lkn=7 lfb=9 lff=10 rle=12 rkn=14 rfb=15 rff=17",
            ["Zd"] = "face=89 body=67 lsh=72 lel=73 lha=76 rsh=100 rel=101 rha=104 lle=5 lkn=7 lfb=9 lff=10 rle=11 rkn=13 rfb=15 rff=16",
        };

        /// <summary>The fighter's two-letter code from any of its file names (PlFx.dat, PlFxNr.dat).</summary>
        public static string Code(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            return name.Length >= 4 && name.StartsWith("Pl", StringComparison.OrdinalIgnoreCase) ? name.Substring(2, 2) : null;
        }

        public static Dictionary<string, int> Get(string code)
        {
            if (code == null || !Maps.TryGetValue(code, out var map))
                return null;
            return map.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split('='))
                .ToDictionary(e => e[0], e => int.Parse(e[1]));
        }

        /// <summary>
        /// The map for this costume, with each bone the costume's model isn't weighted to swapped for the stacked
        /// joint in the same spot that it is (the maps were read off pictures, and a stacked pair looks like one
        /// joint there, but only one of the two bends: weighting to the other leaves a stiff hip or foot).
        /// </summary>
        public static Dictionary<string, int> Get(string code, HSD_JOBJ costume)
        {
            var map = Get(code);
            if (map == null)
                return null;
            var sk = new Skeleton(costume);
            var used = Weighted(costume);
            foreach (var role in map.Keys.ToList())
                if (Limb(role) && map[role] < sk.Joints.Count && !used.Contains(map[role]))
                {
                    var twin = Twin(sk, used, map[role]);
                    if (twin >= 0)
                        map[role] = twin;
                    else
                    {
                        // a limb bone the model never follows is a helper the game moves on its own (where
                        // an item is held, Samus's cannon muzzle): the limb follows the joint above it instead
                        int up = sk.Parent[map[role]];
                        while (up >= 0 && !used.Contains(up))
                            up = sk.Parent[up];
                        if (up >= 0)
                            map[role] = up;
                    }
                }
            return map;
        }

        static bool Limb(string role) =>
            role.Length == 3 && (role[0] == 'l' || role[0] == 'r') && role.Substring(1) is "sh" or "el" or "ha" or "le" or "kn" or "fb" or "ff";

        /// <summary>The joints the costume's own model is weighted to.</summary>
        public static HashSet<int> Weighted(HSD_JOBJ root)
        {
            var list = root.TreeList;
            var used = new HashSet<int>();
            foreach (var j in list)
                foreach (var d in j.Dobj?.List ?? Enumerable.Empty<HSD_DOBJ>())
                    foreach (var p in d.Pobj?.List ?? Enumerable.Empty<HSD_POBJ>())
                        foreach (var e in p.EnvelopeWeights ?? Array.Empty<HSD_Envelope>())
                            foreach (var b in e.JOBJs)
                                used.Add(list.IndexOf(b));
            return used;
        }

        /// <summary>
        /// A joint the costume is weighted to that sits where <paramref name="bone"/> does, up or down its chain
        /// (fighters often stack two joints in one spot, and only one of them carries the bend), or -1.
        /// </summary>
        public static int Twin(Skeleton sk, HashSet<int> used, int bone)
        {
            var at = sk.Position(bone);
            for (int b = sk.Parent[bone]; b >= 0 && (sk.Position(b) - at).Length() < 0.01f; b = sk.Parent[b])
                if (used.Contains(b))
                    return b;
            var next = new List<int> { bone };
            while (next.Count > 0)
            {
                var kids = new List<int>();
                foreach (var n in next)
                    for (int c = 0; c < sk.Joints.Count; c++)
                        if (sk.Parent[c] == n && (sk.Position(c) - at).Length() < 0.01f)
                        {
                            if (used.Contains(c))
                                return c;
                            kids.Add(c);
                        }
                next = kids;
            }
            return -1;
        }

        /// <summary>
        /// Prints where each mapped bone sits, so a wrong number stands out
        /// (left and right should mirror, the face should be on top and the feet at the bottom).
        /// </summary>
        public static int Dump(string costume)
        {
            var code = Code(costume);
            var map = Get(code);
            if (map == null)
            {
                Console.WriteLine($"no bone map for {code ?? costume}");
                return 1;
            }

            var root = (HSD_JOBJ)new HSDRawFile(costume).Roots[0].Data;
            var sk = new Skeleton(root);
            var used = Weighted(root);
            int problems = 0;
            foreach (var (role, bone) in map)
            {
                if (bone >= sk.Joints.Count)
                {
                    Console.WriteLine($"{role,-7} {bone,4}  MISSING (only {sk.Joints.Count} joints)");
                    problems++;
                    continue;
                }
                var p = sk.Position(bone);
                string note = "";
                if (role.Length == 3 && (role[0] == 'l' || role[0] == 'r') && map.TryGetValue((role[0] == 'l' ? "r" : "l") + role.Substring(1), out var other) && other < sk.Joints.Count)
                {
                    var q = sk.Position(other);
                    if (MathF.Abs(p.X + q.X) > 0.5f || MathF.Abs(p.Y - q.Y) > 0.5f)
                    {
                        note = $"  doesn't mirror {other}";
                        problems++;
                    }
                }
                if (!used.Contains(bone))
                {
                    var twin = Twin(sk, used, bone);
                    note += !Limb(role) ? "  not weighted in the costume"
                        : twin >= 0 ? $"  not weighted in the costume; {twin} at the same spot is (used instead)"
                        : $"  not weighted in the costume (follows {Get(code, root)[role]} instead)";
                    problems++;
                }
                Console.WriteLine($"{role,-7} {bone,4}  ({p.X,6:F2}, {p.Y,6:F2}, {p.Z,6:F2}) parent {sk.Parent[bone]}{note}");
            }
            Console.WriteLine(problems == 0 ? "ok" : $"{problems} problems");
            return problems == 0 ? 0 : 3;
        }
    }
}
