using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Plugin.Services;
using SkinTattoo.Core;
using SkinTattoo.Interop;

namespace SkinTattoo.Services;

public sealed class ProjectFileService
{
    private const string ProjectFileName = "project.json";
    private const string PenumbraModDirToken = "{PenumbraModDir}";
    private const string GameDirToken = "{GameDir}";

    private readonly IPluginLog log;
    private readonly LibraryService? library;
    private readonly PenumbraBridge? penumbra;
    private readonly IDataManager? dataManager;
    private readonly string pluginConfigDir;

    private readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string ProjectsRoot { get; }

    public ProjectFileService(IPluginLog log, string pluginConfigDir, LibraryService? library, PenumbraBridge? penumbra = null, IDataManager? dataManager = null)
    {
        this.log = log;
        this.library = library;
        this.penumbra = penumbra;
        this.dataManager = dataManager;
        this.pluginConfigDir = Path.GetFullPath(pluginConfigDir);
        ProjectsRoot = Path.Combine(this.pluginConfigDir, "Projects");
        Directory.CreateDirectory(ProjectsRoot);
    }

    public bool RenameProject(string projectPath, string newName, out string? renamedProjectPath)
    {
        renamedProjectPath = null;
        try
        {
            var fullProjectPath = Path.GetFullPath(projectPath);
            var package = ReadPackage(fullProjectPath);
            var oldDir = Path.GetDirectoryName(fullProjectPath);
            if (string.IsNullOrWhiteSpace(oldDir) || !Directory.Exists(oldDir))
                return false;

            var safeName = SanitizeProjectName(newName);
            if (string.IsNullOrWhiteSpace(safeName))
                return false;

            var oldName = Path.GetFileName(oldDir);
            var targetPath = oldName.Equals(safeName, StringComparison.OrdinalIgnoreCase)
                ? fullProjectPath
                : GetUniqueProjectPath(safeName);
            var newDir = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(newDir))
                return false;

            if (!oldDir.Equals(newDir, StringComparison.OrdinalIgnoreCase))
                Directory.Move(oldDir, newDir);

            var newProjectPath = Path.Combine(newDir, ProjectFileName);
            if (package != null)
            {
                package.Metadata.Name = safeName;
                package.Metadata.ModifiedUtc = DateTime.UtcNow;
                var json = JsonSerializer.Serialize(package, jsonOptions);
                File.WriteAllText(newProjectPath, json);
            }

            renamedProjectPath = newProjectPath;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Project] Rename failed for {Path}", projectPath);
            return false;
        }
    }

    public bool DeleteProject(string projectPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return false;

            var fullProjectPath = Path.GetFullPath(projectPath);
            var projectDir = Path.GetDirectoryName(fullProjectPath);
            if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
                return false;

            var fullProjectsRoot = Path.GetFullPath(ProjectsRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullProjectDir = Path.GetFullPath(projectDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!fullProjectDir.StartsWith(fullProjectsRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            Directory.Delete(projectDir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Project] Delete failed for {Path}", projectPath);
            return false;
        }
    }

    public bool ImportProject(string sourceProjectJsonPath, out string? importedProjectPath)
    {
        importedProjectPath = null;

        if (string.IsNullOrWhiteSpace(sourceProjectJsonPath) || !File.Exists(sourceProjectJsonPath))
            return false;

        try
        {
            var sourceFullPath = Path.GetFullPath(sourceProjectJsonPath);
            var package = ReadPackage(sourceFullPath);
            var preferredName = package?.Metadata?.Name;
            if (string.IsNullOrWhiteSpace(preferredName))
                preferredName = Path.GetFileNameWithoutExtension(sourceProjectJsonPath);

            var destinationProjectPath = GetUniqueProjectPath(preferredName!);
            var destinationDir = Path.GetDirectoryName(destinationProjectPath)!;
            Directory.CreateDirectory(destinationDir);

            File.Copy(sourceFullPath, destinationProjectPath, overwrite: false);
            if (package != null)
            {
                // Expand any relative paths from the source file to absolute before re-relativizing
                var sourceDir = Path.GetDirectoryName(sourceFullPath)!;
                foreach (var group in package.Snapshot.TargetGroups)
                    ResolveGroupPathsAbsolute(group, sourceDir);

                var layerMap = ExtractImages(destinationProjectPath, package.Images);
                RewriteSnapshotPaths(package.Snapshot, layerMap);

                // Re-relativize paths against the destination directory
                foreach (var group in package.Snapshot.TargetGroups)
                    MakeGroupPathsRelative(group, destinationDir);

                var updatedJson = JsonSerializer.Serialize(package, jsonOptions);
                File.WriteAllText(destinationProjectPath, updatedJson);
            }

            importedProjectPath = Path.Combine(destinationDir, ProjectFileName);
            return File.Exists(importedProjectPath);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Project] Import failed for {Path}", sourceProjectJsonPath);
            return false;
        }
    }

    public sealed class ProjectListItem
    {
        public string Name { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
        public int GroupCount { get; set; }
        public int LayerCount { get; set; }
        public DateTime LastModifiedUtc { get; set; }
    }

    public sealed class LoadResult
    {
        public SavedProjectSnapshot Snapshot { get; set; } = new();
        public string ProjectName { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
    }

    private sealed class ProjectPackage
    {
        public int FormatVersion { get; set; } = 1;
        public ProjectMetadata Metadata { get; set; } = new();
        public SavedProjectSnapshot Snapshot { get; set; } = new();
        public List<ProjectImageEntry> Images { get; set; } = [];
    }

    private sealed class ProjectMetadata
    {
        public string Name { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class ProjectImageEntry
    {
        public string LayerKey { get; set; } = string.Empty;
        public string OriginalName { get; set; } = string.Empty;
        public string FolderPath { get; set; } = string.Empty;
        public string BlobFileName { get; set; } = string.Empty;
        public string LogicalPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public string Base64Data { get; set; } = string.Empty;
    }

    public string BuildProjectPath(string projectName)
    {
        var safeName = SanitizeProjectName(projectName);
        var projectDir = Path.Combine(ProjectsRoot, safeName);
        return Path.Combine(projectDir, ProjectFileName);
    }

    public string GetUniqueProjectPath(string preferredName)
    {
        var safeName = SanitizeProjectName(preferredName);
        var candidate = BuildProjectPath(safeName);
        if (!File.Exists(candidate))
            return candidate;

        var n = 2;
        while (true)
        {
            var next = BuildProjectPath($"{safeName} ({n})");
            if (!File.Exists(next))
                return next;
            n++;
        }
    }

    public IReadOnlyList<ProjectListItem> ListProjects()
    {
        if (!Directory.Exists(ProjectsRoot))
            return [];

        var files = Directory.EnumerateFiles(ProjectsRoot, ProjectFileName, SearchOption.AllDirectories);
        var items = new List<ProjectListItem>();

        foreach (var file in files)
        {
            try
            {
                var package = ReadPackage(file);
                if (package == null)
                    continue;

                int layers = 0;
                foreach (var g in package.Snapshot.TargetGroups)
                    layers += g.Layers.Count;

                items.Add(new ProjectListItem
                {
                    Name = string.IsNullOrWhiteSpace(package.Metadata.Name)
                        ? Path.GetFileName(Path.GetDirectoryName(file) ?? file)
                        : package.Metadata.Name,
                    ProjectPath = file,
                    GroupCount = package.Snapshot.TargetGroups.Count,
                    LayerCount = layers,
                    LastModifiedUtc = File.GetLastWriteTimeUtc(file),
                });
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Project] Failed listing file {Path}", file);
            }
        }

        return items
            .OrderByDescending(i => i.LastModifiedUtc)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool SaveProject(string projectPath, string projectName, SavedProjectSnapshot snapshot, bool includeImages = true)
    {
        try
        {
            var fullProjectPath = Path.GetFullPath(projectPath);
            var projectDir = Path.GetDirectoryName(fullProjectPath);
            if (string.IsNullOrEmpty(projectDir))
                return false;

            Directory.CreateDirectory(projectDir);

            var package = BuildPackage(fullProjectPath, projectName, snapshot, includeImages);
            var json = JsonSerializer.Serialize(package, jsonOptions);
            File.WriteAllText(fullProjectPath, json);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Project] Save failed for {Path}", projectPath);
            return false;
        }
    }

    public LoadResult? LoadProject(string projectPath, bool importImagesToLibrary = true)
    {
        try
        {
            var fullProjectPath = Path.GetFullPath(projectPath);
            var package = ReadPackage(fullProjectPath);
            if (package == null)
                return null;

            var projectDir = Path.GetDirectoryName(fullProjectPath)!;
            foreach (var group in package.Snapshot.TargetGroups)
                ResolveGroupPathsAbsolute(group, projectDir);

            if (importImagesToLibrary)
            {
                var logicalToDisk = ExtractImages(fullProjectPath, package.Images);
                RewriteSnapshotPaths(package.Snapshot, logicalToDisk);
            }

            return new LoadResult
            {
                Snapshot = package.Snapshot,
                ProjectName = package.Metadata.Name,
                ProjectPath = fullProjectPath,
            };
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Project] Load failed for {Path}", projectPath);
            return null;
        }
    }

    private ProjectPackage BuildPackage(string projectPath, string projectName, SavedProjectSnapshot source, bool includeImages)
    {
        var snapshot = DeepClone(source);
        var existing = ReadPackage(projectPath);
        var createdUtc = existing?.Metadata.CreatedUtc ?? DateTime.UtcNow;
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        foreach (var group in snapshot.TargetGroups)
            MakeGroupPathsRelative(group, projectDir);
        if (!includeImages)
        {
            return new ProjectPackage
            {
                Metadata = new ProjectMetadata
                {
                    Name = string.IsNullOrWhiteSpace(projectName)
                        ? Path.GetFileName(Path.GetDirectoryName(projectPath) ?? "Project")
                        : projectName,
                    CreatedUtc = createdUtc,
                    ModifiedUtc = DateTime.UtcNow,
                },
                Snapshot = snapshot,
                Images = [],
            };
        }

        var images = new List<ProjectImageEntry>();
        var existingByLayerKey = new Dictionary<string, ProjectImageEntry>(StringComparer.OrdinalIgnoreCase);
        if (existing != null)
        {
            foreach (var image in existing.Images)
            {
                if (!string.IsNullOrWhiteSpace(image.LayerKey))
                    existingByLayerKey[image.LayerKey] = image;
            }
        }

        for (var gi = 0; gi < snapshot.TargetGroups.Count; gi++)
        {
            var group = snapshot.TargetGroups[gi];
            for (var li = 0; li < group.Layers.Count; li++)
            {
                var layer = group.Layers[li];
                var layerKey = BuildLayerKey(gi, li);

                SkinTattoo.Core.LibraryEntry? libraryEntry = null;
                if (library != null)
                {
                    if (!string.IsNullOrWhiteSpace(layer.ImageHash))
                        libraryEntry = library.Get(layer.ImageHash);

                    if (libraryEntry == null && !string.IsNullOrWhiteSpace(layer.ImagePath))
                    {
                        var imagePathFile = Path.GetFileNameWithoutExtension(layer.ImagePath);
                        if (!string.IsNullOrWhiteSpace(imagePathFile))
                            libraryEntry = library.Get(imagePathFile);
                    }
                }

                if (string.IsNullOrWhiteSpace(layer.ImagePath) && string.IsNullOrWhiteSpace(layer.ImageHash))
                    continue;

                if (!string.IsNullOrWhiteSpace(layer.ImageHash)
                    && existingByLayerKey.TryGetValue(layerKey, out var existingEntry)
                    && existingEntry.Hash.Equals(layer.ImageHash, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(existingEntry.Base64Data))
                {
                    existingEntry.LayerKey = layerKey;
                    images.Add(existingEntry);
                    layer.ImagePath = layer.ImageHash;
                    continue;
                }

                var sourcePath = ResolveSourceImagePath(layer.ImagePath, layer.ImageHash);
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    continue;

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(sourcePath);
                }
                catch
                {
                    continue;
                }

                var hash = ComputeSha256(bytes);
                var ext = Path.GetExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".png";

                if (libraryEntry != null && !string.IsNullOrWhiteSpace(libraryEntry.FileName))
                {
                    var libraryExt = Path.GetExtension(libraryEntry.FileName);
                    if (!string.IsNullOrWhiteSpace(libraryExt))
                        ext = libraryExt;
                }

                if (existingByLayerKey.TryGetValue(layerKey, out existingEntry)
                    && existingEntry.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(existingEntry.Base64Data))
                {
                    existingEntry.LayerKey = layerKey;
                    images.Add(existingEntry);
                    layer.ImageHash = hash;
                    layer.ImagePath = hash;
                    continue;
                }

                var entry = new ProjectImageEntry
                {
                    LayerKey = layerKey,
                    Hash = hash,
                    BlobFileName = hash + ext.ToLowerInvariant(),
                    FileName = Path.GetFileName(sourcePath),
                    OriginalName = !string.IsNullOrWhiteSpace(libraryEntry?.OriginalName)
                        ? libraryEntry.OriginalName
                        : Path.GetFileName(sourcePath),
                    FolderPath = libraryEntry?.FolderPath ?? string.Empty,
                    FileType = ext.TrimStart('.').ToLowerInvariant(),
                    Base64Data = Convert.ToBase64String(bytes),
                };

                if (!string.IsNullOrWhiteSpace(libraryEntry?.FileName))
                    entry.FileName = libraryEntry.FileName;
                if (!string.IsNullOrWhiteSpace(libraryEntry?.FileName))
                    entry.BlobFileName = libraryEntry.FileName;

                images.Add(entry);

                layer.ImageHash = hash;
                layer.ImagePath = hash;
            }
        }

        return new ProjectPackage
        {
            Metadata = new ProjectMetadata
            {
                Name = string.IsNullOrWhiteSpace(projectName)
                    ? Path.GetFileName(Path.GetDirectoryName(projectPath) ?? "Project")
                    : projectName,
                CreatedUtc = createdUtc,
                ModifiedUtc = DateTime.UtcNow,
            },
            Snapshot = snapshot,
            Images = images,
        };
    }

    private Dictionary<string, string> ExtractImages(string projectPath, List<ProjectImageEntry> images)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (library == null)
            return result;

        foreach (var image in images)
        {
            if (string.IsNullOrWhiteSpace(image.Base64Data))
                continue;

            try
            {
                var bytes = Convert.FromBase64String(image.Base64Data);
                var hash = image.Hash;
                if (string.IsNullOrWhiteSpace(hash))
                    hash = ComputeSha256(bytes);

                var blobName = !string.IsNullOrWhiteSpace(image.BlobFileName)
                    ? image.BlobFileName
                    : !string.IsNullOrWhiteSpace(image.FileName)
                        ? image.FileName
                        : hash + ".png";

                var originalName = !string.IsNullOrWhiteSpace(image.OriginalName)
                    ? image.OriginalName
                    : image.FileName;

                var entry = library.ImportProjectPayload(hash, blobName, originalName, image.FolderPath, bytes);
                if (entry == null)
                    continue;

                var resolved = library.ResolveDiskPath(entry.Hash);
                if (string.IsNullOrWhiteSpace(resolved))
                    continue;

                if (!string.IsNullOrWhiteSpace(image.LayerKey))
                    result[image.LayerKey] = resolved;
                result[hash] = resolved;
                if (!string.IsNullOrWhiteSpace(image.LogicalPath))
                    result[image.LogicalPath] = resolved;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Project] Failed extracting image payload {Hash}", image.Hash);
            }
        }

        return result;
    }

    private static void RewriteSnapshotPaths(SavedProjectSnapshot snapshot, Dictionary<string, string> logicalToDisk)
    {
        for (var gi = 0; gi < snapshot.TargetGroups.Count; gi++)
        {
            var group = snapshot.TargetGroups[gi];
            for (var li = 0; li < group.Layers.Count; li++)
            {
                var layer = group.Layers[li];
                if (string.IsNullOrWhiteSpace(layer.ImagePath))
                    continue;

                var key = BuildLayerKey(gi, li);
                if (logicalToDisk.TryGetValue(key, out var diskPath)
                    || (!string.IsNullOrWhiteSpace(layer.ImageHash) && logicalToDisk.TryGetValue(layer.ImageHash, out diskPath))
                    || logicalToDisk.TryGetValue(layer.ImagePath, out diskPath))
                {
                    layer.ImagePath = diskPath;
                    if (!string.IsNullOrWhiteSpace(layer.ImageHash))
                        continue;
                    if (logicalToDisk.TryGetValue(key, out _))
                    {
                        var fileName = Path.GetFileNameWithoutExtension(diskPath);
                        if (!string.IsNullOrWhiteSpace(fileName))
                            layer.ImageHash = fileName;
                    }
                }
            }
        }
    }

    private ProjectPackage? ReadPackage(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProjectPackage>(json, jsonOptions);
    }

    private static SavedProjectSnapshot DeepClone(SavedProjectSnapshot source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<SavedProjectSnapshot>(json) ?? new SavedProjectSnapshot();
    }

    private static string BuildLayerKey(int groupIndex, int layerIndex) => $"{groupIndex}:{layerIndex}";

    private static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private string? ResolveSourceImagePath(string? imagePath, string? imageHash)
    {
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            if (File.Exists(imagePath))
                return imagePath;

            var pluginRelative = FromPluginRelative(imagePath);
            if (!string.IsNullOrWhiteSpace(pluginRelative) && File.Exists(pluginRelative))
                return pluginRelative;
        }

        if (!string.IsNullOrWhiteSpace(imageHash) && library != null)
        {
            var resolved = library.ResolveDiskPath(imageHash);
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
                return resolved;
        }

        return null;
    }

    private string ToPluginRelative(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rel = Path.GetRelativePath(pluginConfigDir, fullPath).Replace('\\', '/');
        return rel;
    }

    private string? FromPluginRelative(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        var rel = relativePath.Replace('/', Path.DirectorySeparatorChar)
                              .Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(pluginConfigDir, rel));
        return full.StartsWith(pluginConfigDir, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private string? MakePathRelative(string? path, string baseDir)
    {
        if (string.IsNullOrEmpty(path)) return path;

        // Only transform absolute filesystem paths; leave relative/game-format paths as-is
        if (!Path.IsPathRooted(path)) return path;

        // Try standard relative path first (works when on the same drive)
        var rel = Path.GetRelativePath(baseDir, path);

        // If the result is still rooted (cross-drive on Windows), fall back to
        // a known-root token so the path is portable.
        if (Path.IsPathRooted(rel))
        {
            var fullPath = Path.GetFullPath(path);

            // Check Penumbra mod directory first
            var modDir = penumbra?.GetModDirectory();
            if (!string.IsNullOrEmpty(modDir))
            {
                var fullModDir = Path.GetFullPath(modDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(fullModDir, StringComparison.OrdinalIgnoreCase))
                    return PenumbraModDirToken + "/" + Path.GetRelativePath(fullModDir.TrimEnd(Path.DirectorySeparatorChar), fullPath).Replace('\\', '/');
            }

            // Check FFXIV game directory (parent of sqpack, i.e. the "game" folder)
            var gameDir = GetGameDir();
            if (!string.IsNullOrEmpty(gameDir))
            {
                var fullGameDir = Path.GetFullPath(gameDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(fullGameDir, StringComparison.OrdinalIgnoreCase))
                    return GameDirToken + "/" + Path.GetRelativePath(fullGameDir.TrimEnd(Path.DirectorySeparatorChar), fullPath).Replace('\\', '/');
            }
        }

        return rel;
    }

    private string? ResolvePathAbsolute(string? path, string baseDir)
    {
        if (string.IsNullOrEmpty(path)) return path;

        // Resolve Penumbra mod directory token
        if (path.StartsWith(PenumbraModDirToken, StringComparison.OrdinalIgnoreCase))
        {
            var modDir = penumbra?.GetModDirectory();
            if (!string.IsNullOrEmpty(modDir))
            {
                var rest = path[PenumbraModDirToken.Length..].TrimStart('/', '\\');
                return Path.GetFullPath(Path.Combine(modDir, rest));
            }
            // Penumbra unavailable — leave token in place so it can be resolved later
            return path;
        }

        // Resolve game directory token
        if (path.StartsWith(GameDirToken, StringComparison.OrdinalIgnoreCase))
        {
            var gameDir = GetGameDir();
            if (!string.IsNullOrEmpty(gameDir))
            {
                var rest = path[GameDirToken.Length..].TrimStart('/', '\\');
                return Path.GetFullPath(Path.Combine(gameDir, rest));
            }
            // Game dir unavailable — leave token in place so it can be resolved later
            return path;
        }

        if (Path.IsPathRooted(path)) return path;
        // Only expand genuine filesystem-relative paths (start with . or path separator).
        // Bare game-format paths like "chara/human/..." are not filesystem paths and
        // must not be combined with baseDir.
        if (path[0] != '.' && path[0] != Path.DirectorySeparatorChar && path[0] != Path.AltDirectorySeparatorChar)
            return path;
        return Path.GetFullPath(Path.Combine(baseDir, path));
    }

    private string? GetGameDir()
    {
        // DataPath points to the "sqpack" folder; game files live in its parent ("game" folder)
        var sqpackDir = dataManager?.GameData?.DataPath?.FullName;
        if (string.IsNullOrEmpty(sqpackDir)) return null;
        return Path.GetDirectoryName(sqpackDir);
    }

    private void MakeGroupPathsRelative(SavedTargetGroup group, string projectDir)
    {
        group.DiffuseDiskPath = MakePathRelative(group.DiffuseDiskPath, projectDir);
        group.NormDiskPath = MakePathRelative(group.NormDiskPath, projectDir);
        group.MtrlDiskPath = MakePathRelative(group.MtrlDiskPath, projectDir);
        group.MeshDiskPath = MakePathRelative(group.MeshDiskPath, projectDir);
        group.OrigDiffuseDiskPath = MakePathRelative(group.OrigDiffuseDiskPath, projectDir);
        group.OrigNormDiskPath = MakePathRelative(group.OrigNormDiskPath, projectDir);
        group.OrigMtrlDiskPath = MakePathRelative(group.OrigMtrlDiskPath, projectDir);
        for (var i = 0; i < group.MeshDiskPaths.Count; i++)
            group.MeshDiskPaths[i] = MakePathRelative(group.MeshDiskPaths[i], projectDir) ?? group.MeshDiskPaths[i];
    }

    private void ResolveGroupPathsAbsolute(SavedTargetGroup group, string projectDir)
    {
        group.DiffuseDiskPath = ResolvePathAbsolute(group.DiffuseDiskPath, projectDir);
        group.NormDiskPath = ResolvePathAbsolute(group.NormDiskPath, projectDir);
        group.MtrlDiskPath = ResolvePathAbsolute(group.MtrlDiskPath, projectDir);
        group.MeshDiskPath = ResolvePathAbsolute(group.MeshDiskPath, projectDir);
        group.OrigDiffuseDiskPath = ResolvePathAbsolute(group.OrigDiffuseDiskPath, projectDir);
        group.OrigNormDiskPath = ResolvePathAbsolute(group.OrigNormDiskPath, projectDir);
        group.OrigMtrlDiskPath = ResolvePathAbsolute(group.OrigMtrlDiskPath, projectDir);
        for (var i = 0; i < group.MeshDiskPaths.Count; i++)
            group.MeshDiskPaths[i] = ResolvePathAbsolute(group.MeshDiskPaths[i], projectDir) ?? group.MeshDiskPaths[i];
    }

    private static string SanitizeProjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Untitled";

        var chars = value.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0)
                chars[i] = '_';
        }

        var cleaned = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var destination = Path.Combine(destDir, fileName);
            File.Copy(file, destination, overwrite: false);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);
            CopyDirectory(dir, Path.Combine(destDir, name));
        }
    }
}
