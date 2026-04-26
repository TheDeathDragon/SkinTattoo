using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Collections.Frozen;
using System.Reflection;
using SkinTattoo.Core;
using SkinTattoo.Gui;
using SkinTattoo.Http;
using SkinTattoo.Interop;
using SkinTattoo.Mesh;
using SkinTattoo.Services;
using SkinTattoo.Services.Localization;

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
    private readonly LocalizationManager localization;
    private readonly PenumbraBridge penumbra;
    private readonly TextureSwapService textureSwap;
    private readonly EmissiveCBufferHook emissiveHook;
    private readonly MeshExtractor meshExtractor;
    private readonly SkinMeshResolver skinMeshResolver;
    private readonly DecalImageLoader imageLoader;
    private readonly LibraryService libraryService;
    private readonly PreviewService previewService;
    private readonly ModExportService modExportService;
    private readonly ChangelogService changelogService;
    private readonly DebugServer debugServer;
    private readonly DecalProject project;
    private readonly WindowSystem windowSystem;
    private readonly MainWindow mainWindow;
    private readonly DebugWindow debugWindow;
    private readonly PerformanceWindow performanceWindow;
    private readonly ModelEditorWindow modelEditorWindow;
    private readonly ModExportWindow modExportWindow;
    private readonly LibraryWindow libraryWindow;

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
        IGameInteropProvider gameInterop,
        IKeyState keyState)
    {
        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Initialize(pluginInterface);

        localization = InitializeLocalization(pluginInterface, config, log);

        penumbra = new PenumbraBridge(pluginInterface, log);
        textureSwap = new TextureSwapService(objectTable, log);
        emissiveHook = new EmissiveCBufferHook(gameInterop, log);

        meshExtractor = new MeshExtractor(dataManager, log);
        skinMeshResolver = new SkinMeshResolver(meshExtractor);
        imageLoader = new DecalImageLoader(log, dataManager);

        libraryService = new LibraryService(log, imageLoader, pluginInterface.GetPluginConfigDirectory());

        var outputDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "preview");
        previewService = new PreviewService(
            meshExtractor, imageLoader,
            penumbra, textureSwap, emissiveHook, log, config, outputDir);

        var exportTempDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "export_temp");
        modExportService = new ModExportService(previewService, penumbra, NotificationManager, log, exportTempDir);

        project = new DecalProject();
        project.LoadFromConfig(config);
        project.ReconcileLibraryRefs(libraryService);

        changelogService = new ChangelogService(log);

        mainWindow = new MainWindow(project, previewService, penumbra, config, textureProvider, dataManager, skinMeshResolver, changelogService, keyState, libraryService, imageLoader);
        debugWindow = new DebugWindow();
        performanceWindow = new PerformanceWindow(previewService, emissiveHook, libraryService, project)
        {
            HistoryCountsProvider = () => (mainWindow.UndoHistoryCount, mainWindow.RedoHistoryCount),
        };
        modelEditorWindow = new ModelEditorWindow(project, previewService, penumbra, skinMeshResolver, pluginInterface.UiBuilder.DeviceHandle);

        modExportWindow = new ModExportWindow(project, modExportService, config);
        libraryWindow = new LibraryWindow(libraryService, textureProvider, config);

        mainWindow.DebugWindowRef = debugWindow;
        mainWindow.PerformanceWindowRef = performanceWindow;
        mainWindow.ModelEditorWindowRef = modelEditorWindow;
        mainWindow.ModExportWindowRef = modExportWindow;
        mainWindow.LibraryWindowRef = libraryWindow;
        mainWindow.InitializeRequested = InitializeProjectPreview;

        debugServer = new DebugServer(config, project, penumbra, previewService, dataManager, modExportService, skinMeshResolver, textureSwap);
        if (config.HttpEnabled)
            debugServer.Start();

        windowSystem = new WindowSystem("SkinTattoo");
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(debugWindow);
        windowSystem.AddWindow(performanceWindow);
        windowSystem.AddWindow(modelEditorWindow);
        windowSystem.AddWindow(modExportWindow);
        windowSystem.AddWindow(libraryWindow);

        pluginInterface.UiBuilder.Draw += DrawUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        ApplyHideWithGameUi();

        commandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = Strings.T("command.help"),
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
                        // MeshSlots + MeshGamePath + TargetMatIdx aren't persisted in
                        // SavedTargetGroup, so we re-run the resolver on load to
                        // rebuild them. Without per-slot matIdx, ExtractAndMerge on
                        // the raw mdl list falls back to MaterialIdx==0 per mdl,
                        // which for Bibo+ style multi-mdl groups only keeps one
                        // body region (user sees just the upper body UV).
                        //
                        // Preserve manually added MeshDiskPaths that aren't in the
                        // resolver's result so the user doesn't lose extras.
                        if (group.MeshSlots.Count > 0) continue;
                        if (string.IsNullOrEmpty(group.MtrlGamePath)) continue;

                        var resolution = skinMeshResolver.Resolve(group.MtrlGamePath, trees);
                        if (!resolution.Success) continue;

                        var resolvedPaths = new System.Collections.Generic.HashSet<string>(
                            resolution.MeshSlots.Select(s => s.DiskPath ?? s.GamePath),
                            StringComparer.OrdinalIgnoreCase);
                        var manualExtras = group.MeshDiskPaths
                            .Where(p => !resolvedPaths.Contains(p))
                            .ToList();
                        if (!string.IsNullOrEmpty(group.MeshDiskPath)
                            && !resolvedPaths.Contains(group.MeshDiskPath))
                            manualExtras.Insert(0, group.MeshDiskPath);

                        group.MeshSlots = resolution.MeshSlots;
                        group.LiveTreeHash = resolution.LiveTreeHash;
                        group.MeshGamePath = resolution.PrimaryMdlGamePath;
                        group.MeshDiskPath = resolution.PrimaryMdlDiskPath;
                        group.TargetMatIdx = resolution.MeshSlots[0].MatIdx;
                        group.MeshDiskPaths.Clear();
                        foreach (var extra in manualExtras)
                            if (!string.IsNullOrEmpty(extra) && !group.MeshDiskPaths.Contains(extra))
                                group.MeshDiskPaths.Add(extra);
                    }
                }

                // Prefer the selected group so the canvas wireframe + 3D editor
                // match without the user having to click "reload model" manually.
                TargetGroup? meshGroup = null;
                var selected = project.SelectedGroup;
                if (selected != null
                    && (selected.MeshSlots.Count > 0 || selected.AllMeshPaths.Count > 0))
                {
                    meshGroup = selected;
                }
                else
                {
                    foreach (var group in project.Groups)
                    {
                        if (group.MeshSlots.Count > 0 || group.AllMeshPaths.Count > 0)
                        {
                            meshGroup = group;
                            break;
                        }
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

                // Respect the global toggle: when disabled, skip Penumbra redirect
                // build + apply so opening the editor doesn't auto-install decals.
                // Mesh load above still runs so the 3D editor has geometry to show.
                Dictionary<string, string>? redirects = null;
                if (hasLayers && config.PluginEnabled)
                    redirects = previewService.BuildPreviewRedirects(project);

                await framework.RunOnFrameworkThread(() =>
                {
                    try
                    {
                        previewService.NotifyMeshChanged();
                        if (config.PluginEnabled && redirects != null && redirects.Count > 0)
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

    public void ApplyHideWithGameUi() => ApplyHideWithGameUi(config);

    public static void ApplyHideWithGameUi(Configuration config)
    {
        // DisableUserUiHide=true means the plugin window IGNORES the user toggling
        // the game UI, keeping it visible.
        PluginInterface.UiBuilder.DisableUserUiHide = !config.HideWithGameUi;
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
        localization.Dispose();
    }

    private static LocalizationManager InitializeLocalization(IDalamudPluginInterface pi, Configuration cfg, IPluginLog log)
    {
        var manager = new LocalizationManager();
        Strings.Attach(manager);

        var supported = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["en"] = "English",
            ["zh-CN"] = "简体中文",
        }.ToFrozenDictionary(System.StringComparer.Ordinal);

        var embedded = new EmbeddedLocalizationSource(Assembly.GetExecutingAssembly(), "SkinTattoo.Localization");
        var overlayDir = Path.Combine(pi.GetPluginConfigDirectory(), "Localization");
        var overlay = new FileLocalizationSource(overlayDir);
        var source = new LayeredLocalizationSource(overlay, embedded);

        var options = new LocalizationOptions
        {
            SupportedLanguages = supported,
            DefaultLanguage = "en",
            FileNameResolver = lang => $"{lang}.json",
            Source = source,
            Parser = new KeyValueJsonLocalizationParser(),
            FallbackResolver = _ => ["en"],
            EnableHotReload = true,
            LoggerTag = "SkinTattoo.I18n",
        };

        try
        {
            manager.Configure(options, cfg.Language);
            log.Information("Localization: current={0}", manager.CurrentLanguage);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Localization init failed");
        }

        return manager;
    }
}
