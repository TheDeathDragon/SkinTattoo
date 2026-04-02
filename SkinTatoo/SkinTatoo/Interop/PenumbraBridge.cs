using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace SkinTatoo.Interop;

public class PenumbraBridge : IDisposable
{
    private readonly IPluginLog log;

    private readonly ApiVersion apiVersion;
    private readonly CreateTemporaryCollection createTempCollection;
    private readonly DeleteTemporaryCollection deleteTempCollection;
    private readonly AddTemporaryMod addTempMod;
    private readonly RedrawObject redrawObject;
    private readonly ResolvePlayerPath resolvePlayerPath;

    private Guid collectionId = Guid.Empty;
    private const string Identity = "SkinTatoo";
    private const string CollectionName = "SkinTatoo Preview";
    private const string TempModTag = "SkinTatooDecal";

    public bool IsAvailable { get; private set; }

    public PenumbraBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        apiVersion        = new ApiVersion(pluginInterface);
        createTempCollection = new CreateTemporaryCollection(pluginInterface);
        deleteTempCollection = new DeleteTemporaryCollection(pluginInterface);
        addTempMod        = new AddTemporaryMod(pluginInterface);
        redrawObject      = new RedrawObject(pluginInterface);
        resolvePlayerPath = new ResolvePlayerPath(pluginInterface);

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

    public bool EnsureCollection()
    {
        if (!IsAvailable) return false;
        if (collectionId != Guid.Empty) return true;

        try
        {
            // use the out-param overload that directly returns PenumbraApiEc
            var ec = createTempCollection.Invoke(Identity, CollectionName, out var id);
            if (ec == PenumbraApiEc.Success)
            {
                collectionId = id;
                log.Information("Created Penumbra temp collection: {0}", collectionId);
                return true;
            }
            log.Error("Failed to create temp collection: {0}", ec);
            return false;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to create temp collection");
            return false;
        }
    }

    public bool SetTextureRedirect(string gameTexturePath, string localFilePath)
    {
        if (!EnsureCollection()) return false;

        try
        {
            var paths = new Dictionary<string, string> { { gameTexturePath, localFilePath } };
            var ec = addTempMod.Invoke(TempModTag, collectionId, paths, string.Empty, 99);
            return ec == PenumbraApiEc.Success;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to set texture redirect");
            return false;
        }
    }

    public void RedrawPlayer()
    {
        if (!IsAvailable) return;
        try { redrawObject.Invoke(0, RedrawType.Redraw); }
        catch (Exception ex) { log.Error(ex, "Failed to redraw player"); }
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

    public void Dispose()
    {
        if (collectionId != Guid.Empty)
        {
            try { deleteTempCollection.Invoke(collectionId); }
            catch (Exception ex) { log.Error(ex, "Failed to delete temp collection"); }
            collectionId = Guid.Empty;
        }
    }
}
