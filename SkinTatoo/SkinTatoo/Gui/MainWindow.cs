using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
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
    private bool scaleLocked = true;

    // Resource browser state
    private Dictionary<ushort, ResourceTreeDto>? cachedTrees;
    private bool resourceWindowOpen;
    private bool treeExpandRequest;
    private bool treeCollapseRequest;

    // Canvas state
    private float canvasZoom = 1.0f;
    private Vector2 canvasPan = Vector2.Zero;
    private bool canvasDraggingLayer;
    private bool canvasPanning;
    private bool canvasScalingLayer;
    private bool showHelpWindow;

    // Cached base texture size for the current frame — populated in DrawBaseTexture,
    // consumed by the canvas overlay to show "WxH" in the top-right corner.
    private int lastBaseTexWidth;
    private int lastBaseTexHeight;

    // Panel widths (resizable). -1 on left means "not initialized yet — use default on first draw".
    private float leftPanelWidth = -1;
    private float rightPanelWidth = 260f;

    // Highlight glow state (toggle mode)
    private bool highlightActive;
    private int highlightGroupIndex = -1;
    private int highlightLayerIndex = -1;
    private int highlightFrameCounter;
    private const int HighlightCycleSteps = 400;

    // Initialization phase — drives the "loading..." overlay the first time the user
    // opens the window in a session.
    //
    //   Pending → frames 0-1: window chrome is shown but the background Task hasn't
    //                         started yet (gives the user 1 frame of "something is
    //                         happening" before the spinner).
    //   Loading → Task.Run is in flight; overlay + BeginDisabled freeze the UI.
    //   Done    → normal interactive draw.
    private enum InitPhase { Pending, Loading, Done }
    private InitPhase initPhase = InitPhase.Pending;
    private int initWarmupFrames;
    private Task? initTask;

    // Auto-preview
    private bool previewDirty;
    private DateTime lastDirtyTime = DateTime.MinValue;
    private DateTime firstDirtyTime = DateTime.MinValue;
    private DateTime lastGpuPreviewFireUtc = DateTime.MinValue;
    private const double PreviewDebounceFullSec = 0.5;
    private const double PreviewMaxWaitFullSec = 1.5;
    // Cap GPU swap composite rate. Each compose allocates ~16-24MB of byte[] (LOH),
    // so firing every frame during a 3D-editor drag thrashes the GC. ~33ms ≈ 30Hz
    // is plenty for visual responsiveness while halving the allocation rate.
    private const double GpuPreviewMinIntervalMs = 33;

    // v1 PBR: row pair exhaustion toast
    private string? rowPairToast;
    private DateTime rowPairToastUntil;

    private static readonly string[] BlendModeNames = ["正常", "正片叠底", "滤色", "叠加", "柔光", "强光", "变暗", "变亮", "颜色减淡", "颜色加深", "差值", "排除"];
    private static readonly BlendMode[] BlendModeValues = [BlendMode.Normal, BlendMode.Multiply, BlendMode.Screen, BlendMode.Overlay, BlendMode.SoftLight, BlendMode.HardLight, BlendMode.Darken, BlendMode.Lighten, BlendMode.ColorDodge, BlendMode.ColorBurn, BlendMode.Difference, BlendMode.Exclusion];
    private static readonly string[] LayerFadeMaskNames = ["均匀", "中心扩散", "边缘光环", "边缘描边", "方向渐变", "高斯羽化", "形状描边"];
    private static readonly string[] ClipModeNames = ["无裁剪", "切左半", "切右半", "切上半", "切下半"];

    public DebugWindow? DebugWindowRef { get; set; }
    public ConfigWindow? ConfigWindowRef { get; set; }
    public ModelEditorWindow? ModelEditorWindowRef { get; set; }
    public ModExportWindow? ModExportWindowRef { get; set; }
    public PbrInspectorWindow? PbrInspectorWindowRef { get; set; }

    /// <summary>
    /// Callback from Plugin to perform the one-shot mesh load + preview apply.
    /// Must return a Task that completes when every step (both the background mesh
    /// extraction and the main-thread Penumbra IPC hop) has finished — the MainWindow
    /// keeps the loading overlay up until the returned Task transitions to Completed.
    /// </summary>
    public Func<Task>? InitializeRequested { get; set; }

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
        // Keep the window title in sync with Penumbra's connection state so the user
        // can see at a glance whether redirects will go through.
        WindowName = penumbra.IsAvailable
            ? "SkinTatoo 纹身编辑器 [Penumbra 已连接]###SkinTatooMain"
            : "SkinTatoo 纹身编辑器 [Penumbra 未运行]###SkinTatooMain";

        // Init phase state machine (see field comment). The first two draws just render
        // the window chrome; on draw #2 we kick off the background task so the user sees
        // the spinner appear over the already-visible window.
        if (initPhase == InitPhase.Pending)
        {
            initWarmupFrames++;
            if (initWarmupFrames >= 2)
            {
                initTask = InitializeRequested?.Invoke();
                initPhase = initTask != null ? InitPhase.Loading : InitPhase.Done;
            }
        }
        else if (initPhase == InitPhase.Loading && initTask != null && initTask.IsCompleted)
        {
            if (initTask.IsFaulted)
                DebugServer.AppendLog($"[MainWindow] Init task faulted: {initTask.Exception?.GetBaseException().Message}");
            initTask = null;
            initPhase = InitPhase.Done;
        }

        var loading = initPhase != InitPhase.Done;

        // Apply any completed async GPU swaps (non-blocking)
        previewService.ApplyPendingSwaps();

        // v1 PBR: one-time migration notice (EmissiveMask → LayerFadeMask semantics widened)
        if (config.ShowLayerFadeMaskMigrationNotice)
        {
            ImGui.OpenPopup("##layerFadeMigrate");
        }
        bool modalOpen = true;
        if (ImGui.BeginPopupModal("##layerFadeMigrate", ref modalOpen,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
        {
            ImGui.TextWrapped("项目已从旧版升级。");
            ImGui.Separator();
            ImGui.TextWrapped(
                "注意：图层羽化（原\"发光遮罩\"）的行为有所变化——现在它会让该图层的所有 PBR 效果"
                + "（包括漫反射、镜面反射、粗糙度、金属度、光泽等）一起按形状渐变，而不再仅影响发光。");
            ImGui.TextWrapped("如果旧效果与预期不符，请检查图层羽化设置。");
            ImGui.Separator();
            if (ImGui.Button("我知道了", new Vector2(200, 0)))
            {
                config.ShowLayerFadeMaskMigrationNotice = false;
                config.Save();
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        // Check if 3D editor triggered a change
        if (previewService.ExternalDirty)
        {
            previewService.ExternalDirty = false;
            MarkPreviewDirty();
        }

        // Highlight: push cycling color every frame while active
        UpdateHighlight();

        // While loading, gray out and block input for every widget inside the window.
        // BeginDisabled propagates into child windows, so the three panels are frozen
        // along with the toolbar.
        if (loading) ImGui.BeginDisabled();

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

        if (loading) ImGui.EndDisabled();

        // Loading overlay is drawn on the foreground list so it lands on top of the
        // three child panels (which have their own draw lists and would otherwise
        // paint over anything we put on the main window's list).
        if (loading)
            DrawLoadingOverlay();

        // v1 PBR: row pair exhaustion toast
        if (rowPairToast != null)
        {
            if (DateTime.UtcNow < rowPairToastUntil)
            {
                var vp = ImGui.GetMainViewport();
                ImGui.SetNextWindowPos(
                    vp.WorkPos + new Vector2(vp.WorkSize.X * 0.5f, 80),
                    ImGuiCond.Always, new Vector2(0.5f, 0f));
                if (ImGui.Begin("##rowPairToast",
                    ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize
                    | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
                    | ImGuiWindowFlags.NoSavedSettings))
                {
                    ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), rowPairToast);
                }
                ImGui.End();
            }
            else
            {
                rowPairToast = null;
            }
        }
    }

    private void UpdateHighlight()
    {
        if (!highlightActive) return;
        if (highlightGroupIndex < 0 || highlightGroupIndex >= project.Groups.Count)
        { highlightActive = false; return; }

        var group = project.Groups[highlightGroupIndex];
        if (highlightLayerIndex < 0 || highlightLayerIndex >= group.Layers.Count)
        { highlightActive = false; return; }

        highlightFrameCounter++;
        var t = highlightFrameCounter;
        var hue = (t % HighlightCycleSteps) / (float)HighlightCycleSteps;
        var intensity = 1.0f + 1.0f * MathF.Sin(t * 0.03f);
        var color = TextureSwapService.HsvToRgb(hue, 1f, 1f) * intensity;
        HighlightEmissive(color, group);
    }

    private unsafe void HighlightEmissive(Vector3 color, TargetGroup? targetGroup = null)
    {
        var group = targetGroup ?? project.SelectedGroup;
        if (group == null || string.IsNullOrEmpty(group.MtrlGamePath)) return;

        var charBase = previewService.GetCharacterBase();
        if (charBase == null) return;

        previewService.HighlightEmissiveColor(charBase, group, color);
    }

    private unsafe void RestoreEmissiveAfterHighlight(TargetGroup group)
    {
        if (!group.HasEmissiveLayers())
        {
            // No emissive layers — write black to clear the highlight glow
            var charBase = previewService.GetCharacterBase();
            if (charBase != null)
                previewService.HighlightEmissiveColor(charBase, group, Vector3.Zero);
            return;
        }

        // Has emissive layers — restore the real emissive color via normal preview path
        TryDirectEmissiveUpdate(group, group.Layers.Find(l => l.IsVisible && l.AffectsEmissive)!);
    }

    /// <summary>
    /// Paint the "loading..." overlay on the foreground draw list: a semi-transparent
    /// dim over the window content plus an 8-dot rotating spinner and a Chinese label.
    /// Uses <see cref="ImGui.GetForegroundDrawList()"/> so it lands on top of the three
    /// child panels, which have their own draw lists.
    /// </summary>
    private void DrawLoadingOverlay()
    {
        var wndPos = ImGui.GetWindowPos();
        var cMin = wndPos + ImGui.GetWindowContentRegionMin();
        var cMax = wndPos + ImGui.GetWindowContentRegionMax();
        if (cMax.X <= cMin.X || cMax.Y <= cMin.Y) return;

        var fg = ImGui.GetForegroundDrawList();
        fg.AddRectFilled(cMin, cMax, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f)));

        var center = (cMin + cMax) * 0.5f;

        // Rotating dot spinner: 10 dots around a circle with a trailing fade.
        // Rotation rate and dot count tuned to look fluid without being dizzying.
        const int dotCount = 10;
        const float radius = 26f;
        const float dotRadius = 4f;
        var t = ImGui.GetTime();
        var rotation = (float)(t * 4.0);  // radians / sec
        for (var i = 0; i < dotCount; i++)
        {
            var phase = i / (float)dotCount;
            var angle = rotation + phase * MathF.PI * 2f;
            var p = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            // Brightness trails from dim to bright so the spin direction is obvious.
            var brightness = 0.15f + 0.85f * phase;
            var color = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, brightness));
            fg.AddCircleFilled(p, dotRadius, color);
        }

        // Label below the spinner
        const string label = "加载中…";
        var labelSize = ImGui.CalcTextSize(label);
        var labelPos = center + new Vector2(-labelSize.X * 0.5f, radius + 14f);
        fg.AddText(labelPos, ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.95f, 1f)), label);

        const string sub = "正在加载模型与贴花，请稍候";
        var subSize = ImGui.CalcTextSize(sub);
        var subPos = new Vector2(center.X - subSize.X * 0.5f, labelPos.Y + labelSize.Y + 4f);
        fg.AddText(subPos, ImGui.GetColorU32(new Vector4(0.75f, 0.75f, 0.8f, 1f)), sub);
    }

    private void DrawHelpWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(560, 560), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("操作说明###SkinTatooHelp", ref showHelpWindow))
        {
            ImGui.End();
            return;
        }

        var header = new Vector4(1f, 0.8f, 0.3f, 1f);

        if (ImGui.BeginTabBar("##HelpTabs"))
        {
            if (ImGui.BeginTabItem("UV 编辑器"))
            {
                ImGui.TextColored(header, "画布操作");
                ImGui.Separator();
                ImGui.BulletText("滚轮: 缩放画布");
                ImGui.BulletText("中键拖动: 平移画布");
                ImGui.BulletText("左键拖动: 移动贴花位置");
                ImGui.BulletText("左键点空白: 选择贴花图层");
                ImGui.BulletText("右键拖动: 缩放贴花");

                ImGui.Spacing();
                ImGui.TextColored(header, "修饰键");
                ImGui.Separator();
                ImGui.BulletText("Shift + 左键拖动: 锁定 X 轴");
                ImGui.BulletText("Ctrl + 左键拖动: 锁定 Y 轴");
                ImGui.BulletText("Alt + 右键拖动: 旋转贴花");

                ImGui.Spacing();
                ImGui.TextColored(header, "图层与目标");
                ImGui.Separator();
                ImGui.BulletText("Ctrl+Shift + 删除按钮: 删除图层/目标");
                ImGui.BulletText("右键投影目标标题: 复制贴图 / 法线 / 材质路径");

                ImGui.Spacing();
                ImGui.TextColored(header, "参数面板");
                ImGui.Separator();
                ImGui.BulletText("悬浮数值上滚轮: 微调参数");

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("3D 编辑器"))
            {
                ImGui.TextColored(header, "相机");
                ImGui.Separator();
                ImGui.BulletText("右键拖动: 旋转相机");
                ImGui.BulletText("中键拖动: 平移相机");
                ImGui.BulletText("Ctrl + 滚轮: 相机缩放");
                ImGui.BulletText("重置相机按钮: 回到默认视角");

                ImGui.Spacing();
                ImGui.TextColored(header, "贴花");
                ImGui.Separator();
                ImGui.BulletText("左键点击模型: 将选中图层的贴花放到该位置");
                ImGui.BulletText("滚轮: 缩放选中贴花 (非 Ctrl)");

                ImGui.Spacing();
                ImGui.TextColored(header, "模型管理");
                ImGui.Separator();
                ImGui.BulletText("添加模型: 从玩家资源树追加额外模型");
                ImGui.BulletText("管理模型: 勾选显隐 / 移除已添加模型");
                ImGui.BulletText("工具栏模型按钮: 重新加载当前投影目标的所有模型");

                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private void DrawToolbar()
    {
        if (ImGuiComponents.IconButton(10, FontAwesomeIcon.Cube))
        {
            if (ModelEditorWindowRef != null)
                ModelEditorWindowRef.IsOpen = !ModelEditorWindowRef.IsOpen;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("3D 贴花编辑器");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(11, FontAwesomeIcon.LayerGroup))
        {
            if (PbrInspectorWindowRef != null)
                PbrInspectorWindowRef.IsOpen = !PbrInspectorWindowRef.IsOpen;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("PBR 通道查看器");

        ImGui.SameLine();
        using (ImRaii.Disabled(project.SelectedLayer == null))
        {
            if (ImGuiComponents.IconButton(2, FontAwesomeIcon.Undo))
                ResetSelectedLayer();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("重置当前图层参数");

        ImGui.SameLine();
        // Restore textures: always enabled — clearing Penumbra redirects is idempotent
        // and should always be possible as an escape hatch even with no groups in the project.
        if (ImGuiComponents.IconButton(3, FontAwesomeIcon.Eraser))
        {
            penumbra.ClearRedirect();
            penumbra.RedrawPlayer();
            previewService.ClearTextureCache();
            previewService.ResetSwapState();
            DebugServer.AppendLog("[MainWindow] Restored original textures");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("还原贴图 — 清除 Penumbra 重定向");

        ImGui.SameLine();
        using (ImRaii.Disabled(project.Groups.Count == 0))
        {
            if (ImGuiComponents.IconButton(7, FontAwesomeIcon.FileExport))
                ModExportWindowRef?.OpenAs(Core.ExportTarget.LocalPmp);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("导出 Mod 到本地 (.pmp)");

        ImGui.SameLine();
        using (ImRaii.Disabled(project.Groups.Count == 0 || !penumbra.IsAvailable))
        {
            if (ImGuiComponents.IconButton(8, FontAwesomeIcon.Download))
                ModExportWindowRef?.OpenAs(Core.ExportTarget.InstallToPenumbra);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(penumbra.IsAvailable ? "安装 Mod 到 Penumbra" : "Penumbra 未运行");

        ImGui.SameLine();

        var group = project.SelectedGroup;
        if (group != null && group.AllMeshPaths.Count > 0)
        {
            var meshIcon = previewService.CurrentMesh == null ? FontAwesomeIcon.Cube : FontAwesomeIcon.SyncAlt;
            if (ImGuiComponents.IconButton(4, meshIcon))
            {
                previewService.LoadMeshes(group.AllMeshPaths);
                ModelEditorWindowRef?.OnMeshChanged();
            }
            var meshCount = group.AllMeshPaths.Count;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(previewService.CurrentMesh == null
                ? $"加载模型 ({meshCount} 个)" : $"重新加载模型 ({meshCount} 个)");
            ImGui.SameLine();
        }

        var autoPreview = config.AutoPreview;
        if (ImGui.Checkbox("自动预览", ref autoPreview))
        {
            config.AutoPreview = autoPreview;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("参数变化时自动更新游戏内预览");

        if (!config.AutoPreview)
        {
            ImGui.SameLine();
            var hasTarget = project.SelectedGroup != null && !string.IsNullOrEmpty(project.SelectedGroup.DiffuseGamePath);
            using (ImRaii.Disabled(!hasTarget))
            {
                if (ImGuiComponents.IconButton(5, FontAwesomeIcon.SyncAlt))
                    TriggerPreview();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("更新预览");
        }

        // Right-aligned cluster: 操作说明 / 调试窗口 / 设置 (gear hugs the right edge,
        // with a small padding so it doesn't touch the window border — matches the
        // layout style of the 3D editor toolbar).
        var iconWidth = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        const float rightEdgePadding = 6f;
        var rightEdge = ImGui.GetContentRegionMax().X - rightEdgePadding;
        var rightStartX = rightEdge - iconWidth * 3 - spacing * 2;

        ImGui.SameLine(rightStartX);
        if (ImGuiComponents.IconButton(15, FontAwesomeIcon.QuestionCircle))
            showHelpWindow = !showHelpWindow;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("操作说明");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(1, FontAwesomeIcon.Bug))
        {
            if (DebugWindowRef != null)
                DebugWindowRef.IsOpen = !DebugWindowRef.IsOpen;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("调试窗口");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(6, FontAwesomeIcon.Cog))
        {
            if (ConfigWindowRef != null)
                ConfigWindowRef.IsOpen = !ConfigWindowRef.IsOpen;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("插件设置");
    }

    private void DrawThreePanelLayout(float totalWidth, float height)
    {
        const float splitterWidth = 8f;
        const float minPanelWidth = 140f;

        // First-time layout: left = 20% of total, right holds the last saved width.
        if (leftPanelWidth < 0) leftPanelWidth = totalWidth * 0.20f;

        // Clamp both side panels so the center never collapses below minPanelWidth.
        var maxLeft = totalWidth - rightPanelWidth - minPanelWidth - splitterWidth * 2;
        var maxRight = totalWidth - leftPanelWidth - minPanelWidth - splitterWidth * 2;
        leftPanelWidth = Math.Clamp(leftPanelWidth, minPanelWidth, Math.Max(minPanelWidth, maxLeft));
        rightPanelWidth = Math.Clamp(rightPanelWidth, minPanelWidth, Math.Max(minPanelWidth, maxRight));
        var centerWidth = Math.Max(minPanelWidth, totalWidth - leftPanelWidth - rightPanelWidth - splitterWidth * 2);

        // Left panel
        using (var left = ImRaii.Child("##LeftPanel", new Vector2(leftPanelWidth, height), true))
        {
            if (left.Success) DrawLayerPanel();
        }

        // Left splitter (between left + center). Dragging right increases leftPanelWidth.
        ImGui.SameLine(0, 0);
        DrawSplitterLine("##SplitL", splitterWidth, height,
            delta => leftPanelWidth = Math.Clamp(leftPanelWidth + delta,
                minPanelWidth, Math.Max(minPanelWidth, totalWidth - rightPanelWidth - minPanelWidth - splitterWidth * 2)));

        ImGui.SameLine(0, 0);

        using (var center = ImRaii.Child("##CenterPanel", new Vector2(centerWidth, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (center.Success) DrawCanvas();
        }

        // Right splitter (between center + right). Dragging LEFT increases rightPanelWidth,
        // so invert the delta.
        ImGui.SameLine(0, 0);
        DrawSplitterLine("##SplitR", splitterWidth, height,
            delta => rightPanelWidth = Math.Clamp(rightPanelWidth - delta,
                minPanelWidth, Math.Max(minPanelWidth, totalWidth - leftPanelWidth - minPanelWidth - splitterWidth * 2)));

        ImGui.SameLine(0, 0);

        using (var right = ImRaii.Child("##RightPanel", new Vector2(rightPanelWidth, height), true))
        {
            if (right.Success) DrawParameterPanel();
        }
    }

    /// <summary>
    /// Draw a thin draggable splitter line. Uses <c>InvisibleButton</c> for the drag hit
    /// zone (8 px wide so the user can grab it without pixel-hunting) and manually paints
    /// a 1 px line that brightens + thickens on hover / active. Calls
    /// <paramref name="applyDelta"/> with the signed mouse-X delta each frame while active.
    /// </summary>
    private static void DrawSplitterLine(string id, float width, float height, Action<float> applyDelta)
    {
        var cursorPos = ImGui.GetCursorScreenPos();

        // No frame padding so the hit rect == the visual 8 px column.
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.InvisibleButton(id, new Vector2(width, height));
        ImGui.PopStyleVar();

        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        if (hovered || active)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);

        var drawList = ImGui.GetWindowDrawList();
        var lineColor = (hovered || active)
            ? ImGui.GetColorU32(new Vector4(0.55f, 0.75f, 1f, 1f))
            : ImGui.GetColorU32(new Vector4(0.30f, 0.30f, 0.35f, 0.70f));
        var thickness = (hovered || active) ? 2.5f : 1f;
        var lineX = cursorPos.X + width * 0.5f;
        drawList.AddLine(
            new Vector2(lineX, cursorPos.Y + 2),
            new Vector2(lineX, cursorPos.Y + height - 2),
            lineColor, thickness);

        if (active)
        {
            var delta = ImGui.GetIO().MouseDelta.X;
            if (delta != 0f)
                applyDelta(delta);
        }
    }

    // ── Left Panel: Card-based layer list ──────────────────────────────────

    private void DrawLayerPanel()
    {
        // Group-level buttons
        if (ImGuiComponents.IconButton(10, FontAwesomeIcon.Plus))
        {
            resourceWindowOpen = true;
            RefreshResources();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("添加投影目标");

        ImGui.SameLine();
        var io = ImGui.GetIO();
        var canDeleteGroup = project.SelectedGroupIndex >= 0 && io.KeyCtrl && io.KeyShift;
        using (ImRaii.Disabled(!canDeleteGroup))
        {
            if (ImGuiComponents.IconButton(11, FontAwesomeIcon.Trash))
            {
                var gi2 = project.SelectedGroupIndex;
                if (gi2 >= 0 && gi2 < project.Groups.Count)
                {
                    // Full cleanup: Penumbra only supports clearing all our redirects at once
                    // (no per-path removal), so we clear everything, drop runtime swap state,
                    // redraw, then let the next UpdatePreview rebuild for any remaining groups.
                    penumbra.ClearRedirect();
                    previewService.ClearTextureCache();
                    previewService.ResetSwapState();
                    penumbra.RedrawPlayer();
                    project.RemoveGroup(gi2);
                    DebugServer.AppendLog($"[MainWindow] Removed group {gi2}, cleared redirects");
                }
                MarkPreviewDirty();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("按住 Ctrl+Shift 删除投影目标");

        ImGui.SameLine();
        ImGui.TextDisabled($"({project.Groups.Count})");

        ImGui.Separator();

        using var listChild = ImRaii.Child("##GroupList", new Vector2(-1, -1), false);
        if (!listChild.Success) return;

        for (var gi = 0; gi < project.Groups.Count; gi++)
        {
            ImGui.PushID(gi);
            DrawGroupCard(gi);
            ImGui.PopID();
            ImGui.Spacing();
        }

        if (project.Groups.Count == 0)
            ImGui.TextDisabled("点击 + 添加投影目标");
    }

    private void DrawGroupCard(int gi)
    {
        var group = project.Groups[gi];
        var isGroupSelected = project.SelectedGroupIndex == gi;
        var drawList = ImGui.GetWindowDrawList();
        var availWidth = ImGui.GetContentRegionAvail().X;

        // ── Card header ──
        var headerStart = ImGui.GetCursorScreenPos();
        var headerHeight = ImGui.GetFrameHeight() + 4;
        var headerEnd = headerStart + new Vector2(availWidth, headerHeight);

        var headerColor = isGroupSelected && group.SelectedLayerIndex < 0
            ? ImGui.GetColorU32(new Vector4(0.20f, 0.35f, 0.55f, 1f))
            : ImGui.GetColorU32(new Vector4(0.18f, 0.18f, 0.22f, 1f));

        if (group.IsExpanded)
            drawList.AddRectFilled(headerStart, headerEnd, headerColor, 4f, ImDrawFlags.RoundCornersTop);
        else
            drawList.AddRectFilled(headerStart, headerEnd, headerColor, 4f);

        // Collapse arrow
        ImGui.SetCursorScreenPos(headerStart + new Vector2(4, 2));
        var arrowIcon = group.IsExpanded ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight;
        ImGui.PushStyleColor(ImGuiCol.Button, 0);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.1f)));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0);
        if (ImGuiComponents.IconButton(50, arrowIcon))
            group.IsExpanded = !group.IsExpanded;
        ImGui.PopStyleColor(3);

        // Group name
        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2);
        var nameWidth = availWidth - (ImGui.GetCursorScreenPos().X - headerStart.X) - 8;
        if (nameWidth < 20) nameWidth = 20;
        if (ImGui.Selectable($"{group.Name}##grpHdr", false, ImGuiSelectableFlags.None,
            new Vector2(nameWidth, ImGui.GetTextLineHeight())))
        {
            project.SelectedGroupIndex = gi;
            group.SelectedLayerIndex = -1;
        }

        // Tooltip
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            if (!string.IsNullOrEmpty(group.DiffuseGamePath))
                ImGui.Text($"贴图: {group.DiffuseGamePath}");
            if (!string.IsNullOrEmpty(group.NormGamePath))
                ImGui.Text($"法线: {group.NormGamePath}");
            if (!string.IsNullOrEmpty(group.MtrlGamePath))
                ImGui.Text($"材质: {group.MtrlGamePath}");
            ImGui.TextDisabled("(右键菜单可复制路径)");
            ImGui.EndTooltip();
        }

        // Right-click context menu: copy each of the three game paths
        if (ImGui.BeginPopupContextItem($"##grpCtx{gi}"))
        {
            var hasDiffuse = !string.IsNullOrEmpty(group.DiffuseGamePath);
            using (ImRaii.Disabled(!hasDiffuse))
                if (ImGui.MenuItem("复制贴图路径"))
                    ImGui.SetClipboardText(group.DiffuseGamePath ?? "");

            var hasNorm = !string.IsNullOrEmpty(group.NormGamePath);
            using (ImRaii.Disabled(!hasNorm))
                if (ImGui.MenuItem("复制法线路径"))
                    ImGui.SetClipboardText(group.NormGamePath ?? "");

            var hasMtrl = !string.IsNullOrEmpty(group.MtrlGamePath);
            using (ImRaii.Disabled(!hasMtrl))
                if (ImGui.MenuItem("复制材质路径"))
                    ImGui.SetClipboardText(group.MtrlGamePath ?? "");
            ImGui.EndPopup();
        }

        // Ensure cursor is past header
        ImGui.SetCursorScreenPos(new Vector2(headerStart.X, headerEnd.Y));

        if (!group.IsExpanded) return;

        // ── Card body ──
        var bodyStart = ImGui.GetCursorScreenPos();

        // Use channels: ch0 = background, ch1 = content (drawn on top)
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.Indent(6);

        // Layer toolbar
        if (ImGuiComponents.IconButton(20, FontAwesomeIcon.Plus))
        {
            project.SelectedGroupIndex = gi;
            layerCounter++;
            group.AddLayer($"贴花 {layerCounter}");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("添加贴花图层");

        ImGui.SameLine();
        var io = ImGui.GetIO();
        var canDeleteLayer = isGroupSelected && group.SelectedLayerIndex >= 0
                             && io.KeyCtrl && io.KeyShift;
        using (ImRaii.Disabled(!canDeleteLayer))
        {
            if (ImGuiComponents.IconButton(21, FontAwesomeIcon.Trash))
            {
                var idx = group.SelectedLayerIndex;
                if (idx >= 0 && idx < group.Layers.Count)
                {
                    var doomed = group.Layers[idx];
                    if (doomed.AffectsEmissive)
                        previewService.ResetSwapState();
                    previewService.ForceReleaseRowPair(group, doomed);
                }
                group.RemoveLayer(idx);
                MarkPreviewDirty();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("按住 Ctrl+Shift 删除图层");

        ImGui.SameLine();
        using (ImRaii.Disabled(!isGroupSelected || group.SelectedLayerIndex <= 0))
        {
            if (ImGuiComponents.IconButton(22, FontAwesomeIcon.ArrowUp))
            {
                group.MoveLayerUp(group.SelectedLayerIndex);
                SyncImagePathBuf();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!isGroupSelected || group.SelectedLayerIndex < 0 || group.SelectedLayerIndex >= group.Layers.Count - 1))
        {
            if (ImGuiComponents.IconButton(23, FontAwesomeIcon.ArrowDown))
            {
                group.MoveLayerDown(group.SelectedLayerIndex);
                SyncImagePathBuf();
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"({group.Layers.Count})");

        // Layer rows
        for (var li = 0; li < group.Layers.Count; li++)
        {
            var layer = group.Layers[li];
            ImGui.PushID(li + 1000);

            var isLayerSelected = isGroupSelected && group.SelectedLayerIndex == li;

            // Selected row highlight (on content channel, so it's behind text but above body bg)
            if (isLayerSelected)
            {
                var rowPos = ImGui.GetCursorScreenPos();
                drawList.AddRectFilled(
                    rowPos - new Vector2(2, 1),
                    rowPos + new Vector2(availWidth - 10, ImGui.GetFrameHeight() + 1),
                    ImGui.GetColorU32(new Vector4(0.24f, 0.42f, 0.65f, 0.5f)), 3f);
            }

            // Visibility toggle
            var visIcon = layer.IsVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
            var visColor = layer.IsVisible ? new Vector4(1, 1, 1, 1) : new Vector4(0.5f, 0.5f, 0.5f, 1);
            ImGui.PushStyleColor(ImGuiCol.Text, visColor);
            if (ImGuiComponents.IconButton(100 + li, visIcon))
            {
                layer.IsVisible = !layer.IsVisible;
                if (layer.AffectsEmissive) previewService.ResetSwapState();
                MarkPreviewDirty();
            }
            ImGui.PopStyleColor();

            // Highlight button — hover to activate, crosshair icon
            if (!string.IsNullOrEmpty(layer.ImagePath) && !string.IsNullOrEmpty(group.MtrlGamePath))
            {
                ImGui.SameLine();
                var isThisHighlighted = highlightActive && highlightGroupIndex == gi && highlightLayerIndex == li;
                if (isThisHighlighted)
                {
                    var iconHue = (highlightFrameCounter % HighlightCycleSteps) / (float)HighlightCycleSteps;
                    var ic = TextureSwapService.HsvToRgb(iconHue, 0.8f, 1f);
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(ic.X, ic.Y, ic.Z, 1f));
                }
                ImGuiComponents.IconButton(200 + li, FontAwesomeIcon.Crosshairs);
                if (isThisHighlighted) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("高亮显示贴花");
                    if (!highlightActive || highlightGroupIndex != gi || highlightLayerIndex != li)
                    {
                        if (!previewService.HasEmissiveOffset(group.MtrlGamePath))
                            previewService.EnsureEmissiveInitialized(group);
                        highlightFrameCounter = 0;
                    }
                    highlightActive = true;
                    highlightGroupIndex = gi;
                    highlightLayerIndex = li;
                }
                else if (isThisHighlighted)
                {
                    highlightActive = false;
                    highlightGroupIndex = -1;
                    highlightLayerIndex = -1;
                    highlightFrameCounter = 0;
                    // Restore original emissive color without full redraw
                    RestoreEmissiveAfterHighlight(group);
                }
            }

            ImGui.SameLine();

            if (ImGui.Selectable(layer.Name, isLayerSelected))
            {
                project.SelectedGroupIndex = gi;
                group.SelectedLayerIndex = li;
                SyncImagePathBuf();
            }

            ImGui.PopID();
        }

        ImGui.Unindent(6);
        ImGui.Spacing();

        // Draw body background on channel 0 (behind content)
        var bodyEnd = ImGui.GetCursorScreenPos();
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(
            bodyStart,
            new Vector2(headerStart.X + availWidth, bodyEnd.Y),
            ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.15f, 1f)), 4f, ImDrawFlags.RoundCornersBottom);
        // Card outline
        drawList.AddRect(
            headerStart,
            new Vector2(headerStart.X + availWidth, bodyEnd.Y),
            ImGui.GetColorU32(new Vector4(0.28f, 0.28f, 0.32f, 1f)), 4f);
        drawList.ChannelsMerge();
    }

    // ── Center Panel: Interactive UV Canvas ──────────────────────────────────

    private void DrawCanvas()
    {
        var btnH = ImGui.GetFrameHeight();
        ImGui.AlignTextToFramePadding();
        ImGui.Text("UV 缩放");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 52);
        ImGui.SliderFloat("##zoom", ref canvasZoom, 0.1f, 5.0f, $"{canvasZoom * 100:F0}%%");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("画布缩放 (滚轮)");
        ImGui.SameLine();
        if (ImGui.Button("适应", new Vector2(44, btnH)))
        {
            canvasZoom = 1.0f;
            canvasPan = Vector2.Zero;
        }

        var avail = ImGui.GetContentRegionAvail();
        var canvasSize = new Vector2(avail.X, avail.Y);
        if (canvasSize.X < 10 || canvasSize.Y < 10) return;

        var canvasPos = ImGui.GetCursorScreenPos();
        var fitSize = MathF.Min(canvasSize.X, canvasSize.Y) * canvasZoom;
        var uvOrigin = canvasPos + (canvasSize - new Vector2(fitSize)) * 0.5f
                       - canvasPan * fitSize;

        ImGui.InvisibleButton("##Canvas", canvasSize);
        var isHovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();

        drawList.PushClipRect(canvasPos, canvasPos + canvasSize, true);

        drawList.AddRectFilled(canvasPos, canvasPos + canvasSize,
            ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.1f, 1f)));

        DrawCheckerboard(drawList, uvOrigin, fitSize);
        DrawBaseTexture(drawList, uvOrigin, fitSize);
        DrawLayerOverlays(drawList, uvOrigin, fitSize);

        drawList.AddRect(uvOrigin, uvOrigin + new Vector2(fitSize),
            ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));

        DrawRulers(drawList, canvasPos, canvasSize, uvOrigin, fitSize);

        drawList.PopClipRect();

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

            var hx = uvOrigin.X + t * fitSize;
            if (hx >= canvasPos.X && hx <= canvasPos.X + canvasSize.X)
            {
                var tickLen = (i % 5 == 0) ? 8f : 4f;
                drawList.AddLine(new Vector2(hx, uvOrigin.Y), new Vector2(hx, uvOrigin.Y - tickLen), tickColor);
                if (i % 5 == 0)
                    drawList.AddText(new Vector2(hx + 2, uvOrigin.Y - 16), textColor, label);
            }

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
                    uvOrigin, uvOrigin + new Vector2(fitSize),
                    Vector2.Zero, Vector2.One);
            }
        }
        catch { }
    }

    private void DrawLayerOverlays(ImDrawListPtr drawList, Vector2 uvOrigin, float fitSize)
    {
        var group = project.SelectedGroup;
        if (group == null) return;

        for (var i = 0; i < group.Layers.Count; i++)
        {
            var layer = group.Layers[i];
            if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;

            var isSelected = group.SelectedLayerIndex == i;
            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var pCenter = uvOrigin + center * fitSize;
            var pHalfSize = scale * fitSize * 0.5f;

            // Apply clip to local-space half extents and UV coords
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

            // Bounding box
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

    private void HandleCanvasInput(Vector2 canvasPos, Vector2 canvasSize, Vector2 uvOrigin, float fitSize)
    {
        var io = ImGui.GetIO();
        var mousePos = io.MousePos;
        var group = project.SelectedGroup;
        var selectedLayer = group?.SelectedLayer;
        var hasActiveLayer = selectedLayer != null && !string.IsNullOrEmpty(selectedLayer.ImagePath);

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
                    var rotDelta = -delta.Y * 0.5f;
                    selectedLayer!.RotationDeg = Math.Clamp(selectedLayer.RotationDeg + rotDelta, -180f, 180f);
                }
                else
                {
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

        if (!canvasPanning && !canvasScalingLayer && group != null)
        {
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                canvasDraggingLayer = false;
                if (hasActiveLayer)
                {
                    var pCenter = uvOrigin + selectedLayer!.UvCenter * fitSize;
                    var pHalfSize = selectedLayer.UvScale * fitSize * 0.5f;
                    canvasDraggingLayer = mousePos.X >= pCenter.X - pHalfSize.X && mousePos.X <= pCenter.X + pHalfSize.X &&
                                         mousePos.Y >= pCenter.Y - pHalfSize.Y && mousePos.Y <= pCenter.Y + pHalfSize.Y;
                }

                if (!canvasDraggingLayer)
                {
                    for (var i = group.Layers.Count - 1; i >= 0; i--)
                    {
                        var l = group.Layers[i];
                        if (!l.IsVisible || string.IsNullOrEmpty(l.ImagePath)) continue;
                        var lc = uvOrigin + l.UvCenter * fitSize;
                        var lh = l.UvScale * fitSize * 0.5f;
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
                var delta = io.MouseDelta / fitSize;

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

        var mouseUv = (mousePos - uvOrigin) / fitSize;
        var drawList = ImGui.GetWindowDrawList();
        var bgColor = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f));
        var textColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.9f));
        var dimColor = ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.7f, 0.7f));

        // Bottom-left: mouse UV
        if (mouseUv.X >= 0 && mouseUv.X <= 1 && mouseUv.Y >= 0 && mouseUv.Y <= 1)
        {
            var uvText = $"UV: {mouseUv.X:F3}, {mouseUv.Y:F3}";
            var textPos = canvasPos + new Vector2(4, canvasSize.Y - 20);
            var textSize = ImGui.CalcTextSize(uvText);
            drawList.AddRectFilled(textPos - new Vector2(2, 1), textPos + new Vector2(textSize.X + 4, 17), bgColor);
            drawList.AddText(textPos, textColor, uvText);
        }

        // Bottom-right: operation hints (stacked lines)
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

        // Top-right: base texture resolution (e.g. "4096 x 4096")
        if (lastBaseTexWidth > 0 && lastBaseTexHeight > 0)
        {
            var resText = $"{lastBaseTexWidth} x {lastBaseTexHeight}";
            var resSize = ImGui.CalcTextSize(resText);
            var resPos = canvasPos + new Vector2(canvasSize.X - resSize.X - 6, 4);
            drawList.AddRectFilled(resPos - new Vector2(2, 1), resPos + new Vector2(resSize.X + 4, 17), bgColor);
            drawList.AddText(resPos, textColor, resText);
        }
    }

    // ── Right Panel: Parameters ──────────────────────────────────────────────

    private void DrawParameterPanel()
    {
        var group = project.SelectedGroup;
        var layer = group?.SelectedLayer;
        if (layer == null)
        {
            if (group != null)
                ImGui.TextDisabled("选择或添加一个图层");
            else
                ImGui.TextDisabled("先添加一个投影目标");
            ImGui.Separator();
            DrawActionsSection();
            return;
        }

        var idx = group!.SelectedLayerIndex;
        if (lastEditedLayerIndex != idx)
            SyncImagePathBuf();

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
                AutoFitLayerScale(group, layer);
                lastEditedLayerIndex = idx;
                MarkPreviewDirty();
            }
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(20, FontAwesomeIcon.FolderOpen))
            {
                var capturedGi = project.SelectedGroupIndex;
                var capturedLi = idx;
                fileDialog.OpenFileDialog(
                    "选择贴花图片",
                    "图片文件{.png,.jpg,.jpeg,.tga,.bmp,.dds}",
                    (ok, paths) =>
                    {
                        if (ok && paths.Count > 0 && capturedGi < project.Groups.Count)
                        {
                            var g = project.Groups[capturedGi];
                            if (capturedLi < g.Layers.Count)
                            {
                                var path = paths[0];
                                var picked = g.Layers[capturedLi];
                                picked.ImagePath = path;
                                AutoFitLayerScale(g, picked);
                                imagePathBuf = path;
                                lastEditedLayerIndex = capturedLi;
                                config.LastImageDir = Path.GetDirectoryName(path);
                                config.Save();
                                MarkPreviewDirty();
                            }
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
                var labelW = 56f;
                var cx = layer.UvCenter.X;
                var cy = layer.UvCenter.Y;
                ImGui.AlignTextToFramePadding(); ImGui.Text("中心 X"); ImGui.SameLine(labelW);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.DragFloat("##centerX", ref cx, 0.005f, 0f, 1f, "%.3f"))
                { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }
                if (ScrollAdjust(ref cx, 0.001f, 0f, 1f))
                { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("中心点 X");
                ImGui.AlignTextToFramePadding(); ImGui.Text("中心 Y"); ImGui.SameLine(labelW);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.DragFloat("##centerY", ref cy, 0.005f, 0f, 1f, "%.3f"))
                { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }
                if (ScrollAdjust(ref cy, 0.001f, 0f, 1f))
                { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("中心点 Y");

                ImGui.AlignTextToFramePadding(); ImGui.Text("大小"); ImGui.SameLine(labelW);
                var uvScale = layer.UvScale;
                var lockBtnWidth = ImGui.GetFrameHeight();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - lockBtnWidth - ImGui.GetStyle().ItemSpacing.X);
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
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("大小");
                ImGui.SameLine();
                var lockIcon = scaleLocked ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink;
                if (ImGuiComponents.IconButton(30, lockIcon))
                    scaleLocked = !scaleLocked;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(scaleLocked ? "比例锁定" : "比例解锁");

                ImGui.AlignTextToFramePadding(); ImGui.Text("旋转"); ImGui.SameLine(labelW);
                ImGui.SetNextItemWidth(-1);
                var rot = layer.RotationDeg;
                if (ImGui.DragFloat("##rot", ref rot, 1f, -180f, 180f, "%.1f°"))
                { layer.RotationDeg = rot; MarkPreviewDirty(); }
                if (ScrollAdjust(ref rot, 1f, -180f, 180f))
                { layer.RotationDeg = rot; MarkPreviewDirty(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("旋转");
            }

            if (ImGui.CollapsingHeader("渲染", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var labelW2 = 56f;
                ImGui.AlignTextToFramePadding(); ImGui.Text("透明度"); ImGui.SameLine(labelW2);
                ImGui.SetNextItemWidth(-1);
                var opacity = layer.Opacity;
                if (ImGui.DragFloat("##opacity", ref opacity, 0.01f, 0f, 1f, "%.2f"))
                { layer.Opacity = opacity; MarkPreviewDirty(); }
                if (ScrollAdjust(ref opacity, 0.02f, 0f, 1f))
                { layer.Opacity = opacity; MarkPreviewDirty(); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("不透明度");

                ImGui.AlignTextToFramePadding(); ImGui.Text("混合"); ImGui.SameLine(labelW2);
                ImGui.SetNextItemWidth(-1);
                var blendIdx = Array.IndexOf(BlendModeValues, layer.BlendMode);
                if (blendIdx < 0) blendIdx = 0;
                if (ImGui.Combo("##blend", ref blendIdx, BlendModeNames, BlendModeNames.Length))
                { layer.BlendMode = BlendModeValues[blendIdx]; MarkPreviewDirty(); }

                ImGui.AlignTextToFramePadding(); ImGui.Text("裁剪"); ImGui.SameLine(labelW2);
                ImGui.SetNextItemWidth(-1);
                var clipIdx = (int)layer.Clip;
                if (ImGui.Combo("##clip", ref clipIdx, ClipModeNames, ClipModeNames.Length))
                { layer.Clip = (ClipMode)clipIdx; MarkPreviewDirty(); }
            }

            // ── PBR 属性 (v1) ────────────────────────────────
            if (ImGui.CollapsingHeader("PBR 属性", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var alloc = previewService.GetOrCreateAllocator(group);
                bool exhausted = alloc.AvailableSlots == 0 && layer.AllocatedRowPair < 0;
                bool pbrSupported = previewService.MaterialSupportsPbr(group);

                if (!pbrSupported && !string.IsNullOrEmpty(group.MtrlGamePath))
                {
                    ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f),
                        "当前材质（skin.shpk 类）无 ColorTable");
                    ImGui.TextDisabled("仅「贴花」与「发光」可用，其余字段已置灰");
                }
                else if (exhausted)
                {
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
                        $"该材质 PBR 行号已满（vanilla 占 {alloc.VanillaOccupiedCount}）");
                    ImGui.TextDisabled("请关闭其他图层的 PBR 字段后再试");
                }

                // Two-column table: fixed "checkbox + label" on the left, stretching value
                // widget on the right. Every row is exactly one line, aligned consistently.
                if (ImGui.BeginTable("##PbrFields", 2,
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoPadInnerX))
                {
                    ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed, 104);
                    ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch);

                    // 贴花/漫反射: decal image composite always works; DiffuseColor tint only
                    // takes effect on character.shpk where a row pair exists.
                    BeginPbrRow();
                    DrawPbrRowStart(group, layer, "贴花/漫反射",
                        () => layer.AffectsDiffuse, v => layer.AffectsDiffuse = v,
                        supported: true, requiresRowPair: pbrSupported, exhausted: exhausted);
                    ImGui.TableNextColumn();
                    using (ImRaii.Disabled(!pbrSupported))
                    {
                        ImGui.SetNextItemWidth(-1);
                        var d = layer.DiffuseColor;
                        if (ImGui.ColorEdit3("##diffColor", ref d, ImGuiColorEditFlags.NoLabel))
                        { layer.DiffuseColor = d; MarkPreviewDirty(); }
                        if (ImGui.IsItemHovered() && !pbrSupported)
                            ImGui.SetTooltip("skin.shpk 材质不支持漫反射颜色调制 (贴花图像仍会显示)");
                    }

                    BeginPbrRow();
                    DrawPbrRowStart(group, layer, "镜面反射",
                        () => layer.AffectsSpecular, v => layer.AffectsSpecular = v,
                        supported: pbrSupported, requiresRowPair: true, exhausted: exhausted);
                    ImGui.TableNextColumn();
                    using (ImRaii.Disabled(!pbrSupported))
                    {
                        ImGui.SetNextItemWidth(-1);
                        var sc = layer.SpecularColor;
                        if (ImGui.ColorEdit3("##specColor", ref sc, ImGuiColorEditFlags.NoLabel))
                        { layer.SpecularColor = sc; MarkPreviewDirty(); }
                    }

                    // Emissive: on skin.shpk bypasses row-pair allocation (CBuffer hook + normal.a);
                    // on character.shpk goes through row pair → ColorTable.
                    // Toggling OFF must clear the hook target and drop the mtrl/norm init so the
                    // next UpdatePreview rebinds vanilla files — otherwise the body keeps glowing.
                    BeginPbrRow();
                    DrawPbrRowStart(group, layer, "发光",
                        () => layer.AffectsEmissive, v => layer.AffectsEmissive = v,
                        supported: true, requiresRowPair: pbrSupported, exhausted: exhausted,
                        onDisabled: () => previewService.InvalidateEmissiveForGroup(group));
                    ImGui.TableNextColumn();
                    {
                        var emColor = layer.EmissiveColor;
                        if (ImGui.ColorEdit3("##emColor", ref emColor,
                            ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.NoInputs))
                        { layer.EmissiveColor = emColor; MarkPreviewDirty(); TryDirectEmissiveUpdate(group, layer); }
                        ImGui.SameLine();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextDisabled("强度");
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(-1);
                        var emI = layer.EmissiveIntensity;
                        if (ImGui.DragFloat("##emI", ref emI, 0.05f, 0.1f, 10f, "%.2f"))
                        { layer.EmissiveIntensity = emI; MarkPreviewDirty(); TryDirectEmissiveUpdate(group, layer); }
                    }

                    BeginPbrRow();
                    DrawPbrRowStart(group, layer, "粗糙度",
                        () => layer.AffectsRoughness, v => layer.AffectsRoughness = v,
                        supported: pbrSupported, requiresRowPair: true, exhausted: exhausted);
                    ImGui.TableNextColumn();
                    using (ImRaii.Disabled(!pbrSupported))
                    {
                        ImGui.SetNextItemWidth(-1);
                        var r = layer.Roughness;
                        if (ImGui.SliderFloat("##rough", ref r, 0f, 1f, "%.2f"))
                        { layer.Roughness = r; MarkPreviewDirty(); }
                    }

                    BeginPbrRow();
                    DrawPbrRowStart(group, layer, "金属度",
                        () => layer.AffectsMetalness, v => layer.AffectsMetalness = v,
                        supported: pbrSupported, requiresRowPair: true, exhausted: exhausted);
                    ImGui.TableNextColumn();
                    using (ImRaii.Disabled(!pbrSupported))
                    {
                        ImGui.SetNextItemWidth(-1);
                        var mt = layer.Metalness;
                        if (ImGui.SliderFloat("##metal", ref mt, 0f, 1f, "%.2f"))
                        { layer.Metalness = mt; MarkPreviewDirty(); }
                    }

                    // Sheen: three mini sliders sharing the value column, labels embedded in format.
                    BeginPbrRow();
                    DrawPbrRowStart(group, layer, "光泽",
                        () => layer.AffectsSheen, v => layer.AffectsSheen = v,
                        supported: pbrSupported, requiresRowPair: true, exhausted: exhausted);
                    ImGui.TableNextColumn();
                    using (ImRaii.Disabled(!pbrSupported))
                    {
                        var avail = ImGui.GetContentRegionAvail().X;
                        var third = (avail - ImGui.GetStyle().ItemSpacing.X * 2) / 3f;
                        ImGui.SetNextItemWidth(third);
                        var sr = layer.SheenRate;
                        if (ImGui.SliderFloat("##sheenRate", ref sr, 0f, 1f, "R %.2f"))
                        { layer.SheenRate = sr; MarkPreviewDirty(); }
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(third);
                        var st = layer.SheenTint;
                        if (ImGui.SliderFloat("##sheenTint", ref st, 0f, 1f, "T %.2f"))
                        { layer.SheenTint = st; MarkPreviewDirty(); }
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(-1);
                        var sa = layer.SheenAperture;
                        if (ImGui.SliderFloat("##sheenAp", ref sa, 0f, 20f, "A %.1f"))
                        { layer.SheenAperture = sa; MarkPreviewDirty(); }
                    }

                    ImGui.EndTable();
                }

                // Layer fade mask: separate block styled like the render section —
                // left label + full-width widget, no checkbox column.
                ImGui.Spacing();
                ImGui.TextDisabled("图层羽化（影响所有 PBR 字段）");
                if (ImGui.BeginTable("##PbrFade", 2,
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoPadInnerX))
                {
                    ImGui.TableSetupColumn("##flab", ImGuiTableColumnFlags.WidthFixed, 56);
                    ImGui.TableSetupColumn("##fval", ImGuiTableColumnFlags.WidthStretch);

                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding(); ImGui.Text("形状");
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    var maskIdx = (int)layer.FadeMask;
                    if (ImGui.Combo("##fadeMask", ref maskIdx, LayerFadeMaskNames, LayerFadeMaskNames.Length))
                    { layer.FadeMask = (LayerFadeMask)maskIdx; MarkPreviewDirty(); }

                    if (layer.FadeMask != LayerFadeMask.Uniform)
                    {
                        ImGui.TableNextRow(); ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding(); ImGui.Text("羽化");
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1);
                        var f = layer.FadeMaskFalloff;
                        if (ImGui.SliderFloat("##fadeFalloff", ref f, 0.01f, 1f, "%.2f"))
                        { layer.FadeMaskFalloff = f; MarkPreviewDirty(); }
                    }

                    if (layer.FadeMask == LayerFadeMask.DirectionalGradient)
                    {
                        ImGui.TableNextRow(); ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding(); ImGui.Text("角度");
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1);
                        var a = layer.GradientAngleDeg;
                        if (ImGui.SliderFloat("##gAng", ref a, -180f, 180f, "%.1f°"))
                        { layer.GradientAngleDeg = a; MarkPreviewDirty(); }

                        ImGui.TableNextRow(); ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding(); ImGui.Text("范围");
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1);
                        var gs = layer.GradientScale;
                        if (ImGui.SliderFloat("##gScl", ref gs, 0.1f, 2f, "%.2f"))
                        { layer.GradientScale = gs; MarkPreviewDirty(); }

                        ImGui.TableNextRow(); ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding(); ImGui.Text("偏移");
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-1);
                        var go = layer.GradientOffset;
                        if (ImGui.SliderFloat("##gOff", ref go, -1f, 1f, "%.2f"))
                        { layer.GradientOffset = go; MarkPreviewDirty(); }
                    }
                    ImGui.EndTable();
                }

                if (layer.FadeMask != LayerFadeMask.Uniform)
                    DrawFadeMaskPreview(layer.FadeMask, layer.FadeMaskFalloff, layer);

                if (string.IsNullOrEmpty(group.MtrlGamePath))
                    ImGui.TextColored(new Vector4(1, 0.5f, 0.3f, 1), "需要选择材质(.mtrl)");
            }
        }

        ImGui.Separator();
        DrawActionsSection();
    }

    private void DrawFadeMaskPreview(LayerFadeMask mask, float falloff, DecalLayer layer)
    {
        ImGui.TextDisabled("遮罩预览:");
        var previewSize = 100f;
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var cellCount = 32;
        var cellSize = previewSize / cellCount;

        for (int y = 0; y < cellCount; y++)
        {
            for (int x = 0; x < cellCount; x++)
            {
                float ru = (x + 0.5f) / cellCount - 0.5f;
                float rv = (y + 0.5f) / cellCount - 0.5f;
                float val;
                if (mask == LayerFadeMask.DirectionalGradient)
                    val = PreviewService.ComputeDirectionalGradient(ru, rv, 1f,
                        layer.GradientAngleDeg, layer.GradientScale, falloff, layer.GradientOffset);
                else if (mask == LayerFadeMask.ShapeOutline)
                {
                    // Approximate shape outline in preview: use distance to center circle as alpha proxy
                    float fakeDa = (MathF.Abs(ru) < 0.3f && MathF.Abs(rv) < 0.3f) ? 1f : 0f;
                    float fakeNeighbor = ((MathF.Abs(ru) < 0.29f && MathF.Abs(rv) < 0.29f) ||
                                          (MathF.Abs(ru) > 0.31f || MathF.Abs(rv) > 0.31f)) ? fakeDa : 0.5f;
                    val = PreviewService.ComputeShapeOutline(fakeDa, falloff, fakeNeighbor);
                }
                else
                    val = PreviewService.ComputeFadeMaskWeight(mask, falloff, ru, rv, 1f);

                var color = ImGui.GetColorU32(new Vector4(val, val, val, 1f));
                var p0 = pos + new Vector2(x * cellSize, y * cellSize);
                var p1 = p0 + new Vector2(cellSize + 0.5f, cellSize + 0.5f);
                drawList.AddRectFilled(p0, p1, color);
            }
        }

        drawList.AddRect(pos, pos + new Vector2(previewSize),
            ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));

        // Color preview showing actual emissive color through mask
        var colorPos = pos + new Vector2(previewSize + 8, 0);
        var emColor = layer.EmissiveColor * layer.EmissiveIntensity;
        for (int y = 0; y < cellCount; y++)
        {
            for (int x = 0; x < cellCount; x++)
            {
                float ru = (x + 0.5f) / cellCount - 0.5f;
                float rv = (y + 0.5f) / cellCount - 0.5f;
                float val;
                if (mask == LayerFadeMask.DirectionalGradient)
                    val = PreviewService.ComputeDirectionalGradient(ru, rv, 1f,
                        layer.GradientAngleDeg, layer.GradientScale, falloff, layer.GradientOffset);
                else if (mask == LayerFadeMask.ShapeOutline)
                {
                    // Approximate shape outline in preview: use distance to center circle as alpha proxy
                    float fakeDa = (MathF.Abs(ru) < 0.3f && MathF.Abs(rv) < 0.3f) ? 1f : 0f;
                    float fakeNeighbor = ((MathF.Abs(ru) < 0.29f && MathF.Abs(rv) < 0.29f) ||
                                          (MathF.Abs(ru) > 0.31f || MathF.Abs(rv) > 0.31f)) ? fakeDa : 0.5f;
                    val = PreviewService.ComputeShapeOutline(fakeDa, falloff, fakeNeighbor);
                }
                else
                    val = PreviewService.ComputeFadeMaskWeight(mask, falloff, ru, rv, 1f);

                var cr = Math.Min(emColor.X * val, 1f);
                var cg = Math.Min(emColor.Y * val, 1f);
                var cb = Math.Min(emColor.Z * val, 1f);
                var color = ImGui.GetColorU32(new Vector4(cr, cg, cb, 1f));
                var p0 = colorPos + new Vector2(x * cellSize, y * cellSize);
                var p1 = p0 + new Vector2(cellSize + 0.5f, cellSize + 0.5f);
                drawList.AddRectFilled(p0, p1, color);
            }
        }

        drawList.AddRect(colorPos, colorPos + new Vector2(previewSize),
            ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));
        ImGui.Dummy(new Vector2(previewSize * 2 + 8, previewSize + 4));

        var textY = pos.Y + previewSize + 2;
        drawList.AddText(new Vector2(pos.X, textY),
            ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), "遮罩");
        drawList.AddText(new Vector2(colorPos.X, textY),
            ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), "效果");
        ImGui.Dummy(new Vector2(0, 14));
    }

    private void DrawActionsSection()
    {
        var group = project.SelectedGroup;
        var hasTarget = group != null && !string.IsNullOrEmpty(group.DiffuseGamePath);

        if (config.AutoPreview && previewDirty && hasTarget)
        {
            if (previewService.CanSwapInPlace && config.UseGpuSwap)
            {
                // GPU swap mode: throttle to ~30Hz so a sustained drag doesn't allocate
                // ~20MB/frame and thrash the GC. previewDirty stays true until the
                // throttle window passes, so the latest state is always picked up.
                var now = DateTime.UtcNow;
                if ((now - lastGpuPreviewFireUtc).TotalMilliseconds >= GpuPreviewMinIntervalMs)
                {
                    previewDirty = false;
                    lastGpuPreviewFireUtc = now;
                    TriggerPreview();
                }
            }
            else
            {
                // Full redraw mode: debounce to avoid flicker spam,
                // but force update after max wait to handle continuous scrolling
                var now = DateTime.UtcNow;
                var sinceLastChange = (now - lastDirtyTime).TotalSeconds;
                var sinceFirstChange = (now - firstDirtyTime).TotalSeconds;
                if (sinceLastChange >= PreviewDebounceFullSec || sinceFirstChange >= PreviewMaxWaitFullSec)
                {
                    previewDirty = false;
                    TriggerPreview();
                }
            }
        }
    }

    private void TriggerPreview()
    {
        // Auto-detect mtrl for groups that need emissive
        foreach (var group in project.Groups)
        {
            if (group.HasEmissiveLayers() && string.IsNullOrEmpty(group.MtrlGamePath))
                AutoDetectMtrl(group);
        }

        previewService.UpdatePreview(project);
        ModelEditorWindowRef?.MarkTexturesDirty();
    }

    private void AutoDetectMtrl(TargetGroup group)
    {
        // Try finding mtrl from the resource tree by locating the equipment node
        // that contains our diffuse texture, then grabbing its mtrl descendant
        var trees = penumbra.GetPlayerTrees();
        if (trees == null || string.IsNullOrEmpty(group.DiffuseGamePath))
        {
            DebugServer.AppendLog($"[AutoDetect] Skip: trees={trees != null} diffuse={group.DiffuseGamePath}");
            return;
        }

        var diffuseNorm = group.DiffuseGamePath.Replace('\\', '/').ToLowerInvariant();

        foreach (var (_, tree) in trees)
        {
            foreach (var topNode in tree.Nodes)
            {
                // Case-insensitive match for diffuse texture
                var diffuse = FindDescendant(topNode, n =>
                    n.Type == ResourceType.Tex &&
                    (n.GamePath ?? "").Replace('\\', '/').ToLowerInvariant() == diffuseNorm);
                if (diffuse == null) continue;

                // Found the tree node with our diffuse — find mtrl sibling/parent
                var mtrl = FindDescendant(topNode, n =>
                    n.Type == ResourceType.Mtrl && !n.ActualPath.Contains("pluginConfigs"));
                if (mtrl != null)
                {
                    group.MtrlGamePath = mtrl.GamePath ?? "";
                    group.MtrlDiskPath = mtrl.ActualPath;
                    group.OrigMtrlDiskPath ??= mtrl.ActualPath;
                    DebugServer.AppendLog($"[AutoDetect] mtrl for {group.Name}: {mtrl.GamePath}");
                }

                // Also find norm if missing
                if (string.IsNullOrEmpty(group.NormGamePath))
                {
                    var norm = FindDescendant(topNode, n =>
                        n.Type == ResourceType.Tex &&
                        (n.GamePath ?? "").Contains("_norm") &&
                        !n.ActualPath.Contains("pluginConfigs"));
                    if (norm != null)
                    {
                        group.NormGamePath = norm.GamePath ?? "";
                        group.NormDiskPath = norm.ActualPath;
                        group.OrigNormDiskPath ??= norm.ActualPath;
                        DebugServer.AppendLog($"[AutoDetect] norm for {group.Name}: {norm.GamePath}");
                    }
                }

                if (!string.IsNullOrEmpty(group.MtrlGamePath)) return;
            }
        }

        DebugServer.AppendLog($"[AutoDetect] Tree search failed for {group.Name}, trying disk proximity...");

        // Fallback: match by disk path proximity
        var diffuseDisk = group.OrigDiffuseDiskPath ?? group.DiffuseDiskPath;
        if (string.IsNullOrEmpty(diffuseDisk)) return;
        var targetDir = Path.GetDirectoryName(diffuseDisk)?.Replace('\\', '/').ToLowerInvariant() ?? "";

        var resources = penumbra.GetPlayerResources();
        if (resources == null) return;

        foreach (var (_, paths) in resources)
        {
            foreach (var (diskPath, gamePaths) in paths)
            {
                if (diskPath.Contains("pluginConfigs")) continue;
                var ext = Path.GetExtension(diskPath).ToLowerInvariant();
                if (ext != ".mtrl") continue;

                var mtrlDir = Path.GetDirectoryName(diskPath)?.Replace('\\', '/').ToLowerInvariant() ?? "";
                if (mtrlDir == targetDir)
                {
                    var gp = gamePaths.FirstOrDefault() ?? "";
                    group.MtrlGamePath = gp;
                    group.MtrlDiskPath = diskPath;
                    group.OrigMtrlDiskPath ??= diskPath;
                    DebugServer.AppendLog($"[AutoDetect] mtrl (disk) for {group.Name}: {gp}");
                    return;
                }
            }
        }

        DebugServer.AppendLog($"[AutoDetect] FAILED for {group.Name} diffuse={group.DiffuseGamePath}");
    }

    private void MarkPreviewDirty()
    {
        var now = DateTime.UtcNow;
        if (!previewDirty)
            firstDirtyTime = now;
        previewDirty = true;
        lastDirtyTime = now;
    }


    private unsafe void TryDirectEmissiveUpdate(TargetGroup group, DecalLayer layer)
    {
        if (string.IsNullOrEmpty(group.DiffuseGamePath))
        {
            DebugServer.AppendLog($"[DirectEmissive] Skip: no DiffuseGamePath for group={group.Name}");
            return;
        }
        var charBase = previewService.GetCharacterBase();
        if (charBase == null) return;
        var color = layer.EmissiveColor * layer.EmissiveIntensity;
        DebugServer.AppendLog($"[DirectEmissive] ColorTable swap ({color.X:F2},{color.Y:F2},{color.Z:F2}) to {group.MtrlGamePath}");
        previewService.HighlightEmissiveColor(charBase, group, color);
    }

    private static bool ScrollAdjust(ref float value, float step, float min, float max)
    {
        if (!ImGui.IsItemHovered()) return false;
        var wheel = ImGui.GetIO().MouseWheel;
        if (MathF.Abs(wheel) < 0.01f) return false;
        value = Math.Clamp(value + wheel * step, min, max);
        return true;
    }

    // ── Resource Browser Window ──────────────────────────────────────────────

    private void DrawResourceWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(900, 600), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("添加投影目标###SkinTatooResources", ref resourceWindowOpen))
        {
            ImGui.End();
            return;
        }

        if (ImGui.Button("刷新资源"))
            RefreshResources();
        ImGui.SameLine();
        if (ImGui.Button("全部展开"))
            treeExpandRequest = true;
        ImGui.SameLine();
        if (ImGui.Button("全部折叠"))
            treeCollapseRequest = true;
        ImGui.SameLine();
        ImGui.TextDisabled(penumbra.IsAvailable ? "Penumbra 已连接" : "Penumbra 未连接");

        if (!penumbra.IsAvailable)
        {
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "Penumbra 未连接");
            ImGui.End();
            return;
        }

        if (cachedTrees == null || cachedTrees.Count == 0)
        {
            ImGui.TextDisabled("点击「刷新资源」查询玩家资源");
            ImGui.End();
            return;
        }

        ImGui.Separator();

        using var scroll = ImRaii.Child("##TreeScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) { ImGui.End(); return; }

        foreach (var (objIdx, tree) in cachedTrees)
        {
            ImGui.PushID(objIdx);

            if (treeExpandRequest) ImGui.SetNextItemOpen(true);
            if (treeCollapseRequest) ImGui.SetNextItemOpen(false);

            var headerLabel = $"{tree.Name}  (#{objIdx})";
            if (ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (ImGui.BeginTable("##ResTree", 4,
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 46);
                    ImGui.TableSetupColumn("装备/外貌", ImGuiTableColumnFlags.WidthFixed, 280);
                    ImGui.TableSetupColumn("游戏路径", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                    ImGui.TableSetupColumn("实际路径", ImGuiTableColumnFlags.WidthStretch, 0.5f);
                    ImGui.TableHeadersRow();

                    for (var i = 0; i < tree.Nodes.Count; i++)
                    {
                        if (!HasMtrlDescendant(tree.Nodes[i])) continue;
                        ImGui.PushID(i);
                        DrawResourceNode(tree.Nodes[i], null);
                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }
            }
            ImGui.PopID();
        }

        treeExpandRequest = false;
        treeCollapseRequest = false;

        ImGui.End();
    }

    /// <param name="parentMdl">The parent Mdl node, passed down so Mtrl rows can reference mesh info.</param>
    private void DrawResourceNode(ResourceNodeDto node, ResourceNodeDto? parentMdl)
    {
        var mdlForChildren = node.Type == ResourceType.Mdl ? node : parentMdl;

        ImGui.TableNextRow();

        // Column 1: action button
        ImGui.TableNextColumn();
        var isMtrl = node.Type == ResourceType.Mtrl;
        var mtrlHasDiffuse = isMtrl && HasDiffuseDescendant(node);
        if (mtrlHasDiffuse)
        {
            var addedGroupName = GetMtrlAddedGroupName(node);
            if (addedGroupName != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 1f, 0.3f, 1f));
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.Text(FontAwesomeIcon.Check.ToIconString());
                ImGui.PopFont();
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"已添加到: {addedGroupName}");
            }
            else if (ImGui.SmallButton("添加"))
            {
                AddTargetGroupFromMtrl(node, parentMdl);
            }
        }

        // Column 2: tree structure + name
        ImGui.TableNextColumn();
        var nodeName = node.Name ?? Path.GetFileName(node.GamePath ?? node.ActualPath);
        var visibleChildren = node.Children.Where(c => !ShouldSkipNode(c)).ToList();
        var hasChildren = visibleChildren.Count > 0;
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth;
        if (!hasChildren) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        if (treeExpandRequest) ImGui.SetNextItemOpen(true);
        if (treeCollapseRequest) ImGui.SetNextItemOpen(false);

        var added = mtrlHasDiffuse && GetMtrlAddedGroupName(node) != null;
        if (added) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 1f, 0.3f, 1f));

        var typeTag = GetNodeTypeTag(node);
        var label = string.IsNullOrEmpty(typeTag) ? nodeName : $"{typeTag} {nodeName}";
        var open = ImGui.TreeNodeEx(label, flags);

        if (added) ImGui.PopStyleColor();

        // Column 3: game path
        ImGui.TableNextColumn();
        if (!string.IsNullOrEmpty(node.GamePath))
        {
            ImGui.TextUnformatted(node.GamePath);
            DrawPathHoverPreview(node, node.GamePath);
            DrawPathContextMenu(node.GamePath, "gp");
        }

        // Column 4: actual path
        ImGui.TableNextColumn();
        if (!string.IsNullOrEmpty(node.ActualPath))
        {
            var isModded = IsModdedPath(node);
            if (isModded) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.8f, 1f, 1f));
            ImGui.TextUnformatted(node.ActualPath);
            if (isModded) ImGui.PopStyleColor();
            DrawPathHoverPreview(node, node.ActualPath);
            DrawPathContextMenu(node.ActualPath, "ap");
        }

        // Recurse visible children
        if (open && hasChildren)
        {
            for (var i = 0; i < visibleChildren.Count; i++)
            {
                ImGui.PushID(i);
                DrawResourceNode(visibleChildren[i], mdlForChildren);
                ImGui.PopID();
            }
            ImGui.TreePop();
        }
    }

    private void DrawPathHoverPreview(ResourceNodeDto node, string path)
    {
        if (!ImGui.IsItemHovered() || node.Type != ResourceType.Tex) return;
        try
        {
            var normalized = path.Replace('\\', '/');
            var shared = Path.IsPathRooted(path)
                ? textureProvider.GetFromFile(path)
                : textureProvider.GetFromGame(normalized);
            var wrap = shared.GetWrapOrDefault();
            if (wrap != null)
            {
                ImGui.BeginTooltip();
                ImGui.Image(wrap.Handle, new Vector2(384, 384));
                ImGui.Text($"{wrap.Width}x{wrap.Height}");
                ImGui.EndTooltip();
            }
        }
        catch { }
    }

    private static void DrawPathContextMenu(string path, string id)
    {
        if (!ImGui.BeginPopupContextItem(id)) return;
        if (ImGui.Selectable("复制路径"))
            ImGui.SetClipboardText(path);
        ImGui.EndPopup();
    }

    private static bool IsModdedPath(ResourceNodeDto node)
    {
        if (node.GamePath == null) return false;
        // actualPath with backslashes but same content as gamePath is NOT modded
        var normalized = node.ActualPath.Replace('\\', '/');
        return normalized != node.GamePath;
    }

    private static bool ShouldSkipNode(ResourceNodeDto node)
    {
        // Skip node types that are not useful for decal target selection
        return node.Type is ResourceType.Imc or ResourceType.Sklb or ResourceType.Skp
            or ResourceType.Phyb or ResourceType.Eid or ResourceType.Pbd
            or ResourceType.Kdb or ResourceType.Shpk;
    }

    private static bool HasMtrlDescendant(ResourceNodeDto node)
    {
        if (node.Type == ResourceType.Mtrl) return true;
        return node.Children.Any(HasMtrlDescendant);
    }


    private static string GetNodeTypeTag(ResourceNodeDto node)
    {
        // Equipment-level icons take priority
        var iconTag = node.Icon switch
        {
            ChangedItemIcon.Head => "[头部]",
            ChangedItemIcon.Body => "[身体]",
            ChangedItemIcon.Hands => "[手部]",
            ChangedItemIcon.Legs => "[腿部]",
            ChangedItemIcon.Feet => "[脚部]",
            ChangedItemIcon.Ears => "[耳饰]",
            ChangedItemIcon.Neck => "[项链]",
            ChangedItemIcon.Wrists => "[手镯]",
            ChangedItemIcon.Finger => "[戒指]",
            ChangedItemIcon.Mainhand => "[主手]",
            ChangedItemIcon.Offhand => "[副手]",
            ChangedItemIcon.Customization => "[外貌]",
            _ => (string?)null,
        };
        if (iconTag != null) return iconTag;

        return node.Type switch
        {
            ResourceType.Mdl => "[模型]",
            ResourceType.Mtrl => "[材质]",
            ResourceType.Tex => "[贴图]",
            _ => "",
        };
    }

    // ── Selection logic ──────────────────────────────────────────────────────

    private void AddTargetGroupFromMtrl(ResourceNodeDto mtrlNode, ResourceNodeDto? parentMdl)
    {
        previewService.ClearTextureCache();
        previewService.ResetSwapState();
        penumbra.ClearRedirect();
        penumbra.RedrawPlayer();

        // Capture diffuse game path before refresh for re-finding
        var diffuse = FindDescendant(mtrlNode, n =>
            n.Type == ResourceType.Tex && n.GamePath != null && IsDiffusePath(n.GamePath));
        var diffuseGp = diffuse?.GamePath;
        var mtrlGp = mtrlNode.GamePath;

        // Re-refresh for clean disk paths
        RefreshResources();

        // Re-find the Mtrl node in the refreshed tree
        ResourceNodeDto? freshMtrl = null;
        ResourceNodeDto? freshMdl = null;
        var extraMdls = new List<ResourceNodeDto>();
        if (cachedTrees != null)
        {
            foreach (var (_, tree) in cachedTrees)
            {
                foreach (var topNode in tree.Nodes)
                {
                    var candidates = CollectDescendants(topNode, n => n.Type == ResourceType.Mtrl);
                    foreach (var candidate in candidates)
                    {
                        // Match by mtrl game path first, then by diffuse game path
                        var match = (mtrlGp != null && candidate.GamePath == mtrlGp) ||
                            (diffuseGp != null && FindDescendant(candidate, n =>
                                n.Type == ResourceType.Tex && n.GamePath == diffuseGp) != null);
                        if (!match) continue;

                        var mdl = FindAncestorMdl(topNode, candidate);
                        if (freshMtrl == null)
                        {
                            freshMtrl = candidate;
                            freshMdl = mdl;
                        }
                        else if (mdl != null && mdl != freshMdl)
                        {
                            extraMdls.Add(mdl);
                        }
                    }
                }
            }
        }
        freshMtrl ??= mtrlNode;
        freshMdl ??= parentMdl;

        var groupName = freshMtrl.Name ?? Path.GetFileName(freshMtrl.GamePath ?? freshMtrl.ActualPath);
        var tg = project.AddGroup(groupName);

        // Diffuse and Normal from Mtrl's Tex children
        var freshDiffuse = FindDescendant(freshMtrl, n =>
            n.Type == ResourceType.Tex && n.GamePath != null && IsDiffusePath(n.GamePath));
        var freshNormal = FindDescendant(freshMtrl, n =>
            n.Type == ResourceType.Tex && n.GamePath != null && IsNormalPath(n.GamePath));

        if (freshDiffuse != null)
        {
            tg.DiffuseGamePath = freshDiffuse.GamePath!;
            tg.DiffuseDiskPath = GetDiskPath(freshDiffuse);
            tg.OrigDiffuseDiskPath = tg.DiffuseDiskPath;
        }

        if (freshNormal != null)
        {
            tg.NormGamePath = freshNormal.GamePath!;
            tg.NormDiskPath = GetDiskPath(freshNormal);
            tg.OrigNormDiskPath = tg.NormDiskPath;
        }

        // Material is the Mtrl node itself
        tg.MtrlGamePath = freshMtrl.GamePath ?? "";
        tg.MtrlDiskPath = GetDiskPath(freshMtrl);
        tg.OrigMtrlDiskPath = tg.MtrlDiskPath;

        // Mesh from parent Mdl (primary + extra models sharing the same texture)
        if (freshMdl != null)
        {
            tg.MeshDiskPath = GetDiskPath(freshMdl);
            foreach (var extra in extraMdls)
            {
                var extraPath = GetDiskPath(extra);
                if (!string.IsNullOrEmpty(extraPath) && extraPath != tg.MeshDiskPath)
                    tg.MeshDiskPaths.Add(extraPath);
            }
            previewService.LoadMeshes(tg.AllMeshPaths);
            ModelEditorWindowRef?.OnMeshChanged();
        }

        config.Save();
        DebugServer.AppendLog($"[MainWindow] Added target group: {tg.Name}");
    }

    // ── Resource tree helpers ────────────────────────────────────────────────

    private void RefreshResources()
    {
        cachedTrees = penumbra.GetPlayerTrees();
        var count = cachedTrees?.Values.Sum(t => CountNodes(t.Nodes)) ?? 0;
        DebugServer.AppendLog($"[MainWindow] Refreshed: {cachedTrees?.Count ?? 0} objects, {count} nodes");
    }

    private static int CountNodes(List<ResourceNodeDto> nodes)
        => nodes.Sum(n => 1 + CountNodes(n.Children));

    private static ResourceNodeDto? FindDescendant(ResourceNodeDto node, Func<ResourceNodeDto, bool> predicate)
    {
        foreach (var child in node.Children)
        {
            if (predicate(child)) return child;
            var found = FindDescendant(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

    private static bool HasDiffuseDescendant(ResourceNodeDto node)
    {
        if (node.Type == ResourceType.Tex && node.GamePath != null && IsDiffusePath(node.GamePath))
            return true;
        return node.Children.Any(HasDiffuseDescendant);
    }

    private static bool IsDiffusePath(string gamePath)
    {
        var gp = gamePath.ToLowerInvariant();
        return gp.EndsWith(".tex") && !gp.Contains("_n.tex") && !gp.Contains("_m.tex")
            && !gp.Contains("norm") && !gp.Contains("mask");
    }

    /// <summary>
    /// Get the effective disk path from a resource node.
    /// If actualPath is an absolute path (modded), use it; otherwise use gamePath.
    /// </summary>
    private static string GetDiskPath(ResourceNodeDto node)
        => Path.IsPathRooted(node.ActualPath) ? node.ActualPath : (node.GamePath ?? node.ActualPath);

    private static bool IsNormalPath(string gamePath)
    {
        var gp = gamePath.ToLowerInvariant();
        return gp.EndsWith(".tex") && (gp.Contains("_n.tex") || gp.Contains("norm"));
    }

    private string? GetMtrlAddedGroupName(ResourceNodeDto mtrlNode)
    {
        var diffuse = FindDescendant(mtrlNode, n =>
            n.Type == ResourceType.Tex && n.GamePath != null && IsDiffusePath(n.GamePath));
        if (diffuse?.GamePath == null) return null;
        return project.Groups.FirstOrDefault(g => g.DiffuseGamePath == diffuse.GamePath)?.Name;
    }

    private static List<ResourceNodeDto> CollectDescendants(ResourceNodeDto node, Func<ResourceNodeDto, bool> predicate)
    {
        var result = new List<ResourceNodeDto>();
        if (predicate(node)) result.Add(node);
        foreach (var child in node.Children)
            result.AddRange(CollectDescendants(child, predicate));
        return result;
    }

    private static ResourceNodeDto? FindAncestorMdl(ResourceNodeDto root, ResourceNodeDto target)
    {
        // Walk the tree to find the Mdl node that is an ancestor of target
        if (root == target) return null;
        foreach (var child in root.Children)
        {
            if (child == target)
                return root.Type == ResourceType.Mdl ? root : null;
            var found = FindAncestorMdl(child, target);
            if (found != null) return found;
            // If child contains target and child is Mdl, return child
            if (ContainsNode(child, target) && child.Type == ResourceType.Mdl)
                return child;
        }
        return null;
    }

    private static bool ContainsNode(ResourceNodeDto root, ResourceNodeDto target)
    {
        if (root == target) return true;
        return root.Children.Any(c => ContainsNode(c, target));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Start a new row in the PBR table: opens the row, selects column 0.</summary>
    private static void BeginPbrRow()
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
    }

    /// <summary>
    /// Draw just the checkbox portion of a PBR row. Call <c>ImGui.SameLine()</c> after and
    /// render the value UI (color picker / slider) yourself — wrap it in <c>ImRaii.Disabled</c>
    /// when <paramref name="supported"/> is false so the value appears grayed.
    ///
    /// Checkbox behavior:
    /// - disabled when unsupported AND currently off (prevents turning on something the
    ///   material can't express);
    /// - disabled when row pair is exhausted AND currently off (character.shpk PBR limit);
    /// - toggling on may trigger a row-pair allocation that can fail → shows a toast and reverts;
    /// - toggling off releases the row pair if no other field needs it, then runs
    ///   <paramref name="onDisabled"/> for any caller-specific cleanup.
    ///
    /// Pass <paramref name="requiresRowPair"/> = false to skip allocation (used for the decal
    /// composite on skin.shpk where no ColorTable exists, and for emissive on skin.shpk where
    /// the CBuffer hook handles updates directly).
    /// </summary>
    private void DrawPbrRowStart(TargetGroup group, DecalLayer layer, string label,
        Func<bool> get, Action<bool> set,
        bool supported, bool requiresRowPair, bool exhausted,
        Action? onDisabled = null)
    {
        var was = get();
        var v = was;
        var disableCheckbox = (!supported || exhausted) && !was;
        if (disableCheckbox) ImGui.BeginDisabled();
        if (ImGui.Checkbox(label, ref v))
        {
            if (v && !was)
            {
                bool needsAlloc = requiresRowPair && layer.AllocatedRowPair < 0;
                set(true);
                if (needsAlloc)
                {
                    if (!previewService.TryAllocateRowPairForLayer(group, layer, out var failure))
                    {
                        set(false);
                        rowPairToast = failure switch
                        {
                            PreviewService.RowPairAllocFailure.Unsupported =>
                                $"无法启用 {label}：当前材质（skin.shpk 类）不支持该 PBR 字段。",
                            _ => $"无法启用 {label}：该材质 PBR 行号已满。请关闭其他图层的 PBR 字段后再试。",
                        };
                        rowPairToastUntil = DateTime.UtcNow.AddSeconds(4);
                    }
                    else
                    {
                        MarkPreviewDirty();
                    }
                }
                else
                {
                    MarkPreviewDirty();
                }
            }
            else if (!v && was)
            {
                set(false);
                previewService.ReleaseRowPairIfUnused(group, layer);
                onDisabled?.Invoke();
                MarkPreviewDirty();
            }
        }
        if (disableCheckbox) ImGui.EndDisabled();
    }

    private void ResetSelectedLayer()
    {
        var layer = project.SelectedLayer;
        if (layer == null) return;
        var savedKind = layer.Kind;
        var d = new DecalLayer();
        layer.Kind = savedKind;   // preserve layer type
        layer.UvCenter = d.UvCenter;
        layer.UvScale = d.UvScale;
        layer.RotationDeg = d.RotationDeg;
        layer.Opacity = d.Opacity;
        layer.BlendMode = d.BlendMode;
        layer.Clip = d.Clip;
        layer.IsVisible = d.IsVisible;
        layer.AffectsDiffuse = d.AffectsDiffuse;
        layer.AffectsSpecular = d.AffectsSpecular;
        layer.AffectsEmissive = d.AffectsEmissive;
        layer.AffectsRoughness = d.AffectsRoughness;
        layer.AffectsMetalness = d.AffectsMetalness;
        layer.AffectsSheen = d.AffectsSheen;
        layer.DiffuseColor = d.DiffuseColor;
        layer.SpecularColor = d.SpecularColor;
        layer.EmissiveColor = d.EmissiveColor;
        layer.EmissiveIntensity = d.EmissiveIntensity;
        layer.Roughness = d.Roughness;
        layer.Metalness = d.Metalness;
        layer.SheenRate = d.SheenRate;
        layer.SheenTint = d.SheenTint;
        layer.SheenAperture = d.SheenAperture;
        layer.FadeMask = d.FadeMask;
        layer.FadeMaskFalloff = d.FadeMaskFalloff;
        layer.GradientAngleDeg = d.GradientAngleDeg;
        layer.GradientScale = d.GradientScale;
        layer.GradientOffset = d.GradientOffset;
    }

    private void SyncImagePathBuf()
    {
        var layer = project.SelectedLayer;
        var group = project.SelectedGroup;
        lastEditedLayerIndex = group?.SelectedLayerIndex ?? -1;
        imagePathBuf = layer?.ImagePath ?? string.Empty;
    }

    /// <summary>
    /// Auto-fit a newly assigned decal image's UvScale to the ratio between the decal
    /// resolution and the target base texture resolution. A 1000 px decal on a 4000 px
    /// texture lands at scale 0.25 — close to natural pixel-for-pixel size so the user
    /// only needs a minor tweak from the default.
    /// </summary>
    private void AutoFitLayerScale(TargetGroup? group, DecalLayer layer)
    {
        if (group == null || string.IsNullOrEmpty(layer.ImagePath)) return;
        var decal = previewService.GetImageDimensions(layer.ImagePath);
        if (decal == null) return;
        var (tw, th) = previewService.GetBaseTextureSize(group);
        if (tw <= 0 || th <= 0) return;
        var sx = Math.Clamp((float)decal.Value.Width / tw, 0.02f, 1f);
        var sy = Math.Clamp((float)decal.Value.Height / th, 0.02f, 1f);
        layer.UvScale = new Vector2(sx, sy);
    }

    public void Dispose()
    {
        InitializeRequested = null;
    }
}
