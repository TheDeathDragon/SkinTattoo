using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
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

    // Static log buffer shared across the whole plugin
    public static readonly ConcurrentQueue<string> LogBuffer = new();
    private const int MaxLogEntries = 200;

    public static void AppendLog(string message)
    {
        LogBuffer.Enqueue($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
        while (LogBuffer.Count > MaxLogEntries)
            LogBuffer.TryDequeue(out _);
    }

    public DebugServer(
        Configuration config,
        DecalProject project,
        PenumbraBridge penumbra,
        PreviewService preview)
    {
        _config   = config;
        _project  = project;
        _penumbra = penumbra;
        _preview  = preview;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        var url = $"http://localhost:{_config.HttpPort}/";
        _server = new WebServer(o => o
            .WithUrlPrefix(url)
            .WithMode(HttpListenerMode.EmbedIO))
            .WithWebApi("/api", m => m.WithController(() =>
                new ApiController(_project, _penumbra, _preview, _config)));

        // RunAsync returns a Task — fire and forget, errors go to AppendLog
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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ApiController(
        DecalProject project,
        PenumbraBridge penumbra,
        PreviewService preview,
        Configuration config)
    {
        _project  = project;
        _penumbra = penumbra;
        _preview  = preview;
        _config   = config;
    }

    // ── GET /api/status ───────────────────────────────────────────────────────
    [Route(HttpVerbs.Get, "/status")]
    public object GetStatus() => new
    {
        plugin           = "SkinTatoo",
        gpuReady         = _preview.IsReady,
        penumbraAvailable = _penumbra.IsAvailable,
        meshLoaded       = _preview.CurrentMesh is not null,
        layerCount       = _project.Layers.Count,
        target           = _project.Target.ToString(),
        resolution       = _config.TextureResolution,
    };

    // ── GET /api/project ──────────────────────────────────────────────────────
    [Route(HttpVerbs.Get, "/project")]
    public object GetProject() => new
    {
        target             = _project.Target.ToString(),
        selectedLayerIndex = _project.SelectedLayerIndex,
        layers             = SerializeLayers(),
    };

    // ── POST /api/layer ───────────────────────────────────────────────────────
    [Route(HttpVerbs.Post, "/layer")]
    public async Task<object> PostLayer()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        string name = "New Decal";

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("name", out var nameProp))
                    name = nameProp.GetString() ?? name;
            }
            catch { /* ignore malformed body */ }
        }

        _project.AddLayer(name);
        return new { index = _project.Layers.Count - 1 };
    }

    // ── PUT /api/layer/{id} ───────────────────────────────────────────────────
    [Route(HttpVerbs.Put, "/layer/{id}")]
    public async Task<object> PutLayer(int id)
    {
        if (id < 0 || id >= _project.Layers.Count)
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

        var layer = _project.Layers[id];
        ApplyPartialUpdate(layer, body);

        return new { index = id, layer = SerializeLayer(layer) };
    }

    // ── DELETE /api/layer/{id} ────────────────────────────────────────────────
    [Route(HttpVerbs.Delete, "/layer/{id}")]
    public object DeleteLayer(int id)
    {
        if (id < 0 || id >= _project.Layers.Count)
        {
            HttpContext.Response.StatusCode = 404;
            return new { error = "Layer not found" };
        }

        _project.RemoveLayer(id);
        return new { remaining = _project.Layers.Count };
    }

    // ── POST /api/preview ─────────────────────────────────────────────────────
    [Route(HttpVerbs.Post, "/preview")]
    public async Task<object> PostPreview()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        string? texPath = null;

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("texturePath", out var tp))
                    texPath = tp.GetString();
            }
            catch { }
        }

        await _preview.UpdatePreview(_project, texPath);
        return new { ok = true };
    }

    // ── POST /api/mesh/load ───────────────────────────────────────────────────
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

        await _preview.LoadMesh(path!);
        return new { ok = true, path };
    }

    // ── GET /api/mesh/info ────────────────────────────────────────────────────
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

    // ── GET /api/log ──────────────────────────────────────────────────────────
    [Route(HttpVerbs.Get, "/log")]
    public object GetLog() => new
    {
        entries = DebugServer.LogBuffer.ToArray(),
    };

    // ── helpers ───────────────────────────────────────────────────────────────

    private static List<object> SerializeLayers(DecalProject project)
    {
        var list = new List<object>(project.Layers.Count);
        foreach (var l in project.Layers)
            list.Add(SerializeLayer(l));
        return list;
    }

    private List<object> SerializeLayers() => SerializeLayers(_project);

    private static object SerializeLayer(DecalLayer l) => new
    {
        name                    = l.Name,
        imagePath               = l.ImagePath,
        position                = new { x = l.Position.X, y = l.Position.Y, z = l.Position.Z },
        rotation                = new { x = l.Rotation.X, y = l.Rotation.Y, z = l.Rotation.Z },
        scale                   = new { x = l.Scale.X,    y = l.Scale.Y },
        depth                   = l.Depth,
        opacity                 = l.Opacity,
        blendMode               = l.BlendMode.ToString(),
        isVisible               = l.IsVisible,
        affectsDiffuse          = l.AffectsDiffuse,
        affectsNormal           = l.AffectsNormal,
        backfaceCullingThreshold = l.BackfaceCullingThreshold,
        grazingAngleFade        = l.GrazingAngleFade,
    };

    // Partial-update a layer from a JSON body — only set properties that are present.
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

        if (root.TryGetProperty("position", out v))
            layer.Position = ReadVector3(v, layer.Position);

        if (root.TryGetProperty("rotation", out v))
            layer.Rotation = ReadVector3(v, layer.Rotation);

        if (root.TryGetProperty("scale", out v))
            layer.Scale = ReadVector2(v, layer.Scale);

        if (root.TryGetProperty("depth", out v) && v.ValueKind == JsonValueKind.Number)
            layer.Depth = v.GetSingle();

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

        if (root.TryGetProperty("affectsNormal", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
            layer.AffectsNormal = v.GetBoolean();

        if (root.TryGetProperty("backfaceCullingThreshold", out v) && v.ValueKind == JsonValueKind.Number)
            layer.BackfaceCullingThreshold = v.GetSingle();

        if (root.TryGetProperty("grazingAngleFade", out v) && v.ValueKind == JsonValueKind.Number)
            layer.GrazingAngleFade = v.GetSingle();
    }

    private static Vector3 ReadVector3(JsonElement el, Vector3 fallback)
    {
        if (el.ValueKind != JsonValueKind.Object) return fallback;
        float x = el.TryGetProperty("x", out var ex) ? ex.GetSingle() : fallback.X;
        float y = el.TryGetProperty("y", out var ey) ? ey.GetSingle() : fallback.Y;
        float z = el.TryGetProperty("z", out var ez) ? ez.GetSingle() : fallback.Z;
        return new Vector3(x, y, z);
    }

    private static Vector2 ReadVector2(JsonElement el, Vector2 fallback)
    {
        if (el.ValueKind != JsonValueKind.Object) return fallback;
        float x = el.TryGetProperty("x", out var ex) ? ex.GetSingle() : fallback.X;
        float y = el.TryGetProperty("y", out var ey) ? ey.GetSingle() : fallback.Y;
        return new Vector2(x, y);
    }
}
