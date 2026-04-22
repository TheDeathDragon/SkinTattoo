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
using SkinTattoo.Services.Localization;

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
    private readonly ChangelogService changelogService;
    private readonly LibraryService? library;
    private readonly DecalImageLoader? imageLoader;
    private readonly IKeyState keyState;
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
    private bool previewCurrentLayerOnly;
    private bool showCanvasBaseTexture = true;
    public TargetMap CanvasMapMode { get; private set; } = TargetMap.Diffuse;

    // Cached base texture size
    private int lastBaseTexWidth;
    private int lastBaseTexHeight;

    // Mesh state tick: tracked by object ref + resolver-slot-path hash so we pick
    // up group switches, in-place mutations (add/remove model, resolver reruns),
    // and live-tree swaps (character re-equipment) regardless of whether the 3D
    // editor is open.
    private TargetGroup? lastMeshGroupRef;
    private int lastMeshPathHash;
    private DateTime lastLiveTreePollUtc = DateTime.MinValue;
    // 5s avoids churning Penumbra's GetPlayerTrees (big dict alloc) every second.
    // Gear changes are rare and still get picked up within 5s.
    private const double LiveTreePollIntervalSec = 5.0;

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

    private List<DecalLayer>? copiedGroupLayers;
    private int copiedGroupSelectedLayerIndex = -1;
    private float copiedGroupSrcAspect;
    private TargetGroup? copiedGroupSource;

    private const int HistoryMaxDepth = 100;
    private const double HistoryCoalesceMs = 250;
    private readonly List<SavedProjectSnapshot> undoHistory = [];
    private readonly List<SavedProjectSnapshot> redoHistory = [];
    private bool isReplayingHistory;
    private SavedProjectSnapshot? historyBaselineSnapshot;
    private int historyBaselineSignature;
    private DateTime historyLastCommitUtc = DateTime.MinValue;
    private bool historyTrackerInitialized;

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


    private static readonly BlendMode[] BlendModeValues = [BlendMode.Normal, BlendMode.Multiply, BlendMode.Screen, BlendMode.Overlay, BlendMode.SoftLight, BlendMode.HardLight, BlendMode.Darken, BlendMode.Lighten, BlendMode.ColorDodge, BlendMode.ColorBurn, BlendMode.Difference, BlendMode.Exclusion];

    private static float GetScaleSign(float value) => value < 0f ? -1f : 1f;

    private static float ClampScaleMagnitude(float value) => Math.Clamp(value, 0.01f, 10f);

    private static float ClampSignedScale(float value)
    {
        var sign = GetScaleSign(value);
        return ClampScaleMagnitude(MathF.Abs(value)) * sign;
    }

    private static Vector2 ClampSignedScale(Vector2 scale) =>
        new(ClampSignedScale(scale.X), ClampSignedScale(scale.Y));

    private static Vector2 GetScaleAbs(Vector2 scale) =>
        new(MathF.Abs(scale.X), MathF.Abs(scale.Y));

    private static float NormalizeAngleDeg(float angle)
    {
        while (angle > 180f) angle -= 360f;
        while (angle <= -180f) angle += 360f;
        return angle;
    }

    private void RotateLayer(DecalLayer layer, float deltaDeg)
    {
        layer.RotationDeg = NormalizeAngleDeg(layer.RotationDeg + deltaDeg);
        MarkPreviewDirty();
    }

    private void MirrorLayerHorizontally(DecalLayer layer)
    {
        layer.UvScale = new Vector2(-layer.UvScale.X, layer.UvScale.Y);
        MarkPreviewDirty();
    }

    private void MirrorLayerVertically(DecalLayer layer)
    {
        layer.UvScale = new Vector2(layer.UvScale.X, -layer.UvScale.Y);
        MarkPreviewDirty();
    }

    private static string[] GetBlendModeNames() => [
        Strings.T("enum.blendmode.normal"), Strings.T("enum.blendmode.multiply"), Strings.T("enum.blendmode.screen"),
        Strings.T("enum.blendmode.overlay"), Strings.T("enum.blendmode.softlight"), Strings.T("enum.blendmode.hardlight"),
        Strings.T("enum.blendmode.darken"), Strings.T("enum.blendmode.lighten"), Strings.T("enum.blendmode.colordodge"),
        Strings.T("enum.blendmode.colorburn"), Strings.T("enum.blendmode.difference"), Strings.T("enum.blendmode.exclusion")
    ];

    private static string[] GetFadeMaskNames() => [
        Strings.T("enum.fademask.uniform"), Strings.T("enum.fademask.radial"), Strings.T("enum.fademask.ring"),
        Strings.T("enum.fademask.outline"), Strings.T("enum.fademask.gradient"), Strings.T("enum.fademask.gaussian"),
        Strings.T("enum.fademask.shape_outline")
    ];

    private static string[] GetClipModeNames() => [
        Strings.T("enum.clip.none"), Strings.T("enum.clip.left"), Strings.T("enum.clip.right"),
        Strings.T("enum.clip.top"), Strings.T("enum.clip.bottom")
    ];

    public DebugWindow? DebugWindowRef { get; set; }
    public PerformanceWindow? PerformanceWindowRef { get; set; }
    public ModelEditorWindow? ModelEditorWindowRef { get; set; }
    public ModExportWindow? ModExportWindowRef { get; set; }
    public LibraryWindow? LibraryWindowRef { get; set; }

    public Func<Task>? InitializeRequested { get; set; }

    public MainWindow(
        DecalProject project,
        PreviewService previewService,
        PenumbraBridge penumbra,
        Configuration config,
        ITextureProvider textureProvider,
        IDataManager dataManager,
        Mesh.SkinMeshResolver skinMeshResolver,
        ChangelogService changelogService,
        IKeyState keyState,
        LibraryService? library = null,
        DecalImageLoader? imageLoader = null)
        : base(Strings.T("window.main.title") + "###SkinTattooMain",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.project = project;
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.config = config;
        this.textureProvider = textureProvider;
        this.dataManager = dataManager;
        this.skinMeshResolver = skinMeshResolver;
        this.changelogService = changelogService;
        this.keyState = keyState;
        this.library = library;
        this.imageLoader = imageLoader;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 580),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        // Initialize layer counter from existing layers so names don't repeat
        foreach (var g in project.Groups)
            layerCounter += g.Layers.Count;

        CanvasMapMode = config.UvViewTargetMap switch
        {
            (int)TargetMap.Mask => TargetMap.Mask,
            (int)TargetMap.Normal => TargetMap.Normal,
            _ => TargetMap.Diffuse,
        };
        previewCurrentLayerOnly = config.UvCurrentDecalOnly;
        showCanvasBaseTexture = config.UvShowBaseTexture;
        showUvWireframe = config.UvShowWireframe;
    }

    private void InitializeHistoryTrackerIfNeeded()
    {
        if (historyTrackerInitialized) return;
        ResetHistoryTrackerBaseline();
        historyTrackerInitialized = true;
    }

    private void ResetHistoryTrackerBaseline()
    {
        historyBaselineSnapshot = project.CreateSnapshot();
        historyBaselineSignature = ComputeProjectSignature(project);
        historyLastCommitUtc = DateTime.UtcNow;
    }

    // Computed directly off DecalProject to avoid per-frame SavedProjectSnapshot allocation.
    // Must mirror the fields emitted by DecalProject.SerializeLayer/SerializeGroup.
    private static int ComputeProjectSignature(DecalProject project)
    {
        var hash = new HashCode();

        static void AddString(ref HashCode h, string? value)
        {
            h.Add(value ?? string.Empty, StringComparer.Ordinal);
        }

        hash.Add(project.SelectedGroupIndex);
        hash.Add(project.Groups.Count);

        foreach (var group in project.Groups)
        {
            AddString(ref hash, group.Name);
            AddString(ref hash, group.DiffuseGamePath);
            AddString(ref hash, group.DiffuseDiskPath);
            AddString(ref hash, group.NormGamePath);
            AddString(ref hash, group.NormDiskPath);
            AddString(ref hash, group.MtrlGamePath);
            AddString(ref hash, group.MtrlDiskPath);
            AddString(ref hash, group.MeshDiskPath);
            AddString(ref hash, group.OrigDiffuseDiskPath);
            AddString(ref hash, group.OrigNormDiskPath);
            AddString(ref hash, group.OrigMtrlDiskPath);
            hash.Add(group.SelectedLayerIndex);
            hash.Add(group.MeshDiskPaths.Count);
            foreach (var meshPath in group.MeshDiskPaths)
                AddString(ref hash, meshPath);

            hash.Add(group.Layers.Count);
            foreach (var layer in group.Layers)
            {
                AddString(ref hash, layer.Name);
                AddString(ref hash, layer.ImagePath);
                AddString(ref hash, layer.ImageHash);
                hash.Add(layer.UvCenter.X);
                hash.Add(layer.UvCenter.Y);
                hash.Add(layer.UvScale.X);
                hash.Add(layer.UvScale.Y);
                hash.Add(layer.RotationDeg);
                hash.Add(layer.Opacity);
                hash.Add((int)layer.BlendMode);
                hash.Add(layer.IsVisible);
                hash.Add(layer.AffectsDiffuse);
                hash.Add(layer.AffectsEmissive);
                hash.Add(layer.EmissiveColor.X);
                hash.Add(layer.EmissiveColor.Y);
                hash.Add(layer.EmissiveColor.Z);
                hash.Add(layer.EmissiveIntensity);
                hash.Add((int)layer.AnimMode);
                hash.Add(layer.AnimSpeed);
                hash.Add(layer.AnimAmplitude);
                hash.Add(layer.EmissiveColorB.X);
                hash.Add(layer.EmissiveColorB.Y);
                hash.Add(layer.EmissiveColorB.Z);
                hash.Add(layer.AnimFreq);
                hash.Add((int)layer.AnimDirMode);
                hash.Add(layer.AnimDirAngle);
                hash.Add(layer.AnimDualColor);
                hash.Add((int)layer.FadeMask);
                hash.Add(layer.FadeMaskFalloff);
                hash.Add(layer.GradientAngleDeg);
                hash.Add(layer.GradientScale);
                hash.Add(layer.GradientOffset);
                hash.Add((int)layer.Clip);
                hash.Add((int)layer.Kind);
                hash.Add((int)layer.TargetMap);
                hash.Add(layer.AffectsSpecular);
                hash.Add(layer.AffectsRoughness);
                hash.Add(layer.AffectsMetalness);
                hash.Add(layer.AffectsSheen);
                hash.Add(layer.DiffuseColor.X);
                hash.Add(layer.DiffuseColor.Y);
                hash.Add(layer.DiffuseColor.Z);
                hash.Add(layer.SpecularColor.X);
                hash.Add(layer.SpecularColor.Y);
                hash.Add(layer.SpecularColor.Z);
                hash.Add(layer.Roughness);
                hash.Add(layer.Metalness);
                hash.Add(layer.SheenRate);
                hash.Add(layer.SheenTint);
                hash.Add(layer.SheenAperture);
            }
        }

        return hash.ToHashCode();
    }

    private void TickHistoryTracker()
    {
        if (isReplayingHistory || initPhase != InitPhase.Done) return;
        // Suppress during active canvas drag/scale so a continuous gesture produces
        // one undo entry (the pre-gesture baseline), not a new one every 250 ms.
        if (canvasDraggingLayer || canvasScalingLayer) return;

        InitializeHistoryTrackerIfNeeded();

        var currentSignature = ComputeProjectSignature(project);
        if (historyBaselineSignature == currentSignature)
            return;

        var now = DateTime.UtcNow;
        var shouldPush = (now - historyLastCommitUtc).TotalMilliseconds > HistoryCoalesceMs
                         || undoHistory.Count == 0;

        if (shouldPush && historyBaselineSnapshot != null)
        {
            undoHistory.Add(historyBaselineSnapshot);
            TrimHistory(undoHistory);
            redoHistory.Clear();
        }

        historyBaselineSnapshot = project.CreateSnapshot();
        historyBaselineSignature = currentSignature;
        historyLastCommitUtc = now;
    }

    private static void TrimHistory(List<SavedProjectSnapshot> history)
    {
        if (history.Count <= HistoryMaxDepth) return;
        history.RemoveRange(0, history.Count - HistoryMaxDepth);
    }

    private bool CanUndo => undoHistory.Count > 0;
    private bool CanRedo => redoHistory.Count > 0;

    public int UndoHistoryCount => undoHistory.Count;
    public int RedoHistoryCount => redoHistory.Count;

    private void Undo()
    {
        if (undoHistory.Count == 0) return;

        var current = project.CreateSnapshot();
        var index = undoHistory.Count - 1;
        var target = undoHistory[index];
        undoHistory.RemoveAt(index);

        redoHistory.Add(current);
        TrimHistory(redoHistory);

        OnHistoryReplayed(ReplaySnapshot(target));
    }

    private void Redo()
    {
        if (redoHistory.Count == 0) return;

        var current = project.CreateSnapshot();
        var index = redoHistory.Count - 1;
        var target = redoHistory[index];
        redoHistory.RemoveAt(index);

        undoHistory.Add(current);
        TrimHistory(undoHistory);

        OnHistoryReplayed(ReplaySnapshot(target));
    }

    private bool ReplaySnapshot(SavedProjectSnapshot target)
    {
        isReplayingHistory = true;
        try
        {
            if (project.TryApplySnapshotInPlace(target, library))
                return true;
            project.ApplySnapshot(target, library);
            return false;
        }
        finally
        {
            isReplayingHistory = false;
        }
    }

    private void OnHistoryReplayed(bool inPlace)
    {
        ResetHistoryTrackerBaseline();

        if (highlightActive)
        {
            highlightActive = false;
            highlightGroupIndex = -1;
            highlightLayerIndex = -1;
            highlightFrameCounter = 0;
        }

        SyncImagePathBuf();

        if (inPlace)
        {
            // Structure unchanged: mesh/material handles still valid, GPU swap path composites
            // the new params onto the existing textures without re-triggering character redraw.
            MarkPreviewDirty(immediate: true);
            if (project.SelectedGroup != null)
                TryDirectEmissiveUpdate(project.SelectedGroup);
            return;
        }

        previewService.ClearTextureCache();
        previewService.ResetSwapState();
        previewService.NotifyMeshChanged();
        previewService.ForceFullRedrawNextCycle();
        MarkPreviewDirty(immediate: true);
    }

    private bool undoShortcutPrevZ;

    // FFXIV routes keyboard to the game first; ImGui.IsKeyPressed alone won't fire when a
    // skill bind owns the same key. Read raw state via IKeyState and null it so the game
    // never sees the keystroke this frame.
    private void HandleUndoRedoShortcuts()
    {
        if (initPhase != InitPhase.Done) return;
        if (ImGui.GetIO().WantTextInput)
        {
            undoShortcutPrevZ = false;
            return;
        }

        const Dalamud.Game.ClientState.Keys.VirtualKey ctrl = Dalamud.Game.ClientState.Keys.VirtualKey.CONTROL;
        const Dalamud.Game.ClientState.Keys.VirtualKey shift = Dalamud.Game.ClientState.Keys.VirtualKey.SHIFT;
        const Dalamud.Game.ClientState.Keys.VirtualKey zKey = Dalamud.Game.ClientState.Keys.VirtualKey.Z;

        var ctrlDown = keyState[ctrl];
        var shiftDown = keyState[shift];
        var zDown = keyState[zKey];

        if (ctrlDown && zDown && !undoShortcutPrevZ)
        {
            if (shiftDown) { if (CanRedo) Redo(); }
            else { if (CanUndo) Undo(); }
            keyState[zKey] = false;
        }

        undoShortcutPrevZ = zDown && ctrlDown;
    }

    private void SaveCanvasViewSettings()
    {
        config.UvViewTargetMap = (int)CanvasMapMode;
        config.UvCurrentDecalOnly = previewCurrentLayerOnly;
        config.UvShowBaseTexture = showCanvasBaseTexture;
        config.UvShowWireframe = showUvWireframe;
        config.Save();
    }

    public void OpenSettings()
    {
        IsOpen = true;
        requestSwitchToSettings = true;
    }

    public override void Draw()
    {
        WindowName = penumbra.IsAvailable
            ? Strings.T("window.main.title_connected") + "###SkinTattooMain"
            : Strings.T("window.main.title_disconnected") + "###SkinTattooMain";

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
            // Seed the mesh tick so it doesn't re-fire the work init just did.
            lastMeshGroupRef = project.SelectedGroup;
            lastMeshPathHash = BuildMeshPathHash(project.SelectedGroup);
            previewDirty = false;
            InitializeHistoryTrackerIfNeeded();
        }

        var loading = initPhase != InitPhase.Done;

        previewService.ApplyPendingSwaps();
        TickPendingAdd();
        TickMeshState();

        if (previewService.ExternalDirty)
        {
            previewService.ExternalDirty = false;
            MarkPreviewDirty();
        }

        UpdateHighlight();

        // -- Tab bar --
        if (ImGui.BeginTabBar("##MainTabs", ImGuiTabBarFlags.None))
        {
            // Tab 0: Settings
            var settingsFlags = requestSwitchToSettings
                ? ImGuiTabItemFlags.SetSelected
                : ImGuiTabItemFlags.None;
            if (requestSwitchToSettings) requestSwitchToSettings = false;

            if (ImGui.BeginTabItem(Strings.T("tab.settings") + "##mainTab0", settingsFlags))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            // Tab 1: Editor
            if (ImGui.BeginTabItem(Strings.T("tab.editor") + "##mainTab1"))
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
            if (ImGui.BeginTabItem(Strings.T("tab.help") + "##mainTab2"))
            {
                DrawHelpTab();
                ImGui.EndTabItem();
            }

            // Tab 3: Changelog
            if (ImGui.BeginTabItem(Strings.T("tab.changelog") + "##mainTab3"))
            {
                DrawChangelogTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        HandleUndoRedoShortcuts();
        TickHistoryTracker();

    }

    // -- Highlight ----------------------------------------------------------

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

    // -- Mesh state tick ------------------------------------------------------

    /// <summary>
    /// Runs every frame from Draw. Detects selected-group change, in-place mesh
    /// config mutation (add/remove model, resolver rerun), and live-tree swaps,
    /// then keeps <see cref="PreviewService.CurrentMesh"/> in sync. Lives in
    /// MainWindow so the canvas wireframe stays correct even when the 3D editor
    /// window is closed.
    /// </summary>
    private void TickMeshState()
    {
        if (initPhase != InitPhase.Done) return;

        var group = project.SelectedGroup;

        if (!ReferenceEquals(group, lastMeshGroupRef))
        {
            if (lastMeshGroupRef != null && project.Groups.Contains(lastMeshGroupRef))
                previewService.InvalidateEmissiveForGroup(lastMeshGroupRef);
            lastMeshGroupRef = group;
            lastMeshPathHash = 0;
            MarkPreviewDirty();
        }

        var pathHash = BuildMeshPathHash(group);
        if (pathHash != lastMeshPathHash)
        {
            lastMeshPathHash = pathHash;
            if (group != null && pathHash != 0)
            {
                var captured = group;
                Task.Run(() => previewService.LoadMeshForGroup(captured));
            }
            else
            {
                previewService.ClearMesh();
            }
            previewService.NotifyMeshChanged();
        }

        var now = DateTime.UtcNow;
        if ((now - lastLiveTreePollUtc).TotalSeconds >= LiveTreePollIntervalSec)
        {
            lastLiveTreePollUtc = now;
            PollLiveTreeChange(group);
        }
    }

    // int hash rather than string key so TickMeshState's per-frame identity check
    // doesn't allocate. 0 means "no mesh state" (mapped from old null-string sentinel).
    private static int BuildMeshPathHash(TargetGroup? group)
    {
        if (group == null) return 0;
        var hash = new HashCode();
        if (group.MeshSlots.Count > 0)
        {
            hash.Add(1);
            foreach (var slot in group.MeshSlots)
            {
                hash.Add(slot.GamePath, StringComparer.Ordinal);
                foreach (var mi in slot.MatIdx) hash.Add(mi);
            }
            var h = hash.ToHashCode();
            return h == 0 ? 1 : h;
        }
        if (!string.IsNullOrEmpty(group.MeshDiskPath) || group.MeshDiskPaths.Count > 0)
        {
            hash.Add(2);
            if (!string.IsNullOrEmpty(group.MeshDiskPath))
                hash.Add(group.MeshDiskPath, StringComparer.Ordinal);
            foreach (var p in group.MeshDiskPaths)
                if (!group.HiddenMeshPaths.Contains(p))
                    hash.Add(p, StringComparer.Ordinal);
            var h = hash.ToHashCode();
            return h == 0 ? 1 : h;
        }
        return 0;
    }

    /// <summary>
    /// 1Hz poll that reruns the resolver against the current Penumbra resource
    /// tree. When the player's worn gear (or a mod install) changes the mesh
    /// composition, MeshSlots/LiveTreeHash get refreshed and the next
    /// TickMeshState cycle reloads the mesh via the pathKey diff.
    /// </summary>
    private void PollLiveTreeChange(TargetGroup? group)
    {
        if (group == null) return;
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return;
        if (group.MeshSlots.Count == 0) return;
        if (string.IsNullOrEmpty(group.LiveTreeHash)) return;

        // Skip withUiData: the resolver only needs paths, not item names/icons.
        // Skipping halves the allocation of the returned tree DTO graph.
        var trees = penumbra.GetPlayerTrees(withUiData: false);
        if (trees == null) return;

        var newRes = skinMeshResolver.Resolve(group.MtrlGamePath!, trees);
        if (!newRes.Success) return;
        if (newRes.LiveTreeHash == group.LiveTreeHash) return;

        group.MeshSlots = newRes.MeshSlots;
        group.LiveTreeHash = newRes.LiveTreeHash;
        group.MeshGamePath = newRes.PrimaryMdlGamePath;
        group.MeshDiskPath = newRes.PrimaryMdlDiskPath;
        group.TargetMatIdx = newRes.MeshSlots[0].MatIdx;
        // pathKey will differ next frame -> TickMeshState reloads mesh.
    }

    // -- Loading overlay ------------------------------------------------------

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

        var label = Strings.T("hint.loading");
        var labelSize = ImGui.CalcTextSize(label);
        var labelPos = center + new Vector2(-labelSize.X * 0.5f, radius + 14f);
        fg.AddText(labelPos, ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.95f, 1f)), label);

        var sub = Strings.T("hint.loading_sub");
        var subSize = ImGui.CalcTextSize(sub);
        var subPos = new Vector2(center.X - subSize.X * 0.5f, labelPos.Y + labelSize.Y + 4f);
        fg.AddText(subPos, ImGui.GetColorU32(new Vector4(0.75f, 0.75f, 0.8f, 1f)), sub);
    }

    // -- Help tab --------------------------------------------------------------

    private void DrawHelpTab()
    {
        using var scroll = ImRaii.Child("##HelpScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) return;

        var header = new Vector4(1f, 0.8f, 0.3f, 1f);

        ImGui.TextColored(header, Strings.T("help.uv_editor_canvas"));
        ImGui.Separator();
        ImGui.BulletText(Strings.T("help.canvas_scroll"));
        ImGui.BulletText(Strings.T("help.canvas_pan"));
        ImGui.BulletText(Strings.T("help.canvas_move_decal"));
        ImGui.BulletText(Strings.T("help.canvas_select"));
        ImGui.BulletText(Strings.T("help.canvas_scale"));

        ImGui.Spacing();
        ImGui.TextColored(header, Strings.T("help.uv_editor_modifiers"));
        ImGui.Separator();
        ImGui.BulletText(Strings.T("help.mod_shift"));
        ImGui.BulletText(Strings.T("help.mod_ctrl"));
        ImGui.BulletText(Strings.T("help.mod_alt"));

        ImGui.Spacing();
        ImGui.TextColored(header, Strings.T("help.layer_target"));
        ImGui.Separator();
        ImGui.BulletText(Strings.T("help.delete_hint"));
        ImGui.BulletText(Strings.T("help.context_menu"));

        ImGui.Spacing();
        ImGui.TextColored(header, Strings.T("help.editor3d_camera"));
        ImGui.Separator();
        ImGui.BulletText(Strings.T("help.cam_rotate"));
        ImGui.BulletText(Strings.T("help.cam_pan"));
        ImGui.BulletText(Strings.T("help.cam_zoom"));
        ImGui.BulletText(Strings.T("help.cam_reset"));

        ImGui.Spacing();
        ImGui.TextColored(header, Strings.T("help.editor3d_decal"));
        ImGui.Separator();
        ImGui.BulletText(Strings.T("help.decal_place"));
        ImGui.BulletText(Strings.T("help.decal_scale"));

        ImGui.Spacing();
        ImGui.TextColored(header, Strings.T("help.editor3d_models"));
        ImGui.Separator();
        ImGui.BulletText(Strings.T("help.model_add"));
        ImGui.BulletText(Strings.T("help.model_manage"));
    }

    // -- Toolbar --------------------------------------------------------------

    private void DrawToolbar()
    {
        if (UiHelpers.SquareIconButton(10, FontAwesomeIcon.Cube))
        {
            if (ModelEditorWindowRef != null)
                ModelEditorWindowRef.IsOpen = !ModelEditorWindowRef.IsOpen;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.editor3d"));

        ImGui.SameLine();
        using (ImRaii.Disabled(project.SelectedLayer == null))
        {
            if (UiHelpers.SquareIconButton(2, FontAwesomeIcon.BorderNone))
                ResetSelectedLayer();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.reset_layer"));

        ImGui.SameLine();
        using (ImRaii.Disabled(!CanUndo))
        {
            if (UiHelpers.SquareIconButton(201, FontAwesomeIcon.Undo))
                Undo();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Strings.T("tooltip.undo"));

        ImGui.SameLine();
        using (ImRaii.Disabled(!CanRedo))
        {
            if (UiHelpers.SquareIconButton(202, FontAwesomeIcon.Redo))
                Redo();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Strings.T("tooltip.redo"));

        ImGui.SameLine();
        if (UiHelpers.SquareIconButton(3, FontAwesomeIcon.Eraser))
        {
            penumbra.ClearRedirect();
            penumbra.RedrawPlayer();
            previewService.ClearTextureCache();
            previewService.ResetSwapState();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.clear_redirect"));

        // External edit: export base texture + wireframe
        ImGui.SameLine();
        {
            var grp = project.SelectedGroup;
            using (ImRaii.Disabled(grp == null || string.IsNullOrEmpty(grp?.DiffuseGamePath)))
            {
                if (UiHelpers.SquareIconButton(40, FontAwesomeIcon.Image))
                    ExportBaseTexture(grp!);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.export_base_tex"));

            ImGui.SameLine();
            using (ImRaii.Disabled(grp == null || previewService.CurrentMesh == null))
            {
                if (UiHelpers.SquareIconButton(41, FontAwesomeIcon.BorderAll))
                    ExportUvWireframe(grp!);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.export_uv_wireframe"));
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(project.Groups.Count == 0))
        {
            if (UiHelpers.SquareIconButton(7, FontAwesomeIcon.FileExport))
                ModExportWindowRef?.OpenAs(Core.ExportTarget.LocalPmp);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.export_local"));

        ImGui.SameLine();
        using (ImRaii.Disabled(project.Groups.Count == 0 || !penumbra.IsAvailable))
        {
            if (UiHelpers.SquareIconButton(8, FontAwesomeIcon.Download))
                ModExportWindowRef?.OpenAs(Core.ExportTarget.InstallToPenumbra);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(penumbra.IsAvailable ? Strings.T("tooltip.install_penumbra") : Strings.T("tooltip.penumbra_not_running"));

        ImGui.SameLine();

        var group = project.SelectedGroup;
        if (group != null && !string.IsNullOrEmpty(group.MtrlGamePath))
        {
            var meshIcon = previewService.CurrentMesh == null ? FontAwesomeIcon.Cube : FontAwesomeIcon.SyncAlt;
            if (UiHelpers.SquareIconButton(4, meshIcon))
                previewService.ReResolveAndReloadMesh(group, skinMeshResolver);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Strings.T("tooltip.re_resolve_tip"));
            ImGui.SameLine();
        }

        using (ImRaii.Disabled(!penumbra.IsAvailable))
        {
            if (UiHelpers.SquareIconButton(7, FontAwesomeIcon.PaintBrush))
            {
                previewService.ClearTextureCache();
                previewService.ResetSwapState();
                penumbra.ClearRedirect();
                penumbra.RedrawPlayer();
                MarkPreviewDirty(immediate: true);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Strings.T("tooltip.full_redraw"));
        ImGui.SameLine();

        var autoPreview = config.AutoPreview;
        if (ImGui.Checkbox(Strings.T("checkbox.auto_preview"), ref autoPreview))
        {
            config.AutoPreview = autoPreview;
            config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.auto_preview"));

        if (!config.AutoPreview)
        {
            ImGui.SameLine();
            var hasTarget = project.SelectedGroup != null && !string.IsNullOrEmpty(project.SelectedGroup.DiffuseGamePath);
            using (ImRaii.Disabled(!hasTarget))
            {
                if (UiHelpers.SquareIconButton(5, FontAwesomeIcon.SyncAlt))
                    TriggerPreview();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.update_preview"));
        }

    }

    // -- Layout --------------------------------------------------------------

    private unsafe void DrawThreePanelLayout(float totalWidth, float height)
    {
        const float splitterWidth = 6f;
        const float minPanelWidth = 140f;
        const float minLeftPanelWidth = 280f;
        const float hoverExtend = 5f;
        const float hoverDelay = 0.1f;

        if (leftPanelWidth < 0) leftPanelWidth = MathF.Max(minLeftPanelWidth, totalWidth * 0.22f);

        var maxLeft = totalWidth - rightPanelWidth - minPanelWidth - splitterWidth * 2;
        var maxRight = totalWidth - leftPanelWidth - minPanelWidth - splitterWidth * 2;
        leftPanelWidth = Math.Clamp(leftPanelWidth, minLeftPanelWidth, Math.Max(minLeftPanelWidth, maxLeft));
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
            if (ImGuiP.SplitterBehavior(rect, id, ImGuiAxis.X, &sizeLeft, &sizeRight, minLeftPanelWidth, minRight, hoverExtend, hoverDelay, 0))
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
            // minLeftPanelWidth + splitter + minPanelWidth (center) ensures left+center stay above their mins.
            var minLeftCombined = minLeftPanelWidth + splitterWidth + minPanelWidth;
            if (ImGuiP.SplitterBehavior(rect, id, ImGuiAxis.X, &sizeLeft, &sizeRight, MathF.Max(minLeft, minLeftCombined), minPanelWidth, hoverExtend, hoverDelay, 0))
                rightPanelWidth = sizeRight;
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X + totalWidth - rightPanelWidth, origin.Y));

        // Right panel
        using (var right = ImRaii.Child("##RightPanel", new Vector2(rightPanelWidth, height), true))
        {
            if (right.Success) DrawParameterPanel();
        }
    }

    // -- Auto-preview & helpers ----------------------------------------------

    private void DrawActionsSection()
    {
        var group = project.SelectedGroup;
        var hasTarget = group != null && !string.IsNullOrEmpty(group.DiffuseGamePath);

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

        // skin CT: rebuild per-layer CT from current layer state (includes per-layer animation).
        // Do NOT fall through to HighlightEmissiveColor -- it would overwrite
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
        DisposeDiskTexPreviewCache();
    }

    private bool IsDeleteModifierHeld()
    {
        var keys = config.DeleteModifierKeys;
        if (keys == 0) return true;

        var io = ImGui.GetIO();
        if ((keys & 1) != 0 && !io.KeyCtrl) return false;
        if ((keys & 2) != 0 && !io.KeyShift) return false;
        if ((keys & 4) != 0 && !io.KeyAlt) return false;
        return true;
    }
}
