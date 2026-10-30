using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace SkinTool
{
    /// <summary>
    /// Models that carry several versions of a piece of clothing on top of each other, for the game to show one
    /// at a time: Tom Nook's Animal Crossing rip has three tops (Nook Inc. jacket, aloha shirt, tuxedo) and three
    /// pairs of trousers in one model. Drawn together they show through each other, so each such slot (tops,
    /// bottoms) keeps one version, picked by key for --outfit slot=key.
    ///
    /// A slot is pieces named for the same kind of clothing (TopsRcoOuterL and TopsRcoYshirtH; Shirt_A and
    /// Shirt_B) that sit over the same part of the model. Pieces named alike that sit apart (Shoe_L, Shoe_R)
    /// aren't versions of each other. A piece named after another plus more (BottomsRcoPantsHalf__mSoftmesh)
    /// belongs to it. Left alone, each slot keeps its first version; "all" keeps them all, as the model has them.
    /// </summary>
    public static class Outfits
    {
        public class Choice
        {
            public string Key, Label;
            internal HashSet<string> Objects = new();
        }

        public class Slot
        {
            public string Key, Label;
            public List<Choice> Choices = new();
            public string Default => Choices[0].Key;
            internal HashSet<string> Objects => Choices.SelectMany(c => c.Objects).ToHashSet();
        }

        public const string All = "all";

        // the first word of a piece's name, when it names a kind of clothing
        static readonly string[] Kinds = { "tops", "top", "bottoms", "bottom", "shirt", "shirts", "tshirt", "jacket", "coat", "outer", "pants", "trousers", "shorts", "skirt", "dress", "onepiece", "shoes", "shoe", "boots", "socks", "hat", "hats", "cap", "helmet", "headwear", "glasses", "outfit", "costume", "cloth", "clothes", "clothing", "suit", "uniform", "accessory", "bag" };

        // CamelCase and separators: TopsRcoOuterL -> Tops, Rco, Outer, L
        static string[] Words(string name) =>
            Regex.Matches(name, "[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+").Select(m => m.Value).ToArray();

        // the kind of clothing a piece's name starts with, after any words naming the character (Nook_Tops_A
        // is tops); null when it names none. Only clothing counts: pieces of a body are often named alike
        // (moe-bodyshape_0 and _1, Moe's face and his eyes) and sit over the same spot without being versions
        internal static string KindOf(string name)
        {
            var words = Words(name).Select(w => w.ToLowerInvariant()).ToList();
            // a piece of the body named for what it goes under (Nook's Body__mCapVis, the top of his head shown
            // when he has no cap on) isn't clothing
            if (words.Count > 0 && BodyWords.Contains(words[0]))
                return null;
            return words.FirstOrDefault(Kinds.Contains);
        }

        static readonly string[] BodyWords = { "body", "face", "head", "hair", "skin", "eye", "eyes", "mouth", "hand", "hands", "arm", "arms", "leg", "legs", "foot", "feet", "tail" };

        public static List<Slot> Find(ObjModel model)
        {
            var tris = model.Triangles.Where(t => t.Object != null && !t.Outline).ToList();
            var names = tris.Select(t => t.Object).Distinct().ToList();
            // a piece named after another plus more goes with it (the longest such name)
            string Owner(string name) => names.Where(n => n != name && name.Length > n.Length + 1 && name.StartsWith(n) && !char.IsLetterOrDigit(name[n.Length]))
                .OrderByDescending(n => n.Length).FirstOrDefault();
            var units = names.GroupBy(n => Owner(n) ?? n).ToDictionary(g => g.Key, g => g.ToHashSet());

            // eye states are picked as eyes (see Eyes); detail levels and second copies are left to Duplicates
            bool Skip(string n) => Regex.IsMatch(n, "eye|lod_?\\d", RegexOptions.IgnoreCase);
            var boxes = new Dictionary<string, (Vector3 lo, Vector3 hi)>();
            foreach (var (unit, members) in units)
            {
                var points = tris.Where(t => members.Contains(t.Object)).SelectMany(t => new[] { t.A, t.B, t.C }).Select(c => model.Positions[c.Position]).ToList();
                boxes[unit] = (points.Aggregate(new Vector3(float.MaxValue), Vector3.Min), points.Aggregate(new Vector3(float.MinValue), Vector3.Max));
            }
            float Volume(Vector3 lo, Vector3 hi) { var s = Vector3.Max(hi - lo, Vector3.Zero); return MathF.Max(s.X, 1e-4f) * MathF.Max(s.Y, 1e-4f) * MathF.Max(s.Z, 1e-4f); }
            // over the same part of the model: they share most of the smaller one's box, and are about as big
            // (a button inside a shirt's box isn't another shirt)
            bool Overlap(string a, string b)
            {
                var (la, ha) = boxes[a];
                var (lb, hb) = boxes[b];
                Vector3 sa = ha - la, sb = hb - lb, small = Vector3.Min(sa, sb), big = Vector3.Max(Vector3.Max(sa, sb), new Vector3(1e-4f));
                if (small.X < 0.4f * big.X || small.Y < 0.4f * big.Y || small.Z < 0.4f * big.Z)
                    return false;
                float shared = Volume(Vector3.Max(la, lb), Vector3.Min(ha, hb));
                if (Vector3.Min(ha, hb).X <= Vector3.Max(la, lb).X || Vector3.Min(ha, hb).Y <= Vector3.Max(la, lb).Y || Vector3.Min(ha, hb).Z <= Vector3.Max(la, lb).Z)
                    return false;
                return shared >= 0.6f * MathF.Min(Volume(la, ha), Volume(lb, hb));
            }

            var slots = new List<Slot>();
            foreach (var kind in units.Keys.Where(u => !Skip(u)).GroupBy(KindOf).Where(g => g.Key != null && g.Count() > 1))
            {
                // pieces of a kind that overlap, in groups (in the file's order)
                var left = kind.ToList();
                while (left.Count > 1)
                {
                    var group = new List<string> { left[0] };
                    left.RemoveAt(0);
                    for (bool grew = true; grew;)
                    {
                        grew = false;
                        foreach (var u in left.ToList())
                            if (group.Any(g => Overlap(g, u)))
                            {
                                group.Add(u);
                                left.Remove(u);
                                grew = true;
                            }
                    }
                    if (group.Count > 1)
                        slots.Add(MakeSlot(kind.Key, group, units));
                }
            }
            // from the top down: tops before bottoms
            slots = slots.OrderByDescending(s => s.Choices.SelectMany(c => c.Objects).Select(o => boxes.GetValueOrDefault(o).hi.Y).Max()).ToList();
            // two slots of one kind (a left and a right shoe, each in two versions) get told apart by number
            foreach (var same in slots.GroupBy(s => s.Key).Where(g => g.Count() > 1))
            {
                int n = 1;
                foreach (var s in same)
                {
                    s.Key += "-" + n;
                    s.Label += " " + n++;
                }
            }
            return slots;
        }

        static Slot MakeSlot(string kind, List<string> group, Dictionary<string, HashSet<string>> units)
        {
            var slot = new Slot { Key = Slug(kind), Label = kind };
            // what tells the versions apart: the name past the kind and what they all start with
            int common = group.Aggregate(group[0].Length, (n, g) => Math.Min(n, Enumerable.Range(0, Math.Min(g.Length, group[0].Length)).TakeWhile(i => g[i] == group[0][i]).Count()));
            var rests = group.Select(u => u.Substring(Math.Min(common, u.Length)).Trim('_', ' ', '.', '-')).Select(r => r.Length > 0 ? r : null).ToList();
            // labels in words, without ones every version has or stray single letters (mTops: Tops)
            var shared = rests.Select(r => Words(r ?? "").ToHashSet()).Aggregate((a, b) => a.Intersect(b).ToHashSet());
            for (int i = 0; i < group.Count; i++)
            {
                var unit = group[i];
                var label = rests[i] ?? unit;
                var key = Slug(label);
                var words = Words(label).Where(w => !shared.Contains(w) && !(w.Length == 1 && char.IsLower(w[0]))).ToList();
                label = words.Count > 0 ? string.Join(" ", words) : $"{kind} {i + 1}";
                if (key == All || slot.Choices.Any(c => c.Key == key))
                    key = Slug(unit);
                slot.Choices.Add(new Choice { Key = key, Label = label, Objects = units[unit] });
            }
            return slot;
        }

        static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is var k && k.Length > 0 ? k : "plain";

        /// <summary>
        /// Parses --outfit picks, "tops=yshirth" or "tops=yshirth,bottoms=pantshalf", into <paramref name="picks"/>.
        /// </summary>
        public static void Parse(string value, Dictionary<string, string> picks)
        {
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length != 2 || kv[0].Length == 0 || kv[1].Length == 0)
                    throw new Exception($"outfit takes slot=version, like tops=aloha, not {part}");
                picks[kv[0].ToLowerInvariant()] = kv[1].ToLowerInvariant();
            }
        }

        /// <summary>
        /// Keeps one version of each slot: the one picked, or else the first. Returns lines for the build report.
        /// </summary>
        public static List<string> Apply(ObjModel model, Dictionary<string, string> picks, List<Slot> slots = null)
        {
            slots ??= Find(model);
            var said = new List<string>();
            picks ??= new();
            foreach (var slot in picks.Keys.Where(k => !slots.Any(s => s.Key == k)))
                said.Add($"outfit: this model has no {slot} in more than one version, so --outfit {slot} is left alone");
            var drop = new HashSet<string>();
            foreach (var slot in slots)
            {
                var pick = picks.GetValueOrDefault(slot.Key);
                if (pick == All)
                {
                    said.Add($"outfit: kept every version of the {slot.Label} ({string.Join(", ", slot.Choices.Select(c => c.Label))}), drawn on top of each other");
                    continue;
                }
                var choice = slot.Choices.FirstOrDefault(c => c.Key == (pick ?? slot.Default))
                    ?? throw new Exception($"there is no {slot.Key} called {pick} (pick from: {string.Join(", ", slot.Choices.Select(c => c.Key))}, or all)");
                drop.UnionWith(slot.Objects.Except(choice.Objects));
                said.Add($"outfit: the model has {slot.Choices.Count} {slot.Label} on top of each other ({string.Join(", ", slot.Choices.Select(c => c.Key))}); kept {choice.Key}" + (pick == null ? " (the first; pick one with --outfit " + slot.Key + "=...)" : ""));
            }
            if (drop.Count > 0)
                model.Triangles.RemoveAll(t => t.Object != null && drop.Contains(t.Object));
            return said;
        }

        /// <summary>
        /// Draws each version of each slot (<paramref name="prefix"/>-outfit-<slot>-<key>.png), on a copy of the
        /// model with the other slots at their first version, framed on the slot from the front.
        /// </summary>
        public static void Pictures(ObjModel model, List<Slot> slots, string prefix)
        {
            foreach (var slot in slots)
            {
                var objects = slot.Objects;
                var corners = model.Triangles.Where(t => t.Object != null && objects.Contains(t.Object)).SelectMany(t => new[] { t.A, t.B, t.C }).Select(c => model.Positions[c.Position]).ToList();
                var lo = corners.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                var hi = corners.Aggregate(new Vector3(float.MinValue), Vector3.Max);
                foreach (var choice in slot.Choices.Select(c => c.Key).Append(All))
                {
                    var copy = model.Copy();
                    var picks = slots.ToDictionary(s => s.Key, s => s == slot ? choice : s.Default);
                    Apply(copy, picks, slots);
                    Preview.Closeup(copy, lo, hi, Vector3.UnitZ, $"{prefix}-outfit-{slot.Key}-{choice}.png");
                }
            }
        }
    }
}
