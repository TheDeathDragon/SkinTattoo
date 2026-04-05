using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using SkinTatoo.Core;
using SkinTatoo.Http;
using SkinTatoo.Interop;
using SkinTatoo.Mesh;

namespace SkinTatoo.Services;

public class PreviewService : IDisposable
{
    private readonly MeshExtractor meshExtractor;
    private readonly DecalImageLoader imageLoader;
    private readonly PenumbraBridge penumbra;
    private readonly TextureSwapService? textureSwap;
    private readonly EmissiveCBufferHook? emissiveHook;
    private readonly IPluginLog log;
    private readonly Configuration config;

    private MeshData? currentMesh;
    private readonly string outputDir;
    private bool disposed;

    // GPU swap state tracking
    private readonly HashSet<string> initializedRedirects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> previewDiskPaths = new(StringComparer.OrdinalIgnoreCase);

    // Cache base textures to avoid re-reading from disk on every update
    private readonly Dictionary<string, (byte[] Data, int Width, int Height)> baseTextureCache = new();

    // Emissive ConstantBuffer offsets (mtrlGamePath → byte offset, used for full redraw .mtrl building)
    private readonly Dictionary<string, int> emissiveOffsets = new();

    // Track mtrl preview disk paths (mtrlGamePath → disk path for ColorTable matching)
    private readonly Dictionary<string, string> previewMtrlDiskPaths = new(StringComparer.OrdinalIgnoreCase);

    // Track emissive color to detect changes that need full redraw
    private readonly Dictionary<string, Vector3> lastEmissiveColors = new();

    // Async compositing
    private CancellationTokenSource? asyncCancel;
    private volatile SwapBatch? pendingBatch;

    private record SwapBatchEntry(string GamePath, string? DiskPath, byte[] BgraData, int Width, int Height);
    private record EmissiveEntry(string MtrlGamePath, string? MtrlDiskPath, Vector3 Color, int CBufferOffset);
    private record SwapBatch(List<SwapBatchEntry> Textures, List<EmissiveEntry> Emissives);

    public MeshData? CurrentMesh => currentMesh;

    /// <summary>Whether in-place GPU swap is available for all active groups.</summary>
    public bool CanSwapInPlace { get; private set; }

    /// <summary>Number of paths currently initialized for GPU swap.</summary>
    public int InitializedPathCount => initializedRedirects.Count;

    /// <summary>Last update mode used.</summary>
    public string LastUpdateMode { get; private set; } = "none";

    /// <summary>Whether emissive CBuffer offset is known for a group's mtrl.</summary>
    public bool HasEmissiveOffset(string? mtrlGamePath)
        => !string.IsNullOrEmpty(mtrlGamePath) && emissiveOffsets.TryGetValue(mtrlGamePath, out var off) && off > 0;

    public PreviewService(
        MeshExtractor meshExtractor,
        DecalImageLoader imageLoader,
        PenumbraBridge penumbra,
        TextureSwapService? textureSwap,
        EmissiveCBufferHook? emissiveHook,
        IPluginLog log,
        Configuration config,
        string outputDir)
    {
        this.meshExtractor = meshExtractor;
        this.imageLoader = imageLoader;
        this.penumbra = penumbra;
        this.textureSwap = textureSwap;
        this.emissiveHook = emissiveHook;
        this.log = log;
        this.config = config;
        this.outputDir = outputDir;

        Directory.CreateDirectory(outputDir);
    }

    public bool LoadMesh(string gameMdlPath)
    {
        DebugServer.AppendLog($"[PreviewService] LoadMesh: {gameMdlPath}");

        MeshData? meshData;
        try
        {
            meshData = meshExtractor.ExtractMesh(gameMdlPath);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"LoadMesh exception: {ex.Message}");
            DebugServer.AppendLog($"[PreviewService] LoadMesh exception: {ex.Message}");
            return false;
        }

        if (meshData == null)
        {
            log.Error($"LoadMesh failed: ExtractMesh returned null for {gameMdlPath}");
            DebugServer.AppendLog($"[PreviewService] LoadMesh failed for {gameMdlPath}");
            return false;
        }

        currentMesh = meshData;
        DebugServer.AppendLog($"[PreviewService] Mesh loaded: {meshData.Vertices.Length} verts, {meshData.TriangleCount} tris");
        return true;
    }

    /// <summary>Update preview — auto-selects full redraw or async in-place GPU swap.</summary>
    public void UpdatePreview(DecalProject project)
    {
        if (config.UseGpuSwap && textureSwap != null && CheckCanSwapInPlace(project))
        {
            StartAsyncInPlace(project);
        }
        else
        {
            UpdatePreviewFull(project);
        }
    }

    /// <summary>Call from Draw() every frame to apply completed async swaps.</summary>
    public unsafe void ApplyPendingSwaps()
    {
        var batch = Interlocked.Exchange(ref pendingBatch, null);
        if (batch == null) return;

        var charBase = textureSwap?.GetLocalPlayerCharacterBase();
        if (charBase == null) return;

        foreach (var entry in batch.Textures)
        {
            var slot = textureSwap!.FindTextureSlot(charBase, entry.GamePath, entry.DiskPath);
            if (slot != null)
                textureSwap.SwapTexture(slot, entry.BgraData, entry.Width, entry.Height);
        }

        foreach (var em in batch.Emissives)
        {
            // Try ColorTable swap first (works for character.shpk etc.)
            // ColorTable swap for character.shpk etc.; skin.shpk handled by EmissiveCBufferHook
            textureSwap!.UpdateEmissiveViaColorTable(charBase, em.MtrlGamePath, em.MtrlDiskPath, em.Color);
            if (emissiveHook != null && em.CBufferOffset > 0)
                emissiveHook.SetTargetByPath(charBase, em.MtrlGamePath, em.MtrlDiskPath, em.Color);
        }

        LastUpdateMode = "inplace";
    }

    /// <summary>Get local player CharacterBase pointer for direct manipulation.</summary>
    public unsafe FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* GetCharacterBase()
        => textureSwap?.GetLocalPlayerCharacterBase();

    /// <summary>Write highlight emissive color via ColorTable texture swap or CBuffer hook.</summary>
    public unsafe void HighlightEmissiveColor(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* charBase,
        TargetGroup group, Vector3 color)
    {
        if (textureSwap == null) return;
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return;

        var mtrlDiskPath = previewMtrlDiskPaths.GetValueOrDefault(group.MtrlGamePath!);
        emissiveOffsets.TryGetValue(group.MtrlGamePath!, out var cbufOffset);
        textureSwap.UpdateEmissiveViaColorTable(charBase, group.MtrlGamePath!, mtrlDiskPath, color);
        // Always set hook target for materials with known CBuffer offset (skin.shpk etc.)
        if (emissiveHook != null && cbufOffset > 0)
            emissiveHook.SetTargetByPath(charBase, group.MtrlGamePath!, mtrlDiskPath, color);
    }

    /// <summary>Force a full Penumbra redraw update (always flickers).</summary>
    public void UpdatePreviewFull(DecalProject project)
    {
        LastUpdateMode = "full";
        DebugServer.AppendLog("[PreviewService] Mode: FULL (Penumbra redraw)");

        try
        {
            var redirects = new Dictionary<string, string>();

            foreach (var group in project.Groups)
            {
                if (string.IsNullOrEmpty(group.DiffuseGamePath)) continue;
                if (group.Layers.Count == 0) continue;

                ProcessGroup(group, redirects);
            }

            if (redirects.Count > 0)
                penumbra.SetTextureRedirects(redirects);

            penumbra.RedrawPlayer();

            // Track initialized paths for future GPU swap
            foreach (var (gamePath, diskPath) in redirects)
            {
                initializedRedirects.Add(gamePath);
                previewDiskPaths[gamePath] = diskPath;
            }

            // Snapshot emissive colors so we detect changes that need full redraw
            foreach (var group in project.Groups)
            {
                if (string.IsNullOrEmpty(group.DiffuseGamePath)) continue;
                if (group.HasEmissiveLayers())
                    lastEmissiveColors[group.DiffuseGamePath] = GetCombinedEmissiveColor(group.Layers);
            }

            DebugServer.AppendLog(
                $"[PreviewService] Full update done ({redirects.Count} redirects, {initializedRedirects.Count} initialized)");
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] Full update exception: {ex.Message}");
            log.Error(ex, "UpdatePreviewFull failed");
        }
    }

    /// <summary>Kick off background compositing, results applied via ApplyPendingSwaps.</summary>
    private void StartAsyncInPlace(DecalProject project)
    {
        // Cancel any previous background work
        asyncCancel?.Cancel();
        asyncCancel = new CancellationTokenSource();
        var token = asyncCancel.Token;

        // Capture all data needed by background thread (avoid touching mutable state later)
        var jobs = new List<(TargetGroup Group, string DiffuseGamePath, string? DiskPath,
            string? NormGamePath, string? NormDiskPath, string? MtrlGamePath, int EmissiveOffset,
            List<LayerSnapshot> Layers, Vector3 EmissiveColor)>();

        foreach (var group in project.Groups)
        {
            if (string.IsNullOrEmpty(group.DiffuseGamePath) || group.Layers.Count == 0)
                continue;
            if (!previewDiskPaths.ContainsKey(group.DiffuseGamePath))
                continue;

            previewDiskPaths.TryGetValue(group.DiffuseGamePath, out var diffDisk);
            previewDiskPaths.TryGetValue(group.NormGamePath ?? "", out var normDisk);

            emissiveOffsets.TryGetValue(group.MtrlGamePath ?? "", out var emOff);

            // Snapshot layer parameters so background thread reads stable data
            var snapshots = new List<LayerSnapshot>();
            foreach (var l in group.Layers)
                snapshots.Add(new LayerSnapshot(l));

            jobs.Add((group, group.DiffuseGamePath, diffDisk,
                group.NormGamePath, normDisk ?? (group.OrigNormDiskPath ?? group.NormDiskPath),
                group.MtrlGamePath, emOff, snapshots,
                GetCombinedEmissiveColor(group.Layers)));
        }

        Task.Run(() =>
        {
            try
            {
                var texEntries = new List<SwapBatchEntry>();
                var emEntries = new List<EmissiveEntry>();

                foreach (var job in jobs)
                {
                    if (token.IsCancellationRequested) return;

                    // Convert snapshots to DecalLayers for compositing
                    var layers = job.Layers.ConvertAll(s => s.ToDecalLayer());

                    // Diffuse composite
                    var baseTex = LoadBaseTexture(job.Group);
                    var rgba = CpuUvComposite(layers, baseTex.Data, baseTex.Width, baseTex.Height);
                    if (rgba != null)
                    {
                        if (job.DiskPath != null)
                            WriteBgraTexFile(job.DiskPath, rgba, baseTex.Width, baseTex.Height);
                        var bgra = TextureSwapService.RgbaToBgra(rgba);
                        texEntries.Add(new SwapBatchEntry(
                            job.DiffuseGamePath, job.DiskPath, bgra, baseTex.Width, baseTex.Height));
                    }

                    // Normal map (emissive mask)
                    if (HasEmissiveLayers(job.Layers) && !string.IsNullOrEmpty(job.NormDiskPath))
                    {
                        var normRgba = CompositeEmissiveNorm(
                            layers, job.NormDiskPath!, baseTex.Width, baseTex.Height);
                        if (normRgba != null)
                        {
                            previewDiskPaths.TryGetValue(job.NormGamePath ?? "", out var normDiskOut);
                            if (normDiskOut != null)
                                WriteBgraTexFile(normDiskOut, normRgba, baseTex.Width, baseTex.Height);
                            var normBgra = TextureSwapService.RgbaToBgra(normRgba);
                            texEntries.Add(new SwapBatchEntry(
                                job.NormGamePath!, normDiskOut, normBgra, baseTex.Width, baseTex.Height));
                        }

                        // Emissive color → ColorTable texture swap or CBuffer write
                        if (!string.IsNullOrEmpty(job.MtrlGamePath))
                        {
                            var mtrlDisk = previewMtrlDiskPaths.GetValueOrDefault(job.MtrlGamePath!);
                            emissiveOffsets.TryGetValue(job.MtrlGamePath!, out var emOff);
                            emEntries.Add(new EmissiveEntry(job.MtrlGamePath!, mtrlDisk, job.EmissiveColor, emOff));
                        }
                    }
                }

                if (!token.IsCancellationRequested)
                    pendingBatch = new SwapBatch(texEntries, emEntries);
            }
            catch (Exception ex)
            {
                DebugServer.AppendLog($"[PreviewService] Async composite exception: {ex.Message}");
            }
        }, token);
    }

    // Snapshot of layer parameters for thread-safe background compositing
    private class LayerSnapshot
    {
        public bool IsVisible, AffectsDiffuse, AffectsEmissive;
        public string? ImagePath;
        public Vector2 UvCenter, UvScale;
        public float RotationDeg, Opacity;
        public BlendMode BlendMode;
        public EmissiveMask EmissiveMask;
        public float EmissiveMaskFalloff;
        public Vector3 EmissiveColor;
        public float EmissiveIntensity;
        public float GradientAngleDeg;
        public float GradientScale;
        public float GradientOffset;

        public LayerSnapshot(DecalLayer l)
        {
            IsVisible = l.IsVisible; AffectsDiffuse = l.AffectsDiffuse; AffectsEmissive = l.AffectsEmissive;
            ImagePath = l.ImagePath; UvCenter = l.UvCenter; UvScale = l.UvScale;
            RotationDeg = l.RotationDeg; Opacity = l.Opacity; BlendMode = l.BlendMode;
            EmissiveMask = l.EmissiveMask; EmissiveMaskFalloff = l.EmissiveMaskFalloff;
            EmissiveColor = l.EmissiveColor; EmissiveIntensity = l.EmissiveIntensity;
            GradientAngleDeg = l.GradientAngleDeg; GradientScale = l.GradientScale;
            GradientOffset = l.GradientOffset;
        }

        public DecalLayer ToDecalLayer() => new()
        {
            IsVisible = IsVisible, AffectsDiffuse = AffectsDiffuse, AffectsEmissive = AffectsEmissive,
            ImagePath = ImagePath, UvCenter = UvCenter, UvScale = UvScale,
            RotationDeg = RotationDeg, Opacity = Opacity, BlendMode = BlendMode,
            EmissiveMask = EmissiveMask, EmissiveMaskFalloff = EmissiveMaskFalloff,
            EmissiveColor = EmissiveColor, EmissiveIntensity = EmissiveIntensity,
            GradientAngleDeg = GradientAngleDeg, GradientScale = GradientScale,
            GradientOffset = GradientOffset,
        };
    }

    private static bool HasEmissiveLayers(List<LayerSnapshot> layers)
    {
        foreach (var l in layers)
            if (l.IsVisible && l.AffectsEmissive && !string.IsNullOrEmpty(l.ImagePath))
                return true;
        return false;
    }

    /// <summary>Clear emissive hook targets (e.g. when emissive is disabled).</summary>
    public void ClearEmissiveHookTargets() => emissiveHook?.ClearTargets();

    /// <summary>Clear GPU swap state, forcing next update to do full redraw.</summary>
    public void ResetSwapState()
    {
        asyncCancel?.Cancel();
        pendingBatch = null;
        initializedRedirects.Clear();
        previewDiskPaths.Clear();
        emissiveOffsets.Clear();
        previewMtrlDiskPaths.Clear();
        lastEmissiveColors.Clear();
        emissiveHook?.ClearTargets();
        CanSwapInPlace = false;
        LastUpdateMode = "none";
        DebugServer.AppendLog("[PreviewService] GPU swap state reset");
    }

    // ── Private: check if all groups can swap in-place ───────────────────────

    private bool CheckCanSwapInPlace(DecalProject project)
    {
        if (initializedRedirects.Count == 0)
        {
            CanSwapInPlace = false;
            return false;
        }

        foreach (var group in project.Groups)
        {
            if (string.IsNullOrEmpty(group.DiffuseGamePath) || group.Layers.Count == 0)
                continue;

            // If this group never produced a redirect (no visible output),
            // it doesn't need to be initialized — skip it.
            if (!previewDiskPaths.ContainsKey(group.DiffuseGamePath))
                continue;

            if (!initializedRedirects.Contains(group.DiffuseGamePath))
            {
                DebugServer.AppendLog(
                    $"[PreviewService] CanSwap=NO diffuse not initialized: {group.DiffuseGamePath}");
                CanSwapInPlace = false;
                return false;
            }

            if (group.HasEmissiveLayers())
            {
                if (!string.IsNullOrEmpty(group.NormGamePath)
                    && previewDiskPaths.ContainsKey(group.NormGamePath)
                    && !initializedRedirects.Contains(group.NormGamePath))
                {
                    DebugServer.AppendLog(
                        $"[PreviewService] CanSwap=NO norm not initialized: {group.NormGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }

                if (!string.IsNullOrEmpty(group.MtrlGamePath)
                    && previewDiskPaths.ContainsKey(group.MtrlGamePath)
                    && !initializedRedirects.Contains(group.MtrlGamePath))
                {
                    DebugServer.AppendLog(
                        $"[PreviewService] CanSwap=NO mtrl not initialized: {group.MtrlGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }

            }
        }

        CanSwapInPlace = true;
        return true;
    }

    // ── Private: shared helpers ──────────────────────────────────────────────

    private (byte[] Data, int Width, int Height) LoadBaseTexture(TargetGroup group)
    {
        var diffuseDisk = group.OrigDiffuseDiskPath ?? group.DiffuseDiskPath;
        if (string.IsNullOrEmpty(diffuseDisk))
        {
            int res = config.TextureResolution;
            return (new byte[res * res * 4], res, res);
        }

        // Return cached version if available
        if (baseTextureCache.TryGetValue(diffuseDisk, out var cached))
            return cached;

        var baseTex = LoadTexture(diffuseDisk);
        if (baseTex != null)
        {
            baseTextureCache[diffuseDisk] = baseTex.Value;
            return baseTex.Value;
        }

        int r = config.TextureResolution;
        var fallback = (new byte[r * r * 4], r, r);
        baseTextureCache[diffuseDisk] = fallback;
        return fallback;
    }

    // ── Existing methods (unchanged) ─────────────────────────────────────────

    private void ProcessGroup(TargetGroup group, Dictionary<string, string> redirects)
    {
        var baseTex = LoadBaseTexture(group);
        int w = baseTex.Width, h = baseTex.Height;

        // Diffuse composite
        var diffResult = CpuUvComposite(group.Layers, baseTex.Data, w, h);
        if (diffResult != null)
        {
            var safeName = MakeSafeFileName(group.Name);
            var path = Path.Combine(outputDir, $"preview_{safeName}_d.tex");
            WriteBgraTexFile(path, diffResult, w, h);
            redirects[group.DiffuseGamePath!] = path;
            DebugServer.AppendLog($"[PreviewService] Diffuse → {group.DiffuseGamePath}");
        }

        // Emissive: modify .mtrl + write emissive area into normal map alpha
        var hasEmissive = group.HasEmissiveLayers();
        if (hasEmissive && !string.IsNullOrEmpty(group.MtrlGamePath))
        {
            var emissiveColor = GetCombinedEmissiveColor(group.Layers);
            var safeName = MakeSafeFileName(group.Name);
            var mtrlOutPath = Path.Combine(outputDir, $"preview_{safeName}.mtrl");
            var mtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;

            if (TryBuildEmissiveMtrl(mtrlDisk ?? group.MtrlGamePath!, mtrlOutPath, emissiveColor, out var emOffset))
            {
                redirects[group.MtrlGamePath!] = mtrlOutPath;
                previewMtrlDiskPaths[group.MtrlGamePath!] = mtrlOutPath;
                if (emOffset >= 0)
                    emissiveOffsets[group.MtrlGamePath!] = emOffset;
                DebugServer.AppendLog($"[PreviewService] Mtrl (emissive) → {group.MtrlGamePath} cbufOffset={emOffset}");
            }

            // Write emissive area into normal map alpha channel
            if (!string.IsNullOrEmpty(group.NormGamePath) && !string.IsNullOrEmpty(group.NormDiskPath))
            {
                var normDisk = group.OrigNormDiskPath ?? group.NormDiskPath;
                var normResult = CompositeEmissiveNorm(group.Layers, normDisk!, w, h);
                if (normResult != null)
                {
                    var normPath = Path.Combine(outputDir, $"preview_{safeName}_n.tex");
                    WriteBgraTexFile(normPath, normResult, w, h);
                    redirects[group.NormGamePath!] = normPath;
                    DebugServer.AppendLog($"[PreviewService] Norm (emissive alpha) → {group.NormGamePath}");
                }
            }
        }
    }

    private static string MakeSafeFileName(string name)
    {
        // Use ASCII-only hash to avoid encoding mismatch between disk path
        // and game ResourceHandle.FileName (which garbles non-ASCII characters)
        var hash = (uint)name.GetHashCode(StringComparison.Ordinal);
        return $"g{hash:X8}";
    }

    private (byte[] Data, int Width, int Height)? LoadTexture(string? diskPath)
    {
        if (string.IsNullOrEmpty(diskPath)) return null;
        var img = File.Exists(diskPath) ? imageLoader.LoadImage(diskPath) : LoadGameTexture(diskPath);
        return img;
    }

    /// <summary>
    /// Composite emissive area into normal map alpha channel.
    /// </summary>
    private byte[]? CompositeEmissiveNorm(List<DecalLayer> layers, string normDiskPath, int w, int h)
    {
        byte[] baseNorm;

        // Cache the base normal texture
        if (baseTextureCache.TryGetValue(normDiskPath, out var cachedNorm))
        {
            var (data, iw, ih) = cachedNorm;
            baseNorm = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
        }
        else
        {
            var normImg = File.Exists(normDiskPath) ? imageLoader.LoadImage(normDiskPath) : LoadGameTexture(normDiskPath);
            if (normImg != null)
            {
                baseTextureCache[normDiskPath] = normImg.Value;
                var (data, iw, ih) = normImg.Value;
                baseNorm = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
            }
            else
            {
                baseNorm = new byte[w * h * 4];
            }
        }

        var output = (byte[])baseNorm.Clone();

        // Zero out alpha channel everywhere
        for (int i = 3; i < output.Length; i += 4)
            output[i] = 0;

        bool anyEmissive = false;
        foreach (var layer in layers)
        {
            if (!layer.IsVisible || !layer.AffectsEmissive || string.IsNullOrEmpty(layer.ImagePath)) continue;

            var decalImage = imageLoader.LoadImage(layer.ImagePath);
            if (decalImage == null) continue;

            var (decalData, decalW, decalH) = decalImage.Value;
            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var opacity = layer.Opacity;
            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
            var cosR = MathF.Cos(rotRad);
            var sinR = MathF.Sin(rotRad);

            int pxMin = Math.Max(0, (int)((center.X - scale.X / 2f) * w));
            int pxMax = Math.Min(w - 1, (int)((center.X + scale.X / 2f) * w));
            int pyMin = Math.Max(0, (int)((center.Y - scale.Y / 2f) * h));
            int pyMax = Math.Min(h - 1, (int)((center.Y + scale.Y / 2f) * h));

            for (int py = pyMin; py <= pyMax; py++)
            {
                for (int px = pxMin; px <= pxMax; px++)
                {
                    float u = (px + 0.5f) / w;
                    float v = (py + 0.5f) / h;
                    float lu = (u - center.X) / scale.X;
                    float lv = (v - center.Y) / scale.Y;
                    float ru = lu * cosR + lv * sinR;
                    float rv = -lu * sinR + lv * cosR;
                    if (ru < -0.5f || ru > 0.5f || rv < -0.5f || rv > 0.5f) continue;

                    float du = (ru + 0.5f) * decalW - 0.5f;
                    float dv = (rv + 0.5f) * decalH - 0.5f;
                    SampleBilinear(decalData, decalW, decalH, du, dv, out _, out _, out _, out float da);
                    da *= opacity;
                    if (da < 0.001f) continue;

                    float maskValue;
                    if (layer.EmissiveMask == EmissiveMask.DirectionalGradient)
                        maskValue = ComputeDirectionalGradient(ru, rv, da,
                            layer.GradientAngleDeg, layer.GradientScale, layer.EmissiveMaskFalloff, layer.GradientOffset);
                    else if (layer.EmissiveMask == EmissiveMask.ShapeOutline)
                    {
                        // Sample neighbors to detect alpha edges
                        float step = 1f / MathF.Max(decalW, decalH);
                        float sum = 0; int cnt = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            float ndu = du + dx; float ndv = dv + dy;
                            SampleBilinear(decalData, decalW, decalH, ndu, ndv, out _, out _, out _, out float na);
                            sum += na * opacity; cnt++;
                        }
                        maskValue = ComputeShapeOutline(da, layer.EmissiveMaskFalloff, sum / cnt);
                    }
                    else
                        maskValue = ComputeEmissiveMask(layer.EmissiveMask, layer.EmissiveMaskFalloff, ru, rv, da);

                    int oIdx = (py * w + px) * 4;
                    byte emByte = (byte)Math.Clamp((int)(maskValue * 255), 0, 255);
                    output[oIdx + 3] = (byte)Math.Max(output[oIdx + 3], emByte);
                }
            }

            anyEmissive = true;
        }

        return anyEmissive ? output : null;
    }

    private static Vector3 GetCombinedEmissiveColor(List<DecalLayer> layers)
    {
        var color = Vector3.Zero;
        foreach (var layer in layers)
        {
            if (!layer.IsVisible || !layer.AffectsEmissive || string.IsNullOrEmpty(layer.ImagePath)) continue;
            color += layer.EmissiveColor * layer.EmissiveIntensity;
        }
        return color;
    }

    private bool TryBuildEmissiveMtrl(string mtrlPath, string outputPath, Vector3 emissiveColor, out int emissiveByteOffset)
    {
        emissiveByteOffset = -1;
        try
        {
            byte[] mtrlBytes;
            MtrlFile mtrl;

            if (File.Exists(mtrlPath))
            {
                mtrlBytes = File.ReadAllBytes(mtrlPath);
            }
            else
            {
                var pack = meshExtractor.GetSqPackInstance();
                if (pack == null) return false;
                var sqResult = pack.GetFile(mtrlPath);
                if (sqResult == null) return false;
                mtrlBytes = sqResult.Value.file.RawData.ToArray();
            }

            var tempPath = Path.Combine(outputDir, "temp_orig.mtrl");
            File.WriteAllBytes(tempPath, mtrlBytes);
            var lumina = meshExtractor.GetLuminaForDisk();
            mtrl = lumina!.GetFileFromDisk<MtrlFile>(tempPath);
            try { File.Delete(tempPath); } catch { }

            DebugServer.AppendLog($"[PreviewService] Loaded mtrl: {mtrlPath} ({mtrlBytes.Length} bytes)");
            return MtrlFileWriter.WriteEmissiveMtrl(mtrl, mtrlBytes, outputPath, emissiveColor, out emissiveByteOffset);
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] Emissive mtrl build failed: {ex.Message}");
            return false;
        }
    }

    private (byte[] Data, int Width, int Height)? LoadGameTexture(string gamePath)
    {
        try
        {
            var pack = meshExtractor.GetSqPackInstance();
            if (pack == null) return null;
            var sqResult = pack.GetFile(gamePath);
            if (sqResult == null) return null;

            var rawBytes = sqResult.Value.file.RawData.ToArray();
            var tempPath = Path.Combine(outputDir, "temp_base.tex");
            File.WriteAllBytes(tempPath, rawBytes);
            var result = imageLoader.LoadImage(tempPath);
            try { File.Delete(tempPath); } catch { }
            return result;
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] Game texture load failed: {ex.Message}");
            return null;
        }
    }

    private byte[]? CpuUvComposite(List<DecalLayer> layers, byte[] baseRgba, int w, int h)
    {
        var output = (byte[])baseRgba.Clone();
        int processedLayers = 0;

        foreach (var layer in layers)
        {
            if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;
            if (!layer.AffectsDiffuse) continue;

            var decalImage = imageLoader.LoadImage(layer.ImagePath);
            if (decalImage == null) continue;

            var (decalData, decalW, decalH) = decalImage.Value;
            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var opacity = layer.Opacity;
            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
            var cosR = MathF.Cos(rotRad);
            var sinR = MathF.Sin(rotRad);

            int pxMin = Math.Max(0, (int)((center.X - scale.X / 2f) * w));
            int pxMax = Math.Min(w - 1, (int)((center.X + scale.X / 2f) * w));
            int pyMin = Math.Max(0, (int)((center.Y - scale.Y / 2f) * h));
            int pyMax = Math.Min(h - 1, (int)((center.Y + scale.Y / 2f) * h));

            int hitPixels = 0;

            for (int py = pyMin; py <= pyMax; py++)
            {
                for (int px = pxMin; px <= pxMax; px++)
                {
                    float u = (px + 0.5f) / w;
                    float v = (py + 0.5f) / h;
                    float lu = (u - center.X) / scale.X;
                    float lv = (v - center.Y) / scale.Y;
                    float ru = lu * cosR + lv * sinR;
                    float rv = -lu * sinR + lv * cosR;
                    if (ru < -0.5f || ru > 0.5f || rv < -0.5f || rv > 0.5f) continue;

                    float du = (ru + 0.5f) * decalW - 0.5f;
                    float dv = (rv + 0.5f) * decalH - 0.5f;

                    SampleBilinear(decalData, decalW, decalH, du, dv,
                        out float dr, out float dg, out float db, out float da);
                    da *= opacity;

                    if (da < 0.001f) continue;

                    int oIdx = (py * w + px) * 4;
                    float br = output[oIdx] / 255f;
                    float bg = output[oIdx + 1] / 255f;
                    float bb = output[oIdx + 2] / 255f;

                    output[oIdx]     = (byte)Math.Clamp((int)((dr * da + br * (1 - da)) * 255), 0, 255);
                    output[oIdx + 1] = (byte)Math.Clamp((int)((dg * da + bg * (1 - da)) * 255), 0, 255);
                    output[oIdx + 2] = (byte)Math.Clamp((int)((db * da + bb * (1 - da)) * 255), 0, 255);
                    output[oIdx + 3] = 255;

                    hitPixels++;
                }
            }

            DebugServer.AppendLog($"[PreviewService] UV composite: layer '{layer.Name}' hit {hitPixels} pixels");
            processedLayers++;
        }

        if (processedLayers == 0)
        {
            DebugServer.AppendLog("[PreviewService] No visible layers with images");
            return null;
        }

        return output;
    }

    private static void WriteBgraTexFile(string path, byte[] rgbaData, int width, int height)
    {
        // Retry with backoff — game may hold a read lock on the file
        FileStream? fs = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                break;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }
        if (fs == null) return;
        using var _ = fs;
        using var bw = new BinaryWriter(fs);

        bw.Write(0x00800000u);
        bw.Write(0x1450u);
        bw.Write((ushort)width);
        bw.Write((ushort)height);
        bw.Write((ushort)1);
        bw.Write((ushort)1);
        bw.Write(0u); bw.Write(0u); bw.Write(0u);
        bw.Write(80u);
        for (int i = 1; i < 13; i++) bw.Write(0u);

        for (int i = 0; i < rgbaData.Length; i += 4)
        {
            bw.Write(rgbaData[i + 2]); // B
            bw.Write(rgbaData[i + 1]); // G
            bw.Write(rgbaData[i + 0]); // R
            bw.Write(rgbaData[i + 3]); // A
        }
    }

    private static void SampleBilinear(byte[] data, int w, int h, float fx, float fy,
        out float r, out float g, out float b, out float a)
    {
        int x0 = Math.Clamp((int)MathF.Floor(fx), 0, w - 1);
        int y0 = Math.Clamp((int)MathF.Floor(fy), 0, h - 1);
        int x1 = Math.Min(x0 + 1, w - 1);
        int y1 = Math.Min(y0 + 1, h - 1);
        float tx = fx - MathF.Floor(fx);
        float ty = fy - MathF.Floor(fy);

        int i00 = (y0 * w + x0) * 4;
        int i10 = (y0 * w + x1) * 4;
        int i01 = (y1 * w + x0) * 4;
        int i11 = (y1 * w + x1) * 4;

        float Lerp(int ch) =>
            (data[i00 + ch] * (1 - tx) + data[i10 + ch] * tx) * (1 - ty) +
            (data[i01 + ch] * (1 - tx) + data[i11 + ch] * tx) * ty;

        r = Lerp(0) / 255f;
        g = Lerp(1) / 255f;
        b = Lerp(2) / 255f;
        a = Lerp(3) / 255f;
    }

    /// <summary>
    /// Compute emissive mask value based on position within the decal.
    /// </summary>
    public static float ComputeEmissiveMask(EmissiveMask mask, float falloff, float ru, float rv, float da)
    {
        if (mask == Core.EmissiveMask.Uniform)
            return da;

        float dist = MathF.Sqrt(ru * ru + rv * rv) * 2f;
        dist = MathF.Min(dist, 1f);

        float edgeDist = MathF.Min(0.5f - MathF.Abs(ru), 0.5f - MathF.Abs(rv));
        edgeDist = MathF.Max(edgeDist, 0f) * 2f;

        float f = MathF.Max(falloff, 0.01f);
        float m;

        switch (mask)
        {
            case Core.EmissiveMask.RadialFadeOut:
                m = 1f - Smoothstep(0f, f, dist);
                break;
            case Core.EmissiveMask.RadialFadeIn:
                m = Smoothstep(1f - f, 1f, dist);
                break;
            case Core.EmissiveMask.EdgeGlow:
                m = 1f - Smoothstep(0f, f, edgeDist);
                break;
            case Core.EmissiveMask.GaussianFeather:
                float sigma = MathF.Max(f, 0.01f) * 0.5f;
                float gEdge = MathF.Min(0.5f - MathF.Abs(ru), 0.5f - MathF.Abs(rv));
                gEdge = MathF.Max(gEdge, 0f);
                m = 1f - MathF.Exp(-(gEdge * gEdge) / (2f * sigma * sigma));
                break;
            default:
                m = 1f;
                break;
        }

        return da * m;
    }

    /// <summary>Compute directional gradient mask with angle, scale and offset parameters.</summary>
    public static float ComputeDirectionalGradient(float ru, float rv, float da,
        float angleDeg, float scale, float falloff, float offset)
    {
        float rad = angleDeg * (MathF.PI / 180f);
        float cosA = MathF.Cos(rad);
        float sinA = MathF.Sin(rad);

        float projected = ru * cosA + rv * sinA;
        float s = MathF.Max(scale, 0.01f);
        float t = (projected / s + 0.5f + offset);
        t = MathF.Max(0f, MathF.Min(1f, t));

        float f = MathF.Max(falloff, 0.01f);
        float m = Smoothstep(0.5f - f * 0.5f, 0.5f + f * 0.5f, t);

        return da * m;
    }

    /// <summary>Compute shape outline mask — glow along decal alpha edges (like PS outer glow).</summary>
    public static float ComputeShapeOutline(float da, float falloff, float neighborAvgAlpha)
    {
        // Edge = where alpha differs from neighbors.
        // da > 0 and neighborAvgAlpha < da means we're near an edge inside the decal.
        // We want: glow strongest at decal alpha boundary, fading inward.
        float edgeStrength = MathF.Abs(da - neighborAvgAlpha);
        float f = MathF.Max(falloff, 0.01f);
        float m = Smoothstep(0f, f, edgeStrength);
        return da * m;
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = MathF.Max(0f, MathF.Min(1f, (x - edge0) / (edge1 - edge0 + 1e-6f)));
        return t * t * (3f - 2f * t);
    }

    private static byte[] ResizeBilinear(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        for (var y = 0; y < dstH; y++)
        {
            float fy = (y + 0.5f) * srcH / dstH - 0.5f;
            for (var x = 0; x < dstW; x++)
            {
                float fx = (x + 0.5f) * srcW / dstW - 0.5f;
                SampleBilinear(src, srcW, srcH, fx, fy, out float r, out float g, out float b, out float a);
                var i = (y * dstW + x) * 4;
                dst[i]     = (byte)Math.Clamp((int)(r * 255 + 0.5f), 0, 255);
                dst[i + 1] = (byte)Math.Clamp((int)(g * 255 + 0.5f), 0, 255);
                dst[i + 2] = (byte)Math.Clamp((int)(b * 255 + 0.5f), 0, 255);
                dst[i + 3] = (byte)Math.Clamp((int)(a * 255 + 0.5f), 0, 255);
            }
        }
        return dst;
    }

    public void ClearTextureCache()
    {
        baseTextureCache.Clear();
        DebugServer.AppendLog("[PreviewService] Texture cache cleared");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
    }
}
