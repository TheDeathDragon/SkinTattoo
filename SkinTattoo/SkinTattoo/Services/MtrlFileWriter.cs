using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using SkinTattoo.Http;

namespace SkinTattoo.Services;

/// <summary>Modifies shader keys and constants for emissive glow, rebuilds binary .mtrl.</summary>
public static class MtrlFileWriter
{
    private const uint CategorySkinType = 0x380CAED0;
    private const uint ValueEmissive = 0x72E697CD;
    private const uint ConstantEmissiveColor = 0x38A64362;
    private const uint ConstantIrisRingEmissiveIntensity = 0x7DABA471;

    // PS[19] EMISSIVE nodes require ALL THREE material keys to match.
    // Body mods (bibo etc.) happen to have the right DecalMode/VertexColorMode,
    // but face/tail materials use defaults that route to non-emissive PS variants.
    private const uint CategoryDecalMode = 0xD2777173;
    private const uint ValueDecalEmissive = 0x584265DD;
    private const uint CategoryVertexColorMode = 0xF52CCF05;
    private const uint ValueVertexColorEmissive = 0xA7D2FF60;

    /// <summary>Write .mtrl with emissive enabled. Returns g_EmissiveColor byte offset.</summary>
    public static bool WriteEmissiveMtrl(MtrlFile mtrl, byte[] originalBytes, string outputPath,
        Vector3 emissiveColor, out int emissiveByteOffset)
    {
        emissiveByteOffset = -1;
        try
        {
            // Lumina's MtrlFile parser can't handle Dawntrail ColorTable (2048 bytes)
            // — it only reads 544 bytes then parses MaterialHeader from the wrong
            // offset, producing garbage for shader keys / constants / samplers.
            // Detect this case and use raw-byte patching instead.
            if (TryPatchEmissiveRaw(originalBytes, outputPath, emissiveColor, out emissiveByteOffset))
                return true;

            // Fallback: Lumina parsed correctly — use the structured rebuild path.
            return WriteEmissiveMtrlViaLumina(mtrl, originalBytes, outputPath, emissiveColor, out emissiveByteOffset);
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[MtrlWriter] Error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fast path: parse the raw .mtrl bytes directly, find g_EmissiveColor, patch
    /// its float values in-place, and write the result. No Lumina dependency.
    /// Returns false if g_EmissiveColor is not found (caller should use Lumina path).
    /// </summary>
    private static bool TryPatchEmissiveRaw(byte[] src, string outputPath,
        Vector3 color, out int emissiveByteOffset)
    {
        emissiveByteOffset = -1;
        if (src.Length < 16) return false;

        // ── Parse FileHeader ──
        int dataSetSize = BitConverter.ToUInt16(src, 6);
        int stringTableSize = BitConverter.ToUInt16(src, 8);
        int texCount = src[12];
        int uvCount = src[13];
        int colorCount = src[14];
        int addlDataSize = src[15];

        // ── Locate MaterialHeader ──
        int matHeaderOff = 16
            + texCount * 4
            + uvCount * 4
            + colorCount * 4
            + stringTableSize
            + addlDataSize
            + dataSetSize;

        if (matHeaderOff + 12 > src.Length) return false;

        int shaderValueListSize = BitConverter.ToUInt16(src, matHeaderOff);
        int shaderKeyCount = BitConverter.ToUInt16(src, matHeaderOff + 2);
        int constantCount = BitConverter.ToUInt16(src, matHeaderOff + 4);
        int samplerCount = BitConverter.ToUInt16(src, matHeaderOff + 6);

        // ── Locate Constants array ──
        int constantsOff = matHeaderOff + 12 + shaderKeyCount * 8;
        // Each constant: uint32 ID + uint16 ValueOffset + uint16 ValueSize = 8 bytes
        int samplersOff = constantsOff + constantCount * 8;
        int shaderValuesOff = samplersOff + samplerCount * 12;

        // ── Find g_EmissiveColor constant ──
        int emissiveAbsOff = -1;
        int irisRingAbsOff = -1;

        for (int i = 0; i < constantCount; i++)
        {
            int off = constantsOff + i * 8;
            if (off + 8 > src.Length) break;
            uint id = BitConverter.ToUInt32(src, off);
            int valueOffset = BitConverter.ToUInt16(src, off + 4);
            int valueSize = BitConverter.ToUInt16(src, off + 6);

            if (id == ConstantEmissiveColor && valueSize >= 12)
            {
                int abs = shaderValuesOff + valueOffset;
                if (abs + 12 <= src.Length)
                {
                    emissiveByteOffset = valueOffset;
                    emissiveAbsOff = abs;
                }
            }
            else if (id == ConstantIrisRingEmissiveIntensity && valueSize >= 4)
            {
                int abs = shaderValuesOff + valueOffset;
                if (abs + 4 <= src.Length)
                    irisRingAbsOff = abs;
            }
        }

        if (emissiveAbsOff < 0) return false;

        var patched = (byte[])src.Clone();
        BitConverter.TryWriteBytes(new Span<byte>(patched, emissiveAbsOff, 4), color.X);
        BitConverter.TryWriteBytes(new Span<byte>(patched, emissiveAbsOff + 4, 4), color.Y);
        BitConverter.TryWriteBytes(new Span<byte>(patched, emissiveAbsOff + 8, 4), color.Z);

        if (irisRingAbsOff >= 0)
            BitConverter.TryWriteBytes(new Span<byte>(patched, irisRingAbsOff, 4), 1.0f);

        File.WriteAllBytes(outputPath, patched);
        return true;
    }

    /// <summary>
    /// Lumina-based rebuild path: used when the material doesn't already have
    /// g_EmissiveColor (vanilla materials). Adds the emissive shader key and constant.
    /// </summary>
    private static bool WriteEmissiveMtrlViaLumina(MtrlFile mtrl, byte[] originalBytes,
        string outputPath, Vector3 emissiveColor, out int emissiveByteOffset)
    {
        emissiveByteOffset = -1;

        // Lumina skips AdditionalData — extract from raw bytes
        int addlDataOffset = 16
            + mtrl.FileHeader.TextureCount * 4
            + mtrl.FileHeader.UvSetCount * 4
            + mtrl.FileHeader.ColorSetCount * 4
            + mtrl.FileHeader.StringTableSize;
        int addlDataSize = mtrl.FileHeader.AdditionalDataSize;
        byte[] additionalData = new byte[addlDataSize];
        if (addlDataSize > 0 && addlDataOffset + addlDataSize <= originalBytes.Length)
            Array.Copy(originalBytes, addlDataOffset, additionalData, 0, addlDataSize);

        var shaderKeys = (ShaderKey[])mtrl.ShaderKeys.Clone();
        var constants = new List<Constant>(mtrl.Constants);
        var shaderValues = new List<float>(mtrl.ShaderValues);

        // Check if g_EmissiveColor already exists (shouldn't reach here if
        // TryPatchEmissiveRaw succeeded, but handle gracefully).
        bool foundEmissive = false;
        for (int i = 0; i < constants.Count; i++)
        {
            if (constants[i].ConstantId == ConstantEmissiveColor)
            {
                emissiveByteOffset = constants[i].ValueOffset;
                int floatOffset = emissiveByteOffset / 4;
                shaderValues[floatOffset] = emissiveColor.X;
                shaderValues[floatOffset + 1] = emissiveColor.Y;
                shaderValues[floatOffset + 2] = emissiveColor.Z;
                foundEmissive = true;
                break;
            }
        }

        if (!foundEmissive)
        {
            // Activate emissive shader variant: all 3 material keys must match
            var requiredKeys = new (uint Cat, uint Val)[]
            {
                (CategorySkinType, ValueEmissive),
                (CategoryDecalMode, ValueDecalEmissive),
                (CategoryVertexColorMode, ValueVertexColorEmissive),
            };
            var keyList = new List<ShaderKey>(shaderKeys);
            foreach (var (cat, val) in requiredKeys)
            {
                bool fk = false;
                for (int i = 0; i < keyList.Count; i++)
                {
                    if (keyList[i].Category == cat)
                    {
                        var k = keyList[i]; k.Value = val; keyList[i] = k;
                        fk = true; break;
                    }
                }
                if (!fk)
                    keyList.Add(new ShaderKey { Category = cat, Value = val });
            }
            shaderKeys = keyList.ToArray();
            {
            }

            emissiveByteOffset = shaderValues.Count * 4;
            constants.Add(new Constant
            {
                ConstantId = ConstantEmissiveColor,
                ValueOffset = (ushort)emissiveByteOffset,
                ValueSize = 12,
            });
            shaderValues.Add(emissiveColor.X);
            shaderValues.Add(emissiveColor.Y);
            shaderValues.Add(emissiveColor.Z);
        }

        // ColorTable handling: for skin.shpk, INJECT a Dawntrail ColorTable with emissive data.
        // The patched skin.shpk reads per-row emissive from g_SamplerTable (t10).
        byte[] colorTableData = Array.Empty<byte>();
        int dataSetSize = mtrl.FileHeader.DataSetSize;

        bool isSkinShpk = false;
        {
            int so = mtrl.FileHeader.ShaderPackageNameOffset;
            if (so < mtrl.Strings.Length)
            {
                int end = so;
                while (end < mtrl.Strings.Length && mtrl.Strings[end] != 0) end++;
                isSkinShpk = System.Text.Encoding.UTF8.GetString(mtrl.Strings, so, end - so) == "skin.shpk";
            }
        }

        if (isSkinShpk && !foundEmissive)
        {
            // Generate Dawntrail ColorTable (8 vec4 × 32 rows = 2048 bytes of Half)
            // with emissive color in all rows so the patched shader picks it up.
            colorTableData = BuildSkinColorTable(emissiveColor);

            // Set HasColorTable + Dawntrail dimensions in AdditionalData flags
            if (additionalData.Length >= 4)
            {
                uint flags = BitConverter.ToUInt32(additionalData, 0);
                flags |= 0x4;           // HasColorTable
                flags |= (3u << 4);     // widthLog = 3 → width = 8
                flags |= (5u << 8);     // heightLog = 5 → height = 32
                BitConverter.TryWriteBytes(additionalData.AsSpan(0, 4), flags);
            }
        }
        else if (dataSetSize > 0 && !isSkinShpk)
        {
            // Non-skin materials: preserve existing ColorTable data
            int colorDataOffset = 16
                + mtrl.FileHeader.TextureCount * 4
                + mtrl.FileHeader.UvSetCount * 4
                + mtrl.FileHeader.ColorSetCount * 4
                + mtrl.FileHeader.StringTableSize
                + mtrl.FileHeader.AdditionalDataSize;
            if (colorDataOffset + dataSetSize <= originalBytes.Length)
            {
                colorTableData = new byte[dataSetSize];
                Array.Copy(originalBytes, colorDataOffset, colorTableData, 0, dataSetSize);
            }
        }

        RebuildMtrl(mtrl, shaderKeys, constants.ToArray(), mtrl.Samplers,
            shaderValues.ToArray(), additionalData, colorTableData, outputPath);
        return true;
    }

    /// <summary>Build a Dawntrail ColorTable with per-layer emissive colors.</summary>
    public static byte[] BuildSkinColorTablePerLayer(List<Core.DecalLayer> layers)
    {
        var bytes = new byte[2048]; // 32 rows × 32 halfs × 2 bytes

        void WriteHalf(int row, int idx, float value)
        {
            int off = (row * 32 + idx) * 2;
            BitConverter.TryWriteBytes(bytes.AsSpan(off, 2), (Half)value);
        }

        // Fill all rows with safe defaults (white diffuse/specular, zero emissive)
        for (int row = 0; row < 32; row++)
        {
            WriteHalf(row, 0, 1f); WriteHalf(row, 1, 1f); WriteHalf(row, 2, 1f);
            WriteHalf(row, 4, 1f); WriteHalf(row, 5, 1f); WriteHalf(row, 6, 1f);
            WriteHalf(row, 16, 0.5f);
        }

        // Write per-layer emissive into assigned row pairs. The patched shader samples
        // ColorTable at row = (normal.a*30/255 + 0.5), which for discrete normal.a=k*17
        // lands exactly at row k*2+0.5 — the midpoint of a row pair. GPU linear filter
        // lerps rowLower and rowLower+1, so we must write the same emissive to BOTH
        // rows; otherwise the layer appears at 50% brightness.
        foreach (var layer in layers)
        {
            if (!layer.IsVisible || !layer.AffectsEmissive || layer.AllocatedRowPair < 0) continue;
            int rowLower = layer.AllocatedRowPair * 2;
            var em = layer.EmissiveColor * layer.EmissiveIntensity;
            bool hasAnim = layer.AnimMode != Core.EmissiveAnimMode.None;
            float animSpeed = hasAnim ? layer.AnimSpeed : 0f;
            float animAmp   = hasAnim ? layer.AnimAmplitude : 0f;
            // mode sentinel for DXBC branch selection: 0=pulse, 1=flicker, 2=gradient, 3=ripple
            float animMode = layer.AnimMode switch
            {
                Core.EmissiveAnimMode.Flicker  => 1f,
                Core.EmissiveAnimMode.Gradient => 2f,
                Core.EmissiveAnimMode.Ripple   => 3f,
                _ => 0f,
            };
            // Gradient second color (scaled by intensity for consistent brightness).
            var emB = layer.EmissiveColorB * layer.EmissiveIntensity;
            // Ripple: centerU/V from layer UV placement, freq only when in Ripple mode
            // so other modes produce zero spatial phase offset (shader unconditional path).
            bool isRipple = layer.AnimMode == Core.EmissiveAnimMode.Ripple;
            float centerU = isRipple ? layer.UvCenter.X : 0f;
            float centerV = isRipple ? layer.UvCenter.Y : 0f;
            float freq    = isRipple ? layer.AnimFreq : 0f;
            float dirMode = isRipple ? (float)(int)layer.AnimDirMode : 0f;
            // Direction unit vector precomputed from angle (shader does d·dir projection).
            float angleRad = layer.AnimDirAngle * MathF.PI / 180f;
            float dirX = isRipple ? MathF.Cos(angleRad) : 1f;
            float dirY = isRipple ? MathF.Sin(angleRad) : 0f;
            // dualActive triggers the lerp(colorA, colorB, 0.5+0.5*amp*sin) path:
            // - Gradient mode always dual (by definition)
            // - Ripple mode when user selected AnimDualColor
            // - Pulse/Flicker/None never dual
            bool dualActive = layer.AnimMode == Core.EmissiveAnimMode.Gradient
                              || (isRipple && layer.AnimDualColor);
            float dualFlag = dualActive ? 1f : 0f;
            for (int r = 0; r < 2; r++)
            {
                WriteHalf(rowLower + r, 8,  em.X);
                WriteHalf(rowLower + r, 9,  em.Y);
                WriteHalf(rowLower + r, 10, em.Z);
                WriteHalf(rowLower + r, 12, animSpeed);
                WriteHalf(rowLower + r, 13, animAmp);
                WriteHalf(rowLower + r, 14, animMode);
                // halfs 17/18/19 = Gradient/Ripple-dual colorB RGB (half 16 = vanilla roughness).
                WriteHalf(rowLower + r, 17, emB.X);
                WriteHalf(rowLower + r, 18, emB.Y);
                WriteHalf(rowLower + r, 19, emB.Z);
                // halfs 20/21/22/23 = Ripple centerU / centerV / freq / dirMode.
                WriteHalf(rowLower + r, 20, centerU);
                WriteHalf(rowLower + r, 21, centerV);
                WriteHalf(rowLower + r, 22, freq);
                WriteHalf(rowLower + r, 23, dirMode);
                // halfs 24/25/26 = dirX / dirY / dualActive (half 27 unused).
                WriteHalf(rowLower + r, 24, dirX);
                WriteHalf(rowLower + r, 25, dirY);
                WriteHalf(rowLower + r, 26, dualFlag);
            }
        }

        return bytes;
    }

    // New shader package name for patched skin.shpk. Must match PreviewService.SkinShpkGamePath filename.
    // When this mtrl is loaded the engine sees a new path and triggers a cache miss -> Penumbra redirect.
    private const string SkinCtShaderPackageName = "skin_ct.shpk";

    /// <summary>Write emissive .mtrl with pre-built ColorTable bytes for skin.shpk.
    /// Also rewrites ShaderPackageName from "skin.shpk" to "skin_ct.shpk" so the engine
    /// loads the patched shpk via Penumbra redirect instead of the cached vanilla one.</summary>
    public static bool WriteEmissiveMtrlWithColorTable(MtrlFile mtrl, byte[] originalBytes, string outputPath,
        Vector3 emissiveColor, byte[] colorTableBytes, out int emissiveByteOffset)
    {
        emissiveByteOffset = -1;
        try
        {
            // Lumina AdditionalData extraction
            int addlDataOffset = 16
                + mtrl.FileHeader.TextureCount * 4
                + mtrl.FileHeader.UvSetCount * 4
                + mtrl.FileHeader.ColorSetCount * 4
                + mtrl.FileHeader.StringTableSize;
            int addlDataSize = mtrl.FileHeader.AdditionalDataSize;
            byte[] additionalData = new byte[Math.Max(addlDataSize, 4)];
            if (addlDataSize > 0 && addlDataOffset + addlDataSize <= originalBytes.Length)
                Array.Copy(originalBytes, addlDataOffset, additionalData, 0, addlDataSize);

            // Set HasColorTable + Dawntrail dimensions
            uint flags = additionalData.Length >= 4 ? BitConverter.ToUInt32(additionalData, 0) : 0;
            flags |= 0x4;
            flags |= (3u << 4);
            flags |= (5u << 8);
            BitConverter.TryWriteBytes(additionalData.AsSpan(0, 4), flags);

            // Add emissive shader key + constant
            var shaderKeys = (Lumina.Data.Parsing.ShaderKey[])mtrl.ShaderKeys.Clone();
            var constants = new System.Collections.Generic.List<Lumina.Data.Parsing.Constant>(mtrl.Constants);
            var shaderValues = new System.Collections.Generic.List<float>(mtrl.ShaderValues);

            // PS[19] EMISSIVE nodes require all 3 material keys to match specific values.
            // Body mods (bibo) happen to have the right DecalMode/VertexColorMode,
            // but face/tail/etc materials use defaults that route to non-emissive PS.
            var requiredKeys = new (uint Cat, uint Val)[]
            {
                (CategorySkinType, ValueEmissive),
                (CategoryDecalMode, ValueDecalEmissive),
                (CategoryVertexColorMode, ValueVertexColorEmissive),
            };
            var keyList = new System.Collections.Generic.List<Lumina.Data.Parsing.ShaderKey>(shaderKeys);
            foreach (var (cat, val) in requiredKeys)
            {
                bool found = false;
                for (int i = 0; i < keyList.Count; i++)
                {
                    if (keyList[i].Category == cat)
                    {
                        var k = keyList[i]; k.Value = val; keyList[i] = k;
                        found = true; break;
                    }
                }
                if (!found)
                    keyList.Add(new Lumina.Data.Parsing.ShaderKey { Category = cat, Value = val });
            }
            shaderKeys = keyList.ToArray();

            bool foundEmissive = false;
            for (int i = 0; i < constants.Count; i++)
            {
                if (constants[i].ConstantId == ConstantEmissiveColor)
                {
                    emissiveByteOffset = constants[i].ValueOffset;
                    int fi = emissiveByteOffset / 4;
                    shaderValues[fi] = emissiveColor.X;
                    shaderValues[fi + 1] = emissiveColor.Y;
                    shaderValues[fi + 2] = emissiveColor.Z;
                    foundEmissive = true;
                    break;
                }
            }

            if (!foundEmissive)
            {
                emissiveByteOffset = shaderValues.Count * 4;
                constants.Add(new Lumina.Data.Parsing.Constant
                {
                    ConstantId = ConstantEmissiveColor,
                    ValueOffset = (ushort)emissiveByteOffset,
                    ValueSize = 12,
                });
                shaderValues.Add(emissiveColor.X);
                shaderValues.Add(emissiveColor.Y);
                shaderValues.Add(emissiveColor.Z);
            }

            var (newStrings, newShpkOff, newStrSize) = RewriteShaderPackageName(mtrl, SkinCtShaderPackageName);

            RebuildMtrl(mtrl, shaderKeys, constants.ToArray(), mtrl.Samplers,
                shaderValues.ToArray(), additionalData, colorTableBytes, outputPath,
                newStrings, newShpkOff, newStrSize);
            return true;
        }
        catch (Exception ex)
        {
            Http.DebugServer.AppendLog($"[MtrlWriter] CT error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Return (strings blob, new ShaderPackageName offset, new StringTableSize) with
    /// newName appended to the strings table. Idempotent: if the current name already equals
    /// newName, returns the original unchanged. Result size is 4-byte aligned for safety.</summary>
    private static (byte[] strings, ushort shpkOffset, ushort stringTableSize) RewriteShaderPackageName(
        MtrlFile mtrl, string newName)
    {
        var orig = mtrl.Strings;
        int curOff = mtrl.FileHeader.ShaderPackageNameOffset;
        if (curOff < orig.Length)
        {
            int end = curOff;
            while (end < orig.Length && orig[end] != 0) end++;
            var curName = System.Text.Encoding.ASCII.GetString(orig, curOff, end - curOff);
            if (curName == newName)
                return (orig, mtrl.FileHeader.ShaderPackageNameOffset, mtrl.FileHeader.StringTableSize);
        }

        var nameBytes = System.Text.Encoding.ASCII.GetBytes(newName);
        int newOff = orig.Length;
        int rawSize = newOff + nameBytes.Length + 1;
        int alignedSize = (rawSize + 3) & ~3;

        var result = new byte[alignedSize];
        Array.Copy(orig, result, orig.Length);
        Array.Copy(nameBytes, 0, result, newOff, nameBytes.Length);
        // result[newOff + nameBytes.Length] = 0 already (default); padding bytes stay 0.

        return (result, (ushort)newOff, (ushort)alignedSize);
    }

    /// <summary>
    /// Build a Dawntrail ColorTable (8 vec4 × 32 rows = 1024 Half = 2048 bytes)
    /// with the given emissive color set in ALL rows. This ensures any normal.alpha
    /// value (row index) produces the same emissive color — matching the old uniform behavior
    /// while proving the ColorTable pipeline works end-to-end.
    /// </summary>
    private static byte[] BuildSkinColorTable(Vector3 emissiveColor)
    {
        const int rows = 32;
        const int halfsPerRow = 32; // 8 vec4 × 4 halfs
        var bytes = new byte[rows * halfsPerRow * 2]; // 2048 bytes

        void WriteHalf(int row, int idx, float value)
        {
            int off = (row * halfsPerRow + idx) * 2;
            var h = (Half)value;
            BitConverter.TryWriteBytes(bytes.AsSpan(off, 2), h);
        }

        for (int row = 0; row < rows; row++)
        {
            WriteHalf(row, 0, 1f); WriteHalf(row, 1, 1f); WriteHalf(row, 2, 1f); // Diffuse
            WriteHalf(row, 4, 1f); WriteHalf(row, 5, 1f); WriteHalf(row, 6, 1f); // Specular
            WriteHalf(row, 8, emissiveColor.X);  // Emissive R
            WriteHalf(row, 9, emissiveColor.Y);  // Emissive G
            WriteHalf(row, 10, emissiveColor.Z); // Emissive B
            WriteHalf(row, 16, 0.5f); // Roughness
        }

        return bytes;
    }

    private static void RebuildMtrl(MtrlFile mtrl, ShaderKey[] shaderKeys, Constant[] constants,
        Sampler[] samplers, float[] shaderValues, byte[] additionalData,
        byte[] colorTableData, string outputPath,
        byte[]? stringsOverride = null, ushort? shpkOffsetOverride = null,
        ushort? stringTableSizeOverride = null)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var shaderValueListSize = (ushort)(shaderValues.Length * 4);
        var materialHeader = new MaterialHeader
        {
            ShaderValueListSize = shaderValueListSize,
            ShaderKeyCount = (ushort)shaderKeys.Length,
            ConstantCount = (ushort)constants.Length,
            SamplerCount = (ushort)samplers.Length,
            Unknown1 = mtrl.MaterialHeader.Unknown1,
            Unknown2 = mtrl.MaterialHeader.Unknown2,
        };

        byte[] stringsToWrite = stringsOverride ?? mtrl.Strings;
        ushort stringTableSize = stringTableSizeOverride ?? mtrl.FileHeader.StringTableSize;
        ushort shpkNameOffset = shpkOffsetOverride ?? mtrl.FileHeader.ShaderPackageNameOffset;

        bw.Write(mtrl.FileHeader.Version);

        long fileSizePos = ms.Position;
        bw.Write(0u); // placeholder

        bw.Write(stringTableSize);
        bw.Write(shpkNameOffset);
        bool hasColorTable = colorTableData.Length > 0;
        byte effectiveColorSetCount = hasColorTable ? mtrl.FileHeader.ColorSetCount : (byte)0;

        bw.Write(mtrl.FileHeader.TextureCount);
        bw.Write(mtrl.FileHeader.UvSetCount);
        bw.Write(effectiveColorSetCount);
        // Use actual additionalData length — we may have expanded it from 0→4
        // to store HasColorTable flags. Writing the original size would cause
        // the engine to skip our flags entirely.
        bw.Write((byte)additionalData.Length);

        for (int i = 0; i < mtrl.TextureOffsets.Length; i++)
        {
            uint packed = mtrl.TextureOffsets[i].Offset | ((uint)mtrl.TextureOffsets[i].Flags << 16);
            bw.Write(packed);
        }

        for (int i = 0; i < mtrl.UvColorSets.Length; i++)
        {
            bw.Write(mtrl.UvColorSets[i].NameOffset);
            bw.Write(mtrl.UvColorSets[i].Index);
            bw.Write(mtrl.UvColorSets[i].Unknown1);
        }

        if (hasColorTable)
        {
            for (int i = 0; i < mtrl.ColorSets.Length; i++)
            {
                bw.Write(mtrl.ColorSets[i].NameOffset);
                bw.Write(mtrl.ColorSets[i].Index);
                bw.Write(mtrl.ColorSets[i].Unknown1);
            }
        }

        bw.Write(stringsToWrite);
        bw.Write(additionalData);

        if (colorTableData.Length > 0)
            bw.Write(colorTableData);

        bw.Write(materialHeader.ShaderValueListSize);
        bw.Write(materialHeader.ShaderKeyCount);
        bw.Write(materialHeader.ConstantCount);
        bw.Write(materialHeader.SamplerCount);
        bw.Write(materialHeader.Unknown1);
        bw.Write(materialHeader.Unknown2);

        foreach (var key in shaderKeys)
        {
            bw.Write(key.Category);
            bw.Write(key.Value);
        }

        foreach (var c in constants)
        {
            bw.Write(c.ConstantId);
            bw.Write(c.ValueOffset);
            bw.Write(c.ValueSize);
        }

        foreach (var s in samplers)
        {
            bw.Write(s.SamplerId);
            bw.Write(s.Flags);
            bw.Write(s.TextureIndex);
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((byte)0);
        }

        foreach (var v in shaderValues)
            bw.Write(v);

        var totalSize = (ushort)ms.Length;
        var actualDataSetSize = (uint)colorTableData.Length;
        ms.Position = fileSizePos;
        bw.Write((uint)totalSize | (actualDataSetSize << 16));

        bw.Flush();
        File.WriteAllBytes(outputPath, ms.ToArray());
    }
}
