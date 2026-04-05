using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using SkinTatoo.Http;

namespace SkinTatoo.Services;

/// <summary>
/// Reads a Lumina MtrlFile, modifies shader keys and constants for emissive glow,
/// then fully rebuilds the binary .mtrl file.
/// </summary>
public static class MtrlFileWriter
{
    private const uint CategorySkinType = 0x380CAED0;
    private const uint ValueEmissive = 0x72E697CD;
    private const uint ConstantEmissiveColor = 0x38A64362;

    /// <summary>Returns the byte offset of g_EmissiveColor in the shader values via emissiveByteOffset.</summary>
    public static bool WriteEmissiveMtrl(MtrlFile mtrl, byte[] originalBytes, string outputPath,
        Vector3 emissiveColor, out int emissiveByteOffset)
    {
        emissiveByteOffset = -1;
        try
        {
            // Extract AdditionalData from original bytes (Lumina skips it)
            int addlDataOffset = 16
                + mtrl.FileHeader.TextureCount * 4
                + mtrl.FileHeader.UvSetCount * 4
                + mtrl.FileHeader.ColorSetCount * 4
                + mtrl.FileHeader.StringTableSize;
            int addlDataSize = mtrl.FileHeader.AdditionalDataSize;
            byte[] additionalData = new byte[addlDataSize];
            if (addlDataSize > 0 && addlDataOffset + addlDataSize <= originalBytes.Length)
                Array.Copy(originalBytes, addlDataOffset, additionalData, 0, addlDataSize);

            // Clone arrays so we don't modify the cached MtrlFile
            var shaderKeys = (ShaderKey[])mtrl.ShaderKeys.Clone();
            var constants = new List<Constant>(mtrl.Constants);
            var shaderValues = new List<float>(mtrl.ShaderValues);

            // Step 1: Patch CategorySkinType shader key to EMISSIVE
            // Only touch CategorySkinType — other keys (e.g. iris.shpk's 63030C80)
            // control unrelated rendering and must not be changed.
            bool foundSkinType = false;
            for (int i = 0; i < shaderKeys.Length; i++)
            {
                if (shaderKeys[i].Category == CategorySkinType)
                {
                    DebugServer.AppendLog($"[MtrlWriter] ShaderKey[{i}]: {shaderKeys[i].Value:X8} → {ValueEmissive:X8}");
                    shaderKeys[i].Value = ValueEmissive;
                    foundSkinType = true;
                    break;
                }
            }
            if (!foundSkinType)
            {
                // Material has no CategorySkinType key (common in mod mtrls or non-skin shaders).
                // Inject it so skin.shpk enables emissive; non-skin shaders will simply ignore it.
                var keyList = new List<ShaderKey>(shaderKeys);
                keyList.Add(new ShaderKey { Category = CategorySkinType, Value = ValueEmissive });
                shaderKeys = keyList.ToArray();
                DebugServer.AppendLog($"[MtrlWriter] Injected CategorySkinType (was {mtrl.ShaderKeys.Length} keys)");
            }

            // Step 2: Add or update g_EmissiveColor
            bool foundEmissive = false;
            for (int i = 0; i < constants.Count; i++)
            {
                if (constants[i].ConstantId == ConstantEmissiveColor)
                {
                    // Update existing values
                    emissiveByteOffset = constants[i].ValueOffset;
                    int floatOffset = emissiveByteOffset / 4;
                    shaderValues[floatOffset] = emissiveColor.X;
                    shaderValues[floatOffset + 1] = emissiveColor.Y;
                    shaderValues[floatOffset + 2] = emissiveColor.Z;
                    foundEmissive = true;
                    DebugServer.AppendLog($"[MtrlWriter] Updated g_EmissiveColor offset={emissiveByteOffset}: ({emissiveColor.X:F2}, {emissiveColor.Y:F2}, {emissiveColor.Z:F2})");
                    break;
                }
            }

            if (!foundEmissive)
            {
                // Add new constant entry + 3 float values at the end
                emissiveByteOffset = shaderValues.Count * 4;
                constants.Add(new Constant
                {
                    ConstantId = ConstantEmissiveColor,
                    ValueOffset = (ushort)emissiveByteOffset,
                    ValueSize = 12, // 3 floats = 12 bytes
                });
                shaderValues.Add(emissiveColor.X);
                shaderValues.Add(emissiveColor.Y);
                shaderValues.Add(emissiveColor.Z);
                DebugServer.AppendLog($"[MtrlWriter] Added g_EmissiveColor offset={emissiveByteOffset}: ({emissiveColor.X:F2}, {emissiveColor.Y:F2}, {emissiveColor.Z:F2})");
            }

            // Step 3: Rebuild the entire binary
            RebuildMtrl(mtrl, shaderKeys, constants.ToArray(), mtrl.Samplers, shaderValues.ToArray(), additionalData, outputPath);
            return true;
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[MtrlWriter] Error: {ex.Message}");
            return false;
        }
    }

    private static void RebuildMtrl(MtrlFile mtrl, ShaderKey[] shaderKeys, Constant[] constants, Sampler[] samplers, float[] shaderValues, byte[] additionalData, string outputPath)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // ── Section 1: File Header area ──

        // Calculate sizes for the material data section
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

        // Write MaterialFileHeader (16 bytes)
        bw.Write(mtrl.FileHeader.Version);

        // FileSize and DataSetSize packed into uint32 — we'll patch FileSize after writing
        long fileSizePos = ms.Position;
        bw.Write((uint)mtrl.FileHeader.FileSize | ((uint)mtrl.FileHeader.DataSetSize << 16));

        bw.Write(mtrl.FileHeader.StringTableSize);
        bw.Write(mtrl.FileHeader.ShaderPackageNameOffset);
        bw.Write(mtrl.FileHeader.TextureCount);
        bw.Write(mtrl.FileHeader.UvSetCount);
        bw.Write(mtrl.FileHeader.ColorSetCount);
        bw.Write(mtrl.FileHeader.AdditionalDataSize);

        // TextureOffsets (4 bytes each)
        for (int i = 0; i < mtrl.TextureOffsets.Length; i++)
        {
            uint packed = mtrl.TextureOffsets[i].Offset | ((uint)mtrl.TextureOffsets[i].Flags << 16);
            bw.Write(packed);
        }

        // UvColorSets (4 bytes each)
        for (int i = 0; i < mtrl.UvColorSets.Length; i++)
        {
            bw.Write(mtrl.UvColorSets[i].NameOffset);
            bw.Write(mtrl.UvColorSets[i].Index);
            bw.Write(mtrl.UvColorSets[i].Unknown1);
        }

        // ColorSets (4 bytes each)
        for (int i = 0; i < mtrl.ColorSets.Length; i++)
        {
            bw.Write(mtrl.ColorSets[i].NameOffset);
            bw.Write(mtrl.ColorSets[i].Index);
            bw.Write(mtrl.ColorSets[i].Unknown1);
        }

        // String table
        bw.Write(mtrl.Strings);

        // Additional data — preserve original bytes (contains DataFlags for HasColorTable, etc.)
        bw.Write(additionalData);

        // ── Section 2: Color set data ──

        if (mtrl.FileHeader.DataSetSize > 0)
        {
            // ColorSetInfo (512 bytes)
            unsafe
            {
                for (int i = 0; i < 256; i++)
                    bw.Write(mtrl.ColorSetInfo.Data[i]);
            }

            if (mtrl.FileHeader.DataSetSize > 512)
            {
                // ColorSetDyeInfo (32 bytes)
                unsafe
                {
                    for (int i = 0; i < 16; i++)
                        bw.Write(mtrl.ColorSetDyeInfo.Data[i]);
                }
            }
        }

        // ── Section 3: Material data ──

        // MaterialHeader (12 bytes)
        bw.Write(materialHeader.ShaderValueListSize);
        bw.Write(materialHeader.ShaderKeyCount);
        bw.Write(materialHeader.ConstantCount);
        bw.Write(materialHeader.SamplerCount);
        bw.Write(materialHeader.Unknown1);
        bw.Write(materialHeader.Unknown2);

        // ShaderKeys (8 bytes each)
        foreach (var key in shaderKeys)
        {
            bw.Write(key.Category);
            bw.Write(key.Value);
        }

        // Constants (8 bytes each)
        foreach (var c in constants)
        {
            bw.Write(c.ConstantId);
            bw.Write(c.ValueOffset);
            bw.Write(c.ValueSize);
        }

        // Samplers (12 bytes each)
        foreach (var s in samplers)
        {
            bw.Write(s.SamplerId);
            bw.Write(s.Flags);
            bw.Write(s.TextureIndex);
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((byte)0);
        }

        // ShaderValues (float array)
        foreach (var v in shaderValues)
            bw.Write(v);

        // Patch FileSize in the header
        var totalSize = (ushort)ms.Length;
        ms.Position = fileSizePos;
        bw.Write((uint)totalSize | ((uint)mtrl.FileHeader.DataSetSize << 16));

        bw.Flush();
        File.WriteAllBytes(outputPath, ms.ToArray());
        DebugServer.AppendLog($"[MtrlWriter] Rebuilt: {outputPath} ({ms.Length} bytes, {constants.Length} constants, {shaderValues.Length} values)");
    }
}
