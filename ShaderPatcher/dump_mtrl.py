"""Raw .mtrl parser, bypassing Lumina. Dumps shader keys + constants + samplers.

FFXIV .mtrl layout (post-Dawntrail):
    u32 version           (0x1030000)
    u16 fileSize
    u16 dataSetSize       (ColorTable size, 0 or 2048)
    u16 stringTableSize
    u16 shaderPackageNameOffset  (offset inside string table)
    u8  textureCount
    u8  uvSetCount
    u8  colorSetCount
    u8  additionalDataSize
    TextureEntry[textureCount] : u16 stringOff, u16 flags
    UvSetEntry[uvSetCount]     : u16 stringOff, u16 flags
    ColorSetEntry[colorSetCount] : u16 stringOff, u16 flags
    stringTable[stringTableSize]
    additionalData[additionalDataSize]
    colorTableData[dataSetSize]
    u16 shaderValuesListSize (bytes, = floatCount * 4)
    u16 shaderKeyCount
    u16 constantCount
    u16 samplerCount
    u16 flags
    u16 reserved
    ShaderKey[keyCount]   : u32 category, u32 value
    Constant[constCount]  : u32 id, u16 valueOffset, u16 valueSize
    Sampler[sampCount]    : u32 samplerId, u32 (flags|textureIndex)
    ShaderValues[shaderValuesListSize / 4] floats
"""
import struct, sys

CRC_NAMES = {
    0x380CAED0: "CategorySkinType", 0x72E697CD: "ValEmissive",
    0x2BDB45F1: "ValBody", 0xF5673524: "ValFace", 0x57FF3B64: "ValBodyJJM",
    0xD2777173: "CategoryDecalMode", 0x4242B842: "ValDecalOff",
    0x584265DD: "ValDecalEmissive",
    0xF52CCF05: "CategoryVertexColorMode", 0xDFE74BAC: "ValVertexColorOff",
    0xA7D2FF60: "ValVertexColorEmissive",
    0x38A64362: "g_EmissiveColor", 0x2C2A34DD: "g_DiffuseColor",
    0x59BDA0B1: "g_ShaderID", 0xCB0338DC: "g_SpecularColorMask",
    0xB5545FBB: "g_NormalScale", 0x074953E9: "g_SphereMapIndex",
    0xB7FA33E2: "g_SSAOMask", 0x4255F2F4: "g_TileIndex",
    0x2E60B071: "g_TileScale", 0x575ABFB2: "g_AmbientOcclusionMask",
    0xB500BB24: "g_ScatteringLevel", 0x64D12851: "g_MaterialParameter",
    0x2005679F: "g_SamplerTable", 0x0C5EC1F1: "g_SamplerNormal",
    0x565F8FD8: "g_SamplerIndex", 0x2B99E025: "g_SamplerSpecular",
    0x115306BE: "g_SamplerDiffuse", 0x8A4E82B6: "g_SamplerMask",
    0x87F6474D: "g_SamplerCatchlight",
    0xD62BF368: "g_AlphaAperture", 0xD07A6A65: "g_AlphaOffset",
    0x39551220: "g_TextureMipBias", 0x3632401A: "g_LipRoughnessScale",
    0x12C6AC9F: "g_TileAlpha", 0x6421DD30: "g_TileMipBiasOffset",
    0x5351646E: "g_ShadowPosOffset", 0x800EE35F: "g_SheenRate",
    0x1F264897: "g_SheenTintRate", 0xF490F76E: "g_SheenAperture",
    0x29AC0223: "_unk_0x29AC0223", 0xD925FF32: "_unk_0xD925FF32",
}

def name(crc):
    return CRC_NAMES.get(crc, f"0x{crc:08X}")

def parse_mtrl(path):
    with open(path, "rb") as f:
        data = f.read()

    pos = 0
    def u8():
        nonlocal pos; v = data[pos]; pos += 1; return v
    def u16():
        nonlocal pos; v = struct.unpack_from("<H", data, pos)[0]; pos += 2; return v
    def u32():
        nonlocal pos; v = struct.unpack_from("<I", data, pos)[0]; pos += 4; return v

    version = u32()
    file_size = u16()
    data_set_size = u16()
    string_table_size = u16()
    shpk_name_off = u16()
    texture_count = u8()
    uv_set_count = u8()
    color_set_count = u8()
    additional_data_size = u8()

    print(f"File: {path}")
    print(f"  version=0x{version:08X}  size={file_size}  dataSet={data_set_size}")
    print(f"  stringTable={string_table_size}  shpkOff={shpk_name_off}")
    print(f"  textures={texture_count}  uvSets={uv_set_count}  colorSets={color_set_count}  addlData={additional_data_size}")

    # Skip texture/uv/color entries (4 bytes each)
    pos += (texture_count + uv_set_count + color_set_count) * 4

    strings_start = pos
    string_table = data[pos:pos + string_table_size]
    pos += string_table_size

    shpk_end = string_table.find(b'\x00', shpk_name_off)
    shpk = string_table[shpk_name_off:shpk_end].decode('ascii', errors='replace')
    print(f"  shaderPackage=\"{shpk}\"")

    additional_data = data[pos:pos + additional_data_size]
    pos += additional_data_size
    print(f"  addlData[0..4]={additional_data[:min(4, len(additional_data))].hex()}  (flags encode HasColorTable, dims)")

    color_table_data = data[pos:pos + data_set_size]
    pos += data_set_size

    # Material data section
    shader_values_size = u16()
    shader_key_count = u16()
    constant_count = u16()
    sampler_count = u16()
    flags = u16()
    reserved = u16()

    print(f"  shaderValuesSize={shader_values_size}  keyCount={shader_key_count}  constCount={constant_count}  sampCount={sampler_count}  flags=0x{flags:04X}")

    print(f"\n  -- ShaderKeys ({shader_key_count}) --")
    for i in range(shader_key_count):
        cat = u32(); val = u32()
        print(f"    [{i}] {name(cat)} = {name(val)}")

    print(f"\n  -- Constants ({constant_count}) --")
    const_defs = []
    for i in range(constant_count):
        cid = u32()
        voff = u16(); vsize = u16()
        const_defs.append((cid, voff, vsize))

    # Sampler entries: 12 bytes each (SamplerId u32 + Flags u32 + TextureIndex u8 + 3 bytes padding).
    print(f"\n  -- Samplers ({sampler_count}) --")
    for i in range(sampler_count):
        sid = u32(); flg = u32()
        tex_idx = u8()
        pos += 3  # skip 3 padding bytes
        print(f"    [{i}] {name(sid)}  texIdx={tex_idx}  flags=0x{flg:08X}")

    # Shader values are floats
    values_start = pos
    float_count = shader_values_size // 4
    floats = struct.unpack_from(f"<{float_count}f", data, pos)
    pos += shader_values_size

    print(f"\n  -- Constants (values) --")
    for cid, voff, vsize in const_defs:
        fi = voff // 4
        fc = vsize // 4
        vals = floats[fi:fi + fc]
        print(f"    {name(cid):30s} off={voff:3d} sz={vsize:3d}  = {vals}")

    print(f"\n  trailing bytes: {len(data) - pos}")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: dump_mtrl.py <mtrl_path>")
        sys.exit(1)
    parse_mtrl(sys.argv[1])
