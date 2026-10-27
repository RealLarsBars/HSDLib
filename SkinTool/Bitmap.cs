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

        public Bitmap(int width, int height)
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
                Rgba[i + 3] = 255;
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
