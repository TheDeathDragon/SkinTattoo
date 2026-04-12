using System;
using System.Collections.Generic;
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
using Penumbra.Api.Helpers;
using SkinTattoo.Core;
using SkinTattoo.Http;
using SkinTattoo.Interop;
using SkinTattoo.Services;

namespace SkinTattoo.Gui;

public partial class MainWindow : Window, IDisposable
{
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;
    private readonly ITextureProvider textureProvider;
    private readonly IDataManager dataManager;
    private readonly Mesh.SkinMeshResolver skinMeshResolver;
    private readonly FileDialogManager fileDialog = new();

    private string imagePathBuf = string.Empty;
    private int lastEditedLayerIndex = -1;
    private int layerCounter;
    private bool scaleLocked = true;

    // Resource browser state
    private Dictionary<ushort, ResourceTreeDto>? cachedTrees;
    private bool resourceWindowOpen;

    // Canvas state
    private float canvasZoom = 1.0f;
    private Vector2 canvasPan = Vector2.Zero;
    private bool canvasDraggingLayer;
    private bool canvasPanning;
    private bool canvasScalingLayer;

    // Cached base texture size
    private int lastBaseTexWidth;
    private int lastBaseTexHeight;

    // Track group switch to clear stale mesh
    private int lastSelectedGroupIndex = -1;

    // Panel widths (resizable)
    private float leftPanelWidth = -1;
    private float rightPanelWidth = 260f;

    // Tab control
    private bool requestSwitchToSettings;

    // Highlight glow state
    private bool highlightActive;
    private int highlightGroupIndex = -1;
    private int highlightLayerIndex = -1;
    private int highlightFrameCounter;
    private const int HighlightCycleSteps = 400;

    // Init phase
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
    private const double GpuPreviewMinIntervalMs = 33;


    private static readonly string[] BlendModeNames = ["正常", "正片叠底", "滤色", "叠加", "柔光", "强光", "变暗", "变亮", "颜色减淡", "颜色加深", "差值", "排除"];
    private static readonly BlendMode[] BlendModeValues = [BlendMode.Normal, BlendMode.Multiply, BlendMode.Screen, BlendMode.Overlay, BlendMode.SoftLight, BlendMode.HardLight, BlendMode.Darken, BlendMode.Lighten, BlendMode.ColorDodge, BlendMode.ColorBurn, BlendMode.Difference, BlendMode.Exclusion];
    private static readonly string[] LayerFadeMaskNames = ["均匀", "中心扩散", "边缘光环", "边缘描边", "方向渐变", "高斯羽化", "形状描边"];
    private static readonly string[] ClipModeNames = ["无裁剪", "切左半", "切右半", "切上半", "切下半"];

    public DebugWindow? DebugWindowRef { get; set; }
    public ModelEditorWindow? ModelEditorWindowRef { get; set; }
    public ModExportWindow? ModExportWindowRef { get; set; }

    public Func<Task>? InitializeRequested { get; set; }

    public MainWindow(
        DecalProject project,
        PreviewService previewService,
        PenumbraBridge penumbra,
        Configuration config,
        ITextureProvider textureProvider,
        IDataManager dataManager,
        Mesh.SkinMeshResolver skinMeshResolver)
        : base("SkinTattoo 纹身编辑器###SkinTattooMain",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.project = project;
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.config = config;
        this.textureProvider = textureProvider;
        this.dataManager = dataManager;
        this.skinMeshResolver = skinMeshResolver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 580),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // Initialize layer counter from existing layers so names don't repeat
        foreach (var g in project.Groups)
            layerCounter += g.Layers.Count;
    }

    public void OpenSettings()
    {
        IsOpen = true;
        requestSwitchToSettings = true;
    }

    public override void Draw()
    {
        WindowName = penumbra.IsAvailable
            ? "SkinTattoo 纹身编辑器 [Penumbra 已连接]###SkinTattooMain"
            : "SkinTattoo 纹身编辑器 [Penumbra 未运行]###SkinTattooMain";

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
            // Sync so the group-switch detection below doesn't re-trigger a full preview
            lastSelectedGroupIndex = project.SelectedGroupIndex;
            previewDirty = false;
        }

        var loading = initPhase != InitPhase.Done;

        previewService.ApplyPendingSwaps();

        if (previewService.ExternalDirty)
        {
            previewService.ExternalDirty = false;
            MarkPreviewDirty();
        }

        UpdateHighlight();

        // ── Tab bar ──
        if (ImGui.BeginTabBar("##MainTabs", ImGuiTabBarFlags.None))
        {
            // Tab 0: Settings
            var settingsFlags = requestSwitchToSettings
                ? ImGuiTabItemFlags.SetSelected
                : ImGuiTabItemFlags.None;
            if (requestSwitchToSettings) requestSwitchToSettings = false;

            if (ImGui.BeginTabItem("设置##mainTab0", settingsFlags))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            // Tab 1: Editor
            if (ImGui.BeginTabItem("编辑器##mainTab1"))
            {
                if (loading) ImGui.BeginDisabled();

                DrawToolbar();
                ImGui.Separator();

                var totalWidth = ImGui.GetContentRegionAvail().X;
                var totalHeight = ImGui.GetContentRegionAvail().Y;
                DrawThreePanelLayout(totalWidth, totalHeight);

                fileDialog.Draw();

                if (resourceWindowOpen)
                    DrawResourceWindow();

                if (loading) ImGui.EndDisabled();

                if (loading)
                    DrawLoadingOverlay();

                ImGui.EndTabItem();
            }

            // Tab 2: Help
            if (ImGui.BeginTabItem("操作说明##mainTab2"))
            {
                DrawHelpTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

    }

    // ── Highlight ──────────────────────────────────────────────────────────

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
        HighlightEmissive(color, group, highlightLayerIndex);
    }

    private unsafe void HighlightEmissive(Vector3 color, TargetGroup? targetGroup = null, int layerIndex = -1)
    {
        var group = targetGroup ?? project.SelectedGroup;
        if (group == null || string.IsNullOrEmpty(group.MtrlGamePath)) return;
        var charBase = previewService.GetCharacterBase();
        if (charBase == null) return;
        previewService.HighlightEmissiveColor(charBase, group, color, layerIndex);
    }

    private unsafe void RestoreEmissiveAfterHighlight(TargetGroup group)
    {
        var charBase = previewService.GetCharacterBase();
        if (charBase == null) return;

        if (!group.HasEmissiveLayers())
        {
            previewService.HighlightEmissiveColor(charBase, group, Vector3.Zero);
            return;
        }

        // skin CT: rebuild per-layer CT; legacy: set CBuffer color
        TryDirectEmissiveUpdate(group);
    }

    // ── Loading overlay ──────────────────────────────────────────────────────

    private void DrawLoadingOverlay()
    {
        var wndPos = ImGui.GetWindowPos();
        var cMin = wndPos + ImGui.GetWindowContentRegionMin();
        var cMax = wndPos + ImGui.GetWindowContentRegionMax();
        if (cMax.X <= cMin.X || cMax.Y <= cMin.Y) return;

        var fg = ImGui.GetForegroundDrawList();
        fg.AddRectFilled(cMin, cMax, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f)));

        var center = (cMin + cMax) * 0.5f;

        const int dotCount = 10;
        const float radius = 26f;
        const float dotRadius = 4f;
        var t = ImGui.GetTime();
        var rotation = (float)(t * 4.0);
        for (var i = 0; i < dotCount; i++)
        {
            var phase = i / (float)dotCount;
            var angle = rotation + phase * MathF.PI * 2f;
            var p = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            var brightness = 0.15f + 0.85f * phase;
            var color = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, brightness));
            fg.AddCircleFilled(p, dotRadius, color);
        }

        const string label = "加载中…";
        var labelSize = ImGui.CalcTextSize(label);
        var labelPos = center + new Vector2(-labelSize.X * 0.5f, radius + 14f);
        fg.AddText(labelPos, ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.95f, 1f)), label);

        const string sub = "正在加载模型与贴花，请稍候";
        var subSize = ImGui.CalcTextSize(sub);
        var subPos = new Vector2(center.X - subSize.X * 0.5f, labelPos.Y + labelSize.Y + 4f);
        fg.AddText(subPos, ImGui.GetColorU32(new Vector4(0.75f, 0.75f, 0.8f, 1f)), sub);
    }

    // ── Help tab ──────────────────────────────────────────────────────────────

    private void DrawHelpTab()
    {
        using var scroll = ImRaii.Child("##HelpScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) return;

        var header = new Vector4(1f, 0.8f, 0.3f, 1f);

        ImGui.TextColored(header, "UV 编辑器  -- 画布操作");
        ImGui.Separator();
        ImGui.BulletText("滚轮: 缩放画布");
        ImGui.BulletText("中键拖动: 平移画布");
        ImGui.BulletText("左键拖动: 移动贴花位置");
        ImGui.BulletText("左键点空白: 选择贴花图层");
        ImGui.BulletText("右键拖动: 缩放贴花");

        ImGui.Spacing();
        ImGui.TextColored(header, "UV 编辑器  -- 修饰键");
        ImGui.Separator();
        ImGui.BulletText("Shift + 左键拖动: 锁定 X 轴");
        ImGui.BulletText("Ctrl + 左键拖动: 锁定 Y 轴");
        ImGui.BulletText("Alt + 右键拖动: 旋转贴花");

        ImGui.Spacing();
        ImGui.TextColored(header, "图层与目标");
        ImGui.Separator();
        ImGui.BulletText("Ctrl+Shift + 删除按钮: 删除图层/目标");
        ImGui.BulletText("右键贴花组标题: 复制贴图 / 法线 / 材质路径");

        ImGui.Spacing();
        ImGui.TextColored(header, "3D 编辑器  -- 相机");
        ImGui.Separator();
        ImGui.BulletText("右键拖动: 旋转相机");
        ImGui.BulletText("中键拖动: 平移相机");
        ImGui.BulletText("Ctrl + 滚轮: 相机缩放");
        ImGui.BulletText("重置相机按钮: 回到默认视角");

        ImGui.Spacing();
        ImGui.TextColored(header, "3D 编辑器  -- 贴花");
        ImGui.Separator();
        ImGui.BulletText("左键点击模型: 将选中图层的贴花放到该位置");
        ImGui.BulletText("滚轮: 缩放选中贴花 (非 Ctrl)");

        ImGui.Spacing();
        ImGui.TextColored(header, "3D 编辑器  -- 模型管理");
        ImGui.Separator();
        ImGui.BulletText("添加模型: 从玩家资源树追加额外模型");
        ImGui.BulletText("管理模型: 勾选显隐 / 移除已添加模型");
    }

    // ── Toolbar ──────────────────────────────────────────────────────────────

    private void DrawToolbar()
    {
        if (ImGuiComponents.IconButton(10, FontAwesomeIcon.Cube))
        {
            if (ModelEditorWindowRef != null)
                ModelEditorWindowRef.IsOpen = !ModelEditorWindowRef.IsOpen;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("3D 贴花编辑器");

        ImGui.SameLine();
        using (ImRaii.Disabled(project.SelectedLayer == null))
        {
            if (ImGuiComponents.IconButton(2, FontAwesomeIcon.Undo))
                ResetSelectedLayer();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("重置当前图层参数");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(3, FontAwesomeIcon.Eraser))
        {
            penumbra.ClearRedirect();
            penumbra.RedrawPlayer();
            previewService.ClearTextureCache();
            previewService.ResetSwapState();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("还原贴图  -- 清除 Penumbra 重定向");

        // External edit: export base texture + wireframe
        ImGui.SameLine();
        {
            var grp = project.SelectedGroup;
            using (ImRaii.Disabled(grp == null || string.IsNullOrEmpty(grp?.DiffuseGamePath)))
            {
                if (ImGuiComponents.IconButton(40, FontAwesomeIcon.Image))
                    ExportBaseTexture(grp!);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("导出底图 PNG（用于 PS 编辑）");

            ImGui.SameLine();
            using (ImRaii.Disabled(grp == null || previewService.CurrentMesh == null))
            {
                if (ImGuiComponents.IconButton(41, FontAwesomeIcon.BorderAll))
                    ExportUvWireframe(grp!);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("导出 UV 网格 PNG（用于 PS 叠加参考）");
        }

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
        if (group != null && (!string.IsNullOrEmpty(group.MeshGamePath) || group.AllMeshPaths.Count > 0))
        {
            var meshIcon = previewService.CurrentMesh == null ? FontAwesomeIcon.Cube : FontAwesomeIcon.SyncAlt;
            if (ImGuiComponents.IconButton(4, meshIcon))
            {
                ReloadGroupMesh(group);
                previewService.NotifyMeshChanged();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(previewService.CurrentMesh == null ? "加载模型" : "重新加载模型");
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

    }

    // ── Layout ──────────────────────────────────────────────────────────────

    private unsafe void DrawThreePanelLayout(float totalWidth, float height)
    {
        const float splitterWidth = 6f;
        const float minPanelWidth = 140f;
        const float hoverExtend = 5f;
        const float hoverDelay = 0.1f;

        if (leftPanelWidth < 0) leftPanelWidth = totalWidth * 0.20f;

        var maxLeft = totalWidth - rightPanelWidth - minPanelWidth - splitterWidth * 2;
        var maxRight = totalWidth - leftPanelWidth - minPanelWidth - splitterWidth * 2;
        leftPanelWidth = Math.Clamp(leftPanelWidth, minPanelWidth, Math.Max(minPanelWidth, maxLeft));
        rightPanelWidth = Math.Clamp(rightPanelWidth, minPanelWidth, Math.Max(minPanelWidth, maxRight));
        var centerWidth = Math.Max(minPanelWidth, totalWidth - leftPanelWidth - rightPanelWidth - splitterWidth * 2);

        var origin = ImGui.GetCursorScreenPos();

        // Left panel
        using (var left = ImRaii.Child("##LeftPanel", new Vector2(leftPanelWidth, height), true))
        {
            if (left.Success) DrawLayerPanel();
        }

        // Left splitter (between left and center)
        {
            var splitX = origin.X + leftPanelWidth;
            var rect = new ImRect(
                new Vector2(splitX, origin.Y),
                new Vector2(splitX + splitterWidth, origin.Y + height));
            var id = ImGui.GetID("##SplitL");
            var sizeLeft = leftPanelWidth;
            var sizeRight = totalWidth - leftPanelWidth - splitterWidth;
            var minRight = rightPanelWidth + minPanelWidth + splitterWidth;
            using var color = ImRaii.PushColor(ImGuiCol.Separator, 0u);
            if (ImGuiP.SplitterBehavior(rect, id, ImGuiAxis.X, &sizeLeft, &sizeRight, minPanelWidth, minRight, hoverExtend, hoverDelay, 0))
                leftPanelWidth = sizeLeft;
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X + leftPanelWidth + splitterWidth, origin.Y));

        // Center panel
        using (var center = ImRaii.Child("##CenterPanel", new Vector2(centerWidth, height), true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (center.Success) DrawCanvas();
        }

        // Right splitter (between center and right)
        {
            var splitX = origin.X + leftPanelWidth + splitterWidth + centerWidth;
            var rect = new ImRect(
                new Vector2(splitX, origin.Y),
                new Vector2(splitX + splitterWidth, origin.Y + height));
            var id = ImGui.GetID("##SplitR");
            var sizeLeft = leftPanelWidth + splitterWidth + centerWidth;
            var sizeRight = rightPanelWidth;
            var minLeft = leftPanelWidth + splitterWidth + minPanelWidth;
            using var color = ImRaii.PushColor(ImGuiCol.Separator, 0u);
            if (ImGuiP.SplitterBehavior(rect, id, ImGuiAxis.X, &sizeLeft, &sizeRight, minLeft, minPanelWidth, hoverExtend, hoverDelay, 0))
                rightPanelWidth = sizeRight;
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X + totalWidth - rightPanelWidth, origin.Y));

        // Right panel
        using (var right = ImRaii.Child("##RightPanel", new Vector2(rightPanelWidth, height), true))
        {
            if (right.Success) DrawParameterPanel();
        }
    }

    // ── Auto-preview & helpers ──────────────────────────────────────────────

    private void DrawActionsSection()
    {
        var group = project.SelectedGroup;
        var hasTarget = group != null && !string.IsNullOrEmpty(group.DiffuseGamePath);

        if (project.SelectedGroupIndex != lastSelectedGroupIndex)
        {
            var oldGroupIndex = lastSelectedGroupIndex;
            lastSelectedGroupIndex = project.SelectedGroupIndex;

            // Clean up emissive hook state from the previous group
            if (oldGroupIndex >= 0 && oldGroupIndex < project.Groups.Count)
                previewService.InvalidateEmissiveForGroup(project.Groups[oldGroupIndex]);

            if (project.SelectedGroup == null)
            {
                previewService.ClearMesh();
                previewService.NotifyMeshChanged();
            }
            else
            {
                // Each group can have different MeshSlots (different mdl files
                // and UV layouts). Reload mesh in background so the UV
                // wireframe on the canvas matches the new group.
                // Don't call NotifyMeshChanged from background thread  -- the
                // canvas reads CurrentMesh directly each frame, and
                // ModelEditorWindow detects changes via its own pathKey diff.
                var newGroup = project.SelectedGroup;
                if (newGroup.MeshSlots.Count > 0 || newGroup.AllMeshPaths.Count > 0
                    || !string.IsNullOrEmpty(newGroup.MeshGamePath))
                {
                    Task.Run(() => previewService.LoadMeshForGroup(newGroup));
                }

                MarkPreviewDirty();
            }
        }

        // Poll external file changes (PS save, etc.)
        if (config.PluginEnabled && config.AutoPreview && hasTarget)
            PollFileChanges();

        if (config.PluginEnabled && config.AutoPreview && previewDirty && hasTarget
            && initPhase == InitPhase.Done)
        {
            if (previewService.CanSwapInPlace && config.UseGpuSwap)
            {
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
                var now = DateTime.UtcNow;
                var sinceLastChange = (now - lastDirtyTime).TotalSeconds;
                var sinceFirstChange = (now - firstDirtyTime).TotalSeconds;
                if (forceImmediatePreview
                    || sinceLastChange >= PreviewDebounceFullSec
                    || sinceFirstChange >= PreviewMaxWaitFullSec)
                {
                    forceImmediatePreview = false;
                    previewDirty = false;
                    TriggerPreview();
                }
            }
        }
    }

    private void TriggerPreview()
    {
        if (!config.PluginEnabled) return;

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
        var trees = penumbra.GetPlayerTrees();
        if (trees == null || string.IsNullOrEmpty(group.DiffuseGamePath))
            return;

        var diffuseNorm = group.DiffuseGamePath.Replace('\\', '/').ToLowerInvariant();

        foreach (var (_, tree) in trees)
        {
            foreach (var topNode in tree.Nodes)
            {
                var diffuse = FindDescendant(topNode, n =>
                    n.Type == Penumbra.Api.Enums.ResourceType.Tex &&
                    (n.GamePath ?? "").Replace('\\', '/').ToLowerInvariant() == diffuseNorm);
                if (diffuse == null) continue;

                var mtrl = FindDescendant(topNode, n =>
                    n.Type == Penumbra.Api.Enums.ResourceType.Mtrl && !n.ActualPath.Contains("pluginConfigs"));
                if (mtrl != null)
                {
                    group.MtrlGamePath = mtrl.GamePath ?? "";
                    group.MtrlDiskPath = mtrl.ActualPath;
                    group.OrigMtrlDiskPath ??= mtrl.ActualPath;
                }

                if (string.IsNullOrEmpty(group.NormGamePath))
                {
                    var norm = FindDescendant(topNode, n =>
                        n.Type == Penumbra.Api.Enums.ResourceType.Tex &&
                        (n.GamePath ?? "").Contains("_norm") &&
                        !n.ActualPath.Contains("pluginConfigs"));
                    if (norm != null)
                    {
                        group.NormGamePath = norm.GamePath ?? "";
                        group.NormDiskPath = norm.ActualPath;
                        group.OrigNormDiskPath ??= norm.ActualPath;
                    }
                }

                if (!string.IsNullOrEmpty(group.MtrlGamePath)) return;
            }
        }

        var diffuseDisk = group.OrigDiffuseDiskPath ?? group.DiffuseDiskPath;
        if (string.IsNullOrEmpty(diffuseDisk)) return;
        var targetDir = System.IO.Path.GetDirectoryName(diffuseDisk)?.Replace('\\', '/').ToLowerInvariant() ?? "";

        var resources = penumbra.GetPlayerResources();
        if (resources == null) return;

        foreach (var (_, paths) in resources)
        {
            foreach (var (diskPath, gamePaths) in paths)
            {
                if (diskPath.Contains("pluginConfigs")) continue;
                var ext = System.IO.Path.GetExtension(diskPath).ToLowerInvariant();
                if (ext != ".mtrl") continue;

                var mtrlDir = System.IO.Path.GetDirectoryName(diskPath)?.Replace('\\', '/').ToLowerInvariant() ?? "";
                if (mtrlDir == targetDir)
                {
                    var gp = gamePaths.FirstOrDefault() ?? "";
                    group.MtrlGamePath = gp;
                    group.MtrlDiskPath = diskPath;
                    group.OrigMtrlDiskPath ??= diskPath;
                    return;
                }
            }
        }

        DebugServer.AppendLog($"[AutoDetect] FAILED for {group.Name} diffuse={group.DiffuseGamePath}");
    }

    private bool forceImmediatePreview;

    private void MarkPreviewDirty(bool immediate = false)
    {
        var now = DateTime.UtcNow;
        if (!previewDirty)
            firstDirtyTime = now;
        previewDirty = true;
        lastDirtyTime = now;
        if (immediate)
            forceImmediatePreview = true;
    }

    private unsafe void TryDirectEmissiveUpdate(TargetGroup group)
    {
        if (string.IsNullOrEmpty(group.DiffuseGamePath))
            return;
        var charBase = previewService.GetCharacterBase();
        if (charBase == null) return;

        // skin CT: rebuild per-layer CT from current layer state.
        // Do NOT fall through to HighlightEmissiveColor — it would overwrite
        // per-layer colors with a single combined color.
        if (previewService.RestoreSkinCtAfterHighlight(charBase, group))
            return;

        // Legacy path: set combined emissive via CBuffer
        var color = previewService.GetCombinedEmissiveColorForGroup(group);
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

    private void ResetSelectedLayer()
    {
        var layer = project.SelectedLayer;
        if (layer == null) return;
        var savedKind = layer.Kind;
        var d = new DecalLayer();
        layer.Kind = savedKind;
        layer.UvCenter = d.UvCenter;
        // Apply texture aspect ratio so the decal resets to a square in pixel
        // space, matching the convention used by the scale slider and scroll.
        var selGroup = project.SelectedGroup;
        var (texW, texH) = selGroup != null ? previewService.GetBaseTextureSize(selGroup) : (0, 0);
        float texAspect = (texW > 0 && texH > 0) ? (float)texW / texH : 1f;
        layer.UvScale = new Vector2(d.UvScale.X, d.UvScale.X * texAspect);
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

    private void AutoFitLayerScale(TargetGroup? group, DecalLayer layer)
    {
        if (group == null || string.IsNullOrEmpty(layer.ImagePath)) return;
        var decal = previewService.GetImageDimensions(layer.ImagePath);
        if (decal == null) return;
        var (tw, th) = previewService.GetBaseTextureSize(group);
        if (tw <= 0 || th <= 0) return;
        // Use X-axis ratio as the base, then derive Y to preserve the decal's
        // pixel aspect ratio on non-square textures (e.g., 256x512 tail tex).
        var sx = Math.Clamp((float)decal.Value.Width / tw, 0.02f, 1f);
        float texAspect = (float)tw / th;
        float decalAspect = (float)decal.Value.Width / decal.Value.Height;
        var sy = Math.Clamp(sx * texAspect / decalAspect, 0.02f, 1f);
        layer.UvScale = new Vector2(sx, sy);
    }

    public void Dispose()
    {
        InitializeRequested = null;
    }
}
