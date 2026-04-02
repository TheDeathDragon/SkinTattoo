using System;
using Dalamud.Plugin.Services;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.DXGI;

namespace SkinTatoo.Gpu;

public unsafe class StagingReadback : IDisposable
{
    private readonly DxManager dx;
    private readonly IPluginLog log;
    private ID3D11Texture2D* stagingTexture;
    private uint currentWidth;
    private uint currentHeight;
    private bool disposed;

    public StagingReadback(DxManager dx, IPluginLog log)
    {
        this.dx = dx;
        this.log = log;
    }

    public byte[]? Readback(ID3D11Texture2D* sourceTexture, uint width, uint height)
    {
        if (!dx.IsInitialized) return null;

        EnsureStagingTexture(width, height);
        if (stagingTexture == null) return null;

        dx.Context->CopyResource((ID3D11Resource*)stagingTexture, (ID3D11Resource*)sourceTexture);

        var mapped = new D3D11_MAPPED_SUBRESOURCE();
        var hr = dx.Context->Map((ID3D11Resource*)stagingTexture, 0,
            D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
        if (hr < 0)
        {
            log.Error("Failed to map staging texture: 0x{0:X8}", hr);
            return null;
        }

        var rowSize = width * 4 * sizeof(float);
        var data = new byte[height * rowSize];

        fixed (byte* dst = data)
        {
            for (var y = 0u; y < height; y++)
            {
                var srcRow = (byte*)mapped.pData + y * mapped.RowPitch;
                var dstRow = dst + y * rowSize;
                Buffer.MemoryCopy(srcRow, dstRow, rowSize, rowSize);
            }
        }

        dx.Context->Unmap((ID3D11Resource*)stagingTexture, 0);
        return data;
    }

    private void EnsureStagingTexture(uint width, uint height)
    {
        if (stagingTexture != null && currentWidth == width && currentHeight == height)
            return;

        if (stagingTexture != null)
        {
            stagingTexture->Release();
            stagingTexture = null;
        }

        stagingTexture = dx.CreateTexture2D(width, height,
            DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
            0, D3D11_USAGE.D3D11_USAGE_STAGING,
            (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ);

        currentWidth = width;
        currentHeight = height;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (stagingTexture != null) stagingTexture->Release();
    }
}
