using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SkinTattoo.Core;
using SkinTattoo.Http;
using SkinTattoo.Interop;
using SkinTattoo.Services;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public class PerformanceWindow : Window
{
    private readonly PreviewService previewService;
    private readonly EmissiveCBufferHook emissiveHook;
    private readonly LibraryService libraryService;
    private readonly DecalProject project;

    // MainWindow holds the undo/redo stacks. Resolved via a getter func to keep the
    // constructor order-independent and avoid a circular reference.
    public Func<(int undo, int redo)>? HistoryCountsProvider { get; set; }

    private DateTime lastRefresh = DateTime.MinValue;
    private bool autoRefresh = true;

    private readonly struct Snapshot
    {
        public readonly long ManagedHeap;
        public readonly long WorkingSet;
        public readonly long PrivateBytes;
        public readonly long TotalAllocated;
        public readonly int GcGen0, GcGen1, GcGen2;
        public readonly PreviewService.DiagStats Preview;
        public readonly EmissiveCBufferHook.DiagStats Emissive;
        public readonly int LibraryEntries, LibraryFolders;
        public readonly int GroupCount, LayerCount, UndoCount, RedoCount;
        public readonly int LogBufferCount;
        public readonly DateTime Timestamp;

        public Snapshot(long mh, long ws, long pb, long ta, int g0, int g1, int g2,
            PreviewService.DiagStats preview, EmissiveCBufferHook.DiagStats emissive,
            int le, int lf, int gc, int lc, int uc, int rc, int lb, DateTime ts)
        {
            ManagedHeap = mh; WorkingSet = ws; PrivateBytes = pb; TotalAllocated = ta;
            GcGen0 = g0; GcGen1 = g1; GcGen2 = g2;
            Preview = preview; Emissive = emissive;
            LibraryEntries = le; LibraryFolders = lf;
            GroupCount = gc; LayerCount = lc; UndoCount = uc; RedoCount = rc;
            LogBufferCount = lb;
            Timestamp = ts;
        }
    }

    private Snapshot current;
    private Snapshot? baseline;

    public PerformanceWindow(
        PreviewService previewService,
        EmissiveCBufferHook emissiveHook,
        LibraryService libraryService,
        DecalProject project)
        : base(Strings.T("window.perf.title") + "###SkinTattooPerf",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.previewService = previewService;
        this.emissiveHook = emissiveHook;
        this.libraryService = libraryService;
        this.project = project;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var now = DateTime.UtcNow;
        if (autoRefresh && (now - lastRefresh).TotalMilliseconds >= 500)
            Refresh();

        if (ImGui.Button(Strings.T("button.refresh")))
            Refresh();
        ImGui.SameLine();
        ImGui.Checkbox(Strings.T("label.auto_refresh"), ref autoRefresh);
        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.force_gc")))
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Refresh();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.force_gc"));
        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.set_baseline")))
            baseline = current;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.set_baseline"));
        if (baseline.HasValue)
        {
            ImGui.SameLine();
            if (ImGui.Button(Strings.T("button.clear_baseline")))
                baseline = null;
        }

        if (baseline.HasValue)
        {
            var elapsed = (current.Timestamp - baseline.Value.Timestamp).TotalSeconds;
            var allocDelta = current.TotalAllocated - baseline.Value.TotalAllocated;
            var rate = elapsed > 0 ? allocDelta / elapsed : 0;
            ImGui.TextDisabled(
                Strings.T("perf.baseline_info", elapsed.ToString("0.0"),
                    FormatBytesSigned(allocDelta),
                    FormatBytes((long)rate) + "/s"));
        }

        ImGui.Separator();

        using var scroll = ImRaii.Child("##perfScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) return;

        var header = new Vector4(1f, 0.8f, 0.3f, 1f);

        ImGui.TextColored(header, Strings.T("perf.section.process"));
        ImGui.Separator();
        RowBytes(Strings.T("perf.managed_heap"), current.ManagedHeap, baseline?.ManagedHeap);
        RowBytes(Strings.T("perf.working_set"), current.WorkingSet, baseline?.WorkingSet);
        RowBytes(Strings.T("perf.private_bytes"), current.PrivateBytes, baseline?.PrivateBytes);
        RowBytes(Strings.T("perf.total_allocated"), current.TotalAllocated, baseline?.TotalAllocated);
        RowInt(Strings.T("perf.gc_gen0"), current.GcGen0, baseline?.GcGen0);
        RowInt(Strings.T("perf.gc_gen1"), current.GcGen1, baseline?.GcGen1);
        RowInt(Strings.T("perf.gc_gen2"), current.GcGen2, baseline?.GcGen2);

        ImGui.Spacing();
        ImGui.TextColored(header, Strings.T("perf.section.project"));
        ImGui.Separator();
        RowInt(Strings.T("perf.groups"), current.GroupCount, baseline?.GroupCount);
        RowInt(Strings.T("perf.layers"), current.LayerCount, baseline?.LayerCount);
        RowInt(Strings.T("perf.undo"), current.UndoCount, baseline?.UndoCount);
        RowInt(Strings.T("perf.redo"), current.RedoCount, baseline?.RedoCount);

        ImGui.Spacing();
        ImGui.TextColored(header, Strings.T("perf.section.preview"));
        ImGui.Separator();
        RowInt(Strings.T("perf.preview.redirects"), current.Preview.InitializedRedirects, baseline?.Preview.InitializedRedirects);
        RowInt(Strings.T("perf.preview.preview_disk"), current.Preview.PreviewDiskPaths, baseline?.Preview.PreviewDiskPaths);
        RowInt(Strings.T("perf.preview.preview_mtrl"), current.Preview.PreviewMtrlDiskPaths, baseline?.Preview.PreviewMtrlDiskPaths);
        RowInt(Strings.T("perf.preview.skin_ct"), current.Preview.SkinCtMaterials, baseline?.Preview.SkinCtMaterials);
        RowInt(Strings.T("perf.preview.base_tex_cache"), current.Preview.BaseTextureCache, baseline?.Preview.BaseTextureCache);
        RowInt(Strings.T("perf.preview.composite_results"), current.Preview.CompositeResults, baseline?.Preview.CompositeResults);
        RowInt(Strings.T("perf.preview.group_scratch"), current.Preview.GroupScratch, baseline?.Preview.GroupScratch);
        RowInt(Strings.T("perf.preview.row_pairs"), current.Preview.RowPairAllocators, baseline?.Preview.RowPairAllocators);
        RowInt(Strings.T("perf.preview.emissive_offsets"), current.Preview.EmissiveOffsets, baseline?.Preview.EmissiveOffsets);
        RowInt(Strings.T("perf.preview.last_applied_emissive"), current.Preview.LastAppliedEmissive, baseline?.Preview.LastAppliedEmissive);
        RowInt(Strings.T("perf.preview.vanilla_ct"), current.Preview.VanillaColorTables, baseline?.Preview.VanillaColorTables);
        RowInt(Strings.T("perf.preview.last_built_ct"), current.Preview.LastBuiltColorTables, baseline?.Preview.LastBuiltColorTables);
        RowInt(Strings.T("perf.preview.mask_support"), current.Preview.MaskSupportCache, baseline?.Preview.MaskSupportCache);
        RowInt(Strings.T("perf.preview.skin_shpk_ct"), current.Preview.SkinShpkCtCache, baseline?.Preview.SkinShpkCtCache);
        RowInt(Strings.T("perf.preview.index_map"), current.Preview.IndexMapGamePaths, baseline?.Preview.IndexMapGamePaths);

        ImGui.Spacing();
        ImGui.TextColored(header, Strings.T("perf.section.emissive_hook"));
        ImGui.Separator();
        Row(Strings.T("perf.hook.enabled"), current.Emissive.Enabled ? "on" : "off");
        RowInt(Strings.T("perf.hook.targets"), current.Emissive.Targets, baseline?.Emissive.Targets);
        RowInt(Strings.T("perf.hook.offset_cache"), current.Emissive.OffsetCache, baseline?.Emissive.OffsetCache);
        RowInt(Strings.T("perf.hook.logged_misses"), current.Emissive.LoggedMisses, baseline?.Emissive.LoggedMisses);

        ImGui.Spacing();
        ImGui.TextColored(header, Strings.T("perf.section.library"));
        ImGui.Separator();
        RowInt(Strings.T("perf.library.entries"), current.LibraryEntries, baseline?.LibraryEntries);
        RowInt(Strings.T("perf.library.folders"), current.LibraryFolders, baseline?.LibraryFolders);

        ImGui.Spacing();
        ImGui.TextColored(header, Strings.T("perf.section.debug"));
        ImGui.Separator();
        RowInt(Strings.T("perf.debug.log_buffer"), current.LogBufferCount, baseline?.LogBufferCount);
    }

    private void Refresh()
    {
        lastRefresh = DateTime.UtcNow;

        long workingSet = 0, privateBytes = 0;
        try
        {
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            workingSet = proc.WorkingSet64;
            privateBytes = proc.PrivateMemorySize64;
        }
        catch { }

        int layerCount = 0;
        foreach (var g in project.Groups) layerCount += g.Layers.Count;

        int undo = 0, redo = 0;
        if (HistoryCountsProvider != null)
            (undo, redo) = HistoryCountsProvider();

        current = new Snapshot(
            GC.GetTotalMemory(false),
            workingSet,
            privateBytes,
            GC.GetTotalAllocatedBytes(false),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            previewService.GetDiagStats(),
            emissiveHook.GetDiagStats(),
            libraryService.EntryCount, libraryService.FolderCount,
            project.Groups.Count, layerCount, undo, redo,
            DebugServer.LogBuffer.Count,
            lastRefresh);
    }

    private static void Row(string label, string value)
    {
        ImGui.BulletText($"{label}:");
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
    }

    private static void RowInt(string label, int value, int? baselineValue)
    {
        ImGui.BulletText($"{label}:");
        ImGui.SameLine();
        if (baselineValue.HasValue)
        {
            var delta = value - baselineValue.Value;
            ImGui.TextUnformatted(value.ToString());
            ImGui.SameLine();
            ImGui.TextColored(DeltaColor(delta), $"({FormatIntDelta(delta)})");
        }
        else
        {
            ImGui.TextUnformatted(value.ToString());
        }
    }

    private static void RowBytes(string label, long value, long? baselineValue)
    {
        ImGui.BulletText($"{label}:");
        ImGui.SameLine();
        if (baselineValue.HasValue)
        {
            var delta = value - baselineValue.Value;
            ImGui.TextUnformatted(FormatBytes(value));
            ImGui.SameLine();
            ImGui.TextColored(DeltaColor(delta), $"({FormatBytesSigned(delta)})");
        }
        else
        {
            ImGui.TextUnformatted(FormatBytes(value));
        }
    }

    private static Vector4 DeltaColor(long delta) =>
        delta > 0 ? new Vector4(1f, 0.55f, 0.35f, 1f)
        : delta < 0 ? new Vector4(0.4f, 0.9f, 0.5f, 1f)
        : new Vector4(0.6f, 0.6f, 0.6f, 1f);

    private static string FormatIntDelta(int delta) =>
        delta == 0 ? "0" : (delta > 0 ? "+" : "") + delta;

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "-";
        double v = bytes;
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}";
    }

    private static string FormatBytesSigned(long bytes)
    {
        if (bytes == 0) return "0";
        var sign = bytes >= 0 ? "+" : "-";
        return sign + FormatBytes(Math.Abs(bytes));
    }
}
