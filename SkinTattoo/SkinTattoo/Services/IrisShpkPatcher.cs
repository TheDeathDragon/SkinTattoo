using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace SkinTattoo.Services;

/// <summary>
/// Runtime patcher for iris.shpk: injects a time-driven modulation payload after the
/// native emissive term (`emissive = g_EmissiveColor^2 * mask.r`) of every lighting /
/// composite pass PS, so exported mods keep eye-glow animation without the
/// EmissiveCBufferHook. Animation params (speed/amp/mode/colorB/dualActive) come from a
/// ColorTable embedded in the mtrl, sampled via a g_SamplerTable resource added per PS
/// (same mechanism the skin_ct patch validated). Time source is
/// g_PbrParameterCommon.m_LoopTime -- bound at cb2 in all 64 target PSes, same slot as
/// skin.shpk lighting PSes (see docs/cbuffer-atlas.md).
///
/// Unlike the skin patch (output-anchor injection where r2..r10 are dead), the iris
/// injection point sits mid-shader, so the payload's temporaries are allocated ABOVE the
/// PS's existing dcl_temps count (temp-base shift) and dcl_temps is bumped by 4.
/// </summary>
public static class IrisShpkPatcher
{
    // iris2: payload REPLACES the native masked-emissive term with CT-driven color
    // (col2, pre-squared) and picks the CT row pair per eye via vertex color v1.y
    // (rows 2/3 = left, rows 4/5 = right) -- the same discriminator the vanilla
    // shader uses to blend m_LeftColor/m_RightColor for heterochromia.
    public const string SchemaVersion = "iris2";

    private const uint LightingPassId = 0x955C0B73;   // pass[2], 16 PSes
    private const uint CompositePassId = 0xC885BBD3;  // pass[3], 48 PSes

    // ffcrc (reflected CRC-32, init 0, no final xor) of the buffer names.
    private const uint CrcMaterialParameter = 0x64D12851;
    private const uint CrcPbrParameterCommon = 0xFF0F34A7;

    // Invariant tail of `mul rE.xyz, cb0[3].xyzx, cb0[3].xyzx` (g_EmissiveColor squared);
    // first 3 tokens (opcode + dest operand + dest reg) vary per PS.
    private static readonly byte[] EmissiveInitCb3Tail = SkinShpkPatcher.ToLeBytes(
        0x00208246, 0x00000000, 0x00000003,
        0x00208246, 0x00000000, 0x00000003);

    public static byte[]? Patch(byte[] vanillaShpk)
    {
        try
        {
            var shpk = SkinShpkPatcher.ParseShpk(vanillaShpk);

            int strOff = SkinShpkPatcher.AddString(shpk, "g_SamplerTable");
            int strSz = "g_SamplerTable".Length;

            // Target PS set derived from the node table: every PS reachable from the
            // lighting or composite pass. Matches the offline census (64 of 96).
            var targets = new SortedSet<int>();
            foreach (var n in shpk.Nodes)
                foreach (var p in n.Passes)
                    if (p.Id is LightingPassId or CompositePassId)
                        targets.Add((int)p.Ps);
            if (targets.Count == 0)
            {
                Log("No target PSes derived from node table; aborting");
                return null;
            }

            int ok = 0, skip = 0;
            foreach (int psIdx in targets)
            {
                if (PatchSingleIrisPs(shpk, psIdx, strOff, strSz)) ok++;
                else skip++;
            }
            Log($"CT anim injection: {ok}/{targets.Count} PSes (skipped {skip})");
            if (ok == 0) return null;

            var result = SkinShpkPatcher.RebuildShpk(shpk);
            Log($"Patched iris.shpk: {vanillaShpk.Length} -> {result.Length} bytes");
            return result;
        }
        catch (Exception ex)
        {
            Log($"Patch failed: {ex.Message}");
            return null;
        }
    }

    private static bool PatchSingleIrisPs(
        SkinShpkPatcher.ShpkFile shpk, int psIndex, int strOff, int strSz)
    {
        int targetIdx = shpk.VsCount + psIndex;
        if (targetIdx < 0 || targetIdx >= shpk.Shaders.Count)
        {
            Log($"PS[{psIndex}] out of range");
            return false;
        }

        var ps = shpk.Shaders[targetIdx];
        int blobStart = ps.BlobOff;
        if (blobStart + ps.BlobSz > shpk.BlobSection.Count)
        {
            Log($"PS[{psIndex}] blob exceeds blob section");
            return false;
        }

        var originalDxbc = shpk.BlobSection.GetRange(blobStart, ps.BlobSz).ToArray();
        if (!SkinShpkPatcher.IsDxbc(originalDxbc))
        {
            Log($"PS[{psIndex}] blob is not DXBC");
            return false;
        }

        var shexData = SkinShpkPatcher.ExtractShexData(originalDxbc);
        if (shexData == null)
        {
            Log($"PS[{psIndex}] no SHEX/SHDR chunk");
            return false;
        }

        // The anchor pattern hardcodes cb0 (material params) and the payload hardcodes
        // cb2 (g_PbrParameterCommon.m_LoopTime). Verify both bindings from the resource
        // table before touching the blob.
        int matSlot = -1, pbrSlot = -1;
        for (int i = 0; i < ps.CCnt && i < ps.Resources.Count; i++)
        {
            var r = ps.Resources[i];
            if (r.Id == CrcMaterialParameter) matSlot = r.Slot;
            else if (r.Id == CrcPbrParameterCommon) pbrSlot = r.Slot;
        }
        if (matSlot != 0 || pbrSlot != 2)
        {
            Log($"PS[{psIndex}] unexpected cb layout (mat=b{matSlot} pbr=b{pbrSlot}), skipping");
            return false;
        }

        var (maxS, maxT, temps) = SkinShpkPatcher.ScanShexDecls(shexData);
        for (int i = 0; i < ps.Resources.Count; i++)
        {
            var r = ps.Resources[i];
            if (i >= ps.CCnt && i < ps.CCnt + ps.SCnt) maxS = Math.Max(maxS, r.Slot);
            else if (i >= ps.CCnt + ps.SCnt + ps.UavCnt) maxT = Math.Max(maxT, r.Slot);
        }
        int ctSamp = maxS + 1;
        int ctTex = maxT + 1;
        if (ctSamp > 15 || ctTex > 127)
        {
            Log($"PS[{psIndex}] no free slot (s{ctSamp}/t{ctTex})");
            return false;
        }
        if (temps <= 0)
        {
            Log($"PS[{psIndex}] no dcl_temps");
            return false;
        }
        if (!HasV1YInput(shexData))
        {
            // Payload reads the per-eye discriminator v1.y; every vanilla target PS
            // declares it (64/64 verified). A future layout change surfaces here.
            Log($"PS[{psIndex}] v1.y input not declared, skipping");
            return false;
        }

        var withDecls = SkinShpkPatcher.PatchShexAddDeclarations(shexData, ctSamp, ctTex);
        if (withDecls == null) return false;
        if (!PatchShexSetTemps(withDecls, temps + 5))
        {
            Log($"PS[{psIndex}] dcl_temps rewrite failed");
            return false;
        }

        if (!FindEmissiveAnchor(withDecls, out int insertAt, out uint emReg,
                out uint maskTok, out uint maskReg))
        {
            Log($"PS[{psIndex}] emissive anchor (cb0[3]^2 * mask) not found");
            return false;
        }

        var payload = BuildIrisPulsePayload(temps, emReg, maskTok, maskReg,
            (uint)ctTex, (uint)ctSamp);

        var finalShex = new byte[withDecls.Length + payload.Length];
        withDecls.AsSpan(0, insertAt).CopyTo(finalShex);
        payload.CopyTo(finalShex.AsSpan(insertAt));
        withDecls.AsSpan(insertAt).CopyTo(finalShex.AsSpan(insertAt + payload.Length));

        uint oldCount = BinaryPrimitives.ReadUInt32LittleEndian(finalShex.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(finalShex.AsSpan(4),
            oldCount + (uint)(payload.Length / 4));

        var patchedDxbc = SkinShpkPatcher.RebuildDxbc(originalDxbc, finalShex);

        int oldSize = ps.BlobSz;
        int delta = patchedDxbc.Length - oldSize;
        shpk.BlobSection.RemoveRange(blobStart, oldSize);
        shpk.BlobSection.InsertRange(blobStart, patchedDxbc);
        ps.BlobSz = patchedDxbc.Length;
        foreach (var s in shpk.Shaders)
            if (s.BlobOff > blobStart)
                s.BlobOff += delta;

        var samplerRes = new SkinShpkPatcher.ShpkResource
        {
            Id = SkinShpkPatcher.CrcSamplerTable, StrOff = strOff, StrSz = (ushort)strSz,
            IsTex = 0, Slot = (ushort)ctSamp, Size = 5,
        };
        var textureRes = new SkinShpkPatcher.ShpkResource
        {
            Id = SkinShpkPatcher.CrcSamplerTable, StrOff = strOff, StrSz = (ushort)strSz,
            IsTex = 1, Slot = (ushort)ctTex, Size = 6,
        };
        ps.Resources.Insert(ps.CCnt + ps.SCnt, samplerRes);
        ps.Resources.Add(textureRes);
        ps.SCnt++;
        ps.TCnt++;

        return true;
    }

    /// <summary>True when the declaration region declares input register v1 with the .y
    /// component (vertex color, per-eye discriminator).</summary>
    private static bool HasV1YInput(byte[] shexData)
    {
        int pos = 8;
        while (pos + 4 <= shexData.Length)
        {
            uint tok = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos));
            int opcode = (int)(tok & 0x7FF);
            if (opcode == 0x23)
            {
                int cdLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos + 4));
                if (cdLen <= 0) return false;
                pos += cdLen * 4;
                continue;
            }
            int length = (int)((tok >> 24) & 0x7F);
            if (length == 0) length = 1;
            if (pos + length * 4 > shexData.Length) return false;

            if (opcode is 0x62 or 0x63 or 0x64)
            {
                uint operand = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos + 4));
                uint reg = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos + 8));
                if (reg == 1 && ((operand >> 4) & 0x2) != 0)
                    return true;
            }
            else if (!(opcode >= 0x58 && opcode <= 0x6A) && !(opcode >= 0x9C && opcode <= 0xA5))
            {
                return false;
            }
            pos += length * 4;
        }
        return false;
    }

    /// <summary>Rewrite the dcl_temps count in the declaration region. Returns false when
    /// no dcl_temps instruction exists.</summary>
    private static bool PatchShexSetTemps(byte[] shexData, int newCount)
    {
        int pos = 8;
        while (pos + 4 <= shexData.Length)
        {
            uint tok = BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos));
            int opcode = (int)(tok & 0x7FF);
            if (opcode == 0x23)
            {
                int cdLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(shexData.AsSpan(pos + 4));
                if (cdLen <= 0) return false;
                pos += cdLen * 4;
                continue;
            }
            int length = (int)((tok >> 24) & 0x7F);
            if (length == 0) length = 1;
            if (pos + length * 4 > shexData.Length) return false;

            if (opcode == 0x68)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(shexData.AsSpan(pos + 4), (uint)newCount);
                return true;
            }
            if (!(opcode >= 0x58 && opcode <= 0x6A) && !(opcode >= 0x9C && opcode <= 0xA5))
                return false;
            pos += length * 4;
        }
        return false;
    }

    /// <summary>
    /// Locate `mul rE.xyz, cb0[3]^2` followed (within a few instructions, across the mask
    /// sample) by `mul rE.xyz, rE.xyzx * maskScalar` (operand order varies per pass family).
    /// Returns the byte offset AFTER the second mul -- rE.xyz then holds the final masked
    /// emissive term -- plus rE and the mask operand token pair for payload reuse.
    /// </summary>
    private static bool FindEmissiveAnchor(byte[] shex, out int insertAt, out uint emReg,
        out uint maskTok, out uint maskReg)
    {
        insertAt = -1; emReg = 0; maskTok = 0; maskReg = 0;

        int pos = 8;
        while (pos + 4 <= shex.Length)
        {
            uint tok = BinaryPrimitives.ReadUInt32LittleEndian(shex.AsSpan(pos));
            int opcode = (int)(tok & 0x7FF);
            int lenBytes;
            if (opcode == 0x23)
            {
                int cdLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(shex.AsSpan(pos + 4));
                if (cdLen <= 0) return false;
                lenBytes = cdLen * 4;
            }
            else
            {
                int l = (int)((tok >> 24) & 0x7F);
                if (l == 0) l = 1;
                lenBytes = l * 4;
            }
            if (pos + lenBytes > shex.Length) return false;

            if (opcode == 0x38 && lenBytes == 36
                && BinaryPrimitives.ReadUInt32LittleEndian(shex.AsSpan(pos + 4)) == 0x00100072u
                && SkinShpkPatcher.MatchesAt(shex, pos + 12, EmissiveInitCb3Tail))
            {
                uint candidateReg = BinaryPrimitives.ReadUInt32LittleEndian(shex.AsSpan(pos + 8));
                if (ScanForMaskMul(shex, pos + lenBytes, candidateReg,
                        out insertAt, out maskTok, out maskReg))
                {
                    emReg = candidateReg;
                    return true;
                }
            }
            pos += lenBytes;
        }
        return false;
    }

    private static bool ScanForMaskMul(byte[] shex, int startPos, uint emReg,
        out int insertAt, out uint maskTok, out uint maskReg)
    {
        insertAt = -1; maskTok = 0; maskReg = 0;

        int pos = startPos;
        for (int hop = 0; hop < 3 && pos + 4 <= shex.Length; hop++)
        {
            uint tok = BinaryPrimitives.ReadUInt32LittleEndian(shex.AsSpan(pos));
            int opcode = (int)(tok & 0x7FF);
            int l = (int)((tok >> 24) & 0x7F);
            if (l == 0) l = 1;
            int lenBytes = l * 4;
            if (opcode == 0x23 || pos + lenBytes > shex.Length) return false;

            if (opcode == 0x38 && lenBytes == 28
                && BinaryPrimitives.ReadUInt32LittleEndian(shex.AsSpan(pos + 4)) == 0x00100072u
                && BinaryPrimitives.ReadUInt32LittleEndian(shex.AsSpan(pos + 8)) == emReg)
            {
                uint src0Tok = BinaryPrimitives.ReadUInt32LittleEndian(shex.AsSpan(pos + 12));
                uint src0Reg = BinaryPrimitives.ReadUInt32LittleEndian(shex.AsSpan(pos + 16));
                uint src1Tok = BinaryPrimitives.ReadUInt32LittleEndian(shex.AsSpan(pos + 20));
                uint src1Reg = BinaryPrimitives.ReadUInt32LittleEndian(shex.AsSpan(pos + 24));

                bool src0IsEm = src0Tok == 0x00100246u && src0Reg == emReg;
                bool src1IsEm = src1Tok == 0x00100246u && src1Reg == emReg;
                if (src0IsEm == src1IsEm) { pos += lenBytes; continue; }

                uint mTok = src0IsEm ? src1Tok : src0Tok;
                uint mReg = src0IsEm ? src1Reg : src0Reg;
                // Non-extended 2-token operand of a temp/input register, 1D-indexed.
                if ((mTok & 0x80000000u) != 0) return false;
                int opType = (int)((mTok >> 12) & 0xFF);
                int idxDim = (int)((mTok >> 20) & 0x3);
                if ((opType != 0 && opType != 1) || idxDim != 1) return false;

                insertAt = pos + lenBytes;
                maskTok = mTok;
                maskReg = mReg;
                return true;
            }
            pos += lenBytes;
        }
        return false;
    }

    /// <summary>
    /// iris2 payload: REPLACES the native masked-emissive term entirely with CT-driven
    /// per-eye glow. Row V = 0.09375 + v1.y * 0.0625 -- vertex color selects the row
    /// pair (left eye v1=(1,0) -> rows 2/3, right eye v1=(0,1) -> rows 4/5). CT layout:
    /// col2 halfs 8..10 = colorA^2 (pre-squared to match the native color^2*mask scale),
    /// col3 halfs 12..14 = speed/amp/mode, col4 halfs 17..19 = colorB^2, col6 half 26 =
    /// dualActive. The mtrl g_EmissiveColor no longer drives the masked glow (its term
    /// is overwritten); it stays non-zero only for iris-ring semantics.
    /// Registers rA..rE are tempBase..tempBase+4, above every pre-existing temp.
    /// </summary>
    private static byte[] BuildIrisPulsePayload(int tempBase, uint emReg,
        uint maskTok, uint maskReg, uint ctTex, uint ctSamp)
    {
        uint a = (uint)tempBase, b = (uint)(tempBase + 1);
        uint c = (uint)(tempBase + 2), d = (uint)(tempBase + 3);
        uint e = (uint)(tempBase + 4);

        var t = new List<uint>(240);
        void Emit(params uint[] xs) => t.AddRange(xs);
        void SampleCt(uint dstReg, uint coordReg) => Emit(
            0x8B000045, 0x800000C2, 0x00155543,
            0x001000F2, dstReg,
            0x00100046, coordReg,
            0x00107E46, ctTex,
            0x00106000, ctSamp);

        // -- per-eye row V: mad a.y, v1.y, 0.0625, 0.09375 --
        Emit(0x09000032, 0x00100022, a, 0x0010101A, 1, 0x00004001, 0x3D800000, 0x00004001, 0x3DC00000);
        // -- col2: colorA^2 -> masked base term --
        Emit(0x05000036, 0x00100012, a, 0x00004001, 0x3EA00000);      // col2 U
        SampleCt(e, a);
        Emit(0x07000038, 0x00100072, e, 0x00100246, e, maskTok, maskReg);
        // -- col4: colorB^2 -> masked dual endpoint --
        Emit(0x05000036, 0x00100012, a, 0x00004001, 0x3F100000);      // col4 U
        SampleCt(c, a);
        Emit(0x07000038, 0x00100072, c, 0x00100796, c, maskTok, maskReg);
        // -- col3: speed/amp/mode; phase = speed * m_LoopTime * 2pi --
        Emit(0x05000036, 0x00100012, a, 0x00004001, 0x3EE00000);      // col3 U
        SampleCt(b, a);
        Emit(0x08000038, 0x00100042, a, 0x0010000A, b, 0x0020800A, 2, 0);
        Emit(0x07000038, 0x00100042, a, 0x0010002A, a, 0x00004001, 0x40C90FDA);
        Emit(0x0600004D, 0x00100042, a, 0x0000D000, 0x0010002A, a);   // sincos
        Emit(0x05000036, 0x00100082, d, 0x0010002A, a);               // save raw sin -> d.w
        // -- wave: square when mode >= 0.5 (Flicker/Gradient/Ripple), sine for Pulse --
        Emit(0x0700001D, 0x00100082, a, 0x0010002A, a, 0x00004001, 0x00000000);
        Emit(0x09000037, 0x00100082, a, 0x0010003A, a, 0x00004001, 0x3F800000, 0x00004001, 0xBF800000);
        Emit(0x0700001D, 0x00100012, d, 0x0010002A, b, 0x00004001, 0x3F000000);
        Emit(0x09000037, 0x00100042, a, 0x0010000A, d, 0x0010003A, a, 0x0010002A, a);
        // factor = amp * wave + 1
        Emit(0x09000032, 0x00100042, a, 0x0010001A, b, 0x0010002A, a, 0x00004001, 0x3F800000);
        // -- dual weight w = 0.5 + 0.5 * amp * rawsin; lerp(A, B, w) into c --
        Emit(0x07000038, 0x00100082, d, 0x0010003A, d, 0x0010001A, b);
        Emit(0x09000032, 0x00100082, d, 0x0010003A, d, 0x00004001, 0x3F000000, 0x00004001, 0x3F000000);
        Emit(0x08000000, 0x00100072, c, 0x00100246, c, 0x80100246, 0x00000041, e);
        Emit(0x09000032, 0x00100072, c, 0x00100FF6, d, 0x00100246, c, 0x00100246, e);
        // pulsed = A * factor
        Emit(0x07000038, 0x00100072, e, 0x00100246, e, 0x00100AA6, a);
        // -- dualActive gate from col6.z; REPLACE rE with the chosen branch --
        Emit(0x05000036, 0x00100012, a, 0x00004001, 0x3F500000);      // col6 U
        SampleCt(d, a);
        Emit(0x0700001D, 0x00100082, a, 0x0010002A, d, 0x00004001, 0x3F000000);
        Emit(0x09000037, 0x00100072, emReg, 0x00100FF6, a, 0x00100246, c, 0x00100246, e);

        return SkinShpkPatcher.ToLeBytes(t.ToArray());
    }

    private static void Log(string msg)
        => Http.DebugServer.AppendLog($"[IrisShpk] {msg}");
}
