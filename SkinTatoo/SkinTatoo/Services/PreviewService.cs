using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

/// <summary>
/// Axis-aligned dirty region in texture pixel space. Used by the composite pipeline to
/// restrict per-frame work (base restore + layer paint + sub-region GPU upload) to the
/// bbox that could have changed between frames.
/// </summary>
public readonly struct DirtyRect
{
    public readonly int X;
    public readonly int Y;
    public readonly int W;
    public readonly int H;

    public DirtyRect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }

    public bool IsEmpty => W <= 0 || H <= 0;
    public static DirtyRect Empty => default;
    public static DirtyRect Full(int w, int h) => new(0, 0, w, h);

    public static DirtyRect Union(DirtyRect a, DirtyRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        int x0 = Math.Min(a.X, b.X);
        int y0 = Math.Min(a.Y, b.Y);
        int x1 = Math.Max(a.X + a.W, b.X + b.W);
        int y1 = Math.Max(a.Y + a.H, b.Y + b.H);
        return new DirtyRect(x0, y0, x1 - x0, y1 - y0);
    }

    public DirtyRect Clamp(int texW, int texH)
    {
        int x0 = Math.Max(0, X);
        int y0 = Math.Max(0, Y);
        int x1 = Math.Min(texW, X + W);
        int y1 = Math.Min(texH, Y + H);
        if (x1 <= x0 || y1 <= y0) return Empty;
        return new DirtyRect(x0, y0, x1 - x0, y1 - y0);
    }
}

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

    /// <summary>
    /// Fired by callers (NOT by LoadMesh*/ClearMesh internals) to notify that
    /// the mesh state has been mutated and any view caching it needs to
    /// re-evaluate. Subscribers should be cheap and main-thread only —
    /// callers on background threads must marshal to the framework thread
    /// before invoking <see cref="NotifyMeshChanged"/>.
    ///
    /// Why not auto-fire from inside LoadMesh*: Plugin.cs project init runs
    /// LoadMeshForGroup on a background thread, and ImGui-state subscribers
    /// (e.g. ModelEditorWindow) cannot tolerate cross-thread invocation.
    /// </summary>
    public event Action? MeshChanged;
    public void NotifyMeshChanged() => MeshChanged?.Invoke();

    // GPU swap state tracking — ConcurrentDictionary because background composite Task and
    // main thread (UpdatePreviewFull / EnsureEmissiveInitialized / ResetSwapState) can race.
    private readonly ConcurrentDictionary<string, byte> initializedRedirects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> previewDiskPaths =
        new(StringComparer.OrdinalIgnoreCase);

    // Cache base textures to avoid re-reading from disk on every update.
    // Disk-path key has no encoding constraint, so default comparer is fine.
    private readonly ConcurrentDictionary<string, (byte[] Data, int Width, int Height)> baseTextureCache = new();

    // Emissive ConstantBuffer offsets (mtrlGamePath → byte offset, used for full redraw .mtrl building)
    private readonly ConcurrentDictionary<string, int> emissiveOffsets = new();

    // Track mtrl preview disk paths (mtrlGamePath → disk path for ColorTable matching)
    private readonly ConcurrentDictionary<string, string> previewMtrlDiskPaths =
        new(StringComparer.OrdinalIgnoreCase);

    // v1 PBR: per-TargetGroup row pair allocators (keyed by MtrlGamePath)
    private readonly ConcurrentDictionary<string, RowPairAllocator> rowPairAllocators =
        new(StringComparer.OrdinalIgnoreCase);

    // v1 PBR: index map game path resolved from each mtrl's sampler 0x565F8FD8.
    // Keyed by MtrlGamePath. Cached after first resolution.
    private readonly ConcurrentDictionary<string, string> indexMapGamePaths =
        new(StringComparer.OrdinalIgnoreCase);

    // sampler ID for g_SamplerIndex per Penumbra ShpkFile.cs:17
    private const uint IndexSamplerId = 0x565F8FD8u;

    // Cached vanilla ColorTable bytes per material (keyed by MtrlGamePath).
    // Populated on first ColorTable write from the main thread; background composite
    // reads from here to avoid cross-thread GPU access.
    private readonly ConcurrentDictionary<string, (Half[] Data, int Width, int Height)> vanillaColorTables =
        new(StringComparer.OrdinalIgnoreCase);

    // Most recently built (modified) ColorTable per material — populated by the background
    // composite cycle each time a ColorTableEntry is queued. Surfaced via PBR inspector.
    private readonly ConcurrentDictionary<string, (Half[] Data, int Width, int Height)> lastBuiltColorTables =
        new(StringComparer.OrdinalIgnoreCase);

    // Async compositing
    private CancellationTokenSource? asyncCancel;
    private volatile SwapBatch? pendingBatch;

    // Serialize background composite work + coalesce slider-spam:
    // every StartAsyncInPlace bumps composeRequestSeq; the worker takes the lock and
    // skips its run if a newer request has already arrived. Without this, slider drags
    // queue dozens of Tasks that all race to write the same _n.tex / _id.tex files.
    private readonly SemaphoreSlim composeLock = new(1, 1);
    private long composeRequestSeq;

    // File-write throttle. Each WriteBgraTexFile pushes ~64MB through the FS at 4096²
    // (synchronous IO ≈ 64-128ms per file), and Penumbra only re-reads files on Full
    // Redraw — during inplace cycles GPU swap is the source of truth. So we only flush
    // disk every 250ms; sustained drag still pumps SwapTexture at ~30Hz, but disk IO
    // doesn't dominate the compose thread anymore.
    private DateTime lastFileFlushUtc = DateTime.MinValue;
    private const double FileFlushIntervalMs = 250;

    // Game-side GPU swap throttle. Each SwapTexture at 4096² costs 5-15ms of main thread
    // work (GpuTexture.CreateTexture2D + 64MB InitializeContents + Interlocked.Exchange).
    // At 30Hz drag that exceeds the frame budget. 3D editor preview already reflects the
    // latest state via the ModelEditorWindow subresource upload path, so the game-side
    // swap only needs to catch up every `config.GameSwapIntervalMs`. Idle detection forces
    // a final flush shortly after the user stops dragging so the game doesn't lag behind.
    private DateTime lastGameSwapUtc = DateTime.MinValue;
    private DateTime lastComposeRequestUtc = DateTime.MinValue;
    private bool pendingIdleFlush;
    // ~50ms of quiet time is a reliable "user stopped interacting" signal at 30Hz drag.
    private const double IdleFlushAfterMs = 50;

    // Emissive dedupe: skip UpdateEmissiveViaColorTable + SetTargetByPath when the color
    // for this material hasn't changed since last flush. Without this, a slider drag that
    // doesn't touch emissive still spams tree-walks + GPU CT texture recreates every cycle.
    private readonly Dictionary<string, Vector3> lastAppliedEmissive =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-group reusable scratch buffers — eliminates the per-cycle 4-67MB LOH allocations
    // that thrashed GC during a 3D-editor drag. Filled on the background thread under
    // composeLock; consumed by the main thread next frame.
    //
    // All buffers are SINGLE-buffered. With the 30Hz throttle + serialized compose,
    // main-thread consumption finishes well before the next compose runs. The Rgba/PreviewRgba
    // ones get stored in compositeResults for the 3D editor to read async; in the rare case
    // where the 3D editor reads while a new compose is mid-write, we get at most one frame
    // of torn pixels which self-heals on the next compose (≤33ms). The previous double-buffer
    // strategy doubled resident memory (600MB+ at 4096²), unacceptable.
    /// <summary>
    /// Per-buffer dirty-rect state: tracks the previous cycle's layer-union so we can
    /// compute `dirty = current_union ∪ previous_union` and only repaint that region.
    /// </summary>
    private class DirtyTracker
    {
        public DirtyRect LastUnion;     // union of layer bboxes painted last cycle
        public bool NeedsFullInit = true; // first cycle must populate the full buffer
        public DirtyRect LastDirty;     // dirty rect written this cycle (for downstream upload)

        public DirtyRect ComputeDirty(DirtyRect currentUnion, int texW, int texH)
        {
            DirtyRect d = NeedsFullInit
                ? DirtyRect.Full(texW, texH)
                : DirtyRect.Union(currentUnion, LastUnion);
            return d.Clamp(texW, texH);
        }

        public void Commit(DirtyRect currentUnion, DirtyRect dirty)
        {
            LastUnion = currentUnion;
            LastDirty = dirty;
            NeedsFullInit = false;
        }

        public void Reset()
        {
            LastUnion = DirtyRect.Empty;
            LastDirty = DirtyRect.Empty;
            NeedsFullInit = true;
        }
    }

    private class GroupScratch
    {
        public byte[]? Bgra;        // diffuse BGRA → file write + GPU swap
        public byte[]? Rgba;        // diffuse RGBA → compositeResults dict (3D editor)
        public byte[]? PreviewRgba; // 3D-editor preview RGBA when AffectsDiffuse=false on a layer
        public byte[]? IdxRgba;     // index map composite intermediate (RGBA)
        public byte[]? IdxBgra;     // index map BGRA → file write + GPU swap
        public byte[]? NormBgra;    // legacy emissive norm BGRA
        public byte[]? NormRgba;    // legacy emissive norm intermediate (RGBA)

        // Dirty rect trackers — one per output buffer, since each paints a different
        // layer subset (all-visible, AffectsDiffuse, AllocatedRowPair, AffectsEmissive).
        public readonly DirtyTracker RgbaTracker = new();
        public readonly DirtyTracker PreviewTracker = new();
        public readonly DirtyTracker IdxTracker = new();
        public readonly DirtyTracker NormTracker = new();
    }
    private readonly ConcurrentDictionary<string, GroupScratch> groupScratch =
        new(StringComparer.OrdinalIgnoreCase);

    private static byte[] EnsureBuf(ref byte[]? buf, int size)
    {
        if (buf == null || buf.Length < size) buf = new byte[size];
        return buf;
    }

    /// <summary>
    /// Parallel-row helper for the per-pixel composite loops. Falls back to serial when
    /// the row count is small enough that worker dispatch overhead would dominate.
    /// </summary>
    private static void ParallelRows(int pyMin, int pyMax, Action<int> rowAction)
    {
        int rowCount = pyMax - pyMin + 1;
        if (rowCount <= 0) return;
        // Below this threshold a serial loop is faster than waking thread pool workers.
        // ~64 rows × ~2k px/row at typical decal sizes is the crossover on a 4-core box.
        if (rowCount < 64)
        {
            for (int py = pyMin; py <= pyMax; py++) rowAction(py);
            return;
        }
        Parallel.For(pyMin, pyMax + 1, rowAction);
    }

    private record SwapBatchEntry(string GamePath, string? DiskPath, byte[] BgraData, int Width, int Height);
    private record EmissiveEntry(string MtrlGamePath, string? MtrlDiskPath, Vector3 Color, int CBufferOffset);
    private record ColorTableEntry(string MtrlGamePath, string? MtrlDiskPath, Half[] Data, int Width, int Height);
    private record SwapBatch(List<SwapBatchEntry> Textures, List<EmissiveEntry> Emissives, List<ColorTableEntry> ColorTables);

    public MeshData? CurrentMesh => currentMesh;

    /// <summary>Whether in-place GPU swap is available for all active groups.</summary>
    public bool CanSwapInPlace { get; private set; }

    /// <summary>Number of paths currently initialized for GPU swap.</summary>
    public int InitializedPathCount => initializedRedirects.Count;

    // 3D editor integration: per-group composite results keyed by diffuseGamePath.
    // Dirty rect lets the main thread upload only the changed region (UpdateSubresource
    // with a ResourceRegion) instead of re-creating + re-uploading the whole 64MB texture.
    private readonly ConcurrentDictionary<string, (byte[] Data, int Width, int Height, DirtyRect Dirty)> compositeResults =
        new(StringComparer.OrdinalIgnoreCase);
    private long compositeVersion;
    public bool ExternalDirty { get; set; }
    public (byte[] Data, int Width, int Height, DirtyRect Dirty)? GetCompositeForGroup(string? diffuseGamePath)
    {
        if (diffuseGamePath != null && compositeResults.TryGetValue(diffuseGamePath, out var result))
            return result;
        return null;
    }
    public long CompositeVersion => compositeVersion;
    public void MarkDirty() => ExternalDirty = true;

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
        => LoadMeshWithMatIdx(gameMdlPath, null, null);

    /// <summary>
    /// Load a single mdl, applying an optional matIdx filter and an optional
    /// Penumbra-resolved disk path. Used by SkinMeshResolver-driven flows
    /// where we know exactly which mat slots to extract and have the
    /// mod-aware disk path in hand.
    /// </summary>
    public bool LoadMeshWithMatIdx(string gameMdlPath, int[]? matIdx, string? diskPath)
    {
        DebugServer.AppendLog($"[PreviewService] LoadMesh: {gameMdlPath}"
            + (matIdx != null ? $" matIdx=[{string.Join(",", matIdx)}]" : ""));

        MeshData? meshData;
        try
        {
            meshData = meshExtractor.ExtractMesh(gameMdlPath, matIdx, diskPath);
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

    public bool LoadMeshes(List<string> paths)
    {
        if (paths.Count == 0) return false;
        if (paths.Count == 1) return LoadMesh(paths[0]);

        DebugServer.AppendLog($"[PreviewService] LoadMeshes: {paths.Count} files");
        try
        {
            var merged = meshExtractor.ExtractAndMerge(paths);
            if (merged == null) return false;
            currentMesh = merged;
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"LoadMeshes exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Load mesh for a target group, picking the right code path based on
    /// what fields are populated. Use this for any mesh load triggered by
    /// "the user wants to see this group's mesh on the canvas":
    /// - new resolver groups → MeshSlots (multi-mdl + per-mdl matIdx filter)
    /// - migrated single-mdl groups → MeshGamePath + TargetMatIdx
    /// - pre-resolver groups from old configs → AllMeshPaths
    ///
    /// All three callsites (resource browser reload button, ModelEditorWindow
    /// group switch, plugin startup project init) should funnel through here
    /// so the legacy paths can never accidentally drop slots.
    /// </summary>
    public bool LoadMeshForGroup(TargetGroup group)
    {
        if (group.MeshSlots.Count > 0)
            return LoadMeshSlots(group.MeshSlots);

        if (!string.IsNullOrEmpty(group.MeshGamePath))
            return LoadMeshWithMatIdx(
                group.MeshGamePath!,
                group.TargetMatIdx.Length > 0 ? group.TargetMatIdx : null,
                group.MeshDiskPath);

        if (group.AllMeshPaths.Count > 0)
            return LoadMeshes(group.AllMeshPaths);

        return false;
    }

    /// <summary>
    /// Load a list of mdl + matIdx slots and merge them into a single mesh.
    /// This is what SkinMeshResolver-driven flows use — body skin with a
    /// mod-injected material can map to several equipment mdls that each
    /// contribute a body region (top/glv/dwn/sho).
    /// </summary>
    public bool LoadMeshSlots(List<MeshSlot> slots)
    {
        if (slots.Count == 0) return false;
        DebugServer.AppendLog($"[PreviewService] LoadMeshSlots: {slots.Count} slot(s)");
        try
        {
            var merged = meshExtractor.ExtractAndMergeSlots(slots);
            if (merged == null)
            {
                log.Error($"LoadMeshSlots failed: ExtractAndMergeSlots returned null");
                return false;
            }
            currentMesh = merged;
            DebugServer.AppendLog($"[PreviewService] Mesh loaded: {merged.Vertices.Length} verts, {merged.TriangleCount} tris");
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"LoadMeshSlots exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>Update preview — auto-selects full redraw or async in-place GPU swap.</summary>
    public void UpdatePreview(DecalProject project)
    {
        activeProject = project;  // remember for opportunistic vanilla CT caching in ApplyPendingSwaps
        if (config.UseGpuSwap && textureSwap != null && CheckCanSwapInPlace(project))
        {
            StartAsyncInPlace(project);
        }
        else
        {
            UpdatePreviewFull(project);
        }
    }

    // Sticky reference so ApplyPendingSwaps can attempt vanilla CT caching every frame
    // (only fires when project changes are pending). Set by UpdatePreview*.
    private DecalProject? activeProject;

    /// <summary>Call from Draw() every frame to apply completed async swaps.</summary>
    public unsafe void ApplyPendingSwaps()
    {
        // Opportunistic vanilla CT cache attempt — runs every frame but is idempotent
        // and only fires for materials not yet cached. Catches the case where Full Redraw
        // happened in an earlier frame and the GPU is now ready to read.
        if (activeProject != null)
            TryCacheVanillaColorTables(activeProject);

        var batch = Interlocked.Exchange(ref pendingBatch, null);
        if (batch == null) return;

        var nowUtc = DateTime.UtcNow;

        // Idle detection: if the user has stopped triggering new composites for IdleFlushAfterMs,
        // force a flush regardless of the throttle window so the "final" drag state lands with
        // minimal tail latency. Otherwise the worst-case delay would be a full GameSwapIntervalMs.
        bool idleFlush = false;
        if (pendingIdleFlush
            && (nowUtc - lastComposeRequestUtc).TotalMilliseconds >= IdleFlushAfterMs)
        {
            idleFlush = true;
            pendingIdleFlush = false;
        }

        // Throttle: skip game-side swap work if we're still inside the throttle window AND the
        // user is still actively dragging (no idle flush). Re-stash the batch for the next tick.
        // Clamp at 33ms floor (~30Hz) regardless of config — anything lower is pure main-thread
        // waste since compose itself is 30Hz capped, and defends against legacy configs with
        // lower values that would otherwise hang the game until the user opens settings.
        int intervalMs = System.Math.Max(33, config.GameSwapIntervalMs);
        bool withinThrottle = (nowUtc - lastGameSwapUtc).TotalMilliseconds < intervalMs;
        if (withinThrottle && !idleFlush)
        {
            // Only re-stash if no newer batch has arrived — the worker may have overwritten
            // pendingBatch between our Exchange and now; in that case drop ours silently.
            Interlocked.CompareExchange(ref pendingBatch, batch, null);
            return;
        }
        lastGameSwapUtc = nowUtc;

        var charBase = textureSwap?.GetLocalPlayerCharacterBase();
        if (charBase == null) return;

        foreach (var entry in batch.Textures)
        {
            var slot = textureSwap!.FindTextureSlot(charBase, entry.GamePath, entry.DiskPath);
            if (slot != null)
                textureSwap.SwapTexture(slot, entry.BgraData, entry.Width, entry.Height);
        }

        foreach (var ct in batch.ColorTables)
        {
            textureSwap!.ReplaceColorTableRaw(charBase, ct.MtrlGamePath, ct.MtrlDiskPath, ct.Data, ct.Width, ct.Height);
        }

        foreach (var em in batch.Emissives)
        {
            // Legacy single-emissive path: only fires when no ColorTable entry was queued
            // for this material (skin.shpk fallback or non-Dawntrail layouts).
            // Dedupe: skip if this mtrl's color is identical to what we pushed last time —
            // otherwise a drag that never touches emissive still spams tree walks + CT
            // texture recreation every cycle.
            if (lastAppliedEmissive.TryGetValue(em.MtrlGamePath, out var prevColor) && prevColor == em.Color)
                continue;
            lastAppliedEmissive[em.MtrlGamePath] = em.Color;

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

    /// <summary>
    /// Ensure emissive mtrl redirect is initialized for a group (for highlight preview).
    /// Does a one-time mtrl build + Penumbra redirect + player redraw if needed.
    /// Returns true if emissive is ready for highlight.
    /// </summary>
    public bool EnsureEmissiveInitialized(TargetGroup group)
    {
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return false;

        // Already initialized?
        if (emissiveOffsets.ContainsKey(group.MtrlGamePath!) && emissiveOffsets[group.MtrlGamePath!] > 0)
            return true;

        // Build emissive mtrl
        var mtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;
        if (string.IsNullOrEmpty(mtrlDisk) && string.IsNullOrEmpty(group.MtrlGamePath)) return false;

        var safeName = MakeSafeFileName(group.Name);
        var mtrlOutPath = Path.Combine(outputDir, $"preview_{safeName}.mtrl");
        var defaultColor = new Vector3(1f, 1f, 1f);

        if (!TryBuildEmissiveMtrl(mtrlDisk ?? group.MtrlGamePath!, mtrlOutPath, defaultColor, out var emOffset))
            return false;

        var redirects = new Dictionary<string, string> { [group.MtrlGamePath!] = mtrlOutPath };
        previewMtrlDiskPaths[group.MtrlGamePath!] = mtrlOutPath;
        if (emOffset >= 0)
            emissiveOffsets[group.MtrlGamePath!] = emOffset;
        initializedRedirects.TryAdd(group.MtrlGamePath!, 0);
        previewDiskPaths[group.MtrlGamePath!] = mtrlOutPath;

        // Also build norm with full-white alpha mask so highlight covers entire decal area
        if (!string.IsNullOrEmpty(group.NormGamePath) && !string.IsNullOrEmpty(group.NormDiskPath))
        {
            var normDisk = group.OrigNormDiskPath ?? group.NormDiskPath;
            if (!string.IsNullOrEmpty(normDisk))
            {
                var baseTex = LoadBaseTexture(group);
                var normResult = BuildFullAlphaNorm(normDisk, baseTex.Width, baseTex.Height);
                if (normResult != null)
                {
                    var normPath = Path.Combine(outputDir, $"preview_{safeName}_n.tex");
                    WriteBgraTexFile(normPath, normResult, baseTex.Width, baseTex.Height);
                    redirects[group.NormGamePath!] = normPath;
                    initializedRedirects.TryAdd(group.NormGamePath!, 0);
                    previewDiskPaths[group.NormGamePath!] = normPath;
                }
            }
        }

        penumbra.SetTextureRedirects(redirects);
        penumbra.RedrawPlayer();
        DebugServer.AppendLog($"[PreviewService] Emissive initialized for highlight: {group.MtrlGamePath} offset={emOffset}");
        return emOffset >= 0;
    }

    /// <summary>Build a normal map with alpha=255 everywhere (full emissive coverage for highlight).</summary>
    private byte[]? BuildFullAlphaNorm(string normDiskPath, int w, int h)
    {
        byte[] baseNorm;
        if (baseTextureCache.TryGetValue(normDiskPath, out var cachedNorm))
        {
            var (data, iw, ih) = cachedNorm;
            baseNorm = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
        }
        else
        {
            var normImg = File.Exists(normDiskPath) ? imageLoader.LoadImage(normDiskPath) : LoadGameTexture(normDiskPath);
            if (normImg == null) return null;
            baseTextureCache[normDiskPath] = normImg.Value;
            var (data, iw, ih) = normImg.Value;
            baseNorm = (iw != w || ih != h) ? ResizeBilinear(data, iw, ih, w, h) : (byte[])data.Clone();
        }

        // Set alpha to 255 everywhere for full highlight coverage
        for (int i = 3; i < baseNorm.Length; i += 4)
            baseNorm[i] = 255;
        return baseNorm;
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
                initializedRedirects.TryAdd(gamePath, 0);
                previewDiskPaths[gamePath] = diskPath;
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
    private unsafe void StartAsyncInPlace(DecalProject project)
    {
        // Bump request sequence so any in-flight worker that hasn't taken the lock yet
        // will see itself as superseded and skip its work.
        var mySeq = Interlocked.Increment(ref composeRequestSeq);

        // Interaction heartbeat for idle-flush detection in ApplyPendingSwaps.
        lastComposeRequestUtc = DateTime.UtcNow;
        pendingIdleFlush = true;

        // Cancel any previous background work AND dispose the old CTS so we don't
        // leak its WaitHandle / registration list (~30 leaks/sec during a sustained drag).
        var oldCts = asyncCancel;
        asyncCancel = new CancellationTokenSource();
        var token = asyncCancel.Token;
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch { /* old CTS may already be disposed by Dispose() */ }

        // v1 PBR: cache vanilla ColorTable from GPU before spawning the background task.
        // Idempotent — only the FIRST successful cache per material counts. Vanilla normal
        // scan is handled lazily inside TryAllocateRowPairForLayer (disk-only, no GPU needed).
        TryCacheVanillaColorTables(project);

        // Capture all data needed by background thread (avoid touching mutable state later)
        var jobs = new List<(TargetGroup Group, string DiffuseGamePath, string? DiskPath,
            string? NormGamePath, string? NormDiskPath, string? MtrlGamePath, int EmissiveOffset,
            string? IndexGamePath, string? IndexDiskPath,
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

            // v1 PBR: resolve index map path (cached) and look up its staged disk path
            var indexGame = GetIndexMapGamePath(group);
            string? indexDisk = null;
            if (!string.IsNullOrEmpty(indexGame))
                previewDiskPaths.TryGetValue(indexGame, out indexDisk);

            // Snapshot layer parameters so background thread reads stable data
            var snapshots = new List<LayerSnapshot>();
            foreach (var l in group.Layers)
                snapshots.Add(new LayerSnapshot(l));

            jobs.Add((group, group.DiffuseGamePath, diffDisk,
                group.NormGamePath, normDisk ?? (group.OrigNormDiskPath ?? group.NormDiskPath),
                group.MtrlGamePath, emOff,
                indexGame, indexDisk,
                snapshots,
                GetCombinedEmissiveColor(group.Layers)));
        }

        Task.Run(() =>
        {
            // Serialize composite cycles. We're already on a thread-pool thread so a
            // synchronous Wait is fine — and it lets us stay in the unsafe-inheriting
            // lambda without the async-state-machine restriction.
            try { composeLock.Wait(token); }
            catch (OperationCanceledException) { return; }

            try
            {
                // Coalesce: if a newer request arrived while we were waiting on the
                // lock, drop this one — the next worker will compose the latest state.
                if (Interlocked.Read(ref composeRequestSeq) != mySeq) return;
                if (token.IsCancellationRequested) return;

                var texEntries = new List<SwapBatchEntry>();
                var emEntries = new List<EmissiveEntry>();
                var ctEntries = new List<ColorTableEntry>();

                // Decide once per cycle whether this compose flushes disk (250ms throttle).
                var nowUtc = DateTime.UtcNow;
                bool flushFiles = (nowUtc - lastFileFlushUtc).TotalMilliseconds >= FileFlushIntervalMs;
                if (flushFiles) lastFileFlushUtc = nowUtc;

                foreach (var job in jobs)
                {
                    if (token.IsCancellationRequested) return;

                    // Convert snapshots to DecalLayers for compositing
                    var layers = job.Layers.ConvertAll(s => s.ToDecalLayer());

                    // Diffuse composite
                    var baseTex = LoadBaseTexture(job.Group);
                    int byteCount = baseTex.Width * baseTex.Height * 4;
                    var scratch = groupScratch.GetOrAdd(job.DiffuseGamePath, _ => new GroupScratch());

                    // 3D editor preview diffuse — paints ALL visible layers with images so the
                    // user sees decal placement even when AffectsDiffuse is off.
                    bool hasPreviewOnly = false;
                    foreach (var l in layers)
                    {
                        if (l.IsVisible && !string.IsNullOrEmpty(l.ImagePath) && !l.AffectsDiffuse)
                        { hasPreviewOnly = true; break; }
                    }

                    // GPU diffuse — only paints layers with AffectsDiffuse=true.
                    var rgbaBuf = EnsureBuf(ref scratch.Rgba, byteCount);
                    var rgba = CpuUvComposite(layers, baseTex.Data, baseTex.Width, baseTex.Height,
                        outputBuffer: rgbaBuf, tracker: scratch.RgbaTracker);

                    byte[]? previewRgba;
                    DirtyRect previewDirty;
                    if (hasPreviewOnly)
                    {
                        var prevBuf = EnsureBuf(ref scratch.PreviewRgba, byteCount);
                        previewRgba = CpuUvComposite(layers, baseTex.Data, baseTex.Width, baseTex.Height,
                            ignoreAffectsDiffuseFilter: true, outputBuffer: prevBuf,
                            tracker: scratch.PreviewTracker);
                        previewDirty = scratch.PreviewTracker.LastDirty;
                    }
                    else
                    {
                        previewRgba = rgba;
                        previewDirty = scratch.RgbaTracker.LastDirty;
                    }

                    // 3D editor consumes compositeResults — feed it the preview-with-all-layers version.
                    // Dirty rect accompanies the buffer so ModelEditorWindow only uploads the changed region.
                    if (previewRgba != null)
                    {
                        compositeResults[job.DiffuseGamePath] =
                            (previewRgba, baseTex.Width, baseTex.Height, previewDirty);
                        Interlocked.Increment(ref compositeVersion);
                    }

                    // GPU/file path uses the filtered version. Swizzle only the dirty region —
                    // outside dirty the bgraBuf still holds last cycle's correctly-swizzled bytes
                    // (since rgbaBuf outside dirty is unchanged, so is the swizzled mirror).
                    if (rgba != null)
                    {
                        var bgraBuf = EnsureBuf(ref scratch.Bgra, byteCount);
                        var diffuseDirty = scratch.RgbaTracker.LastDirty;
                        TextureSwapService.RgbaToBgraRegion(rgba, bgraBuf,
                            baseTex.Width, baseTex.Height, diffuseDirty);
                        if (flushFiles && job.DiskPath != null)
                            WriteBgraTexFile(job.DiskPath, bgraBuf, byteCount, baseTex.Width, baseTex.Height);
                        texEntries.Add(new SwapBatchEntry(
                            job.DiffuseGamePath, job.DiskPath, bgraBuf, baseTex.Width, baseTex.Height));
                    }

                    // v1 PBR: layers with allocated row pairs drive the index-map + ColorTable path
                    var allocatedLayers = layers.Where(l => l.AllocatedRowPair >= 0 && l.IsVisible).ToList();
                    bool hasPbrLayers = allocatedLayers.Count > 0;
                    bool ctQueued = false;
                    bool indexQueued = false;

                    // Index map: rewrite R = rowPair*17, G = weight*255 (per Penumbra
                    // MaterialExporter:136-137). Vanilla bytes are read from the staged
                    // disk path that ProcessGroup populated, then cloned + modified.
                    if (hasPbrLayers && !string.IsNullOrEmpty(job.IndexGamePath) && !string.IsNullOrEmpty(job.IndexDiskPath))
                    {
                        var idxRgbaBuf = EnsureBuf(ref scratch.IdxRgba, byteCount);
                        var idxRgba = CompositeIndexMap(allocatedLayers, job.IndexDiskPath!,
                            baseTex.Width, baseTex.Height, outputBuffer: idxRgbaBuf);
                        if (idxRgba != null)
                        {
                            var idxBgraBuf = EnsureBuf(ref scratch.IdxBgra, byteCount);
                            TextureSwapService.RgbaToBgra(idxRgba, idxBgraBuf, byteCount);
                            if (flushFiles)
                                WriteBgraTexFile(job.IndexDiskPath!, idxBgraBuf, byteCount, baseTex.Width, baseTex.Height);
                            texEntries.Add(new SwapBatchEntry(
                                job.IndexGamePath!, job.IndexDiskPath, idxBgraBuf, baseTex.Width, baseTex.Height));
                            indexQueued = true;
                        }
                    }

                    if (hasPbrLayers && !string.IsNullOrEmpty(job.MtrlGamePath)
                        && vanillaColorTables.TryGetValue(job.MtrlGamePath!, out var vanilla)
                        && ColorTableBuilder.IsDawntrailLayout(vanilla.Width, vanilla.Height))
                    {
                        var modified = ColorTableBuilder.Build(vanilla.Data, vanilla.Width, vanilla.Height, allocatedLayers);
                        var mtrlDisk = previewMtrlDiskPaths.GetValueOrDefault(job.MtrlGamePath!);
                        ctEntries.Add(new ColorTableEntry(
                            job.MtrlGamePath!, mtrlDisk, modified, vanilla.Width, vanilla.Height));
                        // Surface the modified table to the PBR inspector.
                        lastBuiltColorTables[job.MtrlGamePath!] = (modified, vanilla.Width, vanilla.Height);
                        ctQueued = true;
                    }

                    // Per-cycle PBR diagnostic — one line per group, fires only when relevant
                    if (hasPbrLayers)
                    {
                        bool vanillaCached = !string.IsNullOrEmpty(job.MtrlGamePath)
                            && vanillaColorTables.ContainsKey(job.MtrlGamePath!);
                        DebugServer.AppendLog(
                            $"[PBR] Cycle {job.Group.Name}: alloc={allocatedLayers.Count} " +
                            $"index={indexQueued} ct={ctQueued} vanillaCached={vanillaCached}");
                    }

                    // Legacy emissive fallback path: only fires when no ColorTable entry was queued
                    // (skin.shpk-class materials, or non-Dawntrail layouts).
                    if (!ctQueued && HasEmissiveLayers(job.Layers) && !string.IsNullOrEmpty(job.NormDiskPath))
                    {
                        var normRgbaBuf = EnsureBuf(ref scratch.NormRgba, byteCount);
                        var normRgba = CompositeEmissiveNorm(
                            layers, job.NormDiskPath!, baseTex.Width, baseTex.Height,
                            outputBuffer: normRgbaBuf);
                        if (normRgba != null)
                        {
                            var normBgraBuf = EnsureBuf(ref scratch.NormBgra, byteCount);
                            TextureSwapService.RgbaToBgra(normRgba, normBgraBuf, byteCount);
                            previewDiskPaths.TryGetValue(job.NormGamePath ?? "", out var normDiskOut);
                            if (flushFiles && normDiskOut != null)
                                WriteBgraTexFile(normDiskOut, normBgraBuf, byteCount, baseTex.Width, baseTex.Height);
                            texEntries.Add(new SwapBatchEntry(
                                job.NormGamePath!, normDiskOut, normBgraBuf, baseTex.Width, baseTex.Height));
                        }

                        if (!string.IsNullOrEmpty(job.MtrlGamePath))
                        {
                            var mtrlDisk = previewMtrlDiskPaths.GetValueOrDefault(job.MtrlGamePath!);
                            emissiveOffsets.TryGetValue(job.MtrlGamePath!, out var emOff);
                            emEntries.Add(new EmissiveEntry(job.MtrlGamePath!, mtrlDisk, job.EmissiveColor, emOff));
                        }
                    }
                }

                if (!token.IsCancellationRequested)
                    pendingBatch = new SwapBatch(texEntries, emEntries, ctEntries);
            }
            catch (Exception ex)
            {
                DebugServer.AppendLog($"[PreviewService] Async composite exception: {ex.Message}");
            }
            finally
            {
                composeLock.Release();
            }
        }, token);
    }

    // Snapshot of layer parameters for thread-safe background compositing
    private class LayerSnapshot
    {
        public LayerKind Kind;
        public string? Name;
        public bool IsVisible;
        public bool AffectsDiffuse, AffectsSpecular, AffectsEmissive, AffectsRoughness, AffectsMetalness, AffectsSheen;
        public string? ImagePath;
        public Vector2 UvCenter, UvScale;
        public float RotationDeg, Opacity;
        public BlendMode BlendMode;
        public ClipMode Clip;
        public LayerFadeMask FadeMask;
        public float FadeMaskFalloff;
        public Vector3 DiffuseColor, SpecularColor, EmissiveColor;
        public float EmissiveIntensity;
        public float Roughness, Metalness, SheenRate, SheenTint, SheenAperture;
        public float GradientAngleDeg;
        public float GradientScale;
        public float GradientOffset;
        public int AllocatedRowPair;

        public LayerSnapshot(DecalLayer l)
        {
            Kind = l.Kind; Name = l.Name; IsVisible = l.IsVisible;
            AffectsDiffuse = l.AffectsDiffuse; AffectsSpecular = l.AffectsSpecular; AffectsEmissive = l.AffectsEmissive;
            AffectsRoughness = l.AffectsRoughness; AffectsMetalness = l.AffectsMetalness; AffectsSheen = l.AffectsSheen;
            ImagePath = l.ImagePath; UvCenter = l.UvCenter; UvScale = l.UvScale;
            RotationDeg = l.RotationDeg; Opacity = l.Opacity; BlendMode = l.BlendMode;
            Clip = l.Clip;
            FadeMask = l.FadeMask; FadeMaskFalloff = l.FadeMaskFalloff;
            DiffuseColor = l.DiffuseColor; SpecularColor = l.SpecularColor;
            EmissiveColor = l.EmissiveColor; EmissiveIntensity = l.EmissiveIntensity;
            Roughness = l.Roughness; Metalness = l.Metalness;
            SheenRate = l.SheenRate; SheenTint = l.SheenTint; SheenAperture = l.SheenAperture;
            GradientAngleDeg = l.GradientAngleDeg; GradientScale = l.GradientScale;
            GradientOffset = l.GradientOffset;
            AllocatedRowPair = l.AllocatedRowPair;
        }

        public DecalLayer ToDecalLayer() => new()
        {
            Kind = Kind, Name = Name ?? "", IsVisible = IsVisible,
            AffectsDiffuse = AffectsDiffuse, AffectsSpecular = AffectsSpecular, AffectsEmissive = AffectsEmissive,
            AffectsRoughness = AffectsRoughness, AffectsMetalness = AffectsMetalness, AffectsSheen = AffectsSheen,
            ImagePath = ImagePath, UvCenter = UvCenter, UvScale = UvScale,
            RotationDeg = RotationDeg, Opacity = Opacity, BlendMode = BlendMode,
            Clip = Clip,
            FadeMask = FadeMask, FadeMaskFalloff = FadeMaskFalloff,
            DiffuseColor = DiffuseColor, SpecularColor = SpecularColor,
            EmissiveColor = EmissiveColor, EmissiveIntensity = EmissiveIntensity,
            Roughness = Roughness, Metalness = Metalness,
            SheenRate = SheenRate, SheenTint = SheenTint, SheenAperture = SheenAperture,
            GradientAngleDeg = GradientAngleDeg, GradientScale = GradientScale,
            GradientOffset = GradientOffset,
            AllocatedRowPair = AllocatedRowPair,
        };
    }

    private static bool HasEmissiveLayers(List<LayerSnapshot> layers)
    {
        foreach (var l in layers)
            if (l.IsVisible && l.AffectsEmissive && !string.IsNullOrEmpty(l.ImagePath))
                return true;
        return false;
    }

    /// <summary>
    /// v1 PBR: cache vanilla ColorTable from GPU for each managed target group.
    /// Idempotent — only fires for groups not yet cached. Must be called on the main thread.
    /// Path matching: uses the redirected staged disk path (post-Penumbra) since the live
    /// material's FileName contains the staged path, not the original.
    /// </summary>
    private unsafe void TryCacheVanillaColorTables(DecalProject project)
    {
        var charBase = textureSwap?.GetLocalPlayerCharacterBase();
        if (charBase == null) return;

        foreach (var group in project.Groups)
        {
            if (string.IsNullOrEmpty(group.MtrlGamePath)) continue;
            if (vanillaColorTables.ContainsKey(group.MtrlGamePath!)) continue;

            // After Penumbra redirect the live mtrl FileName contains the staged disk path,
            // not the original. Match by the staged path (falling back to orig for first-time
            // attempts before any redirect happened).
            var matchDisk = previewMtrlDiskPaths.GetValueOrDefault(group.MtrlGamePath!)
                            ?? group.OrigMtrlDiskPath
                            ?? group.MtrlDiskPath;

            if (textureSwap!.TryGetVanillaColorTable(
                    charBase, group.MtrlGamePath!, matchDisk,
                    out var ctData, out var ctW, out var ctH))
            {
                vanillaColorTables[group.MtrlGamePath!] = (ctData, ctW, ctH);
                DebugServer.AppendLog(
                    $"[PBR] Cached vanilla ColorTable {ctW}x{ctH} for {group.MtrlGamePath}");
            }
        }
    }

    /// <summary>Get (or lazily create) the row pair allocator for a target group.</summary>
    public RowPairAllocator GetOrCreateAllocator(TargetGroup group)
    {
        var key = group.MtrlGamePath ?? group.Name;
        return rowPairAllocators.GetOrAdd(key, _ => new RowPairAllocator());
    }

    // ── PBR Inspector accessors ──────────────────────────────────────────────

    /// <summary>Vanilla ColorTable bytes for a group's mtrl, or null if not yet cached.</summary>
    public (Half[] Data, int Width, int Height)? GetVanillaColorTable(TargetGroup group)
    {
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return null;
        return vanillaColorTables.TryGetValue(group.MtrlGamePath!, out var v) ? v : null;
    }

    /// <summary>Most recently built (modified) ColorTable for a group, or null if no PBR layers active.</summary>
    public (Half[] Data, int Width, int Height)? GetLastBuiltColorTable(TargetGroup group)
    {
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return null;
        return lastBuiltColorTables.TryGetValue(group.MtrlGamePath!, out var v) ? v : null;
    }

    /// <summary>Staged disk path for a redirected texture, or null if not initialized.</summary>
    public string? GetStagedDiskPath(string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath)) return null;
        return previewDiskPaths.TryGetValue(gamePath!, out var disk) ? disk : null;
    }

    /// <summary>
    /// Resolve the g_SamplerIndex (0x565F8FD8) texture's game path from a group's mtrl,
    /// caching the result. Returns null if mtrl is unreadable, the sampler is missing,
    /// or its texture index is out of range.
    /// Negative results are cached as <see cref="string.Empty"/> so we don't re-parse the
    /// mtrl every frame on materials that simply don't have an index sampler (skin.shpk).
    /// </summary>
    public string? GetIndexMapGamePath(TargetGroup group)
    {
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return null;
        if (indexMapGamePaths.TryGetValue(group.MtrlGamePath!, out var cached))
            return string.IsNullOrEmpty(cached) ? null : cached;

        try
        {
            // Load mtrl bytes via disk first, then SqPack fallback
            var mtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;
            byte[]? mtrlBytes = null;
            if (!string.IsNullOrEmpty(mtrlDisk) && File.Exists(mtrlDisk))
                mtrlBytes = File.ReadAllBytes(mtrlDisk);
            else
            {
                var pack = meshExtractor.GetSqPackInstance();
                var sqResult = pack?.GetFile(group.MtrlGamePath!);
                if (sqResult != null) mtrlBytes = sqResult.Value.file.RawData.ToArray();
            }
            if (mtrlBytes == null) return null;

            // Parse via Lumina by writing to a temp file (Lumina API limitation)
            var tempPath = Path.Combine(outputDir, $"temp_idx_{Guid.NewGuid():N}.mtrl");
            File.WriteAllBytes(tempPath, mtrlBytes);
            var lumina = meshExtractor.GetLuminaForDisk();
            var mtrl = lumina!.GetFileFromDisk<MtrlFile>(tempPath);
            try { File.Delete(tempPath); } catch { }

            // Find sampler 0x565F8FD8 → texture index → string offset → null-terminated path
            int texIndex = -1;
            foreach (var s in mtrl.Samplers)
            {
                if (s.SamplerId == IndexSamplerId) { texIndex = s.TextureIndex; break; }
            }
            if (texIndex < 0 || texIndex >= mtrl.TextureOffsets.Length)
            {
                // Cache the negative result so we don't re-parse the mtrl every frame.
                indexMapGamePaths[group.MtrlGamePath!] = string.Empty;
                DebugServer.AppendLog($"[PBR] No g_SamplerIndex (0x565F8FD8) in mtrl {group.MtrlGamePath} (cached)");
                return null;
            }

            int strOffset = mtrl.TextureOffsets[texIndex].Offset;
            int end = strOffset;
            while (end < mtrl.Strings.Length && mtrl.Strings[end] != 0) end++;
            var indexGamePath = System.Text.Encoding.UTF8.GetString(mtrl.Strings, strOffset, end - strOffset);
            if (string.IsNullOrEmpty(indexGamePath))
            {
                indexMapGamePaths[group.MtrlGamePath!] = string.Empty;
                return null;
            }

            indexMapGamePaths[group.MtrlGamePath!] = indexGamePath;
            DebugServer.AppendLog($"[PBR] Resolved index map for {group.Name}: {indexGamePath}");
            return indexGamePath;
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PBR] GetIndexMapGamePath failed for {group.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Diagnostic snapshot for HTTP /api/debug/pbr — returns one entry per managed group
    /// describing allocator state and ColorTable cache hit/miss.
    /// </summary>
    public List<object> GetPbrDiagnostics(DecalProject project)
    {
        var result = new List<object>();
        foreach (var group in project.Groups)
        {
            var alloc = GetOrCreateAllocator(group);
            bool ctCached = !string.IsNullOrEmpty(group.MtrlGamePath)
                && vanillaColorTables.ContainsKey(group.MtrlGamePath!);
            (int W, int H) ctSize = (0, 0);
            if (ctCached && vanillaColorTables.TryGetValue(group.MtrlGamePath!, out var v))
                ctSize = (v.Width, v.Height);

            var layerStates = new List<object>();
            foreach (var l in group.Layers)
            {
                layerStates.Add(new
                {
                    name = l.Name,
                    kind = l.Kind.ToString(),
                    visible = l.IsVisible,
                    allocatedRowPair = l.AllocatedRowPair,
                    requiresRowPair = l.RequiresRowPair,
                    affectsDiffuse = l.AffectsDiffuse,
                    affectsSpecular = l.AffectsSpecular,
                    affectsEmissive = l.AffectsEmissive,
                    affectsRoughness = l.AffectsRoughness,
                    affectsMetalness = l.AffectsMetalness,
                    affectsSheen = l.AffectsSheen,
                });
            }

            result.Add(new
            {
                groupName = group.Name,
                mtrlGamePath = group.MtrlGamePath,
                hasPbrLayers = group.HasPbrLayers(),
                hasEmissiveLayers = group.HasEmissiveLayers(),
                allocator = new
                {
                    scanned = alloc.Scanned,
                    vanillaOccupied = alloc.VanillaOccupiedCount,
                    available = alloc.AvailableSlots,
                },
                vanillaColorTable = new
                {
                    cached = ctCached,
                    width = ctSize.W,
                    height = ctSize.H,
                    isDawntrail = ctCached && ColorTableBuilder.IsDawntrailLayout(ctSize.W, ctSize.H),
                },
                layers = layerStates,
            });
        }
        return result;
    }

    /// <summary>
    /// Whether this group's material supports the v1 PBR pipeline (has g_SamplerIndex
    /// and ColorTable). False on skin.shpk class materials. Result is cached.
    /// </summary>
    public bool MaterialSupportsPbr(TargetGroup group)
    {
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return false;
        return !string.IsNullOrEmpty(GetIndexMapGamePath(group));
    }

    /// <summary>Reason codes for a failed row pair allocation, surfaced to the UI for toasting.</summary>
    public enum RowPairAllocFailure { None, Unsupported, Exhausted }

    /// <summary>
    /// Called from UI when a layer needs a row pair (first Affects* toggled on).
    /// Returns true on success; false on failure with the reason in <paramref name="failure"/>.
    /// Lazy-scans vanilla index map histogram on first allocation per group, so we never
    /// hand out a slot that vanilla is already using.
    /// </summary>
    public bool TryAllocateRowPairForLayer(TargetGroup group, DecalLayer layer, out RowPairAllocFailure failure)
    {
        failure = RowPairAllocFailure.None;
        if (layer.AllocatedRowPair >= 0) return true;   // already has one

        // Reject up front if the material has no g_SamplerIndex / ColorTable (skin.shpk).
        // PBR row-pair fields are physically meaningless on those materials.
        if (!MaterialSupportsPbr(group))
        {
            failure = RowPairAllocFailure.Unsupported;
            DebugServer.AppendLog(
                $"[PBR] Material does not support PBR row pairs: {group.MtrlGamePath} (no g_SamplerIndex)");
            return false;
        }

        var alloc = GetOrCreateAllocator(group);

        // Critical: scan vanilla BEFORE allocating, otherwise we may hand out a row pair
        // that vanilla already uses, causing layer PBR to bleed into vanilla regions.
        EnsureVanillaScan(group, alloc);

        var slot = alloc.TryAllocate();
        if (slot == null)
        {
            failure = RowPairAllocFailure.Exhausted;
            DebugServer.AppendLog(
                $"[PBR] Row pair exhausted for {group.Name} " +
                $"(avail={alloc.AvailableSlots}, vanilla={alloc.VanillaOccupiedCount})");
            return false;
        }
        layer.AllocatedRowPair = slot.Value;
        DebugServer.AppendLog($"[PBR] Allocated row pair {slot.Value} to '{layer.Name}'");
        return true;
    }

    /// <summary>
    /// Vanilla scan: read the index map (g_SamplerIndex) and feed its R channel to
    /// the row pair allocator so we never hand out a slot vanilla is already using.
    /// Loads via SqPack — no Penumbra redirect needed, no GPU access required.
    /// Idempotent.
    /// </summary>
    private void EnsureVanillaScan(TargetGroup group, RowPairAllocator alloc)
    {
        if (alloc.Scanned) return;

        var indexGamePath = GetIndexMapGamePath(group);
        if (string.IsNullOrEmpty(indexGamePath))
        {
            DebugServer.AppendLog($"[PBR] Vanilla scan skipped for {group.Name}: no index map sampler");
            return;
        }

        var indexImg = LoadGameTexture(indexGamePath);
        if (indexImg == null)
        {
            DebugServer.AppendLog($"[PBR] Vanilla scan failed for {group.Name}: cannot load index map {indexGamePath}");
            return;
        }

        var (indexData, w, h) = indexImg.Value;
        alloc.ScanVanillaOccupation(indexData, w, h);
        DebugServer.AppendLog(
            $"[PBR] Vanilla scan {group.Name}: vanillaOccupied={alloc.VanillaOccupiedCount}, available={alloc.AvailableSlots}");
    }

    /// <summary>
    /// Release this layer's row pair if it no longer requires one.
    /// Call after toggling off Affects* fields or before deleting a layer.
    /// </summary>
    public void ReleaseRowPairIfUnused(TargetGroup group, DecalLayer layer)
    {
        if (layer.AllocatedRowPair < 0) return;
        if (layer.RequiresRowPair) return;  // still needed

        var alloc = GetOrCreateAllocator(group);
        alloc.Release(layer.AllocatedRowPair);
        DebugServer.AppendLog($"[PreviewService] Released row pair {layer.AllocatedRowPair} from layer '{layer.Name}'");
        layer.AllocatedRowPair = -1;
    }

    /// <summary>Force-release this layer's row pair (e.g. on layer deletion).</summary>
    public void ForceReleaseRowPair(TargetGroup group, DecalLayer layer)
    {
        if (layer.AllocatedRowPair < 0) return;
        var alloc = GetOrCreateAllocator(group);
        alloc.Release(layer.AllocatedRowPair);
        layer.AllocatedRowPair = -1;
    }

    /// <summary>Clear emissive hook targets (e.g. when emissive is disabled).</summary>
    public void ClearEmissiveHookTargets() => emissiveHook?.ClearTargets();

    /// <summary>
    /// Invalidate a single group's emissive state without nuking row-pair allocators or
    /// PBR caches. Removes the mtrl/norm redirects so the next UpdatePreview falls back
    /// to a Full Redraw that re-binds vanilla files, clearing the CBuffer hook target so
    /// the patched <c>g_EmissiveColor</c> stops being applied each frame.
    /// Use this when a layer's emissive is toggled off.
    /// </summary>
    public void InvalidateEmissiveForGroup(TargetGroup group)
    {
        // Always clear hook targets — cheap, idempotent, and prevents the
        // last-written color from continuing to glow after the user disables emissive.
        emissiveHook?.ClearTargets();

        if (string.IsNullOrEmpty(group.MtrlGamePath)) return;

        // Drop the mtrl/norm initialisation so CheckCanSwapInPlace returns false next
        // cycle and we go through Full Redraw, which rewrites vanilla mtrl + vanilla norm.
        if (!string.IsNullOrEmpty(group.MtrlGamePath))
        {
            initializedRedirects.TryRemove(group.MtrlGamePath!, out _);
            previewDiskPaths.TryRemove(group.MtrlGamePath!, out _);
            previewMtrlDiskPaths.TryRemove(group.MtrlGamePath!, out _);
            emissiveOffsets.TryRemove(group.MtrlGamePath!, out _);
        }
        if (!string.IsNullOrEmpty(group.NormGamePath))
        {
            initializedRedirects.TryRemove(group.NormGamePath!, out _);
            previewDiskPaths.TryRemove(group.NormGamePath!, out _);
        }
        // Force CanSwap recompute next frame
        CanSwapInPlace = false;
        DebugServer.AppendLog($"[PreviewService] Invalidated emissive state for {group.Name}");
    }

    /// <summary>Clear GPU swap state, forcing next update to do full redraw.</summary>
    public void ResetSwapState()
    {
        asyncCancel?.Cancel();
        pendingBatch = null;
        initializedRedirects.Clear();
        previewDiskPaths.Clear();
        emissiveOffsets.Clear();
        previewMtrlDiskPaths.Clear();
        rowPairAllocators.Clear();
        vanillaColorTables.Clear();
        lastBuiltColorTables.Clear();
        indexMapGamePaths.Clear();
        lastAppliedEmissive.Clear();
        lastGameSwapUtc = DateTime.MinValue;
        pendingIdleFlush = false;
        activeProject = null;
        emissiveHook?.ClearTargets();
        CanSwapInPlace = false;
        LastUpdateMode = "none";
        DebugServer.AppendLog("[PreviewService] GPU swap state reset");
    }

    /// <summary>
    /// Composite a group's textures into the given staging directory using
    /// game-path mirrored layout, only including visible layers. Does NOT mutate
    /// any PreviewService runtime state (GPU swap, caches, hook targets).
    /// Returns gamePath → relative disk path (forward slashes) for default_mod.json.
    /// </summary>
    internal Dictionary<string, string> CompositeForExport(TargetGroup group, string stagingDir)
    {
        var redirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(group.DiffuseGamePath))
            return redirects;

        // Build a temporary list of visible layers (skip hidden / null-image)
        var visibleLayers = new List<DecalLayer>();
        foreach (var l in group.Layers)
        {
            if (!l.IsVisible || string.IsNullOrEmpty(l.ImagePath)) continue;
            visibleLayers.Add(l);
        }
        if (visibleLayers.Count == 0)
            return redirects;

        var baseTex = LoadBaseTexture(group);
        int w = baseTex.Width, h = baseTex.Height;

        // Diffuse composite — write to staging/<gamePath>
        var diffResult = CpuUvComposite(visibleLayers, baseTex.Data, w, h);
        if (diffResult != null)
        {
            var diffOut = WriteStagingTex(stagingDir, group.DiffuseGamePath!, diffResult, w, h);
            redirects[group.DiffuseGamePath!] = diffOut;
            DebugServer.AppendLog($"[ModExport] Diffuse → {group.DiffuseGamePath}");
        }

        // Emissive: only if there are visible emissive layers + mtrl path is known
        bool hasEmissive = false;
        foreach (var l in visibleLayers)
            if (l.AffectsEmissive) { hasEmissive = true; break; }

        if (hasEmissive && !string.IsNullOrEmpty(group.MtrlGamePath))
        {
            var emissiveColor = GetCombinedEmissiveColor(visibleLayers);
            var mtrlSource = group.OrigMtrlDiskPath ?? group.MtrlDiskPath ?? group.MtrlGamePath!;
            var mtrlOut = StagingPathFor(stagingDir, group.MtrlGamePath!);
            Directory.CreateDirectory(Path.GetDirectoryName(mtrlOut)!);
            if (TryBuildEmissiveMtrl(mtrlSource, mtrlOut, emissiveColor, out _))
            {
                redirects[group.MtrlGamePath!] = ToForwardSlash(group.MtrlGamePath!);
                DebugServer.AppendLog($"[ModExport] Mtrl → {group.MtrlGamePath}");
            }

            // Emissive normal map (alpha mask)
            if (!string.IsNullOrEmpty(group.NormGamePath) && !string.IsNullOrEmpty(group.NormDiskPath))
            {
                var normSource = group.OrigNormDiskPath ?? group.NormDiskPath!;
                var normResult = CompositeEmissiveNorm(visibleLayers, normSource, w, h);
                if (normResult != null)
                {
                    var normOut = WriteStagingTex(stagingDir, group.NormGamePath!, normResult, w, h);
                    redirects[group.NormGamePath!] = normOut;
                    DebugServer.AppendLog($"[ModExport] Norm (emissive alpha) → {group.NormGamePath}");
                }
            }
        }

        return redirects;
    }

    /// <summary>Map a game path to a staging-rooted disk path (mirrors game tree).</summary>
    private static string StagingPathFor(string stagingDir, string gamePath)
        => Path.Combine(stagingDir, gamePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Convert any path to forward-slash form for default_mod.json.</summary>
    private static string ToForwardSlash(string p) => p.Replace('\\', '/');

    /// <summary>Write a composited RGBA buffer as .tex into staging at the game-path mirror.</summary>
    private string WriteStagingTex(string stagingDir, string gamePath, byte[] rgba, int w, int h)
    {
        var diskPath = StagingPathFor(stagingDir, gamePath);
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
        WriteBgraTexFile(diskPath, rgba, w, h);
        return ToForwardSlash(gamePath);
    }

    // ── Private: check if all groups can swap in-place ───────────────────────

    // One-shot CanSwap=NO log gating — avoid spamming the log on every poll
    private string? lastCanSwapDenyReason;

    private void LogCanSwapDeny(string reason)
    {
        if (lastCanSwapDenyReason != reason)
        {
            DebugServer.AppendLog($"[PBR] CanSwap=NO {reason}");
            lastCanSwapDenyReason = reason;
        }
    }

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

            if (!initializedRedirects.ContainsKey(group.DiffuseGamePath))
            {
                LogCanSwapDeny($"diffuse not initialized: {group.DiffuseGamePath}");
                CanSwapInPlace = false;
                return false;
            }

            if (group.HasEmissiveLayers() || group.HasPbrLayers())
            {
                if (!string.IsNullOrEmpty(group.MtrlGamePath)
                    && !previewDiskPaths.ContainsKey(group.MtrlGamePath))
                {
                    LogCanSwapDeny($"PBR/emissive needs mtrl init: {group.MtrlGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }

                if (!string.IsNullOrEmpty(group.NormGamePath)
                    && previewDiskPaths.ContainsKey(group.NormGamePath)
                    && !initializedRedirects.ContainsKey(group.NormGamePath))
                {
                    LogCanSwapDeny($"norm not initialized: {group.NormGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }

                if (!string.IsNullOrEmpty(group.MtrlGamePath)
                    && previewDiskPaths.ContainsKey(group.MtrlGamePath)
                    && !initializedRedirects.ContainsKey(group.MtrlGamePath))
                {
                    LogCanSwapDeny($"mtrl not initialized: {group.MtrlGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }
            }

            // v1 PBR (character.shpk path): index map must be mounted before inplace can fire
            if (group.HasPbrLayers())
            {
                var indexGamePath = GetIndexMapGamePath(group);
                if (!string.IsNullOrEmpty(indexGamePath)
                    && !previewDiskPaths.ContainsKey(indexGamePath))
                {
                    LogCanSwapDeny($"PBR needs index map init: {indexGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }
            }
        }

        if (lastCanSwapDenyReason != null)
        {
            DebugServer.AppendLog("[PBR] CanSwap=YES");
            lastCanSwapDenyReason = null;
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

        // Don't cache the empty fallback — next call should retry the load
        int r = config.TextureResolution;
        return (new byte[r * r * 4], r, r);
    }

    private void ProcessGroup(TargetGroup group, Dictionary<string, string> redirects)
    {
        var baseTex = LoadBaseTexture(group);
        int w = baseTex.Width, h = baseTex.Height;

        // Diffuse composite
        // GPU diffuse — only paints layers with AffectsDiffuse=true
        var diffResult = CpuUvComposite(group.Layers, baseTex.Data, w, h);

        // 3D editor preview diffuse — paints ALL visible layers with images so the user
        // sees decal placement even when AffectsDiffuse is off (placement guide).
        // Skip the second pass when no layer is "preview-only" (perf optimization).
        bool hasPreviewOnly = false;
        foreach (var l in group.Layers)
        {
            if (l.IsVisible && !string.IsNullOrEmpty(l.ImagePath) && !l.AffectsDiffuse)
            { hasPreviewOnly = true; break; }
        }
        var diffPreview = hasPreviewOnly
            ? CpuUvComposite(group.Layers, baseTex.Data, w, h, ignoreAffectsDiffuseFilter: true)
            : diffResult;

        // PBR-only or WholeMaterial-only groups produce no diffuse delta but still need the
        // material mounted for inplace ColorTable swap. Synthesize a passthrough by cloning
        // vanilla diffuse so the redirect pipeline can engage.
        if (diffResult == null && group.HasPbrLayers())
        {
            diffResult = (byte[])baseTex.Data.Clone();
            DebugServer.AppendLog($"[PBR] Passthrough diffuse for PBR-only group {group.Name}");
        }
        if (diffPreview == null && group.HasPbrLayers())
            diffPreview = (byte[])baseTex.Data.Clone();

        // 3D editor reads compositeResults — feed it the preview version.
        // Full Redraw always writes the whole texture, so dirty = full rect.
        if (diffPreview != null)
        {
            compositeResults[group.DiffuseGamePath!] = (diffPreview, w, h, DirtyRect.Full(w, h));
            Interlocked.Increment(ref compositeVersion);
            // Also mark the async composite's tracker as "full init" so the first inplace
            // cycle after this redraw re-establishes the invariant on its own scratch buffer.
            if (groupScratch.TryGetValue(group.DiffuseGamePath!, out var scratchReset))
            {
                scratchReset.RgbaTracker.Reset();
                scratchReset.PreviewTracker.Reset();
                scratchReset.IdxTracker.Reset();
                scratchReset.NormTracker.Reset();
            }
        }

        // GPU/file path uses the filtered version
        if (diffResult != null)
        {
            var safeName = MakeSafeFileName(group.Name);
            var path = Path.Combine(outputDir, $"preview_{safeName}_d.tex");
            WriteBgraTexFile(path, diffResult, w, h);
            redirects[group.DiffuseGamePath!] = path;
        }

        // Emissive / PBR: modify .mtrl, write emissive into norm.a (skin.shpk legacy path),
        // and mount index map for ColorTable PBR (character.shpk path).
        var hasEmissive = group.HasEmissiveLayers();
        var hasPbr = group.HasPbrLayers();
        if ((hasEmissive || hasPbr) && !string.IsNullOrEmpty(group.MtrlGamePath))
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
            }

            // Legacy emissive path: write emissive into normal map alpha (skin.shpk fallback)
            if (hasEmissive && !string.IsNullOrEmpty(group.NormGamePath) && !string.IsNullOrEmpty(group.NormDiskPath))
            {
                var normDisk = group.OrigNormDiskPath ?? group.NormDiskPath;
                var normResult = CompositeEmissiveNorm(group.Layers, normDisk!, w, h);
                if (normResult != null)
                {
                    var normPath = Path.Combine(outputDir, $"preview_{safeName}_n.tex");
                    WriteBgraTexFile(normPath, normResult, w, h);
                    redirects[group.NormGamePath!] = normPath;
                }
            }

            // v1 PBR (character.shpk path): mount index map (g_SamplerIndex 0x565F8FD8).
            // First Full Redraw writes a passthrough vanilla copy; subsequent inplace updates
            // overwrite it via CompositeIndexMap with row pair + weight in R/G channels.
            if (hasPbr)
            {
                var indexGamePath = GetIndexMapGamePath(group);
                if (!string.IsNullOrEmpty(indexGamePath))
                {
                    var indexImg = LoadGameTexture(indexGamePath);
                    if (indexImg != null)
                    {
                        var (data, iw, ih) = indexImg.Value;
                        var indexClone = (byte[])data.Clone();
                        var indexPath = Path.Combine(outputDir, $"preview_{safeName}_id.tex");
                        WriteBgraTexFile(indexPath, indexClone, iw, ih);
                        redirects[indexGamePath] = indexPath;
                        // Cache vanilla index bytes by the staged disk path so the background
                        // composite can read them back as the baseline for CompositeIndexMap.
                        baseTextureCache[indexPath] = (data, iw, ih);
                        DebugServer.AppendLog($"[PBR] Mounted index map: {indexGamePath} → {indexPath}");
                    }
                    else
                    {
                        DebugServer.AppendLog($"[PBR] Failed to load vanilla index map: {indexGamePath}");
                    }
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
    private byte[]? CompositeEmissiveNorm(List<DecalLayer> layers, string normDiskPath, int w, int h,
        byte[]? outputBuffer = null)
    {
        int needLen = w * h * 4;
        var output = outputBuffer != null && outputBuffer.Length >= needLen
            ? outputBuffer
            : new byte[needLen];

        // Get base normal bytes WITHOUT mutating the cache entry.
        byte[]? cachedBytes = null;
        int cachedW = 0, cachedH = 0;
        if (baseTextureCache.TryGetValue(normDiskPath, out var cachedNorm))
        {
            (cachedBytes, cachedW, cachedH) = cachedNorm;
        }
        else
        {
            var normImg = File.Exists(normDiskPath) ? imageLoader.LoadImage(normDiskPath) : LoadGameTexture(normDiskPath);
            if (normImg != null)
            {
                baseTextureCache[normDiskPath] = normImg.Value;
                (cachedBytes, cachedW, cachedH) = normImg.Value;
            }
        }

        if (cachedBytes == null)
        {
            Array.Clear(output, 0, needLen);
        }
        else if (cachedW != w || cachedH != h)
        {
            var resized = ResizeBilinear(cachedBytes, cachedW, cachedH, w, h);
            Buffer.BlockCopy(resized, 0, output, 0, needLen);
        }
        else
        {
            Buffer.BlockCopy(cachedBytes, 0, output, 0, needLen);
        }

        // Zero out alpha channel everywhere — bound by needLen, not output.Length, since
        // outputBuffer may be a reused over-sized scratch buffer.
        for (int i = 3; i < needLen; i += 4)
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

            // Per-row parallel — each row writes disjoint output bytes (alpha channel only).
            // The Math.Max read-modify-write is per-pixel and rows don't overlap.
            var layerLocal = layer;
            ParallelRows(pyMin, pyMax, py =>
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
                    switch (layerLocal.Clip)
                    {
                        case ClipMode.ClipLeft when ru < 0f: continue;
                        case ClipMode.ClipRight when ru >= 0f: continue;
                        case ClipMode.ClipTop when rv < 0f: continue;
                        case ClipMode.ClipBottom when rv >= 0f: continue;
                    }

                    float du = (ru + 0.5f) * decalW - 0.5f;
                    float dv = (rv + 0.5f) * decalH - 0.5f;
                    SampleBilinear(decalData, decalW, decalH, du, dv, out _, out _, out _, out float da);
                    da *= opacity;
                    if (da < 0.001f) continue;

                    float maskValue;
                    if (layerLocal.FadeMask == LayerFadeMask.DirectionalGradient)
                        maskValue = ComputeDirectionalGradient(ru, rv, da,
                            layerLocal.GradientAngleDeg, layerLocal.GradientScale, layerLocal.FadeMaskFalloff, layerLocal.GradientOffset);
                    else if (layerLocal.FadeMask == LayerFadeMask.ShapeOutline)
                    {
                        // Sample 8 neighbors at 1-pixel offset to detect alpha edges
                        float sum = 0; int cnt = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            float ndu = du + dx; float ndv = dv + dy;
                            SampleBilinear(decalData, decalW, decalH, ndu, ndv, out _, out _, out _, out float na);
                            sum += na * opacity; cnt++;
                        }
                        maskValue = ComputeShapeOutline(da, layerLocal.FadeMaskFalloff, sum / cnt);
                    }
                    else
                        maskValue = ComputeFadeMaskWeight(layerLocal.FadeMask, layerLocal.FadeMaskFalloff, ru, rv, da);

                    int oIdx = (py * w + px) * 4;
                    byte emByte = (byte)Math.Clamp((int)(maskValue * 255), 0, 255);
                    output[oIdx + 3] = (byte)Math.Max(output[oIdx + 3], emByte);
                }
            });

            anyEmissive = true;
        }

        return anyEmissive ? output : null;
    }

    /// <summary>
    /// v1 PBR index map rewrite: per Penumbra MaterialExporter:136-137, character.shpk
    /// shaders read `tablePair = round(g_SamplerIndex.r / 17)` and `rowBlend = 1 - g/255`,
    /// then `lerp(table[tablePair*2], table[tablePair*2+1], rowBlend)`. We write:
    ///   index.R = rowPair * 17  (selects which row pair this pixel uses)
    ///   index.G = weight * 255  (weight=1 ⇒ G=255 ⇒ rowBlend=0 ⇒ reads layer override row)
    /// Vanilla B and A are preserved.
    /// </summary>
    private byte[]? CompositeIndexMap(List<DecalLayer> allocatedLayers, string indexDiskOrGamePath, int w, int h,
        byte[]? outputBuffer = null)
    {
        // Get base index bytes — must NOT mutate the cached entry. When sizes match we
        // skip the clone and copy straight into outputBuffer.
        byte[] cachedBytes;
        int cachedW, cachedH;
        if (baseTextureCache.TryGetValue(indexDiskOrGamePath, out var cachedIdx))
        {
            (cachedBytes, cachedW, cachedH) = cachedIdx;
        }
        else
        {
            var indexImg = File.Exists(indexDiskOrGamePath)
                ? imageLoader.LoadImage(indexDiskOrGamePath)
                : LoadGameTexture(indexDiskOrGamePath);
            if (indexImg == null) return null;
            baseTextureCache[indexDiskOrGamePath] = indexImg.Value;
            (cachedBytes, cachedW, cachedH) = indexImg.Value;
        }

        int needLen = w * h * 4;
        var output = outputBuffer != null && outputBuffer.Length >= needLen
            ? outputBuffer
            : new byte[needLen];

        if (cachedW != w || cachedH != h)
        {
            // Resize path still allocates inside ResizeBilinear, but that's the rare case
            // where index map and diffuse have different dimensions.
            var resized = ResizeBilinear(cachedBytes, cachedW, cachedH, w, h);
            Buffer.BlockCopy(resized, 0, output, 0, needLen);
        }
        else
        {
            Buffer.BlockCopy(cachedBytes, 0, output, 0, needLen);
        }
        bool anyWritten = false;

        // z-order: iterate layers front-to-back so later layers overwrite earlier ones
        foreach (var layer in allocatedLayers)
        {
            if (!layer.IsVisible) continue;
            if (layer.AllocatedRowPair < 0) continue;
            if (string.IsNullOrEmpty(layer.ImagePath)) continue;

            byte rowPairByte = (byte)Math.Clamp(layer.AllocatedRowPair * 17, 0, 255);
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

            // Per-row parallel — each row writes disjoint output pixels. anyWritten is a
            // bool-set-to-true-only flag so concurrent races are benign.
            var layerLocal = layer;
            var anyWrittenFlag = new int[1];
            ParallelRows(pyMin, pyMax, py =>
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
                    switch (layerLocal.Clip)
                    {
                        case ClipMode.ClipLeft when ru < 0f: continue;
                        case ClipMode.ClipRight when ru >= 0f: continue;
                        case ClipMode.ClipTop when rv < 0f: continue;
                        case ClipMode.ClipBottom when rv >= 0f: continue;
                    }

                    float du = (ru + 0.5f) * decalW - 0.5f;
                    float dv = (rv + 0.5f) * decalH - 0.5f;
                    SampleBilinear(decalData, decalW, decalH, du, dv, out _, out _, out _, out float da);
                    da *= opacity;
                    if (da < 0.001f) continue;

                    float weight;
                    if (layerLocal.FadeMask == LayerFadeMask.DirectionalGradient)
                        weight = ComputeDirectionalGradient(ru, rv, da,
                            layerLocal.GradientAngleDeg, layerLocal.GradientScale, layerLocal.FadeMaskFalloff, layerLocal.GradientOffset);
                    else if (layerLocal.FadeMask == LayerFadeMask.ShapeOutline)
                    {
                        float sum = 0; int cnt = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            float ndu = du + dx; float ndv = dv + dy;
                            SampleBilinear(decalData, decalW, decalH, ndu, ndv, out _, out _, out _, out float na);
                            sum += na * opacity; cnt++;
                        }
                        weight = ComputeShapeOutline(da, layerLocal.FadeMaskFalloff, sum / cnt);
                    }
                    else
                        weight = ComputeFadeMaskWeight(layerLocal.FadeMask, layerLocal.FadeMaskFalloff, ru, rv, da);

                    weight = Math.Clamp(weight, 0f, 1f);
                    if (weight <= 0.001f) continue;

                    int oIdx = (py * w + px) * 4;
                    output[oIdx + 0] = rowPairByte;                                  // .r = row pair * 17 (Penumbra MaterialExporter:136)
                    output[oIdx + 1] = (byte)Math.Clamp((int)(weight * 255), 0, 255); // .g = weight (rowBlend = 1 - g/255, :137)
                    // .b and .a left at vanilla values
                    anyWrittenFlag[0] = 1;
                }
            });
            if (anyWrittenFlag[0] != 0) anyWritten = true;
        }

        return anyWritten ? output : null;
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

            // Unique temp file: avoid races between concurrent main/background callers
            var tempPath = Path.Combine(outputDir, $"temp_{Guid.NewGuid():N}.mtrl");
            File.WriteAllBytes(tempPath, mtrlBytes);
            var lumina = meshExtractor.GetLuminaForDisk();
            mtrl = lumina!.GetFileFromDisk<MtrlFile>(tempPath);
            try { File.Delete(tempPath); } catch { }

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
            // Unique temp file: avoid races between concurrent main/background callers.
            // useCache=false because each call uses a fresh GUID path — caching would
            // never hit and just leak entries forever.
            var tempPath = Path.Combine(outputDir, $"temp_{Guid.NewGuid():N}.tex");
            File.WriteAllBytes(tempPath, rawBytes);
            var result = imageLoader.LoadImage(tempPath, useCache: false);
            try { File.Delete(tempPath); } catch { }
            return result;
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] Game texture load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Composite layers' PNG into a diffuse RGBA buffer.
    /// <paramref name="ignoreAffectsDiffuseFilter"/> = true: paint every visible layer with an
    /// image, regardless of AffectsDiffuse — used for the 3D editor preview where the user
    /// needs to see decal placement even when the layer doesn't actually paint the GPU diffuse.
    /// </summary>
    /// <summary>
    /// Compute the axis-aligned bbox of a decal layer in texture pixel coordinates.
    /// Matches the scan-range derivation inside the paint loops (non-rotated extents,
    /// same as the engine's own tile clamping). Rotation is handled per-pixel inside.
    /// </summary>
    private static DirtyRect ComputeLayerBbox(Vector2 uvCenter, Vector2 uvScale, int w, int h)
    {
        int pxMin = Math.Max(0, (int)((uvCenter.X - uvScale.X / 2f) * w));
        int pxMax = Math.Min(w - 1, (int)((uvCenter.X + uvScale.X / 2f) * w));
        int pyMin = Math.Max(0, (int)((uvCenter.Y - uvScale.Y / 2f) * h));
        int pyMax = Math.Min(h - 1, (int)((uvCenter.Y + uvScale.Y / 2f) * h));
        if (pxMax < pxMin || pyMax < pyMin) return DirtyRect.Empty;
        return new DirtyRect(pxMin, pyMin, pxMax - pxMin + 1, pyMax - pyMin + 1);
    }

    private byte[]? CpuUvComposite(List<DecalLayer> layers, byte[] baseRgba, int w, int h,
        bool ignoreAffectsDiffuseFilter = false, byte[]? outputBuffer = null,
        DirtyTracker? tracker = null)
    {
        // outputBuffer lets the caller pass a per-group reusable buffer; otherwise allocate.
        var output = outputBuffer != null && outputBuffer.Length >= baseRgba.Length
            ? outputBuffer
            : new byte[baseRgba.Length];

        // Collect applicable layers + precompute their bboxes once.
        var applicable = new List<(DecalLayer Layer, DirtyRect Rect)>();
        foreach (var layer in layers)
        {
            if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;
            if (!ignoreAffectsDiffuseFilter && !layer.AffectsDiffuse) continue;

            var rect = ComputeLayerBbox(layer.UvCenter, layer.UvScale, w, h);
            if (rect.IsEmpty) continue;
            applicable.Add((layer, rect));
        }

        // Fast-out: nothing to paint AND no prior paint to clean up
        if (applicable.Count == 0 && (tracker == null || tracker.NeedsFullInit || tracker.LastUnion.IsEmpty))
            return null;

        // Union of this cycle's layer bboxes
        var currentUnion = DirtyRect.Empty;
        foreach (var (_, r) in applicable)
            currentUnion = DirtyRect.Union(currentUnion, r);

        // dirty = current ∪ previous (or full on first init / no tracker)
        DirtyRect dirty = tracker == null
            ? DirtyRect.Full(w, h)
            : tracker.ComputeDirty(currentUnion, w, h);

        // Restore base pixels over the dirty region. Outside dirty, the output already
        // equals base (since previous-cycle paint ⊆ previous_union ⊆ dirty), so no
        // work needed there. This is the core CPU saving: 4096² → dirty W*H bytes.
        if (!dirty.IsEmpty)
        {
            int rowBytes = dirty.W * 4;
            int yEnd = dirty.Y + dirty.H;
            for (int py = dirty.Y; py < yEnd; py++)
            {
                int off = (py * w + dirty.X) * 4;
                Buffer.BlockCopy(baseRgba, off, output, off, rowBytes);
            }
        }

        foreach (var (layer, rect) in applicable)
        {
            var decalImage = imageLoader.LoadImage(layer.ImagePath!);
            if (decalImage == null) continue;

            var (decalData, decalW, decalH) = decalImage.Value;
            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var opacity = layer.Opacity;
            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
            var cosR = MathF.Cos(rotRad);
            var sinR = MathF.Sin(rotRad);

            int pxMin = rect.X;
            int pxMax = rect.X + rect.W - 1;
            int pyMin = rect.Y;
            int pyMax = rect.Y + rect.H - 1;

            // Per-row parallelism — each row writes to non-overlapping output pixels.
            // Layer iteration stays sequential so blend modes still see prior layers.
            // Locals are captured by the lambda; that's fine — they're stable for this layer.
            var layerLocal = layer;
            ParallelRows(pyMin, pyMax, py =>
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
                    switch (layerLocal.Clip)
                    {
                        case ClipMode.ClipLeft when ru < 0f: continue;
                        case ClipMode.ClipRight when ru >= 0f: continue;
                        case ClipMode.ClipTop when rv < 0f: continue;
                        case ClipMode.ClipBottom when rv >= 0f: continue;
                    }

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

                    float rr, rg, rb;
                    switch (layerLocal.BlendMode)
                    {
                        case BlendMode.Multiply:
                            rr = br * dr; rg = bg * dg; rb = bb * db;
                            break;
                        case BlendMode.Screen:
                            rr = 1f - (1f - br) * (1f - dr);
                            rg = 1f - (1f - bg) * (1f - dg);
                            rb = 1f - (1f - bb) * (1f - db);
                            break;
                        case BlendMode.Overlay:
                            rr = br < 0.5f ? 2f * br * dr : 1f - 2f * (1f - br) * (1f - dr);
                            rg = bg < 0.5f ? 2f * bg * dg : 1f - 2f * (1f - bg) * (1f - dg);
                            rb = bb < 0.5f ? 2f * bb * db : 1f - 2f * (1f - bb) * (1f - db);
                            break;
                        case BlendMode.SoftLight:
                            rr = (1f - 2f * dr) * br * br + 2f * dr * br;
                            rg = (1f - 2f * dg) * bg * bg + 2f * dg * bg;
                            rb = (1f - 2f * db) * bb * bb + 2f * db * bb;
                            break;
                        case BlendMode.HardLight:
                            rr = dr < 0.5f ? 2f * br * dr : 1f - 2f * (1f - br) * (1f - dr);
                            rg = dg < 0.5f ? 2f * bg * dg : 1f - 2f * (1f - bg) * (1f - dg);
                            rb = db < 0.5f ? 2f * bb * db : 1f - 2f * (1f - bb) * (1f - db);
                            break;
                        case BlendMode.Darken:
                            rr = Math.Min(br, dr); rg = Math.Min(bg, dg); rb = Math.Min(bb, db);
                            break;
                        case BlendMode.Lighten:
                            rr = Math.Max(br, dr); rg = Math.Max(bg, dg); rb = Math.Max(bb, db);
                            break;
                        case BlendMode.ColorDodge:
                            rr = dr >= 1f ? 1f : Math.Min(1f, br / (1f - dr));
                            rg = dg >= 1f ? 1f : Math.Min(1f, bg / (1f - dg));
                            rb = db >= 1f ? 1f : Math.Min(1f, bb / (1f - db));
                            break;
                        case BlendMode.ColorBurn:
                            rr = dr <= 0f ? 0f : Math.Max(0f, 1f - (1f - br) / dr);
                            rg = dg <= 0f ? 0f : Math.Max(0f, 1f - (1f - bg) / dg);
                            rb = db <= 0f ? 0f : Math.Max(0f, 1f - (1f - bb) / db);
                            break;
                        case BlendMode.Difference:
                            rr = Math.Abs(br - dr); rg = Math.Abs(bg - dg); rb = Math.Abs(bb - db);
                            break;
                        case BlendMode.Exclusion:
                            rr = br + dr - 2f * br * dr;
                            rg = bg + dg - 2f * bg * dg;
                            rb = bb + db - 2f * bb * db;
                            break;
                        default:
                            rr = dr; rg = dg; rb = db;
                            break;
                    }

                    output[oIdx]     = (byte)Math.Clamp((int)((rr * da + br * (1 - da)) * 255), 0, 255);
                    output[oIdx + 1] = (byte)Math.Clamp((int)((rg * da + bg * (1 - da)) * 255), 0, 255);
                    output[oIdx + 2] = (byte)Math.Clamp((int)((rb * da + bb * (1 - da)) * 255), 0, 255);
                    output[oIdx + 3] = 255;
                }
            });
        }

        tracker?.Commit(currentUnion, dirty);
        return output;
    }

    /// <summary>RGBA-input variant — used by Full Redraw / export paths that haven't switched to scratch buffers.</summary>
    private static void WriteBgraTexFile(string path, byte[] rgbaData, int width, int height)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                TexFileWriter.WriteRgba(path, rgbaData, width, height);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }
    }

    /// <summary>BGRA-input variant — used by the inplace composite path with reusable scratch buffers.</summary>
    private static void WriteBgraTexFile(string path, byte[] bgraData, int bgraLength, int width, int height)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                TexFileWriter.WriteBgra(path, bgraData, bgraLength, width, height);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
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
    public static float ComputeFadeMaskWeight(LayerFadeMask mask, float falloff, float ru, float rv, float da)
    {
        if (mask == Core.LayerFadeMask.Uniform)
            return da;

        float dist = MathF.Sqrt(ru * ru + rv * rv) * 2f;
        dist = MathF.Min(dist, 1f);

        float edgeDist = MathF.Min(0.5f - MathF.Abs(ru), 0.5f - MathF.Abs(rv));
        edgeDist = MathF.Max(edgeDist, 0f) * 2f;

        float f = MathF.Max(falloff, 0.01f);
        float m;

        switch (mask)
        {
            case Core.LayerFadeMask.RadialFadeOut:
                m = 1f - Smoothstep(0f, f, dist);
                break;
            case Core.LayerFadeMask.RadialFadeIn:
                m = Smoothstep(1f - f, 1f, dist);
                break;
            case Core.LayerFadeMask.EdgeGlow:
                m = 1f - Smoothstep(0f, f, edgeDist);
                break;
            case Core.LayerFadeMask.GaussianFeather:
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
        imageLoader.ClearCache();
        DebugServer.AppendLog("[PreviewService] Texture cache cleared");
    }

    public void ClearMesh()
    {
        currentMesh = null;
        DebugServer.AppendLog("[PreviewService] Mesh cleared");
    }

    /// <summary>Read the on-disk dimensions of a decal image (PNG/JPG/TGA/DDS/TEX). Returns null on failure.</summary>
    public (int Width, int Height)? GetImageDimensions(string path)
    {
        var img = imageLoader.LoadImage(path);
        return img == null ? null : (img.Value.Width, img.Value.Height);
    }

    /// <summary>Get the base diffuse texture size for a group (used for scale auto-fit).</summary>
    public (int Width, int Height) GetBaseTextureSize(TargetGroup group)
    {
        var tex = LoadBaseTexture(group);
        return (tex.Width, tex.Height);
    }

    /// <summary>Get the base diffuse texture RGBA data for export.</summary>
    public (byte[] Data, int Width, int Height)? GetBaseTextureData(TargetGroup group)
    {
        try { return LoadBaseTexture(group); }
        catch { return null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        // Cancel any in-flight background composite so it can't write to cleared state
        try { asyncCancel?.Cancel(); } catch { }
        asyncCancel?.Dispose();
        asyncCancel = null;
        pendingBatch = null;
        composeLock.Dispose();

        imageLoader.ClearCache();
        baseTextureCache.Clear();
        groupScratch.Clear();
        compositeResults.Clear();
        previewDiskPaths.Clear();
        previewMtrlDiskPaths.Clear();
        emissiveOffsets.Clear();
        initializedRedirects.Clear();
        rowPairAllocators.Clear();
        vanillaColorTables.Clear();
        lastBuiltColorTables.Clear();
        indexMapGamePaths.Clear();
        lastAppliedEmissive.Clear();
        emissiveHook?.ClearTargets();
    }
}
