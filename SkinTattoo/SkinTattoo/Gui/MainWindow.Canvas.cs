using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using SkinTattoo.Core;
using SkinTattoo.Services;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public partial class MainWindow
{
    // UV mesh wireframe toggle
    private bool showUvWireframe;

    // GPU wrap cache for the canvas's vanilla / mod-source base texture. The canvas is
    // a placement guide -- decal previews come from DrawLayerOverlays, not the staged
    // preview file. Showing the staged file as the base produced visual doubling during
    // drags: the live overlay leads the staged composite (file write throttled to 4 Hz,
    // and even an in-memory composite via compositeResults lags one async cycle behind
    // the live UvCenter), so user sees baked-old-pos + overlay-new-pos until the source
    // catches up. Reading vanilla bytes via PreviewService.TryGetBaseTexture (Lumina /
    // disk, NOT Penumbra-resolved) keeps the canvas base perfectly stable while the
    // overlay is the sole decal preview -- always in lockstep with itself.
    private sealed record CanvasBaseWrapEntry(IDalamudTextureWrap Wrap, byte[] Data, int Width, int Height);
    private readonly Dictionary<string, CanvasBaseWrapEntry> canvasBaseWrapCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-layer projector overlay cache: mesh-aware rasterization of the decal
    // image into UV space. Re-rasterizes when any projector param / decal image
    // changes. Transparent decal pixels stay transparent in the canvas overlay so
    // multi-UV-island scatter shows up correctly without bleeding into untouched
    // regions.
    private sealed class ProjectorOverlayEntry
    {
        public long Hash;
        public byte[]? Data;
        public int Width;
        public int Height;
        public IDalamudTextureWrap? Wrap;
    }
    private readonly Dictionary<DecalLayer, ProjectorOverlayEntry> projectorOverlayCache = new();

    private IDalamudTextureWrap? GetCanvasBaseWrap(TargetGroup group, string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath)) return null;
        var baseTex = previewService.TryGetBaseTexture(group);
        if (baseTex == null) return null;
        var (data, w, h) = baseTex.Value;
        if (data == null || w <= 0 || h <= 0 || data.Length < w * h * 4) return null;

        // Cache by reference identity: PreviewService.baseTextureCache returns the same
        // byte[] ref until disk source changes, so ref equality is a cheap "still valid"
        // check that survives across many canvas frames without re-uploading.
        if (canvasBaseWrapCache.TryGetValue(gamePath, out var hit)
            && ReferenceEquals(hit.Data, data) && hit.Width == w && hit.Height == h)
            return hit.Wrap;

        if (hit != null)
        {
            try { hit.Wrap.Dispose(); } catch { }
            canvasBaseWrapCache.Remove(gamePath);
        }

        try
        {
            var wrap = textureProvider.CreateFromRaw(
                RawImageSpecification.Rgba32(w, h),
                data,
                $"SkinTattoo/canvas/{Path.GetFileName(gamePath)}");
            canvasBaseWrapCache[gamePath] = new CanvasBaseWrapEntry(wrap, data, w, h);
            return wrap;
        }
        catch { return null; }
    }

    private void DisposeCanvasBaseWrapCache()
    {
        foreach (var entry in canvasBaseWrapCache.Values)
        {
            try { entry.Wrap.Dispose(); } catch { }
        }
        canvasBaseWrapCache.Clear();

        foreach (var entry in projectorOverlayCache.Values)
        {
            try { entry.Wrap?.Dispose(); } catch { }
        }
        projectorOverlayCache.Clear();
    }

    /// <summary>
    /// Mesh-aware UV-space rasterization of a projector layer's decal image.
    /// Output is texW x texH RGBA with transparent background; opaque only where
    /// the decal actually paints. Cached by params hash; re-runs when any input
    /// changes. Result is drawn as the canvas overlay for projector-mode layers.
    /// </summary>
    private IDalamudTextureWrap? GetProjectorOverlayWrap(DecalLayer layer, Mesh.MeshData mesh, int texW, int texH)
    {
        if (imageLoader == null || string.IsNullOrEmpty(layer.ImagePath)) return null;
        if (mesh == null || mesh.TriangleCount == 0) return null;
        if (texW <= 0 || texH <= 0) return null;

        long hash = ComputeProjectorOverlayHash(layer, mesh, texW, texH);

        if (!projectorOverlayCache.TryGetValue(layer, out var entry))
        {
            entry = new ProjectorOverlayEntry();
            projectorOverlayCache[layer] = entry;
        }
        if (entry.Hash == hash && entry.Wrap != null && entry.Width == texW && entry.Height == texH)
            return entry.Wrap;

        var decalImage = imageLoader.LoadImage(layer.ImagePath);
        if (decalImage == null) return null;
        var (decalData, decalW, decalH) = decalImage.Value;

        int needLen = texW * texH * 4;
        if (entry.Data == null || entry.Data.Length < needLen)
            entry.Data = new byte[needLen];
        else
            Array.Clear(entry.Data, 0, needLen);
        var bytes = entry.Data;

        float rotRad = layer.RotationDeg * (MathF.PI / 180f);
        float opacity = layer.Opacity;
        var clipMode = layer.Clip;
        float cutoff = MathF.Cos(layer.ProjWrapAngleDeg * (MathF.PI / 180f));

        Mesh.MeshProjector.Rasterize(mesh, texW, texH,
            layer.ProjOrigin, layer.ProjNormal, layer.ProjTangent,
            layer.ProjSize, layer.ProjDepth, rotRad,
            (px, py, lu, lv) =>
            {
                switch (clipMode)
                {
                    case ClipMode.ClipLeft when lu < 0f: return;
                    case ClipMode.ClipRight when lu >= 0f: return;
                    case ClipMode.ClipTop when lv < 0f: return;
                    case ClipMode.ClipBottom when lv >= 0f: return;
                }
                float du = (lu + 0.5f) * decalW - 0.5f;
                float dv = (lv + 0.5f) * decalH - 0.5f;
                SampleDecalBilinear(decalData, decalW, decalH, du, dv,
                    out float r, out float g, out float b, out float a);
                a *= opacity;
                if (a < 0.001f) return;

                int idx = (py * texW + px) * 4;
                bytes[idx] = (byte)Math.Clamp((int)(r * 255f), 0, 255);
                bytes[idx + 1] = (byte)Math.Clamp((int)(g * 255f), 0, 255);
                bytes[idx + 2] = (byte)Math.Clamp((int)(b * 255f), 0, 255);
                bytes[idx + 3] = (byte)Math.Clamp((int)(a * 255f), 0, 255);
            }, cutoff);

        try
        {
            var newWrap = textureProvider.CreateFromRaw(
                RawImageSpecification.Rgba32(texW, texH),
                bytes,
                $"SkinTattoo/projector/{layer.ImageHash ?? layer.Name}");
            try { entry.Wrap?.Dispose(); } catch { }
            entry.Wrap = newWrap;
            entry.Width = texW;
            entry.Height = texH;
            entry.Hash = hash;
            return newWrap;
        }
        catch
        {
            return null;
        }
    }

    private static long ComputeProjectorOverlayHash(DecalLayer layer, Mesh.MeshData mesh, int texW, int texH)
    {
        // RuntimeHelpers gives reference identity for the mesh so a fresh
        // mesh (group switch / reload) invalidates the cache automatically.
        var h = new HashCode();
        h.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(mesh));
        h.Add(layer.ProjOrigin);
        h.Add(layer.ProjNormal);
        h.Add(layer.ProjTangent);
        h.Add(layer.ProjSize);
        h.Add(layer.ProjDepth);
        h.Add(layer.ProjWrapAngleDeg);
        h.Add(layer.RotationDeg);
        h.Add(layer.Opacity);
        h.Add((int)layer.Clip);
        h.Add(layer.ImagePath);
        h.Add(layer.ImageHash);
        h.Add(texW);
        h.Add(texH);
        return ((long)h.ToHashCode() << 32) ^ (uint)mesh.TriangleCount;
    }

    private static void SampleDecalBilinear(byte[] data, int w, int h, float fx, float fy,
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

    private static string? GuessMaskPathFromSibling(string? texPath)
    {
        if (string.IsNullOrEmpty(texPath)) return null;

        var p = texPath.Replace('\\', '/');
        string ext = Path.GetExtension(p);
        if (string.IsNullOrEmpty(ext)) ext = ".tex";

        bool EndsWithToken(string s, string token)
            => s.EndsWith(token + ext, StringComparison.OrdinalIgnoreCase);

        if (p.EndsWith("_base.tex", StringComparison.OrdinalIgnoreCase))
            return p[..^"_base.tex".Length] + "_mask" + ext;
        if (p.EndsWith("_norm.tex", StringComparison.OrdinalIgnoreCase))
            return p[..^"_norm.tex".Length] + "_mask" + ext;
        if (p.EndsWith("_d.tex", StringComparison.OrdinalIgnoreCase))
            return p[..^"_d.tex".Length] + "_m" + ext;
        if (p.EndsWith("_n.tex", StringComparison.OrdinalIgnoreCase))
            return p[..^"_n.tex".Length] + "_m" + ext;

        if (EndsWithToken(p, "_base"))
            return p[..^("_base".Length + ext.Length)] + "_mask" + ext;
        if (EndsWithToken(p, "_norm"))
            return p[..^("_norm".Length + ext.Length)] + "_mask" + ext;
        if (EndsWithToken(p, "_d"))
            return p[..^("_d".Length + ext.Length)] + "_m" + ext;
        if (EndsWithToken(p, "_n"))
            return p[..^("_n".Length + ext.Length)] + "_m" + ext;

        return null;
    }

    // -- Center Panel: Interactive UV Canvas ----------------------------------
    //
    // Coordinate system:
    //   "virtual UV" = square [0,1]x[0,1] representing maxDim x maxDim pixels
    //   "texture UV" = [0,1]x[0,1] relative to the actual texture (used by compositor/UvCenter)
    //   uvScale   = (texW/maxDim, texH/maxDim)  e.g. (0.5, 1.0) for 1024x2048
    //   texOffset = (1-uvScale.X, 1-uvScale.Y)   e.g. (0.5, 0.0)  -- texture on right half
    //
    //   virtual  -> screen:  uvOrigin + virtualUv * fitSize
    //   raw mesh -> virtual: rawUv * uvScale
    //   texture  -> virtual: texOffset + texUv * uvScale
    //   virtual  -> texture: (virtualUv - texOffset) / uvScale

    private void DrawCanvas()
    {
        var btnH = ImGui.GetFrameHeight();
        ImGui.AlignTextToFramePadding();
        ImGui.Text(Strings.T("label.uv_scale"));
        ImGui.SameLine();

        var fitBtnW = 44f;
        var uvMeshBtnW = ImGui.CalcTextSize(Strings.T("button.uv_mesh")).X + ImGui.GetStyle().FramePadding.X * 2;
        var colorBtnW = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var hasMesh = previewService.CurrentMesh != null;

        var rightBtns = fitBtnW + uvMeshBtnW + colorBtnW + spacing * 3;
        var sliderW = ImGui.GetContentRegionAvail().X - rightBtns;
        ImGui.SetNextItemWidth(sliderW);
        ImGui.SliderFloat("##zoom", ref canvasZoom, 0.1f, 5.0f, $"{canvasZoom * 100:F0}%%");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.canvas_zoom"));

        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.fit"), new Vector2(fitBtnW, btnH)))
        {
            canvasZoom = 1.0f;
            canvasPan = Vector2.Zero;
        }

        ImGui.SameLine();
        var activeColor = showUvWireframe && hasMesh
            ? new Vector4(0.4f, 0.8f, 1f, 1f)
            : new Vector4(0.5f, 0.5f, 0.5f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, activeColor);
        if (ImGui.Button(Strings.T("button.uv_mesh"), new Vector2(uvMeshBtnW, btnH)))
        {
            showUvWireframe = !showUvWireframe;
            SaveCanvasViewSettings();
        }
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
        {
            if (!hasMesh)
                ImGui.SetTooltip(Strings.T("tooltip.uv_mesh_no_model"));
            else
                ImGui.SetTooltip(showUvWireframe ? Strings.T("tooltip.uv_mesh_hide") : Strings.T("tooltip.uv_mesh_show"));
        }

        ImGui.SameLine();
        var wc = new Vector4(config.UvWireframeColorR, config.UvWireframeColorG,
            config.UvWireframeColorB, config.UvWireframeColorA);
        if (ImGui.ColorButton("##wireColor", wc, ImGuiColorEditFlags.AlphaPreview, new Vector2(colorBtnW, btnH)))
            ImGui.OpenPopup("##wireColorPicker");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.wire_color"));
        if (ImGui.BeginPopup("##wireColorPicker"))
        {
            if (ImGui.ColorPicker4("##wcPick", ref wc, ImGuiColorEditFlags.AlphaBar))
            {
                config.UvWireframeColorR = wc.X;
                config.UvWireframeColorG = wc.Y;
                config.UvWireframeColorB = wc.Z;
                config.UvWireframeColorA = wc.W;
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
            ImGui.EndPopup();
        }

        var mapMode = CanvasMapMode;
        ImGui.SetNextItemWidth(100f);
        if (ImGui.BeginCombo("##canvasMap", Strings.T($"canvas_map.{mapMode.ToString().ToLowerInvariant()}")))
        {
            foreach (TargetMap m in System.Enum.GetValues<TargetMap>())
            {
                var label = Strings.T($"canvas_map.{m.ToString().ToLowerInvariant()}");
                if (ImGui.Selectable(label, mapMode == m))
                {
                    CanvasMapMode = m;
                    SaveCanvasViewSettings();
                }
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.canvas_map"));

        ImGui.SameLine();
        var syncEnabled = config.UvSyncViewerWithLayerTargetMap;
        var syncColor = syncEnabled
            ? new Vector4(0.4f, 0.8f, 1f, 1f)
            : new Vector4(0.5f, 0.5f, 0.5f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, syncColor);
        var syncClicked = UiHelpers.SquareIconButton("##canvasMapSync",
            syncEnabled ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink);
        ImGui.PopStyleColor();
        if (syncClicked)
        {
            config.UvSyncViewerWithLayerTargetMap = !syncEnabled;
            if (config.UvSyncViewerWithLayerTargetMap)
                SyncCanvasMapToSelectedLayerIfEnabled();
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T(syncEnabled ? "tooltip.canvas_map_sync_on" : "tooltip.canvas_map_sync_off"));

        ImGui.SameLine();
        var currentOnly = previewCurrentLayerOnly;
        if (ImGui.Checkbox(Strings.T("checkbox.preview_current_layer"), ref currentOnly))
        {
            previewCurrentLayerOnly = currentOnly;
            SaveCanvasViewSettings();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.preview_current_layer"));

        ImGui.SameLine();
        var showBase = showCanvasBaseTexture;
        if (ImGui.Checkbox(Strings.T("checkbox.canvas_show_base"), ref showBase))
        {
            showCanvasBaseTexture = showBase;
            SaveCanvasViewSettings();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.canvas_show_base"));

        // Projector mode toggle for the selected layer. When enabled, the layer
        // paints via 3D mesh projection (mesh-aware, scatters across UV islands
        // automatically) and the UV canvas becomes read-only -- placement must
        // happen in the 3D editor. Disabled = legacy UV-quad path with canvas drag.
        ImGui.SameLine();
        var selectedForToggle = project.SelectedLayer;
        using (ImRaii.Disabled(selectedForToggle == null))
        {
            var projMode = selectedForToggle?.UseProjector ?? false;
            if (ImGui.Checkbox(Strings.T("checkbox.projector_mode") + "##projectorMode", ref projMode) && selectedForToggle != null)
            {
                selectedForToggle.UseProjector = projMode;
                MarkPreviewDirty();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(selectedForToggle == null
                ? Strings.T("tooltip.projector_mode_no_layer")
                : Strings.T("tooltip.projector_mode_on_off"));
        }

        var avail = ImGui.GetContentRegionAvail();
        var canvasSize = new Vector2(avail.X, avail.Y);
        if (canvasSize.X < 10 || canvasSize.Y < 10) return;

        var canvasPos = ImGui.GetCursorScreenPos();

        // Virtual square space: maxDim x maxDim
        float texW = lastBaseTexWidth > 0 ? lastBaseTexWidth : 1024f;
        float texH = lastBaseTexHeight > 0 ? lastBaseTexHeight : 1024f;
        float maxDim = MathF.Max(texW, texH);
        var uvScale = new Vector2(texW / maxDim, texH / maxDim);   // (0.5, 1.0) for 1024x2048
        var texOffset = Vector2.One - uvScale;                      // (0.5, 0.0)  -- texture on right half

        // Canvas is always square
        float baseSize = MathF.Min(canvasSize.X, canvasSize.Y) * canvasZoom;
        var fitSize = new Vector2(baseSize, baseSize);

        var uvOrigin = canvasPos + (canvasSize - fitSize) * 0.5f
                       - canvasPan * fitSize;

        ImGui.InvisibleButton("##Canvas", canvasSize);
        var isHovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        drawList.PushClipRect(canvasPos, canvasPos + canvasSize, true);

        drawList.AddRectFilled(canvasPos, canvasPos + canvasSize,
            ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.1f, 1f)));

        // Checkerboard and texture only in the texture region (right half for 1024x2048)
        var texScreenOrigin = uvOrigin + texOffset * fitSize;
        var texScreenSize = uvScale * fitSize;
        DrawCheckerboard(drawList, texScreenOrigin, texScreenSize);
        bool hasBaseTexture = DrawBaseTexture(drawList, texScreenOrigin, texScreenSize);

        if (showUvWireframe)
            DrawUvWireframe(drawList, uvOrigin, fitSize, uvScale, canvasPos, canvasSize);
        DrawLayerOverlays(drawList, uvOrigin, fitSize, uvScale, texOffset);

        // Border around the texture area
        drawList.AddRect(texScreenOrigin, texScreenOrigin + texScreenSize,
            ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));

        DrawRulers(drawList, canvasPos, canvasSize, texScreenOrigin, texScreenSize);

        // Only show "base map missing" when the user WANTS the base shown (not when they
        // disabled it intentionally via the "Show Base" checkbox).
        if (!hasBaseTexture && showCanvasBaseTexture)
        {
            var missingText = Strings.T("hint.canvas_map_base_missing");
            var missingSize = ImGui.CalcTextSize(missingText);
            var missingPos = new Vector2(
                texScreenOrigin.X + (texScreenSize.X - missingSize.X) * 0.5f,
                texScreenOrigin.Y + (texScreenSize.Y - missingSize.Y) * 0.5f);
            var bgColor = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f));
            var textColor = ImGui.GetColorU32(new Vector4(0.9f, 0.9f, 0.9f, 1f));
            drawList.AddRectFilled(missingPos - new Vector2(4, 2), missingPos + missingSize + new Vector2(4, 2), bgColor);
            drawList.AddText(missingPos, textColor, missingText);
        }

        drawList.PopClipRect();

        if (isHovered)
            HandleCanvasInput(canvasPos, canvasSize, uvOrigin, fitSize, uvScale, texOffset);
    }

    private void DrawUvWireframe(ImDrawListPtr drawList, Vector2 uvOrigin, Vector2 fitSize,
        Vector2 uvScale, Vector2 canvasPos, Vector2 canvasSize)
    {
        var mesh = previewService.CurrentMesh;
        if (mesh == null || mesh.Indices.Length < 3) return;

        var wireColor = ImGui.GetColorU32(new Vector4(
            config.UvWireframeColorR, config.UvWireframeColorG,
            config.UvWireframeColorB, config.UvWireframeColorA));

        var savedFlags = drawList.Flags;
        if (!config.UvWireframeAntiAlias)
        {
            drawList.Flags &= ~ImDrawListFlags.AntiAliasedLines;
            drawList.Flags &= ~ImDrawListFlags.AntiAliasedLinesUseTex;
        }

        // Per-vertex UV tile normalization. FFXIV uses UDIM-style tiling for
        // skin meshes  -- body verts live in tile 1 (X in [1,2]) and other parts
        // in tile 0 (X in [0,1]). After merging multiple mdls (e.g. cross-race
        // body cutouts) the same mesh can contain BOTH tiles, so a single
        // global "uvBase = floor(min)" normalization breaks half the mesh.
        // Take fract() per-vertex instead  -- both (1.6, 0.5) and (0.6, 0.5)
        // become (0.6, 0.5), which is the same texel anyway.
        var texOffset = Vector2.One - uvScale;

        var clipMin = canvasPos;
        var clipMax = canvasPos + canvasSize;
        var doCull = config.UvWireframeCulling;

        HashSet<long>? drawnEdges = null;
        if (config.UvWireframeDedup)
            drawnEdges = new HashSet<long>(mesh.Indices.Length);

        Vector2 ToScreen(Vector2 uv)
        {
            var fract = new Vector2(uv.X - MathF.Floor(uv.X), uv.Y - MathF.Floor(uv.Y));
            return uvOrigin + (texOffset + fract * uvScale) * fitSize;
        }

        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var i0 = mesh.Indices[i];
            var i1 = mesh.Indices[i + 1];
            var i2 = mesh.Indices[i + 2];
            if (i0 >= mesh.Vertices.Length || i1 >= mesh.Vertices.Length || i2 >= mesh.Vertices.Length)
                continue;

            var p0 = ToScreen(mesh.Vertices[i0].UV);
            var p1 = ToScreen(mesh.Vertices[i1].UV);
            var p2 = ToScreen(mesh.Vertices[i2].UV);

            if (doCull)
            {
                var minX = MathF.Min(p0.X, MathF.Min(p1.X, p2.X));
                var maxX = MathF.Max(p0.X, MathF.Max(p1.X, p2.X));
                var minY = MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y));
                var maxY = MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y));
                if (maxX < clipMin.X || minX > clipMax.X || maxY < clipMin.Y || minY > clipMax.Y)
                    continue;
            }

            if (drawnEdges != null)
            {
                if (drawnEdges.Add(EdgeKey(i0, i1))) drawList.AddLine(p0, p1, wireColor, 1f);
                if (drawnEdges.Add(EdgeKey(i1, i2))) drawList.AddLine(p1, p2, wireColor, 1f);
                if (drawnEdges.Add(EdgeKey(i2, i0))) drawList.AddLine(p2, p0, wireColor, 1f);
            }
            else
            {
                drawList.AddLine(p0, p1, wireColor, 1f);
                drawList.AddLine(p1, p2, wireColor, 1f);
                drawList.AddLine(p2, p0, wireColor, 1f);
            }
        }

        drawList.Flags = savedFlags;
    }

    private static long EdgeKey(int a, int b)
    {
        if (a > b) (a, b) = (b, a);
        return ((long)a << 32) | (uint)b;
    }

    /// <summary>
    /// Find the UV tile base: floor of the minimum UV across all vertices.
    /// Body models have UV X in [1,2] -> base=(1,0). Normal models UV in [0,1] -> base=(0,0).
    /// Subtracting this normalizes all UVs to [0,1] texture-space.
    /// </summary>
    private static Vector2 ComputeMeshUvBase(Mesh.MeshData mesh)
    {
        if (mesh.Vertices.Length == 0) return Vector2.Zero;

        float minX = float.MaxValue, minY = float.MaxValue;
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            var uv = mesh.Vertices[i].UV;
            if (uv.X < minX) minX = uv.X;
            if (uv.Y < minY) minY = uv.Y;
        }

        return new Vector2(MathF.Floor(minX), MathF.Floor(minY));
    }

    private void DrawRulers(ImDrawListPtr drawList, Vector2 canvasPos, Vector2 canvasSize, Vector2 texOrigin, Vector2 texSize)
    {
        var tickColor = ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.6f));
        var textColor = ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 0.8f));
        var divisions = 10;

        for (var i = 0; i <= divisions; i++)
        {
            var t = i / (float)divisions;
            var label = $"{t:F1}";

            var hx = texOrigin.X + t * texSize.X;
            if (hx >= canvasPos.X && hx <= canvasPos.X + canvasSize.X)
            {
                var tickLen = (i % 5 == 0) ? 8f : 4f;
                drawList.AddLine(new Vector2(hx, texOrigin.Y), new Vector2(hx, texOrigin.Y - tickLen), tickColor);
                if (i % 5 == 0)
                    drawList.AddText(new Vector2(hx + 2, texOrigin.Y - 16), textColor, label);
            }

            var vy = texOrigin.Y + t * texSize.Y;
            if (vy >= canvasPos.Y && vy <= canvasPos.Y + canvasSize.Y)
            {
                var tickLen = (i % 5 == 0) ? 8f : 4f;
                drawList.AddLine(new Vector2(texOrigin.X, vy), new Vector2(texOrigin.X - tickLen, vy), tickColor);
                if (i % 5 == 0)
                    drawList.AddText(new Vector2(texOrigin.X - 28, vy - 6), textColor, label);
            }
        }
    }

    private void DrawCheckerboard(ImDrawListPtr drawList, Vector2 origin, Vector2 size)
    {
        var checkerSize = 16f;
        var cols = (int)(size.X / checkerSize) + 1;
        var rows = (int)(size.Y / checkerSize) + 1;
        var darkColor = ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f));
        var lightColor = ImGui.GetColorU32(new Vector4(0.2f, 0.2f, 0.2f, 1f));

        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var pMin = origin + new Vector2(x * checkerSize, y * checkerSize);
                var pMax = pMin + new Vector2(checkerSize);
                pMin = Vector2.Max(pMin, origin);
                pMax = Vector2.Min(pMax, origin + size);
                if (pMin.X >= pMax.X || pMin.Y >= pMax.Y) continue;

                var color = ((x + y) % 2 == 0) ? darkColor : lightColor;
                drawList.AddRectFilled(pMin, pMax, color);
            }
        }
    }

    private IDalamudTextureWrap? GetCanvasOverlayWrap(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return null;

        try
        {
            return textureProvider.GetFromFile(imagePath).GetWrapOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private bool DrawBaseTexture(ImDrawListPtr drawList, Vector2 texOrigin, Vector2 texSize)
    {
        var previousBaseWidth = lastBaseTexWidth > 0 ? lastBaseTexWidth : 1024;
        var previousBaseHeight = lastBaseTexHeight > 0 ? lastBaseTexHeight : 1024;
        lastBaseTexWidth = 0;
        lastBaseTexHeight = 0;
        var group = project.SelectedGroup;
        if (group == null) return false;

        if (!showCanvasBaseTexture)
        {
            lastBaseTexWidth = previousBaseWidth;
            lastBaseTexHeight = previousBaseHeight;
            return false;
        }

        string? texDiskPath;
        string? texGamePath;

        if (CanvasMapMode == TargetMap.Normal)
        {
            texDiskPath = group.OrigNormDiskPath ?? group.NormDiskPath;
            texGamePath = group.NormGamePath;
        }
        else if (CanvasMapMode == TargetMap.Mask)
        {
            // Resolve mask game path from material sampler
            texDiskPath = null;
            texGamePath = null;
            if (previewService.TryGetMaskGamePath(group, out var maskPath))
                texGamePath = maskPath;

            // Fallback: derive sibling mask DISK path from current local diffuse/normal
            // textures. This is needed for modded/custom assets where mtrl sampler parsing
            // can fail or the game path doesn't exist in SqPack, but disk siblings do.
            texDiskPath = GuessMaskPathFromSibling(group.OrigDiffuseDiskPath ?? group.DiffuseDiskPath)
                       ?? GuessMaskPathFromSibling(group.OrigNormDiskPath ?? group.NormDiskPath);
            if (!string.IsNullOrEmpty(texDiskPath) && !File.Exists(texDiskPath))
                texDiskPath = null;

            // Fallback: derive sibling mask path from known diffuse/normal names
            // for materials where mtrl sampler parsing fails but standard *_mask
            // files still exist.
            texGamePath ??= GuessMaskPathFromSibling(group.DiffuseGamePath)
                          ?? GuessMaskPathFromSibling(group.NormGamePath);
        }
        else
        {
            texDiskPath = group.DiffuseDiskPath;
            texGamePath = group.DiffuseGamePath;

            // Use vanilla / mod-source diffuse as the canvas base; layer overlays carry
            // the decal preview. See CanvasBaseWrapEntry comment above for the rationale.
            var baseWrap = GetCanvasBaseWrap(group, texGamePath);
            if (baseWrap != null)
            {
                lastBaseTexWidth = baseWrap.Width;
                lastBaseTexHeight = baseWrap.Height;
                drawList.AddImage(baseWrap.Handle,
                    texOrigin, texOrigin + texSize,
                    Vector2.Zero, Vector2.One);
                return true;
            }
        }

        if (string.IsNullOrEmpty(texDiskPath) && string.IsNullOrEmpty(texGamePath))
        {
            lastBaseTexWidth = previousBaseWidth;
            lastBaseTexHeight = previousBaseHeight;
            return false;
        }

        try
        {
            var normalizedGamePath = (texGamePath ?? string.Empty).Replace('\\', '/');
            var stagedDiskPath = !string.IsNullOrEmpty(normalizedGamePath)
                ? previewService.GetStagedDiskPath(normalizedGamePath)
                : null;
            if (string.IsNullOrEmpty(stagedDiskPath) && !string.IsNullOrEmpty(texGamePath))
                stagedDiskPath = previewService.GetStagedDiskPath(texGamePath);

            var diskPath = !string.IsNullOrEmpty(texDiskPath) && File.Exists(texDiskPath)
                ? texDiskPath
                : (!string.IsNullOrEmpty(stagedDiskPath) && File.Exists(stagedDiskPath) ? stagedDiskPath : null);

            var shared = !string.IsNullOrEmpty(diskPath)
                ? textureProvider.GetFromFile(diskPath)
                : textureProvider.GetFromGame(normalizedGamePath);
            var wrap = shared.GetWrapOrDefault();
            if (wrap != null)
            {
                lastBaseTexWidth = wrap.Width;
                lastBaseTexHeight = wrap.Height;
                drawList.AddImage(wrap.Handle,
                    texOrigin, texOrigin + texSize,
                    Vector2.Zero, Vector2.One);
                return true;
            }
        }
        catch { }

        lastBaseTexWidth = previousBaseWidth;
        lastBaseTexHeight = previousBaseHeight;
        return false;
    }

    private void DrawLayerOverlays(ImDrawListPtr drawList, Vector2 uvOrigin, Vector2 fitSize,
        Vector2 uvScale, Vector2 texOffset)
    {
        var group = project.SelectedGroup;
        if (group == null) return;

        var meshForProjector = previewService.CurrentMesh;
        for (var i = 0; i < group.Layers.Count; i++)
        {
            var layer = group.Layers[i];
            if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;
            if (layer.TargetMap != CanvasMapMode) continue;

            bool isCurrent = i == group.SelectedLayerIndex;
            if (previewCurrentLayerOnly && !isCurrent)
                continue;

            var isSelected = group.SelectedLayerIndex == i;

            if (layer.UseProjector && meshForProjector != null
                && lastBaseTexWidth > 0 && lastBaseTexHeight > 0)
            {
                DrawProjectorOutline(drawList, layer, meshForProjector,
                    uvOrigin, fitSize, uvScale, texOffset, isSelected);
                continue;
            }

            // texture UV -> virtual UV -> screen
            var pCenter = uvOrigin + (texOffset + layer.UvCenter * uvScale) * fitSize;
            var absScale = GetScaleAbs(layer.UvScale);
            var pHalfSize = absScale * uvScale * fitSize * 0.5f;
            var mirroredX = layer.UvScale.X < 0f;
            var mirroredY = layer.UvScale.Y < 0f;

            var localMin = -pHalfSize;
            var localMax = pHalfSize;
            float uvLeft = mirroredX ? 1f : 0f;
            float uvRight = mirroredX ? 0f : 1f;
            float uvTop = mirroredY ? 1f : 0f;
            float uvBottom = mirroredY ? 0f : 1f;
            switch (layer.Clip)
            {
                case ClipMode.ClipLeft:
                    if (mirroredX)
                    {
                        localMax.X = 0;
                        uvRight = 0.5f;
                    }
                    else
                    {
                        localMin.X = 0;
                        uvLeft = 0.5f;
                    }
                    break;
                case ClipMode.ClipRight:
                    if (mirroredX)
                    {
                        localMin.X = 0;
                        uvLeft = 0.5f;
                    }
                    else
                    {
                        localMax.X = 0;
                        uvRight = 0.5f;
                    }
                    break;
                case ClipMode.ClipTop:
                    if (mirroredY)
                    {
                        localMax.Y = 0;
                        uvBottom = 0.5f;
                    }
                    else
                    {
                        localMin.Y = 0;
                        uvTop = 0.5f;
                    }
                    break;
                case ClipMode.ClipBottom:
                    if (mirroredY)
                    {
                        localMin.Y = 0;
                        uvTop = 0.5f;
                    }
                    else
                    {
                        localMax.Y = 0;
                        uvBottom = 0.5f;
                    }
                    break;
            }

            try
            {
                if (File.Exists(layer.ImagePath))
                {
                    var wrap = GetCanvasOverlayWrap(layer.ImagePath);
                    if (wrap != null)
                    {
                        var alpha = (uint)(layer.Opacity * 255) << 24 | 0x00FFFFFF;

                        if (MathF.Abs(layer.RotationDeg) < 0.1f)
                        {
                            drawList.AddImage(wrap.Handle,
                                pCenter + localMin, pCenter + localMax,
                                new Vector2(uvLeft, uvTop), new Vector2(uvRight, uvBottom), alpha);
                        }
                        else
                        {
                            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
                            var cos = MathF.Cos(rotRad);
                            var sin = MathF.Sin(rotRad);
                            Vector2 Rotate(Vector2 p) => new(
                                p.X * cos - p.Y * sin,
                                p.X * sin + p.Y * cos);

                            var tl = pCenter + Rotate(new Vector2(localMin.X, localMin.Y));
                            var tr = pCenter + Rotate(new Vector2(localMax.X, localMin.Y));
                            var br = pCenter + Rotate(new Vector2(localMax.X, localMax.Y));
                            var bl = pCenter + Rotate(new Vector2(localMin.X, localMax.Y));

                            drawList.AddImageQuad(wrap.Handle,
                                tl, tr, br, bl,
                                new Vector2(uvLeft, uvTop), new Vector2(uvRight, uvTop),
                                new Vector2(uvRight, uvBottom), new Vector2(uvLeft, uvBottom),
                                alpha);
                        }
                    }
                }
            }
            catch { }

            var borderColor = isSelected
                ? ImGui.GetColorU32(new Vector4(1f, 0.8f, 0.2f, 1f))
                : ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 0.5f));
            var thickness = isSelected ? 2f : 1f;

            if (MathF.Abs(layer.RotationDeg) < 0.1f)
            {
                drawList.AddRect(pCenter + localMin, pCenter + localMax, borderColor, 0, ImDrawFlags.None, thickness);
            }
            else
            {
                var rotRad = layer.RotationDeg * (MathF.PI / 180f);
                var cos = MathF.Cos(rotRad);
                var sin = MathF.Sin(rotRad);
                Vector2 Rotate(Vector2 p) => new(
                    p.X * cos - p.Y * sin,
                    p.X * sin + p.Y * cos);

                var tl = pCenter + Rotate(new Vector2(localMin.X, localMin.Y));
                var tr = pCenter + Rotate(new Vector2(localMax.X, localMin.Y));
                var br = pCenter + Rotate(new Vector2(localMax.X, localMax.Y));
                var bl = pCenter + Rotate(new Vector2(localMin.X, localMax.Y));

                drawList.AddQuad(tl, tr, br, bl, borderColor, thickness);
            }

            if (isSelected)
            {
                var cross = 6f;
                drawList.AddLine(pCenter - new Vector2(cross, 0), pCenter + new Vector2(cross, 0), borderColor, 1f);
                drawList.AddLine(pCenter - new Vector2(0, cross), pCenter + new Vector2(0, cross), borderColor, 1f);
            }
        }
    }

    /// <summary>
    /// Draws a projector layer's actual mesh-aware UV painting onto the canvas.
    /// Calls GetProjectorOverlayWrap which rasterizes the decal image through
    /// MeshProjector (transparent decal pixels stay transparent, so multi-UV-island
    /// scatter shows up exactly as it will be painted in the texture). A thin bbox
    /// border is drawn for selected layers as a placement hint.
    /// </summary>
    private void DrawProjectorOutline(ImDrawListPtr drawList, DecalLayer layer,
        Mesh.MeshData mesh, Vector2 uvOrigin, Vector2 fitSize, Vector2 uvScale,
        Vector2 texOffset, bool isSelected)
    {
        int texW = lastBaseTexWidth;
        int texH = lastBaseTexHeight;
        if (texW <= 0 || texH <= 0) return;

        // Texture region in screen space -- the rasterized overlay maps [0,1] UV
        // onto exactly this rectangle, so painted texels land at the right canvas
        // position automatically.
        var texScreenMin = uvOrigin + texOffset * fitSize;
        var texScreenMax = uvOrigin + (texOffset + uvScale) * fitSize;

        var wrap = GetProjectorOverlayWrap(layer, mesh, texW, texH);
        if (wrap != null)
        {
            var alpha = (uint)(layer.Opacity * 255) << 24 | 0x00FFFFFF;
            // Wrap already has decal RGB * alpha; draw with white tint preserving
            // its internal opacity. Opacity is double-applied (in rasterize and
            // here) -- keep tint at full so the rasterized alpha drives blend.
            drawList.AddImage(wrap.Handle,
                texScreenMin, texScreenMax,
                Vector2.Zero, Vector2.One);
        }

        if (isSelected)
        {
            float rotRad = layer.RotationDeg * (MathF.PI / 180f);
            float cutoff = MathF.Cos(layer.ProjWrapAngleDeg * (MathF.PI / 180f));
            var bbox = Mesh.MeshProjector.ComputeUvBbox(mesh, texW, texH,
                layer.ProjOrigin, layer.ProjNormal, layer.ProjTangent,
                layer.ProjSize, layer.ProjDepth, rotRad, cutoff);
            var borderColor = ImGui.GetColorU32(new Vector4(1f, 0.8f, 0.2f, 0.8f));

            if (!bbox.IsEmpty)
            {
                Vector2 TexToScreen(int px, int py)
                {
                    var texUv = new Vector2((px + 0.5f) / texW, (py + 0.5f) / texH);
                    return uvOrigin + (texOffset + texUv * uvScale) * fitSize;
                }
                var rectMin = TexToScreen(bbox.X, bbox.Y);
                var rectMax = TexToScreen(bbox.X + bbox.W - 1, bbox.Y + bbox.H - 1);
                drawList.AddRect(rectMin, rectMax, borderColor, 0, ImDrawFlags.None, 1.5f);
            }
            else
            {
                var center = uvOrigin + (texOffset + layer.UvCenter * uvScale) * fitSize;
                float c = 6f;
                drawList.AddLine(center - new Vector2(c, 0), center + new Vector2(c, 0), borderColor, 1.5f);
                drawList.AddLine(center - new Vector2(0, c), center + new Vector2(0, c), borderColor, 1.5f);
            }
        }
    }

    private static void DrawProjectorTriangleOutlines(ImDrawListPtr drawList, DecalLayer layer,
        Mesh.MeshData mesh, int texW, int texH, float rotRad,
        Vector2 uvOrigin, Vector2 fitSize, Vector2 uvScale, Vector2 texOffset, uint color)
    {
        var n = layer.ProjNormal;
        if (n.LengthSquared() < 1e-8f) return;
        n = Vector3.Normalize(n);

        var tIn = layer.ProjTangent - Vector3.Dot(layer.ProjTangent, n) * n;
        if (tIn.LengthSquared() < 1e-8f)
        {
            var fallback = MathF.Abs(n.X) < 0.9f ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0);
            tIn = fallback - Vector3.Dot(fallback, n) * n;
        }
        tIn = Vector3.Normalize(tIn);
        var bIn = Vector3.Cross(n, tIn);
        float cosR = MathF.Cos(rotRad);
        float sinR = MathF.Sin(rotRad);
        var t = cosR * tIn + sinR * bIn;
        var b = Vector3.Cross(n, t);

        float halfX = layer.ProjSize.X * 0.5f;
        float halfY = layer.ProjSize.Y * 0.5f;
        float halfZ = layer.ProjDepth;
        float ex = MathF.Abs(t.X) * halfX + MathF.Abs(b.X) * halfY + MathF.Abs(n.X) * halfZ;
        float ey = MathF.Abs(t.Y) * halfX + MathF.Abs(b.Y) * halfY + MathF.Abs(n.Y) * halfZ;
        float ez = MathF.Abs(t.Z) * halfX + MathF.Abs(b.Z) * halfY + MathF.Abs(n.Z) * halfZ;
        var boxMin = layer.ProjOrigin - new Vector3(ex, ey, ez);
        var boxMax = layer.ProjOrigin + new Vector3(ex, ey, ez);

        var verts = mesh.Vertices;
        var indices = mesh.Indices;
        int triCount = indices.Length / 3;
        int drawn = 0;
        const int maxDraw = 600;

        Vector2 UvToScreen(Vector2 uv)
        {
            var fract = new Vector2(uv.X - MathF.Floor(uv.X), uv.Y - MathF.Floor(uv.Y));
            return uvOrigin + (texOffset + fract * uvScale) * fitSize;
        }

        for (int tIdx = 0; tIdx < triCount && drawn < maxDraw; tIdx++)
        {
            int i0 = indices[tIdx * 3];
            int i1 = indices[tIdx * 3 + 1];
            int i2 = indices[tIdx * 3 + 2];
            if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length) continue;

            var p0 = verts[i0].Position;
            var p1 = verts[i1].Position;
            var p2 = verts[i2].Position;

            float triMinX = MathF.Min(p0.X, MathF.Min(p1.X, p2.X));
            float triMaxX = MathF.Max(p0.X, MathF.Max(p1.X, p2.X));
            if (triMaxX < boxMin.X || triMinX > boxMax.X) continue;
            float triMinY = MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y));
            float triMaxY = MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y));
            if (triMaxY < boxMin.Y || triMinY > boxMax.Y) continue;
            float triMinZ = MathF.Min(p0.Z, MathF.Min(p1.Z, p2.Z));
            float triMaxZ = MathF.Max(p0.Z, MathF.Max(p1.Z, p2.Z));
            if (triMaxZ < boxMin.Z || triMinZ > boxMax.Z) continue;

            var avgN = verts[i0].Normal + verts[i1].Normal + verts[i2].Normal;
            if (Vector3.Dot(avgN, n) < 0f) continue;

            // Quick projector-volume membership: at least one vertex must lie
            // within the OBB to count this triangle as "painted".
            bool any = false;
            for (int k = 0; k < 3; k++)
            {
                var pos = k == 0 ? p0 : k == 1 ? p1 : p2;
                var rel = pos - layer.ProjOrigin;
                float ln = Vector3.Dot(rel, n);
                if (ln < -halfZ || ln > halfZ) continue;
                float lu = Vector3.Dot(rel, t) / layer.ProjSize.X;
                if (lu < -0.5f || lu > 0.5f) continue;
                float lv = Vector3.Dot(rel, b) / layer.ProjSize.Y;
                if (lv < -0.5f || lv > 0.5f) continue;
                any = true; break;
            }
            if (!any) continue;

            var s0 = UvToScreen(verts[i0].UV);
            var s1 = UvToScreen(verts[i1].UV);
            var s2 = UvToScreen(verts[i2].UV);
            drawList.AddLine(s0, s1, color, 1f);
            drawList.AddLine(s1, s2, color, 1f);
            drawList.AddLine(s2, s0, color, 1f);
            drawn++;
        }
    }

    private void HandleCanvasInput(Vector2 canvasPos, Vector2 canvasSize, Vector2 uvOrigin, Vector2 fitSize,
        Vector2 uvScale, Vector2 texOffset)
    {
        var io = ImGui.GetIO();
        var mousePos = io.MousePos;
        var group = project.SelectedGroup;
        var selectedLayer = group?.SelectedLayer;
        var hasActiveLayer = selectedLayer != null && !string.IsNullOrEmpty(selectedLayer.ImagePath);

        // Screen size of the texture area
        var texScreenSize = uvScale * fitSize;

        if (MathF.Abs(io.MouseWheel) > 0.01f)
            canvasZoom = Math.Clamp(canvasZoom + io.MouseWheel * 0.15f * canvasZoom, 0.1f, 10f);

        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
        {
            canvasPanning = true;
            canvasPan -= io.MouseDelta / fitSize;
        }
        else
        {
            canvasPanning = false;
        }

        // Projector-mode layers are read-only on the UV canvas: skip right-click
        // resize/rotate and left-click drag start. Layer selection (click on another
        // layer's UV ghost-box) still works so users can switch layers from here.
        var selectedIsProjector = selectedLayer?.UseProjector ?? false;

        if (hasActiveLayer && group != null && !selectedIsProjector)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                var pCenter = uvOrigin + (texOffset + selectedLayer!.UvCenter * uvScale) * fitSize;
                var pHalfSize = GetScaleAbs(selectedLayer.UvScale) * texScreenSize * 0.5f;
                canvasScalingLayer = mousePos.X >= pCenter.X - pHalfSize.X && mousePos.X <= pCenter.X + pHalfSize.X &&
                                    mousePos.Y >= pCenter.Y - pHalfSize.Y && mousePos.Y <= pCenter.Y + pHalfSize.Y;
            }

            if (canvasScalingLayer && ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                var delta = io.MouseDelta;

                if (io.KeyAlt)
                {
                    var rotDelta = -delta.Y * 0.5f;
                    selectedLayer!.RotationDeg = Math.Clamp(selectedLayer.RotationDeg + rotDelta, -180f, 180f);
                }
                else
                {
                    var scaleDelta = delta.X * 0.003f;
                    if (scaleLocked)
                    {
                        // Aspect ratio correction so decal stays square in pixel space
                        float texAspect = (lastBaseTexWidth > 0 && lastBaseTexHeight > 0)
                            ? (float)lastBaseTexWidth / lastBaseTexHeight : 1f;
                        var signX = GetScaleSign(selectedLayer!.UvScale.X);
                        var signY = GetScaleSign(selectedLayer.UvScale.Y);
                        var s = ClampScaleMagnitude(MathF.Abs(selectedLayer.UvScale.X) + scaleDelta);
                        selectedLayer.UvScale = new Vector2(signX * s, signY * s * texAspect);
                    }
                    else
                    {
                        var x = GetScaleSign(selectedLayer!.UvScale.X)
                            * ClampScaleMagnitude(MathF.Abs(selectedLayer.UvScale.X) + scaleDelta);
                        var y = GetScaleSign(selectedLayer.UvScale.Y)
                            * ClampScaleMagnitude(MathF.Abs(selectedLayer.UvScale.Y) + scaleDelta);
                        selectedLayer.UvScale = new Vector2(x, y);
                    }
                }
                MarkPreviewDirty();
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                canvasScalingLayer = false;
        }

        if (!canvasPanning && !canvasScalingLayer && group != null)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                canvasDraggingLayer = false;
                if (hasActiveLayer && !selectedIsProjector)
                {
                    var pCenter = uvOrigin + (texOffset + selectedLayer!.UvCenter * uvScale) * fitSize;
                    var pHalfSize = GetScaleAbs(selectedLayer.UvScale) * texScreenSize * 0.5f;
                    canvasDraggingLayer = mousePos.X >= pCenter.X - pHalfSize.X && mousePos.X <= pCenter.X + pHalfSize.X &&
                                         mousePos.Y >= pCenter.Y - pHalfSize.Y && mousePos.Y <= pCenter.Y + pHalfSize.Y;
                }

                if (!canvasDraggingLayer)
                {
                    for (var i = group.Layers.Count - 1; i >= 0; i--)
                    {
                        var l = group.Layers[i];
                        if (!l.IsVisible || string.IsNullOrEmpty(l.ImagePath)) continue;
                        var lc = uvOrigin + (texOffset + l.UvCenter * uvScale) * fitSize;
                        var lh = GetScaleAbs(l.UvScale) * texScreenSize * 0.5f;
                        if (mousePos.X >= lc.X - lh.X && mousePos.X <= lc.X + lh.X &&
                            mousePos.Y >= lc.Y - lh.Y && mousePos.Y <= lc.Y + lh.Y)
                        {
                            group.SelectedLayerIndex = i;
                            SyncImagePathBuf();
                            SyncCanvasMapToSelectedLayerIfEnabled();
                            canvasDraggingLayer = !l.UseProjector;
                            break;
                        }
                    }
                }
            }

            if (canvasDraggingLayer && ImGui.IsMouseDragging(ImGuiMouseButton.Left)
                && group.SelectedLayer != null && !group.SelectedLayer.UseProjector)
            {
                var layer = group.SelectedLayer;
                // Convert mouse delta to texture UV delta
                var delta = io.MouseDelta / texScreenSize;

                if (io.KeyShift) delta.X = 0;
                if (io.KeyCtrl) delta.Y = 0;

                layer.UvCenter = new Vector2(
                    Math.Clamp(layer.UvCenter.X + delta.X, 0f, 1f),
                    Math.Clamp(layer.UvCenter.Y + delta.Y, 0f, 1f));
                MarkPreviewDirty();
            }

            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                canvasDraggingLayer = false;
        }

        if (canvasDraggingLayer)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        else if (canvasScalingLayer)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        else if (canvasPanning)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        // Virtual UV -> texture UV for display
        var virtualUv = (mousePos - uvOrigin) / fitSize;
        var texUv = (virtualUv - texOffset) / uvScale;

        var drawList = ImGui.GetWindowDrawList();
        var bgColor = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f));
        var textColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.9f));
        var dimColor = ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.7f, 0.7f));

        // Bottom-left: texture-relative mouse UV
        if (texUv.X >= 0 && texUv.X <= 1 && texUv.Y >= 0 && texUv.Y <= 1)
        {
            var uvText = $"UV: {texUv.X:F3}, {texUv.Y:F3}";
            var textPos = canvasPos + new Vector2(4, canvasSize.Y - 20);
            var textSize = ImGui.CalcTextSize(uvText);
            drawList.AddRectFilled(textPos - new Vector2(2, 1), textPos + new Vector2(textSize.X + 4, 17), bgColor);
            drawList.AddText(textPos, textColor, uvText);
        }

        // Bottom-right: operation hints
        {
            var hint1 = Strings.T("hint.canvas_ops");
            var hint2 = Strings.T("hint.canvas_mods");
            var hint1Size = ImGui.CalcTextSize(hint1);
            var hint2Size = ImGui.CalcTextSize(hint2);
            var pos2 = canvasPos + new Vector2(canvasSize.X - hint2Size.X - 6, canvasSize.Y - 20);
            var pos1 = canvasPos + new Vector2(canvasSize.X - hint1Size.X - 6, canvasSize.Y - 38);
            drawList.AddRectFilled(pos1 - new Vector2(2, 1), pos1 + new Vector2(hint1Size.X + 4, 17), bgColor);
            drawList.AddText(pos1, dimColor, hint1);
            drawList.AddRectFilled(pos2 - new Vector2(2, 1), pos2 + new Vector2(hint2Size.X + 4, 17), bgColor);
            drawList.AddText(pos2, dimColor, hint2);
        }

        // Top-left: group name
        if (group != null)
        {
            var groupText = group.Name;
            var groupPos = canvasPos + new Vector2(4, 4);
            var groupSize = ImGui.CalcTextSize(groupText);
            drawList.AddRectFilled(groupPos - new Vector2(2, 1), groupPos + new Vector2(groupSize.X + 4, 17), bgColor);
            drawList.AddText(groupPos, textColor, groupText);
        }

        // Top-right: base texture resolution
        if (lastBaseTexWidth > 0 && lastBaseTexHeight > 0)
        {
            var resText = $"{lastBaseTexWidth} x {lastBaseTexHeight}";
            var resSize = ImGui.CalcTextSize(resText);
            var resPos = canvasPos + new Vector2(canvasSize.X - resSize.X - 6, 4);
            drawList.AddRectFilled(resPos - new Vector2(2, 1), resPos + new Vector2(resSize.X + 4, 17), bgColor);
            drawList.AddText(resPos, textColor, resText);
        }
    }
}
