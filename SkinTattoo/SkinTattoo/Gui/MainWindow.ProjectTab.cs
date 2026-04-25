using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public partial class MainWindow
{
    private int projectAutoSavePauseCount;

    private static bool IconTextButton(string id, FontAwesomeIcon icon, string text)
    {
        var textSize = ImGui.CalcTextSize(text);
        ImGui.PushFont(UiBuilder.IconFont);
        var iconText = icon.ToIconString();
        var iconSize = ImGui.CalcTextSize(iconText);
        ImGui.PopFont();

        var framePad = ImGui.GetStyle().FramePadding;
        var size = new Vector2(framePad.X * 2 + iconSize.X + 6f + textSize.X, ImGui.GetFrameHeight());
        var cursor = ImGui.GetCursorScreenPos();
        var clicked = ImGui.Button($"##{id}", size);

        var color = ImGui.GetColorU32(ImGuiCol.Text);
        var iconPos = new Vector2(cursor.X + framePad.X, cursor.Y + (size.Y - iconSize.Y) * 0.5f);
        var textPos = new Vector2(iconPos.X + iconSize.X + 6f, cursor.Y + (size.Y - textSize.Y) * 0.5f);
        var drawList = ImGui.GetWindowDrawList();
        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(iconPos, color, iconText);
        ImGui.PopFont();
        drawList.AddText(textPos, color, text);

        return clicked;
    }

    private static float CalcIconTextButtonWidth(FontAwesomeIcon icon, string text)
    {
        var textSize = ImGui.CalcTextSize(text);
        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(icon.ToIconString());
        ImGui.PopFont();
        var framePad = ImGui.GetStyle().FramePadding;
        return framePad.X * 2 + iconSize.X + 6f + textSize.X;
    }

    private void MarkProjectListDirty() => projectListCacheDirty = true;

    private void RefreshProjectListCache(bool force = false)
    {
        if (!force && !projectListCacheDirty)
            return;

        cachedProjectList = projectFileService.ListProjects().ToList();
        if (selectedProjectRow >= cachedProjectList.Count)
            selectedProjectRow = cachedProjectList.Count - 1;
        projectListCacheDirty = false;
    }

    private void OnProjectTabOpened()
    {
        RefreshProjectListCache(force: true);

        if (!string.IsNullOrWhiteSpace(config.LastProjectPath))
        {
            var normalizedLast = Path.GetFullPath(config.LastProjectPath);
            var match = cachedProjectList.FirstOrDefault(p =>
                Path.GetFullPath(p.ProjectPath).Equals(normalizedLast, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                currentProjectPath = match.ProjectPath;
        }

        if (!string.IsNullOrWhiteSpace(currentProjectPath)
            && cachedProjectList.All(p => !ProjectPathEquals(p.ProjectPath, currentProjectPath)))
        {
            currentProjectPath = null;
            if (!string.IsNullOrWhiteSpace(config.LastProjectPath))
            {
                config.LastProjectPath = null;
                config.Save();
            }
        }
    }

    private void BeginInlineRename(int row)
    {
        if (row < 0 || row >= cachedProjectList.Count)
            return;

        projectInlineRenameRow = row;
        projectInlineRenameBuffer = cachedProjectList[row].Name;
    }

    private void CommitInlineRename(int row)
    {
        if (row < 0 || row >= cachedProjectList.Count)
            return;

        var target = cachedProjectList[row];
        if (projectFileService.RenameProject(target.ProjectPath, projectInlineRenameBuffer, out var renamedPath)
            && !string.IsNullOrWhiteSpace(renamedPath))
        {
            if (!string.IsNullOrWhiteSpace(currentProjectPath)
                && currentProjectPath.Equals(target.ProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                currentProjectPath = renamedPath;
                config.LastProjectPath = renamedPath;
                config.Save();
            }

            MarkProjectListDirty();
            RefreshProjectListCache(force: true);
            NotifyProject(true, Strings.T("project.notify.rename.title"), Strings.T("project.notify.rename.content"));
        }
        else
        {
            NotifyProject(false, Strings.T("project.notify.rename_failed.title"), Strings.T("project.notify.rename_failed.content"));
        }

        projectInlineRenameRow = -1;
        projectInlineRenameBuffer = string.Empty;
    }

    private void CancelInlineRename()
    {
        projectInlineRenameRow = -1;
        projectInlineRenameBuffer = string.Empty;
    }

    private void DrawProjectTab()
    {
        BootstrapInitialProjectIfNeeded();
        RefreshProjectListCache();

        using var scroll = ImRaii.Child("##ProjectScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) return;

        DrawProjectActions();
        ImGui.Separator();

        // 6 columns: [Open/Badge] [Name] [Groups] [Layers] [Modified] [Export/Delete]
        var actionColW = ImGui.GetFrameHeight() * 2f + ImGui.GetStyle().ItemSpacing.X + 12f;

        using var tablePadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(8f, 8f));
        if (ImGui.BeginTable("##ProjectTable", 6,
                ImGuiTableFlags.RowBg
                | ImGuiTableFlags.BordersOuter
                | ImGuiTableFlags.BordersInnerV
                | ImGuiTableFlags.Resizable
                | ImGuiTableFlags.ScrollY
                | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(string.Empty,                      ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 70f);
            ImGui.TableSetupColumn(Strings.T("project.table.name"),   ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn(Strings.T("project.table.groups"), ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 52f);
            ImGui.TableSetupColumn(Strings.T("project.table.layers"), ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 52f);
            ImGui.TableSetupColumn(Strings.T("project.table.modified"), ImGuiTableColumnFlags.WidthFixed, 160f);
            ImGui.TableSetupColumn(string.Empty,                      ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, actionColW);

            ImGui.TableHeadersRow();

            for (var i = 0; i < cachedProjectList.Count; i++)
            {
                var item = cachedProjectList[i];
                var isLoaded = !string.IsNullOrWhiteSpace(currentProjectPath)
                    && ProjectPathEquals(currentProjectPath, item.ProjectPath);

                ImGui.TableNextRow(ImGuiTableRowFlags.None, 38f);

                // Tint entire row green for the loaded project
                if (isLoaded)
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,
                        ImGui.GetColorU32(new Vector4(0.10f, 0.32f, 0.12f, 0.35f)));

                // Col 0: Open button or "loaded" badge
                ImGui.TableNextColumn();
                if (isLoaded)
                    DrawLoadedBadge();
                else
                {
                    if (IconTextButton("projectLoad" + i, FontAwesomeIcon.FolderOpen, Strings.T("project.button.load")))
                        LoadProjectFromPath(item.ProjectPath);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(Strings.T("project.tooltip.load"));
                }

                // Col 1: Name (or inline rename input)
                ImGui.TableNextColumn();
                if (projectInlineRenameRow == i)
                {
                    ImGui.SetNextItemWidth(-1);
                    var commit = ImGui.InputText("##projInlineRename" + i, ref projectInlineRenameBuffer, 256,
                        ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                    if (commit)
                        CommitInlineRename(i);
                    else if (ImGui.IsItemActive() && ImGui.IsKeyPressed(ImGuiKey.Escape))
                        CancelInlineRename();
                }
                else
                {
                    if (isLoaded)
                        ImGui.TextColored(new Vector4(0.55f, 1.0f, 0.60f, 1f), item.Name);
                    else
                        ImGui.TextUnformatted(item.Name);

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.TextInput);
                        ImGui.SetTooltip(Strings.T("project.tooltip.rename_inline"));
                    }
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        BeginInlineRename(i);
                }

                // Col 2: Group count
                ImGui.TableNextColumn();
                DrawCenteredText(item.GroupCount.ToString());

                // Col 3: Layer count
                ImGui.TableNextColumn();
                DrawCenteredText(item.LayerCount.ToString());

                // Col 4: Last modified
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

                // Col 5: inline Export/Delete actions
                ImGui.TableNextColumn();
                if (UiHelpers.SquareIconButton(7000 + i, FontAwesomeIcon.FileExport))
                    OpenExportOptionsForRow(i);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Strings.T("project.tooltip.export_row"));

                ImGui.SameLine();
                using (ImRaii.Disabled(!IsDeleteModifierHeld()))
                {
                    if (UiHelpers.SquareIconButton(8000 + i, FontAwesomeIcon.Trash))
                        TryBeginProjectDelete(i);
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(IsDeleteModifierHeld()
                        ? Strings.T("project.tooltip.delete")
                        : Strings.T("project.tooltip.delete_requires_modifier"));
            }

            ImGui.EndTable();
        }

        DrawProjectDeleteConfirmModal();
        DrawProjectExportOptionsModal();
        fileDialog.Draw();
    }

    private static bool ProjectPathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return left.Equals(right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void DrawLoadedBadge()
    {
        var badgeText = Strings.T("project.badge.loaded");
        var textSize = ImGui.CalcTextSize(badgeText);
        var framePad = ImGui.GetStyle().FramePadding;
        var badgeSize = new Vector2(textSize.X + framePad.X * 2f, ImGui.GetFrameHeight());
        var badgeMin = ImGui.GetCursorScreenPos();
        var badgeMax = badgeMin + badgeSize;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(badgeMin, badgeMax, ImGui.GetColorU32(new Vector4(0.14f, 0.42f, 0.20f, 0.95f)), 4f);
        drawList.AddRect(badgeMin, badgeMax, ImGui.GetColorU32(new Vector4(0.26f, 0.74f, 0.36f, 1f)), 4f);
        drawList.AddText(
            new Vector2(badgeMin.X + framePad.X, badgeMin.Y + (badgeSize.Y - textSize.Y) * 0.5f),
            ImGui.GetColorU32(new Vector4(0.90f, 1.00f, 0.90f, 1f)),
            badgeText);
        ImGui.Dummy(badgeSize);
    }

    private void TryBeginProjectDelete(int row)
    {
        if (row < 0 || row >= cachedProjectList.Count)
            return;
        if (!IsDeleteModifierHeld())
            return;

        pendingDeleteProjectRow = row;
        openProjectDeleteConfirmModal = true;
    }

    private void DrawProjectDeleteConfirmModal()
    {
        if (openProjectDeleteConfirmModal)
        {
            ImGui.OpenPopup("##project_delete_confirm");
            openProjectDeleteConfirmModal = false;
        }

        using var popup = ImRaii.PopupModal("##project_delete_confirm",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar);
        if (!popup.Success) return;

        var hasTarget = pendingDeleteProjectRow >= 0 && pendingDeleteProjectRow < cachedProjectList.Count;
        var targetName = hasTarget ? cachedProjectList[pendingDeleteProjectRow].Name : string.Empty;
        ImGui.TextWrapped(Strings.T("project.delete.confirm", targetName));
        ImGui.Spacing();

        if (ImGui.Button(Strings.T("button.confirm"), new Vector2(120, 0)))
        {
            if (hasTarget)
            {
                var target = cachedProjectList[pendingDeleteProjectRow];
                if (projectFileService.DeleteProject(target.ProjectPath))
                {
                    if (!string.IsNullOrWhiteSpace(currentProjectPath)
                        && currentProjectPath.Equals(target.ProjectPath, StringComparison.OrdinalIgnoreCase))
                    {
                        PauseProjectAutoSave();
                        try
                        {
                            project.Groups.Clear();
                            project.SelectedGroupIndex = -1;
                            isReplayingHistory = false;
                            OnHistoryReplayed(false);
                            currentProjectPath = null;
                            config.LastProjectPath = null;
                            config.Save();
                        }
                        finally
                        {
                            ResumeProjectAutoSave();
                        }
                    }

                    selectedProjectRow = -1;
                    MarkProjectListDirty();
                    RefreshProjectListCache(force: true);
                    NotifyProject(true, Strings.T("project.notify.delete.title"), Strings.T("project.notify.delete.content", target.Name));
                }
                else
                {
                    NotifyProject(false, Strings.T("project.notify.delete_failed.title"), Strings.T("project.notify.delete_failed.content"));
                }
            }

            pendingDeleteProjectRow = -1;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.cancel"), new Vector2(120, 0)))
        {
            pendingDeleteProjectRow = -1;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawProjectActions()
    {
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(10, 6)))
        {
            var newLabel = Strings.T("project.button.new");
            var importLabel = Strings.T("project.button.import");
            var itemSpacingX = ImGui.GetStyle().ItemSpacing.X;
            var btnGroupW = CalcIconTextButtonWidth(FontAwesomeIcon.Plus, newLabel)
                           + itemSpacingX
                           + CalcIconTextButtonWidth(FontAwesomeIcon.FileImport, importLabel);

            ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - btnGroupW);

            if (IconTextButton("projectNew", FontAwesomeIcon.Plus, newLabel))
                CreateNewProject();
            ImGui.SameLine();
            if (IconTextButton("projectImport", FontAwesomeIcon.FileImport, importLabel))
                OpenImportProjectDialog();
        }
    }

    private void CreateNewProject()
    {
        PauseProjectAutoSave();
        try
        {
            project.Groups.Clear();
            project.SelectedGroupIndex = -1;
            isReplayingHistory = false;
            OnHistoryReplayed(false);
            var newName = GetNextUntitledProjectName();
            var newPath = projectFileService.GetUniqueProjectPath(newName);
            var snapshot = project.CreateSnapshot();
            if (projectFileService.SaveProject(newPath, newName, snapshot))
            {
                currentProjectPath = newPath;
                config.LastProjectPath = newPath;
                config.Save();
            }
            else
            {
                currentProjectPath = null;
                config.LastProjectPath = null;
                config.Save();
            }
            selectedProjectRow = -1;
            MarkProjectListDirty();
            RefreshProjectListCache(force: true);
            NotifyProject(true, Strings.T("project.notify.new.title"), Strings.T("project.notify.new.content"));
        }
        finally
        {
            ResumeProjectAutoSave();
        }
    }

    private static void DrawCenteredText(string text)
    {
        var textW = ImGui.CalcTextSize(text).X;
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (avail - textW) * 0.5f));
        ImGui.TextUnformatted(text);
    }

    private void OpenExportOptionsForRow(int row)
    {
        if (row < 0 || row >= cachedProjectList.Count)
            return;

        pendingExportProjectRow = row;
        openProjectExportOptionsModal = true;
    }

    private void BootstrapInitialProjectIfNeeded()
    {
        if (projectTabFirstOpenHandled)
            return;

        projectTabFirstOpenHandled = true;

        var existing = projectFileService.ListProjects();
        if (existing.Count > 0)
            return;

        if (config.TargetGroups.Count == 0)
            return;

        var snapshot = new SavedProjectSnapshot
        {
            SelectedGroupIndex = config.SelectedGroupIndex,
            TargetGroups = new System.Collections.Generic.List<SavedTargetGroup>(config.TargetGroups),
        };

        var path = projectFileService.GetUniqueProjectPath("Untitled 1");
        if (projectFileService.SaveProject(path, "Untitled 1", snapshot))
        {
            currentProjectPath = path;
            MarkProjectListDirty();
        }
    }

    private void LoadProjectFromPath(string path)
    {
        PauseProjectAutoSave();
        try
        {
            var loaded = projectFileService.LoadProject(path);
            if (loaded == null)
                return;

            isReplayingHistory = true;
            try
            {
                project.ApplySnapshot(loaded.Snapshot, library);
            }
            finally
            {
                isReplayingHistory = false;
            }

            currentProjectPath = loaded.ProjectPath;
            config.LastProjectPath = currentProjectPath;
            config.Save();
            if (library != null)
                project.ReconcileLibraryRefs(library);
            OnHistoryReplayed(false);
            var hasPreviewableLayers = project.Groups.Any(g =>
                !string.IsNullOrEmpty(g.DiffuseGamePath) && g.Layers.Count > 0);

            if (!hasPreviewableLayers)
            {
                previewService.ClearTextureCache();
                previewService.ResetSwapState();
                penumbra.ClearRedirect();
                penumbra.RedrawPlayer();
            }
            else
            {
                var selectedGroup = project.SelectedGroup;
                if (selectedGroup != null && !string.IsNullOrEmpty(selectedGroup.MtrlGamePath))
                    previewService.ReResolveAndReloadMesh(selectedGroup, skinMeshResolver);
                penumbra.RedrawPlayer();
                MarkPreviewDirty(immediate: true);
                TriggerPreview();
            }
            MarkProjectListDirty();
            NotifyProject(true, Strings.T("project.notify.load.title"), Strings.T("project.notify.load.content", loaded.ProjectName));
        }
        finally
        {
            ResumeProjectAutoSave();
        }
    }

    private void OpenImportProjectDialog()
    {
        fileDialog.OpenFileDialog(
            Strings.T("project.dialog.import"),
            "Project JSON{.json}",
            (ok, paths) =>
            {
                if (!ok || paths.Count == 0)
                    return;

                var src = paths[0];
                if (!File.Exists(src))
                    return;

                if (projectFileService.ImportProject(src, out var importedPath) && !string.IsNullOrWhiteSpace(importedPath))
                {
                    var importedName = Path.GetFileName(Path.GetDirectoryName(importedPath) ?? importedPath);
                    MarkProjectListDirty();
                    NotifyProject(true, Strings.T("project.notify.import.title"), Strings.T("project.notify.import.content", importedName));
                }
                else
                {
                    NotifyProject(false, Strings.T("project.notify.import_failed.title"), Strings.T("project.notify.import_failed.content"));
                }
            },
            1,
            config.LastImageDir,
            false);
    }

    private void ExportProjectRowToPath(int row, string path)
    {
        if (row < 0 || row >= cachedProjectList.Count || string.IsNullOrWhiteSpace(path))
            return;

        var loaded = projectFileService.LoadProject(cachedProjectList[row].ProjectPath);
        if (loaded == null)
        {
            NotifyProject(false, Strings.T("project.notify.export_failed.title"), Strings.T("project.notify.export_failed.content"));
            return;
        }

        var exportPath = Path.GetFullPath(path);
        var exportName = Path.GetFileNameWithoutExtension(exportPath);
        if (exportName.EndsWith(".proj", StringComparison.OrdinalIgnoreCase))
            exportName = exportName[..^5];
        if (string.IsNullOrWhiteSpace(exportName))
            exportName = loaded.ProjectName;

        if (projectFileService.SaveProject(exportPath, exportName, loaded.Snapshot, exportIncludeImages))
            NotifyProject(true, Strings.T("project.notify.export.title"), Strings.T("project.notify.export_with_path.content", exportPath));
        else
            NotifyProject(false, Strings.T("project.notify.export_failed.title"), Strings.T("project.notify.export_failed.content"));
    }

    private static string GetDownloadDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        return Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var n = 2;
        while (true)
        {
            var candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(candidate))
                return candidate;
            n++;
        }
    }

    private void DrawProjectExportOptionsModal()
    {
        if (openProjectExportOptionsModal)
        {
            ImGui.OpenPopup("##project_export_options_modal");
            openProjectExportOptionsModal = false;
        }

        using var popup = ImRaii.PopupModal("##project_export_options_modal",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar);
        if (!popup.Success) return;

        ImGui.TextUnformatted(Strings.T("project.export.options.title"));
        ImGui.Checkbox(Strings.T("project.export.options.include_images"), ref exportIncludeImages);
        ImGui.Spacing();

        if (ImGui.Button(Strings.T("button.confirm"), new Vector2(120, 0)))
        {
            if (pendingExportProjectRow >= 0 && pendingExportProjectRow < cachedProjectList.Count)
            {
                var item = cachedProjectList[pendingExportProjectRow];
                var fileName = item.Name;
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = Path.GetFileName(Path.GetDirectoryName(item.ProjectPath) ?? string.Empty);
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = "project";
                var targetPath = GetUniqueFilePath(Path.Combine(GetDownloadDirectory(), fileName + ".proj.json"));
                ExportProjectRowToPath(pendingExportProjectRow, targetPath);
            }

            pendingExportProjectRow = -1;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.cancel"), new Vector2(120, 0)))
        {
            pendingExportProjectRow = -1;
            ImGui.CloseCurrentPopup();
        }
    }

    private static void NotifyProject(bool success, string title, string content)
    {
        try
        {
            Plugin.NotificationManager.AddNotification(new Notification
            {
                Title = title,
                Content = content,
                Type = success ? NotificationType.Success : NotificationType.Error,
            });
        }
        catch
        {
        }
    }

    private void PauseProjectAutoSave()
    {
        Interlocked.Increment(ref projectAutoSavePauseCount);
        suppressProjectAutoSave = true;
    }

    private void ResumeProjectAutoSave()
    {
        var remaining = Interlocked.Decrement(ref projectAutoSavePauseCount);
        if (remaining <= 0)
        {
            projectAutoSavePauseCount = 0;
            suppressProjectAutoSave = false;
        }
    }
}
