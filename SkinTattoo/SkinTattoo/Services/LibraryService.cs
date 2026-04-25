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

    public int EntryCount { get { lock (sync) return entries.Count; } }
    public int FolderCount { get { lock (sync) return folders.Count; } }

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

    public bool RenameFolder(string oldPath, string newPath)
    {
        var normalizedOld = NormalizeFolderPath(oldPath);
        var normalizedNew = NormalizeFolderPath(newPath);
        if (string.IsNullOrEmpty(normalizedOld) || string.IsNullOrEmpty(normalizedNew)) return false;
        if (string.Equals(normalizedOld, normalizedNew, StringComparison.OrdinalIgnoreCase)) return false;
        if (normalizedNew.StartsWith(normalizedOld + "/", StringComparison.OrdinalIgnoreCase)) return false;

        lock (sync)
        {
            if (!folders.Contains(normalizedOld)) return false;
            if (folders.Contains(normalizedNew)) return false;

            var affectedFolders = folders
                .Where(f => string.Equals(f, normalizedOld, StringComparison.OrdinalIgnoreCase)
                            || f.StartsWith(normalizedOld + "/", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var renamedFolders = affectedFolders
                .Select(f => normalizedNew + f[normalizedOld.Length..])
                .ToList();

            if (renamedFolders.Any(r => !affectedFolders.Any(a => string.Equals(a, r, StringComparison.OrdinalIgnoreCase))
                                        && folders.Contains(r)))
                return false;

            foreach (var entry in entries.Values)
            {
                var ef = NormalizeFolderPath(entry.FolderPath);
                if (string.Equals(ef, normalizedOld, StringComparison.OrdinalIgnoreCase)
                    || ef.StartsWith(normalizedOld + "/", StringComparison.OrdinalIgnoreCase))
                {
                    entry.FolderPath = normalizedNew + ef[normalizedOld.Length..];
                }
            }

            foreach (var f in affectedFolders)
                folders.Remove(f);
            foreach (var f in renamedFolders)
                folders.Add(f);

            EnsureFolderTrackedLocked(normalizedNew);
            SaveIndexLocked();
            return true;
        }
    }

    public bool SetFavorite(string hash, bool favorite)
    {
        if (string.IsNullOrWhiteSpace(hash)) return false;

        lock (sync)
        {
            if (!entries.TryGetValue(hash, out var entry)) return false;
            if (entry.IsFavorite == favorite) return false;

            entry.IsFavorite = favorite;
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

    public bool DeleteFolder(string folderPath)
    {
        var normalized = NormalizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(normalized)) return false;

        List<LibraryEntry> removedEntries;

        lock (sync)
        {
            removedEntries = entries.Values
                .Where(entry =>
                {
                    var ef = NormalizeFolderPath(entry.FolderPath);
                    return string.Equals(ef, normalized, StringComparison.OrdinalIgnoreCase)
                           || ef.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            foreach (var entry in removedEntries)
                entries.Remove(entry.Hash);

            var toRemove = folders.Where(f =>
                string.Equals(f, normalized, StringComparison.OrdinalIgnoreCase) ||
                f.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase)
            ).ToList();
            foreach (var f in toRemove) folders.Remove(f);

            SaveIndexLocked();
        }

        foreach (var entry in removedEntries)
        {
            try { File.Delete(Path.Combine(blobDir, entry.FileName)); } catch { }
            try { File.Delete(Path.Combine(thumbDir, entry.Hash + ".png")); } catch { }
        }

        return true;
    }

    public LibraryEntry? Get(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return null;
        lock (sync)
            return entries.TryGetValue(hash, out var e) ? e : null;
    }

    private bool EnsureFolderTrackedLocked(string? folderPath)
    {
        var normalized = NormalizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(normalized)) return false;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var addedAny = false;
        var path = string.Empty;
        foreach (var part in parts)
        {
            path = string.IsNullOrEmpty(path) ? part : path + "/" + part;
            if (folders.Add(path))
                addedAny = true;
        }

        return addedAny;
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

    public LibraryEntry? ImportProjectPayload(string hash, string blobFileName, string originalName, string folderPath, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(hash) || bytes.Length == 0)
            return null;

        var normalizedFolder = NormalizeFolderPath(folderPath);
        var ext = Path.GetExtension(blobFileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".png";
        var canonicalBlob = hash + ext.ToLowerInvariant();
        var destPath = Path.Combine(blobDir, canonicalBlob);

        lock (sync)
        {
            if (entries.TryGetValue(hash, out var existing))
            {
                var existingPath = Path.Combine(blobDir, existing.FileName);
                var shouldWrite = true;
                if (File.Exists(existingPath))
                {
                    try
                    {
                        var currentHash = ComputeHash(File.ReadAllBytes(existingPath));
                        shouldWrite = !currentHash.Equals(hash, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        shouldWrite = true;
                    }
                }

                if (shouldWrite)
                    File.WriteAllBytes(destPath, bytes);

                existing.FileName = canonicalBlob;
                if (!string.IsNullOrWhiteSpace(originalName))
                    existing.OriginalName = originalName;
                existing.FolderPath = normalizedFolder;
                existing.LastUsedAt = DateTime.UtcNow;
                existing.UseCount++;
                EnsureFolderTrackedLocked(normalizedFolder);
                SaveIndexLocked();
                QueueThumbIfMissingLocked(existing);
                return existing;
            }

            File.WriteAllBytes(destPath, bytes);
            var (w, h) = ProbeSize(destPath);
            var created = new LibraryEntry
            {
                Hash = hash,
                FileName = canonicalBlob,
                OriginalName = string.IsNullOrWhiteSpace(originalName) ? canonicalBlob : originalName,
                FolderPath = normalizedFolder,
                AddedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow,
                UseCount = 1,
                Width = w,
                Height = h,
            };

            entries[hash] = created;
            EnsureFolderTrackedLocked(normalizedFolder);
            SaveIndexLocked();
            QueueThumbIfMissingLocked(created);
            return created;
        }
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
            // Format dispatch: pre-folder builds wrote a bare List<LibraryEntry>; current
            // builds write LibraryIndexModel. Pick by first non-whitespace char so an old
            // index migrates transparently on next save instead of being wiped.
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
            bool needsRewrite = false;
            lock (sync)
            {
                entries.Clear();
                folders.Clear();
                foreach (var e in list)
                {
                    if (string.IsNullOrEmpty(e.Hash) || string.IsNullOrEmpty(e.FileName))
                    {
                        needsRewrite = true;
                        continue;
                    }
                    var blob = Path.Combine(blobDir, e.FileName);
                    if (!File.Exists(blob))
                    {
                        // Stale entry whose backing file is gone. Drop it permanently and
                        // also clean any orphan thumbnail so the index stays small.
                        try { File.Delete(Path.Combine(thumbDir, e.Hash + ".png")); } catch { }
                        needsRewrite = true;
                        continue;
                    }
                    e.FolderPath = NormalizeFolderPath(e.FolderPath);
                    entries[e.Hash] = e;
                    if (EnsureFolderTrackedLocked(e.FolderPath))
                        needsRewrite = true;
                }
                if (model != null)
                {
                    foreach (var f in model.Folders)
                        if (EnsureFolderTrackedLocked(f))
                            needsRewrite = true;
                }
                // Persist if we dropped anything OR migrated from the legacy array format.
                if (needsRewrite || model == null)
                    SaveIndexLocked();
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
