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
using SkinTattoo.Core;
using SkinTattoo.Http;
using SkinTattoo.Interop;
using SkinTattoo.Mesh;

namespace SkinTattoo.Services;

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

    // Cached "does the group's mtrl expose g_SamplerMask?" answers. Keyed by
    // mtrl game path. Populated on first lookup so the UI combo stays fast.
    private readonly ConcurrentDictionary<string, bool> maskSupportCache = new();

    /// <summary>Does the group's material declare a g_SamplerMask sampler?</summary>
    public bool MaterialSupportsMask(TargetGroup group)
    {
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return false;
        return maskSupportCache.GetOrAdd(group.MtrlGamePath!, key =>
        {
            var mtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;
            var maskGamePath = GetMaskGamePathFromMtrl(key, mtrlDisk);
            return !string.IsNullOrEmpty(maskGamePath);
        });
    }

    /// <summary>Heuristic: filename hint (_n / _norm / "normal") plus RGB-clustering fallback.</summary>
    public bool IsLikelyNormalMap(string imagePath)
    {
        var name = Path.GetFileNameWithoutExtension(imagePath).ToLowerInvariant();
        if (name.EndsWith("_n") || name.Contains("_norm") || name.Contains("normal"))
            return true;
        var img = imageLoader.LoadImage(imagePath);
        return img != null && DecalImageLoader.LooksLikeNormalMap(img.Value.Data);
    }
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
    /// re-evaluate. Subscribers should be cheap and main-thread only  --
    /// callers on background threads must marshal to the framework thread
    /// before invoking <see cref="NotifyMeshChanged"/>.
    ///
    /// Why not auto-fire from inside LoadMesh*: Plugin.cs project init runs
    /// LoadMeshForGroup on a background thread, and ImGui-state subscribers
    /// (e.g. ModelEditorWindow) cannot tolerate cross-thread invocation.
    /// </summary>
    public event Action? MeshChanged;
    public void NotifyMeshChanged() => MeshChanged?.Invoke();

    // GPU swap state tracking  -- ConcurrentDictionary because background composite Task and
    // main thread (UpdatePreviewFull / EnsureEmissiveInitialized / ResetSwapState) can race.
    private readonly ConcurrentDictionary<string, byte> initializedRedirects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> previewDiskPaths =
        new(StringComparer.OrdinalIgnoreCase);

    // Cache base textures to avoid re-reading from disk on every update.
    // Disk-path key has no encoding constraint, so default comparer is fine.
    private readonly ConcurrentDictionary<string, (byte[] Data, int Width, int Height)> baseTextureCache = new();

    // Emissive ConstantBuffer offsets (mtrlGamePath -> byte offset, used for full redraw .mtrl building)
    private readonly ConcurrentDictionary<string, int> emissiveOffsets = new();

    // Track mtrl preview disk paths (mtrlGamePath -> disk path for ColorTable matching)
    private readonly ConcurrentDictionary<string, string> previewMtrlDiskPaths =
        new(StringComparer.OrdinalIgnoreCase);

    // v1 PBR: per-TargetGroup row pair allocators (keyed by MtrlGamePath)
    private readonly ConcurrentDictionary<string, RowPairAllocator> rowPairAllocators =
        new(StringComparer.OrdinalIgnoreCase);

    // v1 PBR: index map game path resolved from each mtrl's sampler 0x565F8FD8.
    // Keyed by MtrlGamePath. Cached after first resolution.
    private readonly ConcurrentDictionary<string, string> indexMapGamePaths =
        new(StringComparer.OrdinalIgnoreCase);

    // skin.shpk + patched ColorTable: tracks which MtrlGamePaths entered the skin CT
    // pipeline during Full Redraw. Background composite checks this to decide between
    // skin CT (build-from-scratch) vs PBR CT (clone-vanilla) vs legacy CBuffer paths.
    private readonly ConcurrentDictionary<string, byte> skinCtMaterials =
        new(StringComparer.OrdinalIgnoreCase);

    // sampler ID for g_SamplerIndex per Penumbra ShpkFile.cs:17
    private const uint IndexSamplerId = 0x565F8FD8u;
    private const uint MaskSamplerId = 0x8A4E82B6u;

    // Cached vanilla ColorTable bytes per material (keyed by MtrlGamePath).
    // Populated on first ColorTable write from the main thread; background composite
    // reads from here to avoid cross-thread GPU access.
    private readonly ConcurrentDictionary<string, (Half[] Data, int Width, int Height)> vanillaColorTables =
        new(StringComparer.OrdinalIgnoreCase);

    // Most recently built (modified) ColorTable per material  -- populated by the background
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

    // File-write throttle. Each WriteBgraTexFile pushes ~64MB through the FS at 4096^2
    // (synchronous IO ~ 64-128ms per file), and Penumbra only re-reads files on Full
    // Redraw  -- during inplace cycles GPU swap is the source of truth. So we only flush
    // disk every 250ms; sustained drag still pumps SwapTexture at ~30Hz, but disk IO
    // doesn't dominate the compose thread anymore.
    private DateTime lastFileFlushUtc = DateTime.MinValue;
    private const double FileFlushIntervalMs = 250;

    // Game-side GPU swap throttle. Each SwapTexture at 4096^2 costs 5-15ms of main thread
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
    private readonly Dictionary<string, (Vector3 Color, EmissiveAnimMode Anim, float Speed, float Amp)> lastAppliedEmissive =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-group reusable scratch buffers  -- eliminates the per-cycle 4-67MB LOH allocations
    // that thrashed GC during a 3D-editor drag. Filled on the background thread under
    // composeLock; consumed by the main thread next frame.
    //
    // All buffers are SINGLE-buffered. With the 30Hz throttle + serialized compose,
    // main-thread consumption finishes well before the next compose runs. The Rgba/PreviewRgba
    // ones get stored in compositeResults for the 3D editor to read async; in the rare case
    // where the 3D editor reads while a new compose is mid-write, we get at most one frame
    // of torn pixels which self-heals on the next compose (≤33ms). The previous double-buffer
    // strategy doubled resident memory (600MB+ at 4096^2), unacceptable.
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
        public byte[]? Bgra;        // diffuse BGRA -> file write + GPU swap
        public byte[]? Rgba;        // diffuse RGBA -> compositeResults dict (3D editor)
        public byte[]? PreviewRgba; // 3D-editor preview RGBA when AffectsDiffuse=false on a layer
        public byte[]? IdxRgba;     // index map composite intermediate (RGBA)
        public byte[]? IdxBgra;     // index map BGRA -> file write + GPU swap
        public byte[]? NormBgra;
        public byte[]? NormRgba;
        public byte[]? MaskBgra;

        // Dirty rect trackers  -- one per output buffer, since each paints a different
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
        // ~64 rows x ~2k px/row at typical decal sizes is the crossover on a 4-core box.
        if (rowCount < 64)
        {
            for (int py = pyMin; py <= pyMax; py++) rowAction(py);
            return;
        }
        Parallel.For(pyMin, pyMax + 1, rowAction);
    }

    private record SwapBatchEntry(string GamePath, string? DiskPath, byte[] BgraData, int Width, int Height);
    private record EmissiveEntry(string MtrlGamePath, string? MtrlDiskPath, Vector3 Color, int CBufferOffset,
        EmissiveAnimMode AnimMode, float AnimSpeed, float AnimAmplitude, Vector3 GradientColorB);
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

    /// <summary>
    /// Returns the base diffuse texture for a group (from cache or disk).
    /// Used by the 3D editor as a fallback when no composite is available yet.
    /// </summary>
    public (byte[] Data, int Width, int Height)? TryGetBaseTexture(TargetGroup? group)
    {
        if (group == null) return null;
        var diffuseDisk = group.OrigDiffuseDiskPath ?? group.DiffuseDiskPath;
        if (string.IsNullOrEmpty(diffuseDisk)) return null;
        return LoadBaseTexture(group);
    }

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
        return true;
    }

    public bool LoadMeshes(List<string> paths)
    {
        if (paths.Count == 0) return false;
        if (paths.Count == 1) return LoadMesh(paths[0]);

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
    /// - new resolver groups -> MeshSlots (multi-mdl + per-mdl matIdx filter)
    /// - migrated single-mdl groups -> MeshGamePath + TargetMatIdx
    /// - pre-resolver groups from old configs -> AllMeshPaths
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
    /// This is what SkinMeshResolver-driven flows use  -- body skin with a
    /// mod-injected material can map to several equipment mdls that each
    /// contribute a body region (top/glv/dwn/sho).
    /// </summary>
    public bool LoadMeshSlots(List<MeshSlot> slots)
    {
        if (slots.Count == 0) return false;
        try
        {
            var merged = meshExtractor.ExtractAndMergeSlots(slots);
            if (merged == null)
            {
                log.Error($"LoadMeshSlots failed: ExtractAndMergeSlots returned null");
                return false;
            }
            currentMesh = merged;
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"LoadMeshSlots exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>Update preview  -- auto-selects full redraw or async in-place GPU swap.</summary>
    public void UpdatePreview(DecalProject project)
    {
        activeProject = project;  // remember for opportunistic vanilla CT caching in ApplyPendingSwaps

        // Clear stale composites for groups whose layers have all been removed.
        // The preview pipeline skips groups with Layers.Count == 0, so without
        // this the 3D editor would keep showing the old decal composite.
        foreach (var group in project.Groups)
        {
            if (group.Layers.Count == 0 && !string.IsNullOrEmpty(group.DiffuseGamePath))
            {
                if (compositeResults.TryRemove(group.DiffuseGamePath, out _))
                    Interlocked.Increment(ref compositeVersion);
            }
        }

        if (config.UseGpuSwap && textureSwap != null && CheckCanSwapInPlace(project))
        {
            StartAsyncInPlace(project);
        }
        else
        {
            // Diagnostic: explicit reason when we fall back to Full Redraw
            DebugServer.AppendLog($"[UpdatePreview] → FULL (UseGpuSwap={config.UseGpuSwap} textureSwap={(textureSwap != null)} canSwap={CanSwapInPlace} denyReason={lastCanSwapDenyReason ?? "(none)"})");
            UpdatePreviewFull(project);
        }
    }

    // Sticky reference so ApplyPendingSwaps can attempt vanilla CT caching every frame
    // (only fires when project changes are pending). Set by UpdatePreview*.
    private DecalProject? activeProject;

    /// <summary>Call from Draw() every frame to apply completed async swaps.</summary>
    public unsafe void ApplyPendingSwaps()
    {
        // Opportunistic vanilla CT cache attempt  -- runs every frame but is idempotent
        // and only fires for materials not yet cached. Catches the case where Full Redraw
        // happened in an earlier frame and the GPU is now ready to read.
        if (activeProject != null)
            TryCacheVanillaColorTables(activeProject);

        // Opportunistic EmissiveCBufferHook re-arming: on plugin open the first Full Redraw
        // does not produce an in-place batch, and the player's CharacterBase may still be
        // mid-redraw when any early SetTargetByPath fires. Running it every frame (cheap
        // pointer scan; hook dedupes internally) guarantees pulse targets arm as soon as
        // the fresh material handles exist -- no user wiggle required.
        if (activeProject != null)
            MaintainEmissiveHookTargets(activeProject);

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
        // Clamp at 33ms floor (~30Hz) regardless of config  -- anything lower is pure main-thread
        // waste since compose itself is 30Hz capped, and defends against legacy configs with
        // lower values that would otherwise hang the game until the user opens settings.
        int intervalMs = System.Math.Max(33, config.GameSwapIntervalMs);
        bool withinThrottle = (nowUtc - lastGameSwapUtc).TotalMilliseconds < intervalMs;
        if (withinThrottle && !idleFlush)
        {
            // Only re-stash if no newer batch has arrived  -- the worker may have overwritten
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
            if (slot == null)
            {
                DebugServer.AppendLog($"[Swap] slot not found: {entry.GamePath} (disk={entry.DiskPath ?? "null"})");
                continue;
            }
            var ok = textureSwap.SwapTexture(slot, entry.BgraData, entry.Width, entry.Height);
            if (!ok)
                DebugServer.AppendLog($"[Swap] SwapTexture failed: {entry.GamePath}");
        }

        foreach (var ct in batch.ColorTables)
        {
            textureSwap!.ReplaceColorTableRaw(charBase, ct.MtrlGamePath, ct.MtrlDiskPath, ct.Data, ct.Width, ct.Height);
        }

        foreach (var em in batch.Emissives)
        {
            // Legacy single-emissive path: only fires when no ColorTable entry was queued
            // for this material (skin.shpk fallback or non-Dawntrail layouts).
            // Dedupe: skip if this mtrl's color is identical to what we pushed last time  --
            // otherwise a drag that never touches emissive still spams tree walks + CT
            // texture recreation every cycle.
            var key = (em.Color, em.AnimMode, em.AnimSpeed, em.AnimAmplitude);
            bool isDup = lastAppliedEmissive.TryGetValue(em.MtrlGamePath, out var prev) && prev == key;

            // Hook registration is always attempted: on plugin open the first compose may
            // hit the CharacterBase mid-redraw when fresh material handles aren't ready,
            // causing the initial SetTargetByPath to silently miss. Re-calling each cycle
            // until the handle exists guarantees the hook arms for pulse without requiring
            // the user to wiggle a slider. The hook itself dedupes internally.
            if (emissiveHook != null && em.CBufferOffset > 0)
                emissiveHook.SetTargetByPath(charBase, em.MtrlGamePath, em.MtrlDiskPath, em.Color,
                    em.AnimMode, em.AnimSpeed, em.AnimAmplitude, em.GradientColorB);

            if (isDup) continue;
            lastAppliedEmissive[em.MtrlGamePath] = key;

            textureSwap!.UpdateEmissiveViaColorTable(charBase, em.MtrlGamePath, em.MtrlDiskPath, em.Color);
        }

        LastUpdateMode = "inplace";
    }

    /// <summary>Get local player CharacterBase pointer for direct manipulation.</summary>
    public unsafe FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* GetCharacterBase()
        => textureSwap?.GetLocalPlayerCharacterBase();

    /// <summary>
    /// Idempotent per-frame hook maintenance: re-announces every emissive group's current
    /// color + anim params to EmissiveCBufferHook. Exists so the first successful
    /// CharacterBase scan (post-redraw) arms the hook without waiting for a user-triggered
    /// compose cycle. skin-CT materials are excluded (they drive pulse via DXBC, not cbuffer).
    /// </summary>
    private unsafe void MaintainEmissiveHookTargets(DecalProject project)
    {
        if (emissiveHook == null) return;
        var charBase = textureSwap?.GetLocalPlayerCharacterBase();
        if (charBase == null) return;

        foreach (var group in project.Groups)
        {
            if (string.IsNullOrEmpty(group.MtrlGamePath)) continue;
            if (skinCtMaterials.ContainsKey(group.MtrlGamePath!)) continue;
            if (!emissiveOffsets.TryGetValue(group.MtrlGamePath!, out var emOff) || emOff <= 0) continue;
            if (!group.HasEmissiveLayers()) continue;

            var color = GetCombinedEmissiveColor(group.Layers);
            var (mode, speed, amp, colorB) = GetDominantEmissiveAnim(group.Layers);
            var mtrlDisk = previewMtrlDiskPaths.GetValueOrDefault(group.MtrlGamePath!);
            emissiveHook.SetTargetByPath(charBase, group.MtrlGamePath!, mtrlDisk, color, mode, speed, amp, colorB);
        }
    }

    /// <summary>Write highlight emissive color via ColorTable texture swap or CBuffer hook.</summary>
    /// <param name="highlightLayerIndex">For skin CT: only highlight this layer, others keep original color. -1 = all.</param>
    public unsafe void HighlightEmissiveColor(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* charBase,
        TargetGroup group, Vector3 color, int highlightLayerIndex = -1)
    {
        if (textureSwap == null) return;
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return;

        var mtrlDiskPath = previewMtrlDiskPaths.GetValueOrDefault(group.MtrlGamePath!);

        // skin CT materials: build a CT where the target layer uses highlight color
        // and all other layers keep their real emissive colors.
        if (skinCtMaterials.ContainsKey(group.MtrlGamePath!))
        {
            var tempLayers = new List<DecalLayer>();
            for (int i = 0; i < group.Layers.Count; i++)
            {
                var l = group.Layers[i];
                if (!l.IsVisible || l.TargetMap != TargetMap.Diffuse || !l.AffectsEmissive || l.AllocatedRowPair < 0) continue;
                bool isTarget = (highlightLayerIndex < 0 || highlightLayerIndex == i);
                tempLayers.Add(new DecalLayer
                {
                    IsVisible = true, AffectsEmissive = true,
                    AllocatedRowPair = l.AllocatedRowPair,
                    EmissiveColor = isTarget ? color : l.EmissiveColor,
                    EmissiveColorB = l.EmissiveColorB,
                    EmissiveIntensity = isTarget ? 1f : l.EmissiveIntensity,
                    AnimMode = l.AnimMode,
                    AnimSpeed = l.AnimSpeed,
                    AnimAmplitude = l.AnimAmplitude,
                    AnimFreq = l.AnimFreq,
                    AnimDirMode = l.AnimDirMode,
                    AnimDirAngle = l.AnimDirAngle,
                    AnimDualColor = l.AnimDualColor,
                    UvCenter = l.UvCenter,
                });
            }
            if (tempLayers.Count == 0) return;
            var ctBytes = MtrlFileWriter.BuildSkinColorTablePerLayer(tempLayers);
            var ctHalfs = System.Runtime.InteropServices.MemoryMarshal
                .Cast<byte, Half>((ReadOnlySpan<byte>)ctBytes).ToArray();
            textureSwap.ReplaceColorTableRaw(charBase, group.MtrlGamePath!, mtrlDiskPath, ctHalfs, 8, 32);
            return;
        }

        emissiveOffsets.TryGetValue(group.MtrlGamePath!, out var cbufOffset);
        textureSwap.UpdateEmissiveViaColorTable(charBase, group.MtrlGamePath!, mtrlDiskPath, color);
        if (emissiveHook != null && cbufOffset > 0)
        {
            var (animMode, animSpeed, animAmp, animColorB) = GetDominantEmissiveAnim(group.Layers);
            emissiveHook.SetTargetByPath(charBase, group.MtrlGamePath!, mtrlDiskPath, color,
                animMode, animSpeed, animAmp, animColorB);
        }
    }

    /// <summary>
    /// Restore the real per-layer ColorTable after highlight override on skin CT materials.
    /// Returns true if this group uses skin CT and the swap was attempted.
    /// </summary>
    public unsafe bool RestoreSkinCtAfterHighlight(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* charBase, TargetGroup group)
    {
        if (textureSwap == null) return false;
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return false;
        if (!skinCtMaterials.ContainsKey(group.MtrlGamePath!)) return false;

        var ctBytes = MtrlFileWriter.BuildSkinColorTablePerLayer(group.Layers);
        var ctHalfs = System.Runtime.InteropServices.MemoryMarshal
            .Cast<byte, Half>((ReadOnlySpan<byte>)ctBytes).ToArray();
        var mtrlDiskPath = previewMtrlDiskPaths.GetValueOrDefault(group.MtrlGamePath!);
        textureSwap.ReplaceColorTableRaw(charBase, group.MtrlGamePath!, mtrlDiskPath, ctHalfs, 8, 32);
        return true;
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

        var safeName = MakeSafeFileName(group.DiffuseGamePath!);
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
            var redirects = BuildPreviewRedirects(project);
            ApplyPreviewRedirects(project, redirects);
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] Full update exception: {ex.Message}");
            log.Error(ex, "UpdatePreviewFull failed");
        }
    }

    /// <summary>
    /// CPU compositing + file writes. Thread-safe, can run in background.
    /// Returns the redirect map to pass to <see cref="ApplyPreviewRedirects"/>.
    /// </summary>
    public Dictionary<string, string> BuildPreviewRedirects(DecalProject project)
    {
        var redirects = new Dictionary<string, string>();

        // Deploy patched skin.shpk BEFORE processing groups so ProcessGroup
        // can detect the patched shader and route to ColorTable path.
        TryDeployPatchedSkinShpk(project, redirects);

        int skippedNoDiffuse = 0, skippedNoLayers = 0, processed = 0;
        foreach (var group in project.Groups)
        {
            if (string.IsNullOrEmpty(group.DiffuseGamePath)) { skippedNoDiffuse++; continue; }
            if (group.Layers.Count == 0) { skippedNoLayers++; continue; }

            ProcessGroup(group, redirects);
            processed++;
        }
        DebugServer.AppendLog($"[Build] groups={project.Groups.Count} processed={processed} " +
            $"skipNoDiffuse={skippedNoDiffuse} skipNoLayers={skippedNoLayers} " +
            $"redirects={redirects.Count}");

        return redirects;
    }

    /// <summary>
    /// Penumbra IPC + redraw. Must run on the framework/main thread.
    /// </summary>
    public void ApplyPreviewRedirects(DecalProject project, Dictionary<string, string> redirects)
    {
        LastUpdateMode = "full";

        if (redirects.Count > 0)
            penumbra.SetTextureRedirects(redirects);

        penumbra.RedrawPlayer();

        DebugServer.AppendLog($"[PreviewService] Registering {redirects.Count} redirect paths");
        foreach (var (gamePath, diskPath) in redirects)
        {
            initializedRedirects.TryAdd(gamePath, 0);
            previewDiskPaths[gamePath] = diskPath;
        }

        activeProject = project;
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
        // Idempotent  -- only the FIRST successful cache per material counts. Vanilla normal
        // scan is handled lazily inside TryAllocateRowPairForLayer (disk-only, no GPU needed).
        TryCacheVanillaColorTables(project);

        // Capture all data needed by background thread (avoid touching mutable state later)
        var jobs = new List<(TargetGroup Group, string DiffuseGamePath, string? DiskPath,
            string? NormGamePath, string? NormDiskPath, string? MtrlGamePath, int EmissiveOffset,
            string? IndexGamePath, string? IndexDiskPath,
            List<LayerSnapshot> Layers, Vector3 EmissiveColor, bool IsSkinCt)>();

        foreach (var group in project.Groups)
        {
            if (string.IsNullOrEmpty(group.DiffuseGamePath) || group.Layers.Count == 0)
                continue;
            // A group qualifies for inplace swap once ANY of its applicable
            // target textures has been initialized via Full Redraw. A Normal-only
            // group never writes a diffuse redirect, so gating strictly on
            // DiffuseGamePath would permanently skip it.
            bool anyInitialized = previewDiskPaths.ContainsKey(group.DiffuseGamePath)
                || (!string.IsNullOrEmpty(group.NormGamePath)
                    && previewDiskPaths.ContainsKey(group.NormGamePath!));
            if (!anyInitialized) continue;

            previewDiskPaths.TryGetValue(group.DiffuseGamePath, out var diffDisk);

            emissiveOffsets.TryGetValue(group.MtrlGamePath ?? "", out var emOff);

            // v1 PBR: resolve index map path (cached) and look up its staged disk path
            var indexGame = GetIndexMapGamePath(group);
            string? indexDisk = null;
            if (!string.IsNullOrEmpty(indexGame))
                previewDiskPaths.TryGetValue(indexGame, out indexDisk);

            // skin.shpk ColorTable mode: set during Full Redraw when patched shader is active
            bool isSkinCt = !string.IsNullOrEmpty(group.MtrlGamePath)
                            && skinCtMaterials.ContainsKey(group.MtrlGamePath!);

            // Snapshot layer parameters so background thread reads stable data
            var snapshots = new List<LayerSnapshot>();
            foreach (var l in group.Layers)
                snapshots.Add(new LayerSnapshot(l));

            // Composite base MUST come from the vanilla/mod original, never from
            // previewDiskPaths (our own prior output) — user Normal/Mask RGB
            // paints would accumulate on each async cycle and produce a trail.
            jobs.Add((group, group.DiffuseGamePath, diffDisk,
                group.NormGamePath, group.OrigNormDiskPath ?? group.NormDiskPath,
                group.MtrlGamePath, emOff,
                indexGame, indexDisk,
                snapshots,
                GetCombinedEmissiveColor(group.Layers), isSkinCt));
        }

        Task.Run(() =>
        {
            // Serialize composite cycles. We're already on a thread-pool thread so a
            // synchronous Wait is fine  -- and it lets us stay in the unsafe-inheriting
            // lambda without the async-state-machine restriction.
            try { composeLock.Wait(token); }
            catch (OperationCanceledException) { return; }

            try
            {
                // Coalesce: if a newer request arrived while we were waiting on the
                // lock, drop this one  -- the next worker will compose the latest state.
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

                    // 3D editor preview diffuse  -- paints ALL visible layers with images so the
                    // user sees decal placement even when AffectsDiffuse is off.
                    bool hasPreviewOnly = false;
                    foreach (var l in layers)
                    {
                        if (!l.IsVisible || string.IsNullOrEmpty(l.ImagePath)) continue;
                        // Needs preview pass if the layer doesn't paint onto GPU diffuse:
                        // AffectsDiffuse=false, or TargetMap is Mask/Normal.
                        if (l.TargetMap != TargetMap.Diffuse || !l.AffectsDiffuse)
                        { hasPreviewOnly = true; break; }
                    }

                    // GPU diffuse  -- only paints layers with AffectsDiffuse=true.
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

                    // 3D editor consumes compositeResults  -- feed it the preview-with-all-layers version.
                    // Dirty rect accompanies the buffer so ModelEditorWindow only uploads the changed region.
                    if (previewRgba != null)
                    {
                        compositeResults[job.DiffuseGamePath] =
                            (previewRgba, baseTex.Width, baseTex.Height, previewDirty);
                        Interlocked.Increment(ref compositeVersion);
                    }

                    // GPU/file path uses the filtered version. Swizzle only the dirty region  --
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

                    // skin.shpk + patched ColorTable: build CT from scratch (no vanilla base),
                    // composite row indices to normal.alpha, queue CT for GPU swap.
                    if (job.IsSkinCt && hasPbrLayers && !string.IsNullOrEmpty(job.MtrlGamePath))
                    {
                        var skinCtBytes = MtrlFileWriter.BuildSkinColorTablePerLayer(layers);
                        var skinCtHalfs = System.Runtime.InteropServices.MemoryMarshal
                            .Cast<byte, Half>((ReadOnlySpan<byte>)skinCtBytes).ToArray();

                        var mtrlDisk = previewMtrlDiskPaths.GetValueOrDefault(job.MtrlGamePath!);
                        ctEntries.Add(new ColorTableEntry(
                            job.MtrlGamePath!, mtrlDisk, skinCtHalfs, 8, 32));
                        lastBuiltColorTables[job.MtrlGamePath!] = (skinCtHalfs, 8, 32);
                        ctQueued = true;

                        // Normal map: row pair index in alpha channel
                        if (!string.IsNullOrEmpty(job.NormGamePath) && !string.IsNullOrEmpty(job.NormDiskPath))
                        {
                            var normRgbaBuf = EnsureBuf(ref scratch.NormRgba, byteCount);
                            var normRgba = CompositeRowIndexNorm(
                                layers, job.NormDiskPath!, baseTex.Width, baseTex.Height,
                                outputBuffer: normRgbaBuf);
                            if (normRgba != null)
                            {
                                if (AnyTargetMapLayer(layers, TargetMap.Normal))
                                    CpuUvComposite(layers, normRgba, baseTex.Width, baseTex.Height,
                                        outputBuffer: normRgba, targetFilter: TargetMap.Normal, preserveAlpha: true);
                                var normBgraBuf = EnsureBuf(ref scratch.NormBgra, byteCount);
                                TextureSwapService.RgbaToBgra(normRgba, normBgraBuf, byteCount);
                                previewDiskPaths.TryGetValue(job.NormGamePath ?? "", out var normDiskOut);
                                if (flushFiles && normDiskOut != null)
                                    WriteBgraTexFile(normDiskOut, normBgraBuf, byteCount, baseTex.Width, baseTex.Height);
                                texEntries.Add(new SwapBatchEntry(
                                    job.NormGamePath!, normDiskOut, normBgraBuf, baseTex.Width, baseTex.Height));
                            }
                        }
                    }

                    // Index map: rewrite R = rowPair*17, G = weight*255 (per Penumbra
                    // MaterialExporter:136-137). Vanilla bytes are read from the staged
                    // disk path that ProcessGroup populated, then cloned + modified.
                    if (!job.IsSkinCt && hasPbrLayers && !string.IsNullOrEmpty(job.IndexGamePath) && !string.IsNullOrEmpty(job.IndexDiskPath))
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
                        }
                    }

                    if (!job.IsSkinCt && hasPbrLayers && !string.IsNullOrEmpty(job.MtrlGamePath)
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

                    // Legacy emissive fallback path: only fires when no ColorTable entry was queued
                    // (non-Dawntrail layouts or materials without patched skin.shpk).
                    bool hasVisibleEmissive = HasEmissiveLayers(job.Layers);
                    bool hadEmissiveState = !string.IsNullOrEmpty(job.MtrlGamePath) && job.EmissiveOffset > 0;
                    if (!ctQueued && (hasVisibleEmissive || hadEmissiveState) && !string.IsNullOrEmpty(job.NormDiskPath))
                    {
                        if (hasVisibleEmissive)
                        {
                            var normRgbaBuf = EnsureBuf(ref scratch.NormRgba, byteCount);
                            var normRgba = CompositeEmissiveNorm(
                                layers, job.NormDiskPath!, baseTex.Width, baseTex.Height,
                                outputBuffer: normRgbaBuf);
                            if (normRgba != null)
                            {
                                if (AnyTargetMapLayer(layers, TargetMap.Normal))
                                    CpuUvComposite(layers, normRgba, baseTex.Width, baseTex.Height,
                                        outputBuffer: normRgba, targetFilter: TargetMap.Normal, preserveAlpha: true);
                                var normBgraBuf = EnsureBuf(ref scratch.NormBgra, byteCount);
                                TextureSwapService.RgbaToBgra(normRgba, normBgraBuf, byteCount);
                                previewDiskPaths.TryGetValue(job.NormGamePath ?? "", out var normDiskOut);
                                if (flushFiles && normDiskOut != null)
                                    WriteBgraTexFile(normDiskOut, normBgraBuf, byteCount, baseTex.Width, baseTex.Height);
                                texEntries.Add(new SwapBatchEntry(
                                    job.NormGamePath!, normDiskOut, normBgraBuf, baseTex.Width, baseTex.Height));
                            }
                        }

                        if (!string.IsNullOrEmpty(job.MtrlGamePath))
                        {
                            var mtrlDisk = previewMtrlDiskPaths.GetValueOrDefault(job.MtrlGamePath!);
                            emissiveOffsets.TryGetValue(job.MtrlGamePath!, out var emOff);
                            var (animMode, animSpeed, animAmp, animColorB) = GetDominantEmissiveAnim(layers);
                            emEntries.Add(new EmissiveEntry(job.MtrlGamePath!, mtrlDisk, job.EmissiveColor, emOff,
                                animMode, animSpeed, animAmp, animColorB));
                        }
                    }

                    bool irisMaskHandled = false;
                    if (hasVisibleEmissive && !string.IsNullOrEmpty(job.MtrlGamePath) && job.MtrlGamePath!.Contains("_iri_"))
                    {
                        irisMaskHandled = true;
                        var irisMtrlDisk = job.Group.OrigMtrlDiskPath ?? job.Group.MtrlDiskPath;
                        var maskGamePath = GetMaskGamePathFromMtrl(job.MtrlGamePath!, irisMtrlDisk);
                        if (!string.IsNullOrEmpty(maskGamePath))
                        {
                            var maskTex = LoadGameTexture(maskGamePath);
                            if (maskTex != null)
                            {
                                var (maskData, maskW, maskH) = maskTex.Value;
                                var maskRgba = CompositeIrisMask(layers, maskData, maskW, maskH);
                                if (maskRgba != null)
                                {
                                    if (AnyTargetMapLayer(layers, TargetMap.Mask))
                                        CpuUvComposite(layers, maskRgba, maskW, maskH,
                                            outputBuffer: maskRgba, targetFilter: TargetMap.Mask);
                                    int maskBytes = maskW * maskH * 4;
                                    var maskBgra = EnsureBuf(ref scratch.MaskBgra, maskBytes);
                                    TextureSwapService.RgbaToBgra(maskRgba, maskBgra, maskBytes);
                                    previewDiskPaths.TryGetValue(maskGamePath, out var maskDiskOut);
                                    if (flushFiles && maskDiskOut != null)
                                        WriteBgraTexFile(maskDiskOut, maskBgra, maskBytes, maskW, maskH);
                                    texEntries.Add(new SwapBatchEntry(
                                        maskGamePath, maskDiskOut, maskBgra, maskW, maskH));
                                }
                            }
                        }
                    }

                    // User Normal-target layers without emissive: load base + paint RGB
                    bool normWrittenThisCycle = job.IsSkinCt || (!ctQueued && hasVisibleEmissive);
                    if (!normWrittenThisCycle && AnyTargetMapLayer(layers, TargetMap.Normal)
                        && !string.IsNullOrEmpty(job.NormGamePath) && !string.IsNullOrEmpty(job.NormDiskPath))
                    {
                        var normRgbaBuf = EnsureBuf(ref scratch.NormRgba, byteCount);
                        if (LoadRgbaResizedInto(job.NormDiskPath!, baseTex.Width, baseTex.Height, normRgbaBuf))
                        {
                            CpuUvComposite(layers, normRgbaBuf, baseTex.Width, baseTex.Height,
                                outputBuffer: normRgbaBuf,
                                targetFilter: TargetMap.Normal,
                                preserveAlpha: true);
                            var normBgraBuf = EnsureBuf(ref scratch.NormBgra, byteCount);
                            TextureSwapService.RgbaToBgra(normRgbaBuf, normBgraBuf, byteCount);
                            previewDiskPaths.TryGetValue(job.NormGamePath!, out var normDiskOut);
                            if (flushFiles && normDiskOut != null)
                                WriteBgraTexFile(normDiskOut, normBgraBuf, byteCount, baseTex.Width, baseTex.Height);
                            texEntries.Add(new SwapBatchEntry(
                                job.NormGamePath!, normDiskOut, normBgraBuf, baseTex.Width, baseTex.Height));
                        }
                    }

                    // User Mask-target layers without iris: resolve mask via mtrl sampler + paint RGB
                    if (!irisMaskHandled && AnyTargetMapLayer(layers, TargetMap.Mask)
                        && !string.IsNullOrEmpty(job.MtrlGamePath))
                    {
                        var mtrlDisk = previewMtrlDiskPaths.GetValueOrDefault(job.MtrlGamePath!)
                                       ?? job.Group.OrigMtrlDiskPath ?? job.Group.MtrlDiskPath;
                        var maskGamePath = GetMaskGamePathFromMtrl(job.MtrlGamePath!, mtrlDisk);
                        if (!string.IsNullOrEmpty(maskGamePath))
                        {
                            var maskTex = LoadGameTexture(maskGamePath);
                            if (maskTex != null)
                            {
                                var (maskData, maskW, maskH) = maskTex.Value;
                                int maskBytes = maskW * maskH * 4;
                                var maskWork = new byte[maskBytes];
                                Buffer.BlockCopy(maskData, 0, maskWork, 0, maskBytes);
                                var result = CpuUvComposite(layers, maskWork, maskW, maskH,
                                    outputBuffer: maskWork,
                                    targetFilter: TargetMap.Mask);
                                if (result != null)
                                {
                                    var maskBgra = EnsureBuf(ref scratch.MaskBgra, maskBytes);
                                    TextureSwapService.RgbaToBgra(maskWork, maskBgra, maskBytes);
                                    previewDiskPaths.TryGetValue(maskGamePath, out var maskDiskOut);
                                    if (flushFiles && maskDiskOut != null)
                                        WriteBgraTexFile(maskDiskOut, maskBgra, maskBytes, maskW, maskH);
                                    texEntries.Add(new SwapBatchEntry(
                                        maskGamePath, maskDiskOut, maskBgra, maskW, maskH));
                                }
                            }
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
        public TargetMap TargetMap;
        public LayerFadeMask FadeMask;
        public float FadeMaskFalloff;
        public Vector3 DiffuseColor, SpecularColor, EmissiveColor, EmissiveColorB;
        public float EmissiveIntensity;
        public EmissiveAnimMode AnimMode;
        public float AnimSpeed, AnimAmplitude, AnimFreq, AnimDirAngle;
        public RippleDirMode AnimDirMode;
        public bool AnimDualColor;
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
            TargetMap = l.TargetMap;
            FadeMask = l.FadeMask; FadeMaskFalloff = l.FadeMaskFalloff;
            DiffuseColor = l.DiffuseColor; SpecularColor = l.SpecularColor;
            EmissiveColor = l.EmissiveColor; EmissiveColorB = l.EmissiveColorB; EmissiveIntensity = l.EmissiveIntensity;
            AnimMode = l.AnimMode; AnimSpeed = l.AnimSpeed; AnimAmplitude = l.AnimAmplitude; AnimFreq = l.AnimFreq;
            AnimDirMode = l.AnimDirMode; AnimDirAngle = l.AnimDirAngle; AnimDualColor = l.AnimDualColor;
            Roughness = l.Roughness; Metalness = l.Metalness;
            SheenRate = l.SheenRate; SheenTint = l.SheenTint; SheenAperture = l.SheenAperture;
            GradientAngleDeg = l.GradientAngleDeg; GradientScale = l.GradientScale;
            GradientOffset = l.GradientOffset;
            AllocatedRowPair = l.AllocatedRowPair;
        }

        public DecalLayer ToDecalLayer() => new()
        {
            Kind = Kind,
            Name = Name ?? "",
            IsVisible = IsVisible,
            AffectsDiffuse = AffectsDiffuse,
            AffectsSpecular = AffectsSpecular,
            AffectsEmissive = AffectsEmissive,
            AffectsRoughness = AffectsRoughness,
            AffectsMetalness = AffectsMetalness,
            AffectsSheen = AffectsSheen,
            ImagePath = ImagePath,
            UvCenter = UvCenter,
            UvScale = UvScale,
            RotationDeg = RotationDeg,
            Opacity = Opacity,
            BlendMode = BlendMode,
            Clip = Clip,
            TargetMap = TargetMap,
            FadeMask = FadeMask,
            FadeMaskFalloff = FadeMaskFalloff,
            DiffuseColor = DiffuseColor,
            SpecularColor = SpecularColor,
            EmissiveColor = EmissiveColor,
            EmissiveColorB = EmissiveColorB,
            EmissiveIntensity = EmissiveIntensity,
            AnimMode = AnimMode,
            AnimSpeed = AnimSpeed,
            AnimAmplitude = AnimAmplitude,
            AnimFreq = AnimFreq,
            AnimDirMode = AnimDirMode,
            AnimDirAngle = AnimDirAngle,
            AnimDualColor = AnimDualColor,
            Roughness = Roughness,
            Metalness = Metalness,
            SheenRate = SheenRate,
            SheenTint = SheenTint,
            SheenAperture = SheenAperture,
            GradientAngleDeg = GradientAngleDeg,
            GradientScale = GradientScale,
            GradientOffset = GradientOffset,
            AllocatedRowPair = AllocatedRowPair,
        };
    }

    private static bool HasEmissiveLayers(List<LayerSnapshot> layers)
    {
        foreach (var l in layers)
            if (l.IsVisible && l.TargetMap == TargetMap.Diffuse
                && l.AffectsEmissive && !string.IsNullOrEmpty(l.ImagePath))
                return true;
        return false;
    }

    /// <summary>
    /// v1 PBR: cache vanilla ColorTable from GPU for each managed target group.
    /// Idempotent  -- only fires for groups not yet cached. Must be called on the main thread.
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

            // Find sampler 0x565F8FD8 -> texture index -> string offset -> null-terminated path
            int texIndex = -1;
            foreach (var s in mtrl.Samplers)
            {
                if (s.SamplerId == IndexSamplerId) { texIndex = s.TextureIndex; break; }
            }
            if (texIndex < 0 || texIndex >= mtrl.TextureOffsets.Length)
            {
                // Cache the negative result so we don't re-parse the mtrl every frame.
                indexMapGamePaths[group.MtrlGamePath!] = string.Empty;
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
            return indexGamePath;
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PBR] GetIndexMapGamePath failed for {group.Name}: {ex.Message}");
            return null;
        }
    }

    private string? GetMaskGamePathFromMtrl(string mtrlGamePath, string? mtrlDiskPath)
    {
        try
        {
            var mtrlDisk = mtrlDiskPath;
            byte[]? mtrlBytes = null;
            if (!string.IsNullOrEmpty(mtrlDisk) && File.Exists(mtrlDisk))
                mtrlBytes = File.ReadAllBytes(mtrlDisk);
            else
            {
                var pack = meshExtractor.GetSqPackInstance();
                var sqResult = pack?.GetFile(mtrlGamePath);
                if (sqResult != null) mtrlBytes = sqResult.Value.file.RawData.ToArray();
            }
            if (mtrlBytes == null) return null;

            var tempPath = Path.Combine(outputDir, $"temp_mask_{Guid.NewGuid():N}.mtrl");
            File.WriteAllBytes(tempPath, mtrlBytes);
            var lumina = meshExtractor.GetLuminaForDisk();
            var mtrl = lumina!.GetFileFromDisk<MtrlFile>(tempPath);
            try { File.Delete(tempPath); } catch { }

            int texIndex = -1;
            foreach (var s in mtrl.Samplers)
            {
                if (s.SamplerId == MaskSamplerId) { texIndex = s.TextureIndex; break; }
            }
            if (texIndex < 0 || texIndex >= mtrl.TextureOffsets.Length) return null;

            int strOffset = mtrl.TextureOffsets[texIndex].Offset;
            int end = strOffset;
            while (end < mtrl.Strings.Length && mtrl.Strings[end] != 0) end++;
            return System.Text.Encoding.UTF8.GetString(mtrl.Strings, strOffset, end - strOffset);
        }
        catch { return null; }
    }

    private static bool IsIrisMaterial(TargetGroup group) =>
        !string.IsNullOrEmpty(group.MtrlGamePath) && group.MtrlGamePath!.Contains("_iri_");

    /// <summary>
    /// Diagnostic snapshot for HTTP /api/debug/pbr  -- returns one entry per managed group
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

    /// <summary>
    /// True when the group's .mtrl is a skin.shpk material with a non-standard
    /// ColorTable (e.g. Eve "pores" face materials). Emissive preview will strip
    /// the ColorTable to avoid rendering issues.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> skinShpkCtCache = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSkinShpkWithColorTable(TargetGroup group)
    {
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return false;
        var mtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;
        if (string.IsNullOrEmpty(mtrlDisk) || !File.Exists(mtrlDisk)) return false;
        if (skinShpkCtCache.TryGetValue(mtrlDisk, out var cached)) return cached;
        try
        {
            var bytes = File.ReadAllBytes(mtrlDisk);
            var result = HasSkinShpkColorTable(bytes);
            skinShpkCtCache[mtrlDisk] = result;
            return result;
        }
        catch { return false; }
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
            return false;
        }
        layer.AllocatedRowPair = slot.Value;
        return true;
    }

    /// <summary>
    /// Vanilla scan: read the index map (g_SamplerIndex) and feed its R channel to
    /// the row pair allocator so we never hand out a slot vanilla is already using.
    /// Loads via SqPack  -- no Penumbra redirect needed, no GPU access required.
    /// Idempotent.
    /// </summary>
    private void EnsureVanillaScan(TargetGroup group, RowPairAllocator alloc)
    {
        if (alloc.Scanned) return;

        var indexGamePath = GetIndexMapGamePath(group);
        if (string.IsNullOrEmpty(indexGamePath))
            return;

        var indexImg = LoadGameTexture(indexGamePath);
        if (indexImg == null)
        {
            DebugServer.AppendLog($"[PBR] Vanilla scan failed for {group.Name}: cannot load index map {indexGamePath}");
            return;
        }

        var (indexData, w, h) = indexImg.Value;
        alloc.ScanVanillaOccupation(indexData, w, h);
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
    public unsafe void InvalidateEmissiveForGroup(TargetGroup group)
    {
        if (emissiveHook != null && !string.IsNullOrEmpty(group.MtrlGamePath))
        {
            var charBase = GetCharacterBase();
            if (charBase != null)
            {
                var mtrlDisk = previewMtrlDiskPaths.GetValueOrDefault(group.MtrlGamePath!);
                emissiveHook.ClearTargetByPath(charBase, group.MtrlGamePath!, mtrlDisk);
            }
        }

        if (string.IsNullOrEmpty(group.MtrlGamePath)) return;

        // Drop the mtrl/norm initialisation so CheckCanSwapInPlace returns false next
        // cycle and we go through Full Redraw, which rewrites vanilla mtrl + vanilla norm.
        if (!string.IsNullOrEmpty(group.MtrlGamePath))
        {
            initializedRedirects.TryRemove(group.MtrlGamePath!, out _);
            previewDiskPaths.TryRemove(group.MtrlGamePath!, out _);
            previewMtrlDiskPaths.TryRemove(group.MtrlGamePath!, out _);
            emissiveOffsets.TryRemove(group.MtrlGamePath!, out _);
            // Keep skinCtMaterials — it persists until ResetSwapState so inplace
            // composite still routes through the CT path after re-init.
        }
        if (!string.IsNullOrEmpty(group.NormGamePath))
        {
            initializedRedirects.TryRemove(group.NormGamePath!, out _);
            previewDiskPaths.TryRemove(group.NormGamePath!, out _);
        }
        // Force CanSwap recompute next frame
        CanSwapInPlace = false;
    }

    /// <summary>Force the next UpdatePreview to take the Full Redraw path across all groups.
    /// Lighter than ResetSwapState — keeps row-pair allocators, vanilla CT cache, and
    /// emissive offsets intact, just drops the redirect-init markers so
    /// <see cref="CheckCanSwapInPlace"/> returns false next cycle.</summary>
    public void ForceFullRedrawNextCycle()
    {
        initializedRedirects.Clear();
        CanSwapInPlace = false;
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
        skinCtMaterials.Clear();
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
    /// Returns gamePath -> relative disk path (forward slashes) for default_mod.json.
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

        // Diffuse composite  -- write to staging/<gamePath>
        var diffResult = CpuUvComposite(visibleLayers, baseTex.Data, w, h);
        if (diffResult != null)
        {
            var diffOut = WriteStagingTex(stagingDir, group.DiffuseGamePath!, diffResult, w, h);
            redirects[group.DiffuseGamePath!] = diffOut;
        }

        // Emissive: only if there are visible emissive layers + mtrl path is known.
        // Non-Diffuse target layers paint their own texture and are excluded from the
        // emissive pipeline, which targets Diffuse decals' shape as the mask.
        bool hasEmissive = false;
        foreach (var l in visibleLayers)
            if (l.TargetMap == TargetMap.Diffuse && l.AffectsEmissive) { hasEmissive = true; break; }

        if (hasEmissive && !string.IsNullOrEmpty(group.MtrlGamePath))
        {
            bool isSkinMtrl = IsSkinMaterial(group);

            if (isSkinMtrl && patchedSkinShpkPath != null && File.Exists(patchedSkinShpkPath))
            {
                // skin.shpk + ColorTable export: per-layer emissive via embedded CT
                var alloc = new RowPairAllocator();
                alloc.ScanVanillaOccupation(new byte[] { 0, 0, 0, 0 }, 1, 1);
                alloc.TryAllocate(); // reserve row 0
                foreach (var layer in visibleLayers)
                {
                    if (layer.TargetMap == TargetMap.Diffuse && layer.AffectsEmissive)
                        alloc.TryAllocate(layer);
                }

                var ctBytes = MtrlFileWriter.BuildSkinColorTablePerLayer(visibleLayers);
                var emissiveColor = GetCombinedEmissiveColor(visibleLayers);
                var mtrlSource = group.OrigMtrlDiskPath ?? group.MtrlDiskPath ?? group.MtrlGamePath!;
                var mtrlOut = StagingPathFor(stagingDir, group.MtrlGamePath!);
                Directory.CreateDirectory(Path.GetDirectoryName(mtrlOut)!);
                if (TryBuildEmissiveMtrlWithColorTable(mtrlSource, mtrlOut, emissiveColor, ctBytes, out _))
                {
                    redirects[group.MtrlGamePath!] = ToForwardSlash(group.MtrlGamePath!);
                }

                // Normal map: row pair index in alpha channel
                if (!string.IsNullOrEmpty(group.NormGamePath) && !string.IsNullOrEmpty(group.NormDiskPath))
                {
                    var normSource = group.OrigNormDiskPath ?? group.NormDiskPath!;
                    var normResult = CompositeRowIndexNorm(visibleLayers, normSource, w, h);
                    if (normResult != null)
                    {
                        var normOut = WriteStagingTex(stagingDir, group.NormGamePath!, normResult, w, h);
                        redirects[group.NormGamePath!] = normOut;
                    }
                }

                // Patched skin.shpk: copy to staging and include in redirects
                var shpkOut = StagingPathFor(stagingDir, SkinShpkGamePath);
                Directory.CreateDirectory(Path.GetDirectoryName(shpkOut)!);
                File.Copy(patchedSkinShpkPath, shpkOut, overwrite: true);
                redirects[SkinShpkGamePath] = ToForwardSlash(SkinShpkGamePath);
            }
            else
            {
                // Legacy emissive export: uniform CBuffer color
                var emissiveColor = GetCombinedEmissiveColor(visibleLayers);
                var mtrlSource = group.OrigMtrlDiskPath ?? group.MtrlDiskPath ?? group.MtrlGamePath!;
                var mtrlOut = StagingPathFor(stagingDir, group.MtrlGamePath!);
                Directory.CreateDirectory(Path.GetDirectoryName(mtrlOut)!);
                if (TryBuildEmissiveMtrl(mtrlSource, mtrlOut, emissiveColor, out _))
                {
                    redirects[group.MtrlGamePath!] = ToForwardSlash(group.MtrlGamePath!);
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
                    }
                }

                // Iris mask: red-channel composite of decal shapes.
                // iris.shpk reads mask.r as emissive coverage; without it the
                // patched g_EmissiveColor has no visible surface to light.
                if (IsIrisMaterial(group))
                {
                    var irisMtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;
                    var maskGamePath = GetMaskGamePathFromMtrl(group.MtrlGamePath!, irisMtrlDisk);
                    if (!string.IsNullOrEmpty(maskGamePath))
                    {
                        var maskTex = LoadGameTexture(maskGamePath);
                        if (maskTex != null)
                        {
                            var (maskData, mw, mh) = maskTex.Value;
                            var maskPatched = CompositeIrisMask(visibleLayers, maskData, mw, mh);
                            if (maskPatched != null)
                            {
                                var maskOut = WriteStagingTex(stagingDir, maskGamePath, maskPatched, mw, mh);
                                redirects[maskGamePath] = maskOut;
                            }
                        }
                    }
                }
            }
        }

        ApplyUserNormalOverlayForExport(group, visibleLayers, w, h, stagingDir, redirects);
        ApplyUserMaskOverlayForExport(group, visibleLayers, stagingDir, redirects);

        return redirects;
    }

    private void ApplyUserNormalOverlayForExport(TargetGroup group, List<DecalLayer> layers,
        int w, int h, string stagingDir, Dictionary<string, string> redirects)
    {
        if (!AnyTargetMapLayer(layers, TargetMap.Normal)) return;
        if (string.IsNullOrEmpty(group.NormGamePath)) return;

        byte[]? buf = null;
        if (redirects.TryGetValue(group.NormGamePath!, out var existingGame))
        {
            var existingDisk = StagingPathFor(stagingDir, existingGame);
            if (File.Exists(existingDisk)) buf = LoadRgbaResized(existingDisk, w, h);
        }
        buf ??= LoadRgbaResized(group.OrigNormDiskPath ?? group.NormDiskPath ?? group.NormGamePath!, w, h);
        if (buf == null) return;

        var result = CpuUvComposite(layers, buf, w, h,
            outputBuffer: buf, targetFilter: TargetMap.Normal, preserveAlpha: true);
        if (result == null) return;

        var normOut = WriteStagingTex(stagingDir, group.NormGamePath!, result, w, h);
        redirects[group.NormGamePath!] = normOut;
    }

    private void ApplyUserMaskOverlayForExport(TargetGroup group, List<DecalLayer> layers,
        string stagingDir, Dictionary<string, string> redirects)
    {
        if (!AnyTargetMapLayer(layers, TargetMap.Mask)) return;
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return;

        var mtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;
        var maskGamePath = GetMaskGamePathFromMtrl(group.MtrlGamePath!, mtrlDisk);
        if (string.IsNullOrEmpty(maskGamePath)) return;

        byte[]? buf = null;
        int mw = 0, mh = 0;
        if (redirects.TryGetValue(maskGamePath!, out var existingGame))
        {
            var existingDisk = StagingPathFor(stagingDir, existingGame);
            if (File.Exists(existingDisk))
            {
                var img = imageLoader.LoadImage(existingDisk);
                if (img != null) { buf = (byte[])img.Value.Data.Clone(); mw = img.Value.Width; mh = img.Value.Height; }
            }
        }
        if (buf == null)
        {
            var img = LoadGameTexture(maskGamePath!);
            if (img == null) return;
            buf = (byte[])img.Value.Data.Clone();
            mw = img.Value.Width;
            mh = img.Value.Height;
        }

        var result = CpuUvComposite(layers, buf, mw, mh,
            outputBuffer: buf, targetFilter: TargetMap.Mask);
        if (result == null) return;

        var maskOut = WriteStagingTex(stagingDir, maskGamePath!, result, mw, mh);
        redirects[maskGamePath!] = maskOut;
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

    // One-shot CanSwap=NO log gating  -- avoid spamming the log on every poll
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
        // No blanket initializedRedirects check: per-group requirements drive the
        // decision. A project where every group contributes nothing (invisible,
        // Mask-only on sampler-less mtrl, etc.) trivially returns true -- async
        // would just emit an empty batch, which is fine.
        foreach (var group in project.Groups)
        {
            if (string.IsNullOrEmpty(group.DiffuseGamePath) || group.Layers.Count == 0)
                continue;

            // Required redirects depend on layer composition.
            bool needsDiffuse = false, needsNorm = false, needsMask = false;
            foreach (var l in group.Layers)
            {
                if (!l.IsVisible || string.IsNullOrEmpty(l.ImagePath)) continue;
                if (l.TargetMap == TargetMap.Diffuse && l.AffectsDiffuse) needsDiffuse = true;
                else if (l.TargetMap == TargetMap.Normal) needsNorm = true;
                else if (l.TargetMap == TargetMap.Mask) needsMask = true;
            }

            var hasEmissiveLayers = group.HasEmissiveLayers();
            var hasPbrLayers = group.HasPbrLayers() && MaterialSupportsPbr(group);

            // Group that contributes nothing this cycle  -- no need to gate CanSwap on it.
            if (!needsDiffuse && !needsNorm && !needsMask && !hasEmissiveLayers && !hasPbrLayers)
                continue;

            if (needsDiffuse)
            {
                if (!previewDiskPaths.ContainsKey(group.DiffuseGamePath))
                {
                    LogCanSwapDeny($"new group needs full redraw: {group.DiffuseGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }
                if (!initializedRedirects.ContainsKey(group.DiffuseGamePath))
                {
                    LogCanSwapDeny($"diffuse not initialized: {group.DiffuseGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }
            }

            if (needsNorm && !string.IsNullOrEmpty(group.NormGamePath))
            {
                if (!previewDiskPaths.ContainsKey(group.NormGamePath))
                {
                    LogCanSwapDeny($"new norm-only group needs full redraw: {group.NormGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }
                if (!initializedRedirects.ContainsKey(group.NormGamePath))
                {
                    LogCanSwapDeny($"norm not initialized: {group.NormGamePath}");
                    CanSwapInPlace = false;
                    return false;
                }
            }

            if (hasEmissiveLayers || hasPbrLayers)
            {
                if (!string.IsNullOrEmpty(group.MtrlGamePath)
                    && !previewDiskPaths.ContainsKey(group.MtrlGamePath))
                {
                    LogCanSwapDeny($"PBR/emissive needs mtrl init: {group.MtrlGamePath} (diskPaths={previewDiskPaths.Count} initRedir={initializedRedirects.Count})");
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
            if (hasPbrLayers)
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

        lastCanSwapDenyReason = null;
        CanSwapInPlace = true;
        return true;
    }

    // ── Private: shared helpers ──────────────────────────────────────────────

    private (byte[] Data, int Width, int Height) LoadBaseTexture(TargetGroup group)
    {
        var diffuseDisk = group.OrigDiffuseDiskPath ?? group.DiffuseDiskPath;
        if (string.IsNullOrEmpty(diffuseDisk))
        {
            return (new byte[1024 * 1024 * 4], 1024, 1024);
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

        // Don't cache the empty fallback  -- next call should retry the load
        return (new byte[1024 * 1024 * 4], 1024, 1024);
    }

    private void ProcessGroup(TargetGroup group, Dictionary<string, string> redirects)
    {
        var baseTex = LoadBaseTexture(group);
        int w = baseTex.Width, h = baseTex.Height;

        // Diffuse composite
        // GPU diffuse  -- only paints layers with AffectsDiffuse=true
        var diffResult = CpuUvComposite(group.Layers, baseTex.Data, w, h);

        // 3D editor preview diffuse  -- paints ALL visible layers with images so the user
        // sees decal placement even when AffectsDiffuse is off (placement guide).
        // Skip the second pass when no layer is "preview-only" (perf optimization).
        bool hasPreviewOnly = false;
        foreach (var l in group.Layers)
        {
            if (!l.IsVisible || string.IsNullOrEmpty(l.ImagePath)) continue;
            // Needs preview pass if the layer doesn't paint onto GPU diffuse:
            // AffectsDiffuse=false, or TargetMap is Mask/Normal.
            if (l.TargetMap != TargetMap.Diffuse || !l.AffectsDiffuse)
            { hasPreviewOnly = true; break; }
        }
        var diffPreview = hasPreviewOnly
            ? CpuUvComposite(group.Layers, baseTex.Data, w, h, ignoreAffectsDiffuseFilter: true)
            : diffResult;

        // Emissive-only / PBR-only / WholeMaterial-only groups produce no diffuse delta but
        // still need the redirect pipeline to engage so CheckCanSwapInPlace's diffuse-init
        // gate passes on subsequent slider drags (otherwise every drag falls back to Full
        // Redraw). Synthesize a passthrough by cloning vanilla diffuse.
        //
        // Must ignore IsVisible: when the user hides all emissive layers, HasEmissiveLayers()
        // returns false and the passthrough is skipped, breaking CanSwap on next drag. The
        // group is still emissive-configured — Penumbra redirect, mtrl hook, and CT all
        // remain active (emissive values just go to zero when nothing is visible).
        bool hasEmissiveConfigured = false;
        bool hasPbrConfigured = false;
        foreach (var l in group.Layers)
        {
            if (string.IsNullOrEmpty(l.ImagePath)) continue;
            if (l.TargetMap != TargetMap.Diffuse) continue;
            if (l.AffectsEmissive) hasEmissiveConfigured = true;
            if (l.RequiresRowPair) hasPbrConfigured = true;
        }
        bool needsPassthrough = hasEmissiveConfigured
                                || (hasPbrConfigured && MaterialSupportsPbr(group));
        if (diffResult == null && needsPassthrough)
        {
            diffResult = (byte[])baseTex.Data.Clone();
        }
        if (diffPreview == null && needsPassthrough)
            diffPreview = (byte[])baseTex.Data.Clone();

        // 3D editor reads compositeResults  -- feed it the preview version.
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
            var safeName = MakeSafeFileName(group.DiffuseGamePath!);
            var path = Path.Combine(outputDir, $"preview_{safeName}_d.tex");
            WriteBgraTexFile(path, diffResult, w, h);
            redirects[group.DiffuseGamePath!] = path;
        }

        // Emissive / PBR: modify .mtrl, write emissive into norm.a, and mount ColorTable.
        var hasEmissive = group.HasEmissiveLayers();
        var hasPbr = group.HasPbrLayers() && MaterialSupportsPbr(group);
        bool isSkinMtrl = IsSkinMaterial(group);
        bool useSkinColorTable = hasEmissive && patchedSkinShpkPath != null && isSkinMtrl;

        if (hasEmissive)
        {
            DebugServer.AppendLog($"[Emissive] {group.Name}: isSkin={isSkinMtrl} patchedShpk={patchedSkinShpkPath != null}" +
                $" norm={!string.IsNullOrEmpty(group.NormGamePath)}/{!string.IsNullOrEmpty(group.NormDiskPath)}" +
                $" mtrl={group.MtrlGamePath}");
        }

        if (useSkinColorTable)
        {
            skinCtMaterials[group.MtrlGamePath!] = 1;
            DebugServer.AppendLog($"[SkinCT] Using ColorTable path for {group.Name}");
        }

        if ((hasEmissive || hasPbr) && !string.IsNullOrEmpty(group.MtrlGamePath))
        {
            var safeName = MakeSafeFileName(group.DiffuseGamePath!);
            var mtrlOutPath = Path.Combine(outputDir, $"preview_{safeName}.mtrl");
            var mtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;

            if (useSkinColorTable)
            {
                // skin.shpk + patched shader: per-layer emissive via ColorTable rows.
                // 1) Allocate row pairs for each emissive layer.
                // Reserve row pair 0 as "default no emissive" — non-decal areas
                // have normal.alpha=0 which maps to row 0, so it must stay black.
                var alloc = GetOrCreateAllocator(group);
                if (!alloc.Scanned)
                {
                    alloc.ScanVanillaOccupation(new byte[] { 0, 0, 0, 0 }, 1, 1);
                    alloc.TryAllocate(); // consume slot 0
                }
                foreach (var layer in group.Layers)
                {
                    if (layer.IsVisible && layer.TargetMap == TargetMap.Diffuse
                        && layer.AffectsEmissive && layer.AllocatedRowPair < 0)
                        alloc.TryAllocate(layer);
                }

                // 2) Build per-layer ColorTable
                var ctBytes = MtrlFileWriter.BuildSkinColorTablePerLayer(group.Layers);

                // Diagnostic: dump row pair allocations and CT emissive values
                foreach (var layer in group.Layers)
                {
                    if (layer.IsVisible && layer.TargetMap == TargetMap.Diffuse && layer.AffectsEmissive)
                        DebugServer.AppendLog($"[SkinCT] Layer '{layer.Name}' rowPair={layer.AllocatedRowPair} em=({layer.EmissiveColor.X:F2},{layer.EmissiveColor.Y:F2},{layer.EmissiveColor.Z:F2})*{layer.EmissiveIntensity:F1}");
                }

                // 3) Build emissive mtrl with embedded ColorTable
                var emissiveColor = GetCombinedEmissiveColor(group.Layers);
                var mtrlSource = mtrlDisk ?? group.MtrlGamePath!;
                if (TryBuildEmissiveMtrlWithColorTable(
                        mtrlSource, mtrlOutPath, emissiveColor, ctBytes, out var emOffset))
                {
                    redirects[group.MtrlGamePath!] = mtrlOutPath;
                    previewMtrlDiskPaths[group.MtrlGamePath!] = mtrlOutPath;
                    if (emOffset >= 0)
                        emissiveOffsets[group.MtrlGamePath!] = emOffset;
                    // Verify mtrl header
                    try
                    {
                        var hdr = File.ReadAllBytes(mtrlOutPath);
                        int dsz = hdr[6] | (hdr[7] << 8);
                        int addlSz = hdr[15];
                        int colCnt = hdr[14];
                        DebugServer.AppendLog($"[SkinCT] Built CT mtrl for {group.Name}: dataSetSize={dsz} addlDataSize={addlSz} colorSetCount={colCnt} fileLen={hdr.Length}");
                    }
                    catch { DebugServer.AppendLog($"[SkinCT] Built CT mtrl for {group.Name}"); }
                }
                else
                {
                    DebugServer.AppendLog($"[SkinCT] FAILED to build CT mtrl for {group.Name} src={mtrlSource}");
                }

                // 4) Write row pair INDEX to normal.alpha (not emissive mask)
                if (!string.IsNullOrEmpty(group.NormGamePath))
                {
                    var normDisk = group.OrigNormDiskPath ?? group.NormDiskPath ?? group.NormGamePath;
                    var normResult = CompositeRowIndexNorm(group.Layers, normDisk!, w, h);
                    if (normResult != null)
                    {
                        var normPath = Path.Combine(outputDir, $"preview_{safeName}_n.tex");
                        WriteBgraTexFile(normPath, normResult, w, h);
                        redirects[group.NormGamePath!] = normPath;
                    }
                }
            }
            else
            {
                // Original paths: legacy emissive (CBuffer) + character.shpk PBR
                var emissiveColor = GetCombinedEmissiveColor(group.Layers);

                if (TryBuildEmissiveMtrl(mtrlDisk ?? group.MtrlGamePath!, mtrlOutPath, emissiveColor, out var emOffset))
                {
                    redirects[group.MtrlGamePath!] = mtrlOutPath;
                    previewMtrlDiskPaths[group.MtrlGamePath!] = mtrlOutPath;
                    if (emOffset >= 0)
                        emissiveOffsets[group.MtrlGamePath!] = emOffset;
                }

                if (hasEmissive && !string.IsNullOrEmpty(group.NormGamePath))
                {
                    var normDisk = group.OrigNormDiskPath ?? group.NormDiskPath ?? group.NormGamePath;
                    var normResult = CompositeEmissiveNorm(group.Layers, normDisk!, w, h);
                    if (normResult != null)
                    {
                        var normPath = Path.Combine(outputDir, $"preview_{safeName}_n.tex");
                        WriteBgraTexFile(normPath, normResult, w, h);
                        redirects[group.NormGamePath!] = normPath;
                    }
                }

                if (hasEmissive && IsIrisMaterial(group))
                {
                    var irisMtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;
                    var maskGamePath = GetMaskGamePathFromMtrl(group.MtrlGamePath!, irisMtrlDisk);
                    if (!string.IsNullOrEmpty(maskGamePath))
                    {
                        var maskTex = LoadGameTexture(maskGamePath);
                        if (maskTex != null)
                        {
                            var (maskData, mw, mh) = maskTex.Value;
                            var maskPatched = CompositeIrisMask(group.Layers, maskData, mw, mh);
                            if (maskPatched != null)
                            {
                                var maskPath = Path.Combine(outputDir, $"preview_{safeName}_mask.tex");
                                WriteBgraTexFile(maskPath, maskPatched, mw, mh);
                                redirects[maskGamePath] = maskPath;
                            }
                        }
                    }
                }

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
                            baseTextureCache[indexPath] = (data, iw, ih);
                        }
                    }
                }
            }
        }

        ApplyUserNormalOverlay(group, w, h, redirects);
        ApplyUserMaskOverlay(group, redirects);
    }

    private static bool AnyTargetMapLayer(List<DecalLayer> layers, TargetMap target)
    {
        foreach (var l in layers)
            if (l.IsVisible && l.TargetMap == target && !string.IsNullOrEmpty(l.ImagePath))
                return true;
        return false;
    }

    /// <summary>
    /// Load a texture (disk or game sqpack) into a fresh RGBA buffer resized to (w,h).
    /// Returns null on failure.
    /// </summary>
    private byte[]? LoadRgbaResized(string diskOrGamePath, int w, int h)
    {
        (byte[] Data, int Width, int Height)? img;
        if (File.Exists(diskOrGamePath))
            img = imageLoader.LoadImage(diskOrGamePath);
        else
            img = LoadGameTexture(diskOrGamePath);
        if (img == null) return null;
        var (data, iw, ih) = img.Value;
        if (iw == w && ih == h) return (byte[])data.Clone();
        return ResizeBilinear(data, iw, ih, w, h);
    }

    /// <summary>In-buffer variant of <see cref="LoadRgbaResized"/>. Returns false on failure.</summary>
    private bool LoadRgbaResizedInto(string diskOrGamePath, int w, int h, byte[] output)
    {
        (byte[] Data, int Width, int Height)? img;
        if (File.Exists(diskOrGamePath))
            img = imageLoader.LoadImage(diskOrGamePath);
        else
            img = LoadGameTexture(diskOrGamePath);
        if (img == null) return false;
        var (data, iw, ih) = img.Value;
        int needLen = w * h * 4;
        if (iw == w && ih == h)
            Buffer.BlockCopy(data, 0, output, 0, Math.Min(needLen, data.Length));
        else
            Buffer.BlockCopy(ResizeBilinear(data, iw, ih, w, h), 0, output, 0, needLen);
        return true;
    }

    /// <summary>
    /// Paint user Normal-target layers' RGB onto the group's normal output.
    /// Preserves alpha (which may hold emissive mask or ColorTable row index).
    /// Chains on top of any existing normal redirect so alpha work from emissive
    /// pipelines survives.
    /// </summary>
    private void ApplyUserNormalOverlay(TargetGroup group, int w, int h,
        Dictionary<string, string> redirects)
    {
        if (!AnyTargetMapLayer(group.Layers, TargetMap.Normal)) return;
        if (string.IsNullOrEmpty(group.NormGamePath))
        {
            DebugServer.AppendLog($"[NormOverlay] {group.Name}: skip (no NormGamePath)");
            return;
        }

        byte[]? buf = null;
        if (redirects.TryGetValue(group.NormGamePath!, out var existing) && File.Exists(existing))
            buf = LoadRgbaResized(existing, w, h);
        var srcPath = group.OrigNormDiskPath ?? group.NormDiskPath ?? group.NormGamePath!;
        buf ??= LoadRgbaResized(srcPath, w, h);
        if (buf == null)
        {
            DebugServer.AppendLog($"[NormOverlay] {group.Name}: skip (LoadRgbaResized null, src={srcPath})");
            return;
        }

        var result = CpuUvComposite(group.Layers, buf, w, h,
            outputBuffer: buf,
            targetFilter: TargetMap.Normal,
            preserveAlpha: true);
        if (result == null)
        {
            DebugServer.AppendLog($"[NormOverlay] {group.Name}: skip (CpuUvComposite null)");
            return;
        }

        var safeName = MakeSafeFileName(group.DiffuseGamePath!);
        var normPath = Path.Combine(outputDir, $"preview_{safeName}_n.tex");
        WriteBgraTexFile(normPath, result, w, h);
        redirects[group.NormGamePath!] = normPath;
        DebugServer.AppendLog($"[NormOverlay] {group.Name}: wrote {group.NormGamePath} -> {normPath}");
    }

    /// <summary>
    /// Paint user Mask-target layers' RGB onto the material's mask texture.
    /// Looks up the mask game path via the material's g_SamplerMask binding.
    /// Chains on top of any existing mask redirect (e.g. iris emissive mask).
    /// </summary>
    private void ApplyUserMaskOverlay(TargetGroup group, Dictionary<string, string> redirects)
    {
        if (!AnyTargetMapLayer(group.Layers, TargetMap.Mask)) return;
        DebugServer.AppendLog($"[MaskOverlay] {group.Name}: entry");
        if (string.IsNullOrEmpty(group.MtrlGamePath))
        {
            DebugServer.AppendLog($"[MaskOverlay] {group.Name}: skip (no MtrlGamePath)");
            return;
        }

        var mtrlDisk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;
        var maskGamePath = GetMaskGamePathFromMtrl(group.MtrlGamePath!, mtrlDisk);
        if (string.IsNullOrEmpty(maskGamePath))
        {
            DebugServer.AppendLog($"[MaskOverlay] {group.Name}: skip (mask sampler not in mtrl; skin body mtrls usually have no g_SamplerMask)");
            return;
        }

        byte[]? buf = null;
        int mw = 0, mh = 0;
        if (redirects.TryGetValue(maskGamePath!, out var existing) && File.Exists(existing))
        {
            var img = imageLoader.LoadImage(existing);
            if (img != null) { buf = (byte[])img.Value.Data.Clone(); mw = img.Value.Width; mh = img.Value.Height; }
        }
        if (buf == null)
        {
            var img = LoadGameTexture(maskGamePath!);
            if (img == null)
            {
                DebugServer.AppendLog($"[MaskOverlay] {group.Name}: skip (LoadGameTexture null for {maskGamePath})");
                return;
            }
            buf = (byte[])img.Value.Data.Clone();
            mw = img.Value.Width;
            mh = img.Value.Height;
        }

        var result = CpuUvComposite(group.Layers, buf, mw, mh,
            outputBuffer: buf,
            targetFilter: TargetMap.Mask);
        if (result == null)
        {
            DebugServer.AppendLog($"[MaskOverlay] {group.Name}: skip (CpuUvComposite null)");
            return;
        }

        var safeName = MakeSafeFileName(group.DiffuseGamePath!);
        var maskPath = Path.Combine(outputDir, $"preview_{safeName}_mask.tex");
        WriteBgraTexFile(maskPath, result, mw, mh);
        redirects[maskGamePath!] = maskPath;
        DebugServer.AppendLog($"[MaskOverlay] {group.Name}: wrote {maskGamePath} -> {maskPath}");
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

        // Zero out alpha channel everywhere  -- bound by needLen, not output.Length, since
        // outputBuffer may be a reused over-sized scratch buffer.
        for (int i = 3; i < needLen; i += 4)
            output[i] = 0;

        bool anyEmissive = false;
        foreach (var layer in layers)
        {
            if (!layer.IsVisible || layer.TargetMap != TargetMap.Diffuse || !layer.AffectsEmissive || string.IsNullOrEmpty(layer.ImagePath)) continue;

            var decalImage = imageLoader.LoadImage(layer.ImagePath);
            if (decalImage == null) continue;

            var (decalData, decalW, decalH) = decalImage.Value;
            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var opacity = layer.Opacity;
            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
            var cosR = MathF.Cos(rotRad);
            var sinR = MathF.Sin(rotRad);

            GetLayerPixelBounds(center, scale, w, h, out int pxMin, out int pxMax, out int pyMin, out int pyMax);

            // Per-row parallel  -- each row writes disjoint output bytes (alpha channel only).
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
    /// Write row pair index to normal.alpha for skin.shpk ColorTable mode.
    /// Each emissive layer paints its allocated row pair index (0-15 → 0,17,...255)
    /// into the normal map alpha channel. Non-covered pixels stay at 0 (row 0).
    /// </summary>
    private byte[]? CompositeRowIndexNorm(List<DecalLayer> layers, string normDiskPath, int w, int h,
        byte[]? outputBuffer = null)
    {
        int needLen = w * h * 4;
        var output = outputBuffer != null && outputBuffer.Length >= needLen
            ? outputBuffer
            : new byte[needLen];

        byte[]? cachedBytes = null;
        int cachedW = 0, cachedH = 0;
        if (baseTextureCache.TryGetValue(normDiskPath, out var cachedNorm))
            (cachedBytes, cachedW, cachedH) = cachedNorm;
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
            Array.Clear(output, 0, needLen);
        else if (cachedW != w || cachedH != h)
            Buffer.BlockCopy(ResizeBilinear(cachedBytes, cachedW, cachedH, w, h), 0, output, 0, needLen);
        else
            Buffer.BlockCopy(cachedBytes, 0, output, 0, needLen);

        // Zero alpha everywhere first (row 0 = default, no emissive)
        for (int i = 3; i < needLen; i += 4)
            output[i] = 0;

        bool anyPainted = false;
        foreach (var layer in layers)
        {
            if (!layer.IsVisible || layer.TargetMap != TargetMap.Diffuse || !layer.AffectsEmissive || layer.AllocatedRowPair < 0) continue;
            if (string.IsNullOrEmpty(layer.ImagePath)) continue;

            var decalImage = imageLoader.LoadImage(layer.ImagePath);
            if (decalImage == null) continue;

            byte rowByte = (byte)Math.Clamp(layer.AllocatedRowPair * 17, 0, 255);
            var (decalData, decalW, decalH) = decalImage.Value;
            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var opacity = layer.Opacity;
            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
            var cosR = MathF.Cos(rotRad);
            var sinR = MathF.Sin(rotRad);

            GetLayerPixelBounds(center, scale, w, h, out int pxMin, out int pxMax, out int pyMin, out int pyMax);

            var layerLocal = layer;
            var rowByteLocal = rowByte;
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

                    // Fade mask controls emissive coverage boundary shape.
                    // Row index is discrete so we can't do smooth alpha gradients,
                    // but the fade mask determines WHERE the boundary falls.
                    float maskValue;
                    if (layerLocal.FadeMask == LayerFadeMask.DirectionalGradient)
                        maskValue = ComputeDirectionalGradient(ru, rv, da,
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
                        maskValue = ComputeShapeOutline(da, layerLocal.FadeMaskFalloff, sum / cnt);
                    }
                    else
                        maskValue = ComputeFadeMaskWeight(layerLocal.FadeMask, layerLocal.FadeMaskFalloff, ru, rv, da);

                    if (maskValue < 0.5f) continue;

                    output[(py * w + px) * 4 + 3] = rowByteLocal;
                }
            });
            anyPainted = true;
        }

        // Diagnostic removed (was per-frame spam)
        return anyPainted ? output : null;
    }

    private byte[]? CompositeIrisMask(List<DecalLayer> layers, byte[] maskData, int mw, int mh)
    {
        var output = (byte[])maskData.Clone();
        int len = mw * mh * 4;

        for (int i = 0; i < len; i += 4)
            output[i] = 0;

        bool any = false;
        foreach (var layer in layers)
        {
            if (!layer.IsVisible || layer.TargetMap != TargetMap.Diffuse || !layer.AffectsEmissive || string.IsNullOrEmpty(layer.ImagePath)) continue;

            var decalImage = imageLoader.LoadImage(layer.ImagePath);
            if (decalImage == null) continue;

            var (decalData, decalW, decalH) = decalImage.Value;
            var center = layer.UvCenter;
            var scale = layer.UvScale;
            var opacity = layer.Opacity;
            var rotRad = layer.RotationDeg * (MathF.PI / 180f);
            var cosR = MathF.Cos(rotRad);
            var sinR = MathF.Sin(rotRad);

            GetLayerPixelBounds(center, scale, mw, mh, out int pxMin, out int pxMax, out int pyMin, out int pyMax);

            var layerLocal = layer;
            ParallelRows(pyMin, pyMax, py =>
            {
                for (int px = pxMin; px <= pxMax; px++)
                {
                    float u = (px + 0.5f) / mw;
                    float v = (py + 0.5f) / mh;
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

                    int oIdx = (py * mw + px) * 4;
                    byte irisBlue = output[oIdx + 2];
                    byte emByte = (byte)Math.Clamp((int)(maskValue * (irisBlue / 255f) * 255), 0, 255);
                    output[oIdx] = (byte)Math.Max(output[oIdx], emByte);
                }
            });

            any = true;
        }

        return any ? output : null;
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
        // Get base index bytes  -- must NOT mutate the cached entry. When sizes match we
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

            GetLayerPixelBounds(center, scale, w, h, out int pxMin, out int pxMax, out int pyMin, out int pyMax);

            // Per-row parallel  -- each row writes disjoint output pixels. anyWritten is a
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
            if (!layer.IsVisible || layer.TargetMap != TargetMap.Diffuse || !layer.AffectsEmissive || string.IsNullOrEmpty(layer.ImagePath)) continue;
            color += layer.EmissiveColor * layer.EmissiveIntensity;
        }
        return color;
    }

    /// <summary>Get combined emissive color for all visible emissive layers in a group.</summary>
    public Vector3 GetCombinedEmissiveColorForGroup(TargetGroup group) =>
        GetCombinedEmissiveColor(group.Layers);

    // Picks the first visible emissive layer's animation params. For single-layer groups
    // (iris etc.) this is exact; for legacy multi-layer skin fallbacks it is a best-effort
    // approximation — skin.shpk CT path handles per-layer anim directly and does not reach here.
    private static (EmissiveAnimMode Mode, float Speed, float Amp, Vector3 ColorB) GetDominantEmissiveAnim(List<DecalLayer> layers)
    {
        foreach (var l in layers)
        {
            if (!l.IsVisible || l.TargetMap != TargetMap.Diffuse || !l.AffectsEmissive || string.IsNullOrEmpty(l.ImagePath)) continue;
            if (l.AnimMode == EmissiveAnimMode.None) continue;
            return (l.AnimMode, l.AnimSpeed, l.AnimAmplitude, l.EmissiveColorB * l.EmissiveIntensity);
        }
        return (EmissiveAnimMode.None, 0f, 0f, Vector3.Zero);
    }

    private bool TryBuildEmissiveMtrlWithColorTable(string mtrlPath, string outputPath,
        Vector3 emissiveColor, byte[] colorTableBytes, out int emissiveByteOffset)
    {
        emissiveByteOffset = -1;
        try
        {
            byte[] mtrlBytes;
            if (File.Exists(mtrlPath))
                mtrlBytes = File.ReadAllBytes(mtrlPath);
            else
            {
                var pack = meshExtractor.GetSqPackInstance();
                if (pack == null) return false;
                var sqResult = pack.GetFile(mtrlPath);
                if (sqResult == null) return false;
                mtrlBytes = sqResult.Value.file.RawData.ToArray();
            }

            var tempPath = Path.Combine(outputDir, $"temp_{Guid.NewGuid():N}.mtrl");
            File.WriteAllBytes(tempPath, mtrlBytes);
            var lumina = meshExtractor.GetLuminaForDisk();
            var mtrl = lumina!.GetFileFromDisk<MtrlFile>(tempPath);
            try { File.Delete(tempPath); } catch { }

            return MtrlFileWriter.WriteEmissiveMtrlWithColorTable(
                mtrl, mtrlBytes, outputPath, emissiveColor, colorTableBytes, out emissiveByteOffset);
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[PreviewService] EmissiveMtrl+CT error: {ex.Message}");
            return false;
        }
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

    private bool IsSkinMaterial(TargetGroup group)
    {
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return false;
        var disk = group.OrigMtrlDiskPath ?? group.MtrlDiskPath;

        byte[]? bytes = null;
        if (!string.IsNullOrEmpty(disk) && File.Exists(disk))
        {
            try { bytes = File.ReadAllBytes(disk); } catch { }
        }
        if (bytes == null)
        {
            try
            {
                var pack = meshExtractor.GetSqPackInstance();
                var sqResult = pack?.GetFile(group.MtrlGamePath!);
                if (sqResult != null)
                    bytes = sqResult.Value.file.RawData.ToArray();
            }
            catch { }
        }
        if (bytes == null || bytes.Length < 16) return false;

        try
        {
            int shpkOff = bytes[10] | (bytes[11] << 8);
            int texC = bytes[12]; int uvC = bytes[13]; int colC = bytes[14];
            int strStart = 16 + texC * 4 + uvC * 4 + colC * 4;
            int nameStart = strStart + shpkOff;
            if (nameStart >= bytes.Length) return false;
            int end = nameStart;
            while (end < bytes.Length && bytes[end] != 0) end++;
            return System.Text.Encoding.UTF8.GetString(bytes, nameStart, end - nameStart) == "skin.shpk";
        }
        catch { return false; }
    }

    // Rename approach: the patched shpk is deployed under a NEW game path so the engine
    // sees a cache miss on first load and routes through Penumbra's redirect. The vanilla
    // skin.shpk stays put (other non-emissive skin mtrls still reference it unchanged).
    private const string SkinShpkGamePath = "shader/sm5/shpk/skin_ct.shpk";
    private const string VanillaSkinShpkGamePath = "shader/sm5/shpk/skin.shpk";
    private string? patchedSkinShpkPath;
    private bool skinShpkConflictChecked;
    private bool shpkNodeDumped;

    /// <summary>
    /// Non-null when another Penumbra mod redirects skin.shpk before our temp mod.
    /// UI should display this as a warning to the user.
    /// </summary>
    public string? SkinShpkModConflict { get; private set; }

    /// <summary>
    /// If any group uses emissive, deploy the patched skin.shpk that supports ColorTable sampling.
    /// The patched shader replaces the emissive calculation: instead of a uniform CBuffer color,
    /// it reads per-row emissive RGB from g_SamplerTable (t10) bound to the ColorTable texture.
    /// </summary>
    private void TryDeployPatchedSkinShpk(DecalProject project, Dictionary<string, string> redirects)
    {
        bool hasEmissive = false;
        foreach (var group in project.Groups)
        {
            if (group.HasEmissiveLayers()) { hasEmissive = true; break; }
        }
        if (!hasEmissive) return;

        // One-shot conflict check: before our first redirect, see if another mod
        // already replaces skin.shpk. Our temp mod (priority 99) will override it,
        // but warn the user that the other mod's skin.shpk changes are being replaced.
        if (!skinShpkConflictChecked)
        {
            skinShpkConflictChecked = true;
            try
            {
                var resolved = penumbra.ResolvePlayer(VanillaSkinShpkGamePath);
                if (!string.IsNullOrEmpty(resolved)
                    && !resolved.Equals(VanillaSkinShpkGamePath, StringComparison.OrdinalIgnoreCase))
                {
                    SkinShpkModConflict = resolved;
                    DebugServer.AppendLog($"[ShpkPatch] Conflict: vanilla skin.shpk redirected by another mod -> {resolved}");
                }
            }
            catch { }
        }

        if (patchedSkinShpkPath == null || !File.Exists(patchedSkinShpkPath))
        {
            // v6: Ripple direction modes (radial/linear/bidir) + dual-color ripple.
            // Payload 220 → 276 tokens with new col 6 sample and dir dispatch.
            var candidate = Path.Combine(outputDir, "skin_ct_v6.shpk");
            if (!File.Exists(candidate))
            {
                // Runtime patch: read vanilla skin.shpk from SqPack and patch in memory
                try
                {
                    var pack = meshExtractor.GetSqPackInstance();
                    var sqResult = pack?.GetFile(VanillaSkinShpkGamePath);
                    if (sqResult == null)
                    {
                        DebugServer.AppendLog("[ShpkPatch] Cannot read vanilla skin.shpk from SqPack");
                        return;
                    }
                    var vanillaBytes = sqResult.Value.file.RawData.ToArray();
                    var patched = SkinShpkPatcher.Patch(vanillaBytes);
                    if (patched == null)
                    {
                        DebugServer.AppendLog("[ShpkPatch] Runtime patching failed");
                        return;
                    }
                    File.WriteAllBytes(candidate, patched);
                    DebugServer.AppendLog($"[ShpkPatch] Runtime-patched skin.shpk ({vanillaBytes.Length} -> {patched.Length} bytes)");
                }
                catch (Exception ex)
                {
                    DebugServer.AppendLog($"[ShpkPatch] Runtime patch failed: {ex.Message}");
                    return;
                }
            }
            patchedSkinShpkPath = candidate;
        }

        redirects[SkinShpkGamePath] = patchedSkinShpkPath;
        DebugServer.AppendLog("[ShpkPatch] Deployed patched skin.shpk for ColorTable emissive");

        // One-shot NodeSelector dump: parse the patched skin.shpk so we can see if
        // (ValueEmissive, ValueDecalEmissive, ValueVertexColorEmissive) routes to PS[19].
        // Runs even if the file was cached from a prior session.
        if (!shpkNodeDumped)
        {
            shpkNodeDumped = true;
            try
            {
                var bytes = File.ReadAllBytes(patchedSkinShpkPath);
                SkinShpkPatcher.DumpFromBytes(bytes);
            }
            catch (Exception ex) { DebugServer.AppendLog($"[ShpkPatch] Dump failed: {ex.Message}"); }
        }

        // Ensure EmissiveCBufferHook is enabled so skin-family materials flow through
        // the detour for one-shot ShaderPackage diagnostics (even if no CBuffer target
        // is registered — skin CT materials skip SetTargetByPath entirely).
        emissiveHook?.EnableForDiagnostics();
    }

    /// <summary>
    /// Quick binary check: is this a skin.shpk material with a non-zero DataSetSize?
    /// Vanilla skin.shpk materials have DataSetSize=0. Mods like Eve add ColorTable
    /// data for pore/glossy effects, which is incompatible with the emissive variant.
    /// </summary>
    private static bool HasSkinShpkColorTable(byte[] mtrlBytes)
    {
        if (mtrlBytes.Length < 16) return false;

        // DataSetSize is the upper 16 bits of the uint32 at offset 4
        int dataSetSize = mtrlBytes[6] | (mtrlBytes[7] << 8);
        if (dataSetSize == 0) return false;

        // Parse header to find shader package name in string table
        int shpkNameOffset = mtrlBytes[10] | (mtrlBytes[11] << 8);
        int texCount = mtrlBytes[12];
        int uvCount = mtrlBytes[13];
        int colorCount = mtrlBytes[14];

        int stringsStart = 16 + texCount * 4 + uvCount * 4 + colorCount * 4;
        int nameStart = stringsStart + shpkNameOffset;
        if (nameStart >= mtrlBytes.Length) return false;

        // Read null-terminated shader name
        int end = nameStart;
        while (end < mtrlBytes.Length && mtrlBytes[end] != 0) end++;
        var shpkName = System.Text.Encoding.UTF8.GetString(mtrlBytes, nameStart, end - nameStart);

        return shpkName == "skin.shpk";
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
            // useCache=false because each call uses a fresh GUID path  -- caching would
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
    /// image, regardless of AffectsDiffuse  -- used for the 3D editor preview where the user
    /// needs to see decal placement even when the layer doesn't actually paint the GPU diffuse.
    /// </summary>
    /// <summary>
    /// Compute the axis-aligned bbox of a decal layer in texture pixel coordinates.
    /// Matches the scan-range derivation inside the paint loops (non-rotated extents,
    /// same as the engine's own tile clamping). Rotation is handled per-pixel inside.
    /// </summary>
    private static void GetLayerPixelBounds(Vector2 uvCenter, Vector2 uvScale, int w, int h,
        out int pxMin, out int pxMax, out int pyMin, out int pyMax)
    {
        var halfW = MathF.Abs(uvScale.X) * 0.5f;
        var halfH = MathF.Abs(uvScale.Y) * 0.5f;
        pxMin = Math.Max(0, (int)((uvCenter.X - halfW) * w));
        pxMax = Math.Min(w - 1, (int)((uvCenter.X + halfW) * w));
        pyMin = Math.Max(0, (int)((uvCenter.Y - halfH) * h));
        pyMax = Math.Min(h - 1, (int)((uvCenter.Y + halfH) * h));
    }

    private static DirtyRect ComputeLayerBbox(Vector2 uvCenter, Vector2 uvScale, int w, int h)
    {
        GetLayerPixelBounds(uvCenter, uvScale, w, h, out int pxMin, out int pxMax, out int pyMin, out int pyMax);
        if (pxMax < pxMin || pyMax < pyMin) return DirtyRect.Empty;
        return new DirtyRect(pxMin, pyMin, pxMax - pxMin + 1, pyMax - pyMin + 1);
    }

    private byte[]? CpuUvComposite(List<DecalLayer> layers, byte[] baseRgba, int w, int h,
        bool ignoreAffectsDiffuseFilter = false, byte[]? outputBuffer = null,
        DirtyTracker? tracker = null,
        TargetMap targetFilter = TargetMap.Diffuse,
        bool preserveAlpha = false)
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
            if (!ignoreAffectsDiffuseFilter)
            {
                if (layer.TargetMap != targetFilter) continue;
                if (targetFilter == TargetMap.Diffuse && !layer.AffectsDiffuse) continue;
            }

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
        // work needed there. This is the core CPU saving: 4096^2 -> dirty W*H bytes.
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

            // Per-row parallelism  -- each row writes to non-overlapping output pixels.
            // Layer iteration stays sequential so blend modes still see prior layers.
            // Locals are captured by the lambda; that's fine  -- they're stable for this layer.
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

                    // Previously this clipped the diffuse to the emissive feather-mask
                    // boundary (mv >= 0.5) so the diffuse couldn't extend past the
                    // emissive region. That caused the underlying decal to disappear
                    // whenever the user combined "show decal" + emissive + a feather —
                    // and if emissive was black, nothing rendered at all.
                    // Now diffuse always paints the full decal shape; emissive coverage
                    // is independently controlled by the normal.a row-index composite.

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

                    output[oIdx] = (byte)Math.Clamp((int)((rr * da + br * (1 - da)) * 255), 0, 255);
                    output[oIdx + 1] = (byte)Math.Clamp((int)((rg * da + bg * (1 - da)) * 255), 0, 255);
                    output[oIdx + 2] = (byte)Math.Clamp((int)((rb * da + bb * (1 - da)) * 255), 0, 255);
                    if (!preserveAlpha) output[oIdx + 3] = 255;
                }
            });
        }

        tracker?.Commit(currentUnion, dirty);
        return output;
    }

    /// <summary>RGBA-input variant  -- used by Full Redraw / export paths that haven't switched to scratch buffers.</summary>
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

    /// <summary>BGRA-input variant  -- used by the inplace composite path with reusable scratch buffers.</summary>
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

    /// <summary>Compute shape outline mask  -- glow along decal alpha edges (like PS outer glow).</summary>
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
                dst[i] = (byte)Math.Clamp((int)(r * 255 + 0.5f), 0, 255);
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
    }

    public void ClearMesh()
    {
        currentMesh = null;
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
