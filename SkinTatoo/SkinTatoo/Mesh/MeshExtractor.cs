using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Meddle.Utils.Export;
using Meddle.Utils.Files;
using Meddle.Utils.Files.SqPack;
using Meddle.Utils.Helpers;
using SkinTatoo.Http;
using MeddleModel = Meddle.Utils.Export.Model;

namespace SkinTatoo.Mesh;

public class MeshExtractor
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

    /// <summary>
    /// Normalize a path so it works as a SqPack / IDataManager game path.
    /// Penumbra returns ResourceNode.ActualPath as a Windows-style relative
    /// path with backslashes for unmodded resources, which our load APIs
    /// reject. Convert to forward slashes and trim any leading separators.
    /// Absolute Windows paths (with a drive letter) are returned unchanged so
    /// callers can still pass them as a "diskPath" hint.
    /// </summary>
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
            DebugServer.AppendLog($"[MeshExtractor] Initializing Meddle SqPack at: {gamePath}");
            sqPack = new SqPack(gamePath);
        }
        return sqPack;
    }

    public MeshData? ExtractMesh(string gamePath, int[]? allowedMatIdx = null, string? diskPath = null)
    {
        // Penumbra ResourceNode.ActualPath uses backslashes for unmodded
        // resources (e.g. chara\human\c1401\obj\body\...). Legacy callers
        // pass that as gamePath, which fails every load strategy because
        // SqPack and IDataManager both expect forward-slash game paths.
        // Normalize at entry so both shapes work.
        var normalizedGamePath = NormalizeGamePath(gamePath);

        DebugServer.AppendLog($"[MeshExtractor] Loading: {normalizedGamePath}"
            + (allowedMatIdx != null ? $" matIdx=[{string.Join(",", allowedMatIdx)}]" : "")
            + (!string.IsNullOrEmpty(diskPath) && diskPath != normalizedGamePath ? $" disk={diskPath}" : ""));

        byte[]? mdlBytes = null;

        // If a Penumbra-resolved disk path is provided AND it's a real
        // absolute file (mod redirect), prefer it.
        if (!string.IsNullOrEmpty(diskPath) && System.IO.Path.IsPathRooted(diskPath) && System.IO.File.Exists(diskPath))
        {
            try
            {
                mdlBytes = System.IO.File.ReadAllBytes(diskPath);
                DebugServer.AppendLog($"[MeshExtractor] Loaded from supplied disk path: {mdlBytes.Length} bytes");
            }
            catch (Exception ex)
            {
                DebugServer.AppendLog($"[MeshExtractor] Disk-path read exception: {ex.Message}");
            }
        }

        gamePath = normalizedGamePath;

        // Use Meddle's SqPack reader
        if (mdlBytes == null)
        {
            try
            {
                var pack = GetSqPackInstance();
                var result = pack?.GetFile(gamePath);
                if (result != null)
                {
                    mdlBytes = result.Value.file.RawData.ToArray();
                    DebugServer.AppendLog($"[MeshExtractor] Loaded via Meddle SqPack: {mdlBytes.Length} bytes");
                }
                else
                {
                    DebugServer.AppendLog($"[MeshExtractor] Meddle SqPack: file not found");
                }
            }
            catch (Exception ex)
            {
                DebugServer.AppendLog($"[MeshExtractor] Meddle SqPack exception: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Fallback: try reading as local file path (for Penumbra mod redirects)
        if (mdlBytes == null && System.IO.File.Exists(gamePath))
        {
            try
            {
                mdlBytes = System.IO.File.ReadAllBytes(gamePath);
                DebugServer.AppendLog($"[MeshExtractor] Loaded from local file: {mdlBytes.Length} bytes");
            }
            catch (Exception ex)
            {
                DebugServer.AppendLog($"[MeshExtractor] Local file read exception: {ex.Message}");
            }
        }

        // Fallback: Dalamud IDataManager
        if (mdlBytes == null)
        {
            try
            {
                var raw = dataManager.GetFile(gamePath);
                if (raw != null)
                {
                    mdlBytes = raw.Data;
                    DebugServer.AppendLog($"[MeshExtractor] Loaded via IDataManager: {mdlBytes.Length} bytes");
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
            // Only include LOD0 main meshes, skip shadow/water/fog/crest meshes
            int mainMeshCount = mdlFile.Lods.Length > 0 ? mdlFile.Lods[0].MeshCount : model.Meshes.Count;
            var meshData = ConvertMeddleModel(model, mainMeshCount, allowedMatIdx);
            DebugServer.AppendLog($"[MeshExtractor] Done: {meshData.Vertices.Length} verts, {meshData.TriangleCount} tris (mainMeshCount={mainMeshCount}, totalInModel={model.Meshes.Count})");
            return meshData;
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[MeshExtractor] Parse error: {ex.GetType().Name}: {ex.Message}");
            log.Error(ex, "Failed to parse model: {0}", gamePath);
            return null;
        }
    }

    /// <summary>
    /// Read the MaterialFileNames table out of a .mdl file. The returned array
    /// is indexed by matIdx — i.e. result[mesh.MaterialIdx] gives the material
    /// filename string the mdl wants to use for that mesh group.
    ///
    /// Note: these are the ORIGINAL filenames as written in the mdl file. They
    /// may be subject to runtime substitution by the engine (see
    /// docs/皮肤UV网格匹配调研.md §2.7), so callers matching against a Penumbra
    /// resource tree mtrl path should normalize before comparing.
    /// </summary>
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

    /// <summary>
    /// Load every MeshSlot, applying its per-slot matIdx filter, and merge
    /// them all into a single MeshData. Used by SkinMeshResolver-driven
    /// flows where body skin can map to multiple equipment mdls.
    /// </summary>
    public MeshData? ExtractAndMergeSlots(List<Core.MeshSlot> slots)
    {
        if (slots.Count == 0) return null;

        var allVertices = new List<MeshVertex>();
        var allIndices = new List<ushort>();

        foreach (var slot in slots)
        {
            var matIdx = slot.MatIdx.Length > 0 ? slot.MatIdx : null;
            var mesh = ExtractMesh(slot.GamePath, matIdx, slot.DiskPath);
            if (mesh == null)
            {
                DebugServer.AppendLog($"[MeshExtractor] slot {slot.GamePath} returned null, skipping");
                continue;
            }

            // Per-slot UV bounds — helps diagnose tile / channel mismatches.
            if (mesh.Vertices.Length > 0)
            {
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                foreach (var v in mesh.Vertices)
                {
                    if (v.UV.X < minX) minX = v.UV.X;
                    if (v.UV.Y < minY) minY = v.UV.Y;
                    if (v.UV.X > maxX) maxX = v.UV.X;
                    if (v.UV.Y > maxY) maxY = v.UV.Y;
                }
                DebugServer.AppendLog(
                    $"[MeshExtractor]   slot UV bounds: X=[{minX:F3},{maxX:F3}] Y=[{minY:F3},{maxY:F3}]  ({System.IO.Path.GetFileName(slot.GamePath)})");
            }

            var baseVertex = (ushort)allVertices.Count;
            allVertices.AddRange(mesh.Vertices);
            foreach (var idx in mesh.Indices)
                allIndices.Add((ushort)(idx + baseVertex));
        }

        if (allVertices.Count == 0) return null;

        var merged = new MeshData
        {
            Vertices = allVertices.ToArray(),
            Indices = allIndices.ToArray(),
        };

        // Merged UV bounds — if X or Y span > 1 unit, the canvas's
        // single-tile uvBase normalization is going to drop part of the mesh.
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (var v in merged.Vertices)
            {
                if (v.UV.X < minX) minX = v.UV.X;
                if (v.UV.Y < minY) minY = v.UV.Y;
                if (v.UV.X > maxX) maxX = v.UV.X;
                if (v.UV.Y > maxY) maxY = v.UV.Y;
            }
            DebugServer.AppendLog(
                $"[MeshExtractor] Merged {slots.Count} slot(s): {merged.Vertices.Length} verts, {merged.TriangleCount} tris, "
                + $"UV X=[{minX:F3},{maxX:F3}] Y=[{minY:F3},{maxY:F3}]");
        }

        return merged;
    }

    public MeshData? ExtractAndMerge(List<string> paths)
    {
        if (paths.Count == 0) return null;
        if (paths.Count == 1) return ExtractMesh(paths[0]);

        var allVertices = new List<MeshVertex>();
        var allIndices = new List<ushort>();

        foreach (var path in paths)
        {
            var mesh = ExtractMesh(path);
            if (mesh == null) continue;

            var baseVertex = (ushort)allVertices.Count;
            allVertices.AddRange(mesh.Vertices);
            foreach (var idx in mesh.Indices)
                allIndices.Add((ushort)(idx + baseVertex));
        }

        if (allVertices.Count == 0) return null;

        var merged = new MeshData
        {
            Vertices = allVertices.ToArray(),
            Indices = allIndices.ToArray(),
        };
        DebugServer.AppendLog($"[MeshExtractor] Merged {paths.Count} models: {merged.Vertices.Length} verts, {merged.TriangleCount} tris");
        return merged;
    }

    private static MeshData ConvertMeddleModel(MeddleModel model, int maxMeshes = int.MaxValue, int[]? allowedMatIdx = null)
    {
        var allVertices = new List<MeshVertex>();
        var allIndices = new List<ushort>();

        // matIdx filter:
        //   allowedMatIdx == null  → legacy behavior: only matIdx == 0 (the primary skin slot)
        //   allowedMatIdx != null  → only matIdx values in the set (resolver-supplied)
        var allowedSet = allowedMatIdx != null ? new HashSet<int>(allowedMatIdx) : null;

        var meshCount = Math.Min(model.Meshes.Count, maxMeshes);
        for (int mi = 0; mi < meshCount; mi++)
        {
            var mesh = model.Meshes[mi];
            DebugServer.AppendLog($"[MeshExtractor]   mesh[{mi}]: matIdx={mesh.MaterialIdx} verts={mesh.Vertices.Count} indices={mesh.Indices.Count} submeshes={mesh.SubMeshes.Count}");

            var keep = allowedSet != null
                ? allowedSet.Contains(mesh.MaterialIdx)
                : mesh.MaterialIdx == 0;

            if (!keep)
            {
                DebugServer.AppendLog($"[MeshExtractor]   → skipped (matIdx not in filter)");
                continue;
            }
            var baseVertex = (ushort)allVertices.Count;

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
                allIndices.Add((ushort)(idx + baseVertex));
            }
        }

        return new MeshData
        {
            Vertices = allVertices.ToArray(),
            Indices = allIndices.ToArray(),
        };
    }
}
