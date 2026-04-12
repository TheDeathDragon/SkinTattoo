using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SkinTattoo.Core;
using SkinTattoo.Gui;
using SkinTattoo.Http;
using SkinTattoo.Interop;
using SkinTattoo.Mesh;
using SkinTattoo.Services;

namespace SkinTattoo;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static INotificationManager NotificationManager { get; private set; } = null!;

    private const string CommandName = "/skintattoo";

    private readonly Configuration config;
    private readonly PenumbraBridge penumbra;
    private readonly TextureSwapService textureSwap;
    private readonly EmissiveCBufferHook emissiveHook;
    private readonly MeshExtractor meshExtractor;
    private readonly SkinMeshResolver skinMeshResolver;
    private readonly DecalImageLoader imageLoader;
    private readonly PreviewService previewService;
    private readonly ModExportService modExportService;
    private readonly DebugServer debugServer;
    private readonly DecalProject project;
    private readonly WindowSystem windowSystem;
    private readonly MainWindow mainWindow;
    private readonly DebugWindow debugWindow;
    private readonly ModelEditorWindow modelEditorWindow;
    private readonly ModExportWindow modExportWindow;

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
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        penumbra = new PenumbraBridge(pluginInterface, log);
        textureSwap = new TextureSwapService(objectTable, log);
        emissiveHook = new EmissiveCBufferHook(gameInterop, log);

        meshExtractor = new MeshExtractor(dataManager, log);
        skinMeshResolver = new SkinMeshResolver(meshExtractor);
        imageLoader = new DecalImageLoader(log, dataManager);

        var outputDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "preview");
        previewService = new PreviewService(
            meshExtractor, imageLoader,
            penumbra, textureSwap, emissiveHook, log, config, outputDir);

        var exportTempDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "export_temp");
        modExportService = new ModExportService(previewService, penumbra, NotificationManager, log, exportTempDir);

        project = new DecalProject();
        project.LoadFromConfig(config);

        mainWindow = new MainWindow(project, previewService, penumbra, config, textureProvider, dataManager, skinMeshResolver);
        debugWindow = new DebugWindow();
        modelEditorWindow = new ModelEditorWindow(project, previewService, penumbra, skinMeshResolver, pluginInterface.UiBuilder.DeviceHandle);

        modExportWindow = new ModExportWindow(project, modExportService, config);

        mainWindow.DebugWindowRef = debugWindow;
        mainWindow.ModelEditorWindowRef = modelEditorWindow;
        mainWindow.ModExportWindowRef = modExportWindow;
        mainWindow.InitializeRequested = InitializeProjectPreview;

        debugServer = new DebugServer(config, project, penumbra, previewService, dataManager, modExportService, skinMeshResolver, textureSwap);
        if (config.HttpEnabled)
            debugServer.Start();

        windowSystem = new WindowSystem("SkinTattoo");
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(debugWindow);
        windowSystem.AddWindow(modelEditorWindow);
        windowSystem.AddWindow(modExportWindow);

        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        commandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = "打开 SkinTattoo 纹身编辑器窗口",
        });

        this.framework = framework;

        log.Information("SkinTattoo loaded. Penumbra={0}", penumbra.IsAvailable);
    }

    private void DrawUi()
    {
        if (ObjectTable.LocalPlayer == null) return;

        windowSystem.Draw();

        var now = DateTime.UtcNow;
        if ((now - lastAutoSave).TotalSeconds >= AutoSaveIntervalSec)
        {
            lastAutoSave = now;
            project.SaveToConfig(config);
            SaveWindowStates();
        }
    }

    /// <summary>
    /// Background: resolve mesh slots + load mesh + composite textures.
    /// Then hop to framework thread for Penumbra IPC + GPU swap.
    /// </summary>
    private Task InitializeProjectPreview()
    {
        var trees = penumbra.GetPlayerTrees();

        return Task.Run(async () =>
        {
            try
            {
                if (trees != null)
                {
                    foreach (var group in project.Groups)
                    {
                        if (group.MeshSlots.Count == 0 && !string.IsNullOrEmpty(group.MtrlGamePath))
                        {
                            var resolution = skinMeshResolver.Resolve(group.MtrlGamePath, trees);
                            if (resolution.Success)
                            {
                                group.MeshSlots = resolution.MeshSlots;
                                group.LiveTreeHash = resolution.LiveTreeHash;
                                group.MeshGamePath = resolution.PrimaryMdlGamePath;
                                group.MeshDiskPath = resolution.PrimaryMdlDiskPath;
                                group.TargetMatIdx = resolution.MeshSlots[0].MatIdx;
                            }
                        }
                    }
                }

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
                if (meshGroup != null)
                    previewService.LoadMeshForGroup(meshGroup);

                var hasLayers = false;
                foreach (var group in project.Groups)
                {
                    if (!string.IsNullOrEmpty(group.DiffuseGamePath) && group.Layers.Count > 0)
                    { hasLayers = true; break; }
                }

                Dictionary<string, string>? redirects = null;
                if (hasLayers)
                    redirects = previewService.BuildPreviewRedirects(project);

                // Penumbra IPC + redraw must run on the framework thread
                await framework.RunOnFrameworkThread(() =>
                {
                    try
                    {
                        previewService.NotifyMeshChanged();
                        if (redirects != null && redirects.Count > 0)
                        {
                            previewService.ApplyPreviewRedirects(project, redirects);
                            modelEditorWindow.MarkTexturesDirty();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[Init] main-thread preview apply failed");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Init] background init failed");
            }
        });
    }

    private void SaveWindowStates() => config.Save();

    private void OpenConfigUi() => mainWindow.OpenSettings();

    private void OpenMainUi() => mainWindow.IsOpen = true;

    private void OnCommand(string command, string args)
    {
        mainWindow.IsOpen = !mainWindow.IsOpen;
    }


    public void Dispose()
    {
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
        meshExtractor.Dispose();

        penumbra.Dispose();
    }
}
