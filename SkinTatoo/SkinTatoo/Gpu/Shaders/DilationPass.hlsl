Texture2D<float4> InputTexture : register(t0);
Texture2D<float4> ValidityMask : register(t1);

RWTexture2D<float4> OutputTexture : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 DTid : SV_DispatchThreadID)
{
    uint width, height;
    InputTexture.GetDimensions(width, height);

    if (DTid.x >= width || DTid.y >= height)
        return;

    float4 current = InputTexture[DTid.xy];
    float validity = ValidityMask[DTid.xy].w;

    if (validity > 0.5f || current.a > 0.001f)
    {
        OutputTexture[DTid.xy] = current;
        return;
    }

    float4 best = float4(0, 0, 0, 0);
    float bestDist = 99999.0f;

    int2 offsets[8] = {
        int2(-1, -1), int2(0, -1), int2(1, -1),
        int2(-1,  0),              int2(1,  0),
        int2(-1,  1), int2(0,  1), int2(1,  1),
    };

    for (int i = 0; i < 8; i++)
    {
        int2 neighbor = int2(DTid.xy) + offsets[i];
        if (neighbor.x < 0 || neighbor.y < 0 ||
            (uint)neighbor.x >= width || (uint)neighbor.y >= height)
            continue;

        float4 nVal = InputTexture[neighbor];
        float nValidity = ValidityMask[neighbor].w;

        if (nValidity > 0.5f || nVal.a > 0.001f)
        {
            float dist = length(float2(offsets[i]));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = nVal;
            }
        }
    }

    OutputTexture[DTid.xy] = best;
}
