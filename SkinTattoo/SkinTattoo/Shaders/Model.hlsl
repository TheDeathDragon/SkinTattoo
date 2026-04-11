cbuffer VSConstants : register(b0)
{
    float4x4 WorldMatrix;
    float4x4 ViewProjectionMatrix;
};

cbuffer PSConstants : register(b0)
{
    float3 LightDir;
    float _pad0;
    float3 CameraPos;
    float _pad1;
    int HasTexture;
    float3 _pad2;
};

Texture2D DiffuseTex : register(t0);
SamplerState Sampler : register(s0);

struct VS_IN
{
    float3 pos : POSITION;
    float3 norm : NORMAL;
    float2 uv : TEXCOORD;
};

struct PS_IN
{
    float4 pos : SV_POSITION;
    float3 worldPos : WORLDPOS;
    float3 norm : NORMAL;
    float2 uv : TEXCOORD;
};

PS_IN VS(VS_IN input)
{
    PS_IN output = (PS_IN)0;
    float4 worldPos = mul(float4(input.pos, 1.0f), WorldMatrix);
    output.worldPos = worldPos.xyz;
    output.pos = mul(worldPos, ViewProjectionMatrix);
    output.norm = normalize(mul(input.norm, (float3x3)WorldMatrix));
    output.uv = input.uv;
    return output;
}

float4 PS(PS_IN input) : SV_Target
{
    float3 norm = normalize(input.norm);
    float3 lightDir = normalize(LightDir);

    float3 ambient = float3(0.3f, 0.3f, 0.35f);
    float ndotl = saturate(dot(norm, -lightDir));
    float3 diffuse = float3(0.7f, 0.7f, 0.65f) * ndotl;

    float3 viewDir = normalize(CameraPos - input.worldPos);
    float3 halfVec = normalize(-lightDir + viewDir);
    float spec = pow(saturate(dot(norm, halfVec)), 32.0f);
    float3 specular = float3(0.2f, 0.2f, 0.2f) * spec;

    float3 baseColor;
    if (HasTexture)
    {
        baseColor = DiffuseTex.Sample(Sampler, input.uv).rgb;
    }
    else
    {
        baseColor = float3(0.75f, 0.7f, 0.65f);
    }

    float3 result = (ambient + diffuse) * baseColor + specular;
    return float4(result, 1.0f);
}
