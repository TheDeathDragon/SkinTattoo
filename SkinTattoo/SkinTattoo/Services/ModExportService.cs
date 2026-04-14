using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using SkinTattoo.Core;
using SkinTattoo.Http;
using SkinTattoo.Interop;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Services;

/// <summary>Exports a DecalProject to a Penumbra .pmp mod package.</summary>
public class ModExportService : IDisposable
{
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly INotificationManager notifications;
    private readonly IPluginLog log;

    // Must outlive IPC call  -- Penumbra reads async after InstallMod returns
    private readonly string installPmpPath;

    public ModExportService(
        PreviewService previewService,
        PenumbraBridge penumbra,
        INotificationManager notifications,
        IPluginLog log,
        string installTempDir)
    {
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.notifications = notifications;
        this.log = log;

        Directory.CreateDirectory(installTempDir);
        installPmpPath = Path.Combine(installTempDir, "install_pending.pmp");

        TryDeleteInstallPmp();
    }

    public void Dispose()
    {
        TryDeleteInstallPmp();
    }

    private void TryDeleteInstallPmp()
    {
        try
        {
            if (File.Exists(installPmpPath))
                File.Delete(installPmpPath);
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[ModExport] Cleanup failed: {ex.Message}");
        }
    }

    private void Notify(bool success, string title, string content)
    {
        try
        {
            notifications.AddNotification(new Notification
            {
                Title = title,
                Content = content,
                Type = success ? NotificationType.Success : NotificationType.Error,
            });
        }
        catch (Exception ex)
        {
            DebugServer.AppendLog($"[ModExport] Notification failed: {ex.Message}");
        }
    }

    /// <summary>Returns null on success, error message on failure.</summary>
    public string? Validate(ModExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ModName))
            return Strings.T("error.no_mod_name");
        if (options.SelectedGroups.Count == 0)
            return Strings.T("error.no_group_selected");

        bool anyVisible = options.SelectedGroups
            .Any(g => g.Layers.Any(l => l.IsVisible && !string.IsNullOrEmpty(l.ImagePath)));
        if (!anyVisible)
            return Strings.T("error.no_visible_layer");

        if (options.Target == ExportTarget.InstallToPenumbra && !penumbra.IsAvailable)
            return Strings.T("error.penumbra_unavailable");

        if (options.Target == ExportTarget.LocalPmp && string.IsNullOrWhiteSpace(options.OutputPmpPath))
            return Strings.T("error.no_output_path");

        return null;
    }

    /// <summary>Build and optionally install the mod. Synchronous  -- call from background thread.</summary>
    public ModExportResult Export(ModExportOptions options)
    {
        var err = Validate(options);
        if (err != null)
        {
            Notify(false, Strings.T("notify.export.failed"), err);
            return new ModExportResult { Success = false, Message = err };
        }

        var stagingDir = Path.Combine(Path.GetTempPath(),
            $"SkinTattoo_Export_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        try
        {
            var sharedRedirects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var groupExports = new List<GroupExport>();
            int success = 0, skipped = 0;

            foreach (var group in options.SelectedGroups)
            {
                try
                {
                    var groupRedirects = previewService.CompositeForExport(group, stagingDir);
                    if (groupRedirects.Count == 0)
                    {
                        skipped++;
                        continue;
                    }

                    // Shared assets (patched skin_ct.shpk) always load with the mod,
                    // not per-option — otherwise disabling one decal group would drop
                    // the shader that other enabled groups still need.
                    var groupFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (gp, rp) in groupRedirects)
                    {
                        if (IsSharedAsset(gp))
                            sharedRedirects[gp] = rp;
                        else
                            groupFiles[gp] = rp;
                    }

                    groupExports.Add(new GroupExport(group.Name, groupFiles));
                    success++;
                }
                catch (Exception ex)
                {
                    log.Error(ex, $"[ModExport] Group failed: {group.Name}");
                    DebugServer.AppendLog($"[ModExport] Group failed: {group.Name}  -- {ex.Message}");
                    skipped++;
                }
            }

            if (success == 0)
            {
                var msg = Strings.T("error.all_groups_skipped", skipped);
                Notify(false, Strings.T("notify.export.failed"), msg);
                return new ModExportResult
                {
                    Success = false,
                    Message = msg,
                    SkippedGroups = skipped,
                };
            }

            var pmpPath = options.Target == ExportTarget.LocalPmp
                ? options.OutputPmpPath!
                : installPmpPath;

            PmpPackageWriter.Pack(stagingDir, options, sharedRedirects, groupExports, pmpPath);

            if (options.Target == ExportTarget.InstallToPenumbra)
            {
                var ec = penumbra.InstallMod(pmpPath);
                if (ec != PenumbraApiEc.Success)
                {
                    var failMsg = Strings.T("error.penumbra_install_failed", ec);
                    Notify(false, Strings.T("notify.export.failed"), failMsg);
                    return new ModExportResult
                    {
                        Success = false,
                        Message = failMsg,
                        SuccessGroups = success,
                        SkippedGroups = skipped,
                    };
                }
            }

            var summary = skipped > 0
                ? Strings.T("export_summary.success_skip", success, skipped)
                : Strings.T("export_summary.success_only", success);
            var notifyTitle = options.Target == ExportTarget.LocalPmp
                ? Strings.T("notify.export.success_local")
                : Strings.T("notify.export.success_penumbra");
            var notifyContent = options.Target == ExportTarget.LocalPmp
                ? $"{options.ModName}：{summary}\n{pmpPath}"
                : $"{options.ModName}：{summary}";
            Notify(true, notifyTitle, notifyContent);

            return new ModExportResult
            {
                Success = true,
                PmpPath = options.Target == ExportTarget.LocalPmp ? pmpPath : null,
                Message = $"{notifyTitle}（{summary}）",
                SuccessGroups = success,
                SkippedGroups = skipped,
            };
        }
        catch (Exception ex)
        {
            log.Error(ex, "[ModExport] Export failed");
            var msg = Strings.T("error.export_exception", ex.Message);
            Notify(false, Strings.T("notify.export.failed"), msg);
            return new ModExportResult { Success = false, Message = msg };
        }
        finally
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
            catch (Exception ex) { DebugServer.AppendLog($"[ModExport] Staging cleanup failed: {ex.Message}"); }
        }
    }

    // shader packages must stay always-on so the decal groups that reference
    // them keep rendering correctly when the user toggles individual groups.
    private static bool IsSharedAsset(string gamePath)
        => gamePath.Replace('\\', '/').StartsWith("shader/", StringComparison.OrdinalIgnoreCase);
}

internal sealed record GroupExport(string Name, Dictionary<string, string> Files);
