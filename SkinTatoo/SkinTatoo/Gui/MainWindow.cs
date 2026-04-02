using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SkinTatoo.Core;
using SkinTatoo.Http;
using SkinTatoo.Interop;
using SkinTatoo.Services;

namespace SkinTatoo.Gui;

public class MainWindow : Window, IDisposable
{
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;
    private readonly ITextureProvider textureProvider;
    private readonly FileDialogManager fileDialog = new();

    private string imagePathBuf = string.Empty;
    private int lastEditedLayerIndex = -1;
    private int layerCounter;
    private string meshLoadStatus = "";
    private bool scaleLocked = true;

    // Resource browser state
    private List<ResourceGroup>? cachedGroups;
    private bool resourceWindowOpen;

    // Canvas state
    private float canvasZoom = 1.0f;
    private Vector2 canvasPan = Vector2.Zero;
    private bool canvasDraggingLayer;
    private bool canvasPanning;
    private bool canvasScalingLayer; // right-drag scale
    private bool showHelpWindow;

    // Auto-preview debounce: delay after last change
    private bool previewDirty;
    private DateTime lastDirtyTime = DateTime.MinValue;
    private const double PreviewDebounceSec = 0.8;

    private static readonly string[] BlendModeNames = ["正常", "正片叠底", "叠加", "柔光"];
    private static readonly BlendMode[] BlendModeValues = [BlendMode.Normal, BlendMode.Multiply, BlendMode.Overlay, BlendMode.SoftLight];

    public DebugWindow? DebugWindowRef { get; set; }
    public event Action? OnSaveRequested;

    public MainWindow(
        DecalProject project,
        PreviewService previewService,
        PenumbraBridge penumbra,
        Configuration config,
        ITextureProvider textureProvider)
        : base("SkinTatoo 纹身编辑器###SkinTatooMain",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.project = project;
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.config = config;
        this.textureProvider = textureProvider;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 550),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        DrawToolbar();
        ImGui.Separator();

        var totalWidth = ImGui.GetContentRegionAvail().X;
        var totalHeight = ImGui.GetContentRegionAvail().Y;
        DrawThreePanelLayout(totalWidth, totalHeight);

        fileDialog.Draw();

        if (resourceWindowOpen)
            DrawResourceWindow();

        if (showHelpWindow)
            DrawHelpWindow();
    }

    private void DrawHelpWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(340, 320), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("操作说明###SkinTatooHelp", ref showHelpWindow))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "画布操作");
        ImGui.Separator();
        ImGui.BulletText("滚轮: 缩放画布");
        ImGui.BulletText("中键拖动: 平移画布");
        ImGui.BulletText("左键拖动: 移动贴花位置");
        ImGui.BulletText("左键点空白: 选择贴花图层");
        ImGui.BulletText("右键拖动: 缩放贴花");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "修饰键");
        ImGui.Separator();
        ImGui.BulletText("Shift + 左键拖动: 锁定 X 轴");
        ImGui.BulletText("Ctrl + 左键拖动: 锁定 Y 轴");
        ImGui.BulletText("Alt + 右键拖动: 旋转贴花");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "图层");
        ImGui.Separator();
        ImGui.BulletText("Ctrl+Shift + 删除按钮: 删除图层");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "参数面板");
        ImGui.Separator();
        ImGui.BulletText("悬浮数值上滚轮: 微调参数");

        ImGui.End();
    }

    private void DrawToolbar()
    {
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Save))
            OnSaveRequested?.Invoke();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("保存项目");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(1, FontAwesomeIcon.Bug))
        {
            if (DebugWindowRef != null)
                DebugWindowRef.IsOpen = !DebugWindowRef.IsOpen;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("调试窗口");

        ImGui.SameLine();
        using (ImRaii.Disabled(project.SelectedLayer == null))
        {
            if (ImGuiComponents.IconButton(2, FontAwesomeIcon.Undo))
                ResetSelectedLayer();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("重置当前图层参数");

        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrEmpty(config.TargetTextureGamePath)))
        {
            if (ImGuiComponents.IconButton(3, FontAwesomeIcon.Eraser))
            {
                penumbra.ClearRedirect();
                penumbra.RedrawPlayer();
                previewService.ClearTextureCache();
                DebugServer.AppendLog("[MainWindow] Restored original textures");
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("还原贴图 — 清除 Penumbra 重定向");

        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();
        var gpuColor = previewService.IsReady ? new Vector4(0, 1, 0.3f, 1) : new Vector4(1, 0.3f, 0.3f, 1);
        var penColor = penumbra.IsAvailable ? new Vector4(0, 1, 0.3f, 1) : new Vector4(1, 0.8f, 0, 1);
        ImGui.TextColored(gpuColor, previewService.IsReady ? "● GPU" : "GPU [x]");
        ImGui.SameLine();
        ImGui.TextColored(penColor, penumbra.IsAvailable ? "● Penumbra" : "Penumbra [x]");
    }

    /// <summary>Three-panel layout: left=layers, center=canvas, right=parameters.</summary>
    private void DrawThreePanelLayout(float totalWidth, float height)
    {
        var leftWidth = totalWidth * 0.20f;
        var rightWidth = 260f;
        var centerWidth = totalWidth - leftWidth - rightWidth - ImGui.GetStyle().ItemSpacing.X * 2;

        using (var left = ImRaii.Child("##LeftPanel", new Vector2(leftWidth, height), true))
        {
            if (left.Success) DrawLayerPanel();
        }

        ImGui.SameLine();

        using (var center = ImRaii.Child("##CenterPanel", new Vector2(centerWidth, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (center.Success) DrawCanvas();
        }

        ImGui.SameLine();

        using (var right = ImRaii.Child("##RightPanel", new Vector2(rightWidth, height), true))
        {
            if (right.Success) DrawParameterPanel();
        }
    }

    // ── Left Panel: Layer list ────────────────────────────────────────────────

    private void DrawLayerPanel()
    {
        ImGui.TextDisabled("投影目标");
        DrawTargetSelector();

        ImGui.Separator();

        if (ImGuiComponents.IconButton(10, FontAwesomeIcon.Plus))
        {
            layerCounter++;
            project.AddLayer($"贴花 {layerCounter}");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("添加图层");

        ImGui.SameLine();
        var io = ImGui.GetIO();
        var canDelete = project.SelectedLayerIndex >= 0 && project.Layers.Count > 0
                        && io.KeyCtrl && io.KeyShift;
        using (ImRaii.Disabled(!canDelete))
        {
            if (ImGuiComponents.IconButton(11, FontAwesomeIcon.Trash))
                project.RemoveLayer(project.SelectedLayerIndex);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("按住 Ctrl+Shift 激活删除");

        ImGui.SameLine();
        using (ImRaii.Disabled(project.SelectedLayerIndex <= 0))
        {
            if (ImGuiComponents.IconButton(12, FontAwesomeIcon.ArrowUp))
            {
                project.MoveLayerUp(project.SelectedLayerIndex);
                SyncImagePathBuf(project.SelectedLayerIndex);
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("上移图层");

        ImGui.SameLine();
        using (ImRaii.Disabled(project.SelectedLayerIndex < 0 || project.SelectedLayerIndex >= project.Layers.Count - 1))
        {
            if (ImGuiComponents.IconButton(13, FontAwesomeIcon.ArrowDown))
            {
                project.MoveLayerDown(project.SelectedLayerIndex);
                SyncImagePathBuf(project.SelectedLayerIndex);
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("下移图层");

        ImGui.SameLine();
        ImGui.TextDisabled($"({project.Layers.Count})");

        ImGui.Separator();

        using var listChild = ImRaii.Child("##LayerList", new Vector2(-1, -1), false);
        if (!listChild.Success) return;

        for (var i = 0; i < project.Layers.Count; i++)
        {
            var layer = project.Layers[i];
            ImGui.PushID(i);

            var visIcon = layer.IsVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
            var visColor = layer.IsVisible ? new Vector4(1, 1, 1, 1) : new Vector4(0.5f, 0.5f, 0.5f, 1);
            ImGui.PushStyleColor(ImGuiCol.Text, visColor);
            if (ImGuiComponents.IconButton(100 + i, visIcon))
                layer.IsVisible = !layer.IsVisible;
            ImGui.PopStyleColor();
            ImGui.SameLine();

            var isSelected = project.SelectedLayerIndex == i;
            if (ImGui.Selectable(layer.Name, isSelected))
            {
                project.SelectedLayerIndex = i;
                SyncImagePathBuf(i);
            }

            ImGui.PopID();
        }
    }

    private void DrawTargetSelector()
    {
        var hasDiffuse = !string.IsNullOrEmpty(config.TargetTextureGamePath);

        if (hasDiffuse)
        {
            var fileName = Path.GetFileName(config.TargetTextureGamePath);
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.5f, 1f), $"  {fileName}");
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text($"贴图: {config.TargetTextureGamePath}");
                if (!string.IsNullOrEmpty(config.TargetNormGamePath))
                    ImGui.Text($"法线: {config.TargetNormGamePath}");
                if (!string.IsNullOrEmpty(config.TargetMaskGamePath))
                    ImGui.Text($"遮罩: {config.TargetMaskGamePath}");
                if (!string.IsNullOrEmpty(config.TargetMeshDiskPath))
                    ImGui.Text($"网格: {Path.GetFileName(config.TargetMeshDiskPath)}");
                ImGui.EndTooltip();
            }

            var normOk = !string.IsNullOrEmpty(config.TargetNormGamePath);
            var maskOk = !string.IsNullOrEmpty(config.TargetMaskGamePath);
            var meshOk = previewService.CurrentMesh != null;
            if (normOk) { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.5f, 0.5f, 1f, 0.7f), "N"); }
            if (maskOk) { ImGui.SameLine(); ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 0.7f), "M"); }
            if (meshOk) { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.3f, 0.7f, 1f, 0.7f), "▲"); }
        }
        else
        {
            ImGui.TextColored(new Vector4(1, 0.5f, 0.3f, 1), "  未选择目标");
        }

        if (ImGui.Button("选择投影目标...", new Vector2(-1, 22)))
        {
            resourceWindowOpen = true;
            RefreshResources();
        }
    }

    // ── Center Panel: Interactive UV Canvas ───────────────────────────────────

    private void DrawCanvas()
    {
        // Canvas toolbar
        var btnH = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 80);
        ImGui.SliderFloat("##zoom", ref canvasZoom, 0.1f, 5.0f, $"{canvasZoom * 100:F0}%%");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("画布缩放 (滚轮)");
        ImGui.SameLine();
        if (ImGui.Button("适应", new Vector2(44, btnH)))
        {
            canvasZoom = 1.0f;
            canvasPan = Vector2.Zero;
        }
        ImGui.SameLine();
        if (ImGui.Button("?", new Vector2(btnH, btnH)))
            showHelpWindow = !showHelpWindow;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("操作说明");

        var avail = ImGui.GetContentRegionAvail();
        var canvasSize = new Vector2(avail.X, avail.Y);
        if (canvasSize.X < 10 || canvasSize.Y < 10) return;

        var canvasPos = ImGui.GetCursorScreenPos();

        // Fit UV square into canvas area
        var fitSize = MathF.Min(canvasSize.X, canvasSize.Y) * canvasZoom;
        var uvOrigin = canvasPos + (canvasSize - new Vector2(fitSize)) * 0.5f
                       - canvasPan * fitSize;

        // Interaction area
        ImGui.InvisibleButton("##Canvas", canvasSize);
        var isHovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        // Clip to canvas
        drawList.PushClipRect(canvasPos, canvasPos + canvasSize, true);

        // Background
        drawList.AddRectFilled(canvasPos, canvasPos + canvasSize,
            ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.1f, 1f)));

        // Checkerboard pattern for UV area
        DrawCheckerboard(drawList, uvOrigin, fitSize);

        // Draw base texture
        DrawBaseTexture(drawList, uvOrigin, fitSize);

        // Draw layer overlays
        DrawLayerOverlays(drawList, uvOrigin, fitSize);

        // UV border
        drawList.AddRect(uvOrigin, uvOrigin + new Vector2(fitSize),
            ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));

        // Rulers
        DrawRulers(drawList, canvasPos, canvasSize, uvOrigin, fitSize);

        drawList.PopClipRect();

        // Handle mouse interaction
        if (isHovered)
            HandleCanvasInput(canvasPos, canvasSize, uvOrigin, fitSize);
    }

    private void DrawRulers(ImDrawListPtr drawList, Vector2 canvasPos, Vector2 canvasSize, Vector2 uvOrigin, float fitSize)
    {
        var tickColor = ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 0.6f));
        var textColor = ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 0.8f));
        var divisions = 10;

        for (var i = 0; i <= divisions; i++)
        {
            var t = i / (float)divisions;
            var label = $"{t:F1}";

            // Horizontal ruler (top edge)
            var hx = uvOrigin.X + t * fitSize;
            if (hx >= canvasPos.X && hx <= canvasPos.X + canvasSize.X)
            {
                var tickLen = (i % 5 == 0) ? 8f : 4f;
                drawList.AddLine(new Vector2(hx, uvOrigin.Y), new Vector2(hx, uvOrigin.Y - tickLen), tickColor);
                if (i % 5 == 0)
                    drawList.AddText(new Vector2(hx + 2, uvOrigin.Y - 16), textColor, label);
            }

            // Vertical ruler (left edge)
            var vy = uvOrigin.Y + t * fitSize;
            if (vy >= canvasPos.Y && vy <= canvasPos.Y + canvasSize.Y)
            {
                var tickLen = (i % 5 == 0) ? 8f : 4f;
                drawList.AddLine(new Vector2(uvOrigin.X, vy), new Vector2(uvOrigin.X - tickLen, vy), tickColor);
                if (i % 5 == 0)
                    drawList.AddText(new Vector2(uvOrigin.X - 28, vy - 6), textColor, label);
            }
        }
    }

    private void DrawCheckerboard(ImDrawListPtr drawList, Vector2 origin, float size)
    {
        var checkerSize = 16f;
        var cols = (int)(size / checkerSize) + 1;
        var rows = cols;
        var darkColor = ImGui.GetColorU32(new Vector4(0.15f, 0.15f, 0.15f, 1f));
        var lightColor = ImGui.GetColorU32(new Vector4(0.2f, 0.2f, 0.2f, 1f));

        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var pMin = origin + new Vector2(x * checkerSize, y * checkerSize);
                var pMax = pMin + new Vector2(checkerSize);
                // Clamp to UV area
                pMin = Vector2.Max(pMin, origin);
                pMax = Vector2.Min(pMax, origin + new Vector2(size));
                if (pMin.X >= pMax.X || pMin.Y >= pMax.Y) continue;

                var color = ((x + y) % 2 == 0) ? darkColor : lightColor;
                drawList.AddRectFilled(pMin, pMax, color);
            }
        }
    }

    private void DrawBaseTexture(ImDrawListPtr drawList, Vector2 uvOrigin, float fitSize)
    {
        var texPath = config.TargetTextureDiskPath;
        if (string.IsNullOrEmpty(texPath)) return;

        try
        {
            var shared = File.Exists(texPath)
                ? textureProvider.GetFromFile(texPath)
                : textureProvider.GetFromGame(config.TargetTextureGamePath ?? "");
            var wrap = shared.GetWrapOrDefault();
            if (wrap != null)
            {
                drawList.AddImage(wrap.Handle,
                    uvOrigin, uvOrigin + new Vector2(fitSize),
                    Vector2.Zero, Vector2.One);
            }
        }
        catch { }
    }

    private void DrawLayerOverlays(ImDrawListPtr drawList, Vector2 uvOrigin, float fitSize)
    {
        for (var i = 0; i < project.Layers.Count; i++)
        {
            var layer = project.Layers[i];
            if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;

            var isSelected = project.SelectedLayerIndex == i;
            var center = layer.UvCenter;
            var scale = layer.UvScale;

            // Layer bounds in canvas pixels
            var pCenter = uvOrigin + center * fitSize;
            var pHalfSize = scale * fitSize * 0.5f;

            // Draw decal image if available
            try
            {
                if (File.Exists(layer.ImagePath))
                {
                    var wrap = textureProvider.GetFromFile(layer.ImagePath).GetWrapOrDefault();
                    if (wrap != null)
                    {
                        var pMin = pCenter - pHalfSize;
                        var pMax = pCenter + pHalfSize;

                        // Handle rotation: if no rotation, draw simple image
                        if (MathF.Abs(layer.RotationDeg) < 0.1f)
                        {
                            var alpha = (uint)(layer.Opacity * 255) << 24 | 0x00FFFFFF;
                            drawList.AddImage(wrap.Handle, pMin, pMax,
                                Vector2.Zero, Vector2.One, alpha);
                        }
                        else
                        {
                            // For rotated, draw with 4-corner UV mapping
                            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
                            var cos = MathF.Cos(rotRad);
                            var sin = MathF.Sin(rotRad);
                            Vector2 Rotate(Vector2 p) => new(
                                p.X * cos - p.Y * sin,
                                p.X * sin + p.Y * cos);

                            var tl = pCenter + Rotate(-pHalfSize);
                            var tr = pCenter + Rotate(new Vector2(pHalfSize.X, -pHalfSize.Y));
                            var br = pCenter + Rotate(pHalfSize);
                            var bl = pCenter + Rotate(new Vector2(-pHalfSize.X, pHalfSize.Y));

                            var alpha = (uint)(layer.Opacity * 255) << 24 | 0x00FFFFFF;
                            drawList.AddImageQuad(wrap.Handle,
                                tl, tr, br, bl,
                                new Vector2(0, 0), new Vector2(1, 0),
                                new Vector2(1, 1), new Vector2(0, 1),
                                alpha);
                        }
                    }
                }
            }
            catch { }

            // Draw bounding box
            var borderColor = isSelected
                ? ImGui.GetColorU32(new Vector4(1f, 0.8f, 0.2f, 1f))
                : ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 0.5f));
            var thickness = isSelected ? 2f : 1f;

            if (MathF.Abs(layer.RotationDeg) < 0.1f)
            {
                drawList.AddRect(pCenter - pHalfSize, pCenter + pHalfSize, borderColor, 0, ImDrawFlags.None, thickness);
            }
            else
            {
                var rotRad = layer.RotationDeg * (MathF.PI / 180f);
                var cos = MathF.Cos(rotRad);
                var sin = MathF.Sin(rotRad);
                Vector2 Rotate(Vector2 p) => new(
                    p.X * cos - p.Y * sin,
                    p.X * sin + p.Y * cos);

                var tl = pCenter + Rotate(-pHalfSize);
                var tr = pCenter + Rotate(new Vector2(pHalfSize.X, -pHalfSize.Y));
                var br = pCenter + Rotate(pHalfSize);
                var bl = pCenter + Rotate(new Vector2(-pHalfSize.X, pHalfSize.Y));

                drawList.AddQuad(tl, tr, br, bl, borderColor, thickness);
            }

            // Center crosshair for selected
            if (isSelected)
            {
                var cross = 6f;
                drawList.AddLine(pCenter - new Vector2(cross, 0), pCenter + new Vector2(cross, 0), borderColor, 1f);
                drawList.AddLine(pCenter - new Vector2(0, cross), pCenter + new Vector2(0, cross), borderColor, 1f);
            }
        }
    }

    private void HandleCanvasInput(Vector2 canvasPos, Vector2 canvasSize, Vector2 uvOrigin, float fitSize)
    {
        var io = ImGui.GetIO();
        var mousePos = io.MousePos;
        var selectedLayer = project.SelectedLayer;
        var hasActiveLayer = selectedLayer != null && !string.IsNullOrEmpty(selectedLayer.ImagePath);

        // ── Scroll wheel: always canvas zoom ──
        if (MathF.Abs(io.MouseWheel) > 0.01f)
            canvasZoom = Math.Clamp(canvasZoom + io.MouseWheel * 0.15f * canvasZoom, 0.1f, 10f);

        // ── Middle mouse: pan canvas ──
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
        {
            canvasPanning = true;
            canvasPan -= io.MouseDelta / fitSize;
        }
        else
        {
            canvasPanning = false;
        }

        // ── Right mouse drag: scale selected layer ──
        if (hasActiveLayer)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                var pCenter = uvOrigin + selectedLayer!.UvCenter * fitSize;
                var pHalfSize = selectedLayer.UvScale * fitSize * 0.5f;
                canvasScalingLayer = mousePos.X >= pCenter.X - pHalfSize.X && mousePos.X <= pCenter.X + pHalfSize.X &&
                                    mousePos.Y >= pCenter.Y - pHalfSize.Y && mousePos.Y <= pCenter.Y + pHalfSize.Y;
            }

            if (canvasScalingLayer && ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                var delta = io.MouseDelta;

                if (io.KeyAlt)
                {
                    // Alt + right drag: rotate
                    var rotDelta = -delta.Y * 0.5f;
                    selectedLayer!.RotationDeg = Math.Clamp(selectedLayer.RotationDeg + rotDelta, -180f, 180f);
                }
                else
                {
                    // Right drag: scale
                    var scaleDelta = delta.X * 0.003f;
                    if (scaleLocked)
                    {
                        var s = Math.Clamp(selectedLayer!.UvScale.X + scaleDelta, 0.01f, 2f);
                        selectedLayer.UvScale = new Vector2(s, s);
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

        // ── Left mouse: drag layer position ──
        if (!canvasPanning && !canvasScalingLayer)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                canvasDraggingLayer = false;
                // Try current selected layer first
                if (hasActiveLayer)
                {
                    var pCenter = uvOrigin + selectedLayer!.UvCenter * fitSize;
                    var pHalfSize = selectedLayer.UvScale * fitSize * 0.5f;
                    canvasDraggingLayer = mousePos.X >= pCenter.X - pHalfSize.X && mousePos.X <= pCenter.X + pHalfSize.X &&
                                         mousePos.Y >= pCenter.Y - pHalfSize.Y && mousePos.Y <= pCenter.Y + pHalfSize.Y;
                }

                // Try pick another layer
                if (!canvasDraggingLayer)
                {
                    for (var i = project.Layers.Count - 1; i >= 0; i--)
                    {
                        var l = project.Layers[i];
                        if (!l.IsVisible || string.IsNullOrEmpty(l.ImagePath)) continue;
                        var lc = uvOrigin + l.UvCenter * fitSize;
                        var lh = l.UvScale * fitSize * 0.5f;
                        if (mousePos.X >= lc.X - lh.X && mousePos.X <= lc.X + lh.X &&
                            mousePos.Y >= lc.Y - lh.Y && mousePos.Y <= lc.Y + lh.Y)
                        {
                            project.SelectedLayerIndex = i;
                            SyncImagePathBuf(i);
                            canvasDraggingLayer = true;
                            break;
                        }
                    }
                }
            }

            if (canvasDraggingLayer && ImGui.IsMouseDragging(ImGuiMouseButton.Left) && project.SelectedLayer != null)
            {
                var layer = project.SelectedLayer;
                var delta = io.MouseDelta / fitSize;

                // Shift: lock X axis (only move Y), Ctrl: lock Y axis (only move X)
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

        // ── Cursor ──
        if (canvasDraggingLayer)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        else if (canvasScalingLayer)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        else if (canvasPanning)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        // ── Status bar ──
        var mouseUv = (mousePos - uvOrigin) / fitSize;
        if (mouseUv.X >= 0 && mouseUv.X <= 1 && mouseUv.Y >= 0 && mouseUv.Y <= 1)
        {
            var statusText = $"UV: {mouseUv.X:F3}, {mouseUv.Y:F3}";
            if (io.KeyShift) statusText += "  [Shift: 锁X]";
            if (io.KeyCtrl) statusText += "  [Ctrl: 锁Y]";
            if (io.KeyAlt) statusText += "  [Alt: 旋转]";
            var drawList = ImGui.GetWindowDrawList();
            var textPos = canvasPos + new Vector2(4, canvasSize.Y - 18);
            drawList.AddRectFilled(textPos - new Vector2(2, 1), textPos + new Vector2(ImGui.CalcTextSize(statusText).X + 4, 16),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.7f)));
            drawList.AddText(textPos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.95f)), statusText);
        }
    }

    // ── Right Panel: Parameters ───────────────────────────────────────────────

    private void DrawParameterPanel()
    {
        var layer = project.SelectedLayer;
        if (layer == null)
        {
            ImGui.TextDisabled("选择一个图层");
            ImGui.Separator();
            DrawActionsSection();
            return;
        }

        var idx = project.SelectedLayerIndex;
        if (lastEditedLayerIndex != idx)
            SyncImagePathBuf(idx);

        ImGui.SetNextItemWidth(-1);
        var name = layer.Name;
        if (ImGui.InputText("##LayerName", ref name, 128))
            layer.Name = name;

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("贴花图片", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 30);
            if (ImGui.InputText("##ImagePath", ref imagePathBuf, 512))
            {
                layer.ImagePath = imagePathBuf;
                lastEditedLayerIndex = idx;
            }
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(20, FontAwesomeIcon.FolderOpen))
            {
                var capturedIdx = idx;
                fileDialog.OpenFileDialog(
                    "选择贴花图片",
                    "图片文件{.png,.jpg,.jpeg,.tga,.bmp,.dds}",
                    (ok, paths) =>
                    {
                        if (ok && paths.Count > 0 && capturedIdx < project.Layers.Count)
                        {
                            var path = paths[0];
                            project.Layers[capturedIdx].ImagePath = path;
                            imagePathBuf = path;
                            lastEditedLayerIndex = capturedIdx;
                            // Remember directory
                            config.LastImageDir = Path.GetDirectoryName(path);
                            config.Save();
                        }
                    },
                    1, config.LastImageDir, false);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("浏览...");
        }

        var hasImage = !string.IsNullOrEmpty(layer.ImagePath);

        using (ImRaii.Disabled(!hasImage))
        {
            if (ImGui.CollapsingHeader("UV 位置", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.SetNextItemWidth(-1);
                var center = layer.UvCenter;
                if (ImGui.DragFloat2("##中心", ref center, 0.005f, 0f, 1f, "%.3f"))
                { layer.UvCenter = center; MarkPreviewDirty(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("中心点 ");
                // Scroll adjust for center.X (DragFloat2 hover covers both)
                {
                    var cx = layer.UvCenter.X; var cy = layer.UvCenter.Y;
                    if (ScrollAdjust(ref cx, 0.001f, 0f, 1f))
                    { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }
                }

                var lockIcon = scaleLocked ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink;
                if (ImGuiComponents.IconButton(30, lockIcon))
                    scaleLocked = !scaleLocked;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(scaleLocked ? "比例锁定" : "比例解锁");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                var uvScale = layer.UvScale;
                if (scaleLocked)
                {
                    var s = uvScale.X;
                    if (ImGui.DragFloat("##scaleLocked", ref s, 0.005f, 0.01f, 2f, "%.3f"))
                    { layer.UvScale = new Vector2(s, s); MarkPreviewDirty(); }
                    if (ScrollAdjust(ref s, 0.005f, 0.01f, 2f))
                    { layer.UvScale = new Vector2(s, s); MarkPreviewDirty(); }
                }
                else
                {
                    if (ImGui.DragFloat2("##scaleUnlocked", ref uvScale, 0.005f, 0.01f, 2f, "%.3f"))
                    { layer.UvScale = uvScale; MarkPreviewDirty(); }
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("大小 ");

                ImGui.SetNextItemWidth(-1);
                var rot = layer.RotationDeg;
                if (ImGui.DragFloat("##rot", ref rot, 1f, -180f, 180f, "%.1f°"))
                { layer.RotationDeg = rot; MarkPreviewDirty(); }
                if (ScrollAdjust(ref rot, 1f, -180f, 180f))
                { layer.RotationDeg = rot; MarkPreviewDirty(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("旋转 ");
            }

            if (ImGui.CollapsingHeader("渲染", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.SetNextItemWidth(-1);
                var opacity = layer.Opacity;
                if (ImGui.DragFloat("##opacity", ref opacity, 0.01f, 0f, 1f, "%.2f"))
                { layer.Opacity = opacity; MarkPreviewDirty(); }
                if (ScrollAdjust(ref opacity, 0.02f, 0f, 1f))
                { layer.Opacity = opacity; MarkPreviewDirty(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("不透明度 ");

                ImGui.SetNextItemWidth(-1);
                var blendIdx = Array.IndexOf(BlendModeValues, layer.BlendMode);
                if (blendIdx < 0) blendIdx = 0;
                if (ImGui.Combo("##blend", ref blendIdx, BlendModeNames, BlendModeNames.Length))
                { layer.BlendMode = BlendModeValues[blendIdx]; MarkPreviewDirty(); }

                var affDiff = layer.AffectsDiffuse;
                if (ImGui.Checkbox("贴图", ref affDiff))
                { layer.AffectsDiffuse = affDiff; MarkPreviewDirty(); }
                ImGui.SameLine();
                var affMask = layer.AffectsMask;
                if (ImGui.Checkbox("高光", ref affMask))
                { layer.AffectsMask = affMask; MarkPreviewDirty(); }

                if (layer.AffectsMask)
                {
                    ImGui.SetNextItemWidth(-1);
                    var spec = layer.GlowSpecular;
                    if (ImGui.DragFloat("##spec", ref spec, 0.01f, 0f, 1f, "%.2f"))
                    { layer.GlowSpecular = spec; MarkPreviewDirty(); }
                    if (ScrollAdjust(ref spec, 0.02f, 0f, 1f))
                    { layer.GlowSpecular = spec; MarkPreviewDirty(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("镜面反射 ");

                    ImGui.SetNextItemWidth(-1);
                    var smooth = layer.GlowSmoothness;
                    if (ImGui.DragFloat("##smooth", ref smooth, 0.01f, 0f, 1f, "%.2f"))
                    { layer.GlowSmoothness = smooth; MarkPreviewDirty(); }
                    if (ScrollAdjust(ref smooth, 0.02f, 0f, 1f))
                    { layer.GlowSmoothness = smooth; MarkPreviewDirty(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("光滑度 ");
                }
            }
        }

        ImGui.Separator();
        DrawActionsSection();
    }

    private void DrawActionsSection()
    {
        var hasDiffuse = !string.IsNullOrEmpty(config.TargetTextureGamePath);
        var hasMesh = previewService.CurrentMesh != null;

        if (!hasMesh && !string.IsNullOrEmpty(config.TargetMeshDiskPath))
        {
            if (ImGui.Button("加载网格", new Vector2(-1, 24)))
            {
                var ok = previewService.LoadMesh(config.TargetMeshDiskPath);
                meshLoadStatus = ok ? $"{previewService.CurrentMesh?.Vertices.Length} 顶点" : "失败";
            }
        }

        if (!string.IsNullOrEmpty(meshLoadStatus))
            ImGui.TextDisabled(meshLoadStatus);

        var autoPreview = config.AutoPreview;
        if (ImGui.Checkbox("自动预览", ref autoPreview))
        {
            config.AutoPreview = autoPreview;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("参数变化时自动更新游戏内预览");

        if (!config.AutoPreview)
        {
            using (ImRaii.Disabled(!hasDiffuse))
            {
                if (ImGui.Button("更新预览", new Vector2(-1, 26)))
                    TriggerPreview();
            }
        }

        // Auto-preview debounce: wait until no changes for DebounceSec
        if (config.AutoPreview && previewDirty && hasDiffuse)
        {
            var elapsed = (DateTime.UtcNow - lastDirtyTime).TotalSeconds;
            if (elapsed >= PreviewDebounceSec)
            {
                previewDirty = false;
                TriggerPreview();
            }
        }
    }

    private void TriggerPreview()
    {
        var targets = new PreviewService.TextureTargets(
            config.TargetTextureGamePath, config.TargetTextureDiskPath,
            config.TargetNormGamePath, config.TargetNormDiskPath,
            config.TargetMaskGamePath, config.TargetMaskDiskPath);
        previewService.UpdatePreview(project, targets);
    }

    private void MarkPreviewDirty()
    {
        previewDirty = true;
        lastDirtyTime = DateTime.UtcNow;
    }

    /// <summary>Call after a DragFloat — if hovered, scroll wheel micro-adjusts the value.</summary>
    private static bool ScrollAdjust(ref float value, float step, float min, float max)
    {
        if (!ImGui.IsItemHovered()) return false;
        var wheel = ImGui.GetIO().MouseWheel;
        if (MathF.Abs(wheel) < 0.01f) return false;
        value = Math.Clamp(value + wheel * step, min, max);
        return true;
    }

    // ── Resource Browser Window ───────────────────────────────────────────────

    private void DrawResourceWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(780, 550), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("选择投影目标###SkinTatooResources", ref resourceWindowOpen))
        {
            ImGui.End();
            return;
        }

        if (ImGui.Button("刷新资源"))
            RefreshResources();
        ImGui.SameLine();
        ImGui.TextDisabled(penumbra.IsAvailable ? "Penumbra 已连接" : "Penumbra 未连接");

        if (!penumbra.IsAvailable)
        {
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "Penumbra 未连接");
            ImGui.End();
            return;
        }

        if (cachedGroups == null || cachedGroups.Count == 0)
        {
            ImGui.TextDisabled("点击「刷新资源」查询玩家资源");
            ImGui.End();
            return;
        }

        ImGui.Separator();

        using var scroll = ImRaii.Child("##GroupScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) { ImGui.End(); return; }

        for (var gi = 0; gi < cachedGroups.Count; gi++)
        {
            ImGui.PushID(gi);
            DrawResourceGroup(cachedGroups[gi]);
            ImGui.PopID();
        }

        ImGui.End();
    }

    private void DrawResourceGroup(ResourceGroup group)
    {
        var isSelected = group.Diffuse != null &&
                         config.TargetTextureGamePath == group.Diffuse.GamePath;

        var headerColor = isSelected
            ? new Vector4(0.2f, 0.8f, 0.4f, 1f)
            : new Vector4(0.8f, 0.8f, 0.8f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, headerColor);
        var label = isSelected ? $"★ {group.Label}" : group.Label;
        var open = ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen);
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(group.Directory))
            ImGui.SetTooltip(group.Directory);

        if (!open) return;

        ImGui.Indent(8);

        if (group.Diffuse != null)
        {
            if (isSelected)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 1f, 0.3f, 1f));
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.Text(FontAwesomeIcon.Check.ToIconString());
                ImGui.PopFont();
                ImGui.PopStyleColor();
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "当前目标");
            }
            else if (ImGui.Button("使用此组"))
                SelectGroup(group);

            ImGui.SameLine();
            ImGui.TextDisabled($"({group.EntryCount} 项)");
        }
        else
        {
            ImGui.TextDisabled($"({group.EntryCount} 项，无基础贴图)");
        }

        ImGui.Spacing();

        if (ImGui.BeginTable("##GroupItems", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableSetupColumn("##preview", ImGuiTableColumnFlags.WidthFixed, 36);
            ImGui.TableSetupColumn("##info", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 50);

            foreach (var entry in group.OrderedEntries)
            {
                ImGui.PushID(entry.GamePath);
                ImGui.TableNextRow(ImGuiTableRowFlags.None, 36);
                DrawGroupedResourceRow(entry);
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Unindent(8);
        ImGui.Spacing();
    }

    private void DrawGroupedResourceRow(ResourceEntry entry)
    {
        var (roleLabel, roleColor, roleIcon) = GetRoleInfo(entry);
        var isActive = IsEntryActive(entry);

        ImGui.TableNextColumn();
        if (isActive)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 1f, 0.3f, 1f));
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.Text(FontAwesomeIcon.Check.ToIconString());
            ImGui.PopFont();
            ImGui.PopStyleColor();
        }

        ImGui.TableNextColumn();
        if (entry.Extension == ".tex")
        {
            try
            {
                var shared = File.Exists(entry.DiskPath)
                    ? textureProvider.GetFromFile(entry.DiskPath)
                    : textureProvider.GetFromGame(entry.GamePath);
                var wrap = shared.GetWrapOrDefault();
                if (wrap != null)
                {
                    ImGui.Image(wrap.Handle, new Vector2(32, 32));
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Image(wrap.Handle, new Vector2(384, 384));
                        ImGui.Text($"{wrap.Width}x{wrap.Height}");
                        ImGui.EndTooltip();
                    }
                }
            }
            catch { }
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, roleColor);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.Text(roleIcon.ToIconString());
            ImGui.PopFont();
            ImGui.PopStyleColor();
        }

        ImGui.TableNextColumn();
        ImGui.TextColored(roleColor, $"[{roleLabel}]");
        ImGui.SameLine();
        ImGui.Text(Path.GetFileName(entry.GamePath));
        if (ImGui.IsItemClicked()) ImGui.SetClipboardText(entry.GamePath);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text(entry.GamePath);
            if (entry.DiskPath != entry.GamePath) ImGui.TextDisabled(entry.DiskPath);
            ImGui.TextDisabled("点击复制");
            ImGui.EndTooltip();
        }

        ImGui.TableNextColumn();
        if (!isActive && ImGui.SmallButton("使用"))
            SelectSingleEntry(entry);
    }

    private static (string label, Vector4 color, FontAwesomeIcon icon) GetRoleInfo(ResourceEntry entry)
    {
        return entry.Role switch
        {
            TextureRole.Diffuse => ("贴图", new Vector4(0.3f, 1f, 0.5f, 1f), FontAwesomeIcon.Image),
            TextureRole.Normal => ("法线", new Vector4(0.5f, 0.5f, 1f, 1f), FontAwesomeIcon.Image),
            TextureRole.Mask => ("遮罩", new Vector4(1f, 0.8f, 0.3f, 1f), FontAwesomeIcon.Image),
            TextureRole.Mesh => ("网格", new Vector4(0.3f, 0.7f, 1f, 1f), FontAwesomeIcon.DrawPolygon),
            TextureRole.Material => ("材质", new Vector4(0.8f, 0.6f, 0.3f, 1f), FontAwesomeIcon.Palette),
            _ => ("其他", new Vector4(0.6f, 0.6f, 0.6f, 1f), FontAwesomeIcon.File),
        };
    }

    private bool IsEntryActive(ResourceEntry entry) => entry.Role switch
    {
        TextureRole.Diffuse => config.TargetTextureGamePath == entry.GamePath,
        TextureRole.Normal => config.TargetNormGamePath == entry.GamePath,
        TextureRole.Mask => config.TargetMaskGamePath == entry.GamePath,
        TextureRole.Mesh => config.TargetMeshDiskPath == entry.DiskPath,
        _ => false,
    };

    // ── Selection logic ───────────────────────────────────────────────────────

    private void SelectGroup(ResourceGroup group)
    {
        previewService.ClearTextureCache();
        penumbra.ClearRedirect();

        if (group.Diffuse != null)
        {
            config.TargetTextureGamePath = group.Diffuse.GamePath;
            config.TargetTextureDiskPath = group.Diffuse.DiskPath;
        }
        config.TargetNormGamePath = group.Normal?.GamePath;
        config.TargetNormDiskPath = group.Normal?.DiskPath;
        config.TargetMaskGamePath = group.Mask?.GamePath;
        config.TargetMaskDiskPath = group.Mask?.DiskPath;

        if (group.Mesh != null)
        {
            previewService.LoadMesh(group.Mesh.DiskPath);
            config.TargetMeshDiskPath = group.Mesh.DiskPath;
        }
        config.Save();
        DebugServer.AppendLog($"[MainWindow] Selected group: {group.Label}");
    }

    private void SelectSingleEntry(ResourceEntry entry)
    {
        switch (entry.Role)
        {
            case TextureRole.Diffuse:
                previewService.ClearTextureCache();
                penumbra.ClearRedirect();
                config.TargetTextureGamePath = entry.GamePath;
                config.TargetTextureDiskPath = entry.DiskPath;
                break;
            case TextureRole.Normal:
                config.TargetNormGamePath = entry.GamePath;
                config.TargetNormDiskPath = entry.DiskPath;
                break;
            case TextureRole.Mask:
                config.TargetMaskGamePath = entry.GamePath;
                config.TargetMaskDiskPath = entry.DiskPath;
                break;
            case TextureRole.Mesh:
                previewService.LoadMesh(entry.DiskPath);
                config.TargetMeshDiskPath = entry.DiskPath;
                break;
        }
        config.Save();
        DebugServer.AppendLog($"[MainWindow] Selected {entry.Role}: {entry.GamePath}");
    }

    // ── Resource loading & semantic grouping ──────────────────────────────────

    private void RefreshResources()
    {
        var entries = new List<ResourceEntry>();
        var resources = penumbra.GetPlayerResources();
        if (resources == null) { cachedGroups = []; return; }

        foreach (var (objIdx, paths) in resources)
        {
            foreach (var (diskPath, gamePaths) in paths)
            {
                var ext = Path.GetExtension(diskPath).ToLowerInvariant();
                if (ext is not (".mdl" or ".tex" or ".mtrl")) continue;
                foreach (var gp in gamePaths)
                {
                    entries.Add(new ResourceEntry
                    {
                        DiskPath = diskPath, GamePath = gp,
                        Extension = ext, Role = ClassifyRole(gp, ext),
                    });
                }
            }
        }

        cachedGroups = BuildGroups(entries);
        DebugServer.AppendLog($"[MainWindow] Refreshed: {entries.Count} entries -> {cachedGroups.Count} groups");
    }

    private static TextureRole ClassifyRole(string gamePath, string ext)
    {
        if (ext == ".mdl") return TextureRole.Mesh;
        if (ext == ".mtrl") return TextureRole.Material;
        var gp = gamePath.ToLowerInvariant();
        if (gp.Contains("norm") || gp.EndsWith("_n.tex")) return TextureRole.Normal;
        if (gp.Contains("mask") || gp.EndsWith("_s.tex")) return TextureRole.Mask;
        return TextureRole.Diffuse;
    }

    private static string ClassifyBodyPart(string gamePath, string diskPath)
    {
        var gp = gamePath.ToLowerInvariant();
        if (gp.Contains("obj/body/") || (gp.Contains("e0000") && gp.Contains("_top")))
            return "body";
        if (gp.Contains("obj/face/") || gp.Contains("fac_") || gp.Contains("/face/"))
            return "face";
        if (gp.Contains("/hair/") || gp.Contains("obj/hair/"))
            return "hair";
        if (gp.Contains("/tail/") || gp.Contains("obj/tail/"))
            return "tail";
        if (gp.Contains("/zear/") || gp.Contains("obj/zear/"))
            return "ear";
        if (gp.Contains("weapon/"))
            return "weapon";
        if (gp.Contains("equipment/"))
        {
            var eIdx = gp.IndexOf("equipment/", StringComparison.Ordinal) + 10;
            var slashAfter = gp.IndexOf('/', eIdx);
            if (slashAfter > eIdx) return "equip/" + gp[eIdx..slashAfter];
        }
        var dp = diskPath.ToLowerInvariant();
        if (dp.Contains("body") || dp.Contains("_top")) return "body";
        var lastSlash = gamePath.LastIndexOf('/');
        return lastSlash > 0 ? gamePath[..lastSlash] : "other";
    }

    private static readonly Dictionary<string, string> BodyPartLabels = new()
    {
        { "body", "身体" }, { "face", "面部" }, { "hair", "头发" },
        { "tail", "尾巴" }, { "ear", "耳朵" }, { "weapon", "武器" },
    };

    private static List<ResourceGroup> BuildGroups(List<ResourceEntry> entries)
    {
        var partMap = new Dictionary<string, List<ResourceEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var part = ClassifyBodyPart(entry.GamePath, entry.DiskPath);
            if (!partMap.TryGetValue(part, out var list)) { list = []; partMap[part] = list; }
            list.Add(entry);
        }

        var groups = new List<ResourceGroup>();
        foreach (var (part, list) in partMap)
        {
            var group = new ResourceGroup { Directory = part };
            foreach (var e in list)
            {
                switch (e.Role)
                {
                    case TextureRole.Diffuse: group.Diffuse ??= e; break;
                    case TextureRole.Normal: group.Normal ??= e; break;
                    case TextureRole.Mask: group.Mask ??= e; break;
                    case TextureRole.Mesh: group.Mesh ??= e; break;
                }
            }
            group.AllEntries = list;

            string name;
            if (BodyPartLabels.TryGetValue(part, out var cn)) name = cn;
            else if (part.StartsWith("equip/")) name = $"装备 {part[6..]}";
            else name = part;
            var summary = "";
            if (group.Diffuse != null) summary += " D";
            if (group.Normal != null) summary += " N";
            if (group.Mask != null) summary += " M";
            if (group.Mesh != null) summary += " ▲";
            group.Label = $"{name}{summary}";

            groups.Add(group);
        }

        groups.Sort((a, b) =>
        {
            var ap = GetPartPriority(a.Directory);
            var bp = GetPartPriority(b.Directory);
            return ap != bp ? ap.CompareTo(bp) :
                string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });
        return groups;
    }

    private static int GetPartPriority(string part) => part switch
    {
        "body" => 0, "face" => 1, "hair" => 2,
        "tail" or "ear" => 3, "weapon" => 5,
        _ => part.StartsWith("equip/") ? 4 : 6,
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void ResetSelectedLayer()
    {
        var layer = project.SelectedLayer;
        if (layer == null) return;
        var d = new DecalLayer();
        layer.UvCenter = d.UvCenter;
        layer.UvScale = d.UvScale;
        layer.RotationDeg = d.RotationDeg;
        layer.Opacity = d.Opacity;
        layer.BlendMode = d.BlendMode;
        layer.IsVisible = d.IsVisible;
        layer.AffectsDiffuse = d.AffectsDiffuse;
    }

    private void SyncImagePathBuf(int idx)
    {
        lastEditedLayerIndex = idx;
        imagePathBuf = (idx >= 0 && idx < project.Layers.Count)
            ? (project.Layers[idx].ImagePath ?? string.Empty) : string.Empty;
    }

    public void Dispose() { }

    // ── Inner types ───────────────────────────────────────────────────────────

    private enum TextureRole { Diffuse, Normal, Mask, Mesh, Material, Other }

    private class ResourceEntry
    {
        public string DiskPath { get; init; } = "";
        public string GamePath { get; init; } = "";
        public string Extension { get; init; } = "";
        public TextureRole Role { get; init; }
    }

    private class ResourceGroup
    {
        public string Directory { get; set; } = "";
        public string Label { get; set; } = "";
        public ResourceEntry? Diffuse { get; set; }
        public ResourceEntry? Normal { get; set; }
        public ResourceEntry? Mask { get; set; }
        public ResourceEntry? Mesh { get; set; }
        public List<ResourceEntry> AllEntries { get; set; } = [];
        public int EntryCount => AllEntries.Count;
        public IEnumerable<ResourceEntry> OrderedEntries => AllEntries.OrderBy(e => e.Role switch
        {
            TextureRole.Mesh => 0, TextureRole.Diffuse => 1, TextureRole.Normal => 2,
            TextureRole.Mask => 3, TextureRole.Material => 4, _ => 5,
        });
    }
}
