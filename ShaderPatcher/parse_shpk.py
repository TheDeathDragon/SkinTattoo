"""Parse FFXIV .shpk based on Penumbra's ShpkFile.cs (version 0x0D01+).
Focus: extract material-level params, constants, samplers, keys for comparison."""
import struct, sys, os

CRC_NAMES = {
    0x380CAED0: "CategorySkinType", 0x72E697CD: "ValEmissive",
    0x2BDB45F1: "ValBody", 0xF5673524: "ValFace", 0x57FF3B64: "ValBodyJJM",
    0x38A64362: "g_EmissiveColor", 0x2C2A34DD: "g_DiffuseColor",
    0x59BDA0B1: "g_ShaderID", 0xCB0338DC: "g_SpecularColorMask",
    0xB5545FBB: "g_NormalScale", 0x074953E9: "g_SphereMapIndex",
    0xB7FA33E2: "g_SSAOMask", 0x4255F2F4: "g_TileIndex",
    0x2E60B071: "g_TileScale", 0x575ABFB2: "g_AmbientOcclusionMask",
    0xB616DC5A: "TextureMode", 0xF52CCF05: "VertexColorMode",
    0xD2777173: "DecalMode", 0x9D4A3204: "ALumKey?",
    0xB500BB24: "g_ScatteringLevel", 0x64D12851: "g_MaterialParameter",
    0x2005679F: "g_SamplerTable", 0x0C5EC1F1: "g_SamplerNormal",
    0x565F8FD8: "g_SamplerIndex", 0x2B99E025: "g_SamplerSpecular",
    0x115306BE: "g_SamplerDiffuse", 0x8A4E82B6: "g_SamplerMask",
    0x87F6474D: "g_SamplerCatchlight", 0x24826489: "SubColorMap",
    0xD62BF368: "g_AlphaAperture", 0xD07A6A65: "g_AlphaOffset",
    0x39551220: "g_TextureMipBias", 0x8870C938: "g_OutlineWidth",
    0x623CC4FE: "g_OutlineColor", 0xDF15112D: "g_ToonIndex",
    0x800EE35F: "g_SheenRate", 0x1F264897: "g_SheenTintRate",
    0xF490F76E: "g_SheenAperture", 0x3632401A: "g_LipRoughnessScale",
    0x12C6AC9F: "g_TileAlpha", 0x6421DD30: "g_TileMipBiasOffset",
    0x3CCE9E4C: "g_ToonLightScale", 0x759036EE: "g_ToonLightSpecAperture",
    0xD96FAF7A: "g_ToonReflectionScale", 0x5351646E: "g_ShadowPosOffset",
    0x7801E004: "g_GlassIOR", 0xC4647F37: "g_GlassThicknessMax",
}

def cn(crc): return CRC_NAMES.get(crc, f"0x{crc:08X}")

def parse_shpk(filepath):
    with open(filepath, "rb") as f:
        data = f.read()

    pos = 0
    def u16():
        nonlocal pos; v = struct.unpack_from("<H", data, pos)[0]; pos += 2; return v
    def u32():
        nonlocal pos; v = struct.unpack_from("<I", data, pos)[0]; pos += 4; return v

    magic = u32(); version = u32(); dx = u32(); file_size = u32()
    blobs_off = u32(); strings_off = u32()
    vs_count = u32(); ps_count = u32()
    mat_params_size = u32()
    mat_param_count = u16(); has_defaults = u16() != 0
    const_count = u32()
    samp_count = u16(); tex_count = u16()
    uav_count = u32()
    sys_key_count = u32(); scene_key_count = u32(); mat_key_count = u32()
    node_count = u32(); alias_count = u32()
    if version >= 0x0D01:
        pos += 12  # 3 unknown u32

    strings = data[strings_off:]
    name = os.path.basename(filepath)

    print(f"\n{'='*60}")
    print(f"  {name} ({len(data)} bytes)")
    print(f"{'='*60}")
    print(f"VS: {vs_count}  PS: {ps_count}  MatParamsSize: {mat_params_size}")
    print(f"MatParams: {mat_param_count}  Defaults: {has_defaults}")
    print(f"Constants: {const_count}  Samplers: {samp_count}  Textures: {tex_count}")
    print(f"SysKeys: {sys_key_count}  SceneKeys: {scene_key_count}  MatKeys: {mat_key_count}")

    # Skip all shader entries
    total_shaders = vs_count + ps_count
    for i in range(total_shaders):
        if pos + 20 > blobs_off:
            print(f"  WARNING: shader entries overflow at {i}/{total_shaders}")
            break
        _blob_off = u32(); _blob_sz = u32()
        c_cnt = u16(); s_cnt = u16(); uav_cnt = u16(); t_cnt = u16()
        if version >= 0x0D01:
            pos += 4  # unk131
        # Each Resource = 16 bytes (u32 id, u32 strOff, u16 strSz, u16 isTex, u16 slot, u16 size)
        resource_bytes = (c_cnt + s_cnt + uav_cnt + t_cnt) * 16
        if pos + resource_bytes > len(data):
            print(f"  WARNING: shader {i} resources overflow ({resource_bytes} bytes)")
            break
        pos += resource_bytes

    print(f"  After shaders: pos=0x{pos:X} (blobs_off=0x{blobs_off:X})")

    # MaterialParam: { u32 Id, u16 ByteOffset, u16 ByteSize } = 8 bytes
    print(f"\n--- Material Params ({mat_param_count}) ---")
    mat_params = []
    for _ in range(mat_param_count):
        pid = u32(); poff = u16(); psz = u16()
        mat_params.append((pid, poff, psz))
        print(f"  {cn(pid):32s}  off={poff:3d}  sz={psz:2d}")

    # MaterialParamsDefaults
    if has_defaults:
        defaults = data[pos:pos + mat_params_size]
        pos += mat_params_size
        print(f"\n--- Defaults ({mat_params_size} bytes) ---")
        for pid, poff, psz in mat_params:
            if poff + psz <= len(defaults):
                floats = [struct.unpack_from("<f", defaults, poff + j*4)[0] for j in range(psz // 4)]
                fstr = ", ".join(f"{v:.3f}" for v in floats)
                print(f"  {cn(pid):32s}: [{fstr}]")

    # Read Resource: 16 bytes (u32 id, u32 strOff, u16 strSz, u16 isTex, u16 slot, u16 size)
    def read_res():
        rid = u32(); soff = u32(); ssz = u16(); is_tex = u16(); slot = u16(); sz = u16()
        nm = ""
        if soff < len(strings):
            end = strings.find(b'\0', soff)
            nm = strings[soff:end].decode('ascii', errors='replace') if end >= 0 else ""
        return rid, nm, slot, sz

    print(f"\n--- Constants ({const_count}) ---")
    for _ in range(const_count):
        rid, nm, slot, sz = read_res()
        print(f"  {cn(rid):32s}  slot={slot}  sz={sz}  name=\"{nm}\"")

    print(f"\n--- Samplers ({samp_count}) ---")
    for _ in range(samp_count):
        rid, nm, slot, sz = read_res()
        print(f"  {cn(rid):32s}  slot={slot}  sz={sz}  name=\"{nm}\"")

    print(f"\n--- Textures ({tex_count}) ---")
    for _ in range(tex_count):
        rid, nm, slot, sz = read_res()
        print(f"  {cn(rid):32s}  slot={slot}  sz={sz}  name=\"{nm}\"")

    # UAVs
    for _ in range(uav_count):
        read_res()

    # Keys: each is { u32 Id, u32 DefaultValue } = 8 bytes on disk
    print(f"\n--- System Keys ({sys_key_count}) ---")
    for _ in range(sys_key_count):
        kid = u32(); kdef = u32()
        print(f"  {cn(kid):32s}  default={cn(kdef)}")

    print(f"\n--- Scene Keys ({scene_key_count}) ---")
    for _ in range(scene_key_count):
        kid = u32(); kdef = u32()
        print(f"  {cn(kid):32s}  default={cn(kdef)}")

    print(f"\n--- Material Keys ({mat_key_count}) ---")
    for _ in range(mat_key_count):
        kid = u32(); kdef = u32()
        print(f"  {cn(kid):32s}  default={cn(kdef)}")

if __name__ == "__main__":
    script_dir = os.path.dirname(os.path.abspath(__file__))
    base = os.path.join(script_dir, "..", "..")
    for shpk in ["skin.shpk", "character.shpk"]:
        path = os.path.join(base, shpk)
        if os.path.exists(path):
            try:
                parse_shpk(path)
            except Exception as e:
                print(f"\nERROR: {e}")
                import traceback; traceback.print_exc()
