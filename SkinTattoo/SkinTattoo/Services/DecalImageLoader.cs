using System;
using System.Collections.Concurrent;
using System.IO;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using SkinTattoo.Http;
using StbImageSharp;

namespace SkinTattoo.Services;

public class DecalImageLoader
{
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private Lumina.GameData? luminaForDisk;

    private record CacheEntry(byte[] Data, int Width, int Height, DateTime Mtime, long Size);
    private readonly ConcurrentDictionary<string, CacheEntry> imageCache =
        new(StringComparer.OrdinalIgnoreCase);

    public DecalImageLoader(IPluginLog log, IDataManager dataManager)
    {
        this.log = log;
        this.dataManager = dataManager;
    }

    public void ClearCache() => imageCache.Clear();

    private Lumina.GameData GetLuminaForDisk()
    {
        if (luminaForDisk == null)
        {
            var sqpackPath = dataManager.GameData.DataPath.FullName;
            luminaForDisk = new Lumina.GameData(sqpackPath, new Lumina.LuminaOptions
            {
                PanicOnSheetChecksumMismatch = false,
            });
        }
        return luminaForDisk;
    }

    public (byte[] Data, int Width, int Height)? LoadImage(string path, bool useCache = true)
    {
        FileInfo fi;
        try
        {
            fi = new FileInfo(path);
            if (!fi.Exists)
            {
                log.Error("Image file not found: {0}", path);
                DebugServer.AppendLog($"[ImageLoader] File not found: {path}");
                return null;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Image file stat failed: {0}", path);
            return null;
        }

        var key = Path.GetFullPath(path);
        var mtime = fi.LastWriteTimeUtc;
        var size = fi.Length;

        if (useCache && imageCache.TryGetValue(key, out var hit) && hit.Mtime == mtime && hit.Size == size)
            return (hit.Data, hit.Width, hit.Height);

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
            if (useCache)
                imageCache[key] = new CacheEntry(result.Data, result.Width, result.Height, mtime, size);
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
        var lumina = GetLuminaForDisk();
        var texFile = lumina.GetFileFromDisk<TexFile>(path);

        var bgra = texFile.ImageData;
        var width = texFile.Header.Width;
        var height = texFile.Header.Height;

        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            rgba[i + 0] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i + 0];
            rgba[i + 3] = bgra[i + 3];
        }

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
