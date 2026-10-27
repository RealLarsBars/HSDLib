using StbImageWriteSharp;
using System;
using System.IO;
using System.Numerics;

namespace SkinTool
{
    /// <summary>
    /// A plain RGBA picture for the preview renderer.
    /// </summary>
    public class Bitmap
    {
        public readonly int Width, Height;
        public readonly byte[] Rgba;

        /// <param name="clear">leave the background see-through, to lay the picture over another</param>
        public Bitmap(int width, int height, bool clear = false)
        {
            Width = width;
            Height = height;
            Rgba = new byte[width * height * 4];

            // dark grey background
            for (int i = 0; i < Rgba.Length; i += 4)
            {
                Rgba[i] = 40;
                Rgba[i + 1] = 44;
                Rgba[i + 2] = 52;
                Rgba[i + 3] = (byte)(clear ? 0 : 255);
            }
        }

        /// <summary>Lays the drawn (opaque) pixels of <paramref name="img"/> over this one, partly see-through.</summary>
        public void Blend(Bitmap img, float amount)
        {
            for (int i = 0; i < Rgba.Length; i += 4)
            {
                if (img.Rgba[i + 3] == 0) continue;
                for (int c = 0; c < 3; c++)
                    Rgba[i + c] = (byte)(Rgba[i + c] * (1 - amount) + img.Rgba[i + c] * amount + 0.5f);
            }
        }

        /// <summary>Sets a pixel from a 0-1 colour.</summary>
        public void Set(int x, int y, Vector3 col)
        {
            int i = (y * Width + x) * 4;
            Rgba[i] = (byte)Math.Clamp(col.X * 255 + 0.5f, 0, 255);
            Rgba[i + 1] = (byte)Math.Clamp(col.Y * 255 + 0.5f, 0, 255);
            Rgba[i + 2] = (byte)Math.Clamp(col.Z * 255 + 0.5f, 0, 255);
            Rgba[i + 3] = 255;
        }

        public void Draw(Bitmap img, int atX, int atY)
        {
            for (int y = 0; y < img.Height; y++)
                Buffer.BlockCopy(img.Rgba, y * img.Width * 4, Rgba, ((atY + y) * Width + atX) * 4, img.Width * 4);
        }

        public void SavePng(string path)
        {
            using var stream = File.Create(path);
            new ImageWriter().WritePng(Rgba, Width, Height, ColorComponents.RedGreenBlueAlpha, stream);
        }
    }
}
