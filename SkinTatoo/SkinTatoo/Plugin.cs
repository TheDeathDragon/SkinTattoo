using System;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SkinTatoo.Core;
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

    private readonly Configuration config;

    // Interop
    private readonly PenumbraBridge penumbra;

    // Services
    private readonly MeshExtractor meshExtractor;
    private readonly DecalImageLoader imageLoader;
    private readonly PreviewService previewService;

    // HTTP
    private readonly DebugServer debugServer;

    // Project
    private readonly DecalProject project;

    // GUI
    private readonly WindowSystem windowSystem;
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly DebugWindow debugWindow;

    // Auto-save throttle
    private DateTime lastAutoSave = DateTime.MinValue;
    private const double AutoSaveIntervalSec = 30.0;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IDataManager dataManager,
        ITextureProvider textureProvider,
        IPluginLog log)
    {
        // 1. Config
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        // 2. Interop
        penumbra = new PenumbraBridge(pluginInterface, log);

        // 3. Services
        meshExtractor = new MeshExtractor(dataManager, log);
        imageLoader = new DecalImageLoader(log, dataManager);

        var outputDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "preview");
        previewService = new PreviewService(
            meshExtractor, imageLoader,
            penumbra, log, config, outputDir);

        // 5. Project - restore from config
        project = new DecalProject();
        project.LoadFromConfig(config);

        // 6. HTTP
        debugServer = new DebugServer(config, project, penumbra, previewService, dataManager);
        debugServer.Start();

        // 7. GUI
        mainWindow = new MainWindow(project, previewService, penumbra, config, textureProvider);
        configWindow = new ConfigWindow(config);
        debugWindow = new DebugWindow(penumbra, previewService, textureProvider, config);

        mainWindow.DebugWindowRef = debugWindow;
        mainWindow.OnSaveRequested += SaveProject;

        windowSystem = new WindowSystem("SkinTatoo");
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(debugWindow);

        // Restore window open states
        mainWindow.IsOpen = config.MainWindowOpen;
        debugWindow.IsOpen = config.DebugWindowOpen;

        // 8. UiBuilder hooks
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        // 9. Chat command
        commandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = "打开 SkinTatoo 纹身编辑器窗口",
        });

        log.Information("SkinTatoo 已加载。Penumbra={0}", penumbra.IsAvailable);
    }

    private void DrawUi()
    {
        windowSystem.Draw();

        // Periodic auto-save
        var now = DateTime.UtcNow;
        if ((now - lastAutoSave).TotalSeconds >= AutoSaveIntervalSec)
        {
            lastAutoSave = now;
            SaveWindowStates();
        }
    }

    private void SaveProject()
    {
        project.SaveToConfig(config);
        SaveWindowStates();
    }

    private void SaveWindowStates()
    {
        config.MainWindowOpen = mainWindow.IsOpen;
        config.DebugWindowOpen = debugWindow.IsOpen;
        config.Save();
    }

    private void OpenConfigUi() => configWindow.IsOpen = true;

    private void OpenMainUi() => mainWindow.IsOpen = true;

    private void OnCommand(string command, string args)
    {
        mainWindow.IsOpen = !mainWindow.IsOpen;
    }

    public void Dispose()
    {
        // Save state before teardown
        project.SaveToConfig(config);
        SaveWindowStates();

        CommandManager.RemoveHandler(CommandName);

        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;

        windowSystem.RemoveAllWindows();
        mainWindow.OnSaveRequested -= SaveProject;
        mainWindow.Dispose();

        debugServer.Dispose();

        previewService.Dispose();

        penumbra.Dispose();
    }
}
