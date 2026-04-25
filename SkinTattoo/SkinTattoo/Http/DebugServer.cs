using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Penumbra.Api.Helpers;
using SkinTattoo.Core;
using SkinTattoo.Interop;
using SkinTattoo.Services;

namespace SkinTattoo.Http;

public class DebugServer : IDisposable
{
    private WebServer? _server;
    private CancellationTokenSource? _cts;

    private readonly Configuration _config;
    private readonly DecalProject _project;
    private readonly PenumbraBridge _penumbra;
    private readonly PreviewService _preview;
    private readonly IDataManager _dataManager;
    private readonly ModExportService _exportService;

    public static readonly ConcurrentQueue<string> LogBuffer = new();
    private const int MaxLogEntries = 1000;

    public static void AppendLog(string message)
    {
        LogBuffer.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        while (LogBuffer.Count > MaxLogEntries)
            LogBuffer.TryDequeue(out _);
    }

    private readonly TextureSwapService? _textureSwap;
    private readonly Mesh.SkinMeshResolver _resolver;

    public DebugServer(
        Configuration config,
        DecalProject project,
        PenumbraBridge penumbra,
        PreviewService preview,
        IDataManager dataManager,
        ModExportService exportService,
        Mesh.SkinMeshResolver resolver,
        TextureSwapService? textureSwap = null)
    {
        _config = config;
        _project = project;
        _penumbra = penumbra;
        _preview = preview;
        _dataManager = dataManager;
        _exportService = exportService;
        _resolver = resolver;
        _textureSwap = textureSwap;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        var url = $"http://localhost:{_config.HttpPort}/";
        _server = new WebServer(o => o
            .WithUrlPrefix(url)
            .WithMode(HttpListenerMode.EmbedIO))
            .WithWebApi("/api", m => m.WithController(() =>
                new ApiController(_project, _penumbra, _preview, _config, _dataManager, _exportService, _resolver, _textureSwap)));

        _ = _server.RunAsync(_cts.Token).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
                AppendLog($"[DebugServer] error: {t.Exception.GetBaseException().Message}");
        }, TaskContinuationOptions.OnlyOnFaulted);

        AppendLog($"[DebugServer] listening on {url}");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _server?.Dispose();
        _cts?.Dispose();
        _server = null;
        _cts = null;
    }
}

internal sealed class ApiController : WebApiController
{
    private readonly DecalProject _project;
    private readonly PenumbraBridge _penumbra;
    private readonly PreviewService _preview;
    private readonly Configuration _config;
    private readonly IDataManager _dataManager;
    private readonly ModExportService _exportService;
    private readonly Mesh.SkinMeshResolver _resolver;
    private readonly TextureSwapService? _textureSwap;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ApiController(
        DecalProject project,
        PenumbraBridge penumbra,
        PreviewService preview,
        Configuration config,
        IDataManager dataManager,
        ModExportService exportService,
        Mesh.SkinMeshResolver resolver,
        TextureSwapService? textureSwap = null)
    {
        _project = project;
        _penumbra = penumbra;
        _preview = preview;
        _config = config;
        _dataManager = dataManager;
        _exportService = exportService;
        _resolver = resolver;
        _textureSwap = textureSwap;
    }

    [Route(HttpVerbs.Get, "/status")]
    public object GetStatus() => new
    {
        plugin = "SkinTattoo",
        penumbraAvailable = _penumbra.IsAvailable,
        meshLoaded = _preview.CurrentMesh is not null,
        groupCount = _project.Groups.Count,
        gpuSwapEnabled = _config.UseGpuSwap,
        canSwapInPlace = _preview.CanSwapInPlace,
        initializedPaths = _preview.InitializedPathCount,
        lastUpdateMode = _preview.LastUpdateMode,
    };

    [Route(HttpVerbs.Get, "/project")]
    public object GetProject() => new
    {
        selectedGroupIndex = _project.SelectedGroupIndex,
        groups = _project.Groups.Select(g => new
        {
            name = g.Name,
            diffuseGamePath = g.DiffuseGamePath,
            normGamePath = g.NormGamePath,
            mtrlGamePath = g.MtrlGamePath,
            mtrlDiskPath = g.MtrlDiskPath,
            normDiskPath = g.NormDiskPath,
            meshDiskPath = g.MeshDiskPath,
            selectedLayerIndex = g.SelectedLayerIndex,
            layers = g.Layers.Select(SerializeLayer).ToList(),
        }).ToList(),
    };

    [Route(HttpVerbs.Post, "/layer")]
    public async Task<object> PostLayer()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        string name = "New Decal";
        int groupIndex = _project.SelectedGroupIndex;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                    name = nameProp.GetString() ?? name;
                if (doc.RootElement.TryGetProperty("groupIndex", out var gi))
                    groupIndex = gi.GetInt32();
            }
            catch { }
        }

        var group = groupIndex >= 0 && groupIndex < _project.Groups.Count
            ? _project.Groups[groupIndex] : null;
        if (group == null)
            return new { error = "No target group selected" };

        group.AddLayer(name);
        return new { groupIndex, layerIndex = group.Layers.Count - 1 };
    }

    [Route(HttpVerbs.Put, "/layer/{id}")]
    public async Task<object> PutLayer(int id)
    {
        var group = _project.SelectedGroup;
        if (group == null || id < 0 || id >= group.Layers.Count)
        {
            HttpContext.Response.StatusCode = 404;
            return new { error = "Layer not found" };
        }

        var body = await HttpContext.GetRequestBodyAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            HttpContext.Response.StatusCode = 400;
            return new { error = "Empty body" };
        }

        var layer = group.Layers[id];
        ApplyPartialUpdate(layer, body);

        return new { index = id, layer = SerializeLayer(layer) };
    }

    [Route(HttpVerbs.Delete, "/layer/{id}")]
    public object DeleteLayer(int id)
    {
        var group = _project.SelectedGroup;
        if (group == null || id < 0 || id >= group.Layers.Count)
        {
            HttpContext.Response.StatusCode = 404;
            return new { error = "Layer not found" };
        }

        group.RemoveLayer(id);
        return new { remaining = group.Layers.Count };
    }

    [Route(HttpVerbs.Post, "/preview")]
    public object PostPreview()
    {
        _preview.UpdatePreview(_project);
        return new { ok = true, mode = _preview.LastUpdateMode };
    }

    [Route(HttpVerbs.Post, "/preview/full")]
    public object PostPreviewFull()
    {
        _preview.UpdatePreviewFull(_project);
        return new { ok = true, mode = "full" };
    }

    [Route(HttpVerbs.Post, "/preview/inplace")]
    public object PostPreviewInPlace()
    {
        // Triggers async in-place, results applied on next Draw frame
        _preview.UpdatePreview(_project);
        return new { ok = true, mode = "async-inplace" };
    }

    [Route(HttpVerbs.Post, "/swap/reset")]
    public object PostSwapReset()
    {
        _preview.ResetSwapState();
        return new { ok = true };
    }

    [Route(HttpVerbs.Get, "/swap/status")]
    public object GetSwapStatus() => new
    {
        gpuSwapEnabled = _config.UseGpuSwap,
        canSwapInPlace = _preview.CanSwapInPlace,
        initializedPaths = _preview.InitializedPathCount,
        lastUpdateMode = _preview.LastUpdateMode,
    };

    [Route(HttpVerbs.Get, "/debug/pbr")]
    public object GetPbrDebug() => new
    {
        canSwapInPlace = _preview.CanSwapInPlace,
        lastUpdateMode = _preview.LastUpdateMode,
        groups = _preview.GetPbrDiagnostics(_project),
    };

    // -- Shader-patch mode switch (A/B debugging) -----------------------------
    // GET to read current mode; POST /debug/patch-mode?mode=v11b|v13 to switch.
    // Switching invalidates the cached patched shpk so next preview regenerates it.
    [Route(HttpVerbs.Get, "/debug/patch-mode")]
    public object GetPatchMode() => new
    {
        mode = Services.SkinShpkPatcher.Mode.ToString(),
        available = Enum.GetNames(typeof(Services.SkinShpkPatcher.PatchMode)),
    };

    [Route(HttpVerbs.Post, "/debug/patch-mode")]
    public object PostPatchMode()
    {
        var q = HttpContext.GetRequestQueryData();
        var modeStr = (q["mode"] ?? "").ToLowerInvariant();
        Services.SkinShpkPatcher.PatchMode newMode;
        if (modeStr == "v13" || modeStr == "valbody" || modeStr == "valbody_v13")
            newMode = Services.SkinShpkPatcher.PatchMode.ValBody_v13;
        else if (modeStr == "v11b" || modeStr == "valemissive" || modeStr == "valemissive_v11b")
            newMode = Services.SkinShpkPatcher.PatchMode.ValEmissive_v11b;
        else
            return new { error = "?mode= must be v11b or v13" };

        Services.SkinShpkPatcher.Mode = newMode;
        _preview.ResetSwapState();
        return new { ok = true, mode = newMode.ToString() };
    }

    // -- Raw mtrl dump: shader keys + constants + sampler list ---------
    // Accepts ?disk=<path> to inspect any .mtrl on disk (e.g. bibo source).
    [Route(HttpVerbs.Get, "/debug/mtrl")]
    public object GetMtrl()
    {
        var q = HttpContext.GetRequestQueryData();
        var disk = q["disk"];
        if (string.IsNullOrWhiteSpace(disk))
        {
            int gi = 0; int.TryParse(q["group"], out gi);
            if (gi < 0 || gi >= _project.Groups.Count)
                return new { error = "provide ?disk=<path> or ?group=<idx>" };
            var g = _project.Groups[gi];
            var source = (q["source"] ?? "orig").ToLowerInvariant();
            disk = source switch
            {
                "orig" => g.OrigMtrlDiskPath ?? g.MtrlDiskPath,
                "current" => g.MtrlDiskPath,
                _ => g.OrigMtrlDiskPath ?? g.MtrlDiskPath,
            };
        }
        if (string.IsNullOrWhiteSpace(disk))
            return new { error = "no path resolved" };
        if (!System.IO.File.Exists(disk))
            return new { error = "file not found", path = disk };

        try
        {
            // Work around Lumina's Dawntrail ColorTable quirk by copying into a temp file
            // inside SqPack-agnostic cache dir, same technique the plugin uses elsewhere.
            var bytes = System.IO.File.ReadAllBytes(disk);
            var tmp = Path.Combine(Path.GetTempPath(), $"skintattoo_mtrl_{Guid.NewGuid():N}.mtrl");
            System.IO.File.WriteAllBytes(tmp, bytes);
            Lumina.Data.Files.MtrlFile f;
            try { f = _dataManager.GameData.GetFileFromDisk<Lumina.Data.Files.MtrlFile>(tmp); }
            finally { try { System.IO.File.Delete(tmp); } catch { } }
            var shpkName = System.Text.Encoding.ASCII.GetString(
                f.Strings, f.FileHeader.ShaderPackageNameOffset,
                Array.IndexOf(f.Strings, (byte)0, f.FileHeader.ShaderPackageNameOffset)
                    - f.FileHeader.ShaderPackageNameOffset);
            var keys = f.ShaderKeys.Select(k => new
            {
                categoryId = $"0x{k.Category:X8}",
                valueId = $"0x{k.Value:X8}",
            }).ToList();
            var constants = f.Constants.Select(c =>
            {
                var vals = new List<float>();
                int fi = c.ValueOffset / 4;
                int cnt = c.ValueSize / 4;
                for (int i = 0; i < cnt && fi + i < f.ShaderValues.Length; i++)
                    vals.Add(f.ShaderValues[fi + i]);
                return new
                {
                    constantId = $"0x{c.ConstantId:X8}",
                    offset = (int)c.ValueOffset,
                    size = (int)c.ValueSize,
                    values = vals,
                };
            }).ToList();
            var samplers = f.Samplers.Select(s => new
            {
                samplerId = $"0x{s.SamplerId:X8}",
                textureIndex = s.TextureIndex,
                flags = $"0x{s.Flags:X8}",
            }).ToList();
            return new
            {
                path = disk,
                shaderPackage = shpkName,
                dataSetSize = f.FileHeader.DataSetSize,
                hasColorTable = (bytes.Length >= 16) && ((bytes[15] & 0x04) != 0),
                shaderKeys = keys,
                constants,
                samplers,
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, path = disk };
        }
    }

    // -- Texture channel stats (built for investigating normal.a seam issue) ---
    // Accepts one of: ?path=<game-path>, ?disk=<absolute-path>,
    // or ?group=<idx>&source=vanilla|current|diskmod  where:
    //   vanilla  = SqPack raw (pre-any-mod)
    //   current  = whatever Penumbra is serving right now (mod or preview)
    //   diskmod  = the body-mod source on disk (OrigNormDiskPath / NormDiskPath)
    [Route(HttpVerbs.Get, "/debug/tex-stats")]
    public object GetTexStats()
    {
        var q = HttpContext.GetRequestQueryData();
        string? resolved = null;
        string label = "";

        var explicitPath = q["path"];
        var explicitDisk = q["disk"];
        if (!string.IsNullOrWhiteSpace(explicitDisk))
        {
            resolved = explicitDisk; label = $"disk={explicitDisk}";
        }
        else if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            resolved = explicitPath; label = $"path={explicitPath}";
        }
        else
        {
            var groupIdx = 0;
            int.TryParse(q["group"], out groupIdx);
            if (groupIdx < 0 || groupIdx >= _project.Groups.Count)
                return new { error = "group out of range" };
            var g = _project.Groups[groupIdx];
            var source = (q["source"] ?? "vanilla").ToLowerInvariant();
            switch (source)
            {
                case "vanilla":
                    resolved = g.NormGamePath;
                    label = $"group[{groupIdx}] vanilla norm game path";
                    break;
                case "current":
                    try { resolved = _penumbra.ResolvePlayer(g.NormGamePath ?? ""); } catch { }
                    if (string.IsNullOrEmpty(resolved)) resolved = g.NormGamePath;
                    label = $"group[{groupIdx}] current (penumbra resolved)";
                    break;
                case "diskmod":
                    resolved = g.OrigNormDiskPath ?? g.NormDiskPath;
                    label = $"group[{groupIdx}] body mod disk";
                    break;
                default:
                    return new { error = "source must be vanilla|current|diskmod" };
            }
        }

        if (string.IsNullOrWhiteSpace(resolved))
            return new { error = "no path resolved", label };

        var img = _preview.LoadTextureAny(resolved);
        if (img == null)
            return new { error = "load failed", label, path = resolved };

        var (data, w, h) = img.Value;
        return new
        {
            label,
            path = resolved,
            width = w,
            height = h,
            channels = ComputeChannelStats(data, w, h),
            samples = SampleGrid(data, w, h, 8),
        };
    }

    private static object ComputeChannelStats(byte[] rgba, int w, int h)
    {
        // Return per-channel histogram + min/max/mean/median/p10/p90 + unique count.
        var result = new Dictionary<string, object>();
        string[] names = { "r", "g", "b", "a" };
        int pixCount = w * h;
        for (int ch = 0; ch < 4; ch++)
        {
            var hist = new int[256];
            long sum = 0;
            byte min = 255, max = 0;
            for (int i = ch; i < rgba.Length; i += 4)
            {
                var v = rgba[i];
                hist[v]++;
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            int unique = 0;
            foreach (var c in hist) if (c > 0) unique++;
            // Percentiles
            int p10Idx = (int)(pixCount * 0.10);
            int p50Idx = (int)(pixCount * 0.50);
            int p90Idx = (int)(pixCount * 0.90);
            int acc = 0, p10 = -1, p50 = -1, p90 = -1;
            for (int v = 0; v < 256; v++)
            {
                acc += hist[v];
                if (p10 < 0 && acc >= p10Idx) p10 = v;
                if (p50 < 0 && acc >= p50Idx) p50 = v;
                if (p90 < 0 && acc >= p90Idx) p90 = v;
            }
            // Top 10 bins by count (to spot bimodal / dominant values)
            var top = new List<(int Value, int Count)>();
            for (int v = 0; v < 256; v++) if (hist[v] > 0) top.Add((v, hist[v]));
            top.Sort((a, b) => b.Count.CompareTo(a.Count));
            var topList = top.Take(10)
                .Select(t => new { value = t.Value, count = t.Count,
                    pct = (float)t.Count / pixCount })
                .ToList();
            result[names[ch]] = new
            {
                min,
                max,
                mean = (float)sum / pixCount,
                p10,
                median = p50,
                p90,
                uniqueValues = unique,
                topBins = topList,
            };
        }
        return result;
    }

    private static object SampleGrid(byte[] rgba, int w, int h, int n)
    {
        var pts = new List<object>();
        for (int yi = 0; yi < n; yi++)
        {
            for (int xi = 0; xi < n; xi++)
            {
                int x = (int)((xi + 0.5) * w / n);
                int y = (int)((yi + 0.5) * h / n);
                int o = (y * w + x) * 4;
                if (o + 3 >= rgba.Length) continue;
                pts.Add(new
                {
                    x, y,
                    r = rgba[o + 0],
                    g = rgba[o + 1],
                    b = rgba[o + 2],
                    a = rgba[o + 3],
                });
            }
        }
        return pts;
    }

    [Route(HttpVerbs.Get, "/textures")]
    public object GetTextures()
    {
        if (_textureSwap == null)
            return new { error = "TextureSwapService not available" };
        return new { dump = _textureSwap.DumpCharacterTextures() };
    }

    [Route(HttpVerbs.Post, "/swap/toggle")]
    public object PostSwapToggle()
    {
        _config.UseGpuSwap = !_config.UseGpuSwap;
        _config.Save();
        return new { useGpuSwap = _config.UseGpuSwap };
    }

    [Route(HttpVerbs.Post, "/mesh/load")]
    public async Task<object> PostMeshLoad()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            HttpContext.Response.StatusCode = 400;
            return new { error = "Body must contain 'path'" };
        }

        string? path = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("path", out var pathProp))
                path = pathProp.GetString();
        }
        catch { }

        if (string.IsNullOrWhiteSpace(path))
        {
            HttpContext.Response.StatusCode = 400;
            return new { error = "Missing 'path' field" };
        }

        var ok = _preview.LoadMesh(path!);
        var mesh = _preview.CurrentMesh;
        return new
        {
            ok,
            path,
            triangles = mesh?.TriangleCount ?? 0,
            vertices = mesh?.Vertices.Length ?? 0,
        };
    }

    [Route(HttpVerbs.Get, "/mesh/info")]
    public object GetMeshInfo()
    {
        var mesh = _preview.CurrentMesh;
        if (mesh is null)
            return new { loaded = false, triangles = 0, vertices = 0 };

        return new
        {
            loaded = true,
            triangles = mesh.TriangleCount,
            vertices = mesh.Vertices.Length,
        };
    }

    [Route(HttpVerbs.Get, "/log")]
    public object GetLog() => new
    {
        entries = DebugServer.LogBuffer.ToArray(),
    };

    [Route(HttpVerbs.Get, "/filecheck")]
    public object GetFileCheck()
    {
        var path = HttpContext.GetRequestQueryData()["path"];
        if (string.IsNullOrEmpty(path))
            return new { error = "missing ?path= parameter" };

        var exists = _dataManager.FileExists(path);
        string? resolvedPath = null;
        try
        {
            var resolved = _penumbra.ResolvePlayer(path);
            resolvedPath = resolved;
        }
        catch { }

        object? rawInfo = null;
        if (exists)
        {
            try
            {
                var raw = _dataManager.GetFile(path);
                rawInfo = new { size = raw?.Data.Length ?? 0 };
            }
            catch (Exception ex)
            {
                rawInfo = new { error = ex.Message };
            }
        }

        return new
        {
            path,
            exists,
            penumbraResolved = resolvedPath,
            rawFile = rawInfo,
            gameDataPath = _dataManager.GameData.DataPath.FullName,
        };
    }

    [Route(HttpVerbs.Get, "/debug/skin-chain")]
    public object GetSkinChain()
    {
        var query = HttpContext.GetRequestQueryData();
        var mtrl = query["mtrl"];
        var tex = query["tex"];

        // If only tex is given, walk the live tree to find a parent mtrl that
        // owns this tex, then use its game path. tex paths are unreliable
        // under mods (they get rewritten to mod paths), but mtrl game paths
        // stay vanilla  -- that's why the resolver wants mtrl as input.
        var trees = _penumbra.GetPlayerTrees();
        if (string.IsNullOrWhiteSpace(mtrl) && !string.IsNullOrWhiteSpace(tex) && trees != null)
        {
            foreach (var (_, tree) in trees)
            {
                foreach (var top in tree.Nodes)
                {
                    var found = FindMtrlForTex(top, tex);
                    if (found != null) { mtrl = found.GamePath; break; }
                }
                if (!string.IsNullOrEmpty(mtrl)) break;
            }
        }

        if (string.IsNullOrWhiteSpace(mtrl))
            return new { error = "missing ?mtrl= (or ?tex= that resolves to a mtrl in the live tree)" };

        var parsed = Core.TexPathParser.ParseFromMtrl(mtrl);

        string? mtrlResolved = null;
        try { mtrlResolved = _penumbra.ResolvePlayer(mtrl); } catch { }

        var resolution = _resolver.Resolve(mtrl, trees);

        // For diagnostics, also list every mdl node in the tree that references
        // this exact mtrl (engine-rewritten form). The race-filtered resolver
        // result is just one of these  -- the others are noise.
        var liveMdls = new List<object>();
        if (trees != null)
        {
            foreach (var (treeId, tree) in trees)
            {
                foreach (var top in tree.Nodes)
                    CollectMdlsReferencingMtrl(top, null, mtrl!, treeId, tree.Name, liveMdls);
            }
        }

        return new
        {
            input = new { mtrl, tex },
            parsed = new
            {
                race = parsed.Race,
                slotKind = parsed.SlotKind,
                slotAbbr = parsed.SlotAbbr,
                slotId = parsed.SlotId,
                roleSuffix = parsed.RoleSuffix,
                bodySlotIdRewritten = parsed.BodySlotIdIsRewritten,
            },
            mtrlPenumbraResolved = mtrlResolved,
            resolution = new
            {
                success = resolution.Success,
                playerRace = resolution.PlayerRace,
                slotKind = resolution.SlotKind,
                slotId = resolution.SlotId,
                meshSlots = resolution.MeshSlots.Select(s => new
                {
                    gamePath = s.GamePath,
                    diskPath = s.DiskPath,
                    matIdx = s.MatIdx,
                }).ToList(),
                diagnostics = resolution.Diagnostics,
            },
            liveMdlsReferencingThisMtrl = liveMdls,
        };
    }

    private static ResourceNodeDto? FindMtrlForTex(ResourceNodeDto node, string targetTex)
    {
        if (node.Type == Penumbra.Api.Enums.ResourceType.Mtrl)
        {
            foreach (var child in node.Children)
                if (child.Type == Penumbra.Api.Enums.ResourceType.Tex
                    && string.Equals(child.GamePath, targetTex, StringComparison.OrdinalIgnoreCase))
                    return node;
        }
        foreach (var child in node.Children)
        {
            var found = FindMtrlForTex(child, targetTex);
            if (found != null) return found;
        }
        return null;
    }

    private static void CollectMdlsReferencingMtrl(
        ResourceNodeDto node, ResourceNodeDto? parentMdl,
        string targetMtrlGamePath, ushort treeId, string? treeName,
        List<object> sink)
    {
        var mdl = node.Type == Penumbra.Api.Enums.ResourceType.Mdl ? node : parentMdl;

        if (node.Type == Penumbra.Api.Enums.ResourceType.Mtrl
            && string.Equals(node.GamePath, targetMtrlGamePath, StringComparison.OrdinalIgnoreCase)
            && mdl != null)
        {
            sink.Add(new
            {
                treeId,
                treeName,
                mdlGamePath = mdl.GamePath,
                mdlActualPath = mdl.ActualPath,
            });
        }

        foreach (var child in node.Children)
            CollectMdlsReferencingMtrl(child, mdl, targetMtrlGamePath, treeId, treeName, sink);
    }

    [Route(HttpVerbs.Get, "/player/trees")]
    public object GetPlayerTrees()
    {
        var trees = _penumbra.GetPlayerTrees();
        if (trees == null)
            return new { error = "Penumbra not available" };

        return trees.ToDictionary(
            kvp => $"obj_{kvp.Key}",
            kvp => new
            {
                name = kvp.Value.Name,
                raceCode = kvp.Value.RaceCode,
                nodes = kvp.Value.Nodes.Select(SerializeNode).ToList(),
            });
    }

    private static object SerializeNode(ResourceNodeDto n) => new
    {
        type = n.Type.ToString(),
        icon = n.Icon.ToString(),
        name = n.Name,
        gamePath = n.GamePath,
        actualPath = n.ActualPath,
        children = n.Children.Select(SerializeNode).ToList(),
    };

    [Route(HttpVerbs.Get, "/player/resources")]
    public object GetPlayerResources()
    {
        var resources = _penumbra.GetPlayerResources();
        if (resources == null)
            return new { error = "Penumbra not available or no player resources" };

        var result = new Dictionary<string, object>();
        foreach (var (objIdx, paths) in resources)
        {
            var filtered = new Dictionary<string, string[]>();
            foreach (var (gamePath, resolvedPaths) in paths)
            {
                if (gamePath.Contains(".mdl") || gamePath.Contains(".tex") || gamePath.Contains(".mtrl"))
                    filtered[gamePath] = resolvedPaths.ToArray();
            }
            if (filtered.Count > 0)
                result[$"obj_{objIdx}"] = filtered;
        }
        return result;
    }

    [Route(HttpVerbs.Post, "/export")]
    public async Task<object> PostExport()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            HttpContext.Response.StatusCode = 400;
            return new { error = "Empty body" };
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var options = new Core.ModExportOptions
            {
                ModName = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "",
                Author = root.TryGetProperty("author", out var a) ? (a.GetString() ?? "") : "",
                Version = root.TryGetProperty("version", out var v) ? (v.GetString() ?? "1.0") : "1.0",
                Description = root.TryGetProperty("description", out var d) ? (d.GetString() ?? "") : "",
                Target = (root.TryGetProperty("target", out var t) && t.GetString() == "penumbra")
                    ? Core.ExportTarget.InstallToPenumbra
                    : Core.ExportTarget.LocalPmp,
                OutputPmpPath = root.TryGetProperty("outputPath", out var op) ? op.GetString() : null,
            };

            if (root.TryGetProperty("groupIndices", out var giArr) && giArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in giArr.EnumerateArray())
                {
                    var idx = el.GetInt32();
                    if (idx >= 0 && idx < _project.Groups.Count)
                        options.SelectedGroups.Add(_project.Groups[idx]);
                }
            }
            else
            {
                foreach (var g in _project.Groups)
                    if (g.Layers.Count > 0)
                        options.SelectedGroups.Add(g);
            }

            var result = await Task.Run(() => _exportService.Export(options));
            HttpContext.Response.StatusCode = result.Success ? 200 : 500;
            return new
            {
                success = result.Success,
                message = result.Message,
                pmpPath = result.PmpPath,
                successGroups = result.SuccessGroups,
                skippedGroups = result.SkippedGroups,
            };
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            return new { error = ex.Message };
        }
    }

    private static object SerializeLayer(DecalLayer l) => new
    {
        kind = l.Kind.ToString(),
        name = l.Name,
        imagePath = l.ImagePath,
        uvCenter = new { x = l.UvCenter.X, y = l.UvCenter.Y },
        uvScale = new { x = l.UvScale.X, y = l.UvScale.Y },
        rotationDeg = l.RotationDeg,
        opacity = l.Opacity,
        blendMode = l.BlendMode.ToString(),
        clip = l.Clip.ToString(),
        targetMap = l.TargetMap.ToString(),
        isVisible = l.IsVisible,
        allocatedRowPair = l.AllocatedRowPair,

        affectsDiffuse = l.AffectsDiffuse,
        affectsSpecular = l.AffectsSpecular,
        affectsEmissive = l.AffectsEmissive,
        affectsRoughness = l.AffectsRoughness,
        affectsMetalness = l.AffectsMetalness,
        affectsSheen = l.AffectsSheen,

        diffuseColor = new { r = l.DiffuseColor.X, g = l.DiffuseColor.Y, b = l.DiffuseColor.Z },
        specularColor = new { r = l.SpecularColor.X, g = l.SpecularColor.Y, b = l.SpecularColor.Z },
        emissiveColor = new { r = l.EmissiveColor.X, g = l.EmissiveColor.Y, b = l.EmissiveColor.Z },
        emissiveIntensity = l.EmissiveIntensity,
        roughness = l.Roughness,
        metalness = l.Metalness,
        sheenRate = l.SheenRate,
        sheenTint = l.SheenTint,
        sheenAperture = l.SheenAperture,

        fadeMask = l.FadeMask.ToString(),
        fadeMaskFalloff = l.FadeMaskFalloff,
        gradientAngleDeg = l.GradientAngleDeg,
        gradientScale = l.GradientScale,
        gradientOffset = l.GradientOffset,

        emissiveMask = l.FadeMask.ToString(),
        emissiveMaskFalloff = l.FadeMaskFalloff,
    };

    private static void ApplyPartialUpdate(DecalLayer layer, string json)
    {
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(json); }
        catch { return; }

        try
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("name", out var v) && v.ValueKind == JsonValueKind.String)
                layer.Name = v.GetString()!;

            if (root.TryGetProperty("imagePath", out v) && v.ValueKind == JsonValueKind.String)
                layer.ImagePath = v.GetString();

            if (root.TryGetProperty("uvCenter", out v))
                layer.UvCenter = ReadVector2(v, layer.UvCenter);

            if (root.TryGetProperty("uvScale", out v))
                layer.UvScale = ReadVector2(v, layer.UvScale);

            if (root.TryGetProperty("rotationDeg", out v) && v.ValueKind == JsonValueKind.Number)
                layer.RotationDeg = v.GetSingle();

            if (root.TryGetProperty("opacity", out v) && v.ValueKind == JsonValueKind.Number)
                layer.Opacity = v.GetSingle();

            if (root.TryGetProperty("blendMode", out v) && v.ValueKind == JsonValueKind.String)
            {
                if (Enum.TryParse<BlendMode>(v.GetString(), ignoreCase: true, out var bm))
                    layer.BlendMode = bm;
            }

            if (root.TryGetProperty("isVisible", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                layer.IsVisible = v.GetBoolean();

            if (root.TryGetProperty("affectsDiffuse", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                layer.AffectsDiffuse = v.GetBoolean();

            if (root.TryGetProperty("affectsEmissive", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                layer.AffectsEmissive = v.GetBoolean();

            if (root.TryGetProperty("allocatedRowPair", out v) && v.ValueKind == JsonValueKind.Number)
                layer.AllocatedRowPair = v.GetInt32();

            if (root.TryGetProperty("emissiveIntensity", out v) && v.ValueKind == JsonValueKind.Number)
                layer.EmissiveIntensity = v.GetSingle();

            if (root.TryGetProperty("emissiveColor", out v))
            {
                if (v.ValueKind == JsonValueKind.Object)
                {
                    float r = v.TryGetProperty("r", out var vr) ? vr.GetSingle() : layer.EmissiveColor.X;
                    float g = v.TryGetProperty("g", out var vg) ? vg.GetSingle() : layer.EmissiveColor.Y;
                    float b = v.TryGetProperty("b", out var vb) ? vb.GetSingle() : layer.EmissiveColor.Z;
                    layer.EmissiveColor = new Vector3(r, g, b);
                }
            }

            if (root.TryGetProperty("kind", out v) && v.ValueKind == JsonValueKind.String)
            {
                if (Enum.TryParse<LayerKind>(v.GetString(), ignoreCase: true, out var k))
                    layer.Kind = k;
            }

            if (root.TryGetProperty("clip", out v) && v.ValueKind == JsonValueKind.String)
            {
                if (Enum.TryParse<ClipMode>(v.GetString(), ignoreCase: true, out var cm))
                    layer.Clip = cm;
            }

            if (root.TryGetProperty("targetMap", out v) && v.ValueKind == JsonValueKind.String)
            {
                if (Enum.TryParse<TargetMap>(v.GetString(), ignoreCase: true, out var tm))
                    layer.TargetMap = tm;
            }

            if (root.TryGetProperty("affectsSpecular", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                layer.AffectsSpecular = v.GetBoolean();
            if (root.TryGetProperty("affectsRoughness", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                layer.AffectsRoughness = v.GetBoolean();
            if (root.TryGetProperty("affectsMetalness", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                layer.AffectsMetalness = v.GetBoolean();
            if (root.TryGetProperty("affectsSheen", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                layer.AffectsSheen = v.GetBoolean();

            if (root.TryGetProperty("diffuseColor", out v) && v.ValueKind == JsonValueKind.Object)
            {
                float r = v.TryGetProperty("r", out var vr) ? vr.GetSingle() : layer.DiffuseColor.X;
                float g = v.TryGetProperty("g", out var vg) ? vg.GetSingle() : layer.DiffuseColor.Y;
                float b = v.TryGetProperty("b", out var vb) ? vb.GetSingle() : layer.DiffuseColor.Z;
                layer.DiffuseColor = new Vector3(r, g, b);
            }
            if (root.TryGetProperty("specularColor", out v) && v.ValueKind == JsonValueKind.Object)
            {
                float r = v.TryGetProperty("r", out var vr) ? vr.GetSingle() : layer.SpecularColor.X;
                float g = v.TryGetProperty("g", out var vg) ? vg.GetSingle() : layer.SpecularColor.Y;
                float b = v.TryGetProperty("b", out var vb) ? vb.GetSingle() : layer.SpecularColor.Z;
                layer.SpecularColor = new Vector3(r, g, b);
            }

            if (root.TryGetProperty("roughness", out v) && v.ValueKind == JsonValueKind.Number)
                layer.Roughness = v.GetSingle();
            if (root.TryGetProperty("metalness", out v) && v.ValueKind == JsonValueKind.Number)
                layer.Metalness = v.GetSingle();
            if (root.TryGetProperty("sheenRate", out v) && v.ValueKind == JsonValueKind.Number)
                layer.SheenRate = v.GetSingle();
            if (root.TryGetProperty("sheenTint", out v) && v.ValueKind == JsonValueKind.Number)
                layer.SheenTint = v.GetSingle();
            if (root.TryGetProperty("sheenAperture", out v) && v.ValueKind == JsonValueKind.Number)
                layer.SheenAperture = v.GetSingle();

            if (root.TryGetProperty("fadeMask", out v) && v.ValueKind == JsonValueKind.String)
            {
                if (Enum.TryParse<LayerFadeMask>(v.GetString(), ignoreCase: true, out var fm))
                    layer.FadeMask = fm;
            }
            else if (root.TryGetProperty("emissiveMask", out v) && v.ValueKind == JsonValueKind.String)
            {
                if (Enum.TryParse<LayerFadeMask>(v.GetString(), ignoreCase: true, out var fm))
                    layer.FadeMask = fm;
            }

            if (root.TryGetProperty("fadeMaskFalloff", out v) && v.ValueKind == JsonValueKind.Number)
                layer.FadeMaskFalloff = v.GetSingle();
            else if (root.TryGetProperty("emissiveMaskFalloff", out v) && v.ValueKind == JsonValueKind.Number)
                layer.FadeMaskFalloff = v.GetSingle();

            if (root.TryGetProperty("gradientAngleDeg", out v) && v.ValueKind == JsonValueKind.Number)
                layer.GradientAngleDeg = v.GetSingle();
            if (root.TryGetProperty("gradientScale", out v) && v.ValueKind == JsonValueKind.Number)
                layer.GradientScale = v.GetSingle();
            if (root.TryGetProperty("gradientOffset", out v) && v.ValueKind == JsonValueKind.Number)
                layer.GradientOffset = v.GetSingle();
        }
        finally { doc?.Dispose(); }
    }

    private static Vector2 ReadVector2(JsonElement el, Vector2 fallback)
    {
        if (el.ValueKind != JsonValueKind.Object) return fallback;
        float x = el.TryGetProperty("x", out var ex) ? ex.GetSingle() : fallback.X;
        float y = el.TryGetProperty("y", out var ey) ? ey.GetSingle() : fallback.Y;
        return new Vector2(x, y);
    }
}
