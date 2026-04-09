using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using SkinTatoo.Http;

namespace SkinTatoo.Interop;

using GpuTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;
using TexFormat = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.TextureFormat;
using TexFlags = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.TextureFlags;

/// <summary>
/// Directly replaces GPU textures on the character's materials without triggering
/// a full Penumbra redraw. Modeled after Glamourer's SafeTextureHandle approach.
/// </summary>
public unsafe class TextureSwapService
{
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly HashSet<string> loggedEmissiveNotFound = new(StringComparer.OrdinalIgnoreCase);

    private const TexFlags CreateFlags =
        TexFlags.TextureType2D | TexFlags.Managed | TexFlags.Immutable;


    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsBadReadPtr(void* lp, nuint ucb);

    // Validate pointer is readable for `size` bytes
    private static bool CanRead(void* ptr, int size)
        => ptr != null && !IsBadReadPtr(ptr, (nuint)size);

    public TextureSwapService(IObjectTable objectTable, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.log = log;
    }

    public CharacterBase* GetLocalPlayerCharacterBase()
    {
        var player = objectTable[0];
        if (player == null) return null;

        var gameObj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address;
        if (!CanRead(gameObj, 0x110)) return null;

        var drawObj = gameObj->DrawObject;
        if (!CanRead(drawObj, 0x360)) return null;

        return (CharacterBase*)drawObj;
    }

    /// <summary>
    /// Find a loaded texture in the character's material tree.
    /// Tries matching by game path first, then disk path as fallback.
    /// Returns a pointer to the Texture* field inside TextureResourceHandle.
    /// </summary>
    public GpuTexture** FindTextureSlot(CharacterBase* charBase, string gamePath, string? diskPath = null)
    {
        if (!CanRead(charBase, 0x360)) return null;

        var normGame = gamePath.Replace('\\', '/').ToLowerInvariant();
        var normDisk = diskPath?.Replace('/', '\\').ToLowerInvariant();

        var slotCount = charBase->SlotCount;
        if (slotCount <= 0 || slotCount > 30) return null;

        var modelsPtr = charBase->Models;
        if (!CanRead(modelsPtr, slotCount * sizeof(nint))) return null;

        // Navigate via Model→Material→TextureEntry (not the flat Materials array)
        for (int s = 0; s < slotCount; s++)
        {
            var model = modelsPtr[s];
            if (!CanRead(model, 0xA8)) continue;

            var matCount = model->MaterialCount;
            if (matCount <= 0 || matCount > 20) continue;

            var mats = model->Materials;
            if (!CanRead(mats, matCount * sizeof(nint))) continue;

            for (int m = 0; m < matCount; m++)
            {
                var mat = mats[m];
                if (!CanRead(mat, 0x58)) continue;

                var texCount = mat->TextureCount;
                if (texCount <= 0 || texCount > 32) continue;

                var textures = mat->Textures;
                if (!CanRead(textures, texCount * 0x18)) continue;

                for (int t = 0; t < texCount; t++)
                {
                    var texHandle = textures[t].Texture;
                    if (!CanRead(texHandle, 0x130)) continue;

                    string fileName;
                    try
                    {
                        fileName = ((ResourceHandle*)texHandle)->FileName.ToString();
                    }
                    catch { continue; }

                    if (string.IsNullOrEmpty(fileName)) continue;

                    var normFile = fileName.Replace('\\', '/').ToLowerInvariant();

                    // Match game path
                    if (normFile == normGame
                        || normFile.EndsWith(normGame)
                        || normGame.EndsWith(normFile))
                    {
                        return &texHandle->Texture;
                    }

                    // Match disk path (Penumbra-redirected textures store disk path)
                    if (normDisk != null)
                    {
                        var normFileBack = normFile.Replace('/', '\\');
                        if (normFileBack == normDisk
                            || normFileBack.EndsWith(normDisk)
                            || normDisk.EndsWith(normFileBack))
                        {
                            return &texHandle->Texture;
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Create a new GPU texture from BGRA pixel data and atomically swap it
    /// into the given texture slot.
    /// </summary>
    public bool SwapTexture(GpuTexture** slot, byte[] bgraData, int width, int height)
    {
        if (!CanRead(slot, sizeof(nint)) || *slot == null)
        {
            DebugServer.AppendLog("[TextureSwap] Slot is null or unreadable");
            return false;
        }

        var device = Device.Instance();
        if (device == null)
        {
            DebugServer.AppendLog("[TextureSwap] Device.Instance() is null");
            return false;
        }

        var newTex = GpuTexture.CreateTexture2D(
            width, height, 1,
            TexFormat.B8G8R8A8_UNORM,
            CreateFlags,
            7);

        if (newTex == null)
        {
            DebugServer.AppendLog("[TextureSwap] CreateTexture2D failed");
            return false;
        }

        fixed (byte* dataPtr = bgraData)
        {
            if (!newTex->InitializeContents(dataPtr))
            {
                newTex->DecRef();
                DebugServer.AppendLog("[TextureSwap] InitializeContents failed");
                return false;
            }
        }

        var oldPtr = Interlocked.Exchange(ref *(nint*)slot, (nint)newTex);
        if (oldPtr != 0)
            ((GpuTexture*)oldPtr)->DecRef();
        return true;
    }

    /// <summary>
    /// Copy the vanilla ColorTable bytes from a matched material's MaterialResourceHandle.
    /// Returns Half[] of length ctWidth*ctHeight*4, plus dimensions.
    /// Used by v1 PBR path to get a mutable baseline before writing back a full table.
    /// Returns false if no matching material or HasColorTable=false.
    /// </summary>
    public bool TryGetVanillaColorTable(CharacterBase* charBase, string mtrlGamePath,
        string? mtrlDiskPath, out Half[] data, out int width, out int height)
    {
        data = Array.Empty<Half>();
        width = 0;
        height = 0;

        if (!CanRead(charBase, 0x360)) return false;

        var slotCount = charBase->SlotCount;
        if (slotCount <= 0 || slotCount > 30) return false;

        var modelsPtr = charBase->Models;
        if (!CanRead(modelsPtr, slotCount * sizeof(nint))) return false;

        var normMtrl = mtrlGamePath.Replace('\\', '/').ToLowerInvariant();
        var normDisk = mtrlDiskPath?.Replace('\\', '/').ToLowerInvariant();

        for (int s = 0; s < slotCount; s++)
        {
            var model = modelsPtr[s];
            if (!CanRead(model, 0xA8)) continue;
            var matCount = model->MaterialCount;
            if (matCount <= 0 || matCount > 20) continue;
            var mats = model->Materials;
            if (!CanRead(mats, matCount * sizeof(nint))) continue;

            for (int m = 0; m < matCount; m++)
            {
                var mat = mats[m];
                if (!CanRead(mat, 0x58)) continue;
                var mtrlHandle = mat->MaterialResourceHandle;
                if (!CanRead(mtrlHandle, 0x100)) continue;

                string mtrlFileName;
                try { mtrlFileName = ((ResourceHandle*)mtrlHandle)->FileName.ToString(); }
                catch { continue; }
                if (string.IsNullOrEmpty(mtrlFileName)) continue;

                var normFileName = mtrlFileName.Replace('\\', '/').ToLowerInvariant();
                bool matched = normFileName.EndsWith(normMtrl) || normMtrl.EndsWith(normFileName);
                if (!matched && normDisk != null)
                    matched = normFileName.EndsWith(normDisk) || normDisk.EndsWith(normFileName)
                           || normFileName.Contains(Path.GetFileName(normDisk));
                if (!matched) continue;

                if (!mtrlHandle->HasColorTable) return false;
                var ctData = mtrlHandle->ColorTable;
                if (ctData == null) return false;

                var ctW = mtrlHandle->ColorTableWidth;
                var ctH = mtrlHandle->ColorTableHeight;
                if (ctW <= 0 || ctH <= 0) return false;

                int halfCount = ctW * ctH * 4;
                var copy = new Half[halfCount];
                fixed (Half* dst = copy)
                {
                    Buffer.MemoryCopy(ctData, dst, halfCount * sizeof(Half), halfCount * sizeof(Half));
                }
                data = copy;
                width = ctW;
                height = ctH;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Replace a matched material's ColorTable GPU texture with a new full table.
    /// Input: Half[] of length width*height*4, where width = vec4 count per row (= texture width),
    /// height = row count. Performs an atomic slot swap via Interlocked.Exchange.
    /// Modeled after Glamourer's DirectXService.ReplaceColorTable.
    /// </summary>
    public bool ReplaceColorTableRaw(CharacterBase* charBase, string mtrlGamePath,
        string? mtrlDiskPath, Half[] data, int width, int height)
    {
        if (!CanRead(charBase, 0x360)) return false;
        if (data.Length != width * height * 4) return false;

        var slotCount = charBase->SlotCount;
        if (slotCount <= 0 || slotCount > 30) return false;
        var modelsPtr = charBase->Models;
        if (!CanRead(modelsPtr, slotCount * sizeof(nint))) return false;
        var ctTextures = charBase->ColorTableTextures;
        if (!CanRead(ctTextures, slotCount * CharacterBase.MaterialsPerSlot * sizeof(nint))) return false;

        var normMtrl = mtrlGamePath.Replace('\\', '/').ToLowerInvariant();
        var normDisk = mtrlDiskPath?.Replace('\\', '/').ToLowerInvariant();

        for (int s = 0; s < slotCount; s++)
        {
            var model = modelsPtr[s];
            if (!CanRead(model, 0xA8)) continue;
            var matCount = model->MaterialCount;
            if (matCount <= 0 || matCount > 20) continue;
            var mats = model->Materials;
            if (!CanRead(mats, matCount * sizeof(nint))) continue;

            for (int m = 0; m < matCount; m++)
            {
                var mat = mats[m];
                if (!CanRead(mat, 0x58)) continue;
                var mtrlHandle = mat->MaterialResourceHandle;
                if (!CanRead(mtrlHandle, 0x100)) continue;

                string mtrlFileName;
                try { mtrlFileName = ((ResourceHandle*)mtrlHandle)->FileName.ToString(); }
                catch { continue; }
                if (string.IsNullOrEmpty(mtrlFileName)) continue;

                var normFileName = mtrlFileName.Replace('\\', '/').ToLowerInvariant();
                bool matched = normFileName.EndsWith(normMtrl) || normMtrl.EndsWith(normFileName);
                if (!matched && normDisk != null)
                    matched = normFileName.EndsWith(normDisk) || normDisk.EndsWith(normFileName)
                           || normFileName.Contains(Path.GetFileName(normDisk));
                if (!matched) continue;

                if (!mtrlHandle->HasColorTable) continue;

                int flatIndex = s * CharacterBase.MaterialsPerSlot + m;
                var texSlot = &ctTextures[flatIndex];
                if (*texSlot == null)
                {
                    DebugServer.AppendLog($"[TextureSwap] ReplaceColorTableRaw: slot null Model[{s}]Mat[{m}]");
                    return false;
                }

                var newTex = GpuTexture.CreateTexture2D(
                    width, height, 1,
                    TexFormat.R16G16B16A16_FLOAT,
                    CreateFlags, 7);
                if (newTex == null)
                {
                    DebugServer.AppendLog("[TextureSwap] ReplaceColorTableRaw: CreateTexture2D failed");
                    return false;
                }

                fixed (Half* dataPtr = data)
                {
                    if (!newTex->InitializeContents(dataPtr))
                    {
                        newTex->DecRef();
                        DebugServer.AppendLog("[TextureSwap] ReplaceColorTableRaw: InitializeContents failed");
                        return false;
                    }
                }

                var oldPtr = Interlocked.Exchange(ref *(nint*)texSlot, (nint)newTex);
                if (oldPtr != 0)
                    ((GpuTexture*)oldPtr)->DecRef();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Update emissive color by swapping the ColorTable texture on the matching material.
    /// Matches by material game path or disk path against MaterialResourceHandle.FileName.
    /// Reads the base color table from MaterialResourceHandle.DataSet, modifies emissive rows,
    /// creates a new R16G16B16A16_FLOAT texture and atomically swaps it into
    /// CharacterBase.ColorTableTextures[].
    /// Modeled after Glamourer's DirectXService.ReplaceColorTable.
    /// </summary>
    public bool UpdateEmissiveViaColorTable(CharacterBase* charBase, string mtrlGamePath,
        string? mtrlDiskPath, Vector3 color)
    {
        if (!CanRead(charBase, 0x360)) return false;

        var slotCount = charBase->SlotCount;
        if (slotCount <= 0 || slotCount > 30) return false;

        var modelsPtr = charBase->Models;
        if (!CanRead(modelsPtr, slotCount * sizeof(nint))) return false;

        var ctTextures = charBase->ColorTableTextures;
        if (!CanRead(ctTextures, slotCount * CharacterBase.MaterialsPerSlot * sizeof(nint))) return false;

        var normMtrl = mtrlGamePath.Replace('\\', '/').ToLowerInvariant();
        var normDisk = mtrlDiskPath?.Replace('\\', '/').ToLowerInvariant();

        for (int s = 0; s < slotCount; s++)
        {
            var model = modelsPtr[s];
            if (!CanRead(model, 0xA8)) continue;

            var matCount = model->MaterialCount;
            if (matCount <= 0 || matCount > 20) continue;

            var mats = model->Materials;
            if (!CanRead(mats, matCount * sizeof(nint))) continue;

            for (int m = 0; m < matCount; m++)
            {
                var mat = mats[m];
                if (!CanRead(mat, 0x58)) continue;

                // Match by material resource path (game path or Penumbra-redirected disk path)
                var mtrlHandle = mat->MaterialResourceHandle;
                if (!CanRead(mtrlHandle, 0x100)) continue;

                string mtrlFileName;
                try { mtrlFileName = ((ResourceHandle*)mtrlHandle)->FileName.ToString(); }
                catch { continue; }

                if (string.IsNullOrEmpty(mtrlFileName)) continue;
                var normFileName = mtrlFileName.Replace('\\', '/').ToLowerInvariant();

                // Try game path match
                bool matched = normFileName.EndsWith(normMtrl) || normMtrl.EndsWith(normFileName);

                // Try disk path match (Penumbra FileName may contain "|prefix|diskpath")
                if (!matched && normDisk != null)
                    matched = normFileName.EndsWith(normDisk) || normDisk.EndsWith(normFileName)
                           || normFileName.Contains(Path.GetFileName(normDisk));

                if (!matched) continue;

                // skin.shpk: no ColorTable, emissive handled by EmissiveCBufferHook
                if (!mtrlHandle->HasColorTable)
                    continue;

                var colorTableData = mtrlHandle->ColorTable;
                if (colorTableData == null) continue;

                var ctWidth = mtrlHandle->ColorTableWidth;   // vec4 count per row (= texture width)
                var ctHeight = mtrlHandle->ColorTableHeight;  // row count (= texture height)
                if (ctWidth < 3 || ctHeight <= 0) continue;   // need at least 3 vec4s to reach emissive

                var halfCount = ctWidth * ctHeight * 4;
                var byteSize = halfCount * sizeof(Half);

                // ColorTable is small (typically 16 vec4 × 4 = 64 Halves = 128 bytes), stackalloc is safe
                Span<Half> copy = stackalloc Half[halfCount];
                fixed (Half* dst = copy)
                {
                    Buffer.MemoryCopy(colorTableData, dst, byteSize, byteSize);
                }

                int rowStride = ctWidth * 4; // Halves per row
                for (int row = 0; row < ctHeight; row++)
                {
                    int baseIdx = row * rowStride;
                    // Emissive = vec4 #2 → indices 8, 9, 10 within each row
                    copy[baseIdx + 8] = (Half)color.X;
                    copy[baseIdx + 9] = (Half)color.Y;
                    copy[baseIdx + 10] = (Half)color.Z;
                }

                // Get ColorTable texture slot: flat index = slot * MaterialsPerSlot + matIdx
                int flatIndex = s * CharacterBase.MaterialsPerSlot + m;
                var texSlot = &ctTextures[flatIndex];
                if (*texSlot == null)
                {
                    DebugServer.AppendLog($"[TextureSwap] Emissive ColorTable slot null at Model[{s}]Mat[{m}]");
                    return false;
                }

                var newTex = GpuTexture.CreateTexture2D(
                    ctWidth, ctHeight, 1,
                    TexFormat.R16G16B16A16_FLOAT,
                    CreateFlags,
                    7);

                if (newTex == null)
                {
                    DebugServer.AppendLog($"[TextureSwap] Emissive CreateTexture2D failed");
                    return false;
                }

                fixed (Half* dataPtr = copy)
                {
                    if (!newTex->InitializeContents(dataPtr))
                    {
                        newTex->DecRef();
                        DebugServer.AppendLog($"[TextureSwap] Emissive InitializeContents failed");
                        return false;
                    }
                }

                var oldPtr = Interlocked.Exchange(ref *(nint*)texSlot, (nint)newTex);
                if (oldPtr != 0)
                    ((GpuTexture*)oldPtr)->DecRef();

                return true;
            }
        }

        // Only log once per mtrl to avoid spam (skin.shpk has no ColorTable, handled by CBuffer hook)
        if (loggedEmissiveNotFound.Add(mtrlGamePath))
            DebugServer.AppendLog($"[TextureSwap] Emissive ColorTable not found (skin.shpk?): {mtrlGamePath}");
        return false;
    }

    /// <summary>Convert HSV (h 0-1, s 0-1, v 0-1) to RGB Vector3.</summary>
    public static Vector3 HsvToRgb(float h, float s, float v)
    {
        h = ((h % 1f) + 1f) % 1f;
        int hi = (int)(h * 6f) % 6;
        float f = h * 6f - (int)(h * 6f);
        float p = v * (1f - s);
        float q = v * (1f - f * s);
        float t = v * (1f - (1f - f) * s);
        return hi switch
        {
            0 => new Vector3(v, t, p),
            1 => new Vector3(q, v, p),
            2 => new Vector3(p, v, t),
            3 => new Vector3(p, q, v),
            4 => new Vector3(t, p, v),
            _ => new Vector3(v, p, q),
        };
    }

    /// <summary>Convert RGBA byte array to BGRA byte array.</summary>
    public static byte[] RgbaToBgra(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        RgbaToBgra(rgba, bgra, rgba.Length);
        return bgra;
    }

    /// <summary>
    /// Convert RGBA → BGRA into a caller-provided destination buffer (no allocation).
    /// <paramref name="byteCount"/> = number of valid bytes (multiple of 4).
    /// </summary>
    public static void RgbaToBgra(byte[] rgba, byte[] dst, int byteCount)
    {
        for (int i = 0; i < byteCount; i += 4)
        {
            dst[i]     = rgba[i + 2]; // B
            dst[i + 1] = rgba[i + 1]; // G
            dst[i + 2] = rgba[i];     // R
            dst[i + 3] = rgba[i + 3]; // A
        }
    }

    /// <summary>
    /// Convert only a rectangular sub-region of RGBA → BGRA. Rows outside the rect are
    /// untouched in <paramref name="dst"/>, which is the whole point — if the caller
    /// only updates part of an RGBA buffer, it can mirror that same sub-region in its
    /// BGRA scratch without re-swizzling 64MB every frame.
    /// </summary>
    public static void RgbaToBgraRegion(byte[] rgba, byte[] dst, int texW, int texH, SkinTatoo.Services.DirtyRect rect)
    {
        if (rect.IsEmpty) return;
        int yEnd = rect.Y + rect.H;
        int xEnd = rect.X + rect.W;
        for (int py = rect.Y; py < yEnd; py++)
        {
            int rowBase = py * texW * 4;
            int i0 = rowBase + rect.X * 4;
            int i1 = rowBase + xEnd * 4;
            for (int i = i0; i < i1; i += 4)
            {
                dst[i]     = rgba[i + 2]; // B
                dst[i + 1] = rgba[i + 1]; // G
                dst[i + 2] = rgba[i];     // R
                dst[i + 3] = rgba[i + 3]; // A
            }
        }
    }

    /// <summary>Dump textures via Model→Material path (more reliable than CharacterBase.Materials).</summary>
    public string DumpCharacterTextures()
    {
        var charBase = GetLocalPlayerCharacterBase();
        if (charBase == null) return "CharacterBase not found";

        var sb = new StringBuilder();
        sb.AppendLine($"SlotCount={charBase->SlotCount} cb=0x{(nint)charBase:X}");

        var modelsPtr = charBase->Models;
        if (!CanRead(modelsPtr, charBase->SlotCount * sizeof(nint)))
            return sb.Append("Models** unreadable").ToString();

        for (int s = 0; s < charBase->SlotCount; s++)
        {
            var model = modelsPtr[s];
            if (!CanRead(model, 0xA8)) continue;

            var matCount = model->MaterialCount;
            sb.AppendLine($"  Model[{s}]: matCount={matCount} model=0x{(nint)model:X}");
            if (matCount <= 0 || matCount > 20) continue;

            var mats = model->Materials;
            if (!CanRead(mats, matCount * sizeof(nint))) continue;

            for (int m = 0; m < matCount; m++)
            {
                var mat = mats[m];
                if (!CanRead(mat, 0x58)) continue;

                // Read raw fields
                var texCount = mat->TextureCount;
                var textures = mat->Textures;
                sb.AppendLine(
                    $"    Mat[{m}]: texCount={texCount} texPtr=0x{(nint)textures:X} mat=0x{(nint)mat:X}");

                // Also try MaterialResourceHandle path
                var mrh = mat->MaterialResourceHandle;
                if (CanRead(mrh, 0x100))
                {
                    var mrhTexCount = *(byte*)((byte*)mrh + 0xFA);
                    var mrhTexPtr = *(nint*)((byte*)mrh + 0xD0);
                    sb.AppendLine(
                        $"      MRH texCount={mrhTexCount} texPtr=0x{mrhTexPtr:X} mrh=0x{(nint)mrh:X}");

                    // Try to read MRH textures (TextureResourceHandle* at stride 0x10)
                    if (mrhTexCount > 0 && mrhTexCount <= 16 && CanRead((void*)mrhTexPtr, mrhTexCount * 0x10))
                    {
                        for (int t = 0; t < mrhTexCount; t++)
                        {
                            var texResHandle = *(TextureResourceHandle**)((byte*)mrhTexPtr + t * 0x10);
                            if (!CanRead(texResHandle, 0x130)) continue;

                            string fn;
                            try { fn = ((ResourceHandle*)texResHandle)->FileName.ToString(); }
                            catch { fn = "<error>"; }

                            var gpuTex = texResHandle->Texture;
                            if (CanRead(gpuTex, 0x70))
                                sb.AppendLine(
                                    $"        [{t}] {gpuTex->ActualWidth}x{gpuTex->ActualHeight} path={fn}");
                            else
                                sb.AppendLine($"        [{t}] path={fn}");
                        }
                    }
                }

                // Also try Material.Textures path if texCount is valid
                if (texCount > 0 && texCount <= 32 && CanRead(textures, texCount * 0x18))
                {
                    for (int t = 0; t < texCount; t++)
                    {
                        var texHandle = textures[t].Texture;
                        if (!CanRead(texHandle, 0x130)) continue;

                        string fn;
                        try { fn = ((ResourceHandle*)texHandle)->FileName.ToString(); }
                        catch { fn = "<error>"; }

                        sb.AppendLine($"      MatTex[{t}] id=0x{textures[t].Id:X} path={fn}");
                    }
                }
            }
        }

        return sb.ToString();
    }
}
