using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using SkinTattoo.Core;
using SkinTattoo.Services;

namespace SkinTattoo.Gui;

public partial class MainWindow
{
    // UV mesh wireframe toggle
    private bool showUvWireframe;

    // ── Center Panel: Interactive UV Canvas ──────────────────────────────────
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
        ImGui.Text("UV 缩放");
        ImGui.SameLine();

        var fitBtnW = 44f;
        var uvMeshBtnW = ImGui.CalcTextSize("UV网格").X + ImGui.GetStyle().FramePadding.X * 2;
        var colorBtnW = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var hasMesh = previewService.CurrentMesh != null;

        // Reserve space for buttons on the right
        var rightBtns = fitBtnW + uvMeshBtnW + spacing * 2
                        + (showUvWireframe ? colorBtnW + spacing : 0);
        var sliderW = ImGui.GetContentRegionAvail().X - rightBtns;
        ImGui.SetNextItemWidth(sliderW);
        ImGui.SliderFloat("##zoom", ref canvasZoom, 0.1f, 5.0f, $"{canvasZoom * 100:F0}%%");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("画布缩放 (滚轮)");

        ImGui.SameLine();
        if (ImGui.Button("适应", new Vector2(fitBtnW, btnH)))
        {
            canvasZoom = 1.0f;
            canvasPan = Vector2.Zero;
        }

        ImGui.SameLine();
        var activeColor = showUvWireframe && hasMesh
            ? new Vector4(0.4f, 0.8f, 1f, 1f)
            : new Vector4(0.5f, 0.5f, 0.5f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, activeColor);
        if (ImGui.Button("UV网格", new Vector2(uvMeshBtnW, btnH)))
            showUvWireframe = !showUvWireframe;
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
        {
            if (!hasMesh)
                ImGui.SetTooltip("需要先导入模型才能显示 UV 网格");
            else
                ImGui.SetTooltip(showUvWireframe ? "隐藏 UV 网格" : "显示 UV 网格");
        }

        if (showUvWireframe)
        {
            ImGui.SameLine();
            var wc = new Vector4(config.UvWireframeColorR, config.UvWireframeColorG,
                config.UvWireframeColorB, config.UvWireframeColorA);
            if (ImGui.ColorButton("##wireColor", wc, ImGuiColorEditFlags.AlphaPreview, new Vector2(colorBtnW, btnH)))
                ImGui.OpenPopup("##wireColorPicker");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("网格颜色");
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
        DrawBaseTexture(drawList, texScreenOrigin, texScreenSize);

        if (showUvWireframe)
            DrawUvWireframe(drawList, uvOrigin, fitSize, uvScale, canvasPos, canvasSize);
        DrawLayerOverlays(drawList, uvOrigin, fitSize, uvScale, texOffset);

        // Border around the texture area
        drawList.AddRect(texScreenOrigin, texScreenOrigin + texScreenSize,
            ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));

        DrawRulers(drawList, canvasPos, canvasSize, texScreenOrigin, texScreenSize);

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

    private void DrawBaseTexture(ImDrawListPtr drawList, Vector2 texOrigin, Vector2 texSize)
    {
        lastBaseTexWidth = 0;
        lastBaseTexHeight = 0;
        var group = project.SelectedGroup;
        if (group == null) return;

        var texDiskPath = group.DiffuseDiskPath;
        var texGamePath = group.DiffuseGamePath;
        if (string.IsNullOrEmpty(texDiskPath) && string.IsNullOrEmpty(texGamePath)) return;

        try
        {
            var shared = !string.IsNullOrEmpty(texDiskPath) && File.Exists(texDiskPath)
                ? textureProvider.GetFromFile(texDiskPath)
                : textureProvider.GetFromGame(texGamePath ?? "");
            var wrap = shared.GetWrapOrDefault();
            if (wrap != null)
            {
                lastBaseTexWidth = wrap.Width;
                lastBaseTexHeight = wrap.Height;
                drawList.AddImage(wrap.Handle,
                    texOrigin, texOrigin + texSize,
                    Vector2.Zero, Vector2.One);
            }
        }
        catch { }
    }

    private void DrawLayerOverlays(ImDrawListPtr drawList, Vector2 uvOrigin, Vector2 fitSize,
        Vector2 uvScale, Vector2 texOffset)
    {
        var group = project.SelectedGroup;
        if (group == null) return;

        for (var i = 0; i < group.Layers.Count; i++)
        {
            var layer = group.Layers[i];
            if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;

            var isSelected = group.SelectedLayerIndex == i;

            // texture UV -> virtual UV -> screen
            var pCenter = uvOrigin + (texOffset + layer.UvCenter * uvScale) * fitSize;
            var pHalfSize = layer.UvScale * uvScale * fitSize * 0.5f;

            var localMin = -pHalfSize;
            var localMax = pHalfSize;
            var uvMin = Vector2.Zero;
            var uvMax = Vector2.One;
            switch (layer.Clip)
            {
                case ClipMode.ClipLeft:
                    localMin.X = 0; uvMin.X = 0.5f;
                    break;
                case ClipMode.ClipRight:
                    localMax.X = 0; uvMax.X = 0.5f;
                    break;
                case ClipMode.ClipTop:
                    localMin.Y = 0; uvMin.Y = 0.5f;
                    break;
                case ClipMode.ClipBottom:
                    localMax.Y = 0; uvMax.Y = 0.5f;
                    break;
            }

            try
            {
                if (File.Exists(layer.ImagePath))
                {
                    var wrap = textureProvider.GetFromFile(layer.ImagePath).GetWrapOrDefault();
                    if (wrap != null)
                    {
                        var alpha = (uint)(layer.Opacity * 255) << 24 | 0x00FFFFFF;

                        if (MathF.Abs(layer.RotationDeg) < 0.1f)
                        {
                            drawList.AddImage(wrap.Handle,
                                pCenter + localMin, pCenter + localMax,
                                uvMin, uvMax, alpha);
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
                                new Vector2(uvMin.X, uvMin.Y), new Vector2(uvMax.X, uvMin.Y),
                                new Vector2(uvMax.X, uvMax.Y), new Vector2(uvMin.X, uvMax.Y),
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

        if (hasActiveLayer && group != null)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                var pCenter = uvOrigin + (texOffset + selectedLayer!.UvCenter * uvScale) * fitSize;
                var pHalfSize = selectedLayer.UvScale * texScreenSize * 0.5f;
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
                        var s = Math.Clamp(selectedLayer!.UvScale.X + scaleDelta, 0.01f, 2f);
                        selectedLayer.UvScale = new Vector2(s, s * texAspect);
                    }
                    else
                    {
                        selectedLayer!.UvScale = new Vector2(
                            Math.Clamp(selectedLayer.UvScale.X + scaleDelta, 0.01f, 2f),
                            Math.Clamp(selectedLayer.UvScale.Y + scaleDelta, 0.01f, 2f));
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
                if (hasActiveLayer)
                {
                    var pCenter = uvOrigin + (texOffset + selectedLayer!.UvCenter * uvScale) * fitSize;
                    var pHalfSize = selectedLayer.UvScale * texScreenSize * 0.5f;
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
                        var lh = l.UvScale * texScreenSize * 0.5f;
                        if (mousePos.X >= lc.X - lh.X && mousePos.X <= lc.X + lh.X &&
                            mousePos.Y >= lc.Y - lh.Y && mousePos.Y <= lc.Y + lh.Y)
                        {
                            group.SelectedLayerIndex = i;
                            SyncImagePathBuf();
                            canvasDraggingLayer = true;
                            break;
                        }
                    }
                }
            }

            if (canvasDraggingLayer && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && group.SelectedLayer != null)
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
            var hint1 = "左键:移动贴花  右键:缩放贴花  中键:平移画布  滚轮:缩放画布";
            var hint2 = "Shift:锁定X移动  Ctrl:锁定Y移动  Alt:右键变旋转";
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
