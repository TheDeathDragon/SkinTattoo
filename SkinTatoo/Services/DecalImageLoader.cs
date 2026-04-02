using System;
using System.IO;
using Dalamud.Plugin.Services;
using StbImageSharp;

namespace SkinTatoo.Services;

public class DecalImageLoader
{
    private readonly IPluginLog log;

    public DecalImageLoader(IPluginLog log)
    {
        this.log = log;
    }

    public (byte[] Data, int Width, int Height)? LoadImage(string path)
    {
        if (!File.Exists(path))
        {
            log.Error("Image file not found: {0}", path);
            return null;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            return ext switch
            {
                ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" => LoadStbImage(path),
                ".dds" => LoadDds(path),
                _ => throw new NotSupportedException($"Unsupported image format: {ext}"),
            };
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load image: {0}", path);
            return null;
        }
    }

    private static (byte[] Data, int Width, int Height) LoadStbImage(string path)
    {
        StbImage.stbi_set_flip_vertically_on_load(0);
        using var stream = File.OpenRead(path);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        return (image.Data, image.Width, image.Height);
    }

    private static (byte[] Data, int Width, int Height) LoadDds(string path)
    {
        var data = File.ReadAllBytes(path);

        if (data.Length < 128 || data[0] != 'D' || data[1] != 'D' || data[2] != 'S' || data[3] != ' ')
            throw new InvalidDataException("Invalid DDS file");

        var height = BitConverter.ToInt32(data, 12);
        var width = BitConverter.ToInt32(data, 16);

        var pixelDataSize = width * height * 4;
        if (data.Length < 128 + pixelDataSize)
            throw new InvalidDataException("DDS file too small for declared dimensions");

        var pixels = new byte[pixelDataSize];
        Array.Copy(data, 128, pixels, 0, pixelDataSize);
        return (pixels, width, height);
    }
}
