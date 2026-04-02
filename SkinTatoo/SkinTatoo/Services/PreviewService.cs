using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Plugin.Services;
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
    private readonly IPluginLog log;
    private readonly Configuration config;

    private MeshData? currentMesh;
    private readonly string outputDir;
    private bool disposed;

    // Cached original textures — loaded once per path, reused for every preview
    private byte[]? cachedBaseRgba;
    private string? cachedBasePath;
    private int cachedBaseW, cachedBaseH;
    private byte[]? cachedNormRgba;
    private string? cachedNormPath;
    private byte[]? cachedMaskRgba;
    private string? cachedMaskPath;

    public bool IsReady => currentMesh != null;

    public void ClearTextureCache()
    {
        cachedBaseRgba = null; cachedBasePath = null; cachedBaseW = 0; cachedBaseH = 0;
        cachedNormRgba = null; cachedNormPath = null;
        cachedMaskRgba = null; cachedMaskPath = null;
        DebugServer.AppendLog("[PreviewService] Texture cache cleared");
    }
    public MeshData? CurrentMesh => currentMesh;

    public PreviewService(
        MeshExtractor meshExtractor,
        DecalImageLoader imageLoader,
        PenumbraBridge penumbra,
        IPluginLog log,
        Configuration config,
        string outputDir)
    {
        this.meshExtractor = meshExtractor;
        this.imageLoader = imageLoader;
        this.penumbra = penumbra;
        this.log = log;
        this.config = config;
        this.outputDir = outputDir;

        Directory.CreateDirectory(outputDir);
    }

    // Synchronous — must be called from a thread that can access Dalamud IDataManager
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
            var msg = $"LoadMesh exception: {ex.Message}";
            log.Error(ex, msg);
            DebugServer.AppendLog($"[PreviewService] {msg}");
            return false;
        }

        if (meshData == null)
        {
            var msg = $"LoadMesh failed: ExtractMesh returned null for {gameMdlPath}";
            log.Error(msg);
            DebugServer.AppendLog($"[PreviewService] {msg}");
            return false;
        }

        currentMesh = meshData;

        var info = $"Mesh loaded: {meshData.Vertices.Length} verts, {meshData.TriangleCount} tris";
        log.Information(info);
        DebugServer.AppendLog($"[PreviewService] {info}");
        return true;
    }

    public record TextureTargets(
        string? DiffuseGamePath, string? DiffuseDiskPath,
        string? NormGamePath, string? NormDiskPath,
        string? MaskGamePath, string? MaskDiskPath);

    public string? UpdatePreview(DecalProject project, TextureTargets targets)
    {
        DebugServer.AppendLog($"[PreviewService] UpdatePreview: layers={project.Layers.Count}");

        if (!IsReady)
        {
            DebugServer.AppendLog("[PreviewService] Not ready");
            return null;
        }

        // Load base texture at native resolution — don't force resize
        var baseTex = LoadAndCacheNative(ref cachedBaseRgba, ref cachedBasePath, ref cachedBaseW, ref cachedBaseH, targets.DiffuseDiskPath, "diffuse");
        int w = cachedBaseW > 0 ? cachedBaseW : config.TextureResolution;
        int h = cachedBaseH > 0 ? cachedBaseH : config.TextureResolution;

        var normTex = LoadAndCache(ref cachedNormRgba, ref cachedNormPath, targets.NormDiskPath, w, h, "normal");
        var maskTex = LoadAndCache(ref cachedMaskRgba, ref cachedMaskPath, targets.MaskDiskPath, w, h, "mask");

        try
        {
            // Composite diffuse
            var diffResult = CpuUvComposite(project, baseTex, w, h, "diffuse");

            // Composite mask (glow/specular)
            var maskResult = CpuUvCompositeMask(project, maskTex, w, h);

            // Collect all redirects and apply in a single Penumbra call
            // (multiple calls with the same tag would overwrite each other)
            var redirects = new Dictionary<string, string>();

            if (diffResult != null && !string.IsNullOrEmpty(targets.DiffuseGamePath))
            {
                var path = Path.Combine(outputDir, "preview_d.tex");
                WriteBgraTexFile(path, diffResult, w, h);
                redirects[targets.DiffuseGamePath] = path;
                DebugServer.AppendLog($"[PreviewService] Diffuse → {targets.DiffuseGamePath}");
            }

            if (maskResult != null && !string.IsNullOrEmpty(targets.MaskGamePath))
            {
                var path = Path.Combine(outputDir, "preview_m.tex");
                WriteBgraTexFile(path, maskResult, w, h);
                redirects[targets.MaskGamePath] = path;
                DebugServer.AppendLog($"[PreviewService] Mask → {targets.MaskGamePath}");
            }

            if (redirects.Count > 0)
                penumbra.SetTextureRedirects(redirects);

            penumbra.RedrawPlayer();
            DebugServer.AppendLog($"[PreviewService] Preview updated ({redirects.Count} redirects)");
            return Path.Combine(outputDir, "preview_d.tex");
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] UpdatePreview exception: {ex.Message}");
            log.Error(ex, "UpdatePreview failed");
            return null;
        }
    }


    // Load texture from game path — try sqpack via Meddle, then decode with Lumina
    private (byte[] Data, int Width, int Height)? LoadGameTexture(string gamePath)
    {
        try
        {
            var pack = meshExtractor.GetSqPackInstance();
            if (pack == null) return null;

            var sqResult = pack.GetFile(gamePath);
            if (sqResult == null)
            {
                DebugServer.AppendLog($"[PreviewService] Game texture not found in sqpack: {gamePath}");
                return null;
            }

            var rawBytes = sqResult.Value.file.RawData.ToArray();
            DebugServer.AppendLog($"[PreviewService] Game texture raw: {rawBytes.Length} bytes");

            // Write to temp file, then use Lumina GetFileFromDisk to decode BC compression
            var tempPath = Path.Combine(outputDir, "temp_base.tex");
            File.WriteAllBytes(tempPath, rawBytes);
            var result = imageLoader.LoadImage(tempPath);
            try { File.Delete(tempPath); } catch { }

            if (result != null)
                DebugServer.AppendLog($"[PreviewService] Game texture decoded: {result.Value.Width}x{result.Value.Height}");

            return result;
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] Game texture load failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Load texture at native resolution (no resize)
    private byte[] LoadAndCacheNative(ref byte[]? cached, ref string? cachedPath, ref int cachedW, ref int cachedH, string? diskPath, string label)
    {
        if (cached != null)
            return cached;

        cachedPath = diskPath;

        if (!string.IsNullOrEmpty(diskPath))
        {
            var img = File.Exists(diskPath)
                ? imageLoader.LoadImage(diskPath)
                : LoadGameTexture(diskPath);

            if (img != null)
            {
                var (data, iw, ih) = img.Value;
                cached = data;
                cachedW = iw;
                cachedH = ih;
                DebugServer.AppendLog($"[PreviewService] Cached {label} (native): {iw}x{ih}");
            }
        }

        if (cached == null)
        {
            cachedW = config.TextureResolution;
            cachedH = config.TextureResolution;
            cached = new byte[cachedW * cachedH * 4];
        }
        return cached;
    }

    private byte[] LoadAndCache(ref byte[]? cached, ref string? cachedPath, string? diskPath, int w, int h, string label)
    {
        if (cached != null)
            return cached;

        cachedPath = diskPath;

        if (!string.IsNullOrEmpty(diskPath))
        {
            var img = File.Exists(diskPath)
                ? imageLoader.LoadImage(diskPath)
                : LoadGameTexture(diskPath);

            if (img != null)
            {
                var (data, iw, ih) = img.Value;
                cached = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : data;
                DebugServer.AppendLog($"[PreviewService] Cached {label}: {iw}x{ih} → {w}x{h}");
            }
        }

        cached ??= new byte[w * h * 4];
        return cached;
    }

    // UV-space decal compositing — places decal directly onto UV map
    private byte[]? CpuUvComposite(DecalProject project, byte[] baseRgba, int w, int h, string channel = "diffuse")
    {
        var output = (byte[])baseRgba.Clone();
        int processedLayers = 0;

        foreach (var layer in project.Layers)
        {
            if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;
            if (channel == "diffuse" && !layer.AffectsDiffuse) continue;

            var decalImage = imageLoader.LoadImage(layer.ImagePath);
            if (decalImage == null) continue;

            var (decalData, decalW, decalH) = decalImage.Value;
            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var opacity = layer.Opacity;
            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
            var cosR = MathF.Cos(rotRad);
            var sinR = MathF.Sin(rotRad);

            // Decal covers UV rect: center +/- scale/2
            float uMin = center.X - scale.X / 2f;
            float uMax = center.X + scale.X / 2f;
            float vMin = center.Y - scale.Y / 2f;
            float vMax = center.Y + scale.Y / 2f;

            // Pixel range in output texture
            int pxMin = Math.Max(0, (int)(uMin * w));
            int pxMax = Math.Min(w - 1, (int)(uMax * w));
            int pyMin = Math.Max(0, (int)(vMin * h));
            int pyMax = Math.Min(h - 1, (int)(vMax * h));

            int hitPixels = 0;

            for (int py = pyMin; py <= pyMax; py++)
            {
                for (int px = pxMin; px <= pxMax; px++)
                {
                    // Current UV coordinate
                    float u = (px + 0.5f) / w;
                    float v = (py + 0.5f) / h;

                    // Transform to decal-local coordinates [-0.5, 0.5]
                    float lu = (u - center.X) / scale.X;
                    float lv = (v - center.Y) / scale.Y;

                    // Apply rotation
                    float ru = lu * cosR + lv * sinR;
                    float rv = -lu * sinR + lv * cosR;

                    // Check if within decal bounds [-0.5, 0.5]
                    if (ru < -0.5f || ru > 0.5f || rv < -0.5f || rv > 0.5f) continue;

                    // Map to decal image coordinates and bilinear sample
                    float du = (ru + 0.5f) * decalW - 0.5f;
                    float dv = (rv + 0.5f) * decalH - 0.5f;

                    SampleBilinear(decalData, decalW, decalH, du, dv,
                        out float dr, out float dg, out float db, out float da);
                    da *= opacity;

                    if (da < 0.001f) continue;

                    // Alpha blend
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

            DebugServer.AppendLog($"[PreviewService] UV composite: layer '{layer.Name}' hit {hitPixels} pixels, center=({center.X:F2},{center.Y:F2}) scale=({scale.X:F2},{scale.Y:F2})");
            processedLayers++;
        }

        if (processedLayers == 0)
        {
            DebugServer.AppendLog("[PreviewService] No visible layers with images");
            return null;
        }

        return output;
    }

    // UV-space mask compositing — writes specular/smoothness to the decal area
    private byte[]? CpuUvCompositeMask(DecalProject project, byte[] baseMask, int w, int h)
    {
        var output = (byte[])baseMask.Clone();
        bool anyMask = false;

        foreach (var layer in project.Layers)
        {
            if (!layer.IsVisible || !layer.AffectsMask || string.IsNullOrEmpty(layer.ImagePath)) continue;

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

            byte specByte = (byte)Math.Clamp((int)(layer.GlowSpecular * 255), 0, 255);
            byte smoothByte = (byte)Math.Clamp((int)((1f - layer.GlowSmoothness) * 255), 0, 255); // roughness = 1 - smoothness

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

                    // Use decal alpha to determine glow area
                    int sx = Math.Clamp((int)((ru + 0.5f) * decalW), 0, decalW - 1);
                    int sy = Math.Clamp((int)((rv + 0.5f) * decalH), 0, decalH - 1);
                    float da = (decalData[(sy * decalW + sx) * 4 + 3] / 255f) * opacity;
                    if (da < 0.001f) continue;

                    // Mask format: R=specular, G=roughness, B=SSS
                    int oIdx = (py * w + px) * 4;
                    output[oIdx] = (byte)Math.Clamp((int)(specByte * da + output[oIdx] * (1 - da)), 0, 255);       // R: specular
                    output[oIdx + 1] = (byte)Math.Clamp((int)(smoothByte * da + output[oIdx + 1] * (1 - da)), 0, 255); // G: roughness
                    // B (SSS) and A unchanged
                }
            }

            DebugServer.AppendLog($"[PreviewService] Mask composite: layer '{layer.Name}', spec={layer.GlowSpecular:F2}, smooth={layer.GlowSmoothness:F2}");
            anyMask = true;
        }

        return anyMask ? output : null;
    }

    // Write BGRA .tex file directly from RGBA byte array
    private static void WriteBgraTexFile(string path, byte[] rgbaData, int width, int height)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var bw = new BinaryWriter(fs);

        bw.Write(0x00800000u); // TextureType2D
        bw.Write(0x1450u);     // B8G8R8A8 format
        bw.Write((ushort)width);
        bw.Write((ushort)height);
        bw.Write((ushort)1);   // depth
        bw.Write((ushort)1);   // mip count
        bw.Write(0u); bw.Write(0u); bw.Write(0u); // LOD offsets
        bw.Write(80u); // surface 0 offset
        for (int i = 1; i < 13; i++) bw.Write(0u);

        // Write pixels as BGRA
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

    private static byte[] ResizeBilinear(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        for (var y = 0; y < dstH; y++)
        {
            float fy = (y + 0.5f) * srcH / dstH - 0.5f;
            for (var x = 0; x < dstW; x++)
            {
                float fx = (x + 0.5f) * srcW / dstW - 0.5f;
                SampleBilinear(src, srcW, srcH, fx, fy,
                    out float r, out float g, out float b, out float a);
                var dstIdx = (y * dstW + x) * 4;
                dst[dstIdx]     = (byte)Math.Clamp((int)(r * 255 + 0.5f), 0, 255);
                dst[dstIdx + 1] = (byte)Math.Clamp((int)(g * 255 + 0.5f), 0, 255);
                dst[dstIdx + 2] = (byte)Math.Clamp((int)(b * 255 + 0.5f), 0, 255);
                dst[dstIdx + 3] = (byte)Math.Clamp((int)(a * 255 + 0.5f), 0, 255);
            }
        }
        return dst;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
    }
}
