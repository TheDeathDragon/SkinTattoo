"""Patch PS[19] EMISSIVE DXBC to add ColorTable sampling.

Strategy:
  1. Parse DXBC container, locate SHEX and RDEF chunks
  2. In SHEX: insert dcl_sampler s5 + dcl_resource_texture2d t10
  3. In SHEX: replace emissive instructions [35][36] with ColorTable sample
  4. In RDEF: add g_SamplerTable sampler + texture bindings
  5. Rebuild DXBC, fix chunk offsets, use D3DCompiler to validate
"""
import struct
import ctypes
import os
import sys
import copy

# ── DXBC checksum via wine algorithm ──

def dxbc_checksum(blob: bytes) -> tuple:
    """Compute DXBC 128-bit checksum.

    Custom-padded MD5: standard MD5 transform with DXBC-specific padding.
    Input = blob[20:] (skip magic + checksum field).
    Source: vkd3d-proton checksum.c / GPUOpen DXBCChecksum.cpp.
    """
    MD5_S = [
        7,12,17,22,7,12,17,22,7,12,17,22,7,12,17,22,
        5,9,14,20,5,9,14,20,5,9,14,20,5,9,14,20,
        4,11,16,23,4,11,16,23,4,11,16,23,4,11,16,23,
        6,10,15,21,6,10,15,21,6,10,15,21,6,10,15,21,
    ]
    MD5_T = [
        0xd76aa478,0xe8c7b756,0x242070db,0xc1bdceee,0xf57c0faf,0x4787c62a,0xa8304613,0xfd469501,
        0x698098d8,0x8b44f7af,0xffff5bb1,0x895cd7be,0x6b901122,0xfd987193,0xa679438e,0x49b40821,
        0xf61e2562,0xc040b340,0x265e5a51,0xe9b6c7aa,0xd62f105d,0x02441453,0xd8a1e681,0xe7d3fbc8,
        0x21e1cde6,0xc33707d6,0xf4d50d87,0x455a14ed,0xa9e3e905,0xfcefa3f8,0x676f02d9,0x8d2a4c8a,
        0xfffa3942,0x8771f681,0x6d9d6122,0xfde5380c,0xa4beea44,0x4bdecfa9,0xf6bb4b60,0xbebfbc70,
        0x289b7ec6,0xeaa127fa,0xd4ef3085,0x04881d05,0xd9d4d039,0xe6db99e5,0x1fa27cf8,0xc4ac5665,
        0xf4292244,0x432aff97,0xab9423a7,0xfc93a039,0x655b59c3,0x8f0ccc92,0xffeff47d,0x85845dd1,
        0x6fa87e4f,0xfe2ce6e0,0xa3014314,0x4e0811a1,0xf7537e82,0xbd3af235,0x2ad7d2bb,0xeb86d391,
    ]
    M32 = 0xFFFFFFFF

    def rotl32(x, n):
        return ((x << n) | (x >> (32 - n))) & M32

    def transform(state, block):
        a, b, c, d = state
        W = struct.unpack('<16I', block)
        for i in range(64):
            if i < 16:
                f = (b & c) | ((~b) & d); g = i
            elif i < 32:
                f = (d & b) | ((~d) & c); g = (5*i+1) % 16
            elif i < 48:
                f = b ^ c ^ d; g = (3*i+5) % 16
            else:
                f = c ^ (b | (~d & M32)); g = (7*i) % 16
            f = (f + a + MD5_T[i] + W[g]) & M32
            a = d; d = c; c = b
            b = (b + rotl32(f, MD5_S[i])) & M32
        return ((state[0]+a)&M32, (state[1]+b)&M32, (state[2]+c)&M32, (state[3]+d)&M32)

    data = blob[20:]
    length = len(data)
    num_bits = (length * 8) & M32
    num_bits2 = ((num_bits >> 2) | 1) & M32
    state = (0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476)

    leftover_length = length % 64
    full_end = length - leftover_length
    for off in range(0, full_end, 64):
        state = transform(state, data[off:off+64])

    leftover = data[full_end:]
    if leftover_length >= 56:
        block1 = bytearray(64)
        block1[:leftover_length] = leftover
        block1[leftover_length] = 0x80
        state = transform(state, bytes(block1))
        block2 = bytearray(64)
        struct.pack_into('<I', block2, 0, num_bits)
        struct.pack_into('<I', block2, 60, num_bits2)
        state = transform(state, bytes(block2))
    else:
        combined = bytearray(64)
        struct.pack_into('<I', combined, 0, num_bits)
        combined[4:4+leftover_length] = leftover
        combined[4+leftover_length] = 0x80
        struct.pack_into('<I', combined, 60, num_bits2)
        state = transform(state, bytes(combined))

    return state


def d3d_disassemble(dxbc_bytes: bytes) -> str:
    """Disassemble DXBC via D3DCompiler_47.dll."""
    d3d = ctypes.CDLL('D3DCompiler_47.dll')
    blob_ptr = ctypes.c_void_p()
    hr = d3d.D3DDisassemble(dxbc_bytes, len(dxbc_bytes), 0, None, ctypes.byref(blob_ptr))
    if hr != 0 or not blob_ptr.value:
        raise RuntimeError(f"D3DDisassemble failed: 0x{hr & 0xFFFFFFFF:08X}")

    vtable = ctypes.cast(
        ctypes.c_void_p(ctypes.cast(blob_ptr, ctypes.POINTER(ctypes.c_void_p))[0]),
        ctypes.POINTER(ctypes.c_void_p * 5))[0]
    get_ptr = ctypes.CFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(vtable[3])
    get_size = ctypes.CFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(vtable[4])
    release = ctypes.CFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(vtable[2])

    buf = get_ptr(blob_ptr)
    sz = get_size(blob_ptr)
    text = ctypes.string_at(buf, sz).decode('utf-8', errors='replace')
    release(blob_ptr)
    return text


def d3d_validate(dxbc_bytes: bytes) -> bool:
    """Quick validation: try to disassemble the DXBC. Returns True if valid."""
    try:
        d3d_disassemble(dxbc_bytes)
        return True
    except:
        return False


# ── SHEX instruction building ──

def encode_u32(vals):
    """Encode list of uint32 values to bytes."""
    return b''.join(struct.pack('<I', v) for v in vals)


def build_dcl_sampler(register: int, mode: int = 0) -> bytes:
    """Build dcl_sampler instruction.

    mode: 0 = default, 1 = comparison, 2 = mono
    Format: 3 tokens
      [0] opcode: 0x0300005A (len=3, opcode=0x5A, mode in bits 11-12)
      [1] operand: 0x00106000 (sampler operand type)
      [2] register index
    """
    opcode_token = (3 << 24) | (mode << 11) | 0x5A
    operand = 0x00106000
    return encode_u32([opcode_token, operand, register])


def build_dcl_resource_texture2d(register: int) -> bytes:
    """Build dcl_resource_texture2d instruction.

    Texture2D dimension = 3, encoded in bits 11-15.
    Format: 4 tokens
      [0] opcode: 0x04001858 (len=4, dimension=3<<11, opcode=0x58)
      [1] operand: 0x00107000 (texture operand type)
      [2] register index
      [3] return type: 0x00005555 (float,float,float,float)
    """
    opcode_token = (4 << 24) | (3 << 11) | 0x58
    operand = 0x00107000
    return encode_u32([opcode_token, operand, register, 0x00005555])


def build_colortable_sample_instructions() -> bytes:
    """Build the replacement instructions for emissive ColorTable sampling.

    Replaces:
      mul r1.xyz, cb0[3].xyzx, cb0[3].xyzx   (9 tokens = 36 bytes)
      mul r1.xyz, r0.zzzz, r1.xyzx            (7 tokens = 28 bytes)
    Total: 16 tokens = 64 bytes

    With:
      // r0.z = normal.alpha (row index, 0.0-1.0)
      // Convert to ColorTable UV:
      //   y = (r0.z * 31.0 + 0.5) / 32.0  → row center
      //   x = (2.0 + 0.5) / 8.0 = 0.3125  → emissive column center
      mad r1.x, r0.z, l(31.0), l(0.5)                    // 9 tokens
      mul r1.y, r1.x, l(0.03125)                          // 7 tokens
      mov r1.x, l(0.3125)                                 // 5 tokens
      sample_indexable(texture2d) r1.xyzw, r1.xyxx, t10.xyzw, s5  // 11 tokens
    Total: 32 tokens = 128 bytes (net +16 tokens = +64 bytes)

    Actually, let's try to keep it within the original 16 tokens budget by being clever:
      mov r1.xy, l(0.3125, <rowUV>, 0, 0)    → can't, row is dynamic

    Better approach - use fewer instructions:
      mad r1.y, r0.z, l(0.96875), l(0.015625)  // rowUV  (7 tokens)
      // 0.96875 = 31/32, 0.015625 = 0.5/32
      sample_indexable(texture2d) r1.xyzw, l(0.3125, r1.y), t10, s5
      → can't mix immediate and register in sample src

    Most compact viable approach:
      mad r1.x, r0.z, l(0.96875), l(0.015625)            // 9 tokens: r1.x = rowUV
      sample r1.xyzw, r1.x(as UV), t10, s5                // needs 2D UV...

    The problem is sample needs a 2D UV. We need at least 2 instructions to set up UV.
    But we can use a single mad to set both x and y:

    Actually the simplest approach for minimal tokens:
      mad r1.xy, r0.zz, l(0, 0.96875, 0, 0), l(0.3125, 0.015625, 0, 0)  // 11 tokens
      sample_indexable(texture2d)(float4) r1.xyzw, r1.xyxx, t10.xyzw, s5  // 11 tokens
    Total: 22 tokens (net +6 tokens = +24 bytes)

    Hmm, still bigger than 16. Let me try a different encoding.

    Option: use the extra space. The SHEX chunk size grows, we update all offsets.
    This is simpler than trying to fit in the same space.

    For robustness, let's go with the clean 2-instruction approach:
    """
    import struct

    # Instruction 1: mad r1.xy, r0.zzzz, l(0.0, 0.96875, 0.0, 0.0), l(0.3125, 0.015625, 0.0, 0.0)
    # mad opcode = 0x32, with 4 src operands: dest, src0, src1, src2
    # Actually mad = 0x32 takes 3 sources: dest = src0 * src1 + src2
    #
    # Encoding mad with immediate operands is complex. Let me use a simpler sequence:

    # Alternative: 3 simple instructions
    # mov r1.x, l(0.3125)                     // emissive column
    # mad r1.y, r0.z, l(0.96875), l(0.015625) // row UV
    # sample r1.xyzw, r1.xyxx, t10.xyzw, s5   // ColorTable read

    # For now, return placeholder bytes - we'll encode properly after testing the framework
    # Just use the SAME total size as original (16 tokens = 64 bytes) padded with NOPs

    # NOP = opcode 0x3A, length 1 token = 0x0100003A
    nop_token = 0x0100003A

    # Placeholder: 16 NOP tokens
    return encode_u32([nop_token] * 16)


# ── DXBC patching ──

def patch_shex_add_declarations(shex_data: bytearray,
                                 new_sampler_reg: int,
                                 new_texture_reg: int) -> bytearray:
    """Insert dcl_sampler and dcl_resource_texture2d after existing declarations."""
    # Find the end of declaration region (last dcl_* instruction)
    # Declarations are at the start of SHEX data (after 8-byte version+count header)

    pos = 8  # skip version token + token count
    last_dcl_end = pos

    while pos < len(shex_data):
        opcode_token = struct.unpack_from('<I', shex_data, pos)[0]
        opcode = opcode_token & 0x7FF
        length = (opcode_token >> 24) & 0x7F
        if length == 0:
            length = 1

        if opcode >= 0x58 and opcode <= 0x6A:
            # Still in declaration region
            last_dcl_end = pos + length * 4
        else:
            break

        pos += length * 4

    # Build new declaration bytes
    new_sampler = build_dcl_sampler(new_sampler_reg)
    new_resource = build_dcl_resource_texture2d(new_texture_reg)
    insert_bytes = new_sampler + new_resource  # 3+4 = 7 tokens = 28 bytes

    # Insert after last declaration
    result = bytearray(shex_data[:last_dcl_end])
    result.extend(insert_bytes)
    result.extend(shex_data[last_dcl_end:])

    # Update token count (at offset 4 in SHEX data)
    old_count = struct.unpack_from('<I', result, 4)[0]
    new_count = old_count + 7  # 3 + 4 tokens added
    struct.pack_into('<I', result, 4, new_count)

    return result


def patch_shex_replace_emissive(shex_data: bytearray) -> bytearray:
    """Replace emissive mul+mul with ColorTable sample.

    Target pattern (after declarations have been inserted, offsets shifted):
      mul r1.xyz, cb0[3].xyzx, cb0[3].xyzx  (9 tokens)
      mul r1.xyz, r0.zzzz, r1.xyzx          (7 tokens)

    We find these by matching the cb0[3] reference pattern.
    """
    # Search for the mul cb0[3]*cb0[3] pattern
    # Token signature: 09000038 00100072 00000001 00208246 00000000 00000003 00208246 00000000 00000003
    target_pattern = struct.pack('<9I',
        0x09000038, 0x00100072, 0x00000001,
        0x00208246, 0x00000000, 0x00000003,
        0x00208246, 0x00000000, 0x00000003)

    idx = shex_data.find(target_pattern)
    if idx < 0:
        raise ValueError("Could not find emissive mul cb0[3]*cb0[3] pattern in SHEX")

    # The next instruction (mul r0.z*r1) immediately follows: 7 tokens = 28 bytes
    second_pattern = struct.pack('<7I',
        0x07000038, 0x00100072, 0x00000001,
        0x00100AA6, 0x00000000, 0x00100246, 0x00000001)

    idx2 = idx + 36  # 9 tokens * 4 bytes
    actual = shex_data[idx2:idx2 + 28]
    if actual != second_pattern:
        raise ValueError("Second emissive mul instruction doesn't match expected pattern")

    print(f"  Found emissive pattern at SHEX offset 0x{idx:X} (2 instructions, 16 tokens)")

    # Build replacement: ColorTable sampling
    # We need to replace 16 tokens (64 bytes) with our new code.
    # Our code will be larger, so we insert and update token count.

    # New instructions:
    # 1. mov r1.x, l(0.3125)
    #    Opcode: 0x36 (mov), length varies
    #    mov dest, src_imm
    #    dest = r1.x = 00100012 00000001
    #    src = l(0.3125) = immediate float
    #
    # For simplicity, let's encode with known-good token sequences.
    # We'll use the approach: replace 64 bytes with 64 bytes exactly (padded with nop if shorter)
    # or expand the buffer if longer.

    # Approach: encode 3 instructions that fit in ~20 tokens:
    #
    # mad r1.x, r0.z, l(0.96875), l(0.015625)
    # mov r1.y, l(0.3125)
    # sample r1.xyzw, r1.yxxx, t10.xyzw, s5
    #
    # But encoding is complex. For the initial prototype, let's just
    # zero out the emissive color (set r1.xyz = 0) to verify the patch works,
    # then refine with actual ColorTable sampling.

    # ColorTable sampling replacement (25 tokens, original was 16 → net +9 tokens = +36 bytes)
    #
    # r0.z = normal.alpha (row pair index, 0.0 = pair 0, 1.0 = pair 15)
    # ColorTable texture (t10): 8-wide × 32-tall, R16G16B16A16_FLOAT
    # Emissive is at column 2 (vec4 index 2): u = (2 + 0.5) / 8 = 0.3125
    # Row UV: v = (rowPair * 2 + 0.5) / 32 = rowPair * 0.0625 + 0.015625
    #   But rowPair = r0.z * 15, so v = r0.z * 15 * 0.0625 + 0.015625 = r0.z * 0.9375 + 0.015625
    #
    # Instruction 1: mad r1.y, r0.z, l(0.9375), l(0.015625)    — 9 tokens
    # Instruction 2: mov r1.x, l(0.3125)                        — 5 tokens
    # Instruction 3: sample t10, r1.xy, s5 → r1.xyzw            — 11 tokens
    # Total: 25 tokens
    #
    # Output: r1.xyz = emissive RGB from ColorTable (same register as original)

    replacement_tokens = [
        # mad r1.y, r0.z, l(0.9375), l(0.015625)
        0x09000032,  # mad, length=9
        0x00100022, 0x00000001,  # dest r1.y
        0x0010002A, 0x00000000,  # src0 r0.z (select_1 z)
        0x00004001, 0x3F700000,  # src1 l(0.9375)
        0x00004001, 0x3C800000,  # src2 l(0.015625)

        # mov r1.x, l(0.3125)
        0x05000036,  # mov, length=5
        0x00100012, 0x00000001,  # dest r1.x
        0x00004001, 0x3EA00000,  # src l(0.3125)

        # sample_indexable(texture2d)(float4) r1.xyzw, r1.xyxx, t10.xyzw, s5
        0x8B000045, 0x800000C2, 0x00155543,  # opcode + extended tokens (from reference)
        0x001000F2, 0x00000001,  # dest r1.xyzw (mask=0xF, reg=1)
        0x00100046, 0x00000001,  # src0 r1.xyxx (swizzle, reg=1)
        0x00107E46, 0x0000000A,  # src1 t10.xyzw (texture, reg=10)
        0x00106000, 0x00000005,  # src2 s5 (sampler, reg=5)
    ]

    replacement = encode_u32(replacement_tokens)
    original_size = 64  # 16 tokens
    new_size = len(replacement)  # 25 tokens = 100 bytes
    delta = new_size - original_size

    print(f"  Replacement: {len(replacement_tokens)} tokens ({new_size} bytes), delta={delta:+d} bytes")

    result = bytearray(shex_data[:idx])
    result.extend(replacement)
    result.extend(shex_data[idx + original_size:])

    # Update token count
    old_count = struct.unpack_from('<I', result, 4)[0]
    new_count = old_count + (len(replacement_tokens) - 16)
    struct.pack_into('<I', result, 4, new_count)

    return result


def rebuild_dxbc(original: bytes, new_shex_data: bytes) -> bytearray:
    """Rebuild DXBC container with modified SHEX chunk."""
    # Parse original
    chunk_count = struct.unpack_from('<I', original, 28)[0]
    chunk_offsets = [struct.unpack_from('<I', original, 32 + i * 4)[0] for i in range(chunk_count)]

    chunks = []
    for off in chunk_offsets:
        magic = original[off:off + 4]
        size = struct.unpack_from('<I', original, off + 4)[0]
        data = original[off + 8:off + 8 + size]
        chunks.append((magic, data))

    # Replace SHEX chunk
    new_chunks = []
    for magic, data in chunks:
        if magic in (b'SHEX', b'SHDR'):
            new_chunks.append((magic, new_shex_data))
        else:
            new_chunks.append((magic, data))

    # Rebuild DXBC
    header_size = 32 + chunk_count * 4
    # Calculate chunk offsets
    offset = header_size
    new_offsets = []
    for magic, data in new_chunks:
        new_offsets.append(offset)
        offset += 8 + len(data)  # 4 magic + 4 size + data

    total_size = offset

    result = bytearray(total_size)

    # Header
    result[0:4] = b'DXBC'
    # Checksum: compute after all data is written
    struct.pack_into('<I', result, 20, 1)  # version
    struct.pack_into('<I', result, 24, total_size)
    struct.pack_into('<I', result, 28, chunk_count)

    # Chunk offset table
    for i, off in enumerate(new_offsets):
        struct.pack_into('<I', result, 32 + i * 4, off)

    # Chunk data
    for i, (magic, data) in enumerate(new_chunks):
        off = new_offsets[i]
        result[off:off + 4] = magic
        struct.pack_into('<I', result, off + 4, len(data))
        result[off + 8:off + 8 + len(data)] = data

    # Compute and write correct checksum
    checksum = dxbc_checksum(bytes(result))
    struct.pack_into('<4I', result, 4, *checksum)

    return result


# ── Main ──

def main():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    dxbc_path = os.path.join(script_dir, 'reference', 'ps_019_EMISSIVE.dxbc')
    out_path = os.path.join(script_dir, 'reference', 'ps_019_PATCHED.dxbc')

    with open(dxbc_path, 'rb') as f:
        original = f.read()

    print(f"Original DXBC: {len(original)} bytes")
    print(f"Original disassembly check: ", end="")
    print("OK" if d3d_validate(original) else "FAIL")

    # Step 1: Extract SHEX chunk data
    chunk_count = struct.unpack_from('<I', original, 28)[0]
    shex_data = None
    for i in range(chunk_count):
        off = struct.unpack_from('<I', original, 32 + i * 4)[0]
        magic = original[off:off + 4]
        size = struct.unpack_from('<I', original, off + 4)[0]
        if magic in (b'SHEX', b'SHDR'):
            shex_data = bytearray(original[off + 8:off + 8 + size])
            print(f"SHEX chunk: {size} bytes at 0x{off:X}")
            break

    if shex_data is None:
        print("ERROR: No SHEX/SHDR chunk found")
        return

    # Step 2: Add s5 + t10 declarations
    print("\nAdding dcl_sampler s5 + dcl_resource_texture2d t10...")
    patched = patch_shex_add_declarations(shex_data, 5, 10)
    added_bytes = len(patched) - len(shex_data)
    print(f"  SHEX grew by {added_bytes} bytes ({added_bytes // 4} tokens)")

    # Step 3: Replace emissive instructions (NOP prototype)
    print("\nReplacing emissive instructions with NOP (prototype)...")
    patched = patch_shex_replace_emissive(patched)

    # Step 4: Rebuild DXBC
    print("\nRebuilding DXBC container...")
    result = rebuild_dxbc(original, bytes(patched))
    print(f"  New DXBC: {len(result)} bytes (delta: {len(result) - len(original):+d})")

    # Step 5: Validate with D3DCompiler
    print("\nValidating patched DXBC with D3DCompiler...")
    try:
        text = d3d_disassemble(bytes(result))
        lines = text.strip().split('\n')
        print(f"  Disassembly OK: {len(lines)} lines")

        # Find NOP region
        print("  NOP region:")
        for i, l in enumerate(lines):
            if 'nop' in l.lower():
                start = max(0, i - 1)
                end = min(len(lines), i + 18)
                for j in range(start, end):
                    print(f"    [{j}] {lines[j]}")
                break

        # Save
        with open(out_path, 'wb') as f:
            f.write(result)
        print(f"\n  Saved patched DXBC to: {out_path}")

        # Also save the disassembly
        disasm_path = out_path.replace('.dxbc', '_disasm.txt')
        with open(disasm_path, 'w', encoding='utf-8') as f:
            f.write(text)
        print(f"  Saved disassembly to: {disasm_path}")

    except Exception as e:
        print(f"  VALIDATION FAILED: {e}")
        # Save anyway for debugging
        with open(out_path, 'wb') as f:
            f.write(result)
        print(f"  Saved (invalid) DXBC for debugging: {out_path}")


if __name__ == "__main__":
    main()
