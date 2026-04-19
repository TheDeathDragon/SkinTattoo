using System;
using System.Collections.Concurrent;
using System.IO;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
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

    /// <summary>Alpha looks like a baked-in emissive mask (≥10% transparent + ≥0.1% lit).</summary>
    public static bool LooksLikeEmissiveMask(byte[] rgba)
    {
        if (rgba == null || rgba.Length < 16) return false;
        int total = rgba.Length / 4;
        int zero = 0, nonzero = 0;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] == 0) zero++;
            else nonzero++;
        }
        if (zero == 0 || nonzero == 0) return false;
        return (float)zero / total >= 0.10f && (float)nonzero / total >= 0.001f;
    }

    /// <summary>Heuristic: tangent-space normal maps cluster around (128, 128, 255).</summary>
    public static bool LooksLikeNormalMap(byte[] rgba)
    {
        if (rgba == null || rgba.Length < 16) return false;
        long sumR = 0, sumG = 0, sumB = 0, n = 0;
        for (int i = 0; i + 3 < rgba.Length; i += 64)
        {
            if (rgba[i + 3] < 16) continue;
            sumR += rgba[i]; sumG += rgba[i + 1]; sumB += rgba[i + 2]; n++;
        }
        if (n < 64) return false;
        int avgR = (int)(sumR / n), avgG = (int)(sumG / n), avgB = (int)(sumB / n);
        // Tight R/G + strong B rejects bluish photos (skies, shirts) that otherwise
        // pass a loose B-dominance test.
        return avgB >= 210 && avgB - avgR >= 40 && avgB - avgG >= 40
               && avgR >= 100 && avgR <= 170 && avgG >= 100 && avgG <= 170;
    }

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

        var headerSize = BitConverter.ToInt32(data, 4);
        if (headerSize != 124)
            throw new InvalidDataException($"Unexpected DDS header size: {headerSize}");

        var height = BitConverter.ToInt32(data, 12);
        var width = BitConverter.ToInt32(data, 16);

        var pfFlags = BitConverter.ToUInt32(data, 80);
        var bitCount = BitConverter.ToInt32(data, 88);
        var rMask = BitConverter.ToUInt32(data, 92);
        var gMask = BitConverter.ToUInt32(data, 96);
        var bMask = BitConverter.ToUInt32(data, 100);

        const uint DDPF_ALPHAPIXELS = 0x1;
        const uint DDPF_FOURCC = 0x4;
        const uint DDPF_RGB = 0x40;
        const uint DDPF_LUMINANCE = 0x20000;

        var dataOffset = 128;
        CompressionFormat? bc = null;
        bool isBgra = false;
        bool hasAlpha = false;

        if ((pfFlags & DDPF_FOURCC) != 0)
        {
            var fourCC = new string(new[] { (char)data[84], (char)data[85], (char)data[86], (char)data[87] });
            if (fourCC == "DX10")
            {
                if (data.Length < 148)
                    throw new InvalidDataException("DDS file truncated (missing DX10 header)");
                dataOffset = 148;
                var dxgi = BitConverter.ToInt32(data, 128);
                switch (dxgi)
                {
                    case 28: case 29:                       // R8G8B8A8_UNORM(_SRGB)
                        isBgra = false; hasAlpha = true; break;
                    case 87: case 91:                       // B8G8R8A8_UNORM(_SRGB)
                        isBgra = true; hasAlpha = true; break;
                    case 88:                                // B8G8R8X8_UNORM
                        isBgra = true; hasAlpha = false; break;
                    case 71: case 72: bc = CompressionFormat.Bc1; break;
                    case 74: case 75: bc = CompressionFormat.Bc2; break;
                    case 77: case 78: bc = CompressionFormat.Bc3; break;
                    case 83: case 84: bc = CompressionFormat.Bc5; break;
                    case 98: case 99: bc = CompressionFormat.Bc7; break;
                    default: throw new NotSupportedException($"Unsupported DXGI format: {dxgi}");
                }
            }
            else
            {
                switch (fourCC)
                {
                    case "DXT1": bc = CompressionFormat.Bc1; break;
                    case "DXT2":
                    case "DXT3": bc = CompressionFormat.Bc2; break;
                    case "DXT4":
                    case "DXT5": bc = CompressionFormat.Bc3; break;
                    case "ATI2":
                    case "BC5U": bc = CompressionFormat.Bc5; break;
                    case "BC7U":
                    case "BC7L": bc = CompressionFormat.Bc7; break;
                    default: throw new NotSupportedException($"Unsupported DDS FourCC: {fourCC}");
                }
            }
        }
        else if ((pfFlags & DDPF_RGB) != 0 && bitCount == 32)
        {
            if (rMask == 0x00FF0000 && gMask == 0x0000FF00 && bMask == 0x000000FF) isBgra = true;
            else if (rMask == 0x000000FF && gMask == 0x0000FF00 && bMask == 0x00FF0000) isBgra = false;
            else throw new NotSupportedException(
                $"Unsupported 32-bit DDS bitmask: R={rMask:X8} G={gMask:X8} B={bMask:X8}");
            hasAlpha = (pfFlags & DDPF_ALPHAPIXELS) != 0;
        }
        else if ((pfFlags & DDPF_LUMINANCE) != 0)
        {
            throw new NotSupportedException("Luminance DDS format is not supported");
        }
        else
        {
            throw new NotSupportedException(
                $"Unsupported DDS pixel format: flags=0x{pfFlags:X} bitCount={bitCount}");
        }

        var pixelDataSize = width * height * 4;
        var pixels = new byte[pixelDataSize];

        if (bc.HasValue)
        {
            var blockBytes = bc.Value == CompressionFormat.Bc1 ? 8 : 16;
            var blocksW = Math.Max(1, (width + 3) / 4);
            var blocksH = Math.Max(1, (height + 3) / 4);
            var compressedSize = blocksW * blocksH * blockBytes;
            if (data.Length < dataOffset + compressedSize)
                throw new InvalidDataException(
                    $"DDS file truncated: need {compressedSize} compressed bytes, have {data.Length - dataOffset}");

            using var stream = new MemoryStream(data, dataOffset, compressedSize);
            var decoder = new BcDecoder();
            var rgba = decoder.DecodeRaw(stream, width, height, bc.Value);
            for (int i = 0; i < rgba.Length; i++)
            {
                var c = rgba[i];
                pixels[i * 4 + 0] = c.r;
                pixels[i * 4 + 1] = c.g;
                pixels[i * 4 + 2] = c.b;
                pixels[i * 4 + 3] = c.a;
            }
        }
        else
        {
            if (data.Length < dataOffset + pixelDataSize)
                throw new InvalidDataException("DDS file too small for declared dimensions");

            if (isBgra)
            {
                for (int i = 0; i < pixelDataSize; i += 4)
                {
                    pixels[i + 0] = data[dataOffset + i + 2];
                    pixels[i + 1] = data[dataOffset + i + 1];
                    pixels[i + 2] = data[dataOffset + i + 0];
                    pixels[i + 3] = hasAlpha ? data[dataOffset + i + 3] : (byte)255;
                }
            }
            else
            {
                Array.Copy(data, dataOffset, pixels, 0, pixelDataSize);
                if (!hasAlpha)
                {
                    for (int i = 3; i < pixelDataSize; i += 4)
                        pixels[i] = 255;
                }
            }
        }

        return (pixels, width, height);
    }
}
