using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SkinTattoo.Services;

/// <summary>
/// Runtime patcher: reads vanilla skin.shpk bytes, patches PS[19] DXBC to add
/// ColorTable emissive sampling (dcl_sampler s5, dcl_resource_texture2d t10,
/// replaces emissive mul+mul with mad+mov+sample), adds g_SamplerTable resource
/// entries, and rebuilds the complete .shpk binary.
///
/// Ported from ShaderPatcher/dxbc_patch_colortable.py + shpk_patcher.py.
/// </summary>
public static class SkinShpkPatcher
{
    private const uint CrcSamplerTable = 0x2005679F;

    // Material key CRCs. When `Mode == ValEmissive_v11b`, MtrlFileWriter forces the mtrl
    // onto (ValEmissive, ValDecalEmissive, ValVertexColorEmissive) so node selector picks
    // PS[19]-family. When `Mode == ValBody_v13`, mtrl keys are left as the body mod author
    // set them (typically ValBody), which routes through PS[8]-family.
    public const uint CategorySkinType = 0x380CAED0;
    public const uint ValueEmissive = 0x72E697CD;
    public const uint CategoryDecalMode = 0xD2777173;
    public const uint ValueDecalEmissive = 0x584265DD;
    public const uint CategoryVertexColorMode = 0xF52CCF05;
    public const uint ValueVertexColorEmissive = 0xA7D2FF60;

    /// <summary>
    /// Which PS family to inject ColorTable emissive into. v11b / v13 expose a visible
    /// trade-off on bibo/3BO bodies: ValEmissive preserves the bloom halo but reveals a
    /// mesh-boundary seam; ValBody eliminates the seam but loses bloom and may corrupt
    /// some deferred-shadow cascades. Caller flips this while we collect A/B data.
    /// </summary>
    public enum PatchMode
    {
        /// <summary>Patch 32 ValEmissive pass[2] PSes (19, 28, ... 382) and force mtrl
        /// keys to (ValEmissive, ValDecalEmissive, ValVertexColorEmissive). Pipeline has
        /// the full v8 emissive init replacement + pulse animation + tile-orb UV rewrite.
        /// Output: skin_ct_v11b.shpk</summary>
        ValEmissive_v11b,

        /// <summary>Patch 32 ValBody pass[2] PSes (8, 23, ... 377) with a single
        /// "inject emissive at output" stage. mtrl keeps its original ValBody keys,
        /// only ShaderPackageName is rewritten to skin_ct.shpk.
        /// Output: skin_ct_v13.shpk</summary>
        ValBody_v13,
    }

    public static PatchMode Mode { get; set; } = PatchMode.ValEmissive_v11b;

    // ValEmissive pass[2] lighting PS indices (v11b path).
    private static readonly int[] EmissivePsIndices = {
        19, 28, 49, 58, 79, 88, 109, 118,
        139, 148, 169, 178, 199, 208, 229, 238,
        247, 256, 265, 274, 283, 292, 301, 310,
        319, 328, 337, 346, 355, 364, 373, 382,
    };

    // ValEmissive pass[0] g-buffer-write PS indices (v11b path). These PSes write
    // skin-type / gloss-mask / tangent data into the g-buffer for pass[2] to consume;
    // they sample g_SamplerTileOrb with v2.zw (UV1) just like pass[2] does. bibo/3BO
    // body mods leave UV1 inconsistent between upper- and lower-body meshes, so the
    // gbuffer ends up with discontinuous tile-orb contribution -> pass[2] reads it
    // and the seam at the waist appears even after patching pass[2]. Rewriting the
    // UV source to v2.xy (UV0) here closes the last piece.
    private static readonly int[] EmissiveGbufferPsIndices = {
        17, 47, 77, 107, 137, 167, 197, 227,
    };

    // ValBody pass[2] lighting PS indices (v13 path).
    private static readonly int[] ValBodyPsIndices = {
        8, 23, 38, 53, 68, 83, 98, 113,
        128, 143, 158, 173, 188, 203, 218, 233,
        242, 251, 260, 269, 278, 287, 296, 305,
        314, 323, 332, 341, 350, 359, 368, 377,
    };

    private static int[] LightingPsIndices => Mode == PatchMode.ValBody_v13
        ? ValBodyPsIndices : EmissivePsIndices;

    // -- Public API ------------------------------------------------------

    /// <summary>
    /// Patch vanilla skin.shpk bytes to add ColorTable emissive support.
    /// Returns patched .shpk bytes, or null on failure (logged to DebugServer).
    /// </summary>
    public static byte[]? Patch(byte[] vanillaShpk)
    {
        try
        {
            var shpk = ParseShpk(vanillaShpk);

            // g_SamplerTable string is added once to the string section, reused by all PSes.
            int strOff = AddString(shpk, "g_SamplerTable");
            int strSz = "g_SamplerTable".Length;

            int success = 0, skipped = 0;
            foreach (int psIdx in LightingPsIndices)
            {
                var status = PatchSinglePs(shpk, psIdx, strOff, strSz);
                if (status) success++;
                else skipped++;
            }

            if (success == 0)
            {
                Log("All PS patches failed; aborting");
                return null;
            }

            // v11b extra pass: rewrite the tile-orb UV lookup from v2.zw (UV1) to v2.xy
            // (UV0) in every ValEmissive pass[0] g-buffer PS. Same rationale as the
            // corresponding pass[2] rewrite -- bibo/3BO meshes only author UV0 per-vertex
            // consistently.
            if (Mode == PatchMode.ValEmissive_v11b)
            {
                int gbSuccess = 0, gbSkipped = 0;
                foreach (int psIdx in EmissiveGbufferPsIndices)
                {
                    if (PatchSingleGbufferPsUvRewrite(shpk, psIdx)) gbSuccess++;
                    else gbSkipped++;
                }
                Log($"Gbuffer PS UV-rewrite: {gbSuccess}/{EmissiveGbufferPsIndices.Length} (skipped {gbSkipped})");
            }

            var result = RebuildShpk(shpk);
            Log($"Patched skin.shpk: {vanillaShpk.Length} -> {result.Length} bytes, PS patched {success}/{LightingPsIndices.Length} (skipped {skipped}) [anim-v3 per-layer]");

            DumpNodeSelectorTable(shpk);

            return result;
        }
        catch (Exception ex)
        {
            Log($"Patch failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Patch one PS in-place: rewrite DXBC SHEX (add s5/t10, replace emissive mul+mul
    /// with mad+mov+sample), update blob section + downstream shader offsets, add g_SamplerTable
    /// resource entries. Returns false if the PS blob is malformed or the emissive pattern
    /// cannot be located -- caller logs and continues with the remaining PSes.</summary>
    private static bool PatchSinglePs(ShpkFile shpk, int psIndex, int strOff, int strSz)
    {
        int targetIdx = shpk.VsCount + psIndex;
        if (targetIdx < 0 || targetIdx >= shpk.Shaders.Count)
        {
            Log($"PS[{psIndex}] out of range");
            return false;
        }

        var ps = shpk.Shaders[targetIdx];
        int blobStart = ps.BlobOff;
        int blobEnd = blobStart + ps.BlobSz;
        if (blobEnd > shpk.BlobSection.Count)
        {
            Log($"PS[{psIndex}] blob exceeds blob section");
            return false;
        }

        var originalDxbc = shpk.BlobSection.GetRange(blobStart, ps.BlobSz).ToArray();
        if (!IsDxbc(originalDxbc))
        {
            Log($"PS[{psIndex}] blob is not DXBC");
            return false;
        }

        var shexData = ExtractShexData(originalDxbc);
        if (shexData == null)
        {
            Log($"PS[{psIndex}] no SHEX/SHDR chunk");
            return false;
        }

        var withDecls = PatchShexAddDeclarations(shexData, 5, 10);
        if (withDecls == null) return false;

        byte[]? finalShex;
        if (Mode == PatchMode.ValBody_v13)
        {
            // v13 pipeline: inject emissive sample before the `mul o0.xyz + ret` tail of
            // each ValBody lighting PS. mtrl keeps ValBody shader keys -> avoids the
            // seam that ValEmissive g-buffer pipeline exposes on bibo/3BO bodies.
            finalShex = PatchShexInjectEmissiveAtOutput(withDecls);
            if (finalShex == null)
            {
                Log($"PS[{psIndex}] v13 output anchor (mul o0+ret) not found");
                return false;
            }
        }
        else
        {
            // v11b pipeline (original ValEmissive path).
            var withGlossMask = PatchShexInjectGlossMask(withDecls);
            if (withGlossMask == null)
            {
                Log($"PS[{psIndex}] gloss-mask pattern not found, continuing without seam fix");
                withGlossMask = withDecls;
            }

            var withReplacement = PatchShexReplaceEmissive(withGlossMask);
            if (withReplacement == null)
            {
                Log($"PS[{psIndex}] emissive pattern not found");
                return false;
            }

            var withCb0Ext = PatchShexExtendCb0(withReplacement, 20);
            if (withCb0Ext == null)
            {
                Log($"PS[{psIndex}] CB0 dcl not found");
                return false;
            }

            var withPulse = PatchShexInjectPulseModulation(withCb0Ext);
            if (withPulse == null)
            {
                Log($"PS[{psIndex}] pulse anchor not matched (non-r1 dest), static emissive only");
                withPulse = withCb0Ext;
            }

            var withTileUv = PatchShexTileOrbUseUv0(withPulse);
            if (withTileUv == null)
            {
                Log($"PS[{psIndex}] tile-orb UV1 pattern not found, skipping UV0 rewrite");
                withTileUv = withPulse;
            }

            finalShex = withTileUv;
        }

        var patchedDxbc = RebuildDxbc(originalDxbc, finalShex);

        int oldSize = ps.BlobSz;
        int newSize = patchedDxbc.Length;
        int delta = newSize - oldSize;

        shpk.BlobSection.RemoveRange(blobStart, oldSize);
        shpk.BlobSection.InsertRange(blobStart, patchedDxbc);
        ps.BlobSz = newSize;

        // Downstream shaders share the blob section -- shift their offsets by delta.
        foreach (var s in shpk.Shaders)
            if (s.BlobOff > blobStart)
                s.BlobOff += delta;

        var samplerRes = new ShpkResource
        {
            Id = CrcSamplerTable, StrOff = strOff, StrSz = (ushort)strSz,
            IsTex = 0, Slot = 5, Size = 5,
        };
        var textureRes = new ShpkResource
        {
            Id = CrcSamplerTable, StrOff = strOff, StrSz = (ushort)strSz,
            IsTex = 1, Slot = 10, Size = 6,
        };

        int insertSampAt = ps.CCnt + ps.SCnt;
        ps.Resources.Insert(insertSampAt, samplerRes);
        ps.Resources.Add(textureRes);
        ps.SCnt++;
        ps.TCnt++;

        return true;
    }

    /// <summary>Minimal pass[0] patch: only rewrites `mul rX.xy, v2.zwzz, cb0[7].xyxx`
    /// -> `mul rX.xy, v2.xyxx, cb0[7].xyxx` so the g-buffer pass picks the tile-orb UV
    /// from UV0 instead of UV1. Blob size stays identical (same instruction length) so
    /// no resource bookkeeping, no blob-offset shifting, no token count update are
    /// needed. If the anchor is absent the PS is left untouched.</summary>
    private static bool PatchSingleGbufferPsUvRewrite(ShpkFile shpk, int psIndex)
    {
        int targetIdx = shpk.VsCount + psIndex;
        if (targetIdx < 0 || targetIdx >= shpk.Shaders.Count) return false;
        var ps = shpk.Shaders[targetIdx];
        int blobStart = ps.BlobOff;
        if (blobStart + ps.BlobSz > shpk.BlobSection.Count) return false;

        var originalDxbc = shpk.BlobSection.GetRange(blobStart, ps.BlobSz).ToArray();
        if (!IsDxbc(originalDxbc)) return false;

        var shexData = ExtractShexData(originalDxbc);
        if (shexData == null) return false;

        var rewritten = PatchShexTileOrbUseUv0(shexData);
        if (rewritten == null) return false;

        // Size is identical, but the DXBC checksum still needs to be recomputed.
        var patchedDxbc = RebuildDxbc(originalDxbc, rewritten);
        if (patchedDxbc.Length != originalDxbc.Length)
        {
            Log($"PS[{psIndex}] gbuffer UV rewrite produced unexpected size change");
            return false;
        }

        for (int i = 0; i < patchedDxbc.Length; i++)
            shpk.BlobSection[blobStart + i] = patchedDxbc[i];
        return true;
    }

    // -- DXBC Checksum (custom MD5 variant from vkd3d-proton) ------------

    private static readonly uint[] Md5S =
    {
        7,12,17,22,7,12,17,22,7,12,17,22,7,12,17,22,
        5,9,14,20,5,9,14,20,5,9,14,20,5,9,14,20,
        4,11,16,23,4,11,16,23,4,11,16,23,4,11,16,23,
        6,10,15,21,6,10,15,21,6,10,15,21,6,10,15,21,
    };

    private static readonly uint[] Md5T =
    {
        0xd76aa478,0xe8c7b756,0x242070db,0xc1bdceee,0xf57c0faf,0x4787c62a,0xa8304613,0xfd469501,
        0x698098d8,0x8b44f7af,0xffff5bb1,0x895cd7be,0x6b901122,0xfd987193,0xa679438e,0x49b40821,
        0xf61e2562,0xc040b340,0x265e5a51,0xe9b6c7aa,0xd62f105d,0x02441453,0xd8a1e681,0xe7d3fbc8,
        0x21e1cde6,0xc33707d6,0xf4d50d87,0x455a14ed,0xa9e3e905,0xfcefa3f8,0x676f02d9,0x8d2a4c8a,
        0xfffa3942,0x8771f681,0x6d9d6122,0xfde5380c,0xa4beea44,0x4bdecfa9,0xf6bb4b60,0xbebfbc70,
        0x289b7ec6,0xeaa127fa,0xd4ef3085,0x04881d05,0xd9d4d039,0xe6db99e5,0x1fa27cf8,0xc4ac5665,
        0xf4292244,0x432aff97,0xab9423a7,0xfc93a039,0x655b59c3,0x8f0ccc92,0xffeff47d,0x85845dd1,
        0x6fa87e4f,0xfe2ce6e0,0xa3014314,0x4e0811a1,0xf7537e82,0xbd3af235,0x2ad7d2bb,0xeb86d391,
    };

    private static uint RotL32(uint x, int n) => (x << n) | (x >> (32 - n));

    private static (uint, uint, uint, uint) Md5Transform((uint, uint, uint, uint) state, ReadOnlySpan<byte> block)
    {
        var (a, b, c, d) = state;
        Span<uint> W = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
            W[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));

        for (int i = 0; i < 64; i++)
        {
            uint f; int g;
            if (i < 16) { f = (b & c) | (~b & d); g = i; }
            else if (i < 32) { f = (d & b) | (~d & c); g = (5 * i + 1) % 16; }
            else if (i < 48) { f = b ^ c ^ d; g = (3 * i + 5) % 16; }
            else { f = c ^ (b | ~d); g = (7 * i) % 16; }
            f = f + a + Md5T[i] + W[g];
            a = d; d = c; c = b;
            b = b + RotL32(f, (int)Md5S[i]);
        }

        return (state.Item1 + a, state.Item2 + b, state.Item3 + c, state.Item4 + d);
    }

    private static (uint, uint, uint, uint) DxbcChecksum(ReadOnlySpan<byte> blob)
    {
        // Input: blob[20:] (skip DXBC magic + checksum field)
        var data = blob.Slice(20);
        int length = data.Length;
        uint numBits = (uint)(length * 8);
        uint numBits2 = (numBits >> 2) | 1;
        var state = (0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u);

        int leftoverLength = length % 64;
        int fullEnd = length - leftoverLength;
        for (int off = 0; off < fullEnd; off += 64)
            state = Md5Transform(state, data.Slice(off, 64));

        var leftover = data.Slice(fullEnd);

        if (leftoverLength >= 56)
        {
            Span<byte> block1 = stackalloc byte[64];
            block1.Clear();
            leftover.CopyTo(block1);
            block1[leftoverLength] = 0x80;
            state = Md5Transform(state, block1);

            Span<byte> block2 = stackalloc byte[64];
            block2.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(block2, numBits);
            BinaryPrimitives.WriteUInt32LittleEndian(block2.Slice(60), numBits2);
            state = Md5Transform(state, block2);
        }
        else
        {
            Span<byte> combined = stackalloc byte[64];
            combined.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(combined, numBits);
            leftover.CopyTo(combined.Slice(4));
            combined[4 + leftoverLength] = 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(combined.Slice(60), numBits2);
            state = Md5Transform(state, combined);
        }

        return state;
    }

    // -- DXBC Container --------------------------------------------------

    private static bool IsDxbc(byte[] data) => data.Length >= 32
        && data[0] == (byte)'D' && data[1] == (byte)'X' && data[2] == (byte)'B' && data[3] == (byte)'C';

    private static byte[]? ExtractShexData(byte[] dxbc)
    {
        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(28));
        for (int i = 0; i < chunkCount; i++)
        {
            int off = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(32 + i * 4));
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(dxbc.AsSpan(off));
            int size = BinaryPrimitives.ReadInt32LittleEndian(dxbc.AsSpan(off + 4));
            // SHEX = 0x58454853, SHDR = 0x52444853
            if (magic == 0x58454853 || magic == 0x52444853)
                return dxbc.AsSpan(off + 8, size).ToArray();
        }
        return null;
    }

    private static byte[] RebuildDxbc(byte[] original, byte[] newShexData)
    {
        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(28));
        var chunks = new List<(uint Magic, byte[] Data)>();
        for (int i = 0; i < chunkCount; i++)
        {
            int off = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(32 + i * 4));
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(off));
            int size = BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(off + 4));
            var data = original.AsSpan(off + 8, size).ToArray();
            if (magic == 0x58454853 || magic == 0x52444853)
                chunks.Add((magic, newShexData));
            else
                chunks.Add((magic, data));
        }

        int headerSize = 32 + chunkCount * 4;
        int totalSize = headerSize;
        foreach (var (_, data) in chunks)
            totalSize += 8 + data.Length;

        var result = new byte[totalSize];
        var span = result.AsSpan();

        // Header
        result[0] = (byte)'D'; result[1] = (byte)'X'; result[2] = (byte)'B'; result[3] = (byte)'C';
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(20), 1); // version
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24), totalSize);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28), chunkCount);

        int offset = headerSize;
        for (int i = 0; i < chunks.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(32 + i * 4), offset);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset), chunks[i].Magic);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset + 4), chunks[i].Data.Length);
            chunks[i].Data.CopyTo(span.Slice(offset + 8));
            offset += 8 + chunks[i].Data.Length;
        }

        // Compute and write checksum
        var (c0, c1, c2, c3) = DxbcChecksum(result);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), c0);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), c1);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), c2);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16), c3);

        return result;
    }

    // -- SHEX Patching ---------------------------------------------------

    private static byte[]? PatchShexAddDeclarations(byte[] shexData, int samplerReg, int textureReg)
    {
        // Find end of declaration region (opcodes 0x58-0x6A)
        int pos = 8; // skip version + token count
        int lastDclEnd = pos;

        while (pos + 4 <= shexData.Length)
        {
            uint opcodeToken = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos));
            int opcode = (int)(opcodeToken & 0x7FF);
            int length = (int)((opcodeToken >> 24) & 0x7F);
            if (length == 0) length = 1;

            if (opcode >= 0x58 && opcode <= 0x6A)
                lastDclEnd = pos + length * 4;
            else
                break;

            pos += length * 4;
        }

        // dcl_sampler s{reg}: 3 tokens
        // dcl_resource_texture2d t{reg}: 4 tokens
        // Total: 7 tokens = 28 bytes
        var insert = new byte[28];
        var ins = insert.AsSpan();

        // dcl_sampler: opcode=0x5A, len=3, mode=0 (default)
        BinaryPrimitives.WriteUInt32LittleEndian(ins, (3u << 24) | 0x5A);
        BinaryPrimitives.WriteUInt32LittleEndian(ins.Slice(4), 0x00106000);
        BinaryPrimitives.WriteUInt32LittleEndian(ins.Slice(8), (uint)samplerReg);

        // dcl_resource_texture2d: opcode=0x58, len=4, dim=3 (texture2d)
        BinaryPrimitives.WriteUInt32LittleEndian(ins.Slice(12), (4u << 24) | (3u << 11) | 0x58);
        BinaryPrimitives.WriteUInt32LittleEndian(ins.Slice(16), 0x00107000);
        BinaryPrimitives.WriteUInt32LittleEndian(ins.Slice(20), (uint)textureReg);
        BinaryPrimitives.WriteUInt32LittleEndian(ins.Slice(24), 0x00005555); // float4 return type

        // Build result: [before] + [declarations] + [after]
        var result = new byte[shexData.Length + 28];
        shexData.AsSpan(0, lastDclEnd).CopyTo(result);
        insert.CopyTo(result.AsSpan(lastDclEnd));
        shexData.AsSpan(lastDclEnd).CopyTo(result.AsSpan(lastDclEnd + 28));

        // Update token count (+7)
        uint oldCount = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), oldCount + 7);

        return result;
    }

    // Anchor for the tail of every ValBody pass[2] PS in skin.shpk (verified across
    // PS[8..377]): the brightness-scale + return pair.
    //   mul  o0.xyz, r0.xyzx, cb1[3].xxxx        ; 8 tokens
    //   ret                                      ; 1 token
    // We deliberately exclude the preceding `sqrt r0.xyz` so we can inject emissive
    // *after* the gamma curve runs. Adding emissive before sqrt crushes the intensity
    // (e.g. em=5.3 -> sqrt(5.3)=2.3) and kills bloom-range HDR output; adding after
    // sqrt preserves the full linear emissive on top of the tone-mapped lighting.
    // Register operands r0 / o0 / cb1[3] are identical across every ValBody PS.
    private static readonly byte[] ValBodyOutputAnchor = ToLeBytes(
        // mul o0.xyz, r0.xyzx, cb1[3].xxxx
        0x08000038u, 0x00102072u, 0x00000000u, 0x00100246u, 0x00000000u,
        0x00208006u, 0x00000001u, 0x00000003u,
        // ret
        0x0100003Eu);

    // Instructions to inject immediately before ValBodyOutputAnchor (i.e. after
    // `sqrt r0.xyz` has already run, so emissive is added in gamma-mapped space):
    //   sample_indexable r1.xyzw, v2.xyxx, t5.xyzw, s1       ; re-sample normal (alpha = row key)
    //   mad r1.y, r1.w, l(0.9375), l(0.015625)               ; CT row UV (match ValEmissive path)
    //   mov r1.x, l(0.3125)                                  ; CT col 2 (emissive RGB column)
    //   sample_indexable r1.xyzw, r1.xyxx, t10.xyzw, s5      ; read emissive from ColorTable
    //   add r0.xyz, r0.xyzx, r1.xyzx                         ; accumulate HDR emissive atop output
    // 11+9+5+11+7 = 43 tokens = 172 bytes.
    //
    // Register choice: r1 is safe across all 32 PSes -- the last use of r1.xyzw in each PS
    // is `div o0.w, r1.x, r1.y` a few instructions before ValBodyOutputAnchor, so clobbering
    // r1 at the injection site has no downstream consumer.
    private static readonly byte[] ValBodyEmissiveInjection = ToLeBytes(
        // sample r1.xyzw, v2.xyxx, t5.xyzw, s1
        0x8B000045u, 0x800000C2u, 0x00155543u,
        0x001000F2u, 0x00000001u,
        0x00101046u, 0x00000002u,
        0x00107E46u, 0x00000005u,
        0x00106000u, 0x00000001u,
        // mad r1.y, r1.w, l(0.9375), l(0.015625)
        0x09000032u,
        0x00100022u, 0x00000001u,
        0x0010003Au, 0x00000001u,
        0x00004001u, 0x3F700000u,
        0x00004001u, 0x3C800000u,
        // mov r1.x, l(0.3125)
        0x05000036u,
        0x00100012u, 0x00000001u,
        0x00004001u, 0x3EA00000u,
        // sample r1.xyzw, r1.xyxx, t10.xyzw, s5
        0x8B000045u, 0x800000C2u, 0x00155543u,
        0x001000F2u, 0x00000001u,
        0x00100046u, 0x00000001u,
        0x00107E46u, 0x0000000Au,
        0x00106000u, 0x00000005u,
        // add r0.xyz, r0.xyzx, r1.xyzx
        0x07000000u,
        0x00100072u, 0x00000000u,
        0x00100246u, 0x00000000u,
        0x00100246u, 0x00000001u);

    /// <summary>Inject a ColorTable emissive sample + accumulate right before the output
    /// `sqrt + mul o0 + ret` tail of a ValBody pass[2] lighting PS. Bumps the SHEX token
    /// count by 43. Returns null if the anchor is not found.</summary>
    private static byte[]? PatchShexInjectEmissiveAtOutput(byte[] shexData)
    {
        int anchor = FindPattern(shexData, ValBodyOutputAnchor);
        if (anchor < 0) return null;

        var result = new byte[shexData.Length + ValBodyEmissiveInjection.Length];
        shexData.AsSpan(0, anchor).CopyTo(result);
        ValBodyEmissiveInjection.CopyTo(result.AsSpan(anchor));
        shexData.AsSpan(anchor).CopyTo(result.AsSpan(anchor + ValBodyEmissiveInjection.Length));

        // Bump SHEX token count by 43 (ValBodyEmissiveInjection.Length / 4).
        uint oldCount = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4),
            oldCount + (uint)(ValBodyEmissiveInjection.Length / 4));
        return result;
    }

    // Invariant tail of `mul rX.xyz, cb0[3].xyzx, cb0[3].xyzx` (last 6 of 9 tokens).
    // Only the first 3 tokens (opcode + dest operand + dest reg) vary by SceneKey.
    private static readonly byte[] EmissiveInitCb3Tail = ToLeBytes(
        0x00208246, 0x00000000, 0x00000003,
        0x00208246, 0x00000000, 0x00000003);

    private static byte[] BuildColorTableReplacement(uint destReg, uint normalReg, int normalComp)
    {
        uint madSrcNorm = 0x0010000Au | ((uint)normalComp << 4);  // TEMP + select_1 + component
        return ToLeBytes(
            // mad rDest.y, rNormal.<normalComp>, 0.9375, 0.015625
            0x09000032,
            0x00100022, destReg,
            madSrcNorm, normalReg,
            0x00004001, 0x3F700000,
            0x00004001, 0x3C800000,
            // mov rDest.x, 0.3125
            0x05000036,
            0x00100012, destReg,
            0x00004001, 0x3EA00000,
            // sample rDest.xyzw, rDest.xyxx, t10, s5
            0x8B000045, 0x800000C2, 0x00155543,
            0x001000F2, destReg,
            0x00100046, destReg,
            0x00107E46, 0x0000000A,
            0x00106000, 0x00000005);
    }

    /// <summary>Register-agnostic emissive-to-ColorTable replacement. Detects the init pair
    /// `mul rX.xyz, cb0[3]*cb0[3]` + `mul rX.xyz, rS.zzzz, rX.xyzx` and writes the ColorTable
    /// sample parameterized by (dest, normal, normalComp). Covers all 32 Emissive pass[2] PSes;
    /// the previous hardcoded-r1 version only matched 8/32 (one SceneKey band).</summary>
    private static byte[]? PatchShexReplaceEmissive(byte[] shexData)
    {
        int initPos = -1;
        uint destReg = 0;
        uint normalReg = 0;
        int normalComp = 0;

        int pos = 8;
        while (pos + 36 <= shexData.Length)
        {
            uint tok = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos));
            int opcode = (int)(tok & 0x7FF);
            int length = (int)((tok >> 24) & 0x7F);
            if (length == 0) length = 1;
            int blen = length * 4;
            if (pos + blen > shexData.Length) break;

            if (opcode == 0x38 && blen == 36 && MatchesAt(shexData, pos + 12, EmissiveInitCb3Tail))
            {
                uint destOp = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos + 4));
                if (destOp != 0x00100072u) { pos += blen; continue; }
                destReg = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos + 8));

                // Second MUL (7 tokens) carries the normal-alpha source operand.
                int next = pos + blen;
                if (next + 28 > shexData.Length) break;
                uint tok2 = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(next));
                if ((tok2 & 0x7FF) != 0x38 || ((tok2 >> 24) & 0x7F) * 4 != 28) { pos += blen; continue; }
                uint src0Op = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(next + 12));
                uint src0Reg = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(next + 16));
                int numComp = (int)(src0Op & 0x3);
                int selMode = (int)((src0Op >> 2) & 0x3);
                int opType = (int)((src0Op >> 12) & 0xFF);
                if (numComp != 2 || selMode != 1 || opType != 0) { pos += blen; continue; }
                // Require uniform swizzle (zzzz or wwww) to confirm single-component broadcast.
                int swiz = (int)((src0Op >> 4) & 0xFF);
                int c0 = swiz & 3;
                int c1 = (swiz >> 2) & 3;
                int c2 = (swiz >> 4) & 3;
                int c3 = (swiz >> 6) & 3;
                if (c0 != c1 || c1 != c2 || c2 != c3) { pos += blen; continue; }

                normalReg = src0Reg;
                normalComp = c0;
                initPos = pos;
                break;
            }
            pos += blen;
        }

        if (initPos < 0)
        {
            Log("Could not find emissive init (cb0[3]*cb0[3] pattern)");
            return null;
        }

        byte[] replacement = BuildColorTableReplacement(destReg, normalReg, normalComp);
        const int originalSize = 16 * 4;   // 9-token + 7-token MULs
        int newSize = replacement.Length;  // 25 tokens
        int tokenDelta = (newSize - originalSize) / 4;

        var result = new byte[shexData.Length + newSize - originalSize];
        shexData.AsSpan(0, initPos).CopyTo(result);
        replacement.CopyTo(result.AsSpan(initPos));
        shexData.AsSpan(initPos + originalSize).CopyTo(result.AsSpan(initPos + newSize));

        uint oldCount = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(oldCount + tokenDelta));
        return result;
    }

    // Anchor: the ColorTable sample instruction inserted by PatchShexReplaceEmissive.
    // `sample_indexable(texture2d)(float4) r1.xyzw, r1.xyxx, t10.xyzw, s5` (11 tokens, 44 bytes).
    // After this instruction, r1.xyz holds the per-layer emissive color and is consumed by
    // `mul r1.xyz, r1.xyzx, cb5[0].xyzx` (g_MaterialParameterDynamic.m_EmissiveColor) at the
    // end of the shader -- exactly the "emissive contribution only" vector we want to modulate.
    private static readonly byte[] EmissiveSampleAnchor = ToLeBytes(
        0x8B000045, 0x800000C2, 0x00155543,
        0x001000F2, 0x00000001,
        0x00100046, 0x00000001,
        0x00107E46, 0x0000000A,
        0x00106000, 0x00000005);

    // Animation modulation payload: 36 instructions, 276 tokens, 1104 bytes.
    // Modes: Pulse(0) / Flicker(1) / Gradient(2) / Ripple(3) + RippleDirection(radial/linear/bidir) + DualColor.
    //  - col 3 halfs 12..15 = speed, amp, mode, (unused)
    //  - col 4 halfs 17..19 = gradient/dual colorB RGB (half 16 = vanilla roughness, untouched)
    //  - col 5 halfs 20..23 = ripple centerU, centerV, freq, dirMode
    //  - col 6 halfs 24..26 = dirX, dirY, dualActive (half 27 unused)
    // C# sets dualActive=1 when (mode==Gradient) or (mode==Ripple && RippleDual).
    // dirMode: 0=radial, 1=linear, 2=bidirectional. Non-Ripple modes use freq=0 -> spatial offset is 0.
    //
    //   -- col 3 (anim params) --
    //   mov    r9.x, l(0.4375)                       ; col 3 U
    //   mad    r9.y, r0.z, l(0.9375), l(0.015625)    ; V
    //   sample r2.xyzw, r9.xyxx, t10, s5             ; r2.x=speed, .y=amp, .z=mode
    //   -- col 5 (ripple + dirMode) --
    //   mov    r9.z, l(0.6875)                       ; col 5 U
    //   sample r6.xyzw, r9.zyzz, t10, s5             ; r6.x=cU, .y=cV, .z=freq, .w=dirMode
    //   add    r8.xy, v2.xyxx, -r6.xyxx              ; d = uv - center
    //   dp2    r8.z, r8.xyxx, r8.xyxx                ; d.d
    //   sqrt   r8.z, r8.z                            ; distRadial
    //   -- col 6 (direction + dualActive) --
    //   mov    r9.w, l(0.8125)                       ; col 6 U
    //   sample r10.xyzw, r9.wyww, t10, s5            ; r10.x=dirX, .y=dirY, .z=dualActive
    //   dp2    r8.w, r8.xyxx, r10.xyxx               ; distLinear = d.dir
    //   ge     r4.x, r6.w, l(0.5)                    ; dirMode>=0.5? (linear or bidir)
    //   ge     r4.y, r6.w, l(1.5)                    ; dirMode>=1.5? (bidir)
    //   movc   r8.z, r4.x, r8.w, r8.z                ; pick linear vs radial
    //   movc   r8.z, r4.y, |r8.w|, r8.z              ; if bidir, use |linear|
    //   mul    r8.x, r6.z, r8.z                      ; spatialPhase = freq * dist
    //   -- phase --
    //   mul    r9.x, r2.x, cb2[0].x
    //   mul    r9.x, r9.x, l(6.283185)
    //   add    r9.x, r9.x, -r8.x
    //   sincos r9.x, null, r9.x
    //   -- Pulse/Flicker branch --
    //   mov    r7.w, r9.x
    //   ge     r9.z, r9.x, l(0)
    //   movc   r9.z, r9.z, l(1), l(-1)
    //   ge     r9.w, r2.z, l(0.5)
    //   movc   r9.x, r9.w, r9.z, r9.x
    //   mad    r9.x, r2.y, r9.x, l(1.0)
    //   mov    r7.xyz, r1.xyzx
    //   mul    r1.xyz, r1.xyzx, r9.xxxx
    //   -- Gradient / dual-color branch --
    //   mov    r9.z, l(0.5625)
    //   sample r3.xyzw, r9.zyzz, t10, s5
    //   mul    r4.x, r7.w, r2.y
    //   mad    r4.x, r4.x, l(0.5), l(0.5)
    //   add    r5.xyz, r3.yzwy, -r7.xyzx
    //   mad    r5.xyz, r4.xxxx, r5.xyzx, r7.xyzx
    //   ge     r4.y, r10.z, l(0.5)                    ; dualActive mask (from col 6)
    //   movc   r1.xyz, r4.yyyy, r5.xyzx, r1.xyzx     ; dual ? lerp : mono
    private static readonly byte[] PulsePayload = ToLeBytes(
        0x05000036, 0x00100012, 0x00000009, 0x00004001, 0x3EE00000,
        0x09000032, 0x00100022, 0x00000009, 0x0010002A, 0x00000000, 0x00004001, 0x3F700000, 0x00004001, 0x3C800000,
        0x8B000045, 0x800000C2, 0x00155543, 0x001000F2, 0x00000002, 0x00100046, 0x00000009, 0x00107E46, 0x0000000A, 0x00106000, 0x00000005,
        0x05000036, 0x00100042, 0x00000009, 0x00004001, 0x3F300000,
        0x8B000045, 0x800000C2, 0x00155543, 0x001000F2, 0x00000006, 0x00100A66, 0x00000009, 0x00107E46, 0x0000000A, 0x00106000, 0x00000005,
        0x08000000, 0x00100032, 0x00000008, 0x00101046, 0x00000002, 0x80100046, 0x00000041, 0x00000006,
        0x0700000F, 0x00100042, 0x00000008, 0x00100046, 0x00000008, 0x00100046, 0x00000008,
        0x0500004B, 0x00100042, 0x00000008, 0x0010002A, 0x00000008,
        0x05000036, 0x00100082, 0x00000009, 0x00004001, 0x3F500000,
        0x8B000045, 0x800000C2, 0x00155543, 0x001000F2, 0x0000000A, 0x00100F76, 0x00000009, 0x00107E46, 0x0000000A, 0x00106000, 0x00000005,
        0x0700000F, 0x00100082, 0x00000008, 0x00100046, 0x00000008, 0x00100046, 0x0000000A,
        0x0700001D, 0x00100012, 0x00000004, 0x0010003A, 0x00000006, 0x00004001, 0x3F000000,
        0x0700001D, 0x00100022, 0x00000004, 0x0010003A, 0x00000006, 0x00004001, 0x3FC00000,
        0x09000037, 0x00100042, 0x00000008, 0x0010000A, 0x00000004, 0x0010003A, 0x00000008, 0x0010002A, 0x00000008,
        0x0A000037, 0x00100042, 0x00000008, 0x0010001A, 0x00000004, 0x8010003A, 0x00000081, 0x00000008, 0x0010002A, 0x00000008,
        0x07000038, 0x00100012, 0x00000008, 0x0010002A, 0x00000006, 0x0010002A, 0x00000008,
        0x08000038, 0x00100012, 0x00000009, 0x0010000A, 0x00000002, 0x0020800A, 0x00000002, 0x00000000,
        0x07000038, 0x00100012, 0x00000009, 0x0010000A, 0x00000009, 0x00004001, 0x40C90FDA,
        0x08000000, 0x00100012, 0x00000009, 0x0010000A, 0x00000009, 0x8010000A, 0x00000041, 0x00000008,
        0x0600004D, 0x00100012, 0x00000009, 0x0000D000, 0x0010000A, 0x00000009,
        0x05000036, 0x00100082, 0x00000007, 0x0010000A, 0x00000009,
        0x0700001D, 0x00100042, 0x00000009, 0x0010000A, 0x00000009, 0x00004001, 0x00000000,
        0x09000037, 0x00100042, 0x00000009, 0x0010002A, 0x00000009, 0x00004001, 0x3F800000, 0x00004001, 0xBF800000,
        0x0700001D, 0x00100082, 0x00000009, 0x0010002A, 0x00000002, 0x00004001, 0x3F000000,
        0x09000037, 0x00100012, 0x00000009, 0x0010003A, 0x00000009, 0x0010002A, 0x00000009, 0x0010000A, 0x00000009,
        0x09000032, 0x00100012, 0x00000009, 0x0010001A, 0x00000002, 0x0010000A, 0x00000009, 0x00004001, 0x3F800000,
        0x05000036, 0x00100072, 0x00000007, 0x00100246, 0x00000001,
        0x07000038, 0x00100072, 0x00000001, 0x00100246, 0x00000001, 0x00100006, 0x00000009,
        0x05000036, 0x00100042, 0x00000009, 0x00004001, 0x3F100000,
        0x8B000045, 0x800000C2, 0x00155543, 0x001000F2, 0x00000003, 0x00100A66, 0x00000009, 0x00107E46, 0x0000000A, 0x00106000, 0x00000005,
        0x07000038, 0x00100012, 0x00000004, 0x0010003A, 0x00000007, 0x0010001A, 0x00000002,
        0x09000032, 0x00100012, 0x00000004, 0x0010000A, 0x00000004, 0x00004001, 0x3F000000, 0x00004001, 0x3F000000,
        0x08000000, 0x00100072, 0x00000005, 0x00100796, 0x00000003, 0x80100246, 0x00000041, 0x00000007,
        0x09000032, 0x00100072, 0x00000005, 0x00100006, 0x00000004, 0x00100246, 0x00000005, 0x00100246, 0x00000007,
        0x0700001D, 0x00100022, 0x00000004, 0x0010002A, 0x0000000A, 0x00004001, 0x3F000000,
        0x09000037, 0x00100072, 0x00000001, 0x00100556, 0x00000004, 0x00100246, 0x00000005, 0x00100246, 0x00000001);

    /// <summary>Insert pulse modulation payload after the ColorTable sample instruction
    /// so only the emissive term (r1.xyz) gets modulated.</summary>
    private static byte[]? PatchShexInjectPulseModulation(byte[] shexData)
    {
        int idx = FindPattern(shexData, EmissiveSampleAnchor);
        if (idx < 0) return null;
        int insertAt = idx + EmissiveSampleAnchor.Length;

        int payloadTokens = PulsePayload.Length / 4;
        var result = new byte[shexData.Length + PulsePayload.Length];
        shexData.AsSpan(0, insertAt).CopyTo(result);
        PulsePayload.CopyTo(result.AsSpan(insertAt));
        shexData.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + PulsePayload.Length));

        uint oldCount = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)(oldCount + payloadTokens));
        return result;
    }

    /// <summary>Insert `mul rC.<comp>, rC.<comp>, rS.<normalComp>` just before the
    /// `mul[_sat] rC.<comp>, rC.<comp>, v1.w` vertex-alpha multiplication so the main-surface
    /// accumulator picks up normal.alpha again (Body PS gloss-mask behavior). See docs Ch5 Path A.
    /// Returns null if either the emissive init pair or the vertex-alpha mul isn't found.</summary>
    private static byte[]? PatchShexInjectGlossMask(byte[] shexData)
    {
        int emissive2Pos = -1;
        int normalReg = 0;
        int normalComp = 0;

        // Scan for `mul rX.xyz, cb0[3]*cb0[3]` + `mul rX.xyz, rS.<swiz>, rX.xyzx` to extract
        // rS (normal-alpha source register) and its broadcasted component.
        int pos = 8;
        while (pos + 4 <= shexData.Length)
        {
            uint tok = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos));
            int opcode = (int)(tok & 0x7FF);
            int length = (int)((tok >> 24) & 0x7F);
            if (length == 0) length = 1;
            int blen = length * 4;
            if (pos + blen > shexData.Length) break;

            if (opcode == 0x38 && blen == 36 && MatchesAt(shexData, pos + 12, EmissiveInitCb3Tail))
            {
                int nextPos = pos + blen;
                if (nextPos + 28 <= shexData.Length)
                {
                    uint tok2 = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(nextPos));
                    if ((tok2 & 0x7FF) == 0x38 && ((tok2 >> 24) & 0x7F) * 4 == 28)
                    {
                        uint src0Tok = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(nextPos + 12));
                        uint src0Reg = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(nextPos + 16));
                        int numComp = (int)(src0Tok & 0x3);
                        int selMode = (int)((src0Tok >> 2) & 0x3);
                        int opType = (int)((src0Tok >> 12) & 0xFF);
                        if (numComp == 2 && selMode == 1 && opType == 0)
                        {
                            int swiz = (int)((src0Tok >> 4) & 0xFF);
                            int c0 = swiz & 0x3;
                            // Require uniform broadcast (.zzzz or .wwww) to confirm single-component source.
                            if (c0 == ((swiz >> 2) & 0x3) && c0 == ((swiz >> 4) & 0x3) && c0 == ((swiz >> 6) & 0x3))
                            {
                                emissive2Pos = nextPos;
                                normalReg = (int)src0Reg;
                                normalComp = c0;
                                break;
                            }
                        }
                    }
                }
            }
            pos += blen;
        }

        if (emissive2Pos < 0)
            return null;

        // Locate `mul[_sat] rC.<comp>, rC.<comp>, v1.w` (dest register == src0 register,
        // both single-component; v1.w = INPUT reg 1, select_1, component w).
        int targetPos = -1;
        int destReg = 0;
        int destComp = 0;
        int scan = emissive2Pos + 28;
        while (scan + 28 <= shexData.Length)
        {
            uint tok = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(scan));
            int opcode = (int)(tok & 0x7FF);
            int length = (int)((tok >> 24) & 0x7F);
            if (length == 0) length = 1;
            int blen = length * 4;
            if (scan + blen > shexData.Length) break;

            if (opcode == 0x38 && blen == 28)
            {
                uint src1Tok = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(scan + 20));
                uint src1Reg = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(scan + 24));
                int src1OpType = (int)((src1Tok >> 12) & 0xFF);
                int src1SelMode = (int)((src1Tok >> 2) & 0x3);
                int src1Comp = (int)((src1Tok >> 4) & 0x3);
                if (src1OpType == 1 && src1SelMode == 2 && src1Comp == 3 && src1Reg == 1)
                {
                    uint destTok = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(scan + 4));
                    uint destRegVal = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(scan + 8));
                    uint src0Tok = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(scan + 12));
                    uint src0Reg = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(scan + 16));

                    int destNum = (int)(destTok & 0x3);
                    int destMode = (int)((destTok >> 2) & 0x3);
                    int destType = (int)((destTok >> 12) & 0xFF);
                    int destMask = (int)((destTok >> 4) & 0xF);
                    int src0Num = (int)(src0Tok & 0x3);
                    int src0Mode = (int)((src0Tok >> 2) & 0x3);
                    int src0Type = (int)((src0Tok >> 12) & 0xFF);
                    int src0Comp = (int)((src0Tok >> 4) & 0x3);

                    if (destNum == 2 && destMode == 0 && destType == 0
                        && src0Num == 2 && src0Mode == 2 && src0Type == 0
                        && destRegVal == src0Reg
                        && (destMask == 1 || destMask == 2 || destMask == 4 || destMask == 8))
                    {
                        int dc = destMask switch { 1 => 0, 2 => 1, 4 => 2, _ => 3 };
                        if (src0Comp == dc)
                        {
                            targetPos = scan;
                            destReg = (int)destRegVal;
                            destComp = dc;
                            break;
                        }
                    }
                }
            }
            scan += blen;
        }

        if (targetPos < 0)
            return null;

        int destMaskBit = 1 << destComp;
        uint destOperand = 0x00100002u | (uint)(destMaskBit << 4);     // TEMP + mask mode + single-bit mask
        uint srcDestSel = 0x0010000Au | (uint)(destComp << 4);         // TEMP + select_1 on dest's component
        uint srcNormSel = 0x0010000Au | (uint)(normalComp << 4);       // TEMP + select_1 on normal's component
        byte[] newInst = ToLeBytes(
            0x07000038,
            destOperand, (uint)destReg,
            srcDestSel, (uint)destReg,
            srcNormSel, (uint)normalReg);

        var result = new byte[shexData.Length + newInst.Length];
        shexData.AsSpan(0, targetPos).CopyTo(result);
        newInst.CopyTo(result.AsSpan(targetPos));
        shexData.AsSpan(targetPos).CopyTo(result.AsSpan(targetPos + newInst.Length));

        uint oldCount = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), oldCount + 7);
        return result;
    }

    // Anchor for the tile-orb UV mul: `mul rX.xy, v2.zwzz, cb0[7].xyxx`.
    // The last 20 bytes (src0 + src1 with their index u32s) are invariant across
    // all 32 emissive PSes; only opcode/dst (which vary by register allocation) precede it.
    //   src0 operand  = 0x00101AE6  (INPUT v[], 1D index imm32, swizzle=0xAE=zwzz)
    //   src0 reg idx  = 2           (v2)
    //   src1 operand  = 0x00208046  (CB, 2D index imm32, swizzle=0x04=xyxx)
    //   src1 cb idx   = 0
    //   src1 elem idx = 7
    private static readonly byte[] TileOrbUv1Tail = ToLeBytes(
        0x00101AE6, 0x00000002, 0x00208046, 0x00000000, 0x00000007);

    /// <summary>Rewrite `mul rX.xy, v2.zwzz, cb0[7].xyxx` -> `mul rX.xy, v2.xyxx, cb0[7].xyxx`
    /// by flipping the src0 swizzle field from 0xAE (zwzz) to 0x04 (xyxx). In-place, same
    /// instruction length, so no SHEX token count update is needed.</summary>
    private static byte[]? PatchShexTileOrbUseUv0(byte[] shexData)
    {
        int idx = FindPattern(shexData, TileOrbUv1Tail);
        if (idx < 0) return null;

        var result = (byte[])shexData.Clone();
        // TileOrbUv1Tail starts at src0 operand. Rewrite that u32's swizzle bits (4..11)
        // from 0xAE to 0x04. Keep everything else (num_components, select_mode, op_type,
        // index_dim, index0_repr) identical -- mask those bits and OR in the new swizzle.
        uint src0 = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(idx));
        uint rebuilt = (src0 & ~0x0FF0u) | (0x04u << 4);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(idx), rebuilt);
        return result;
    }

    /// <summary>Find the dcl_constantbuffer for CB0 and bump its array size to newSize.
    /// In-place modification (no byte count change, no token count update needed).</summary>
    private static byte[]? PatchShexExtendCb0(byte[] shexData, int newSize)
    {
        int pos = 8;
        while (pos + 16 <= shexData.Length)
        {
            uint opcodeToken = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos));
            int opcode = (int)(opcodeToken & 0x7FF);
            int length = (int)((opcodeToken >> 24) & 0x7F);
            if (length == 0) length = 1;

            if (opcode < 0x58 || opcode > 0x6A) break;

            if (opcode == 0x59 && length == 4)
            {
                uint cbReg = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos + 8));
                if (cbReg == 0)
                {
                    uint currentSize = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos + 12));
                    if (currentSize >= (uint)newSize)
                    {
                        Log($"CB0 dcl already >= {newSize} (current={currentSize})");
                        return shexData;
                    }
                    var result = (byte[])shexData.Clone();
                    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(pos + 12), (uint)newSize);
                    return result;
                }
            }
            pos += length * 4;
        }
        return null;
    }

    // -- SHPK Parse/Rebuild ----------------------------------------------

    private class ShpkResource
    {
        public uint Id;
        public int StrOff;
        public ushort StrSz;
        public ushort IsTex;
        public ushort Slot;
        public ushort Size;
    }

    private class ShpkShader
    {
        public int BlobOff;
        public int BlobSz;
        public int CCnt, SCnt, UavCnt, TCnt;
        public uint Unk131;
        public List<ShpkResource> Resources = new();
    }

    private class ShpkNode
    {
        public uint Selector;
        public int PassCount;
        public byte[] PassIndices = new byte[16];
        public uint[] Unk131Keys = new uint[2];
        public uint[] SysKeys = Array.Empty<uint>();
        public uint[] SceneKeys = Array.Empty<uint>();
        public uint[] MatKeys = Array.Empty<uint>();
        public uint[] SvKeys = new uint[2];
        public List<(uint Id, uint Vs, uint Ps, uint A, uint B, uint C)> Passes = new();
    }

    private class ShpkFile
    {
        public uint Version, Dx;
        public int VsCount, PsCount;
        public int MatParamsSize, MatParamCount;
        public ushort HasDefaults;
        public int ConstCount;
        public ushort SampCount, TexCount;
        public int UavCount;
        public int SysKeyCount, SceneKeyCount, MatKeyCount;
        public uint[] UnkAbc = new uint[3];
        public List<ShpkShader> Shaders = new();
        public byte[] MatParamsRaw = Array.Empty<byte>();
        public byte[] MatDefaultsRaw = Array.Empty<byte>();
        public byte[] GlobalConsts = Array.Empty<byte>();
        public byte[] GlobalSamplers = Array.Empty<byte>();
        public byte[] GlobalTextures = Array.Empty<byte>();
        public byte[] GlobalUavs = Array.Empty<byte>();
        public List<(uint Kid, uint Def)> SysKeys = new();
        public List<(uint Kid, uint Def)> SceneKeys = new();
        public List<(uint Kid, uint Def)> MatKeys = new();
        public uint[] SvDefaults = new uint[2];
        public List<ShpkNode> Nodes = new();
        public List<(uint A, uint B)> Aliases = new();
        public byte[] Additional = Array.Empty<byte>();
        public List<byte> BlobSection = new();
        public List<byte> StringSection = new();
    }

    private static ShpkFile ParseShpk(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);

        var shpk = new ShpkFile();
        r.ReadUInt32(); // magic (ShPk)
        shpk.Version = r.ReadUInt32();
        shpk.Dx = r.ReadUInt32();
        r.ReadUInt32(); // file_size
        int blobsOff = r.ReadInt32();
        int stringsOff = r.ReadInt32();
        shpk.VsCount = r.ReadInt32();
        shpk.PsCount = r.ReadInt32();
        shpk.MatParamsSize = r.ReadInt32();
        shpk.MatParamCount = r.ReadUInt16();
        shpk.HasDefaults = r.ReadUInt16();
        shpk.ConstCount = r.ReadInt32();
        shpk.SampCount = r.ReadUInt16();
        shpk.TexCount = r.ReadUInt16();
        shpk.UavCount = r.ReadInt32();
        shpk.SysKeyCount = r.ReadInt32();
        shpk.SceneKeyCount = r.ReadInt32();
        shpk.MatKeyCount = r.ReadInt32();
        int nodeCount = r.ReadInt32();
        int aliasCount = r.ReadInt32();
        if (shpk.Version >= 0x0D01)
            for (int i = 0; i < 3; i++) shpk.UnkAbc[i] = r.ReadUInt32();

        // Shader entries
        int totalShaders = shpk.VsCount + shpk.PsCount;
        for (int i = 0; i < totalShaders; i++)
        {
            var s = new ShpkShader
            {
                BlobOff = r.ReadInt32(), BlobSz = r.ReadInt32(),
                CCnt = r.ReadUInt16(), SCnt = r.ReadUInt16(),
                UavCnt = r.ReadUInt16(), TCnt = r.ReadUInt16(),
            };
            if (shpk.Version >= 0x0D01) s.Unk131 = r.ReadUInt32();
            int resCnt = s.CCnt + s.SCnt + s.UavCnt + s.TCnt;
            for (int j = 0; j < resCnt; j++)
            {
                s.Resources.Add(new ShpkResource
                {
                    Id = r.ReadUInt32(), StrOff = r.ReadInt32(),
                    StrSz = r.ReadUInt16(), IsTex = r.ReadUInt16(),
                    Slot = r.ReadUInt16(), Size = r.ReadUInt16(),
                });
            }
            shpk.Shaders.Add(s);
        }

        // Material params
        shpk.MatParamsRaw = r.ReadBytes(shpk.MatParamCount * 8);
        if (shpk.HasDefaults != 0) shpk.MatDefaultsRaw = r.ReadBytes(shpk.MatParamsSize);

        // Global resources
        shpk.GlobalConsts = r.ReadBytes(shpk.ConstCount * 16);
        shpk.GlobalSamplers = r.ReadBytes(shpk.SampCount * 16);
        shpk.GlobalTextures = r.ReadBytes(shpk.TexCount * 16);
        shpk.GlobalUavs = r.ReadBytes(shpk.UavCount * 16);

        // Keys
        for (int i = 0; i < shpk.SysKeyCount; i++) shpk.SysKeys.Add((r.ReadUInt32(), r.ReadUInt32()));
        for (int i = 0; i < shpk.SceneKeyCount; i++) shpk.SceneKeys.Add((r.ReadUInt32(), r.ReadUInt32()));
        for (int i = 0; i < shpk.MatKeyCount; i++) shpk.MatKeys.Add((r.ReadUInt32(), r.ReadUInt32()));
        shpk.SvDefaults[0] = r.ReadUInt32(); shpk.SvDefaults[1] = r.ReadUInt32();

        // Nodes
        for (int ni = 0; ni < nodeCount; ni++)
        {
            var n = new ShpkNode
            {
                Selector = r.ReadUInt32(),
                PassCount = r.ReadInt32(),
                PassIndices = r.ReadBytes(16),
            };
            n.Unk131Keys[0] = r.ReadUInt32(); n.Unk131Keys[1] = r.ReadUInt32();
            n.SysKeys = new uint[shpk.SysKeyCount];
            for (int i = 0; i < shpk.SysKeyCount; i++) n.SysKeys[i] = r.ReadUInt32();
            n.SceneKeys = new uint[shpk.SceneKeyCount];
            for (int i = 0; i < shpk.SceneKeyCount; i++) n.SceneKeys[i] = r.ReadUInt32();
            n.MatKeys = new uint[shpk.MatKeyCount];
            for (int i = 0; i < shpk.MatKeyCount; i++) n.MatKeys[i] = r.ReadUInt32();
            n.SvKeys[0] = r.ReadUInt32(); n.SvKeys[1] = r.ReadUInt32();
            for (int pi = 0; pi < n.PassCount; pi++)
                n.Passes.Add((r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(),
                    r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32()));
            shpk.Nodes.Add(n);
        }

        // Aliases
        for (int i = 0; i < aliasCount; i++) shpk.Aliases.Add((r.ReadUInt32(), r.ReadUInt32()));

        // Additional data between current position and blobs offset
        int pos = (int)ms.Position;
        if (pos < blobsOff)
            shpk.Additional = r.ReadBytes(blobsOff - pos);

        // Blob and string sections
        shpk.BlobSection = new List<byte>(data[blobsOff..stringsOff]);
        shpk.StringSection = new List<byte>(data[stringsOff..]);

        return shpk;
    }

    /// <summary>Parse vanilla bytes and dump node selector table for diagnostics only. No patching.</summary>
    public static void DumpFromBytes(byte[] shpkBytes)
    {
        try
        {
            var shpk = ParseShpk(shpkBytes);
            DumpNodeSelectorTable(shpk);
        }
        catch (Exception ex) { Log($"DumpFromBytes failed: {ex.Message}"); }
    }

    private static void DumpNodeSelectorTable(ShpkFile shpk)
    {
        try
        {
            Log($"NodeDump: nodes={shpk.Nodes.Count} sysKeys={shpk.SysKeyCount} sceneKeys={shpk.SceneKeyCount} matKeys={shpk.MatKeyCount}");

            int skinTypeIdx = -1, decalIdx = -1, vcIdx = -1;
            for (int i = 0; i < shpk.MatKeys.Count; i++)
            {
                var (kid, def) = shpk.MatKeys[i];
                Log($"NodeDump: MatKey[{i}] id=0x{kid:X8} default=0x{def:X8}");
                if (kid == CategorySkinType) skinTypeIdx = i;
                else if (kid == CategoryDecalMode) decalIdx = i;
                else if (kid == CategoryVertexColorMode) vcIdx = i;
            }
            Log($"NodeDump: indices SkinType={skinTypeIdx} DecalMode={decalIdx} VertexColor={vcIdx}");

            // Pass 1: aggregate stats + dump first few full MATCH nodes.
            // Goal: discover which pass slot / SubViewIndex actually routes to PS[19].
            int matchedNodes = 0;
            int matchedNodesWithPs19 = 0;
            int matchedFullDumped = 0;
            int ps19Nodes = 0;

            // Track which pass slot (0..4) PS[19] appears in across all MATCH nodes,
            // and which SubViewIndex (0..15) maps to that slot in PassIndices.
            var ps19PassSlotCounts = new int[16];     // pass index -> count
            var ps19SubViewCounts = new int[16];      // subview index -> count

            // Track distinct sceneKey/sysKey combos for matched nodes -- useful for
            // inferring whether scene/sys keys partition Emissive variants further.
            var matchedSceneKeyValues = new HashSet<string>();
            var matchedSysKeyValues = new HashSet<string>();

            // Per-pass-slot: which PS values appear at that slot across MATCH nodes.
            // pass[2] is the lighting pass (where we need ColorTable patching). Tracking
            // all slots so we can confirm pattern + decide whether GBuffer/shadow PS also need patching.
            var perSlotPsCount = new Dictionary<int, Dictionary<uint, int>>();
            for (int i = 0; i < 5; i++) perSlotPsCount[i] = new Dictionary<uint, int>();

            for (int ni = 0; ni < shpk.Nodes.Count; ni++)
            {
                var n = shpk.Nodes[ni];

                bool matchKeys = skinTypeIdx >= 0 && decalIdx >= 0 && vcIdx >= 0
                    && n.MatKeys[skinTypeIdx] == ValueEmissive
                    && n.MatKeys[decalIdx] == ValueDecalEmissive
                    && n.MatKeys[vcIdx] == ValueVertexColorEmissive;

                // PS[19] probe: representative lighting PS, kept so existing NodeDump output
                // remains comparable with prior diagnostic logs even after multi-PS patching.
                const int ProbePs = 19;
                bool hasPs19 = false;
                int ps19PassSlot = -1;
                for (int pi = 0; pi < n.Passes.Count; pi++)
                {
                    if (n.Passes[pi].Ps == ProbePs)
                    {
                        hasPs19 = true;
                        if (ps19PassSlot < 0) ps19PassSlot = pi;
                    }
                }

                if (matchKeys)
                {
                    matchedNodes++;
                    matchedSceneKeyValues.Add(string.Join(",", Array.ConvertAll(n.SceneKeys, v => $"0x{v:X8}")));
                    matchedSysKeyValues.Add(string.Join(",", Array.ConvertAll(n.SysKeys, v => $"0x{v:X8}")));

                    for (int pi = 0; pi < n.Passes.Count && pi < 5; pi++)
                    {
                        var ps = n.Passes[pi].Ps;
                        var dict = perSlotPsCount[pi];
                        dict[ps] = dict.TryGetValue(ps, out var c) ? c + 1 : 1;
                    }

                    if (hasPs19)
                    {
                        matchedNodesWithPs19++;
                        if (ps19PassSlot >= 0 && ps19PassSlot < 16)
                            ps19PassSlotCounts[ps19PassSlot]++;
                        for (int sv = 0; sv < 16; sv++)
                            if (n.PassIndices[sv] == ps19PassSlot)
                                ps19SubViewCounts[sv]++;
                    }

                    // Full dump for first 4 matched nodes: all 5 pass.Ps + PassIndices[16]
                    if (matchedFullDumped < 4)
                    {
                        matchedFullDumped++;
                        var passDescs = new List<string>();
                        for (int pi = 0; pi < n.Passes.Count; pi++)
                            passDescs.Add($"[{pi}]Vs={n.Passes[pi].Vs}/Ps={n.Passes[pi].Ps}");
                        var pi16 = string.Join(",", n.PassIndices);
                        var svKeyStr = string.Join(",", Array.ConvertAll(n.SvKeys, v => $"0x{v:X8}"));
                        var sceneKeyStr = string.Join(",", Array.ConvertAll(n.SceneKeys, v => $"0x{v:X8}"));
                        Log($"NodeDump: MATCH-FULL[{ni}] sel=0x{n.Selector:X8} passCount={n.PassCount} " +
                            $"passes={{{string.Join(" ", passDescs)}}} " +
                            $"PassIndices[16]=[{pi16}] " +
                            $"sv=[{svKeyStr}] scene=[{sceneKeyStr}]");
                    }
                }
                else if (hasPs19)
                {
                    ps19Nodes++;
                    if (ps19Nodes <= 4) // cap log volume
                    {
                        string sk = skinTypeIdx >= 0 ? $"skin=0x{n.MatKeys[skinTypeIdx]:X8}" : "";
                        string dm = decalIdx >= 0 ? $" decal=0x{n.MatKeys[decalIdx]:X8}" : "";
                        string vc = vcIdx >= 0 ? $" vc=0x{n.MatKeys[vcIdx]:X8}" : "";
                        Log($"NodeDump: PS19-OTHER[{ni}] sel=0x{n.Selector:X8} passCount={n.PassCount} {sk}{dm}{vc} slot={ps19PassSlot}");
                    }
                }
            }
            Log($"NodeDump: totalMatchedForcedKeys={matchedNodes} matchedWithPS19={matchedNodesWithPs19} " +
                $"otherNodesReachingPS19={ps19Nodes}");
            Log($"NodeDump: matchedSceneKeyVariants={matchedSceneKeyValues.Count} matchedSysKeyVariants={matchedSysKeyValues.Count}");

            // Histogram: which pass slot does PS[19] live in?
            var slotHist = new List<string>();
            for (int i = 0; i < 16; i++)
                if (ps19PassSlotCounts[i] > 0) slotHist.Add($"slot[{i}]={ps19PassSlotCounts[i]}");
            Log($"NodeDump: PS19 pass-slot histogram (matched nodes): {string.Join(" ", slotHist)}");

            // Histogram: which SubViewIndex(es) route to PS[19]?
            var svHist = new List<string>();
            for (int i = 0; i < 16; i++)
                if (ps19SubViewCounts[i] > 0) svHist.Add($"sv[{i}]={ps19SubViewCounts[i]}");
            Log($"NodeDump: PS19 SubViewIndex histogram (matched nodes): {string.Join(" ", svHist)}");

            // Per-pass-slot PS distribution. The unique PS list at slot 2 is the
            // EXACT set of lighting PSes we need to patch for face/body emissive to work
            // across all sceneKey states.
            for (int slot = 0; slot < 5; slot++)
            {
                var dict = perSlotPsCount[slot];
                if (dict.Count == 0) continue;
                var sorted = new SortedDictionary<uint, int>(dict);
                var pairs = new List<string>();
                foreach (var kv in sorted) pairs.Add($"{kv.Key}x{kv.Value}");
                Log($"NodeDump: pass[{slot}] PS distribution ({dict.Count} unique): {string.Join(",", pairs)}");
            }
            // Pure list of unique pass[2].Ps (lighting pass) -- copy this into the patcher.
            var slot2Unique = new SortedSet<uint>(perSlotPsCount[2].Keys);
            Log($"NodeDump: pass[2] LIGHTING PSes to patch ({slot2Unique.Count}): [{string.Join(",", slot2Unique)}]");

            // Also: find all nodes whose SkinType matches each vanilla value, showing
            // which PS they route to. Useful to verify how face nodes select.
            if (skinTypeIdx >= 0)
            {
                var vanillaSkinValues = new (uint Val, string Name)[]
                {
                    (0x2BDB45F1, "Body"), (0xF5673524, "Face"),
                    (0x57FF3B64, "BodyJJM"), (0x72E697CD, "Emissive"),
                };
                foreach (var (val, name) in vanillaSkinValues)
                {
                    int count = 0;
                    var psSet = new HashSet<uint>();
                    foreach (var n in shpk.Nodes)
                    {
                        if (n.MatKeys[skinTypeIdx] != val) continue;
                        count++;
                        foreach (var p in n.Passes) psSet.Add(p.Ps);
                    }
                    var psStr = string.Join(",", new SortedSet<uint>(psSet));
                    Log($"NodeDump: SkinType={name}(0x{val:X8}) nodes={count} PSes=[{psStr}]");
                }
            }
        }
        catch (Exception ex) { Log($"NodeDump failed: {ex.Message}"); }
    }

    private static int AddString(ShpkFile shpk, string s)
    {
        int offset = shpk.StringSection.Count;
        shpk.StringSection.AddRange(Encoding.ASCII.GetBytes(s));
        shpk.StringSection.Add(0);
        return offset;
    }

    private static byte[] RebuildShpk(ShpkFile shpk)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        int nodeCount = shpk.Nodes.Count;
        int aliasCount = shpk.Aliases.Count;

        // Header (placeholders for file_size, blobs_off, strings_off)
        w.Write(0x6B506853u); // ShPk
        w.Write(shpk.Version);
        w.Write(shpk.Dx);
        long fileSizePos = ms.Position; w.Write(0); // file_size placeholder
        long blobsOffPos = ms.Position; w.Write(0); // blobs_off placeholder
        long stringsOffPos = ms.Position; w.Write(0); // strings_off placeholder
        w.Write(shpk.VsCount);
        w.Write(shpk.PsCount);
        w.Write(shpk.MatParamsSize);
        w.Write((ushort)shpk.MatParamCount); w.Write(shpk.HasDefaults);
        w.Write(shpk.ConstCount);
        w.Write(shpk.SampCount); w.Write(shpk.TexCount);
        w.Write(shpk.UavCount);
        w.Write(shpk.SysKeyCount); w.Write(shpk.SceneKeyCount); w.Write(shpk.MatKeyCount);
        w.Write(nodeCount);
        w.Write(aliasCount);
        foreach (var v in shpk.UnkAbc) w.Write(v);

        // Shader entries
        foreach (var s in shpk.Shaders)
        {
            w.Write(s.BlobOff); w.Write(s.BlobSz);
            w.Write((ushort)s.CCnt); w.Write((ushort)s.SCnt);
            w.Write((ushort)s.UavCnt); w.Write((ushort)s.TCnt);
            if (shpk.Version >= 0x0D01) w.Write(s.Unk131);
            foreach (var r in s.Resources)
            {
                w.Write(r.Id); w.Write(r.StrOff);
                w.Write(r.StrSz); w.Write(r.IsTex);
                w.Write(r.Slot); w.Write(r.Size);
            }
        }

        // Material params
        w.Write(shpk.MatParamsRaw);
        if (shpk.HasDefaults != 0) w.Write(shpk.MatDefaultsRaw);

        // Global resources
        w.Write(shpk.GlobalConsts);
        w.Write(shpk.GlobalSamplers);
        w.Write(shpk.GlobalTextures);
        w.Write(shpk.GlobalUavs);

        // Keys
        foreach (var (kid, def) in shpk.SysKeys) { w.Write(kid); w.Write(def); }
        foreach (var (kid, def) in shpk.SceneKeys) { w.Write(kid); w.Write(def); }
        foreach (var (kid, def) in shpk.MatKeys) { w.Write(kid); w.Write(def); }
        foreach (var v in shpk.SvDefaults) w.Write(v);

        // Nodes
        foreach (var n in shpk.Nodes)
        {
            w.Write(n.Selector);
            w.Write(n.PassCount);
            w.Write(n.PassIndices);
            foreach (var v in n.Unk131Keys) w.Write(v);
            foreach (var v in n.SysKeys) w.Write(v);
            foreach (var v in n.SceneKeys) w.Write(v);
            foreach (var v in n.MatKeys) w.Write(v);
            foreach (var v in n.SvKeys) w.Write(v);
            foreach (var p in n.Passes)
            { w.Write(p.Id); w.Write(p.Vs); w.Write(p.Ps); w.Write(p.A); w.Write(p.B); w.Write(p.C); }
        }

        // Aliases
        foreach (var (a, b) in shpk.Aliases) { w.Write(a); w.Write(b); }

        // Additional data
        w.Write(shpk.Additional);

        // Blobs offset
        w.Flush();
        int blobsOff = (int)ms.Position;
        ms.Position = blobsOffPos; w.Write(blobsOff); ms.Position = blobsOff;
        w.Write(shpk.BlobSection.ToArray());

        // Strings offset
        w.Flush();
        int stringsOff = (int)ms.Position;
        ms.Position = stringsOffPos; w.Write(stringsOff); ms.Position = stringsOff;
        w.Write(shpk.StringSection.ToArray());

        // File size
        w.Flush();
        int fileSize = (int)ms.Position;
        ms.Position = fileSizePos; w.Write(fileSize);

        return ms.ToArray();
    }

    // -- Helpers ----------------------------------------------------------

    private static byte[] ToLeBytes(params uint[] values)
    {
        var result = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), values[i]);
        return result;
    }

    private static int FindPattern(byte[] haystack, byte[] needle)
    {
        int limit = haystack.Length - needle.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static bool MatchesAt(byte[] data, int offset, byte[] pattern)
    {
        if (offset + pattern.Length > data.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (data[offset + i] != pattern[i]) return false;
        return true;
    }

    private static void Log(string msg)
        => Http.DebugServer.AppendLog($"[SkinShpkPatcher] {msg}");
}
