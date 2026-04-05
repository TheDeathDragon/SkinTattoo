using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using SkinTatoo.Core;
using SkinTatoo.Interop;
using SkinTatoo.Services;

namespace SkinTatoo.Http;

public class DebugServer : IDisposable
{
    private WebServer? _server;
    private CancellationTokenSource? _cts;

    private readonly Configuration _config;
    private readonly DecalProject _project;
    private readonly PenumbraBridge _penumbra;
    private readonly PreviewService _preview;
    private readonly IDataManager _dataManager;

    public static readonly ConcurrentQueue<string> LogBuffer = new();
    private const int MaxLogEntries = 200;

    public static void AppendLog(string message)
    {
        LogBuffer.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        while (LogBuffer.Count > MaxLogEntries)
            LogBuffer.TryDequeue(out _);
    }

    private readonly TextureSwapService? _textureSwap;

    public DebugServer(
        Configuration config,
        DecalProject project,
        PenumbraBridge penumbra,
        PreviewService preview,
        IDataManager dataManager,
        TextureSwapService? textureSwap = null)
    {
        _config   = config;
        _project  = project;
        _penumbra = penumbra;
        _preview  = preview;
        _dataManager = dataManager;
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
                new ApiController(_project, _penumbra, _preview, _config, _dataManager, _textureSwap)));

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
        _cts    = null;
    }
}

// ─── Controller ──────────────────────────────────────────────────────────────

internal sealed class ApiController : WebApiController
{
    private readonly DecalProject _project;
    private readonly PenumbraBridge _penumbra;
    private readonly PreviewService _preview;
    private readonly Configuration _config;
    private readonly IDataManager _dataManager;
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
        TextureSwapService? textureSwap = null)
    {
        _project  = project;
        _penumbra = penumbra;
        _preview  = preview;
        _config   = config;
        _dataManager = dataManager;
        _textureSwap = textureSwap;
    }

    [Route(HttpVerbs.Get, "/status")]
    public object GetStatus() => new
    {
        plugin           = "SkinTatoo",
        penumbraAvailable = _penumbra.IsAvailable,
        meshLoaded       = _preview.CurrentMesh is not null,
        groupCount       = _project.Groups.Count,
        resolution       = _config.TextureResolution,
        gpuSwapEnabled   = _config.UseGpuSwap,
        canSwapInPlace   = _preview.CanSwapInPlace,
        initializedPaths = _preview.InitializedPathCount,
        lastUpdateMode   = _preview.LastUpdateMode,
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
                var doc = JsonDocument.Parse(body);
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
        gpuSwapEnabled   = _config.UseGpuSwap,
        canSwapInPlace   = _preview.CanSwapInPlace,
        initializedPaths = _preview.InitializedPathCount,
        lastUpdateMode   = _preview.LastUpdateMode,
    };

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
            var doc = JsonDocument.Parse(body);
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
            loaded    = true,
            triangles = mesh.TriangleCount,
            vertices  = mesh.Vertices.Length,
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

    // ── helpers ───────────────────────────────────────────────────────────────

    private static object SerializeLayer(DecalLayer l) => new
    {
        name                    = l.Name,
        imagePath               = l.ImagePath,
        uvCenter                = new { x = l.UvCenter.X, y = l.UvCenter.Y },
        uvScale                 = new { x = l.UvScale.X, y = l.UvScale.Y },
        rotationDeg             = l.RotationDeg,
        opacity                 = l.Opacity,
        blendMode               = l.BlendMode.ToString(),
        isVisible               = l.IsVisible,
        affectsDiffuse          = l.AffectsDiffuse,
        affectsEmissive         = l.AffectsEmissive,
        emissiveColor           = new { r = l.EmissiveColor.X, g = l.EmissiveColor.Y, b = l.EmissiveColor.Z },
        emissiveIntensity       = l.EmissiveIntensity,
        emissiveMask            = l.EmissiveMask.ToString(),
        emissiveMaskFalloff     = l.EmissiveMaskFalloff,
    };

    private static void ApplyPartialUpdate(DecalLayer layer, string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return; }

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
    }

    private static Vector2 ReadVector2(JsonElement el, Vector2 fallback)
    {
        if (el.ValueKind != JsonValueKind.Object) return fallback;
        float x = el.TryGetProperty("x", out var ex) ? ex.GetSingle() : fallback.X;
        float y = el.TryGetProperty("y", out var ey) ? ey.GetSingle() : fallback.Y;
        return new Vector2(x, y);
    }
}
