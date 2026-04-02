using System;
using System.IO;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using SkinTatoo.Http;
using StbImageSharp;

namespace SkinTatoo.Services;

public class DecalImageLoader
{
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private Lumina.GameData? luminaForDisk;

    public DecalImageLoader(IPluginLog log, IDataManager dataManager)
    {
        this.log = log;
        this.dataManager = dataManager;
    }

    private Lumina.GameData GetLuminaForDisk()
    {
        if (luminaForDisk == null)
        {
            // DataPath points to sqpack directory, Lumina GameData needs the parent 'game' directory
            var sqpackPath = dataManager.GameData.DataPath.FullName;
            DebugServer.AppendLog($"[ImageLoader] DataPath: {sqpackPath}");
            luminaForDisk = new Lumina.GameData(sqpackPath, new Lumina.LuminaOptions
            {
                PanicOnSheetChecksumMismatch = false,
            });
        }
        return luminaForDisk;
    }

    public (byte[] Data, int Width, int Height)? LoadImage(string path)
    {
        if (!File.Exists(path))
        {
            log.Error("Image file not found: {0}", path);
            DebugServer.AppendLog($"[ImageLoader] File not found: {path}");
            return null;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            var result = ext switch
            {
                ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" => LoadStbImage(path),
                ".dds" => LoadDds(path),
                ".tex" => LoadTexFile(path),
                _ => throw new NotSupportedException($"Unsupported image format: {ext}"),
            };
            DebugServer.AppendLog($"[ImageLoader] Loaded {ext}: {result.Width}x{result.Height} from {Path.GetFileName(path)}");
            return result;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to load image: {0}", path);
            DebugServer.AppendLog($"[ImageLoader] Failed: {ex.GetType().Name}: {ex.Message}");
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

    private (byte[] Data, int Width, int Height) LoadTexFile(string path)
    {
        // Use our Lumina submodule to parse .tex files (handles BC1/BC3/BC5/BC7)
        var lumina = GetLuminaForDisk();
        var texFile = lumina.GetFileFromDisk<TexFile>(path);

        // ImageData returns B8G8R8A8 decoded pixels (mip 0, slice 0)
        var bgra = texFile.ImageData;
        var width = texFile.Header.Width;
        var height = texFile.Header.Height;

        // Convert BGRA → RGBA
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            rgba[i + 0] = bgra[i + 2]; // R ← B
            rgba[i + 1] = bgra[i + 1]; // G ← G
            rgba[i + 2] = bgra[i + 0]; // B ← R
            rgba[i + 3] = bgra[i + 3]; // A ← A
        }

        DebugServer.AppendLog($"[ImageLoader] .tex decoded: {width}x{height}, format=0x{(int)texFile.Header.Format:X}");
        return (rgba, width, height);
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
