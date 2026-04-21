using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using SkinTattoo.Core;
using SkinTattoo.Http;
using StbImageWriteSharp;

namespace SkinTattoo.Services;

/// <summary>
/// Cross-project decal resource library. Content-hashed blob store with
/// lazy-generated thumbnails. Index persisted to library/index.json.
/// </summary>
public sealed class LibraryService
{
    private const int ThumbSize = 96;
    private const int HashHexLen = 16;

    private readonly IPluginLog log;
    private readonly DecalImageLoader imageLoader;
    private readonly string rootDir;
    private readonly string blobDir;
    private readonly string thumbDir;
    private readonly string indexPath;

    private readonly object sync = new();
    private readonly Dictionary<string, LibraryEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> pendingThumbs = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ImportImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds", ".tex"
    };

    private sealed class LibraryIndexModel
    {
        public List<LibraryEntry> Entries { get; set; } = [];
        public List<string> Folders { get; set; } = [];
    }

    public int ImportFolderTree(string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            return 0;

        var root = Path.GetFullPath(sourceRoot);
        var rootFolderName = NormalizeFolderPath(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)));
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        int imported = 0;

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            if (!ImportImageExtensions.Contains(ext))
                continue;

            var entry = ImportFromPath(file);
            if (entry == null)
                continue;

            string relativeDir;
            try
            {
                relativeDir = Path.GetRelativePath(root, Path.GetDirectoryName(file) ?? string.Empty);
            }
            catch
            {
                relativeDir = string.Empty;
            }

            var relativeFolder = NormalizeFolderPath(relativeDir);
            var folder = string.IsNullOrEmpty(rootFolderName)
                ? relativeFolder
                : string.IsNullOrEmpty(relativeFolder)
                    ? rootFolderName
                    : NormalizeFolderPath(rootFolderName + "/" + relativeFolder);
            lock (sync)
            {
                entry.FolderPath = folder;
                EnsureFolderTrackedLocked(folder);
                entries[entry.Hash] = entry;
                SaveIndexLocked();
            }

            imported++;
        }

        return imported;
    }

    public LibraryService(IPluginLog log, DecalImageLoader imageLoader, string pluginConfigDir)
    {
        this.log = log;
        this.imageLoader = imageLoader;
        rootDir = Path.Combine(pluginConfigDir, "library");
        blobDir = Path.Combine(rootDir, "blobs");
        thumbDir = Path.Combine(rootDir, "thumbs");
        indexPath = Path.Combine(rootDir, "index.json");

        Directory.CreateDirectory(blobDir);
        Directory.CreateDirectory(thumbDir);

        LoadIndex();
    }

    public IReadOnlyList<LibraryEntry> Snapshot()
    {
        lock (sync)
            return entries.Values
                .OrderByDescending(e => e.LastUsedAt)
                .ThenByDescending(e => e.AddedAt)
                .ToList();
    }

    public IReadOnlyList<string> SnapshotFolders()
    {
        lock (sync)
            return folders.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string NormalizeFolderPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return string.Empty;
        var p = folderPath.Replace('\\', '/').Trim();
        while (p.StartsWith('/')) p = p[1..];
        while (p.EndsWith('/')) p = p[..^1];
        if (string.IsNullOrWhiteSpace(p)) return string.Empty;

        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalizedParts = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (normalizedParts.Count > 0)
                    normalizedParts.RemoveAt(normalizedParts.Count - 1);
                continue;
            }
            normalizedParts.Add(part);
        }

        return string.Join('/', normalizedParts);
    }

    public IReadOnlyList<LibraryEntry> SnapshotByFolder(string? folderPath)
    {
        var target = NormalizeFolderPath(folderPath);
        lock (sync)
            return entries.Values
                .Where(e => string.Equals(NormalizeFolderPath(e.FolderPath), target, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.LastUsedAt)
                .ThenByDescending(e => e.AddedAt)
                .ToList();
    }

    public bool CreateFolder(string? folderPath)
    {
        var normalized = NormalizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(normalized)) return false;

        lock (sync)
        {
            if (!folders.Add(normalized)) return false;
            SaveIndexLocked();
            return true;
        }
    }

    public bool SetEntryFolder(string hash, string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(hash)) return false;
        var normalized = NormalizeFolderPath(folderPath);

        lock (sync)
        {
            if (!entries.TryGetValue(hash, out var entry)) return false;
            entry.FolderPath = normalized;
            EnsureFolderTrackedLocked(normalized);
            SaveIndexLocked();
            return true;
        }
    }

    public LibraryEntry? Get(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return null;
        lock (sync)
            return entries.TryGetValue(hash, out var e) ? e : null;
    }

    private void EnsureFolderTrackedLocked(string? folderPath)
    {
        var normalized = NormalizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(normalized)) return;
        folders.Add(normalized);
    }

    public string? ResolveDiskPath(string hash)
    {
        var entry = Get(hash);
        if (entry == null) return null;
        var path = Path.Combine(blobDir, entry.FileName);
        return File.Exists(path) ? path : null;
    }

    public string ThumbPath(LibraryEntry entry) =>
        Path.Combine(thumbDir, entry.Hash + ".png");

    /// <summary>Queue thumbnail regeneration if the file is missing. Safe to call
    /// every frame from the library window  --  dedup via pendingThumbs.</summary>
    public void EnsureThumb(LibraryEntry entry)
    {
        lock (sync) QueueThumbIfMissingLocked(entry);
    }

    /// <summary>Import a disk file. If its content hash is already in the library,
    /// the existing entry is returned (no copy). Otherwise the file is copied into
    /// blobs/ and a new entry is registered.</summary>
    public LibraryEntry? ImportFromPath(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
        {
            log.Warning("[Library] Import skipped, missing file: {0}", sourcePath ?? "<null>");
            return null;
        }

        string hash;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(sourcePath);
            hash = ComputeHash(bytes);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Library] Hash failed: {0}", sourcePath);
            return null;
        }

        lock (sync)
        {
            if (entries.TryGetValue(hash, out var existing))
            {
                existing.FolderPath = NormalizeFolderPath(existing.FolderPath);
                existing.LastUsedAt = DateTime.UtcNow;
                existing.UseCount++;
                SaveIndexLocked();
                QueueThumbIfMissingLocked(existing);
                return existing;
            }
        }

        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var fileName = hash + ext.ToLowerInvariant();
        var destPath = Path.Combine(blobDir, fileName);

        try
        {
            File.WriteAllBytes(destPath, bytes);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Library] Copy failed: {0} -> {1}", sourcePath, destPath);
            return null;
        }

        var (w, h) = ProbeSize(destPath);

        var entry = new LibraryEntry
        {
            Hash = hash,
            FileName = fileName,
            OriginalName = Path.GetFileName(sourcePath),
            FolderPath = string.Empty,
            AddedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
            UseCount = 1,
            Width = w,
            Height = h,
        };

        lock (sync)
        {
            entries[hash] = entry;
            EnsureFolderTrackedLocked(entry.FolderPath);
            SaveIndexLocked();
            QueueThumbIfMissingLocked(entry);
        }

        DebugServer.AppendLog($"[Library] Imported {entry.OriginalName} ({w}x{h}) -> {hash}");
        return entry;
    }

    public void Touch(string hash)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(hash, out var e)) return;
            e.LastUsedAt = DateTime.UtcNow;
            e.UseCount++;
            SaveIndexLocked();
        }
    }

    public bool Remove(string hash)
    {
        LibraryEntry? removed = null;
        lock (sync)
        {
            if (!entries.TryGetValue(hash, out removed)) return false;
            entries.Remove(hash);
            SaveIndexLocked();
        }

        try { File.Delete(Path.Combine(blobDir, removed.FileName)); } catch { }
        try { File.Delete(Path.Combine(thumbDir, removed.Hash + ".png")); } catch { }
        return true;
    }

    private void LoadIndex()
    {
        if (!File.Exists(indexPath)) return;
        try
        {
            var json = File.ReadAllText(indexPath);
            // Legacy format (pre-folder): JSON root is a List<LibraryEntry>. New format
            // wraps entries in LibraryIndexModel. Dispatch on the first non-whitespace char
            // to avoid deserializing the wrong shape (which throws and wipes the index).
            List<LibraryEntry>? list;
            LibraryIndexModel? model = null;
            var trimmed = json.AsSpan().TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '[')
            {
                list = JsonSerializer.Deserialize<List<LibraryEntry>>(json);
            }
            else
            {
                model = JsonSerializer.Deserialize<LibraryIndexModel>(json);
                list = model?.Entries;
            }
            if (list == null) return;
            lock (sync)
            {
                entries.Clear();
                folders.Clear();
                foreach (var e in list)
                {
                    if (string.IsNullOrEmpty(e.Hash) || string.IsNullOrEmpty(e.FileName)) continue;
                    e.FolderPath = NormalizeFolderPath(e.FolderPath);
                    entries[e.Hash] = e;
                    EnsureFolderTrackedLocked(e.FolderPath);
                }

                if (model != null)
                {
                    foreach (var f in model.Folders)
                        EnsureFolderTrackedLocked(f);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Library] Index load failed, starting empty");
        }
    }

    private void SaveIndexLocked()
    {
        try
        {
            var model = new LibraryIndexModel
            {
                Entries = entries.Values.ToList(),
                Folders = folders.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
            };
            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            var tmp = indexPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(indexPath)) File.Replace(tmp, indexPath, null);
            else File.Move(tmp, indexPath);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Library] Index save failed");
        }
    }

    private static string ComputeHash(byte[] data)
    {
        var full = SHA256.HashData(data);
        return Convert.ToHexString(full, 0, HashHexLen / 2).ToLowerInvariant();
    }

    private (int W, int H) ProbeSize(string path)
    {
        var img = imageLoader.LoadImage(path, useCache: false);
        return img.HasValue ? (img.Value.Width, img.Value.Height) : (0, 0);
    }

    private void QueueThumbIfMissingLocked(LibraryEntry entry)
    {
        var thumb = Path.Combine(thumbDir, entry.Hash + ".png");
        if (File.Exists(thumb)) return;
        if (!pendingThumbs.Add(entry.Hash)) return;

        var blob = Path.Combine(blobDir, entry.FileName);
        Task.Run(() => GenerateThumb(entry.Hash, blob, thumb));
    }

    private void GenerateThumb(string hash, string srcPath, string thumbPath)
    {
        try
        {
            var img = imageLoader.LoadImage(srcPath, useCache: false);
            if (!img.HasValue) return;

            var (src, sw, sh) = img.Value;
            if (sw <= 0 || sh <= 0) return;

            int tw, th;
            if (sw >= sh)
            {
                tw = ThumbSize;
                th = Math.Max(1, (int)Math.Round(sh * (float)ThumbSize / sw));
            }
            else
            {
                th = ThumbSize;
                tw = Math.Max(1, (int)Math.Round(sw * (float)ThumbSize / sh));
            }

            var dst = ResizeBilinearRgba(src, sw, sh, tw, th);

            using var ms = new MemoryStream();
            new ImageWriter().WritePng(dst, tw, th, ColorComponents.RedGreenBlueAlpha, ms);
            var tmp = thumbPath + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            if (File.Exists(thumbPath)) File.Replace(tmp, thumbPath, null);
            else File.Move(tmp, thumbPath);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Library] Thumb gen failed: {0}", hash);
        }
        finally
        {
            lock (sync) pendingThumbs.Remove(hash);
        }
    }

    private static byte[] ResizeBilinearRgba(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        float xScale = (float)sw / dw;
        float yScale = (float)sh / dh;
        for (int y = 0; y < dh; y++)
        {
            float sy = (y + 0.5f) * yScale - 0.5f;
            int y0 = Math.Clamp((int)Math.Floor(sy), 0, sh - 1);
            int y1 = Math.Clamp(y0 + 1, 0, sh - 1);
            float fy = sy - y0;
            for (int x = 0; x < dw; x++)
            {
                float sx = (x + 0.5f) * xScale - 0.5f;
                int x0 = Math.Clamp((int)Math.Floor(sx), 0, sw - 1);
                int x1 = Math.Clamp(x0 + 1, 0, sw - 1);
                float fx = sx - x0;

                int i00 = (y0 * sw + x0) * 4;
                int i01 = (y0 * sw + x1) * 4;
                int i10 = (y1 * sw + x0) * 4;
                int i11 = (y1 * sw + x1) * 4;
                int o = (y * dw + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    float top = src[i00 + c] * (1 - fx) + src[i01 + c] * fx;
                    float bot = src[i10 + c] * (1 - fx) + src[i11 + c] * fx;
                    dst[o + c] = (byte)Math.Clamp((int)(top * (1 - fy) + bot * fy + 0.5f), 0, 255);
                }
            }
        }
        return dst;
    }
}
