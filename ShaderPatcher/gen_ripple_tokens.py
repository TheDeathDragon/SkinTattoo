"""Compile HLSL with pulse+flicker+gradient+ripple 4-way branch, extract DXBC tokens."""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gen_pulse_tokens import compile_hlsl, d3d_disassemble
from dxbc_patcher import parse_dxbc, parse_shex

HLSL = r"""
cbuffer g_PbrParameterCommon : register(b2) {
    float LoopTime;
    float3 _pad0;
    float4 _reserve[4];
};

Texture2D ColorTable : register(t10);
SamplerState samTable : register(s5);

struct VIn { float4 pos : SV_Position; float3 col : COLOR; float2 uv : TEXCOORD0; float2 uvB : TEXCOORD1; };

float4 main(VIn v) : SV_Target {
    float2 uvEm = float2(0.3125, v.uv.y * 0.9375 + 0.015625);
    float4 em = ColorTable.Sample(samTable, uvEm);
    float2 uvAn = float2(0.4375, v.uv.y * 0.9375 + 0.015625);
    float4 an = ColorTable.Sample(samTable, uvAn);
    float2 uvRp = float2(0.6875, v.uv.y * 0.9375 + 0.015625);
    float4 rp = ColorTable.Sample(samTable, uvRp);

    float speed = an.x;
    float amp = an.y;
    float mode = an.z;
    float2 center = rp.xy;
    float freq = rp.z;

    float2 d = v.uvB - center;          // use uvB as spatial coord
    float dist = sqrt(dot(d, d));
    float phase = 2*3.141593 * speed * LoopTime - freq * dist;
    float s = sin(phase);

    float k = 1.0 + amp * s;
    float3 final = em.xyz * k;
    return float4(final, 1.0);
}
"""

dxbc = compile_hlsl(HLSL)
print(f"Compiled: {len(dxbc)} bytes\n")
dis = d3d_disassemble(dxbc)
print(dis)
print("=" * 60)

container = parse_dxbc(dxbc)
shex = container.get_chunk(b'SHEX')
_, _, _, instructions = parse_shex(shex)
print(f"\nSHEX instructions ({len(instructions)} total):")
for i, inst in enumerate(instructions):
    hex_tokens = ' '.join(f'{t:08X}' for t in inst.tokens)
    print(f"  [{i}] op=0x{inst.opcode:02X} len={inst.length:2d} {inst.name:20s} | {hex_tokens}")
