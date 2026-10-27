using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SkinTool
{
    /// <summary>
    /// Lets a model arrive as one .zip (OBJ, MTL and textures together), which is how
    /// uploads from the Top-8er panel come in.
    /// </summary>
    static class ModelZip
    {
        const long MaxUnpacked = 200L * 1024 * 1024;

        /// <summary>Unpacks the zip into <paramref name="folder"/> and returns the OBJ inside.</summary>
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

            var objs = Directory.EnumerateFiles(folder, "*.obj", SearchOption.AllDirectories).ToList();
            if (objs.Count == 0)
                throw new Exception("the zip has no .obj file");
            if (objs.Count > 1)
                throw new Exception($"the zip has {objs.Count} .obj files; put one model in it");
            return objs[0];
        }

        public static void Pack(string folder, string zip)
        {
            if (File.Exists(zip))
                File.Delete(zip);
            ZipFile.CreateFromDirectory(folder, zip);
        }
    }
}
