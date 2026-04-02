using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SkinTatoo.Services;

public static class TexFileWriter
{
    private const uint HeaderSize = 80;
    private const uint TextureFormatR8G8B8A8 = 0x1450;

    public static void WriteUncompressed(string path, byte[] rgbaFloatData, int width, int height)
    {
        var pixelCount = width * height;
        var byteData = new byte[pixelCount * 4];
        var floatSpan = MemoryMarshal.Cast<byte, float>(rgbaFloatData);

        for (var i = 0; i < pixelCount; i++)
        {
            byteData[i * 4 + 0] = FloatToByte(floatSpan[i * 4 + 0]);
            byteData[i * 4 + 1] = FloatToByte(floatSpan[i * 4 + 1]);
            byteData[i * 4 + 2] = FloatToByte(floatSpan[i * 4 + 2]);
            byteData[i * 4 + 3] = FloatToByte(floatSpan[i * 4 + 3]);
        }

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        bw.Write(0u);                        // attributes
        bw.Write(TextureFormatR8G8B8A8);     // format
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
