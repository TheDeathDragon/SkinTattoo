Texture2D<float4> PositionMap : register(t0);
Texture2D<float4> NormalMap : register(t1);
Texture2D<float4> DecalTexture : register(t2);
SamplerState DecalSampler : register(s0);

RWTexture2D<float4> DecalBuffer : register(u0);

cbuffer ProjectionParams : register(b0)
{
    float4x4 ViewProjection;
    float3 ProjectionDir;
    float BackfaceThreshold;
    float GrazingFade;
    float Opacity;
    float2 _Padding;
};

[numthreads(8, 8, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    uint width, height;
    PositionMap.GetDimensions(width, height);

    if (DTid.x >= width || DTid.y >= height)
        return;

    float4 posData = PositionMap[DTid.xy];
    if (posData.w < 0.5f)
    {
        DecalBuffer[DTid.xy] = float4(0, 0, 0, 0);
        return;
    }

    float3 worldPos = posData.xyz;
    float3 worldNormal = normalize(NormalMap[DTid.xy].xyz);

    float nDotP = dot(worldNormal, -ProjectionDir);
    if (nDotP < BackfaceThreshold)
    {
        DecalBuffer[DTid.xy] = float4(0, 0, 0, 0);
        return;
    }

    float4 projPos = mul(float4(worldPos, 1.0f), ViewProjection);
    float3 decalUV = float3(projPos.xy * 0.5f + 0.5f, projPos.z);
    if (any(decalUV < 0.0f) || any(decalUV > 1.0f))
    {
        DecalBuffer[DTid.xy] = float4(0, 0, 0, 0);
        return;
    }

    float4 decalColor = DecalTexture.SampleLevel(DecalSampler, decalUV.xy, 0);

    float fade = saturate((nDotP - BackfaceThreshold) / max(GrazingFade - BackfaceThreshold, 0.001f));
    decalColor.a *= fade * Opacity;

    DecalBuffer[DTid.xy] = decalColor;
}
