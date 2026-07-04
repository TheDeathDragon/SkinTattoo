"""CBuffer atlas: dump per-shader constant-buffer/sampler/texture bindings,
material param tables, key/node structure and per-pass binding reports.

FFXIV name hash: reflected CRC-32 poly 0xEDB88320, init=0, no final xor
  == ~zlib.crc32(name, 0xFFFFFFFF) & 0xFFFFFFFF

Usage: python cbuffer_atlas.py shpkext/skin_current.shpk shpkext/iris.shpk ...
"""
import struct, sys, zlib
from collections import defaultdict


def ffcrc(name):
    return ~zlib.crc32(name.encode(), 0xFFFFFFFF) & 0xFFFFFFFF


assert ffcrc('g_SamplerNormal') == 0x0C5EC1F1
assert ffcrc('g_EmissiveColor') == 0x38A64362

KNOWN_NAMES = [
    'g_AlphaThreshold', 'g_AlphaAperture', 'g_AmbientOcclusionMask', 'g_AngleClip',
    'g_BackScatterPower', 'g_Color', 'g_ColorUVScale', 'g_DetailColor',
    'g_DetailColorFadeDistance', 'g_DetailColorMipBias', 'g_DetailColorUvScale',
    'g_DetailID', 'g_DetailNormalScale', 'g_DetailNormalUvScale', 'g_DiffuseColor',
    'g_EmissiveColor', 'g_EnvMapPower', 'g_FarClip', 'g_Fresnel', 'g_FresnelValue0',
    'g_FurLength', 'g_GlassIOR', 'g_GlassThicknessMax', 'g_Gradation',
    'g_HeightMapScale', 'g_HeightScale', 'g_InclusionAperture', 'g_Intensity',
    'g_IrisOptionColorEmissiveIntensity', 'g_IrisOptionColorRate', 'g_IrisRingColor',
    'g_IrisRingEmissiveIntensity', 'g_IrisRingForceColor', 'g_IrisThickness',
    'g_LayerColor', 'g_LayerDepth', 'g_LayerIrregularity', 'g_LayerScale',
    'g_LayerVelocity', 'g_LipFresnelValue0', 'g_LipRoughnessScale', 'g_LipShininess',
    'g_MultiDetailColor', 'g_MultiDiffuseColor', 'g_MultiEmissiveColor',
    'g_MultiHeightScale', 'g_MultiNormalScale', 'g_MultiSpecularColor',
    'g_MultiSSAOMask', 'g_NormalScale', 'g_NormalScale1', 'g_NormalUVScale',
    'g_OutlineColor', 'g_OutlineWidth', 'g_PrefersFailure', 'g_ReflectionPower',
    'g_ScatteringLevel', 'g_ShaderID', 'g_ShadowAlphaThreshold', 'g_ShadowOffset',
    'g_ShadowPosOffset', 'g_SheenAperture', 'g_SheenRate', 'g_SheenTintRate',
    'g_Shininess', 'g_SpecularColor', 'g_SpecularColorMask', 'g_SpecularMask',
    'g_SpecularPower', 'g_SpecularUVScale', 'g_SSAOMask', 'g_TexAnim', 'g_TexU',
    'g_TexV', 'g_TileAlpha', 'g_TileIndex', 'g_TileScale', 'g_ToonIndex',
    'g_ToonLightScale', 'g_ToonReflectionScale', 'g_ToonSpecIndex',
    'g_TransparencyDistance', 'g_UVScrollTime', 'g_WaveSpeed', 'g_WaveTime',
    'g_WaveTime1', 'g_WhiteEyeColor', 'g_SphereMapIndex', 'g_SubColor',
    'g_CausticsReflectionPowerBright', 'g_CausticsReflectionPowerDark',
    'g_TextureMipBias', 'g_OutlineType', 'g_VertexMovementScale', 'g_SkinColor',
    'g_SkinFresnelValue0', 'g_SkinShininess', 'g_LipColor', 'g_HairColor',
    'g_MainColor', 'g_MeshColor', 'g_OptionColor', 'g_RightColor', 'g_LeftColor',
    'g_DecalColor', 'g_WhitecapColor', 'g_WetnessAperture', 'g_SubSurfacePower',
    'g_SubSurfaceProfileID', 'g_SubSurfaceWidth', 'g_Wetness', 'g_WetnessDepth',
    # keys
    'CategorySkinType', 'CategoryHairType', 'CategoryIrisType', 'CategoryEyeType',
    'DecalMode', 'VertexColorMode', 'TextureMode', 'LightingLow', 'DefaultTechnique',
    'SubViewShadow0', 'SubViewShadow1', 'SubViewCubeMap0', 'TransformView',
    'TransformProj', 'GetAmbientLight', 'GetAmbientOcclusion', 'ApplyDitherClip',
    'ApplyVertexMovement', 'GetRLR', 'GetUnderWaterLighting', 'GetVelocity',
    'ApplyDissolveColor', 'ApplyAuraColor', 'ApplyOmniShadow', 'DrawDepthMode',
    'DrawOffscreen', 'CalculateInstancingPosition', 'ApplyWavingAnim',
    'ApplyWavingAnimation', 'GetInstanceData', 'GetInstancingData', 'GetShadow',
    'IsDepthShadow', 'GetLocalPosition', 'ForceFarZ', 'ApplyConeAttenuation',
    'ApplyFog', 'ApplyDither', 'AddLayer', 'Color', 'Default', 'Depth', 'Shadow',
    'Iris', 'Skin', 'Face', 'Body', 'BodyJJM', 'Emissive', 'PASS_LIGHTING_OPAQUE',
]
CRC2NAME = {ffcrc(n): n for n in KNOWN_NAMES}
CRC2NAME.update({
    0x380CAED0: 'CategorySkinType', 0xD2777173: 'DecalMode', 0xF52CCF05: 'VertexColorMode',
})


def nameof(crc):
    return CRC2NAME.get(crc, f'0x{crc:08X}')


OPNAMES = {
    0x00: 'add', 0x01: 'and', 0x09: 'dp2', 0x0A: 'dp3', 0x0B: 'dp4', 0x0E: 'div',
    0x12: 'eq', 0x13: 'exp', 0x15: 'ftoi', 0x16: 'ftou', 0x18: 'ge', 0x1D: 'ge',
    0x1F: 'iadd', 0x23: 'customdata', 0x26: 'if', 0x2B: 'ilt', 0x2C: 'imad',
    0x2F: 'imul', 0x31: 'lt', 0x32: 'mad', 0x33: 'min', 0x34: 'max', 0x36: 'mov',
    0x37: 'movc', 0x38: 'mul', 0x3D: 'resinfo', 0x3E: 'ret', 0x40: 'round_ne',
    0x41: 'round_ni', 0x43: 'rsq', 0x45: 'sample', 0x46: 'sample_c',
    0x48: 'sample_l', 0x49: 'sample_d', 0x4B: 'sqrt', 0x4D: 'sincos',
    0x4E: 'udiv', 0x56: 'utof', 0x57: 'xor', 0x5B: 'itof',
}


def parse(path):
    data = open(path, 'rb').read()
    s = type('S', (), {})()
    s.data = data
    pos = [0]

    def u16():
        v = struct.unpack_from('<H', data, pos[0])[0]; pos[0] += 2; return v

    def u32():
        v = struct.unpack_from('<I', data, pos[0])[0]; pos[0] += 4; return v

    u32(); s.version = u32(); u32(); u32()
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
        pos[0] += 12
    if s.version >= 0x0E01:
        pos[0] += 4

    s.shaders = []
    for i in range(s.vs_count + s.ps_count):
        blob_off, blob_sz = u32(), u32()
        c, sm, uv, t = u16(), u16(), u16(), u16()
        if s.version >= 0x0D01:
            u32()
        res = []
        for j in range(c + sm + uv + t):
            rid = u32(); str_off = u32(); str_sz = u16(); is_tex = u16(); slot = u16(); size = u16()
            kind = 'cb' if j < c else 's' if j < c + sm else 'u' if j < c + sm + uv else 't'
            res.append({'id': rid, 'kind': kind, 'slot': slot, 'size': size, 'str_off': str_off})
        s.shaders.append({'off': blob_off, 'sz': blob_sz, 'res': res})

    s.mat_params = []
    for _ in range(mat_param_count):
        pid = u32(); off = u16(); sz = u16()
        s.mat_params.append({'id': pid, 'offset': off, 'size': sz})
    if has_defaults:
        s.defaults = data[pos[0]:pos[0] + s.mat_params_size]
        pos[0] += s.mat_params_size
    else:
        s.defaults = b''
    pos[0] += const_count * 16 + samp_count * 16 + tex_count * 16 + uav_count * 16
    s.sys_keys = [(u32(), u32()) for _ in range(sys_n)]
    s.scene_keys = [(u32(), u32()) for _ in range(scene_n)]
    s.mat_keys = [(u32(), u32()) for _ in range(mat_n)]
    s.sub_view_keys = [(u32(), u32())]

    s.nodes = []
    for ni in range(node_count):
        sel = u32(); pc = u32()
        pos[0] += 16 + 8
        sysv = [u32() for _ in range(sys_n)]
        scenev = [u32() for _ in range(scene_n)]
        matv = [u32() for _ in range(mat_n)]
        subv = [u32(), u32()]
        passes = []
        for pi in range(pc):
            p = [u32() for _ in range(6)]
            passes.append(p)
        s.nodes.append({'sel': sel, 'sys': sysv, 'scene': scenev, 'mat': matv,
                        'sub': subv, 'passes': passes})
    s.blob = data[blobs_off:strings_off]
    s.strings = data[strings_off:]
    return s


def rstr(s, off):
    end = s.strings.find(b'\0', off)
    return s.strings[off:end].decode('ascii', 'replace')


def get_shex(blob):
    if blob[:4] != b'DXBC':
        return None
    n = struct.unpack_from('<I', blob, 28)[0]
    for i in range(n):
        off = struct.unpack_from('<I', blob, 32 + i * 4)[0]
        if blob[off:off + 4] in (b'SHEX', b'SHDR'):
            sz = struct.unpack_from('<I', blob, off + 4)[0]
            return blob[off + 8:off + 8 + sz]
    return None


def instructions(shex):
    p = 8
    n = len(shex)
    while p + 4 <= n:
        tok = struct.unpack_from('<I', shex, p)[0]
        op = tok & 0x7FF
        if op == 0x23:
            ln = struct.unpack_from('<I', shex, p + 4)[0] * 4
        else:
            ln = ((tok >> 24) & 0x7F) * 4
            if ln == 0:
                ln = 4
        if p + ln > n:
            break
        toks = list(struct.unpack_from(f'<{ln // 4}I', shex, p))
        yield p, op, ln, toks
        p += ln


def cb_reads(toks):
    out = []
    i = 0
    while i < len(toks) - 2:
        t = toks[i]
        if ((t >> 12) & 0xFF) == 0x08 and ((t >> 20) & 0x3) == 2:
            ext = (t >> 31) & 1
            j = i + 1 + ext
            if j + 1 < len(toks):
                out.append((toks[j], toks[j + 1], i))
        i += 1
    return out


def shex_decls(shex):
    """Return (temps, dcl_cb_slots, dcl_s, dcl_t, v_inputs)."""
    temps = None
    cbs, ss, ts, vs = [], [], [], []
    for p, op, ln, toks in instructions(shex):
        if op == 0x68:
            temps = toks[1]
        elif op == 0x59:
            cbs.append(toks[2])
        elif op == 0x5A:
            ss.append(toks[2])
        elif op in (0x58, 0xA1, 0xA2):
            ts.append(toks[2])
        elif op in (0x62, 0x63, 0x64):
            vs.append(toks[2])
        elif op == 0x23:
            continue
        elif not (0x58 <= op <= 0x6A or 0x9C <= op <= 0xA5):
            break
    return temps, sorted(cbs), sorted(ss), sorted(ts), sorted(set(vs))


def analyze_shpk(path, dump_passes=True):
    s = parse(path)
    name = path.split('/')[-1].split('\\')[-1]
    print(f'\n{"=" * 70}\n{name}: ver=0x{s.version:04X} VS={s.vs_count} PS={s.ps_count} '
          f'matParamsSize=0x{s.mat_params_size:X} nodes={len(s.nodes)}')
    print(f'  sceneKeys={[(nameof(k), nameof(d)) for k, d in s.scene_keys]}')
    print(f'  matKeys={[(nameof(k), nameof(d)) for k, d in s.mat_keys]}')

    print(f'  -- material params ({len(s.mat_params)}):')
    for p in sorted(s.mat_params, key=lambda x: x['offset']):
        dflt = ''
        if s.defaults and p['offset'] + p['size'] <= len(s.defaults):
            vals = struct.unpack_from(f'<{p["size"] // 4}f', s.defaults, p['offset'])
            dflt = ' = ' + ','.join(f'{v:g}' for v in vals)
        print(f'    +0x{p["offset"]:03X} sz={p["size"]:2} {nameof(p["id"])}{dflt}')

    for kind, label in (('cb', 'cbuffers'), ('s', 'samplers'), ('t', 'textures')):
        atlas = defaultdict(lambda: defaultdict(int))
        sizes = defaultdict(set)
        for i, sh in enumerate(s.shaders):
            stage = 'VS' if i < s.vs_count else 'PS'
            for r in sh['res']:
                if r['kind'] != kind:
                    continue
                nm = rstr(s, r['str_off'])
                atlas[nm][(stage, r['slot'])] += 1
                sizes[nm].add(r['size'])
        print(f'  -- {label}:')
        for nm in sorted(atlas):
            slots = ', '.join(f'{st}:b{sl}x{c}' if kind == 'cb' else f'{st}:{sl}x{c}'
                              for (st, sl), c in sorted(atlas[nm].items()))
            print(f'    {nm:36} sizes={sorted(sizes[nm])} {slots}')

    passmap = defaultdict(set)
    for n_ in s.nodes:
        for slot, p in enumerate(n_['passes']):
            passmap[(slot, p[0])].add(p[2])
    print(f'  -- passes (slot, id) -> PS count:')
    for (slot, pid), pss in sorted(passmap.items()):
        print(f'    pass[{slot}] id=0x{pid:08X}: {len(pss)} PS  e.g. {sorted(pss)[:8]}')

    if not dump_passes:
        return s

    # per-pass binding report
    em = next((p for p in s.mat_params if p['id'] == 0x38A64362), None)
    em_elem = em['offset'] // 16 if em else None
    print(f'  -- per-pass binding report (matCB elem {em_elem} = g_EmissiveColor):')
    for (slot, pid), pss in sorted(passmap.items()):
        agg = defaultdict(list)
        for psi in sorted(pss):
            sh = s.shaders[s.vs_count + psi]
            slots = {}
            for r in sh['res']:
                if r['kind'] != 'cb':
                    continue
                nm = rstr(s, r['str_off'])
                if nm in ('g_MaterialParameter', 'g_PbrParameterCommon', 'g_CommonParameter'):
                    slots[nm] = r['slot']
            blob = bytes(s.blob[sh['off']:sh['off'] + sh['sz']])
            shex = get_shex(blob)
            if shex is None:
                continue
            temps, cbs, ss, ts, vs = shex_decls(shex)
            emhits = 0
            mat_slot = slots.get('g_MaterialParameter')
            if mat_slot is not None and em_elem is not None:
                for p_, op, ln, toks in instructions(shex):
                    for (cbi, cbe, ti) in cb_reads(toks):
                        if cbi == mat_slot and cbe == em_elem:
                            emhits += 1
            key = (slots.get('g_MaterialParameter'), slots.get('g_PbrParameterCommon'),
                   slots.get('g_CommonParameter'), temps,
                   max(ss, default=-1), max(ts, default=-1), emhits, 2 in vs)
            agg[key].append(psi)
        for key, members in sorted(agg.items(), key=lambda kv: -len(kv[1])):
            mat, pbr, comm, temps, smax, tmax, emhits, v2 = key
            print(f'    pass[{slot}] 0x{pid:08X} x{len(members)}: mat=b{mat} pbr=b{pbr} '
                  f'common=b{comm} temps={temps} maxS={smax} maxT={tmax} '
                  f'emReads={emhits} v2={v2}  PSes={members[:10]}{"..." if len(members) > 10 else ""}')
    return s


def emissive_window(s, psi, label=''):
    em = next((p for p in s.mat_params if p['id'] == 0x38A64362), None)
    if em is None:
        return
    elem = em['offset'] // 16
    sh = s.shaders[s.vs_count + psi]
    mat_slot = next((r['slot'] for r in sh['res'] if r['kind'] == 'cb'
                     and rstr(s, r['str_off']) == 'g_MaterialParameter'), None)
    blob = bytes(s.blob[sh['off']:sh['off'] + sh['sz']])
    shex = get_shex(blob)
    if shex is None or mat_slot is None:
        return
    insns = list(instructions(shex))
    hits = [k for k, (p_, op, ln, toks) in enumerate(insns)
            if any(cbi == mat_slot and cbe == elem for (cbi, cbe, ti) in cb_reads(toks))]
    print(f'\n  == {label} PS{psi}: g_EmissiveColor(cb{mat_slot}[{elem}]) hit instrs {hits}')
    for k0 in hits:
        for k in range(max(0, k0 - 2), min(len(insns), k0 + 3)):
            pos_, op, ln, toks = insns[k]
            mark = '>>>' if k == k0 else '   '
            print(f'  {mark} @{pos_:05X} {OPNAMES.get(op, f"op{op:02X}"):9} '
                  + ' '.join(f'{t:08X}' for t in toks[:12]))
        print('  ---')


if __name__ == '__main__':
    dump_ps = [int(x) for x in sys.argv[2:]] if len(sys.argv) > 2 and sys.argv[2].isdigit() else []
    for path in sys.argv[1:]:
        if path.isdigit():
            continue
        s = analyze_shpk(path)
        for psi in dump_ps:
            emissive_window(s, psi, path)
