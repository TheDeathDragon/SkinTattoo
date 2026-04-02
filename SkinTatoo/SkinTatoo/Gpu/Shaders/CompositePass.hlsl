Texture2D<float4> BaseTexture : register(t0);
Texture2D<float4> DecalBuffer : register(t1);

RWTexture2D<float4> OutputTexture : register(u0);

cbuffer CompositeParams : register(b0)
{
    uint BlendModeValue;
    uint IsNormalMap;
    float2 _Padding;
};

float3 OverlayBlend(float3 base, float3 blend)
{
    float3 result;
    result.r = base.r < 0.5f ? 2.0f * base.r * blend.r : 1.0f - 2.0f * (1.0f - base.r) * (1.0f - blend.r);
    result.g = base.g < 0.5f ? 2.0f * base.g * blend.g : 1.0f - 2.0f * (1.0f - base.g) * (1.0f - blend.g);
    result.b = base.b < 0.5f ? 2.0f * base.b * blend.b : 1.0f - 2.0f * (1.0f - base.b) * (1.0f - blend.b);
    return result;
}

float3 SoftLightBlend(float3 base, float3 blend)
{
    return (1.0f - 2.0f * blend) * base * base + 2.0f * blend * base;
}

float3 ReorientedNormalBlend(float2 baseRG, float2 decalRG)
{
    float3 baseN = float3(baseRG * 2.0f - 1.0f, 0);
    baseN.z = sqrt(max(0, 1.0f - dot(baseN.xy, baseN.xy)));

    float3 decalN = float3(decalRG * 2.0f - 1.0f, 0);
    decalN.z = sqrt(max(0, 1.0f - dot(decalN.xy, decalN.xy)));

    float3 t = baseN * float3(2, 2, 2) + float3(-1, -1, 0);
    float3 u = decalN * float3(-2, -2, 2) + float3(1, 1, -1);
    float3 r = t * dot(t, u) / t.z - u;

    return float3(r.xy * 0.5f + 0.5f, 0);
}

[numthreads(8, 8, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    uint width, height;
    BaseTexture.GetDimensions(width, height);

    if (DTid.x >= width || DTid.y >= height)
        return;

    float4 base = BaseTexture[DTid.xy];
    float4 decal = DecalBuffer[DTid.xy];

    if (decal.a < 0.001f)
    {
        OutputTexture[DTid.xy] = base;
        return;
    }

    float4 result = base;

    if (IsNormalMap == 1)
    {
        float3 blended = ReorientedNormalBlend(base.rg, decal.rg);
        result.rg = lerp(base.rg, blended.rg, decal.a);
    }
    else
    {
        float3 blended;
        if (BlendModeValue == 0)
            blended = decal.rgb;
        else if (BlendModeValue == 1)
            blended = base.rgb * decal.rgb;
        else if (BlendModeValue == 2)
            blended = OverlayBlend(base.rgb, decal.rgb);
        else
            blended = SoftLightBlend(base.rgb, decal.rgb);

        result.rgb = blended * decal.a + base.rgb * (1.0f - decal.a);
        result.a = decal.a + base.a * (1.0f - decal.a);
    }

    OutputTexture[DTid.xy] = result;
}
