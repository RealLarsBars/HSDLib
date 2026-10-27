using System;
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

        /// <summary>Unpacks the zip into <paramref name="folder"/> and returns the model inside.</summary>
        public static string Extract(string zip, string folder)
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

            var models = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(ModelImport.IsModel).ToList();
            if (models.Count == 0)
                throw new Exception("the zip has no model (.obj, .fbx or .dae)");
            if (models.Count > 1)
                throw new Exception($"the zip has {models.Count} models ({string.Join(", ", models.Select(Path.GetFileName))}); put one in it");
            return models[0];
        }

        public static void Pack(string folder, string zip)
        {
            if (File.Exists(zip))
                File.Delete(zip);
            ZipFile.CreateFromDirectory(folder, zip);
        }
    }
}
