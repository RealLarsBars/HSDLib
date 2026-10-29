using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace SkinTool
{
    /// <summary>
    /// Models that come with their eyes in more than one state, to pick which the skin gets:
    ///  - Super Mario Odyssey rips: an eye area per state beside the model (Name_LEyeClose.SMD; see <see cref="EyeSwap"/>);
    ///  - eye textures in a numbered or named set (cartman_eyes_1.png to _9, eye_open.png and eye_closed.png);
    ///  - eye meshes per state drawn on top of each other (Eye_Open and Eye_Close).
    /// Each choice has a short key for --eyes. Left alone, the skin gets what it always did: the open eyes,
    /// or the texture the model's materials name.
    /// </summary>
    public static class Eyes
    {
        public class Choice
        {
            public string Key, Label;
            internal string State;                             // Odyssey: the files' state ("" open)
            internal Dictionary<string, string> Textures;      // texture set: material -> texture file
            internal HashSet<string> Objects;                  // meshes: the eye meshes this state keeps
        }

        public class Found
        {
            public string Kind; // "odyssey", "textures" or "meshes"
            public List<Choice> Choices = new();
            public string Default;
            // the eye meshes or materials, to frame a close-up
            internal HashSet<string> Materials = new(), Objects = new();
        }

        static readonly string[] StateWords = { "open", "opened", "close", "closed", "shut", "half", "halfopen", "blink", "wink", "smile", "happy", "sad", "angry", "mad", "sleep", "sleepy", "surprised", "shock", "hurt", "damage", "dead" };

        public static Found Find(ObjModel model, string modelPath) =>
            Odyssey(modelPath) ?? Meshes(model) ?? TextureSet(model);

        static Found Odyssey(string modelPath)
        {
            var states = EyeSwap.States(modelPath);
            if (states.Count == 0 || states.Count == 1 && states[0] != "")
                return null;
            var found = new Found { Kind = "odyssey", Default = states.Contains("") ? "open" : Slug(states[0]) };
            foreach (var s in states)
                found.Choices.Add(new Choice { Key = Slug(EyeSwap.Name(s)), Label = EyeSwap.Name(s) + " eyes", State = s });
            return found;
        }

        /// <summary>Eye meshes named for a state (Eye_Open, EyeL_Close): only one state's are kept.</summary>
        static Found Meshes(ObjModel model)
        {
            var byState = new Dictionary<string, HashSet<string>>();
            foreach (var name in model.Triangles.Select(t => t.Object).Where(o => o != null).Distinct())
            {
                if (!Regex.IsMatch(name, "eye", RegexOptions.IgnoreCase))
                    continue;
                var state = StateWords.OrderByDescending(w => w.Length).FirstOrDefault(name.ToLowerInvariant().Contains);
                if (state == null)
                    continue;
                if (!byState.TryGetValue(state, out var set))
                    byState[state] = set = new HashSet<string>();
                set.Add(name);
            }
            if (byState.Count < 2)
                return null;
            var found = new Found { Kind = "meshes" };
            foreach (var (state, objects) in byState.OrderBy(e => !e.Key.StartsWith("open")).ThenBy(e => e.Key))
            {
                found.Choices.Add(new Choice { Key = Slug(state), Label = state + " eyes", Objects = objects });
                found.Objects.UnionWith(objects);
            }
            found.Default = (found.Choices.FirstOrDefault(c => c.Key.StartsWith("open")) ?? found.Choices[0]).Key;
            return found;
        }

        // cartman_eyes_1, eye_closed: the set's name and the variant. Not "Eye (PS2).png": the same eyes
        // ripped from another console (Gusteau's) aren't another state of them.
        static readonly Regex Variant = new("^(.*?)(?:[ _.-]*(\\d+)|[ _.-]*(" + string.Join("|", StateWords) + "))$", RegexOptions.IgnoreCase);

        static (string set, string variant) Split(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var m = Variant.Match(name);
            if (!m.Success || m.Groups[1].Value.Length == 0)
                return (name.ToLowerInvariant(), "");
            var variant = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            return (m.Groups[1].Value.ToLowerInvariant(), variant);
        }

        /// <summary>An eye material whose texture is one of a set beside it: each variant swaps in its own.</summary>
        static Found TextureSet(ObjModel model)
        {
            var used = model.Triangles.Select(t => t.Material).Where(m => m != null).ToHashSet();
            // variant -> material -> texture
            var variants = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            string current = null;
            var found = new Found { Kind = "textures" };
            foreach (var (material, texture) in model.Textures)
            {
                if (!used.Contains(material) || model.TextureOf(material) == null || !Regex.IsMatch(Path.GetFileName(texture), "eye", RegexOptions.IgnoreCase))
                    continue;
                var (set, own) = Split(texture);
                var siblings = Directory.EnumerateFiles(Path.GetDirectoryName(texture))
                    .Where(f => Regex.IsMatch(f, "\\.(png|jpe?g|bmp|tga)$", RegexOptions.IgnoreCase) && Split(f).set == set)
                    .ToList();
                if (siblings.Count < 2)
                    continue;
                found.Materials.Add(material);
                current ??= own;
                foreach (var f in siblings)
                {
                    var v = Split(f).variant;
                    if (!variants.TryGetValue(v, out var swaps))
                        variants[v] = swaps = new Dictionary<string, string>();
                    swaps[material] = f;
                }
            }
            if (variants.Count < 2)
                return null;
            foreach (var (variant, swaps) in variants.OrderBy(e => e.Key.Length > 0).ThenBy(e => int.TryParse(e.Key, out int n) ? n : int.MaxValue).ThenBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                var key = variant.Length == 0 ? "plain" : Slug(variant);
                if (found.Choices.Any(c => c.Key == key))
                    continue;
                found.Choices.Add(new Choice
                {
                    Key = key,
                    Label = variant.Length == 0 ? "the model's own eyes" : int.TryParse(variant, out _) ? $"eyes {variant}" : $"{variant.ToLowerInvariant()} eyes",
                    Textures = swaps,
                });
            }
            found.Default = current.Length == 0 ? "plain" : Slug(current);
            return found;
        }

        static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is var k && k.Length > 0 ? k : "plain";

        /// <summary>
        /// Gives the model the eyes picked by key (null: the default). Returns a line for the build report, or null.
        /// </summary>
        public static string Apply(ObjModel model, string modelPath, string pick, Found found = null)
        {
            found ??= Find(model, modelPath);
            if (found == null)
                return pick == null ? null : "eyes: this model has only one set of eyes, so --eyes is left alone";
            var choice = found.Choices.FirstOrDefault(c => c.Key == (pick ?? found.Default))
                ?? throw new Exception($"there are no eyes called {pick} (pick from: {string.Join(", ", found.Choices.Select(c => c.Key))})");
            return Apply(model, modelPath, found, choice);
        }

        static string Apply(ObjModel model, string modelPath, Found found, Choice choice)
        {
            switch (found.Kind)
            {
                case "odyssey":
                    return EyeSwap.Apply(model, modelPath, choice.State);
                case "meshes":
                    int before = model.Triangles.Count;
                    model.Triangles.RemoveAll(t => t.Object != null && found.Objects.Contains(t.Object) && !choice.Objects.Contains(t.Object));
                    return $"eyes: kept the {choice.Label} ({string.Join(", ", choice.Objects.OrderBy(o => o))}), left out {before - model.Triangles.Count} triangles of the other states";
                default:
                    if (choice.Key == found.Default)
                        return null;
                    foreach (var (material, texture) in choice.Textures)
                        model.Textures[material] = texture;
                    return $"eyes: {string.Join(", ", choice.Textures.Values.Select(Path.GetFileName).Distinct())}";
            }
        }

        /// <summary>
        /// Draws a close-up of each choice's eyes (<paramref name="prefix"/>-eyes-<key>.png), on copies of the
        /// model as loaded with no eye swap. False when the eyes couldn't be found on the model (an Odyssey
        /// eye file that doesn't line up with it), so there's nothing to pick.
        /// </summary>
        public static bool Pictures(ObjModel raw, string modelPath, Found found, string prefix)
        {
            Vector3 lo = default, hi = default, facing = Vector3.UnitZ;
            bool framed = false;
            var failed = new List<Choice>();
            foreach (var choice in found.Choices.OrderBy(c => c.Key != found.Default).ToList())
            {
                var copy = raw.Copy();
                // an eye file that doesn't line up with the face: not a choice
                if (Apply(copy, modelPath, found, choice) == null && found.Kind == "odyssey")
                {
                    failed.Add(choice);
                    continue;
                }
                // framed once, on the default eyes, so every picture shows the same spot
                if (!framed)
                {
                    var eyes = copy.Triangles.Where(t => found.Kind switch
                    {
                        "odyssey" => t.Object == "eye area",
                        "meshes" => found.Objects.Contains(t.Object ?? ""),
                        _ => found.Materials.Contains(t.Material ?? ""),
                    }).ToList();
                    if (eyes.Count == 0)
                        return false;
                    var corners = eyes.SelectMany(t => new[] { t.A, t.B, t.C }).ToList();
                    lo = corners.Select(c => copy.Positions[c.Position]).Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                    hi = corners.Select(c => copy.Positions[c.Position]).Aggregate(new Vector3(float.MinValue), Vector3.Max);
                    var n = corners.Where(c => c.Normal >= 0).Aggregate(Vector3.Zero, (s, c) => s + copy.Normals[c.Normal]);
                    n.Y = 0;
                    if (n.LengthSquared() > 1e-6f)
                        facing = Vector3.Normalize(n);
                    framed = true;
                }
                Preview.Closeup(copy, lo, hi, facing, $"{prefix}-eyes-{choice.Key}.png");
            }
            found.Choices.RemoveAll(failed.Contains);
            return framed && found.Choices.Count > 1;
        }
    }
}
