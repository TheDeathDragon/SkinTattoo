using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SkinTatoo.Services;

public static class TexFileWriter
{
    private const uint HeaderSize = 80;
    private const uint TextureFormatB8G8R8A8 = 0x1450;
    private const uint AttributeTextureType2D = 0x00800000;

    public static void WriteUncompressed(string path, byte[] rgbaFloatData, int width, int height)
    {
        var pixelCount = width * height;
        // Output as B8G8R8A8 (FFXIV convention: the 0x1450 format is BGRA order)
        var byteData = new byte[pixelCount * 4];
        var floatSpan = MemoryMarshal.Cast<byte, float>(rgbaFloatData);

        for (var i = 0; i < pixelCount; i++)
        {
            var r = FloatToByte(floatSpan[i * 4 + 0]);
            var g = FloatToByte(floatSpan[i * 4 + 1]);
            var b = FloatToByte(floatSpan[i * 4 + 2]);
            var a = FloatToByte(floatSpan[i * 4 + 3]);
            // BGRA order for FFXIV
            byteData[i * 4 + 0] = b;
            byteData[i * 4 + 1] = g;
            byteData[i * 4 + 2] = r;
            byteData[i * 4 + 3] = a;
        }

        // Use FileShare.Read so game/Penumbra can read while we write
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs);

        bw.Write(AttributeTextureType2D);    // attributes (TextureType2D)
        bw.Write(TextureFormatB8G8R8A8);     // format (B8G8R8A8)
        bw.Write((ushort)width);             // width
        bw.Write((ushort)height);            // height
        bw.Write((ushort)1);                 // depth
        bw.Write((ushort)1);                 // mip count
        bw.Write(0u);                        // LOD offset 0
        bw.Write(0u);                        // LOD offset 1
        bw.Write(0u);                        // LOD offset 2

        bw.Write(HeaderSize);                // surface 0 offset
        for (var i = 1; i < 13; i++)
            bw.Write(0u);                    // remaining surface offsets

        bw.Write(byteData);
    }

    private static byte FloatToByte(float v) =>
        (byte)Math.Clamp((int)(v * 255.0f + 0.5f), 0, 255);
}
