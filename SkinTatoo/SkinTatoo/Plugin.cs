using System;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SkinTatoo.Core;
using SkinTatoo.Gpu;
using SkinTatoo.Gui;
using SkinTatoo.Http;
using SkinTatoo.Interop;
using SkinTatoo.Mesh;
using SkinTatoo.Services;

namespace SkinTatoo;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/skintatoo";

    // 配置
    private readonly Configuration config;

    // GPU 层
    private readonly DxManager dxManager;
    private readonly ComputeShaderPipeline pipeline;
    private readonly StagingReadback readback;

    // Interop 层
    private readonly PenumbraBridge penumbra;
    private readonly BodyModDetector bodyModDetector;

    // 服务层
    private readonly MeshExtractor meshExtractor;
    private readonly DecalImageLoader imageLoader;
    private readonly PreviewService previewService;

    // HTTP 调试服务
    private readonly DebugServer debugServer;

    // 项目数据
    private readonly DecalProject project;

    // GUI
    private readonly WindowSystem windowSystem;
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IDataManager dataManager,
        IPluginLog log)
    {
        // ── 1. 配置 ────────────────────────────────────────────────────────────
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        // ── 2. GPU ─────────────────────────────────────────────────────────────
        dxManager = new DxManager(log);
        dxManager.InitializeStandaloneDevice();

        pipeline = new ComputeShaderPipeline(dxManager, log);
        pipeline.Initialize();

        readback = new StagingReadback(dxManager, log);

        // ── 3. Interop ─────────────────────────────────────────────────────────
        penumbra = new PenumbraBridge(pluginInterface, log);
        bodyModDetector = new BodyModDetector(penumbra, log);

        // ── 4. Services ────────────────────────────────────────────────────────
        meshExtractor = new MeshExtractor(dataManager, log);
        imageLoader = new DecalImageLoader(log);

        var outputDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "preview");
        previewService = new PreviewService(
            dxManager, pipeline, readback,
            meshExtractor, imageLoader,
            penumbra, log, config, outputDir);

        // ── 5. 项目数据 ────────────────────────────────────────────────────────
        project = new DecalProject { Target = config.LastTarget };

        // ── 6. HTTP 调试服务 ───────────────────────────────────────────────────
        debugServer = new DebugServer(config, project, penumbra, previewService);
        debugServer.Start();

        // ── 7. GUI ─────────────────────────────────────────────────────────────
        mainWindow = new MainWindow(project, previewService, penumbra, config);
        configWindow = new ConfigWindow(config);

        windowSystem = new WindowSystem("SkinTatoo");
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);

        // ── 8. 注册 UiBuilder 钩子 ─────────────────────────────────────────────
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        // ── 9. 注册聊天命令 ────────────────────────────────────────────────────
        commandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = "打开 SkinTatoo 纹身编辑器窗口",
        });

        log.Information("SkinTatoo 已加载。GPU={0} Penumbra={1}",
            dxManager.IsInitialized, penumbra.IsAvailable);
    }

    private void DrawUi() => windowSystem.Draw();

    private void OpenConfigUi() => configWindow.IsOpen = true;

    private void OpenMainUi() => mainWindow.IsOpen = true;

    private void OnCommand(string command, string args)
    {
        mainWindow.IsOpen = !mainWindow.IsOpen;
    }

    public void Dispose()
    {
        // 逆序释放

        CommandManager.RemoveHandler(CommandName);

        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;

        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();

        debugServer.Dispose();

        previewService.Dispose();

        readback.Dispose();
        pipeline.Dispose();
        dxManager.Dispose();

        penumbra.Dispose();
    }
}
