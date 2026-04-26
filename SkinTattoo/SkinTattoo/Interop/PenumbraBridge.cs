using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace SkinTattoo.Interop;

public class PenumbraBridge : IDisposable
{
    private readonly IPluginLog log;

    private readonly ApiVersion apiVersion;
    private readonly AddTemporaryModAll addTempModAll;
    private readonly RemoveTemporaryModAll removeTempModAll;
    private readonly RedrawObject redrawObject;
    private readonly ResolvePlayerPath resolvePlayerPath;
    private readonly GetPlayerResourcePaths getPlayerResourcePaths;
    private readonly GetPlayerResourceTrees getPlayerResourceTrees;
    private readonly Penumbra.Api.IpcSubscribers.InstallMod installMod;
    private readonly GetModDirectory getModDirectory;

    private const string TempModTag = "SkinTattooTemp";

    public bool IsAvailable { get; private set; }
    public bool HasActiveRedirects { get; private set; }

    public PenumbraBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        apiVersion = new ApiVersion(pluginInterface);
        addTempModAll = new AddTemporaryModAll(pluginInterface);
        removeTempModAll = new RemoveTemporaryModAll(pluginInterface);
        redrawObject = new RedrawObject(pluginInterface);
        resolvePlayerPath = new ResolvePlayerPath(pluginInterface);
        getPlayerResourcePaths = new GetPlayerResourcePaths(pluginInterface);
        getPlayerResourceTrees = new GetPlayerResourceTrees(pluginInterface);
        installMod = new Penumbra.Api.IpcSubscribers.InstallMod(pluginInterface);
        getModDirectory = new GetModDirectory(pluginInterface);

        CheckAvailability();
    }

    private void CheckAvailability()
    {
        try
        {
            var version = apiVersion.Invoke();
            IsAvailable = true;
            log.Information("Penumbra IPC available (v{0}.{1}).", version.Breaking, version.Features);
        }
        catch
        {
            IsAvailable = false;
            log.Warning("Penumbra IPC not available.");
        }
    }

    public bool SetTextureRedirect(string gameTexturePath, string localFilePath)
    {
        return SetTextureRedirects(new Dictionary<string, string> { { gameTexturePath, localFilePath } });
    }

    public bool SetTextureRedirects(Dictionary<string, string> redirects)
    {
        if (!IsAvailable || redirects.Count == 0) return false;

        try
        {
            var ec = addTempModAll.Invoke(TempModTag, redirects, string.Empty, 99);
            Http.DebugServer.AppendLog($"[PenumbraBridge] AddTemporaryModAll ({redirects.Count} paths): {ec}");
            if (ec == PenumbraApiEc.Success) HasActiveRedirects = true;
            return ec == PenumbraApiEc.Success;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to set texture redirects");
            return false;
        }
    }

    public void ClearRedirect()
    {
        if (!IsAvailable) return;
        try
        {
            removeTempModAll.Invoke(TempModTag, 99);
            HasActiveRedirects = false;
            Http.DebugServer.AppendLog("[PenumbraBridge] Cleared temp mod");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to clear temp mod");
        }
    }

    public void RedrawPlayer()
    {
        if (!IsAvailable) return;
        try { redrawObject.Invoke(0, RedrawType.Redraw); }
        catch (Exception ex) { log.Error(ex, "Failed to redraw player"); }
    }

    /// <summary>Install a .pmp mod package into Penumbra.</summary>
    public PenumbraApiEc InstallMod(string pmpPath)
    {
        if (!IsAvailable) return PenumbraApiEc.SystemDisposed;
        try
        {
            var ec = installMod.Invoke(pmpPath);
            Http.DebugServer.AppendLog($"[PenumbraBridge] InstallMod({pmpPath}) -> {ec}");
            return ec;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to install mod");
            return PenumbraApiEc.UnknownError;
        }
    }

    /// <summary>Returns Penumbra's configured mod root directory, or null if unavailable.</summary>
    public string? GetModDirectory()
    {
        if (!IsAvailable) return null;
        try
        {
            var dir = getModDirectory.Invoke();
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to get Penumbra mod directory");
            return null;
        }
    }

    public string? ResolvePlayer(string gamePath)
    {
        if (!IsAvailable) return null;
        try
        {
            var resolved = resolvePlayerPath.Invoke(gamePath);
            return string.IsNullOrEmpty(resolved) ? null : resolved;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to resolve player path");
            return null;
        }
    }

    public Dictionary<ushort, Dictionary<string, HashSet<string>>>? GetPlayerResources()
    {
        if (!IsAvailable) return null;
        try { return getPlayerResourcePaths.Invoke(); }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to get player resource paths");
            return null;
        }
    }

    public Dictionary<ushort, ResourceTreeDto>? GetPlayerTrees(bool withUiData = true)
    {
        if (!IsAvailable) return null;
        try { return getPlayerResourceTrees.Invoke(withUiData: withUiData); }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to get player resource trees");
            return null;
        }
    }

    public void Dispose()
    {
        ClearRedirect();
        RedrawPlayer();
    }
}
