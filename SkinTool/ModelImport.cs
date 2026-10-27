using Assimp;
using Matrix4x4 = System.Numerics.Matrix4x4;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SkinTool
{
    /// <summary>
    /// Reads model formats other than OBJ (FBX, DAE and others) with Assimp, into the same shape an
    /// OBJ loads into: shared positions, per-corner UVs and normals, one material per texture.
    /// </summary>
    static class ModelImport
    {
        public static readonly string[] Extensions = { ".obj", ".fbx", ".dae", ".glb", ".gltf", ".3ds", ".smd" };

        static ModelImport()
        {
            // AssimpNet finds its native library through "libdl.so", which newer Linux systems only ship
            // as libdl.so.2 (and from glibc 2.34 its functions live in libc itself)
            NativeLibrary.SetDllImportResolver(typeof(AssimpContext).Assembly, (name, assembly, paths) =>
                name == "libdl.so" && (NativeLibrary.TryLoad("libdl.so.2", out var handle) || NativeLibrary.TryLoad("libc.so.6", out handle)) ? handle : IntPtr.Zero);

            // a published skintool carries the native library next to it (AssimpNet's own search only
            // looks in places a normal build puts it)
            var lib = Path.Combine(AppContext.BaseDirectory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Assimp64.dll"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libassimp.dylib" : "libassimp.so");
            if (File.Exists(lib) && !Assimp.Unmanaged.AssimpLibrary.Instance.IsLibraryLoaded)
                Assimp.Unmanaged.AssimpLibrary.Instance.LoadLibrary(lib);
        }

        public static bool IsModel(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        public static ObjModel Load(string path)
        {
            using var context = new AssimpContext();
            // node transforms are baked into the vertices, so a model built from posed parts comes in as it looks
            var scene = context.ImportFile(path, PostProcessSteps.Triangulate | PostProcessSteps.PreTransformVertices
                | PostProcessSteps.GenerateSmoothNormals | PostProcessSteps.JoinIdenticalVertices | PostProcessSteps.SortByPrimitiveType);
            if (scene == null || !scene.HasMeshes)
                throw new Exception($"{Path.GetFileName(path)} has no meshes");

            var model = new ObjModel { Directory = Path.GetDirectoryName(Path.GetFullPath(path)) };
            var toYUp = UpAxis(scene);

            // materials, with their diffuse texture; textures packed into the file are written out next to it
            var names = new string[scene.MaterialCount];
            for (int m = 0; m < scene.MaterialCount; m++)
            {
                var mat = scene.Materials[m];
                names[m] = $"{m}_{(string.IsNullOrEmpty(mat.Name) ? "material" : mat.Name)}";
                if (!mat.HasTextureDiffuse)
                    continue;
                var file = mat.TextureDiffuse.FilePath;
                if (file.StartsWith("*") && int.TryParse(file.Substring(1), out int index) && index < scene.TextureCount)
                    file = WriteEmbedded(scene.Textures[index], model.Directory, $"embedded_{index}");
                else if (scene.TextureCount > 0 && scene.Textures.FirstOrDefault(t => t.Filename == file) is EmbeddedTexture packed && packed.IsCompressed)
                    file = WriteEmbedded(packed, model.Directory, Path.GetFileNameWithoutExtension(file));
                model.Textures[names[m]] = model.Find(file.Replace('\\', '/'));
            }

            // positions are shared between meshes and seams by place, like an OBJ's, so the surface stays
            // one piece for weighting even where the file splits it for UVs or materials
            var shared = new Dictionary<Vector3, int>();
            foreach (var mesh in scene.Meshes)
            {
                if (!mesh.HasFaces || mesh.PrimitiveType != PrimitiveType.Triangle)
                    continue;
                var position = new int[mesh.VertexCount];
                int uvStart = model.UVs.Count, normalStart = model.Normals.Count;
                for (int v = 0; v < mesh.VertexCount; v++)
                {
                    var p = Vector3.Transform(new Vector3(mesh.Vertices[v].X, mesh.Vertices[v].Y, mesh.Vertices[v].Z), toYUp);
                    if (!shared.TryGetValue(p, out position[v]))
                    {
                        position[v] = shared[p] = model.Positions.Count;
                        model.Positions.Add(p);
                    }
                    var uv = mesh.HasTextureCoords(0) ? mesh.TextureCoordinateChannels[0][v] : default;
                    model.UVs.Add(new Vector2(uv.X, uv.Y));
                    var n = mesh.HasNormals ? mesh.Normals[v] : new Vector3D(0, 1, 0);
                    model.Normals.Add(Vector3.Normalize(Vector3.TransformNormal(new Vector3(n.X, n.Y, n.Z), toYUp)));
                }

                foreach (var face in mesh.Faces)
                {
                    if (face.IndexCount != 3)
                        continue;
                    ObjModel.Corner C(int k) => new ObjModel.Corner
                    {
                        Position = position[face.Indices[k]],
                        UV = mesh.HasTextureCoords(0) ? uvStart + face.Indices[k] : -1,
                        Normal = normalStart + face.Indices[k],
                    };
                    model.Triangles.Add(new ObjModel.Triangle { A = C(0), B = C(1), C = C(2), Material = names.Length > 0 ? names[mesh.MaterialIndex] : null, Object = mesh.Name });
                }
            }
            if (model.Triangles.Count == 0)
                throw new Exception($"{Path.GetFileName(path)} has no triangles");
            return model;
        }

        /// <summary>
        /// FBX files say which way is up and forward; turn them so up is +Y and the front faces +Z,
        /// as in an OBJ. (Assimp already does this for DAE.)
        /// </summary>
        static Matrix4x4 UpAxis(Scene scene)
        {
            int Get(string key, int fallback) => scene.Metadata.TryGetValue(key, out var e) && e.Data is int i ? i : fallback;
            if (!scene.Metadata.ContainsKey("UpAxis"))
                return Matrix4x4.Identity;
            Vector3 Axis(int axis, int sign) => (axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ) * (sign < 0 ? -1 : 1);
            var right = Axis(Get("CoordAxis", 0), Get("CoordAxisSign", 1));
            var up = Axis(Get("UpAxis", 1), Get("UpAxisSign", 1));
            var front = Axis(Get("FrontAxis", 2), Get("FrontAxisSign", 1));
            // rows of the file's axes: a point's new x, y, z are how far it runs along right, up and front
            return new Matrix4x4(
                right.X, up.X, front.X, 0,
                right.Y, up.Y, front.Y, 0,
                right.Z, up.Z, front.Z, 0,
                0, 0, 0, 1);
        }

        static string WriteEmbedded(EmbeddedTexture texture, string folder, string name)
        {
            if (!texture.IsCompressed)
                throw new Exception("the model packs a raw texture inside it; export its textures as PNG files instead");
            var ext = string.IsNullOrEmpty(texture.CompressedFormatHint) ? "png" : texture.CompressedFormatHint;
            var file = Path.Combine(folder, $"{name}.{ext}");
            File.WriteAllBytes(file, texture.CompressedData);
            return file;
        }
    }
}
