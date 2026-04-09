using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace SkinTatoo.DirectX;

[StructLayout(LayoutKind.Sequential)]
public struct VSConstants
{
    public Matrix WorldMatrix;
    public Matrix ViewProjectionMatrix;
}

[StructLayout(LayoutKind.Sequential)]
public struct PSConstants
{
    public Vector3 LightDir;
    public float _pad0;
    public Vector3 CameraPos;
    public float _pad1;
    public int HasTexture;
    public Vector3 _pad2;
}

public class DxRenderer : IDisposable
{
    private readonly Device device;
    private readonly DeviceContext ctx;

    private int width = 4;
    private int height = 4;

    private Texture2D? renderTexture;
    private ShaderResourceView? renderSrv;
    private RenderTargetView? renderTarget;

    private Texture2D? depthTexture;
    private DepthStencilView? depthView;
    private DepthStencilState? depthState;

    private VertexShader? vertexShader;
    private PixelShader? pixelShader;
    private InputLayout? inputLayout;

    private SharpDX.Direct3D11.Buffer? vsCBuffer;
    private SharpDX.Direct3D11.Buffer? psCBuffer;

    private SamplerState? sampler;
    private RasterizerState? rasterState;

    // Reusable BGRA scratch for CreateTextureFromRgba — without this every call
    // allocates a fresh w*h*4 byte[] (67MB at 4096²) and pins it via the DataStream
    // GCHandle. The unreleased pin keeps it stuck in LOH until the finalizer runs,
    // so a sustained 3D-editor drag at 30Hz pumps ~2GB/s of pinned LOH and the
    // working set climbs without bound.
    private byte[]? bgraScratch;

    public nint OutputPointer => renderSrv?.NativePointer ?? nint.Zero;
    public Device Device => device;
    public DeviceContext Context => ctx;

    public DxRenderer(nint deviceHandle)
    {
        device = new Device(deviceHandle);
        ctx = device.ImmediateContext;

        CompileShaders();
        CreateStates();
        ResizeResources();
    }

    private void CompileShaders()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string hlslSource;
        using (var stream = assembly.GetManifestResourceStream("SkinTatoo.Shaders.Model.hlsl"))
        {
            if (stream == null) throw new Exception("Shader resource 'SkinTatoo.Shaders.Model.hlsl' not found");
            using var reader = new StreamReader(stream);
            hlslSource = reader.ReadToEnd();
        }

        using var vsCompiled = ShaderBytecode.Compile(hlslSource, "VS", "vs_4_0");
        if (vsCompiled.HasErrors) throw new Exception($"VS compile error: {vsCompiled.Message}");
        vertexShader = new VertexShader(device, vsCompiled);

        using var psCompiled = ShaderBytecode.Compile(hlslSource, "PS", "ps_4_0");
        if (psCompiled.HasErrors) throw new Exception($"PS compile error: {psCompiled.Message}");
        pixelShader = new PixelShader(device, psCompiled);

        using var signature = ShaderSignature.GetInputSignature(vsCompiled);
        inputLayout = new InputLayout(device, signature, new[]
        {
            new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElement("TEXCOORD", 0, Format.R32G32_Float, 24, 0),
        });

        vsCBuffer = new SharpDX.Direct3D11.Buffer(device,
            Utilities.SizeOf<VSConstants>(),
            ResourceUsage.Default, BindFlags.ConstantBuffer,
            CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        psCBuffer = new SharpDX.Direct3D11.Buffer(device,
            Utilities.SizeOf<PSConstants>(),
            ResourceUsage.Default, BindFlags.ConstantBuffer,
            CpuAccessFlags.None, ResourceOptionFlags.None, 0);
    }

    private void CreateStates()
    {
        depthState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            IsStencilEnabled = false,
            DepthWriteMask = DepthWriteMask.All,
            DepthComparison = Comparison.Less,
        });

        sampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            BorderColor = Color.Black,
            ComparisonFunction = Comparison.Never,
            MaximumAnisotropy = 16,
            MinimumLod = 0,
            MaximumLod = float.MaxValue,
        });

        rasterState = new RasterizerState(device, new RasterizerStateDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            IsDepthClipEnabled = true,
            IsMultisampleEnabled = true,
        });
    }

    public void Resize(int newWidth, int newHeight)
    {
        if (newWidth < 1) newWidth = 1;
        if (newHeight < 1) newHeight = 1;
        if (newWidth == width && newHeight == height) return;
        width = newWidth;
        height = newHeight;
        ResizeResources();
    }

    private void ResizeResources()
    {
        renderTexture?.Dispose();
        renderSrv?.Dispose();
        renderTarget?.Dispose();
        depthTexture?.Dispose();
        depthView?.Dispose();

        renderTexture = new Texture2D(device, new Texture2DDescription
        {
            Format = Format.B8G8R8A8_UNorm,
            ArraySize = 1,
            MipLevels = 1,
            Width = width,
            Height = height,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        });
        renderSrv = new ShaderResourceView(device, renderTexture);
        renderTarget = new RenderTargetView(device, renderTexture);

        depthTexture = new Texture2D(device, new Texture2DDescription
        {
            ArraySize = 1,
            BindFlags = BindFlags.DepthStencil,
            CpuAccessFlags = CpuAccessFlags.None,
            Format = Format.D32_Float,
            Width = width,
            Height = height,
            MipLevels = 1,
            OptionFlags = ResourceOptionFlags.None,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
        });
        depthView = new DepthStencilView(device, depthTexture);
    }

    public void BeginFrame(out RasterizerState? oldRaster, out RenderTargetView[] oldRtvs, out DepthStencilView? oldDsv, out DepthStencilState? oldDss)
    {
        oldRaster = ctx.Rasterizer.State;
        oldRtvs = ctx.OutputMerger.GetRenderTargets(OutputMergerStage.SimultaneousRenderTargetCount, out oldDsv);
        oldDss = ctx.OutputMerger.GetDepthStencilState(out _);

        ctx.ClearRenderTargetView(renderTarget, new Color4(0.15f, 0.15f, 0.18f, 1f));
        ctx.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth, 1f, 0);

        ctx.Rasterizer.SetViewport(new Viewport(0, 0, width, height, 0f, 1f));
        ctx.Rasterizer.State = rasterState;
        ctx.OutputMerger.SetDepthStencilState(depthState);
        ctx.OutputMerger.SetTargets(depthView, renderTarget);

        ctx.VertexShader.Set(vertexShader);
        ctx.PixelShader.Set(pixelShader);
        ctx.InputAssembler.InputLayout = inputLayout;
        ctx.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;

        ctx.VertexShader.SetConstantBuffer(0, vsCBuffer);
        ctx.PixelShader.SetConstantBuffer(0, psCBuffer);
        ctx.PixelShader.SetSampler(0, sampler);
    }

    public void UpdateVSConstants(ref VSConstants data) => ctx.UpdateSubresource(ref data, vsCBuffer);
    public void UpdatePSConstants(ref PSConstants data) => ctx.UpdateSubresource(ref data, psCBuffer);

    public void BindDiffuseTexture(ShaderResourceView? srv)
    {
        ctx.PixelShader.SetShaderResource(0, srv);
    }

    public void EndFrame(RasterizerState? oldRaster, RenderTargetView[] oldRtvs, DepthStencilView? oldDsv, DepthStencilState? oldDss)
    {
        ctx.Flush();

        ctx.Rasterizer.State = oldRaster;
        ctx.OutputMerger.SetRenderTargets(oldDsv, oldRtvs);
        ctx.OutputMerger.SetDepthStencilState(oldDss);
    }

    public ShaderResourceView CreateTextureFromRgba(byte[] rgbaData, int texWidth, int texHeight, out Texture2D texture)
    {
        int byteCount = texWidth * texHeight * 4;
        if (bgraScratch == null || bgraScratch.Length < byteCount)
            bgraScratch = new byte[byteCount];

        for (int i = 0; i < byteCount; i += 4)
        {
            bgraScratch[i]     = rgbaData[i + 2]; // B
            bgraScratch[i + 1] = rgbaData[i + 1]; // G
            bgraScratch[i + 2] = rgbaData[i];     // R
            bgraScratch[i + 3] = rgbaData[i + 3]; // A
        }

        // `using` is critical: DataStream.Create pins the byte[] via a GCHandle and
        // the Texture2D ctor copies from that pointer synchronously, so we can free
        // the pin immediately on the next line. Without this, every call leaks a
        // pinned LOH buffer until the finalizer eventually runs.
        using var stream = DataStream.Create(bgraScratch, true, true);
        var rect = new DataRectangle(stream.DataPointer, texWidth * 4);
        texture = new Texture2D(device, new Texture2DDescription
        {
            Width = texWidth,
            Height = texHeight,
            ArraySize = 1,
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            CpuAccessFlags = CpuAccessFlags.None,
            Format = Format.B8G8R8A8_UNorm,
            MipLevels = 1,
            OptionFlags = ResourceOptionFlags.None,
            SampleDescription = new SampleDescription(1, 0),
        }, rect);

        return new ShaderResourceView(device, texture);
    }

    /// <summary>
    /// Create an empty persistent RGBA texture sized for repeated sub-region updates.
    /// Format is R8G8B8A8_UNorm so callers can feed RGBA buffers directly without the
    /// RGBA→BGRA swizzle that CreateTextureFromRgba does — the shader's Sample() returns
    /// the same float4 regardless of channel ordering, so it's transparent to HLSL.
    /// </summary>
    public (Texture2D Texture, ShaderResourceView Srv) CreateUpdatableRgbaTexture(int texWidth, int texHeight)
    {
        var tex = new Texture2D(device, new Texture2DDescription
        {
            Width = texWidth,
            Height = texHeight,
            ArraySize = 1,
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,            // Default allows UpdateSubresource
            CpuAccessFlags = CpuAccessFlags.None,
            Format = Format.R8G8B8A8_UNorm,
            MipLevels = 1,
            OptionFlags = ResourceOptionFlags.None,
            SampleDescription = new SampleDescription(1, 0),
        });
        var srv = new ShaderResourceView(device, tex);
        return (tex, srv);
    }

    /// <summary>
    /// Update a sub-rect of <paramref name="texture"/> from a full-sized RGBA source buffer.
    /// Row pitch = full buffer row (so UpdateSubresource strides past the rows outside the
    /// sub-region). Data pointer = offset into the buffer for the top-left of the sub-rect.
    /// Call with (0,0,texW,texH) after (re)creating the texture to seed it fully.
    /// </summary>
    public void UpdateRgbaRegion(Texture2D texture, byte[] rgbaData, int bufferWidth,
        int regionX, int regionY, int regionW, int regionH)
    {
        if (regionW <= 0 || regionH <= 0) return;

        using var stream = DataStream.Create(rgbaData, true, true);
        var ptr = stream.DataPointer + (regionY * bufferWidth + regionX) * 4;
        var box = new DataBox(ptr, bufferWidth * 4, 0);
        var region = new ResourceRegion(regionX, regionY, 0, regionX + regionW, regionY + regionH, 1);
        ctx.UpdateSubresource(box, texture, 0, region);
    }

    public void Dispose()
    {
        renderTexture?.Dispose();
        renderSrv?.Dispose();
        renderTarget?.Dispose();
        depthTexture?.Dispose();
        depthView?.Dispose();
        depthState?.Dispose();
        sampler?.Dispose();
        rasterState?.Dispose();
        vertexShader?.Dispose();
        pixelShader?.Dispose();
        inputLayout?.Dispose();
        vsCBuffer?.Dispose();
        psCBuffer?.Dispose();
        bgraScratch = null;
    }
}
