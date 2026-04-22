using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using SkinTattoo.Core;
using SkinTattoo.Http;

namespace SkinTattoo.Interop;

/// <summary>
/// Hooks OnRenderMaterial to modify g_EmissiveColor in real-time.
/// CPU-side CBuffer writes before render command submission -> GPU upload.
/// </summary>
public unsafe class EmissiveCBufferHook : IDisposable
{
    private const uint CrcEmissiveColor = 0x38A64362;

    private readonly IPluginLog log;
    private readonly Hook<OnRenderMaterialDelegate> hook;

    private struct TargetData
    {
        public Vector3 BaseColor;
        public Vector3 GradientColorB;
        public EmissiveAnimMode AnimMode;
        public float AnimSpeed;
        public float AnimAmplitude;
    }

    private readonly ConcurrentDictionary<nint, TargetData> targets = new();
    private readonly ConcurrentDictionary<nint, int> offsetCache = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly System.Collections.Generic.HashSet<string> loggedMisses = new(StringComparer.OrdinalIgnoreCase);

    private bool enabled;
    private int errorCount;

    private delegate nint OnRenderMaterialDelegate(
        nint modelRenderer, nint outFlags, nint param, nint material, uint materialIndex);

    public EmissiveCBufferHook(IGameInteropProvider interop, IPluginLog log)
    {
        this.log = log;
        hook = interop.HookFromSignature<OnRenderMaterialDelegate>(
            "E8 ?? ?? ?? ?? 44 0F B7 28", Detour);
    }

    public void Enable()
    {
        if (!enabled)
        {
            hook.Enable();
            enabled = true;
            DebugServer.AppendLog("[EmissiveHook] Enabled");
        }
    }

    public void Disable()
    {
        if (enabled)
        {
            hook.Disable();
            enabled = false;
            DebugServer.AppendLog("[EmissiveHook] Disabled");
        }
    }

    /// <summary>Register a target by matching MaterialResourceHandle path.</summary>
    public void SetTargetByPath(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* charBase,
        string mtrlGamePath, string? mtrlDiskPath, Vector3 emissiveColor,
        EmissiveAnimMode animMode = EmissiveAnimMode.None,
        float animSpeed = 0f, float animAmplitude = 0f,
        Vector3 gradientColorB = default)
    {
        if (charBase == null) return;

        var normGame = mtrlGamePath.Replace('\\', '/').ToLowerInvariant();
        var normDisk = mtrlDiskPath?.Replace('\\', '/').ToLowerInvariant();

        var slotCount = charBase->SlotCount;
        if (slotCount <= 0 || slotCount > 30) return;

        var models = charBase->Models;
        if (models == null) return;

        for (int s = 0; s < slotCount; s++)
        {
            var model = models[s];
            if (model == null) continue;

            var matCount = model->MaterialCount;
            if (matCount <= 0 || matCount > 20) continue;

            var mats = model->Materials;
            if (mats == null) continue;

            for (int m = 0; m < matCount; m++)
            {
                var mat = mats[m];
                if (mat == null) continue;

                var mrh = mat->MaterialResourceHandle;
                if (mrh == null) continue;

                string fileName;
                try { fileName = ((ResourceHandle*)mrh)->FileName.ToString(); }
                catch { continue; }

                if (string.IsNullOrEmpty(fileName)) continue;
                var normFile = fileName.Replace('\\', '/').ToLowerInvariant();

                bool matched = normFile.EndsWith(normGame) || normGame.EndsWith(normFile);
                if (!matched && normDisk != null)
                    matched = normFile.Contains(System.IO.Path.GetFileName(normDisk));

                if (matched)
                {
                    var key = (nint)mrh;
                    var data = new TargetData
                    {
                        BaseColor = emissiveColor,
                        GradientColorB = gradientColorB,
                        AnimMode = animMode,
                        AnimSpeed = animSpeed,
                        AnimAmplitude = animAmplitude,
                    };
                    if (targets.TryGetValue(key, out var existing)
                        && existing.BaseColor == data.BaseColor
                        && existing.GradientColorB == data.GradientColorB
                        && existing.AnimMode == data.AnimMode
                        && existing.AnimSpeed == data.AnimSpeed
                        && existing.AnimAmplitude == data.AnimAmplitude)
                        return;
                    targets[key] = data;
                    lock (loggedMisses) loggedMisses.Remove(mtrlGamePath);
                    if (!enabled) Enable();
                    DebugServer.AppendLog(
                        $"[EmissiveHook] Target set: {fileName} color=({emissiveColor.X:F2},{emissiveColor.Y:F2},{emissiveColor.Z:F2}) " +
                        $"anim={animMode} speed={animSpeed:F2} amp={animAmplitude:F2}");
                    return;
                }
            }
        }

        // Throttle the miss log: per-frame maintenance calls retry every frame until the
        // fresh MaterialResourceHandle appears. Log once per path per session.
        bool shouldLog;
        lock (loggedMisses) shouldLog = loggedMisses.Add(mtrlGamePath);
        if (shouldLog)
            DebugServer.AppendLog($"[EmissiveHook] Material not found: {mtrlGamePath}");
    }

    public void ClearTargets()
    {
        targets.Clear();
        offsetCache.Clear();
        if (enabled) Disable();
    }

    /// <summary>Remove hook target for a specific material by searching the path.</summary>
    public void ClearTargetByPath(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* charBase,
        string mtrlGamePath, string? mtrlDiskPath)
    {
        if (charBase == null) return;

        var normGame = mtrlGamePath.Replace('\\', '/').ToLowerInvariant();
        var normDisk = mtrlDiskPath?.Replace('\\', '/').ToLowerInvariant();

        var slotCount = charBase->SlotCount;
        if (slotCount <= 0 || slotCount > 30) return;

        var models = charBase->Models;
        if (models == null) return;

        for (int s = 0; s < slotCount; s++)
        {
            var model = models[s];
            if (model == null) continue;

            var matCount = model->MaterialCount;
            if (matCount <= 0 || matCount > 20) continue;

            var mats = model->Materials;
            if (mats == null) continue;

            for (int m = 0; m < matCount; m++)
            {
                var mat = mats[m];
                if (mat == null) continue;

                var mrh = mat->MaterialResourceHandle;
                if (mrh == null) continue;

                string fileName;
                try { fileName = ((ResourceHandle*)mrh)->FileName.ToString(); }
                catch { continue; }

                if (string.IsNullOrEmpty(fileName)) continue;
                var normFile = fileName.Replace('\\', '/').ToLowerInvariant();

                bool matched = normFile.EndsWith(normGame) || normGame.EndsWith(normFile);
                if (!matched && normDisk != null)
                    matched = normFile.Contains(System.IO.Path.GetFileName(normDisk));

                if (matched)
                {
                    targets.TryRemove((nint)mrh, out _);
                    offsetCache.TryRemove((nint)mrh, out _);
                    if (targets.IsEmpty && enabled) Disable();
                    return;
                }
            }
        }
    }

    /// <summary>Set emissive color for iris materials (_iri_a / _iri_b) on the character.</summary>
    public void SetIrisEmissive(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* charBase,
        Vector3 leftColor, Vector3 rightColor,
        EmissiveAnimMode animMode = EmissiveAnimMode.None,
        float animSpeed = 0f, float animAmplitude = 0f)
    {
        if (charBase == null) return;

        var slotCount = charBase->SlotCount;
        if (slotCount <= 0 || slotCount > 30) return;

        var models = charBase->Models;
        if (models == null) return;

        for (int s = 0; s < slotCount; s++)
        {
            var model = models[s];
            if (model == null) continue;

            var matCount = model->MaterialCount;
            if (matCount <= 0 || matCount > 20) continue;

            var mats = model->Materials;
            if (mats == null) continue;

            for (int m = 0; m < matCount; m++)
            {
                var mat = mats[m];
                if (mat == null) continue;

                var mrh = mat->MaterialResourceHandle;
                if (mrh == null) continue;

                string fileName;
                try { fileName = ((ResourceHandle*)mrh)->FileName.ToString(); }
                catch { continue; }

                if (string.IsNullOrEmpty(fileName)) continue;
                var lower = fileName.ToLowerInvariant();

                if (lower.Contains("_iri_a"))
                {
                    targets[(nint)mrh] = new TargetData
                    {
                        BaseColor = leftColor, AnimMode = animMode,
                        AnimSpeed = animSpeed, AnimAmplitude = animAmplitude,
                    };
                    if (!enabled) Enable();
                }
                else if (lower.Contains("_iri_b"))
                {
                    targets[(nint)mrh] = new TargetData
                    {
                        BaseColor = rightColor, AnimMode = animMode,
                        AnimSpeed = animSpeed, AnimAmplitude = animAmplitude,
                    };
                    if (!enabled) Enable();
                }
            }
        }
    }

    /// <summary>Remove iris material targets.</summary>
    public void ClearIrisTargets(
        FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* charBase)
    {
        if (charBase == null) return;

        var slotCount = charBase->SlotCount;
        if (slotCount <= 0 || slotCount > 30) return;

        var models = charBase->Models;
        if (models == null) return;

        for (int s = 0; s < slotCount; s++)
        {
            var model = models[s];
            if (model == null) continue;

            var matCount = model->MaterialCount;
            if (matCount <= 0 || matCount > 20) continue;

            var mats = model->Materials;
            if (mats == null) continue;

            for (int m = 0; m < matCount; m++)
            {
                var mat = mats[m];
                if (mat == null) continue;

                var mrh = mat->MaterialResourceHandle;
                if (mrh == null) continue;

                string fileName;
                try { fileName = ((ResourceHandle*)mrh)->FileName.ToString(); }
                catch { continue; }

                if (string.IsNullOrEmpty(fileName)) continue;
                var lower = fileName.ToLowerInvariant();

                if (lower.Contains("_iri_a") || lower.Contains("_iri_b"))
                {
                    targets.TryRemove((nint)mrh, out _);
                    offsetCache.TryRemove((nint)mrh, out _);
                }
            }
        }

        if (targets.IsEmpty && enabled) Disable();
    }

    public bool HasTargets => !targets.IsEmpty;

    public record struct DiagStats(int Targets, int OffsetCache, int LoggedMisses, bool Enabled);

    public DiagStats GetDiagStats()
    {
        int missCount;
        lock (loggedMisses) missCount = loggedMisses.Count;
        return new DiagStats(targets.Count, offsetCache.Count, missCount, enabled);
    }

    private nint Detour(nint modelRenderer, nint outFlags, nint param, nint materialPtr, uint materialIndex)
    {
        if (materialPtr != 0)
        {
            try
            {
                var material = (Material*)materialPtr;
                var mrh = material->MaterialResourceHandle;
                if (mrh != null
                    && !targets.IsEmpty
                    && targets.TryGetValue((nint)mrh, out var data))
                {
                    PatchEmissive(material, mrh, ComputeModulatedColor(data));
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref errorCount) <= 5)
                    DebugServer.AppendLog($"[EmissiveHook] Error #{errorCount}: {ex.Message}");
            }
        }

        return hook.Original(modelRenderer, outFlags, param, materialPtr, materialIndex);
    }

    private Vector3 ComputeModulatedColor(TargetData data)
    {
        if (data.AnimMode == EmissiveAnimMode.None
            || data.AnimAmplitude <= 0f || data.AnimSpeed <= 0f)
            return data.BaseColor;
        double t = clock.Elapsed.TotalSeconds;
        float s = (float)Math.Sin(t * data.AnimSpeed * 2.0 * Math.PI);
        if (data.AnimMode == EmissiveAnimMode.Gradient)
        {
            // mix  in  [0.5-0.5*amp, 0.5+0.5*amp] -- amp controls how far we swing between colors
            float mix = MathF.Max(0f, MathF.Min(1f, 0.5f + 0.5f * data.AnimAmplitude * s));
            return Vector3.Lerp(data.BaseColor, data.GradientColorB, mix);
        }
        // Ripple has no per-pixel UV available via the CBuffer hook (iris path), so it
        // gracefully degrades to Pulse -- the user still sees time-driven modulation.
        float wave = data.AnimMode == EmissiveAnimMode.Flicker
            ? (s >= 0f ? 1f : -1f)
            : s;
        float k = MathF.Max(0f, 1f + wave * data.AnimAmplitude);
        return data.BaseColor * k;
    }

    private void PatchEmissive(Material* material, MaterialResourceHandle* mrh, Vector3 color)
    {
        var cbuf = material->MaterialParameterCBuffer;
        if (cbuf == null || cbuf->ByteSize <= 0) return;

        int offset = GetEmissiveOffset(mrh);
        if (offset < 0 || offset + 12 > cbuf->ByteSize) return;

        var ptr = cbuf->LoadSourcePointer(offset, 12, ConstantBuffer.DefaultLoadSourcePointerFlags);
        if (ptr == null) return;

        var dst = (float*)ptr;
        dst[0] = color.X;
        dst[1] = color.Y;
        dst[2] = color.Z;
    }

    /// <summary>Find g_EmissiveColor byte offset via ShaderPackage.MaterialElements CRC lookup.</summary>
    private int GetEmissiveOffset(MaterialResourceHandle* mrh)
    {
        var key = (nint)mrh;
        if (offsetCache.TryGetValue(key, out var cached))
            return cached;

        int result = -1;
        var shpkHandle = mrh->ShaderPackageResourceHandle;
        if (shpkHandle != null)
        {
            var shpk = shpkHandle->ShaderPackage;
            if (shpk != null)
            {
                var elements = shpk->MaterialElementsSpan;
                for (int i = 0; i < elements.Length; i++)
                {
                    if (elements[i].CRC == CrcEmissiveColor)
                    {
                        result = elements[i].Offset;
                        DebugServer.AppendLog(
                            $"[EmissiveHook] Found g_EmissiveColor: offset={result} size={elements[i].Size}");
                        break;
                    }
                }
            }
        }
        offsetCache[key] = result;
        return result;
    }

    public void Dispose()
    {
        Disable();
        targets.Clear();
        offsetCache.Clear();
        hook?.Dispose();
    }
}
