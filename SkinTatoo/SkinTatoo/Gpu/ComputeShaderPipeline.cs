using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DXGI;

namespace SkinTatoo.Gpu;

[StructLayout(LayoutKind.Sequential)]
public struct ProjectionCB
{
    public Matrix4x4 ViewProjection;
    public Vector3 ProjectionDir;
    public float BackfaceThreshold;
    public float GrazingFade;
    public float Opacity;
    public Vector2 Padding;
}

[StructLayout(LayoutKind.Sequential)]
public struct CompositeCB
{
    public uint BlendMode;
    public uint IsNormalMap;
    public Vector2 Padding;
}

public unsafe class ComputeShaderPipeline : IDisposable
{
    private readonly DxManager dx;
    private readonly IPluginLog log;

    private ID3D11ComputeShader* projectionShader;
    private ID3D11ComputeShader* compositeShader;
    private ID3D11ComputeShader* dilationShader;

    private ID3D11Buffer* projectionCB;
    private ID3D11Buffer* compositeCB;

    private ID3D11SamplerState* linearSampler;

    private bool disposed;

    public bool IsInitialized => projectionShader != null;

    public ComputeShaderPipeline(DxManager dx, IPluginLog log)
    {
        this.dx = dx;
        this.log = log;
    }

    public void Initialize()
    {
        if (!dx.IsInitialized) return;

        projectionShader = dx.CreateComputeShaderFromHlsl(LoadShaderBytecode("ProjectionPass.hlsl"));
        compositeShader = dx.CreateComputeShaderFromHlsl(LoadShaderBytecode("CompositePass.hlsl"));
        dilationShader = dx.CreateComputeShaderFromHlsl(LoadShaderBytecode("DilationPass.hlsl"));

        projectionCB = dx.CreateConstantBuffer((uint)sizeof(ProjectionCB));
        compositeCB = dx.CreateConstantBuffer((uint)sizeof(CompositeCB));

        var samplerDesc = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            MaxAnisotropy = 1,
            MaxLOD = float.MaxValue,
        };

        ID3D11SamplerState* sampler = null;
        dx.Device->CreateSamplerState(&samplerDesc, &sampler);
        linearSampler = sampler;

        log.Information("Compute shader pipeline initialized.");
    }

    public void DispatchProjection(
        ID3D11ShaderResourceView* positionMapSRV,
        ID3D11ShaderResourceView* normalMapSRV,
        ID3D11ShaderResourceView* decalTextureSRV,
        ID3D11UnorderedAccessView* decalBufferUAV,
        ProjectionCB parameters,
        uint width, uint height)
    {
        var ctx = dx.Context;
        if (ctx == null || projectionShader == null) return;

        dx.UpdateConstantBuffer(projectionCB, parameters);

        ctx->CSSetShader(projectionShader, null, 0);

        var srv0 = positionMapSRV;
        var srv1 = normalMapSRV;
        var srv2 = decalTextureSRV;
        var srvArr = stackalloc ID3D11ShaderResourceView*[] { srv0, srv1, srv2 };
        ctx->CSSetShaderResources(0, 3, srvArr);

        var uav0 = decalBufferUAV;
        ctx->CSSetUnorderedAccessViews(0, 1, &uav0, null);
        var cb0 = projectionCB;
        ctx->CSSetConstantBuffers(0, 1, &cb0);
        var samp0 = linearSampler;
        ctx->CSSetSamplers(0, 1, &samp0);

        ctx->Dispatch((width + 7) / 8, (height + 7) / 8, 1);

        var nullSrvs = stackalloc ID3D11ShaderResourceView*[3] { null, null, null };
        ctx->CSSetShaderResources(0, 3, nullSrvs);
        ID3D11UnorderedAccessView* nullUav = null;
        ctx->CSSetUnorderedAccessViews(0, 1, &nullUav, null);
    }

    public void DispatchComposite(
        ID3D11ShaderResourceView* baseTextureSRV,
        ID3D11ShaderResourceView* decalBufferSRV,
        ID3D11UnorderedAccessView* outputUAV,
        CompositeCB parameters,
        uint width, uint height)
    {
        var ctx = dx.Context;
        if (ctx == null || compositeShader == null) return;

        dx.UpdateConstantBuffer(compositeCB, parameters);

        ctx->CSSetShader(compositeShader, null, 0);

        var csrv0 = baseTextureSRV;
        var csrv1 = decalBufferSRV;
        var csrvArr = stackalloc ID3D11ShaderResourceView*[] { csrv0, csrv1 };
        ctx->CSSetShaderResources(0, 2, csrvArr);

        var cuav0 = outputUAV;
        ctx->CSSetUnorderedAccessViews(0, 1, &cuav0, null);
        var ccb0 = compositeCB;
        ctx->CSSetConstantBuffers(0, 1, &ccb0);

        ctx->Dispatch((width + 7) / 8, (height + 7) / 8, 1);

        var nullSrvs = stackalloc ID3D11ShaderResourceView*[2] { null, null };
        ctx->CSSetShaderResources(0, 2, nullSrvs);
        ID3D11UnorderedAccessView* nullUav = null;
        ctx->CSSetUnorderedAccessViews(0, 1, &nullUav, null);
    }

    public void DispatchDilation(
        ID3D11ShaderResourceView* inputSRV,
        ID3D11ShaderResourceView* validityMaskSRV,
        ID3D11UnorderedAccessView* outputUAV,
        uint width, uint height)
    {
        var ctx = dx.Context;
        if (ctx == null || dilationShader == null) return;

        ctx->CSSetShader(dilationShader, null, 0);

        var dsrv0 = inputSRV;
        var dsrv1 = validityMaskSRV;
        var dsrvArr = stackalloc ID3D11ShaderResourceView*[] { dsrv0, dsrv1 };
        ctx->CSSetShaderResources(0, 2, dsrvArr);

        var duav0 = outputUAV;
        ctx->CSSetUnorderedAccessViews(0, 1, &duav0, null);

        ctx->Dispatch((width + 7) / 8, (height + 7) / 8, 1);

        var nullSrvs = stackalloc ID3D11ShaderResourceView*[2] { null, null };
        ctx->CSSetShaderResources(0, 2, nullSrvs);
        ID3D11UnorderedAccessView* nullUav = null;
        ctx->CSSetUnorderedAccessViews(0, 1, &nullUav, null);
    }

    private static byte[] LoadShaderBytecode(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"SkinTatoo.Gpu.Shaders.{name}";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new FileNotFoundException($"Embedded shader resource not found: {resourceName}");

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (projectionShader != null) projectionShader->Release();
        if (compositeShader != null) compositeShader->Release();
        if (dilationShader != null) dilationShader->Release();
        if (projectionCB != null) projectionCB->Release();
        if (compositeCB != null) compositeCB->Release();
        if (linearSampler != null) linearSampler->Release();
    }
}
