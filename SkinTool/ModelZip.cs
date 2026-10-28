using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SkinTool
{
    /// <summary>
    /// Lets a model arrive as one .zip (a model with its textures), which is how
    /// uploads from the Top-8er panel come in.
    /// </summary>
    static class ModelZip
    {
        const long MaxUnpacked = 200L * 1024 * 1024;

        /// <summary>
        /// The model to use from a model file, a folder or a zip (unpacked into <paramref name="folder"/>).
        /// With more than one model, <paramref name="pick"/> names it by its path inside (as models lists it).
        /// </summary>
        public static string Find(string input, string folder, string pick = null)
        {
            if (input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                Unpack(input, folder);
            else if (Directory.Exists(input))
                folder = input;
            else if (pick == null)
                return input;
            else
                folder = Path.GetDirectoryName(Path.GetFullPath(input));

            var models = ModelsIn(folder);
            if (pick != null)
            {
                var want = pick.Replace('\\', '/');
                return models.FirstOrDefault(m => Path.GetRelativePath(folder, m).Replace('\\', '/').Equals(want, StringComparison.OrdinalIgnoreCase))
                    ?? throw new Exception($"there's no model called {pick}");
            }
            if (models.Count == 0)
                throw new Exception("the zip has no model (.obj, .fbx or .dae)");
            if (models.Count > 1)
                throw new Exception($"the zip has {models.Count} models ({string.Join(", ", models.Select(Path.GetFileName))}); pick one");
            return models[0];
        }

        /// <summary>
        /// Every model in a folder, in a fixed order; not the eye areas a Super Mario Odyssey rip carries for
        /// each expression (Name_LEyeClose.SMD and so on), which are pieces of a face (see <see cref="EyeSwap"/>).
        /// </summary>
        public static List<string> ModelsIn(string folder) =>
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(ModelImport.IsModel)
                .Where(m => !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(m), "_[LR]Eye[A-Za-z]*$"))
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Unpacks the zip into <paramref name="folder"/>.</summary>
        public static void Unpack(string zip, string folder)
        {
            using var archive = ZipFile.OpenRead(zip);
            if (archive.Entries.Sum(e => e.Length) > MaxUnpacked)
                throw new Exception("the zip unpacks to more than 200 MB");

            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/") || entry.FullName.StartsWith("__MACOSX/"))
                    continue;

                // never write outside the folder, whatever the entry's name says
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('\\', '/')));
                if (!target.StartsWith(root))
                    throw new Exception($"bad path in zip: {entry.FullName}");

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                entry.ExtractToFile(target, true);
            }
        }

        public static void Pack(string folder, string zip)
        {
            if (File.Exists(zip))
                File.Delete(zip);
            ZipFile.CreateFromDirectory(folder, zip);
        }
    }
}
