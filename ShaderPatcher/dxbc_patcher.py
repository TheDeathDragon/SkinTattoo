"""DXBC container parser and patcher for FFXIV skin.shpk ColorTable modification.

Step 1: Parse DXBC container (RDEF, ISGN, OSGN, SHEX, STAT chunks)
Step 2: Parse SHEX instruction stream (SM5.0 opcodes)
Step 3: Add sampler/texture declarations + modify emissive instructions
Step 4: Update RDEF bindings + recompute checksum
"""
import struct
import hashlib
import sys
import os
from dataclasses import dataclass, field
from typing import List, Optional, Tuple, Dict

# ── SM5.0 Opcode Definitions (D3D10_SB_OPCODE_TYPE) ──

OPCODES = {
    0x00: "add", 0x01: "and", 0x02: "break", 0x03: "breakc",
    0x04: "call", 0x05: "callc", 0x06: "case", 0x07: "continue",
    0x08: "continuec", 0x09: "cut", 0x0A: "default",
    0x0B: "deriv_rtx", 0x0C: "deriv_rty", 0x0D: "discard",
    0x0E: "div", 0x0F: "dp2", 0x10: "dp3", 0x11: "dp4",
    0x12: "else", 0x13: "emit", 0x14: "emitthencut",
    0x15: "endif", 0x16: "endloop", 0x17: "endswitch",
    0x18: "eq", 0x19: "exp", 0x1A: "frc",
    0x1B: "ftoi", 0x1C: "ftou", 0x1D: "ge",
    0x1E: "iadd", 0x1F: "if",
    0x20: "ieq", 0x21: "ige", 0x22: "ilt",
    0x23: "imad", 0x24: "imax", 0x25: "imin",
    0x26: "imul", 0x27: "ine", 0x28: "ineg",
    0x29: "ishl", 0x2A: "ishr", 0x2B: "itof",
    0x2C: "label", 0x2D: "ld", 0x2E: "ld_ms",
    0x2F: "log", 0x30: "loop", 0x31: "lt",
    0x32: "mad", 0x33: "min", 0x34: "max",
    0x35: "customdata", 0x36: "mov", 0x37: "movc",
    0x38: "mul", 0x39: "ne", 0x3A: "nop",
    0x3B: "not", 0x3C: "or", 0x3D: "resinfo",
    0x3E: "ret", 0x3F: "retc",
    0x40: "round_ne", 0x41: "round_ni",
    0x42: "round_pi", 0x43: "round_z",
    0x44: "rsq", 0x45: "sample", 0x46: "sample_c",
    0x47: "sample_c_lz", 0x48: "sample_l",
    0x49: "sample_d", 0x4A: "sample_b", 0x4B: "sqrt",
    0x4C: "switch", 0x4D: "sincos",
    0x4E: "udiv", 0x4F: "ult", 0x50: "uge",
    0x51: "umul", 0x52: "umad", 0x53: "umax",
    0x54: "umin", 0x55: "ushr", 0x56: "utof", 0x57: "xor",
    0x58: "dcl_resource", 0x59: "dcl_constantbuffer",
    0x5A: "dcl_sampler", 0x5B: "dcl_index_range",
    0x5C: "dcl_gs_output_primitive_topology",
    0x5D: "dcl_gs_input_primitive",
    0x5E: "dcl_max_output_vertex_count",
    0x5F: "dcl_input", 0x60: "dcl_input_sgv",
    0x61: "dcl_input_siv", 0x62: "dcl_input_ps",
    0x63: "dcl_input_ps_sgv", 0x64: "dcl_input_ps_siv",
    0x65: "dcl_output", 0x66: "dcl_output_sgv",
    0x67: "dcl_output_siv", 0x68: "dcl_temps",
    0x69: "dcl_indexable_temp", 0x6A: "dcl_global_flags",
}

# Resource dimension (bits 11-15 of dcl_resource opcode token)
RESOURCE_DIM = {
    1: "buffer", 2: "texture1d", 3: "texture2d", 4: "texture2dms",
    5: "texture3d", 6: "texturecube", 7: "texture1darray",
    8: "texture2darray", 9: "texture2dmsarray", 10: "texturecubearray",
}

# Opcodes in the declaration region
DCL_OPCODES = set(range(0x58, 0x6B))

# ── Data Classes ──

@dataclass
class DxbcChunk:
    magic: bytes  # 4 bytes: RDEF, ISGN, OSGN, SHEX, STAT
    offset: int   # absolute offset in file
    size: int     # chunk data size (excluding magic+size header)
    data: bytes   # raw chunk data (after magic+size)

@dataclass
class DxbcContainer:
    checksum: Tuple[int, int, int, int]  # 4 × uint32
    total_size: int
    chunks: List[DxbcChunk]
    raw: bytearray  # mutable copy of full file

    def get_chunk(self, magic: bytes) -> Optional[DxbcChunk]:
        for c in self.chunks:
            if c.magic == magic:
                return c
        return None

@dataclass
class ShexInstruction:
    offset: int       # byte offset within SHEX data (after magic+size+ver+count)
    opcode: int       # raw opcode (bits 0-10)
    extended: bool    # bit 11
    length: int       # instruction length in tokens (bits 24-30)
    tokens: List[int] # all tokens including opcode token
    name: str = ""

@dataclass
class RdefBinding:
    name: str
    bind_type: int    # 0=cbuffer, 2=texture, 3=sampler
    return_type: int
    dimension: int    # 1=buffer, 3=texture2d, 5=texture2darray, 8=texturecube, 9=texturecubearray
    num_samples: int
    bind_point: int   # register slot
    bind_count: int
    flags: int


# ── DXBC Checksum ──

def compute_dxbc_checksum(data: bytearray) -> Tuple[int, int, int, int]:
    """Compute DXBC 128-bit checksum (custom MD5 variant).

    The DXBC checksum is an MD5 computed over the entire file content
    with the checksum field (bytes 4-19) zeroed out.
    """
    patched = bytearray(data)
    # Zero out the checksum field at offset 4-19
    for i in range(4, 20):
        patched[i] = 0
    digest = hashlib.md5(bytes(patched)).digest()
    return struct.unpack_from('<4I', digest)


# ── DXBC Container Parser ──

def parse_dxbc(data: bytes) -> DxbcContainer:
    """Parse a DXBC binary container."""
    if len(data) < 32:
        raise ValueError("Data too short for DXBC header")

    magic = data[:4]
    if magic != b'DXBC':
        raise ValueError(f"Bad magic: {magic!r}, expected b'DXBC'")

    checksum = struct.unpack_from('<4I', data, 4)
    version = struct.unpack_from('<I', data, 20)[0]
    total_size = struct.unpack_from('<I', data, 24)[0]
    chunk_count = struct.unpack_from('<I', data, 28)[0]

    chunks = []
    for i in range(chunk_count):
        chunk_off = struct.unpack_from('<I', data, 32 + i * 4)[0]
        chunk_magic = data[chunk_off:chunk_off + 4]
        chunk_size = struct.unpack_from('<I', data, chunk_off + 4)[0]
        chunk_data = data[chunk_off + 8:chunk_off + 8 + chunk_size]
        chunks.append(DxbcChunk(
            magic=chunk_magic,
            offset=chunk_off,
            size=chunk_size,
            data=chunk_data,
        ))

    return DxbcContainer(
        checksum=checksum,
        total_size=total_size,
        chunks=chunks,
        raw=bytearray(data),
    )


# ── SHEX Parser ──

def parse_shex(chunk: DxbcChunk) -> Tuple[int, int, int, List[ShexInstruction]]:
    """Parse SHEX/SHDR chunk into instruction list.

    Returns: (version_token, token_count, shader_type, instructions)
    shader_type: 0 = PS, 1 = VS
    """
    data = chunk.data
    version_token = struct.unpack_from('<I', data, 0)[0]
    token_count = struct.unpack_from('<I', data, 4)[0]

    shader_type = (version_token >> 16) & 0xFFFF
    major = (version_token >> 4) & 0xF
    minor = version_token & 0xF

    instructions = []
    pos = 8  # skip version + token count

    while pos < len(data):
        inst_offset = pos
        opcode_token = struct.unpack_from('<I', data, pos)[0]

        raw_opcode = opcode_token & 0x7FF
        extended = (opcode_token >> 11) & 1
        length = (opcode_token >> 24) & 0x7F

        # Length=0 means different things depending on context
        if length == 0:
            if raw_opcode == 0x35:  # customdata
                # Custom data: token[1] = dword count (including header)
                if pos + 8 <= len(data):
                    dword_count = struct.unpack_from('<I', data, pos + 4)[0]
                    length = dword_count
                else:
                    length = 1
            elif raw_opcode in (0x12, 0x15, 0x16, 0x17, 0x0A, 0x30):
                # else, endif, endloop, endswitch, default, loop: 1 token
                length = 1
            elif raw_opcode == 0x3E:  # ret
                length = 1
            else:
                length = 1

        if pos + length * 4 > len(data):
            break

        tokens = [struct.unpack_from('<I', data, pos + j * 4)[0] for j in range(length)]

        name = OPCODES.get(raw_opcode, f"op_{raw_opcode:#x}")

        instructions.append(ShexInstruction(
            offset=inst_offset - 8,  # relative to after version+count
            opcode=raw_opcode,
            extended=bool(extended),
            length=length,
            tokens=tokens,
            name=name,
        ))

        pos += length * 4

    return version_token, token_count, shader_type, instructions


# ── RDEF Parser ──

def parse_rdef_bindings(chunk: DxbcChunk) -> List[RdefBinding]:
    """Parse resource bindings from RDEF chunk."""
    data = chunk.data
    if len(data) < 28:
        return []

    # RDEF header
    constant_buffer_count = struct.unpack_from('<I', data, 0)[0]
    constant_buffer_offset = struct.unpack_from('<I', data, 4)[0]
    resource_binding_count = struct.unpack_from('<I', data, 8)[0]
    resource_binding_offset = struct.unpack_from('<I', data, 12)[0]
    target_minor = data[16]
    target_major = data[17]
    # 2 bytes padding/flags
    # uint32 flags at offset 20
    # creator string offset at offset 24

    bindings = []
    for i in range(resource_binding_count):
        off = resource_binding_offset + i * 32
        if off + 32 > len(data):
            break

        name_offset = struct.unpack_from('<I', data, off)[0]
        bind_type = struct.unpack_from('<I', data, off + 4)[0]
        return_type = struct.unpack_from('<I', data, off + 8)[0]
        dimension = struct.unpack_from('<I', data, off + 12)[0]
        num_samples = struct.unpack_from('<I', data, off + 16)[0]
        bind_point = struct.unpack_from('<I', data, off + 20)[0]
        bind_count = struct.unpack_from('<I', data, off + 24)[0]
        flags = struct.unpack_from('<I', data, off + 28)[0]

        # Read name from string pool
        name_end = data.index(0, name_offset) if name_offset < len(data) else name_offset
        name = data[name_offset:name_end].decode('ascii', errors='replace')

        bindings.append(RdefBinding(
            name=name, bind_type=bind_type, return_type=return_type,
            dimension=dimension, num_samples=num_samples,
            bind_point=bind_point, bind_count=bind_count, flags=flags,
        ))

    return bindings


# ── SHPK PS Extractor ──

def extract_ps_from_shpk(shpk_path: str, ps_index: int) -> bytes:
    """Extract a pixel shader's DXBC blob from a .shpk file."""
    with open(shpk_path, 'rb') as f:
        data = f.read()

    pos = 0
    def u16():
        nonlocal pos; v = struct.unpack_from('<H', data, pos)[0]; pos += 2; return v
    def u32():
        nonlocal pos; v = struct.unpack_from('<I', data, pos)[0]; pos += 4; return v

    magic = u32()
    version = u32()
    dx = u32()
    file_size = u32()
    blobs_off = u32()
    strings_off = u32()
    vs_count = u32()
    ps_count = u32()
    mat_params_size = u32()
    mat_param_count = u16()
    has_defaults = u16() != 0
    const_count = u32()
    samp_count = u16()
    tex_count = u16()
    uav_count = u32()
    sys_key_count = u32()
    scene_key_count = u32()
    mat_key_count = u32()
    node_count = u32()
    alias_count = u32()
    if version >= 0x0D01:
        pos += 12  # 3 unknown u32

    # Parse shader entries to find target PS
    shaders = []
    total_shaders = vs_count + ps_count
    for i in range(total_shaders):
        blob_off = u32()
        blob_sz = u32()
        c_cnt = u16(); s_cnt = u16(); uav_cnt = u16(); t_cnt = u16()
        if version >= 0x0D01:
            pos += 4  # unk131

        resources = []
        for _ in range(c_cnt + s_cnt + uav_cnt + t_cnt):
            r_id = u32(); r_str_off = u32()
            r_str_sz = u16(); r_is_tex = u16(); r_slot = u16(); r_size = u16()
            resources.append({
                'id': r_id, 'str_off': r_str_off, 'str_sz': r_str_sz,
                'is_tex': r_is_tex, 'slot': r_slot, 'size': r_size,
            })

        shaders.append({
            'blob_off': blob_off, 'blob_sz': blob_sz,
            'const_cnt': c_cnt, 'samp_cnt': s_cnt,
            'uav_cnt': uav_cnt, 'tex_cnt': t_cnt,
            'resources': resources,
        })

    # Target PS is at index vs_count + ps_index
    target_idx = vs_count + ps_index
    if target_idx >= len(shaders):
        raise ValueError(f"PS[{ps_index}] out of range (have {ps_count} PS)")

    entry = shaders[target_idx]
    abs_off = blobs_off + entry['blob_off']
    blob = data[abs_off:abs_off + entry['blob_sz']]

    # skin.shpk PS has 0 bytes additional header
    # The blob IS the DXBC directly
    if blob[:4] != b'DXBC':
        raise ValueError(f"PS[{ps_index}] blob does not start with DXBC magic (got {blob[:4]!r})")

    return blob, entry


# ── Analysis / Dump ──

def dump_dxbc_info(dxbc: DxbcContainer):
    """Print human-readable summary of DXBC container."""
    print(f"DXBC Container: {dxbc.total_size} bytes, {len(dxbc.chunks)} chunks")
    print(f"Checksum: {' '.join(f'{c:08X}' for c in dxbc.checksum)}")

    for c in dxbc.chunks:
        print(f"\n  {c.magic.decode()} at 0x{c.offset:X}, {c.size} bytes")

        if c.magic in (b'SHEX', b'SHDR'):
            ver, tok_cnt, stype, instructions = parse_shex(c)
            major = (ver >> 4) & 0xF
            minor = ver & 0xF
            print(f"    SM{major}.{minor} {'PS' if stype == 0 else 'VS'} tokens={tok_cnt} instructions={len(instructions)}")

            # Count declarations vs computation instructions
            dcl_count = sum(1 for i in instructions if i.opcode in DCL_OPCODES or i.name.startswith('dcl'))
            print(f"    Declarations: {dcl_count}, Code: {len(instructions) - dcl_count}")

            # List all dcl_sampler and dcl_resource
            for inst in instructions:
                if inst.opcode == 0x5A:  # dcl_sampler
                    if len(inst.tokens) >= 3:
                        reg_idx = inst.tokens[2]
                        print(f"    dcl_sampler s{reg_idx}")
                elif inst.opcode == 0x58:  # dcl_resource
                    dim = (inst.tokens[0] >> 11) & 0x1F
                    dim_name = RESOURCE_DIM.get(dim, f"dim{dim}")
                    if len(inst.tokens) >= 3:
                        reg_idx = inst.tokens[2]
                        print(f"    dcl_resource_{dim_name} t{reg_idx}")

        elif c.magic == b'RDEF':
            bindings = parse_rdef_bindings(c)
            type_names = {0: 'cbuffer', 2: 'texture', 3: 'sampler'}
            dim_names = {1: 'buffer', 3: 'texture2d', 5: 'texture2darray', 8: 'texcube', 9: 'texcubearray'}
            for b in bindings:
                tn = type_names.get(b.bind_type, f'type{b.bind_type}')
                dn = dim_names.get(b.dimension, f'dim{b.dimension}')
                if b.bind_type == 2:  # texture
                    print(f"    {tn} {dn} t{b.bind_point}: {b.name}")
                elif b.bind_type == 3:  # sampler
                    print(f"    {tn} s{b.bind_point}: {b.name}")
                else:
                    print(f"    {tn} cb{b.bind_point}: {b.name}")


def find_emissive_instructions(instructions: List[ShexInstruction]) -> List[int]:
    """Find the emissive calculation instructions in EMISSIVE PS variant.

    Looking for the pattern:
      sample_b t5 → r0.xz  (normal map sample, r0.z = normal.alpha)
      mul r1.xyz, cb0[3], cb0[3]  (g_EmissiveColor squared)
      mul r1.xyz, r0.z, r1  (mask * color)

    We identify this by looking for consecutive cb0[3] references.
    """
    results = []
    for idx, inst in enumerate(instructions):
        # Look for mul with cb0[3] operand (g_EmissiveColor at offset 48 = float4 index 3)
        if inst.name == 'mul' and len(inst.tokens) >= 4:
            # Check if any source operand references cb0[3]
            for t in inst.tokens[1:]:
                # CB operand type = 0x8 (immediate indexed CB), index encoding varies
                pass  # Will implement detailed operand decoding later

        # Simpler approach: search for the specific byte pattern
        # cb0[3] is encoded as operand type 8 (CB), dimension 0, index [0][3]
        # In tokens, this appears as specific uint32 values
        pass

    return results


# ── Main ──

if __name__ == "__main__":
    script_dir = os.path.dirname(os.path.abspath(__file__))
    shpk_path = os.path.join(script_dir, "..", "..", "skin.shpk")

    if not os.path.exists(shpk_path):
        print(f"ERROR: {shpk_path} not found")
        sys.exit(1)

    print("=== Extracting PS[19] (EMISSIVE) from skin.shpk ===\n")

    try:
        blob, entry = extract_ps_from_shpk(shpk_path, 19)
    except Exception as e:
        print(f"ERROR extracting PS[19]: {e}")
        sys.exit(1)

    print(f"Extracted PS[19]: {len(blob)} bytes")
    print(f"  Shader resources: {entry['const_cnt']} constants, {entry['samp_cnt']} samplers, "
          f"{entry['uav_cnt']} UAVs, {entry['tex_cnt']} textures")

    # Save extracted DXBC
    out_path = os.path.join(script_dir, "reference", "ps_019_EMISSIVE.dxbc")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, 'wb') as f:
        f.write(blob)
    print(f"Saved to: {out_path}")

    # Parse and dump
    print("\n=== DXBC Container Analysis ===\n")
    dxbc = parse_dxbc(blob)
    dump_dxbc_info(dxbc)

    # Verify checksum
    computed = compute_dxbc_checksum(dxbc.raw)
    match = computed == dxbc.checksum
    print(f"\nChecksum verification: {'OK' if match else 'MISMATCH'}")
    if not match:
        print(f"  File:     {' '.join(f'{c:08X}' for c in dxbc.checksum)}")
        print(f"  Computed: {' '.join(f'{c:08X}' for c in computed)}")

    # Parse SHEX instructions and look for emissive pattern
    shex = dxbc.get_chunk(b'SHEX')
    if shex:
        ver, tok_cnt, stype, instructions = parse_shex(shex)
        print(f"\n=== SHEX Instruction Stream ({len(instructions)} instructions) ===\n")

        # Dump first 40 instructions (declarations + early code)
        print("--- Declarations ---")
        for i, inst in enumerate(instructions[:50]):
            tokens_hex = ' '.join(f'{t:08X}' for t in inst.tokens)
            flag = " [EXT]" if inst.extended else ""
            print(f"  [{i:3d}] @{inst.offset:04X} len={inst.length:2d} "
                  f"op=0x{inst.opcode:03X}{flag} {inst.name:20s} | {tokens_hex}")
            if inst.name == 'ret' or (not inst.name.startswith('dcl') and i > 35):
                break

        # Search for cb0[3] references (g_EmissiveColor at float4 index 3)
        # In SM5 operand encoding, cb0[3] appears as token sequence:
        #   0x00208?46 (CB operand header) 0x00000000 (cb index) 0x00000003 (element index)
        # The 0x00208246 pattern = CB operand, .xyzx swizzle
        print("\n--- Instructions referencing cb0[3] (g_EmissiveColor) ---")
        emissive_indices = []
        for i, inst in enumerate(instructions):
            for ti in range(len(inst.tokens)):
                t = inst.tokens[ti]
                # CB operand: bits[12:19] = operand type, type 8 = constant buffer
                # 0x00208?46 where ? varies by swizzle
                if (t & 0x00F08000) == 0x00208000:  # CB operand with 2D indexing
                    if ti + 2 < len(inst.tokens):
                        cb_idx = inst.tokens[ti + 1]
                        elem_idx = inst.tokens[ti + 2]
                        if cb_idx == 0 and elem_idx == 3:
                            tokens_hex = ' '.join(f'{t:08X}' for t in inst.tokens)
                            print(f"  [{i:3d}] @{inst.offset:04X} {inst.name:20s} | {tokens_hex}")
                            emissive_indices.append(i)
                            break

        if emissive_indices:
            # Show context: 3 instructions before and after the first cb0[3] reference
            first = emissive_indices[0]
            print(f"\n--- Context around emissive (instructions {first-2}..{first+3}) ---")
            for i in range(max(0, first - 2), min(len(instructions), first + 4)):
                inst = instructions[i]
                tokens_hex = ' '.join(f'{t:08X}' for t in inst.tokens)
                marker = " <<<" if i in emissive_indices else ""
                print(f"  [{i:3d}] @{inst.offset:04X} len={inst.length:2d} "
                      f"{inst.name:20s} | {tokens_hex}{marker}")

    print("\n=== Shader Resources from SHPK Entry ===\n")
    # Read resource names from shpk string table
    with open(shpk_path, 'rb') as f:
        shpk_data = f.read()
    strings_off = struct.unpack_from('<I', shpk_data, 0x14)[0]

    res = entry['resources']
    c_cnt = entry['const_cnt']
    s_cnt = entry['samp_cnt']
    u_cnt = entry['uav_cnt']
    t_cnt = entry['tex_cnt']

    def read_shpk_string(off):
        end = shpk_data.index(0, strings_off + off)
        return shpk_data[strings_off + off:end].decode('ascii', errors='replace')

    idx = 0
    print("Constants:")
    for r in res[:c_cnt]:
        name = read_shpk_string(r['str_off'])
        print(f"  [{idx}] id=0x{r['id']:08X} slot={r['slot']} size={r['size']} name={name}")
        idx += 1

    print("Samplers:")
    for r in res[c_cnt:c_cnt + s_cnt]:
        name = read_shpk_string(r['str_off'])
        print(f"  [{idx}] id=0x{r['id']:08X} slot={r['slot']} size={r['size']} tex={r['is_tex']} name={name}")
        idx += 1

    print("UAVs:")
    for r in res[c_cnt + s_cnt:c_cnt + s_cnt + u_cnt]:
        name = read_shpk_string(r['str_off'])
        print(f"  [{idx}] id=0x{r['id']:08X} slot={r['slot']} size={r['size']} name={name}")
        idx += 1

    print("Textures:")
    for r in res[c_cnt + s_cnt + u_cnt:]:
        name = read_shpk_string(r['str_off'])
        print(f"  [{idx}] id=0x{r['id']:08X} slot={r['slot']} size={r['size']} tex={r['is_tex']} name={name}")
        idx += 1
