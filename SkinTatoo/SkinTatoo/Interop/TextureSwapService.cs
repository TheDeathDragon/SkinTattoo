using System;
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
                        DebugServer.AppendLog(
                            $"[TextureSwap] Found at Model[{s}]Mat[{m}]Tex[{t}]: {fileName}");
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
                            DebugServer.AppendLog(
                                $"[TextureSwap] Found at Model[{s}]Mat[{m}]Tex[{t}]: {fileName}");
                            return &texHandle->Texture;
                        }
                    }
                }
            }
        }

        DebugServer.AppendLog($"[TextureSwap] NOT FOUND game={gamePath} disk={diskPath}");
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

        DebugServer.AppendLog(
            $"[TextureSwap] OK {width}x{height} old=0x{oldPtr:X} new=0x{(nint)newTex:X}");
        return true;
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

                // Copy ColorTable data and modify emissive color in all rows
                var copy = new Half[halfCount];
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
                if (*texSlot == null) continue;

                // Create new ColorTable texture and swap atomically
                var newTex = GpuTexture.CreateTexture2D(
                    ctWidth, ctHeight, 1,
                    TexFormat.R16G16B16A16_FLOAT,
                    CreateFlags,
                    7);

                if (newTex == null)
                {
                    DebugServer.AppendLog($"[TextureSwap] Emissive CreateTexture2D failed");
                    continue;
                }

                fixed (Half* dataPtr = copy)
                {
                    if (!newTex->InitializeContents(dataPtr))
                    {
                        newTex->DecRef();
                        DebugServer.AppendLog($"[TextureSwap] Emissive InitializeContents failed");
                        continue;
                    }
                }

                var oldPtr = Interlocked.Exchange(ref *(nint*)texSlot, (nint)newTex);
                if (oldPtr != 0)
                    ((GpuTexture*)oldPtr)->DecRef();

                DebugServer.AppendLog(
                    $"[TextureSwap] Emissive ColorTable swap Model[{s}]Mat[{m}] " +
                    $"color=({color.X:F2},{color.Y:F2},{color.Z:F2}) " +
                    $"ct={ctWidth}x{ctHeight} old=0x{oldPtr:X} new=0x{(nint)newTex:X}");

                return true;
            }
        }

        DebugServer.AppendLog($"[TextureSwap] Emissive NOT FOUND mtrl={mtrlGamePath} disk={mtrlDiskPath ?? "null"}");
        return false;
    }

    private bool MaterialMatchesTexture(Material* mat, string gamePath, string? diskPath)
    {
        var texCount = mat->TextureCount;
        if (texCount <= 0 || texCount > 32) return false;

        var textures = mat->Textures;
        if (!CanRead(textures, texCount * 0x18)) return false;

        for (int t = 0; t < texCount; t++)
        {
            var texHandle = textures[t].Texture;
            if (!CanRead(texHandle, 0x130)) continue;

            string fn;
            try { fn = ((ResourceHandle*)texHandle)->FileName.ToString(); }
            catch { continue; }

            if (string.IsNullOrEmpty(fn)) continue;
            var normFn = fn.Replace('\\', '/').ToLowerInvariant();
            var normGame = gamePath.Replace('\\', '/').ToLowerInvariant();
            if (normFn.EndsWith(normGame) || normGame.EndsWith(normFn))
                return true;
            if (diskPath != null)
            {
                var normDisk = diskPath.Replace('/', '\\').ToLowerInvariant();
                var normFnBack = fn.Replace('/', '\\').ToLowerInvariant();
                if (normFnBack.EndsWith(normDisk) || normDisk.EndsWith(normFnBack))
                    return true;
            }
        }

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
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i]     = rgba[i + 2]; // B
            bgra[i + 1] = rgba[i + 1]; // G
            bgra[i + 2] = rgba[i];     // R
            bgra[i + 3] = rgba[i + 3]; // A
        }
        return bgra;
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
