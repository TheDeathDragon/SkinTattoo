using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static INotificationManager NotificationManager { get; private set; } = null!;

    private const string CommandName = "/skintatoo";

    private readonly Configuration config;

    // Interop
    private readonly PenumbraBridge penumbra;
    private readonly TextureSwapService textureSwap;
    private readonly EmissiveCBufferHook emissiveHook;

    // Services
    private readonly MeshExtractor meshExtractor;
    private readonly SkinMeshResolver skinMeshResolver;
    private readonly DecalImageLoader imageLoader;
    private readonly PreviewService previewService;
    private readonly ModExportService modExportService;

    // HTTP
    private readonly DebugServer debugServer;

    // Project
    private readonly DecalProject project;

    // GUI
    private readonly WindowSystem windowSystem;
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly DebugWindow debugWindow;
    private readonly ModelEditorWindow modelEditorWindow;
    private readonly ModExportWindow modExportWindow;
    private readonly PbrInspectorWindow pbrInspectorWindow;

    // Periodic auto-save of project + window state (30 s is frequent enough that the
    // user doesn't lose work after a crash, infrequent enough not to thrash the config file).
    private DateTime lastAutoSave = DateTime.MinValue;
    private const double AutoSaveIntervalSec = 30.0;

    private readonly IFramework framework;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IDataManager dataManager,
        ITextureProvider textureProvider,
        IPluginLog log,
        IObjectTable objectTable,
        IFramework framework,
        IGameInteropProvider gameInterop)
    {
        // 1. Config
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        // 2. Interop
        penumbra = new PenumbraBridge(pluginInterface, log);
        textureSwap = new TextureSwapService(objectTable, log);
        emissiveHook = new EmissiveCBufferHook(gameInterop, log);

        // 3. Services
        meshExtractor = new MeshExtractor(dataManager, log);
        skinMeshResolver = new SkinMeshResolver(meshExtractor);
        imageLoader = new DecalImageLoader(log, dataManager);

        var outputDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "preview");
        previewService = new PreviewService(
            meshExtractor, imageLoader,
            penumbra, textureSwap, emissiveHook, log, config, outputDir);

        var exportTempDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "export_temp");
        modExportService = new ModExportService(previewService, penumbra, NotificationManager, log, exportTempDir);

        // 4. Project - restore from config
        project = new DecalProject();
        project.LoadFromConfig(config);

        // 5. HTTP
        debugServer = new DebugServer(config, project, penumbra, previewService, dataManager, modExportService, skinMeshResolver, textureSwap);
        debugServer.Start();

        // 6. GUI
        mainWindow = new MainWindow(project, previewService, penumbra, config, textureProvider, dataManager, skinMeshResolver);
        configWindow = new ConfigWindow(config);
        debugWindow = new DebugWindow();

        // 3D Editor
        modelEditorWindow = new ModelEditorWindow(project, previewService, penumbra, skinMeshResolver, pluginInterface.UiBuilder.DeviceHandle);

        modExportWindow = new ModExportWindow(project, modExportService, config);
        pbrInspectorWindow = new PbrInspectorWindow(project, previewService, textureProvider);

        mainWindow.DebugWindowRef = debugWindow;
        mainWindow.ConfigWindowRef = configWindow;
        mainWindow.ModelEditorWindowRef = modelEditorWindow;
        mainWindow.ModExportWindowRef = modExportWindow;
        mainWindow.PbrInspectorWindowRef = pbrInspectorWindow;
        mainWindow.InitializeRequested = InitializeProjectPreview;

        windowSystem = new WindowSystem("SkinTatoo");
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(configWindow);
        windowSystem.AddWindow(debugWindow);
        windowSystem.AddWindow(modelEditorWindow);
        windowSystem.AddWindow(modExportWindow);
        windowSystem.AddWindow(pbrInspectorWindow);

        // Every window starts closed on plugin load, regardless of prior session state.
        // This avoids any heavy work (mesh load, preview upload, 3D editor DX init) the
        // instant the game finishes loading; the user opens the windows explicitly via
        // /skintatoo or toolbar buttons, and that first show drives the init path.
        // Note: ImGui still persists window positions in imgui.ini, so reopening lands
        // the window exactly where the user left it.

        // 7. UiBuilder hooks
        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        // 8. Chat command
        commandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = "打开 SkinTatoo 纹身编辑器窗口",
        });

        this.framework = framework;

        log.Information("SkinTatoo 已加载。Penumbra={0}", penumbra.IsAvailable);
    }

    private void DrawUi()
    {
        // Only draw when logged into the game world
        if (ObjectTable.LocalPlayer == null) return;

        windowSystem.Draw();

        // Periodic auto-save: both project and window state.
        var now = DateTime.UtcNow;
        if ((now - lastAutoSave).TotalSeconds >= AutoSaveIntervalSec)
        {
            lastAutoSave = now;
            project.SaveToConfig(config);
            SaveWindowStates();
        }
    }

    /// <summary>
    /// Kick off mesh load + preview apply for every configured group. Returns a Task
    /// that completes after both the background mesh load and the main-thread IPC hop
    /// finish, so MainWindow can display a loading overlay until then.
    ///
    /// Runs entirely off the main thread: <c>LoadMeshes</c> (file IO + mesh extraction)
    /// on a <see cref="Task.Run"/> worker, then hops back to the framework thread via
    /// <see cref="IFramework.RunOnFrameworkThread(System.Action)"/> for the Penumbra IPC
    /// + GPU swap hand-off. The first MainWindow draw is never blocked.
    /// </summary>
    private Task InitializeProjectPreview()
    {
        // Snapshot the work we need to do while we're still on the main thread — the
        // background Task will then read frozen data and won't race the UI.
        TargetGroup? meshGroup = null;
        foreach (var group in project.Groups)
        {
            if ((group.MeshSlots.Count > 0 || group.AllMeshPaths.Count > 0)
                && previewService.CurrentMesh == null)
            {
                meshGroup = group;
                break;
            }
        }

        var hasLayers = false;
        foreach (var group in project.Groups)
        {
            if (!string.IsNullOrEmpty(group.DiffuseGamePath) && group.Layers.Count > 0)
            { hasLayers = true; break; }
        }

        return Task.Run(async () =>
        {
            try
            {
                if (meshGroup != null)
                    previewService.LoadMeshForGroup(meshGroup);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Init] background mesh load failed");
            }

            // Hop back to the framework thread: Penumbra IPC + mesh-editor state updates
            // must run on main to be safe. Awaiting this means the returned Task only
            // completes once the main-thread step is done — the MainWindow loading
            // overlay stays up until everything is ready.
            await framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    previewService.NotifyMeshChanged();
                    if (hasLayers)
                    {
                        previewService.UpdatePreview(project);
                        modelEditorWindow.MarkTexturesDirty();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Init] main-thread preview apply failed");
                }
            });
        });
    }

    private void SaveWindowStates()
    {
        // Window open state is intentionally NOT persisted for any window: on plugin
        // load every window starts closed so the first frame is free of heavy work.
        // ImGui still persists window positions in imgui.ini.
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
        mainWindow.InitializeRequested = null;
        mainWindow.Dispose();
        modelEditorWindow.Dispose();

        debugServer.Dispose();

        modExportService.Dispose();
        previewService.Dispose();
        emissiveHook.Dispose();

        penumbra.Dispose();
    }
}
