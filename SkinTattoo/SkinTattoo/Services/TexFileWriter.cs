using System;
using System.IO;

namespace SkinTattoo.Services;

public static class TexFileWriter
{
    private const uint HeaderSize = 80;
    private const uint TextureFormatB8G8R8A8 = 0x1450;
    private const uint AttributeTextureType2D = 0x00800000;

    public static void WriteRgba(string path, byte[] rgbaBytes, int width, int height)
    {
        var bgra = new byte[rgbaBytes.Length];
        for (var i = 0; i < rgbaBytes.Length; i += 4)
        {
            bgra[i + 0] = rgbaBytes[i + 2];
            bgra[i + 1] = rgbaBytes[i + 1];
            bgra[i + 2] = rgbaBytes[i + 0];
            bgra[i + 3] = rgbaBytes[i + 3];
        }
        WriteBgra(path, bgra, bgra.Length, width, height);
    }

    /// <summary>Write a B8G8R8A8 .tex file from a pre-swizzled BGRA buffer.</summary>
    public static void WriteBgra(string path, byte[] bgra, int bgraLength, int width, int height)
    {
        WriteBgra(path, new ReadOnlySpan<byte>(bgra, 0, bgraLength), width, height);
    }

    private static void WriteBgra(string path, ReadOnlySpan<byte> bgra, int width, int height)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs);

        bw.Write(AttributeTextureType2D);
        bw.Write(TextureFormatB8G8R8A8);
        bw.Write((ushort)width);
        bw.Write((ushort)height);
        bw.Write((ushort)1);  // depth
        bw.Write((ushort)1);  // mip count
        bw.Write(0u);
        bw.Write(0u);
        bw.Write(0u);

        bw.Write(HeaderSize);                // surface 0 offset
        for (var i = 1; i < 13; i++)
            bw.Write(0u);                    // remaining surface offsets

        bw.Write(bgra);
    }
}
