using System;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using SkinTatoo.Core;
using SkinTatoo.Gpu;
using SkinTatoo.Interop;
using SkinTatoo.Mesh;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.D3D11_BIND_FLAG;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace SkinTatoo.Services;

public unsafe class PreviewService : IDisposable
{
    private readonly DxManager dx;
    private readonly ComputeShaderPipeline pipeline;
    private readonly StagingReadback readback;
    private readonly MeshExtractor meshExtractor;
    private readonly DecalImageLoader imageLoader;
    private readonly PenumbraBridge penumbra;
    private readonly IPluginLog log;
    private readonly Configuration config;

    // 每次 LoadMesh 后持有的 GPU 资源
    private ID3D11Texture2D* positionMapTex;
    private ID3D11ShaderResourceView* positionMapSRV;
    private ID3D11Texture2D* normalMapTex;
    private ID3D11ShaderResourceView* normalMapSRV;
    private uint mapWidth;
    private uint mapHeight;

    private MeshData? currentMesh;
    private readonly string outputDir;
    private bool disposed;

    // GPU 管线是否就绪
    public bool IsReady => dx.IsInitialized && pipeline.IsInitialized;

    // 当前已加载的 mesh，供外部查询（如 DebugServer）
    public MeshData? CurrentMesh => currentMesh;

    public PreviewService(
        DxManager dx,
        ComputeShaderPipeline pipeline,
        StagingReadback readback,
        MeshExtractor meshExtractor,
        DecalImageLoader imageLoader,
        PenumbraBridge penumbra,
        IPluginLog log,
        Configuration config,
        string outputDir)
    {
        this.dx = dx;
        this.pipeline = pipeline;
        this.readback = readback;
        this.meshExtractor = meshExtractor;
        this.imageLoader = imageLoader;
        this.penumbra = penumbra;
        this.log = log;
        this.config = config;
        this.outputDir = outputDir;

        Directory.CreateDirectory(outputDir);
    }

    // 从游戏 .mdl 路径提取 mesh，生成 position/normal map 并上传至 GPU。
    // 异步包装：mesh 提取和 CPU 生成在 Task.Run 中进行，GPU 上传在调用线程执行。
    public Task LoadMesh(string gameMdlPath)
    {
        return Task.Run(() => LoadMeshInternal(gameMdlPath));
    }

    private void LoadMeshInternal(string gameMdlPath)
    {
        if (!dx.IsInitialized || !pipeline.IsInitialized)
        {
            log.Error("PreviewService.LoadMesh: GPU not ready");
            return;
        }

        var meshData = meshExtractor.ExtractMesh(gameMdlPath);
        if (meshData == null)
        {
            log.Error("PreviewService.LoadMesh: failed to extract mesh from {0}", gameMdlPath);
            return;
        }

        var res = (uint)Math.Clamp(config.TextureResolution, 256, 4096);
        mapWidth  = res;
        mapHeight = res;

        var gen = new PositionMapGenerator((int)res, (int)res);
        gen.Generate(meshData);

        FreeMapResources();

        positionMapTex = dx.CreateTexture2D(
            mapWidth, mapHeight,
            DXGI_FORMAT_R32G32B32A32_FLOAT,
            (uint)D3D11_BIND_SHADER_RESOURCE);
        if (positionMapTex == null) return;
        dx.UploadTextureData(positionMapTex, gen.PositionMap, mapWidth, mapHeight);
        positionMapSRV = dx.CreateSRV(positionMapTex, DXGI_FORMAT_R32G32B32A32_FLOAT);

        normalMapTex = dx.CreateTexture2D(
            mapWidth, mapHeight,
            DXGI_FORMAT_R32G32B32A32_FLOAT,
            (uint)D3D11_BIND_SHADER_RESOURCE);
        if (normalMapTex == null) return;
        dx.UploadTextureData(normalMapTex, gen.NormalMap, mapWidth, mapHeight);
        normalMapSRV = dx.CreateSRV(normalMapTex, DXGI_FORMAT_R32G32B32A32_FLOAT);

        currentMesh = meshData;
        log.Information("PreviewService: mesh loaded ({0}x{1} maps) from {2}", res, res, gameMdlPath);
    }

    // 对 project 中每个可见层执行完整的投影→合成→膨胀流水线，
    // 将结果写成 .tex 文件并通过 Penumbra 重定向到 gameTexturePath，然后重绘玩家。
    public Task UpdatePreview(DecalProject project, string? gameTexturePath)
    {
        return Task.Run(() => UpdatePreviewInternal(project, gameTexturePath));
    }

    private void UpdatePreviewInternal(DecalProject project, string? gameTexturePath)
    {
        if (!dx.IsInitialized || !pipeline.IsInitialized)
        {
            log.Error("PreviewService.UpdatePreview: GPU not ready");
            return;
        }

        if (positionMapSRV == null || normalMapSRV == null)
        {
            log.Error("PreviewService.UpdatePreview: no mesh loaded");
            return;
        }

        // 累积合成贴图：从全透明黑色开始
        ID3D11Texture2D* accumTex = CreateFloatRWTexture();
        if (accumTex == null) return;
        ID3D11ShaderResourceView* accumSRV = dx.CreateSRV(accumTex, DXGI_FORMAT_R32G32B32A32_FLOAT);
        ID3D11UnorderedAccessView* accumUAV = dx.CreateUAV(accumTex, DXGI_FORMAT_R32G32B32A32_FLOAT);

        try
        {
            foreach (var layer in project.Layers)
            {
                if (!layer.IsVisible) continue;
                if (string.IsNullOrEmpty(layer.ImagePath)) continue;

                ProcessLayer(layer, ref accumTex, ref accumSRV, ref accumUAV);
            }

            // 读回最终结果并写 .tex
            var rawData = readback.Readback(accumTex, mapWidth, mapHeight);
            if (rawData == null)
            {
                log.Error("PreviewService.UpdatePreview: GPU readback returned null");
                return;
            }

            var localPath = Path.Combine(outputDir, "preview.tex");
            TexFileWriter.WriteUncompressed(localPath, rawData, (int)mapWidth, (int)mapHeight);

            if (!string.IsNullOrEmpty(gameTexturePath))
            {
                penumbra.SetTextureRedirect(gameTexturePath, localPath);
                penumbra.RedrawPlayer();
            }

            log.Information("PreviewService: preview updated → {0}", localPath);
        }
        finally
        {
            if (accumUAV != null) accumUAV->Release();
            if (accumSRV != null) accumSRV->Release();
            if (accumTex != null) accumTex->Release();
        }
    }

    // 对单个 DecalLayer 执行投影、合成、膨胀，结果写回 accumTex
    private void ProcessLayer(
        DecalLayer layer,
        ref ID3D11Texture2D* accumTex,
        ref ID3D11ShaderResourceView* accumSRV,
        ref ID3D11UnorderedAccessView* accumUAV)
    {
        var imageResult = imageLoader.LoadImage(layer.ImagePath!);
        if (imageResult == null)
        {
            log.Warning("PreviewService: cannot load image {0}, skipping layer", layer.ImagePath);
            return;
        }

        var (imgData, imgW, imgH) = imageResult.Value;
        var floatData = ConvertRgba8ToFloat4(imgData, imgW, imgH);

        ID3D11Texture2D* decalTex = dx.CreateTexture2D(
            (uint)imgW, (uint)imgH,
            DXGI_FORMAT_R32G32B32A32_FLOAT,
            (uint)D3D11_BIND_SHADER_RESOURCE);
        if (decalTex == null) return;

        dx.UploadTextureData(decalTex, floatData, (uint)imgW, (uint)imgH);
        ID3D11ShaderResourceView* decalSRV = dx.CreateSRV(decalTex, DXGI_FORMAT_R32G32B32A32_FLOAT);

        // 投影结果缓冲（与 position map 同分辨率）
        ID3D11Texture2D* projBufTex = CreateFloatRWTexture();
        ID3D11UnorderedAccessView* projBufUAV = projBufTex != null
            ? dx.CreateUAV(projBufTex, DXGI_FORMAT_R32G32B32A32_FLOAT)
            : null;
        ID3D11ShaderResourceView* projBufSRV = projBufTex != null
            ? dx.CreateSRV(projBufTex, DXGI_FORMAT_R32G32B32A32_FLOAT)
            : null;

        // 膨胀输出缓冲
        ID3D11Texture2D* dilatedTex = CreateFloatRWTexture();
        ID3D11UnorderedAccessView* dilatedUAV = dilatedTex != null
            ? dx.CreateUAV(dilatedTex, DXGI_FORMAT_R32G32B32A32_FLOAT)
            : null;
        ID3D11ShaderResourceView* dilatedSRV = dilatedTex != null
            ? dx.CreateSRV(dilatedTex, DXGI_FORMAT_R32G32B32A32_FLOAT)
            : null;

        // 新的合成输出（替换 accum）
        ID3D11Texture2D* compositedTex = null;
        ID3D11UnorderedAccessView* compositedUAV = null;
        ID3D11ShaderResourceView* compositedSRV = null;

        try
        {
            if (projBufUAV == null || projBufSRV == null ||
                dilatedUAV == null || dilatedSRV == null)
                return;

            // 1. 投影：将贴花投影到 UV 空间
            var projCB = new ProjectionCB
            {
                ViewProjection    = layer.GetProjectionMatrix(),
                ProjectionDir     = layer.GetForwardDirection(),
                BackfaceThreshold = layer.BackfaceCullingThreshold,
                GrazingFade       = layer.GrazingAngleFade,
                Opacity           = layer.Opacity,
            };
            pipeline.DispatchProjection(
                positionMapSRV, normalMapSRV, decalSRV,
                projBufUAV, projCB,
                mapWidth, mapHeight);

            // 2. 膨胀：消除 UV 接缝处的黑边
            pipeline.DispatchDilation(
                projBufSRV, projBufSRV,
                dilatedUAV,
                mapWidth, mapHeight);

            // 3. 合成：将当前层叠加到 accum
            compositedTex = CreateFloatRWTexture();
            if (compositedTex == null) return;
            compositedUAV = dx.CreateUAV(compositedTex, DXGI_FORMAT_R32G32B32A32_FLOAT);
            compositedSRV = dx.CreateSRV(compositedTex, DXGI_FORMAT_R32G32B32A32_FLOAT);
            if (compositedUAV == null) return;

            var compCB = new CompositeCB
            {
                BlendMode   = (uint)layer.BlendMode,
                IsNormalMap = layer.AffectsNormal ? 1u : 0u,
            };
            pipeline.DispatchComposite(
                accumSRV,
                dilatedSRV,
                compositedUAV,
                compCB,
                mapWidth, mapHeight);

            // 用新合成结果替换 accum（释放旧 accum 资源）
            accumUAV->Release();
            accumSRV->Release();
            accumTex->Release();

            accumTex = compositedTex;
            accumSRV = compositedSRV!;
            accumUAV = compositedUAV!;

            // 所有权已移交，防止 finally 中重复释放
            compositedTex = null;
            compositedSRV = null;
            compositedUAV = null;
        }
        finally
        {
            if (projBufUAV != null) projBufUAV->Release();
            if (projBufSRV != null) projBufSRV->Release();
            if (projBufTex != null) projBufTex->Release();
            if (dilatedUAV  != null) dilatedUAV->Release();
            if (dilatedSRV  != null) dilatedSRV->Release();
            if (dilatedTex  != null) dilatedTex->Release();
            if (compositedUAV != null) compositedUAV->Release();
            if (compositedSRV != null) compositedSRV->Release();
            if (compositedTex != null) compositedTex->Release();
            if (decalSRV != null) decalSRV->Release();
            if (decalTex != null) decalTex->Release();
        }
    }

    // 创建一个可同时用作 SRV/UAV 的 RGBA float32 贴图
    private ID3D11Texture2D* CreateFloatRWTexture()
    {
        return dx.CreateTexture2D(
            mapWidth, mapHeight,
            DXGI_FORMAT_R32G32B32A32_FLOAT,
            (uint)(D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS));
    }

    // 将 RGBA8 字节数组线性转换为 [0,1] float4 数组
    private static float[] ConvertRgba8ToFloat4(byte[] data, int w, int h)
    {
        var result = new float[w * h * 4];
        const float inv255 = 1.0f / 255.0f;
        for (var i = 0; i < result.Length; i++)
            result[i] = data[i] * inv255;
        return result;
    }

    private void FreeMapResources()
    {
        if (positionMapSRV != null) { positionMapSRV->Release(); positionMapSRV = null; }
        if (positionMapTex != null) { positionMapTex->Release(); positionMapTex = null; }
        if (normalMapSRV   != null) { normalMapSRV->Release();   normalMapSRV   = null; }
        if (normalMapTex   != null) { normalMapTex->Release();   normalMapTex   = null; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        FreeMapResources();
    }
}
