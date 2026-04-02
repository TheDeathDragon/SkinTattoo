using System;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DXGI;

namespace SkinTatoo.Gpu;

public unsafe class DxManager : IDisposable
{
    private readonly IPluginLog log;
    private ID3D11Device* device;
    private ID3D11DeviceContext* context;
    private bool disposed;

    public ID3D11Device* Device => device;
    public ID3D11DeviceContext* Context => context;
    public bool IsInitialized => device != null;

    public DxManager(IPluginLog log)
    {
        this.log = log;
    }

    public void InitializeStandaloneDevice()
    {
        if (device != null) return;

        fixed (ID3D11Device** ppDevice = &device)
        fixed (ID3D11DeviceContext** ppContext = &context)
        {
            var featureLevel = D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0;
            D3D_FEATURE_LEVEL actualLevel;
            var hr = DirectX.D3D11CreateDevice(
                null,
                D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                HMODULE.NULL,
                0,
                &featureLevel,
                1,
                D3D11_SDK_VERSION,
                ppDevice,
                &actualLevel,
                ppContext);

            if (hr < 0)
            {
                log.Error("Failed to create standalone D3D11 device: 0x{0:X8}", hr);
                return;
            }
        }

        log.Information("Standalone DX11 device created successfully.");
    }

    public ID3D11ComputeShader* CreateComputeShader(byte[] bytecode)
    {
        if (device == null) return null;

        ID3D11ComputeShader* shader = null;
        fixed (byte* pBytecode = bytecode)
        {
            var hr = device->CreateComputeShader(
                pBytecode, (nuint)bytecode.Length, null, &shader);
            if (hr < 0)
            {
                log.Error("Failed to create compute shader: 0x{0:X8}", hr);
                return null;
            }
        }
        return shader;
    }

    public ID3D11Texture2D* CreateTexture2D(
        uint width, uint height, DXGI_FORMAT format,
        uint bindFlags, D3D11_USAGE usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
        uint cpuAccessFlags = 0)
    {
        if (device == null) return null;

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = usage,
            BindFlags = bindFlags,
            CPUAccessFlags = cpuAccessFlags,
            MiscFlags = 0,
        };

        ID3D11Texture2D* texture = null;
        var hr = device->CreateTexture2D(&desc, null, &texture);
        if (hr < 0)
        {
            log.Error("Failed to create texture2D ({0}x{1}): 0x{2:X8}", width, height, hr);
            return null;
        }
        return texture;
    }

    public ID3D11ShaderResourceView* CreateSRV(ID3D11Texture2D* texture, DXGI_FORMAT format)
    {
        if (device == null) return null;

        var desc = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = format,
            ViewDimension = D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D,
        };
        desc.Anonymous.Texture2D.MipLevels = 1;
        desc.Anonymous.Texture2D.MostDetailedMip = 0;

        ID3D11ShaderResourceView* srv = null;
        var hr = device->CreateShaderResourceView((ID3D11Resource*)texture, &desc, &srv);
        if (hr < 0)
        {
            log.Error("Failed to create SRV: 0x{0:X8}", hr);
            return null;
        }
        return srv;
    }

    public ID3D11UnorderedAccessView* CreateUAV(ID3D11Texture2D* texture, DXGI_FORMAT format)
    {
        if (device == null) return null;

        var desc = new D3D11_UNORDERED_ACCESS_VIEW_DESC
        {
            Format = format,
            ViewDimension = D3D11_UAV_DIMENSION.D3D11_UAV_DIMENSION_TEXTURE2D,
        };
        desc.Anonymous.Texture2D.MipSlice = 0;

        ID3D11UnorderedAccessView* uav = null;
        var hr = device->CreateUnorderedAccessView((ID3D11Resource*)texture, &desc, &uav);
        if (hr < 0)
        {
            log.Error("Failed to create UAV: 0x{0:X8}", hr);
            return null;
        }
        return uav;
    }

    public ID3D11Buffer* CreateConstantBuffer(uint size)
    {
        if (device == null) return null;

        size = (size + 15u) & ~15u;

        var desc = new D3D11_BUFFER_DESC
        {
            ByteWidth = size,
            Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
        };

        ID3D11Buffer* buffer = null;
        var hr = device->CreateBuffer(&desc, null, &buffer);
        if (hr < 0)
        {
            log.Error("Failed to create constant buffer: 0x{0:X8}", hr);
            return null;
        }
        return buffer;
    }

    public void UpdateConstantBuffer<T>(ID3D11Buffer* buffer, T data) where T : unmanaged
    {
        if (context == null) return;

        var mapped = new D3D11_MAPPED_SUBRESOURCE();
        var hr = context->Map((ID3D11Resource*)buffer, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped);
        if (hr < 0) return;

        *(T*)mapped.pData = data;
        context->Unmap((ID3D11Resource*)buffer, 0);
    }

    public void UploadTextureData(ID3D11Texture2D* texture, float[] data, uint width, uint height)
    {
        if (context == null) return;

        var staging = CreateTexture2D(width, height,
            DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
            0, D3D11_USAGE.D3D11_USAGE_STAGING,
            (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE);
        if (staging == null) return;

        var mapped = new D3D11_MAPPED_SUBRESOURCE();
        var hr = context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_WRITE, 0, &mapped);
        if (hr >= 0)
        {
            var rowSize = width * 4 * sizeof(float);
            fixed (float* src = data)
            {
                for (var y = 0u; y < height; y++)
                {
                    var dstRow = (byte*)mapped.pData + y * mapped.RowPitch;
                    var srcRow = (byte*)src + y * rowSize;
                    Buffer.MemoryCopy(srcRow, dstRow, mapped.RowPitch, rowSize);
                }
            }
            context->Unmap((ID3D11Resource*)staging, 0);
            context->CopyResource((ID3D11Resource*)texture, (ID3D11Resource*)staging);
        }

        staging->Release();
    }

    public ID3D11ComputeShader* CreateComputeShaderFromHlsl(byte[] hlslSource, string entryPoint = "CSMain")
    {
        if (device == null) return null;

        ID3DBlob* shaderBlob = null;
        ID3DBlob* errorBlob = null;

        fixed (byte* pSource = hlslSource)
        {
            var pEntry = (sbyte*)System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(entryPoint);
            var pTarget = (sbyte*)System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi("cs_5_0");
            try
            {
                var hr = DirectX.D3DCompile(
                    pSource,
                    (nuint)hlslSource.Length,
                    null,
                    null,
                    null,
                    pEntry,
                    pTarget,
                    0,
                    0,
                    &shaderBlob,
                    &errorBlob);

                if (hr < 0)
                {
                    if (errorBlob != null)
                    {
                        var errorMsg = new string((sbyte*)errorBlob->GetBufferPointer(), 0, (int)errorBlob->GetBufferSize());
                        log.Error("Shader compilation error: {0}", errorMsg);
                        errorBlob->Release();
                    }
                    return null;
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal((nint)pEntry);
                System.Runtime.InteropServices.Marshal.FreeHGlobal((nint)pTarget);
            }
        }

        if (errorBlob != null) errorBlob->Release();

        ID3D11ComputeShader* shader = null;
        var createHr = device->CreateComputeShader(
            shaderBlob->GetBufferPointer(),
            shaderBlob->GetBufferSize(),
            null,
            &shader);

        shaderBlob->Release();

        if (createHr < 0)
        {
            log.Error("Failed to create compute shader: 0x{0:X8}", createHr);
            return null;
        }

        return shader;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (context != null) { context->Release(); context = null; }
        if (device != null) { device->Release(); device = null; }
    }
}
