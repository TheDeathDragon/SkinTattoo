"""Patch skin.shpk: add ColorTable-enabled PS variant + Node mappings.

Approach:
  1. Parse the complete .shpk binary
  2. Clone PS[19] with patched DXBC blob + g_SamplerTable resources
  3. Clone the 4 EMISSIVE nodes, changing PS index to the new variant
  4. Keep the same MaterialKey value (Emissive) for now (simplest test)
  5. Rebuild .shpk with correct offsets
"""
import struct
import os
import sys
import copy
from dxbc_patch_colortable import (
    dxbc_checksum, d3d_disassemble, d3d_validate,
    patch_shex_add_declarations, patch_shex_replace_emissive,
    rebuild_dxbc, encode_u32,
)

# CRC constants
CRC_SAMPLER_TABLE = 0x2005679F  # g_SamplerTable

def parse_shpk_full(data: bytes) -> dict:
    """Parse a .shpk file into a complete structure suitable for rebuilding."""
    pos = 0
    def u8():
        nonlocal pos; v = data[pos]; pos += 1; return v
    def u16():
        nonlocal pos; v = struct.unpack_from("<H", data, pos)[0]; pos += 2; return v
    def u32():
        nonlocal pos; v = struct.unpack_from("<I", data, pos)[0]; pos += 4; return v
    def read(n):
        nonlocal pos; v = data[pos:pos+n]; pos += n; return v

    # Header
    magic = u32(); version = u32(); dx = u32(); file_size = u32()
    blobs_off = u32(); strings_off = u32()
    vs_count = u32(); ps_count = u32()
    mat_params_size = u32()
    mat_param_count = u16(); has_defaults = u16()
    const_count = u32(); samp_count = u16(); tex_count = u16()
    uav_count = u32()
    sys_key_count = u32(); scene_key_count = u32(); mat_key_count = u32()
    node_count = u32(); alias_count = u32()
    unk_abc = [u32(), u32(), u32()]  # v0x0D01

    # Shader entries
    shaders = []
    for i in range(vs_count + ps_count):
        blob_off = u32(); blob_sz = u32()
        c_cnt = u16(); s_cnt = u16(); uav_cnt = u16(); t_cnt = u16()
        unk131 = u32()
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
            'c_cnt': c_cnt, 's_cnt': s_cnt, 'uav_cnt': uav_cnt, 't_cnt': t_cnt,
            'unk131': unk131, 'resources': resources,
        })

    # Material params
    mat_params_raw = read(mat_param_count * 8)
    mat_defaults_raw = read(mat_params_size) if has_defaults else b''

    # Global resources
    global_consts = read(const_count * 16)
    global_samplers = read(samp_count * 16)
    global_textures = read(tex_count * 16)
    global_uavs = read(uav_count * 16)

    # Keys
    sys_keys = [(u32(), u32()) for _ in range(sys_key_count)]
    scene_keys = [(u32(), u32()) for _ in range(scene_key_count)]
    mat_keys = [(u32(), u32()) for _ in range(mat_key_count)]
    sv_defaults = [u32(), u32()]

    # Nodes
    nodes = []
    for _ in range(node_count):
        selector = u32()
        pass_count = u32()
        pass_indices = list(read(16))
        unk131_keys = [u32(), u32()]
        node_sys = [u32() for _ in range(sys_key_count)]
        node_scene = [u32() for _ in range(scene_key_count)]
        node_mat = [u32() for _ in range(mat_key_count)]
        node_sv = [u32() for _ in range(2)]
        passes = []
        for _ in range(pass_count):
            pid = u32(); vs = u32(); ps = u32()
            pa = u32(); pb = u32(); pc = u32()
            passes.append({'id': pid, 'vs': vs, 'ps': ps, 'a': pa, 'b': pb, 'c': pc})
        nodes.append({
            'selector': selector, 'pass_count': pass_count,
            'pass_indices': pass_indices, 'unk131_keys': unk131_keys,
            'sys_keys': node_sys, 'scene_keys': node_scene,
            'mat_keys': node_mat, 'sv_keys': node_sv,
            'passes': passes,
        })

    # Aliases
    aliases = []
    for _ in range(alias_count):
        aliases.append((u32(), u32()))

    # Additional data between pos and blobs_off
    additional = data[pos:blobs_off]

    # Blob data
    blob_section = data[blobs_off:strings_off]
    string_section = data[strings_off:]

    return {
        'version': version, 'dx': dx,
        'vs_count': vs_count, 'ps_count': ps_count,
        'mat_params_size': mat_params_size, 'mat_param_count': mat_param_count,
        'has_defaults': has_defaults, 'const_count': const_count,
        'samp_count': samp_count, 'tex_count': tex_count, 'uav_count': uav_count,
        'sys_key_count': sys_key_count, 'scene_key_count': scene_key_count,
        'mat_key_count': mat_key_count,
        'unk_abc': unk_abc,
        'shaders': shaders,
        'mat_params_raw': mat_params_raw, 'mat_defaults_raw': mat_defaults_raw,
        'global_consts': global_consts, 'global_samplers': global_samplers,
        'global_textures': global_textures, 'global_uavs': global_uavs,
        'sys_keys': sys_keys, 'scene_keys': scene_keys, 'mat_keys': mat_keys,
        'sv_defaults': sv_defaults,
        'nodes': nodes, 'aliases': aliases,
        'additional': additional,
        'blob_section': bytearray(blob_section),
        'string_section': bytearray(string_section),
    }


def add_string(shpk: dict, s: str) -> int:
    """Add a string to the string section, return its offset."""
    encoded = s.encode('ascii') + b'\x00'
    offset = len(shpk['string_section'])
    shpk['string_section'].extend(encoded)
    return offset


def add_blob(shpk: dict, blob_data: bytes) -> tuple:
    """Add blob data to the blob section, return (offset, size)."""
    offset = len(shpk['blob_section'])
    shpk['blob_section'].extend(blob_data)
    return offset, len(blob_data)


def rebuild_shpk(shpk: dict) -> bytes:
    """Rebuild complete .shpk binary from parsed structure."""
    buf = bytearray()
    def w8(v): buf.append(v & 0xFF)
    def w16(v): buf.extend(struct.pack('<H', v))
    def w32(v): buf.extend(struct.pack('<I', v))
    def w_bytes(b): buf.extend(b)

    vs_count = shpk['vs_count']
    ps_count = shpk['ps_count']
    node_count = len(shpk['nodes'])
    alias_count = len(shpk['aliases'])

    # Header (placeholder for file_size, blobs_off, strings_off)
    w32(0x6B506853)  # ShPk
    w32(shpk['version'])
    w32(shpk['dx'])
    file_size_pos = len(buf); w32(0)  # placeholder
    blobs_off_pos = len(buf); w32(0)  # placeholder
    strings_off_pos = len(buf); w32(0)  # placeholder
    w32(vs_count)
    w32(ps_count)
    w32(shpk['mat_params_size'])
    w16(shpk['mat_param_count']); w16(shpk['has_defaults'])
    w32(shpk['const_count'])
    w16(shpk['samp_count']); w16(shpk['tex_count'])
    w32(shpk['uav_count'])
    w32(shpk['sys_key_count']); w32(shpk['scene_key_count']); w32(shpk['mat_key_count'])
    w32(node_count)
    w32(alias_count)
    for v in shpk['unk_abc']:
        w32(v)

    # Shader entries
    for s in shpk['shaders']:
        w32(s['blob_off']); w32(s['blob_sz'])
        w16(s['c_cnt']); w16(s['s_cnt']); w16(s['uav_cnt']); w16(s['t_cnt'])
        w32(s['unk131'])
        for r in s['resources']:
            w32(r['id']); w32(r['str_off'])
            w16(r['str_sz']); w16(r['is_tex']); w16(r['slot']); w16(r['size'])

    # Material params
    w_bytes(shpk['mat_params_raw'])
    if shpk['has_defaults']:
        w_bytes(shpk['mat_defaults_raw'])

    # Global resources
    w_bytes(shpk['global_consts'])
    w_bytes(shpk['global_samplers'])
    w_bytes(shpk['global_textures'])
    w_bytes(shpk['global_uavs'])

    # Keys
    for kid, kdef in shpk['sys_keys']:
        w32(kid); w32(kdef)
    for kid, kdef in shpk['scene_keys']:
        w32(kid); w32(kdef)
    for kid, kdef in shpk['mat_keys']:
        w32(kid); w32(kdef)
    for v in shpk['sv_defaults']:
        w32(v)

    # Nodes
    for n in shpk['nodes']:
        w32(n['selector'])
        w32(n['pass_count'])
        w_bytes(bytes(n['pass_indices']))
        for v in n['unk131_keys']:
            w32(v)
        for v in n['sys_keys']:
            w32(v)
        for v in n['scene_keys']:
            w32(v)
        for v in n['mat_keys']:
            w32(v)
        for v in n['sv_keys']:
            w32(v)
        for p in n['passes']:
            w32(p['id']); w32(p['vs']); w32(p['ps'])
            w32(p['a']); w32(p['b']); w32(p['c'])

    # Aliases
    for a, b in shpk['aliases']:
        w32(a); w32(b)

    # Additional data
    w_bytes(shpk['additional'])

    # Blobs offset
    blobs_off = len(buf)
    struct.pack_into('<I', buf, blobs_off_pos, blobs_off)
    w_bytes(shpk['blob_section'])

    # Strings offset
    strings_off = len(buf)
    struct.pack_into('<I', buf, strings_off_pos, strings_off)
    w_bytes(shpk['string_section'])

    # File size
    struct.pack_into('<I', buf, file_size_pos, len(buf))

    return bytes(buf)


def patch_skin_shpk(input_path: str, output_path: str):
    """Main: patch skin.shpk to replace PS[19] blob with ColorTable version."""
    with open(input_path, 'rb') as f:
        data = f.read()

    print(f"Parsing {input_path} ({len(data)} bytes)...")
    shpk = parse_shpk_full(data)
    print(f"  VS={shpk['vs_count']} PS={shpk['ps_count']} Nodes={len(shpk['nodes'])} Aliases={len(shpk['aliases'])}")

    vs_count = shpk['vs_count']
    ps19 = shpk['shaders'][vs_count + 19]

    # Extract PS[19] DXBC blob
    blob_start = ps19['blob_off']
    blob_end = blob_start + ps19['blob_sz']
    original_dxbc = bytes(shpk['blob_section'][blob_start:blob_end])
    print(f"  PS[19] blob: {len(original_dxbc)} bytes at blob offset 0x{blob_start:X}")

    if original_dxbc[:4] != b'DXBC':
        raise ValueError("PS[19] blob is not DXBC")

    # Patch the DXBC
    print("\nPatching DXBC...")

    # Extract SHEX
    chunk_count = struct.unpack_from('<I', original_dxbc, 28)[0]
    shex_data = None
    for i in range(chunk_count):
        off = struct.unpack_from('<I', original_dxbc, 32 + i*4)[0]
        magic = original_dxbc[off:off+4]
        size = struct.unpack_from('<I', original_dxbc, off+4)[0]
        if magic in (b'SHEX', b'SHDR'):
            shex_data = bytearray(original_dxbc[off+8:off+8+size])
            break

    patched_shex = patch_shex_add_declarations(shex_data, 5, 10)
    patched_shex = patch_shex_replace_emissive(patched_shex)
    patched_dxbc = rebuild_dxbc(original_dxbc, bytes(patched_shex))

    print(f"  Patched DXBC: {len(patched_dxbc)} bytes")

    # Validate
    if d3d_validate(bytes(patched_dxbc)):
        print("  D3DCompiler validation: OK")
    else:
        print("  D3DCompiler validation: FAILED!")
        return

    # Replace PS[19]'s blob in the blob section
    # Since size changed, we need to rebuild blob section offsets
    # Strategy: replace PS[19]'s blob in-place, adjust all subsequent blob offsets

    old_size = ps19['blob_sz']
    new_size = len(patched_dxbc)
    delta = new_size - old_size

    print(f"\n  Blob delta: {delta:+d} bytes")

    # Replace in blob section
    new_blob_section = bytearray()
    new_blob_section.extend(shpk['blob_section'][:blob_start])
    new_blob_section.extend(patched_dxbc)
    new_blob_section.extend(shpk['blob_section'][blob_end:])
    shpk['blob_section'] = new_blob_section

    # Update PS[19]'s blob size
    ps19['blob_sz'] = new_size

    # Adjust blob offsets for all shaders that come AFTER PS[19]'s blob
    for s in shpk['shaders']:
        if s['blob_off'] > blob_start:
            s['blob_off'] += delta

    # Add g_SamplerTable to PS[19]'s resource list
    # Need: 1 sampler entry + 1 texture entry
    str_off = add_string(shpk, "g_SamplerTable")
    str_sz = len("g_SamplerTable")

    # Sampler resource
    sampler_res = {
        'id': CRC_SAMPLER_TABLE, 'str_off': str_off, 'str_sz': str_sz,
        'is_tex': 0, 'slot': 5, 'size': 5,  # s5
    }
    # Texture resource
    texture_res = {
        'id': CRC_SAMPLER_TABLE, 'str_off': str_off, 'str_sz': str_sz,
        'is_tex': 1, 'slot': 10, 'size': 6,  # t10, size=sampler_index+1
    }

    # Insert sampler after existing samplers, texture after existing textures
    c = ps19['c_cnt']
    s = ps19['s_cnt']
    u = ps19['uav_cnt']
    t = ps19['t_cnt']

    # Resources order: [constants...][samplers...][uavs...][textures...]
    insert_samp_at = c + s
    insert_tex_at = c + s + u + t  # append at end

    ps19['resources'].insert(insert_samp_at, sampler_res)
    ps19['resources'].append(texture_res)
    ps19['s_cnt'] += 1
    ps19['t_cnt'] += 1

    print(f"  Added g_SamplerTable: sampler s5 + texture t10")
    print(f"  PS[19] resources: {ps19['c_cnt']}C {ps19['s_cnt']}S {ps19['uav_cnt']}U {ps19['t_cnt']}T")

    # Rebuild
    print("\nRebuilding .shpk...")
    result = rebuild_shpk(shpk)
    print(f"  Output: {len(result)} bytes (delta: {len(result) - len(data):+d})")

    # Quick sanity: re-parse to verify
    try:
        verify = parse_shpk_full(result)
        v_ps19 = verify['shaders'][verify['vs_count'] + 19]
        vblob_start = v_ps19['blob_off']
        vblob = verify['blob_section'][vblob_start:vblob_start + v_ps19['blob_sz']]
        if vblob[:4] == b'DXBC' and d3d_validate(bytes(vblob)):
            print("  Re-parse + D3D validation: OK")
        else:
            print("  Re-parse: DXBC header OK but D3D validation failed")
    except Exception as e:
        print(f"  Re-parse FAILED: {e}")

    with open(output_path, 'wb') as f:
        f.write(result)
    print(f"\nSaved to: {output_path}")


if __name__ == "__main__":
    script_dir = os.path.dirname(os.path.abspath(__file__))
    input_path = os.path.join(script_dir, "..", "..", "skin.shpk")
    output_path = os.path.join(script_dir, "skin_patched.shpk")

    if not os.path.exists(input_path):
        print(f"ERROR: vanilla skin.shpk not found at {input_path}")
        print("Place the vanilla skin.shpk (exported from game) next to the SkinTatoo directory.")
        sys.exit(1)

    patch_skin_shpk(input_path, output_path)
