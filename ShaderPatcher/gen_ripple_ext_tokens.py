"""Ripple v2: direction mode (radial / linear / bidir) + dual-color.

CT col 5 halfs: centerU, centerV, freq, dirMode
CT col 6 halfs: dirX, dirY, dualFlag, (unused)

dirMode: 0 = radial (use length(uv-center)), 1 = linear (dot(d, dir)), 2 = bidir (abs(dot))
dualFlag: >= 0.5 means blend colorB into peaks
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gen_pulse_tokens import compile_hlsl, d3d_disassemble
from dxbc_patcher import parse_dxbc, parse_shex

HLSL = r"""
cbuffer g_PbrParameterCommon : register(b2) { float LoopTime; float3 _pad0; float4 _reserve[4]; };
Texture2D ColorTable : register(t10);
SamplerState samTable : register(s5);

struct VIn { float4 pos : SV_Position; float3 col : COLOR; float2 uv : TEXCOORD0; float2 worldUv : TEXCOORD1; };

float4 main(VIn v) : SV_Target {
    float rowV = v.uv.y * 0.9375 + 0.015625;
    float4 em = ColorTable.Sample(samTable, float2(0.3125, rowV));
    float4 an = ColorTable.Sample(samTable, float2(0.4375, rowV));
    float4 cb = ColorTable.Sample(samTable, float2(0.5625, rowV));
    float4 rp = ColorTable.Sample(samTable, float2(0.6875, rowV));
    float4 rp2 = ColorTable.Sample(samTable, float2(0.8125, rowV));

    float speed = an.x;
    float amp = an.y;
    float mode = an.z;
    float3 colorB = cb.yzw;
    float2 center = rp.xy;
    float freq = rp.z;
    float dirMode = rp.w;
    float2 dir = rp2.xy;
    float dualFlag = rp2.z;

    float2 d = v.worldUv - center;
    float distRadial = sqrt(dot(d, d));
    float proj = dot(d, dir);
    float distLinear = proj;
    float distBidir = abs(proj);

    float dist = distRadial;
    if (dirMode >= 1.5) dist = distBidir;
    else if (dirMode >= 0.5) dist = distLinear;

    float phase = 2 * 3.141593 * speed * LoopTime - freq * dist;
    float s = sin(phase);

    float3 colA = em.xyz;
    float k_mono = 1.0 + amp * s;
    float3 monoResult = colA * k_mono;

    float mix = 0.5 + 0.5 * s;
    float3 dualResult = lerp(colA, colorB, mix);

    float3 rippleResult = (dualFlag >= 0.5) ? dualResult : monoResult;

    // (simplified: pretend we only route ripple here; real shader has full mode branching)
    return float4(rippleResult, 1.0);
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
