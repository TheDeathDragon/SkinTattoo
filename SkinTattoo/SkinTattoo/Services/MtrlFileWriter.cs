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

    /// <summary>Write .mtrl with emissive enabled. Returns g_EmissiveColor byte offset.</summary>
    public static bool WriteEmissiveMtrl(MtrlFile mtrl, byte[] originalBytes, string outputPath,
        Vector3 emissiveColor, out int emissiveByteOffset)
    {
        emissiveByteOffset = -1;
        try
        {
            // Lumina skips AdditionalData  -- extract from raw bytes
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

            bool foundSkinType = false;
            for (int i = 0; i < shaderKeys.Length; i++)
            {
                if (shaderKeys[i].Category == CategorySkinType)
                {
                    shaderKeys[i].Value = ValueEmissive;
                    foundSkinType = true;
                    break;
                }
            }
            if (!foundSkinType)
            {
                var keyList = new List<ShaderKey>(shaderKeys);
                keyList.Add(new ShaderKey { Category = CategorySkinType, Value = ValueEmissive });
                shaderKeys = keyList.ToArray();
            }

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
                    break;
                }
            }

            if (!foundEmissive)
            {
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
            }

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

        bw.Write(mtrl.FileHeader.Version);

        long fileSizePos = ms.Position;
        bw.Write((uint)mtrl.FileHeader.FileSize | ((uint)mtrl.FileHeader.DataSetSize << 16));

        bw.Write(mtrl.FileHeader.StringTableSize);
        bw.Write(mtrl.FileHeader.ShaderPackageNameOffset);
        bw.Write(mtrl.FileHeader.TextureCount);
        bw.Write(mtrl.FileHeader.UvSetCount);
        bw.Write(mtrl.FileHeader.ColorSetCount);
        bw.Write(mtrl.FileHeader.AdditionalDataSize);

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

        for (int i = 0; i < mtrl.ColorSets.Length; i++)
        {
            bw.Write(mtrl.ColorSets[i].NameOffset);
            bw.Write(mtrl.ColorSets[i].Index);
            bw.Write(mtrl.ColorSets[i].Unknown1);
        }

        bw.Write(mtrl.Strings);
        bw.Write(additionalData);

        if (mtrl.FileHeader.DataSetSize > 0)
        {
            unsafe
            {
                for (int i = 0; i < 256; i++)
                    bw.Write(mtrl.ColorSetInfo.Data[i]);
            }

            if (mtrl.FileHeader.DataSetSize > 512)
            {
                unsafe
                {
                    for (int i = 0; i < 16; i++)
                        bw.Write(mtrl.ColorSetDyeInfo.Data[i]);
                }
            }
        }

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
        ms.Position = fileSizePos;
        bw.Write((uint)totalSize | ((uint)mtrl.FileHeader.DataSetSize << 16));

        bw.Flush();
        File.WriteAllBytes(outputPath, ms.ToArray());
    }
}
