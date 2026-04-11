using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Meddle.Utils.Export;
using Meddle.Utils.Files;
using Meddle.Utils.Files.SqPack;
using Meddle.Utils.Helpers;
using SkinTattoo.Http;
using MeddleModel = Meddle.Utils.Export.Model;

namespace SkinTattoo.Mesh;

public class MeshExtractor : IDisposable
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private SqPack? sqPack;
    private Lumina.GameData? luminaForDisk;

    public MeshExtractor(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public void Dispose()
    {
        sqPack?.Dispose();
        sqPack = null;
        luminaForDisk?.Dispose();
        luminaForDisk = null;
    }

    /// <summary>Normalize backslashes to forward slashes for SqPack/IDataManager compatibility.</summary>
    private static string NormalizeGamePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (System.IO.Path.IsPathRooted(path)) return path;
        return path.Replace('\\', '/').TrimStart('/');
    }

    public Lumina.GameData? GetLuminaForDisk()
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

    public SqPack? GetSqPackInstance()
    {
        sqPack ??= CreateSqPack();
        return sqPack;
    }

    private SqPack CreateSqPack()
    {
        if (sqPack == null)
        {
            var sqpackPath = dataManager.GameData.DataPath.FullName;
            var gamePath = System.IO.Path.GetDirectoryName(sqpackPath)!;
            sqPack = new SqPack(gamePath);
        }
        return sqPack;
    }

    public MeshData? ExtractMesh(string gamePath, int[]? allowedMatIdx = null, string? diskPath = null)
    {
        var normalizedGamePath = NormalizeGamePath(gamePath);

        byte[]? mdlBytes = null;

        if (!string.IsNullOrEmpty(diskPath) && System.IO.Path.IsPathRooted(diskPath) && System.IO.File.Exists(diskPath))
        {
            try
            {
                mdlBytes = System.IO.File.ReadAllBytes(diskPath);
            }
            catch (Exception ex)
            {
                DebugServer.AppendLog($"[MeshExtractor] Disk-path read exception: {ex.Message}");
            }
        }

        gamePath = normalizedGamePath;

        if (mdlBytes == null)
        {
            try
            {
                var pack = GetSqPackInstance();
                var result = pack?.GetFile(gamePath);
                if (result != null)
                {
                    mdlBytes = result.Value.file.RawData.ToArray();
                }
            }
            catch { }
        }

        if (mdlBytes == null && System.IO.File.Exists(gamePath))
        {
            try
            {
                mdlBytes = System.IO.File.ReadAllBytes(gamePath);
            }
            catch (Exception ex)
            {
                DebugServer.AppendLog($"[MeshExtractor] Local file read exception: {ex.Message}");
            }
        }

        if (mdlBytes == null)
        {
            try
            {
                var raw = dataManager.GetFile(gamePath);
                if (raw != null)
                {
                    mdlBytes = raw.Data;
                }
            }
            catch (Exception ex)
            {
                DebugServer.AppendLog($"[MeshExtractor] IDataManager fallback exception: {ex.Message}");
            }
        }

        if (mdlBytes == null)
        {
            DebugServer.AppendLog($"[MeshExtractor] Failed to load: {gamePath}");
            return null;
        }

        try
        {
            var mdlFile = new MdlFile(mdlBytes);
            var model = new MeddleModel(gamePath, mdlFile, null);
            int mainMeshCount = mdlFile.Lods.Length > 0 ? mdlFile.Lods[0].MeshCount : model.Meshes.Count;
            var meshData = ConvertMeddleModel(model, mainMeshCount, allowedMatIdx);
            return meshData;
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[MeshExtractor] Parse error: {ex.GetType().Name}: {ex.Message}");
            log.Error(ex, "Failed to parse model: {0}", gamePath);
            return null;
        }
    }

    /// <summary>Read the MaterialFileNames table from a .mdl file, indexed by matIdx.</summary>
    public string[]? ReadMaterialFileNames(string gamePath, string? diskPath = null)
    {
        gamePath = NormalizeGamePath(gamePath);
        byte[]? mdlBytes = null;

        if (!string.IsNullOrEmpty(diskPath) && System.IO.Path.IsPathRooted(diskPath) && System.IO.File.Exists(diskPath))
        {
            try { mdlBytes = System.IO.File.ReadAllBytes(diskPath); } catch { }
        }

        if (mdlBytes == null)
        {
            try
            {
                var pack = GetSqPackInstance();
                var result = pack?.GetFile(gamePath);
                if (result != null) mdlBytes = result.Value.file.RawData.ToArray();
            }
            catch { }
        }

        if (mdlBytes == null)
        {
            try
            {
                var raw = dataManager.GetFile(gamePath);
                if (raw != null) mdlBytes = raw.Data;
            }
            catch { }
        }

        if (mdlBytes == null) return null;

        try
        {
            var mdlFile = new MdlFile(mdlBytes);
            var strings = mdlFile.GetStrings();
            var result = new string[mdlFile.MaterialNameOffsets.Length];
            for (var i = 0; i < mdlFile.MaterialNameOffsets.Length; i++)
            {
                var offset = (int)mdlFile.MaterialNameOffsets[i];
                result[i] = strings.TryGetValue(offset, out var s) ? s : "";
            }
            return result;
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[MeshExtractor] ReadMaterialFileNames parse error: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Load and merge multiple MeshSlots with per-slot matIdx filter.</summary>
    public MeshData? ExtractAndMergeSlots(List<Core.MeshSlot> slots)
    {
        if (slots.Count == 0) return null;

        var allVertices = new List<MeshVertex>();
        var allIndices = new List<int>();

        foreach (var slot in slots)
        {
            var matIdx = slot.MatIdx.Length > 0 ? slot.MatIdx : null;
            var mesh = ExtractMesh(slot.GamePath, matIdx, slot.DiskPath);
            if (mesh == null)
            {
                DebugServer.AppendLog($"[MeshExtractor] slot {slot.GamePath} returned null, skipping");
                continue;
            }

            var baseVertex = allVertices.Count;
            allVertices.AddRange(mesh.Vertices);
            foreach (var idx in mesh.Indices)
                allIndices.Add(idx + baseVertex);
        }

        if (allVertices.Count == 0) return null;

        var merged = new MeshData
        {
            Vertices = allVertices.ToArray(),
            Indices = allIndices.ToArray(),
        };

        return merged;
    }

    public MeshData? ExtractAndMerge(List<string> paths)
    {
        if (paths.Count == 0) return null;
        if (paths.Count == 1) return ExtractMesh(paths[0]);

        var allVertices = new List<MeshVertex>();
        var allIndices = new List<int>();

        foreach (var path in paths)
        {
            var mesh = ExtractMesh(path);
            if (mesh == null) continue;

            var baseVertex = allVertices.Count;
            allVertices.AddRange(mesh.Vertices);
            foreach (var idx in mesh.Indices)
                allIndices.Add(idx + baseVertex);
        }

        if (allVertices.Count == 0) return null;

        var merged = new MeshData
        {
            Vertices = allVertices.ToArray(),
            Indices = allIndices.ToArray(),
        };
        return merged;
    }

    private static MeshData ConvertMeddleModel(MeddleModel model, int maxMeshes = int.MaxValue, int[]? allowedMatIdx = null)
    {
        var allVertices = new List<MeshVertex>();
        var allIndices = new List<int>();

        var allowedSet = allowedMatIdx != null ? new HashSet<int>(allowedMatIdx) : null;

        var meshCount = Math.Min(model.Meshes.Count, maxMeshes);
        for (int mi = 0; mi < meshCount; mi++)
        {
            var mesh = model.Meshes[mi];

            var keep = allowedSet != null
                ? allowedSet.Contains(mesh.MaterialIdx)
                : mesh.MaterialIdx == 0;

            if (!keep)
            {
                continue;
            }
            var baseVertex = allVertices.Count;

            foreach (var v in mesh.Vertices)
            {
                var pos = v.Position ?? Vector3.Zero;
                var normal = (v.Normals != null && v.Normals.Length > 0) ? v.Normals[0] : Vector3.UnitY;
                var rawUv = (v.TexCoords != null && v.TexCoords.Length > 0) ? v.TexCoords[0] : Vector2.Zero;
                var uv = rawUv;

                allVertices.Add(new MeshVertex
                {
                    Position = pos,
                    Normal = normal,
                    UV = uv,
                });
            }

            foreach (var idx in mesh.Indices)
            {
                allIndices.Add(idx + baseVertex);
            }
        }

        return new MeshData
        {
            Vertices = allVertices.ToArray(),
            Indices = allIndices.ToArray(),
        };
    }
}
