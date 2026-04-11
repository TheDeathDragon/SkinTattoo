using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
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

    private readonly ConcurrentDictionary<nint, Vector3> targets = new();
    private readonly ConcurrentDictionary<nint, int> offsetCache = new();

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
        string mtrlGamePath, string? mtrlDiskPath, Vector3 emissiveColor)
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
                    if (targets.TryGetValue(key, out var existing) && existing == emissiveColor)
                        return;
                    targets[key] = emissiveColor;
                    if (!enabled) Enable();
                    DebugServer.AppendLog($"[EmissiveHook] Target set: {fileName} color=({emissiveColor.X:F2},{emissiveColor.Y:F2},{emissiveColor.Z:F2})");
                    return;
                }
            }
        }

        DebugServer.AppendLog($"[EmissiveHook] Material not found: {mtrlGamePath}");
    }

    public void ClearTargets()
    {
        targets.Clear();
        offsetCache.Clear();
        if (enabled) Disable();
    }

    public bool HasTargets => !targets.IsEmpty;

    private nint Detour(nint modelRenderer, nint outFlags, nint param, nint materialPtr, uint materialIndex)
    {
        if (!targets.IsEmpty && materialPtr != 0)
        {
            try
            {
                var material = (Material*)materialPtr;
                var mrh = material->MaterialResourceHandle;
                if (mrh != null && targets.TryGetValue((nint)mrh, out var color))
                {
                    PatchEmissive(material, mrh, color);
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
                            $"[EmissiveHook] Found g_EmissiveColor via ShaderPackage: offset={result} size={elements[i].Size}");
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
