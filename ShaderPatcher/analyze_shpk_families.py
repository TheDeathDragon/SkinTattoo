"""v14.1 shpk analyzer: node families by CategorySkinType + DXBC emissive scan.

Answers:
  - which PS indices each skin-type family (Emissive/Body/Face/BodyJJM) uses per pass
  - whether each PS reads the g_EmissiveColor constant (cb0[reg]) at all
  - whether the exact v11 mul+mul patch pattern exists in each PS
"""
import struct, sys, os

CAT_SKIN = 0x380CAED0
VALS = {0x72E697CD: 'Emissive', 0x2BDB45F1: 'Body', 0xF5673524: 'Face', 0x57FF3B64: 'BodyJJM'}
EMISSIVE_CRC = 0x38A64362


class Shpk:
    pass


def parse(path):
    data = open(path, 'rb').read()
    s = Shpk()
    pos = 0

    def u16():
        nonlocal pos
        v = struct.unpack_from('<H', data, pos)[0]; pos += 2; return v

    def u32():
        nonlocal pos
        v = struct.unpack_from('<I', data, pos)[0]; pos += 4; return v

    magic = u32(); s.version = u32(); dx = u32(); fsize = u32()
    blobs_off = u32(); strings_off = u32()
    s.vs_count = u32(); s.ps_count = u32()
    s.mat_params_size = u32()
    mat_param_count = u16(); has_defaults = u16()
    const_count = u32()
    samp_count = u16(); tex_count = u16()
    uav_count = u32()
    sys_n = u32(); scene_n = u32(); mat_n = u32()
    node_count = u32(); alias_count = u32()
    if s.version >= 0x0D01:
        pos += 12
    if s.version >= 0x0E01:
        pos += 4

    s.shaders = []
    for i in range(s.vs_count + s.ps_count):
        blob_off, blob_sz = u32(), u32()
        c, sm, uv, t = u16(), u16(), u16(), u16()
        if s.version >= 0x0D01:
            u32()
        res = []
        for j in range(c + sm + uv + t):
            rid = u32(); str_off = u32(); str_sz = u16(); is_tex = u16(); slot = u16(); size = u16()
            res.append((rid, slot, size))
        s.shaders.append({'off': blob_off, 'sz': blob_sz, 'res': res,
                          'counts': (c, sm, uv, t)})

    s.mat_params = []
    for i in range(mat_param_count):
        pid = u32(); off = u16(); sz = u16()
        s.mat_params.append((pid, off, sz))
    if has_defaults:
        s.defaults = data[pos:pos + s.mat_params_size]; pos += s.mat_params_size

    pos += const_count * 16 + samp_count * 16 + tex_count * 16 + uav_count * 16

    s.sys_keys = [(u32(), u32()) for _ in range(sys_n)]
    s.scene_keys = [(u32(), u32()) for _ in range(scene_n)]
    s.mat_keys = [(u32(), u32()) for _ in range(mat_n)]
    pos += 8  # sv defaults

    s.nodes = []
    for ni in range(node_count):
        sel = u32(); pass_count = u32()
        pos += 16  # pass indices
        pos += 8   # unk131 keys
        sysk = [u32() for _ in range(sys_n)]
        scek = [u32() for _ in range(scene_n)]
        matk = [u32() for _ in range(mat_n)]
        pos += 8   # sv keys
        passes = []
        for pi in range(pass_count):
            p = [u32() for _ in range(6)]
            passes.append(p)  # (Id, Vs, Ps, A, B, C)
        s.nodes.append({'sel': sel, 'mat': matk, 'passes': passes})

    s.blob = data[blobs_off:strings_off]
    s.strings = data[strings_off:]
    return s


def get_shex(blob_bytes):
    if blob_bytes[:4] != b'DXBC':
        return None
    n = struct.unpack_from('<I', blob_bytes, 28)[0]
    for i in range(n):
        off = struct.unpack_from('<I', blob_bytes, 32 + i * 4)[0]
        tag = blob_bytes[off:off + 4]
        if tag in (b'SHEX', b'SHDR'):
            sz = struct.unpack_from('<I', blob_bytes, off + 4)[0]
            return blob_bytes[off + 8:off + 8 + sz]
    return None


def scan_ps(s, ps_idx, emissive_reg):
    sh = s.shaders[s.vs_count + ps_idx]
    blob = s.blob[sh['off']:sh['off'] + sh['sz']]
    shex = get_shex(bytes(blob))
    if shex is None:
        return None
    # flexible v11 pattern: mul rN.xyz, cb0[reg].xyzx, cb0[reg].xyzx (any dest reg)
    tail = struct.pack('<6I', 0x00208246, 0x00000000, emissive_reg,
                       0x00208246, 0x00000000, emissive_reg)
    head = struct.pack('<I', 0x09000038)
    has_exact = False
    at = 0
    while True:
        i = shex.find(tail, at)
        if i < 0:
            break
        if i >= 12 and shex[i - 12:i - 8] == head:
            has_exact = True
            break
        at = i + 4
    # loose: any cb0[reg] operand -- type bits[12:19]==8 (CONSTANT_BUFFER), 2D index
    loose = 0
    probe = struct.pack('<II', 0x00000000, emissive_reg)
    at = 0
    while True:
        i = shex.find(probe, at)
        if i < 0:
            break
        if i >= 4:
            tok = struct.unpack_from('<I', shex, i - 4)[0]
            if ((tok >> 12) & 0xFF) == 0x08 and ((tok >> 20) & 0x3) == 2:
                loose += 1
        at = i + 4
    return {'exact': has_exact, 'cbreads': loose, 'size': len(shex),
            'samplers': [r for r in sh['res']]}


def family_report(s, label):
    ski = next((i for i, (k, d) in enumerate(s.mat_keys) if k == CAT_SKIN), -1)
    print(f'=== {label}: v={s.version:#x} vs={s.vs_count} ps={s.ps_count} '
          f'nodes={len(s.nodes)} matkeys={[hex(k) for k, d in s.mat_keys]} skinTypeIdx={ski}')
    emis = [(p, o, z) for (p, o, z) in s.mat_params if p == EMISSIVE_CRC]
    print(f'  g_EmissiveColor param: {[(hex(p), o, z) for p, o, z in emis]}')
    if not emis:
        return None, None
    reg = emis[0][1] // 16
    print(f'  -> cb0 register {reg}')
    if ski < 0:
        return None, reg
    fams = {}
    for n in s.nodes:
        v = VALS.get(n['mat'][ski])
        if v is None:
            continue
        fams.setdefault(v, [])
        fams[v].append(n)
    for v, nodes in fams.items():
        # collect (passId -> set of PS)
        by_pass = {}
        for n in nodes:
            for slot, p in enumerate(n['passes']):
                by_pass.setdefault((slot, p[0]), set()).add(p[2])
        print(f'  family {v}: {len(nodes)} nodes')
        for (slot, pid), pss in sorted(by_pass.items()):
            print(f'    pass[{slot}] id={pid:#010x}: PS {sorted(pss)[:8]}{"..." if len(pss) > 8 else ""} ({len(pss)} unique)')
    return fams, reg


def full_report(path):
    s = parse(path)
    fams, reg = family_report(s, os.path.basename(path))
    if reg is None:
        return

    if fams:
        # scan every family's PSes per pass slot for emissive reads
        for fam in sorted(fams):
            pss = set()
            for n in fams[fam]:
                for slot, p in enumerate(n['passes']):
                    pss.add((slot, p[2]))
            slots = {}
            for slot, ps in pss:
                slots.setdefault(slot, set()).add(ps)
            for slot in sorted(slots):
                hits_exact, hits_loose, none_ = [], [], []
                for ps in sorted(slots[slot]):
                    r = scan_ps(s, ps, reg)
                    if r is None:
                        continue
                    if r['exact']:
                        hits_exact.append(ps)
                    elif r['cbreads'] > 0:
                        hits_loose.append((ps, r['cbreads']))
                    else:
                        none_.append(ps)
                print(f'  [{fam}] pass-slot {slot}: exact={hits_exact[:10]}{"..." if len(hits_exact) > 10 else ""} '
                      f'loose={hits_loose[:6]} none={len(none_)}')
    else:
        # no CategorySkinType key (e.g. iris.shpk): scan all PSes flat
        exact_ps, loose_ps = [], []
        for ps in range(s.ps_count):
            r = scan_ps(s, ps, reg)
            if r is None:
                continue
            if r['exact']:
                exact_ps.append(ps)
            elif r['cbreads'] > 0:
                loose_ps.append((ps, r['cbreads']))
        print(f'  PS with EXACT v11 mul+mul pattern: {exact_ps}')
        print(f'  PS with loose cb0[{reg}] reads: {loose_ps[:20]}')


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print('usage: analyze_shpk_families.py <skin.shpk> [more.shpk ...]')
        sys.exit(1)
    for p in sys.argv[1:]:
        full_report(p)
        print()
