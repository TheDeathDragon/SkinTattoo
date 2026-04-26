using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SkinTattoo.Core;
using SkinTattoo.Services;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public sealed class LibraryWindow : Window
{
    private enum LibraryViewMode
    {
        Large = 0,
        Medium = 1,
        Small = 2,
        Detail = 3,
    }

    private enum LibraryContentTab
    {
        Resources = 0,
        Favorites = 1,
    }

    private readonly LibraryService library;
    private readonly ITextureProvider textureProvider;
    private readonly Configuration config;
    private readonly FileDialogManager fileDialog = new();
    private readonly string folderIconPath;
    private readonly bool folderIconExists;
    private readonly ISharedImmediateTexture? folderIconTexture;

    private string search = string.Empty;
    private string? pendingDeleteHash;
    private string? pendingDeleteFolder;
    private string? pendingRenameFolder;
    private string? selectedEntryHash;
    private string selectedFolder = string.Empty;
    private string createFolderInput = string.Empty;
    private string renameFolderInput = string.Empty;
    private LibraryViewMode viewMode;
    private LibraryContentTab contentTab = LibraryContentTab.Resources;

    public Action<LibraryEntry>? OnPicked { get; set; }

    public void SetSelectedEntry(string? hash)
    {
        selectedEntryHash = string.IsNullOrWhiteSpace(hash) ? null : hash;
    }

    public LibraryWindow(LibraryService library, ITextureProvider textureProvider, Configuration config)
        : base(Strings.T("window.library.title") + "###SkinTattooLibrary",
               ImGuiWindowFlags.NoScrollbar)
    {
        this.library = library;
        this.textureProvider = textureProvider;
        this.config = config;
        folderIconPath = Path.Combine(config.GetAsmDir(), "images", "folder-light.png");
        folderIconExists = File.Exists(folderIconPath);
        folderIconTexture = folderIconExists ? textureProvider.GetFromFile(folderIconPath) : null;
        viewMode = NormalizeViewMode(config.LibraryViewMode);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        fileDialog.Draw();

        var currentFolder = LibraryService.NormalizeFolderPath(selectedFolder);
        selectedFolder = currentFolder;

        DrawContentTabPicker();
        ImGui.Separator();
        if (UiHelpers.SquareIconButton("libImport", FontAwesomeIcon.FileImport))
            OpenImportFilesDialog();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("library.tooltip.import"));

        ImGui.SameLine();
        if (UiHelpers.SquareIconButton("libImportFolder", FontAwesomeIcon.FolderOpen))
            OpenImportFolderDialog();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("library.tooltip.import_folder"));

        ImGui.SameLine();
        if (UiHelpers.SquareIconButton("libCreateFolder", FontAwesomeIcon.FolderPlus))
            ImGui.OpenPopup("##libCreateFolder");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("library.tooltip.create_folder"));

        ImGui.SameLine();
        DrawViewModePicker();


        ImGui.SameLine();
        var searchWidth = MathF.Max(90f, MathF.Min(180f, ImGui.GetContentRegionAvail().X));
        ImGui.SetNextItemWidth(searchWidth);
        ImGui.InputTextWithHint("##LibrarySearch", Strings.T("library.search_hint"), ref search, 128);

        ImGui.SameLine();

        if (!string.IsNullOrEmpty(currentFolder))
        {
            if (UiHelpers.SquareIconButton("libUpFolder", FontAwesomeIcon.ArrowTurnUp))
                selectedFolder = GetParentFolder(currentFolder);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("library.tooltip.up_folder"));
            ImGui.SameLine();
        }

        var breadcrumbStartX = ImGui.GetCursorPosX();
        var breadcrumbAvailWidth = ImGui.GetContentRegionAvail().X;
        DrawFolderBreadcrumbs(currentFolder, breadcrumbStartX, breadcrumbAvailWidth);

        ImGui.Separator();

        DrawCreateFolderPopup();
        DrawRenameFolderPopup();

        var allEntries = library.Snapshot();
        var folders = library.SnapshotFolders();
        using (var contentPane = ImRaii.Child("##LibraryContent", new Vector2(-1, -1), false))
        {
            if (!contentPane.Success) return;

            var childFolders = folders
                .Where(f => IsDirectChildFolder(currentFolder, f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var entries = new List<LibraryEntry>();
            foreach (var e in allEntries)
                if (string.Equals(LibraryService.NormalizeFolderPath(e.FolderPath), currentFolder, StringComparison.OrdinalIgnoreCase))
                    entries.Add(e);

            if (!string.IsNullOrWhiteSpace(search))
            {
                childFolders = folders
                    .Where(f => IsFolderInCurrentTree(f, currentFolder))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                entries = allEntries
                    .Where(e => IsEntryInCurrentTree(e, currentFolder))
                    .OrderBy(e => e.OriginalName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.AddedAt)
                    .ToList();
            }

            // Favorites are intentionally scoped to the currently selected folder tree
            // so the view stays consistent with the active navigation context.
            var favoriteEntries = allEntries
                .Where(e => e.IsFavorite)
                .Where(e => IsEntryInCurrentTree(e, currentFolder))
                .Where(e => MatchesEntrySearch(e))
                .OrderBy(e => e.OriginalName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.AddedAt)
                .ToList();

            if (!string.IsNullOrEmpty(currentFolder))
            {
                entries = entries
                    .OrderBy(e => e.OriginalName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.AddedAt)
                    .ToList();
            }

            if (contentTab == LibraryContentTab.Favorites)
            {
                if (viewMode == LibraryViewMode.Detail)
                    DrawDetailMode(allEntries, [], favoriteEntries);
                else
                    DrawGridMode([], favoriteEntries, GetCellSize(viewMode));
            }
            else
            {
                if (viewMode == LibraryViewMode.Detail)
                    DrawDetailMode(allEntries, childFolders, entries);
                else
                    DrawGridMode(childFolders, entries, GetCellSize(viewMode));
            }

            DrawDeleteConfirm();
            DrawFolderDeleteConfirm();
        }
    }

    private readonly record struct FolderAggregate(string SizeText, string AddedText);

    private void DrawViewModePicker()
    {
        var current = NormalizeViewMode(config.LibraryViewMode);
        if (current != viewMode)
            viewMode = current;

        ImGui.SetNextItemWidth(85f);
        if (!ImGui.BeginCombo("##LibraryViewMode", GetViewModeLabel(viewMode)))
            return;

        foreach (LibraryViewMode mode in Enum.GetValues<LibraryViewMode>())
        {
            var selected = mode == viewMode;
            if (ImGui.Selectable(GetViewModeLabel(mode), selected))
            {
                viewMode = mode;
                config.LibraryViewMode = (int)mode;
                config.Save();
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static LibraryViewMode NormalizeViewMode(int raw)
    {
        return Enum.IsDefined(typeof(LibraryViewMode), raw)
            ? (LibraryViewMode)raw
            : LibraryViewMode.Medium;
    }

    private static float GetCellSize(LibraryViewMode mode)
    {
        return mode switch
        {
            LibraryViewMode.Large => 128f,
            LibraryViewMode.Medium => 96f,
            LibraryViewMode.Small => 72f,
            _ => 96f,
        };
    }

    private static string GetViewModeLabel(LibraryViewMode mode)
    {
        return mode switch
        {
            LibraryViewMode.Large => Strings.T("library.view.large"),
            LibraryViewMode.Medium => Strings.T("library.view.medium"),
            LibraryViewMode.Small => Strings.T("library.view.small"),
            LibraryViewMode.Detail => Strings.T("library.view.detail"),
            _ => Strings.T("library.view.medium"),
        };
    }

    private void DrawGridMode(IReadOnlyList<string> childFolders, IReadOnlyList<LibraryEntry> entries, float cellSize)
    {
        const float cellPad = 12f;
        const int maxLabelLines = 3;

        var avail = ImGui.GetContentRegionAvail().X;
        var cols = Math.Max(1, (int)(avail / (cellSize + cellPad)));

        using var child = ImRaii.Child("##LibraryGrid", new Vector2(-1, -1), false);
        if (!child.Success) return;

        int drawn = 0;

        foreach (var folder in childFolders)
        {
            var folderName = GetFolderName(folder);
            if (!MatchesFolderSearch(folderName))
                continue;

            if (drawn % cols != 0) ImGui.SameLine();
            DrawFolderCell(folder, folderName, cellSize, maxLabelLines);
            drawn++;
        }

        foreach (var entry in entries)
        {
            if (!MatchesEntrySearch(entry))
                continue;

            if (drawn % cols != 0) ImGui.SameLine();
            DrawCell(entry, cellSize, maxLabelLines);
            drawn++;
        }

        if (drawn == 0)
            ImGui.TextDisabled(Strings.T("library.empty"));
    }

    private void DrawContentTabPicker()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = MathF.Max(80f, (ImGui.GetContentRegionAvail().X) * 0.5f);

        ImGui.SetCursorPosX(spacing);
        if (DrawContentTabButton(Strings.T("library.tabs.favorites"), contentTab == LibraryContentTab.Favorites, width))
            contentTab = LibraryContentTab.Favorites;

        ImGui.SameLine();
        ImGui.SetCursorPosX(width + spacing*2);
        if (DrawContentTabButton(Strings.T("library.tabs.resources"), contentTab == LibraryContentTab.Resources, width))
            contentTab = LibraryContentTab.Resources;
    }

    private static bool DrawContentTabButton(string label, bool active, float width)
    {
        var colors = ImGui.GetStyle().Colors;
        var baseColor = active ? colors[(int)ImGuiCol.TabActive] : colors[(int)ImGuiCol.Tab];
        var hoveredColor = colors[(int)ImGuiCol.TabHovered];

        using var _1 = ImRaii.PushColor(ImGuiCol.Button, baseColor);
        using var _2 = ImRaii.PushColor(ImGuiCol.ButtonHovered, hoveredColor);
        using var _3 = ImRaii.PushColor(ImGuiCol.ButtonActive, baseColor);

        return ImGui.Button(label, new Vector2(width, 0f));
    }

    private void DrawDetailMode(IReadOnlyList<LibraryEntry> allEntries, IReadOnlyList<string> childFolders, IReadOnlyList<LibraryEntry> entries)
    {
        using var child = ImRaii.Child("##LibraryDetail", new Vector2(-1, -1), false);
        if (!child.Success) return;

        bool drewAny = false;

        if (ImGui.BeginTable("##LibraryDetailTable", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            // Name column stretches to fill remaining space; the other columns auto-size
            // to their content (file extension, formatted size, date string).
            ImGui.TableSetupColumn(Strings.T("library.detail.name"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Strings.T("library.detail.type"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn(Strings.T("library.detail.size"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn(Strings.T("library.detail.added"), ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();

            foreach (var folder in childFolders)
            {
                var folderName = GetFolderName(folder);
                if (!MatchesFolderSearch(folderName))
                    continue;

                var aggregate = BuildFolderAggregate(folder, allEntries);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawNameTypeIcon(FontAwesomeIcon.Folder);
                ImGui.SameLine();
                if (ImGui.Selectable(folderName + "##folder_row_" + folder, false))
                    selectedFolder = folder;

                if (ImGui.BeginPopupContextItem("##folder_row_ctx_" + folder))
                {
                    if (ImGui.MenuItem(Strings.T("library.menu.rename_folder")))
                    {
                        pendingRenameFolder = folder;
                        renameFolderInput = folderName;
                    }

                    if (ImGui.MenuItem(Strings.T("library.menu.delete_folder")))
                        pendingDeleteFolder = folder;
                    ImGui.EndPopup();
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Strings.T("library.detail.folder_type"));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(aggregate.SizeText);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(aggregate.AddedText);
                drewAny = true;
            }

            foreach (var entry in entries)
            {
                var type = GetEntryTypeText(entry);
                if (!MatchesEntrySearch(entry, type))
                    continue;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawNameTypeIcon(FontAwesomeIcon.FileImage);
                ImGui.SameLine();
                if (ImGui.Selectable(entry.OriginalName + "##file_row_" + entry.Hash, string.Equals(selectedEntryHash, entry.Hash, StringComparison.OrdinalIgnoreCase)))
                {
                    selectedEntryHash = entry.Hash;
                    OnPicked?.Invoke(entry);
                }

                if (ImGui.IsItemHovered())
                {
                    var thumbPath = library.ThumbPath(entry);
                    if (!File.Exists(thumbPath))
                        library.EnsureThumb(entry);
                    DrawEntryPreviewTooltip(entry, 96f);
                }

                if (ImGui.BeginPopupContextItem("##lib_ctx_" + entry.Hash))
                {
                    if (ImGui.MenuItem(Strings.T("library.menu.apply")))
                    {
                        selectedEntryHash = entry.Hash;
                        OnPicked?.Invoke(entry);
                    }

                    if (ImGui.MenuItem(entry.IsFavorite
                            ? Strings.T("library.menu.unfavorite")
                            : Strings.T("library.menu.favorite")))
                        library.SetFavorite(entry.Hash, !entry.IsFavorite);

                    ImGui.Separator();
                    if (ImGui.MenuItem(Strings.T("library.menu.delete")))
                        pendingDeleteHash = entry.Hash;
                    ImGui.EndPopup();
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(type);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(GetEntrySizeText(entry));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(GetAddedText(entry.AddedAt));
                drewAny = true;
            }

            ImGui.EndTable();
        }

        if (!drewAny)
            ImGui.TextDisabled(Strings.T("library.empty"));
    }

    private void DrawCreateFolderPopup()
    {
        using var popup = ImRaii.Popup("##libCreateFolder");
        if (!popup.Success) return;

        ImGui.SetNextItemWidth(280f);
        ImGui.InputTextWithHint("##libFolderName", Strings.T("library.folder.name_hint"), ref createFolderInput, 256);

        if (ImGui.Button(Strings.T("button.confirm"), new Vector2(120, 0)))
        {
            var normalizedInput = LibraryService.NormalizeFolderPath(createFolderInput);
            if (!string.IsNullOrEmpty(normalizedInput))
            {
                var targetFolder = normalizedInput.Contains('/', StringComparison.Ordinal)
                    ? normalizedInput
                    : LibraryService.NormalizeFolderPath(string.IsNullOrEmpty(selectedFolder)
                        ? normalizedInput
                        : selectedFolder + "/" + normalizedInput);

                if (library.CreateFolder(targetFolder))
                    selectedFolder = targetFolder;
            }
            createFolderInput = string.Empty;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.cancel"), new Vector2(120, 0)))
        {
            createFolderInput = string.Empty;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawRenameFolderPopup()
    {
        if (pendingRenameFolder == null) return;

        ImGui.OpenPopup("##libRenameFolder");
        using var popup = ImRaii.PopupModal("##libRenameFolder",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar);
        if (!popup.Success) return;

        var sourceFolder = pendingRenameFolder;
        var sourceName = GetFolderName(sourceFolder);
        ImGui.TextUnformatted(Strings.T("library.folder.rename_prompt", sourceName));

        ImGui.SetNextItemWidth(280f);
        var inputConfirmed = ImGui.InputTextWithHint("##libRenameFolderName", Strings.T("library.folder.name_hint"), ref renameFolderInput, 256, ImGuiInputTextFlags.EnterReturnsTrue);

        var normalizedInput = LibraryService.NormalizeFolderPath(renameFolderInput);
        var parentPath = GetParentFolder(sourceFolder);
        var targetFolder = string.IsNullOrEmpty(normalizedInput)
            ? string.Empty
            : string.IsNullOrEmpty(parentPath)
                ? normalizedInput
                : LibraryService.NormalizeFolderPath(parentPath + "/" + normalizedInput);

        var confirmPressed = ImGui.Button(Strings.T("button.confirm"), new Vector2(120, 0));
        if (confirmPressed || inputConfirmed)
        {
            if (!string.IsNullOrEmpty(targetFolder) && library.RenameFolder(sourceFolder, targetFolder))
                selectedFolder = RewritePathPrefix(selectedFolder, sourceFolder, targetFolder);

            pendingRenameFolder = null;
            renameFolderInput = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.cancel"), new Vector2(120, 0)))
        {
            pendingRenameFolder = null;
            renameFolderInput = string.Empty;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawFolderCell(string folderPath, string folderName, float size, int maxLabelLines)
    {
        ImGui.BeginGroup();
        var cursor = ImGui.GetCursorScreenPos();

        var clicked = ImGui.InvisibleButton("##folder_" + folderPath, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();

        if (ImGui.BeginPopupContextItem("##folderCtx_" + folderPath))
        {
            if (ImGui.MenuItem(Strings.T("library.menu.rename_folder")))
            {
                pendingRenameFolder = folderPath;
                renameFolderInput = folderName;
            }

            if (ImGui.MenuItem(Strings.T("library.menu.delete_folder")))
                pendingDeleteFolder = folderPath;
            ImGui.EndPopup();
        }

        var drawList = ImGui.GetWindowDrawList();
        var bg = hovered
            ? ImGui.GetColorU32(ImGuiCol.FrameBgHovered)
            : ImGui.GetColorU32(ImGuiCol.FrameBg);
        drawList.AddRectFilled(cursor, cursor + new Vector2(size, size), bg, 4f);

        DrawLargeFolderIcon(drawList, cursor, size, hovered);

        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(folderName);
            ImGui.TextDisabled(folderPath);
            ImGui.EndTooltip();
        }

        if (clicked)
            selectedFolder = folderPath;

        DrawWrappedText(folderName, size, maxLabelLines);

        ImGui.EndGroup();
    }

    private void DrawEntryPreviewTooltip(LibraryEntry entry, float previewSize)
    {
        ImGui.BeginTooltip();

        var thumbPath = library.ThumbPath(entry);
        if (File.Exists(thumbPath))
        {
            var wrap = textureProvider.GetFromFile(thumbPath).GetWrapOrDefault();
            if (wrap != null)
            {
                var maxDim = Math.Max(wrap.Width, wrap.Height);
                if (maxDim > 0)
                {
                    var scale = previewSize / maxDim;
                    var size = new Vector2(wrap.Width * scale, wrap.Height * scale);
                    ImGui.Image(wrap.Handle, size);
                }
            }
        }

        ImGui.TextUnformatted(entry.OriginalName);
        ImGui.TextDisabled($"{entry.Width} x {entry.Height}");
        ImGui.TextDisabled(Strings.T("library.tooltip.used_count", entry.UseCount));
        ImGui.EndTooltip();
    }

    private static string GetFolderName(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return string.Empty;
        var idx = folderPath.LastIndexOf('/');
        return idx < 0 ? folderPath : folderPath[(idx + 1)..];
    }

    private static string GetParentFolder(string folderPath)
    {
        var normalized = LibraryService.NormalizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(normalized)) return string.Empty;

        var idx = normalized.LastIndexOf('/');
        return idx < 0 ? string.Empty : normalized[..idx];
    }

    private static string RewritePathPrefix(string path, string oldPrefix, string newPrefix)
    {
        var normalizedPath = LibraryService.NormalizeFolderPath(path);
        var oldNormalized = LibraryService.NormalizeFolderPath(oldPrefix);
        var newNormalized = LibraryService.NormalizeFolderPath(newPrefix);

        if (string.IsNullOrEmpty(normalizedPath) || string.IsNullOrEmpty(oldNormalized))
            return normalizedPath;

        if (string.Equals(normalizedPath, oldNormalized, StringComparison.OrdinalIgnoreCase))
            return newNormalized;

        if (!normalizedPath.StartsWith(oldNormalized + "/", StringComparison.OrdinalIgnoreCase))
            return normalizedPath;

        return newNormalized + normalizedPath[oldNormalized.Length..];
    }

    private static bool IsDirectChildFolder(string parentFolder, string candidateFolder)
    {
        var parent = LibraryService.NormalizeFolderPath(parentFolder);
        var child = LibraryService.NormalizeFolderPath(candidateFolder);
        if (string.IsNullOrEmpty(child)) return false;

        if (string.IsNullOrEmpty(parent))
            return !child.Contains('/');

        if (!child.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = child[(parent.Length + 1)..];
        return !remainder.Contains('/');
    }

    private static bool IsSameOrDescendantFolder(string rootFolder, string candidateFolder)
    {
        var root = LibraryService.NormalizeFolderPath(rootFolder);
        var candidate = LibraryService.NormalizeFolderPath(candidateFolder);

        if (string.IsNullOrEmpty(root))
            return true;

        if (string.IsNullOrEmpty(candidate))
            return false;

        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEntryInCurrentTree(LibraryEntry entry, string currentFolder)
    {
        var entryFolder = LibraryService.NormalizeFolderPath(entry.FolderPath);
        return IsSameOrDescendantFolder(currentFolder, entryFolder);
    }

    private static bool IsFolderInCurrentTree(string folderPath, string currentFolder)
    {
        var folder = LibraryService.NormalizeFolderPath(folderPath);
        return IsSameOrDescendantFolder(currentFolder, folder);
    }

    private readonly record struct BreadcrumbItem(string FullLabel, string Path);
    private readonly record struct BreadcrumbRenderItem(string Label, string FullLabel, string Path, bool ShowTooltip);

    private void DrawFolderBreadcrumbs(string currentFolder, float startX, float availableWidth)
    {
        if (availableWidth <= 0f)
            return;

        var items = BuildBreadcrumbItems(currentFolder);
        if (items.Count == 0)
            return;

        var firstVisible = 0;
        var rendered = BuildBreadcrumbRenderItems(items, firstVisible, availableWidth);
        while (firstVisible < items.Count - 1 && rendered == null)
        {
            firstVisible++;
            var prefixWidth = firstVisible > 0 ? MeasureBreadcrumbOverflowPrefixWidth() : 0f;
            rendered = BuildBreadcrumbRenderItems(items, firstVisible, MathF.Max(0f, availableWidth - prefixWidth));
        }

        if (rendered == null)
        {
            // Window too narrow to fit even the minimum breadcrumb — keep the overflow
            // popup accessible so the user can still navigate at any window size.
            var overflowPopupId = "##crumb_overflow_popup_" + currentFolder;
            ImGui.SetCursorPosX(startX);
            if (ImGui.Button("...##crumb_overflow_btn_" + currentFolder))
                ImGui.OpenPopup(overflowPopupId);
            if (ImGui.BeginPopup(overflowPopupId))
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var label = string.IsNullOrEmpty(item.Path) ? item.FullLabel : item.Path;
                    if (ImGui.Selectable(label + "##crumb_hidden_all_" + item.Path))
                    {
                        selectedFolder = item.Path;
                        ImGui.CloseCurrentPopup();
                    }
                }
                ImGui.EndPopup();
            }
            return;
        }

        ImGui.SetCursorPosX(startX);

        if (firstVisible > 0)
        {
            var overflowPopupId = "##crumb_overflow_popup_" + currentFolder;
            if (ImGui.Button("...##crumb_overflow_btn_" + currentFolder))
                ImGui.OpenPopup(overflowPopupId);

            if (ImGui.BeginPopup(overflowPopupId))
            {
                for (var i = 0; i < firstVisible; i++)
                {
                    var item = items[i];
                    var label = string.IsNullOrEmpty(item.Path) ? item.FullLabel : item.Path;
                    if (ImGui.Selectable(label + "##crumb_hidden_" + item.Path))
                    {
                        selectedFolder = item.Path;
                        ImGui.CloseCurrentPopup();
                    }
                }

                ImGui.EndPopup();
            }

            if (rendered.Count > 0)
            {
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(">");
                ImGui.SameLine();
            }
        }

        for (var i = 0; i < rendered.Count; i++)
        {
            var item = rendered[i];
            if (ImGui.Button(item.Label + "##crumb_" + item.Path))
                selectedFolder = item.Path;

            if (item.ShowTooltip && ImGui.IsItemHovered())
                ImGui.SetTooltip(item.FullLabel);

            if (i < rendered.Count - 1)
            {
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(">");
                ImGui.SameLine();
            }
        }
    }

    private static float MeasureBreadcrumbOverflowPrefixWidth()
    {
        var style = ImGui.GetStyle();
        var framePadX = style.FramePadding.X * 2f;
        var itemSpacingX = style.ItemSpacing.X;
        var separatorWidth = ImGui.CalcTextSize(">").X;

        var overflowButtonWidth = ImGui.CalcTextSize("...").X + framePadX;
        return overflowButtonWidth + itemSpacingX + separatorWidth + itemSpacingX;
    }

    private static List<BreadcrumbItem> BuildBreadcrumbItems(string currentFolder)
    {
        var items = new List<BreadcrumbItem>();
        var allLabel = Strings.T("library.folder.all");
        items.Add(new BreadcrumbItem(allLabel, string.Empty));

        if (string.IsNullOrEmpty(currentFolder))
            return items;

        var parts = currentFolder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = string.Empty;
        foreach (var part in parts)
        {
            path = string.IsNullOrEmpty(path) ? part : path + "/" + part;
            items.Add(new BreadcrumbItem(part, path));
        }

        return items;
    }

    private static List<BreadcrumbRenderItem>? BuildBreadcrumbRenderItems(
        IReadOnlyList<BreadcrumbItem> items,
        int startIndex,
        float availableWidth)
    {
        if (startIndex >= items.Count)
            return [];

        const int minTruncateChars = 16;

        var render = new List<BreadcrumbRenderItem>(items.Count - startIndex);
        var limits = new List<int>(items.Count - startIndex);
        var minLimits = new List<int>(items.Count - startIndex);

        for (var i = startIndex; i < items.Count; i++)
        {
            var full = items[i].FullLabel;
            limits.Add(full.Length);
            minLimits.Add(Math.Min(minTruncateChars, full.Length));
            render.Add(new BreadcrumbRenderItem(full, full, items[i].Path, false));
        }

        var totalWidth = MeasureBreadcrumbWidth(render);
        if (totalWidth <= availableWidth)
            return render;

        // Pre-compute per-item text widths so we can maintain totalWidth incrementally
        // instead of calling MeasureBreadcrumbWidth (O(N) CalcTextSize calls) each iteration.
        var textWidths = new float[render.Count];
        for (var i = 0; i < render.Count; i++)
            textWidths[i] = ImGui.CalcTextSize(render[i].Label).X;

        while (totalWidth > availableWidth)
        {
            // Find the widest item that still has room to truncate further.
            var reduceIndex = -1;
            var widestTextWidth = float.MinValue;
            for (var i = 0; i < render.Count; i++)
            {
                if (limits[i] <= minLimits[i]) continue;
                if (textWidths[i] > widestTextWidth)
                {
                    widestTextWidth = textWidths[i];
                    reduceIndex = i;
                }
            }

            if (reduceIndex < 0)
                return null;

            // Binary search: find the longest label for reduceIndex that makes totalWidth <= availableWidth.
            // Changing this item's width from widestTextWidth to newW shifts totalWidth by (newW - widestTextWidth).
            // We need: newW <= widestTextWidth - (totalWidth - availableWidth).
            var targetTextWidth = widestTextWidth - (totalWidth - availableWidth);
            var fullLabel = render[reduceIndex].FullLabel;
            var lo = minLimits[reduceIndex];
            var hi = limits[reduceIndex] - 1;
            var bestLen = lo;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (ImGui.CalcTextSize(TruncateLabel(fullLabel, mid)).X <= targetTextWidth)
                {
                    bestLen = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            limits[reduceIndex] = bestLen;
            var newLabel = TruncateLabel(fullLabel, bestLen);
            var newTextWidth = ImGui.CalcTextSize(newLabel).X;
            totalWidth -= widestTextWidth - newTextWidth;
            textWidths[reduceIndex] = newTextWidth;
            render[reduceIndex] = new BreadcrumbRenderItem(
                newLabel, fullLabel, render[reduceIndex].Path, newLabel != fullLabel);
        }

        return render;
    }

    private static float MeasureBreadcrumbWidth(IReadOnlyList<BreadcrumbRenderItem> items)
    {
        if (items.Count == 0)
            return 0f;

        var style = ImGui.GetStyle();
        var framePadX = style.FramePadding.X * 2f;
        var itemSpacingX = style.ItemSpacing.X;
        var separatorWidth = ImGui.CalcTextSize(">").X;

        float total = 0f;
        for (var i = 0; i < items.Count; i++)
        {
            var buttonWidth = ImGui.CalcTextSize(items[i].Label).X + framePadX;
            total += buttonWidth;

            if (i < items.Count - 1)
                total += itemSpacingX + separatorWidth + itemSpacingX;
        }

        return total;
    }

    private static string TruncateLabel(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || maxChars <= 0)
            return string.Empty;

        if (value.Length <= maxChars)
            return value;

        if (maxChars <= 3)
            return new string('.', maxChars);

        return value[..(maxChars - 3)] + "...";
    }

    private void DrawCell(LibraryEntry entry, float size, int maxLabelLines)
    {
        ImGui.BeginGroup();
        var cursor = ImGui.GetCursorScreenPos();
        var isSelected = string.Equals(selectedEntryHash, entry.Hash, StringComparison.OrdinalIgnoreCase);

        var clicked = ImGui.InvisibleButton("##cell_" + entry.Hash, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();

        var drawList = ImGui.GetWindowDrawList();
        var bg = isSelected
            ? ImGui.GetColorU32(new Vector4(0.20f, 0.36f, 0.60f, 0.95f))
            : hovered
                ? ImGui.GetColorU32(ImGuiCol.ButtonHovered)
                : ImGui.GetColorU32(ImGuiCol.FrameBg);
        drawList.AddRectFilled(cursor, cursor + new Vector2(size, size), bg, 4f);

        var thumbPath = library.ThumbPath(entry);
        if (!File.Exists(thumbPath))
            library.EnsureThumb(entry);
        if (File.Exists(thumbPath))
        {
            var wrap = textureProvider.GetFromFile(thumbPath).GetWrapOrDefault();
            if (wrap != null)
            {
                var iw = wrap.Width;
                var ih = wrap.Height;
                var maxDim = Math.Max(iw, ih);
                float scale = (size - 8f) / maxDim;
                var dw = iw * scale;
                var dh = ih * scale;
                var imgCursor = cursor + new Vector2((size - dw) * 0.5f, (size - dh) * 0.5f);
                drawList.AddImage(wrap.Handle, imgCursor, imgCursor + new Vector2(dw, dh));
            }
        }
        else
        {
            var loading = Strings.T("library.loading");
            var ts = ImGui.CalcTextSize(loading);
            drawList.AddText(cursor + new Vector2((size - ts.X) * 0.5f, (size - ts.Y) * 0.5f),
                ImGui.GetColorU32(ImGuiCol.TextDisabled), loading);
        }

        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(entry.OriginalName);
            ImGui.TextDisabled($"{entry.Width} x {entry.Height}");
            ImGui.TextDisabled(Strings.T("library.tooltip.used_count", entry.UseCount));
            ImGui.EndTooltip();
        }

        if (clicked)
        {
            selectedEntryHash = entry.Hash;
            OnPicked?.Invoke(entry);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##lib_ctx_" + entry.Hash);

        if (ImGui.BeginPopup("##lib_ctx_" + entry.Hash))
        {
            if (ImGui.MenuItem(Strings.T("library.menu.apply")))
            {
                selectedEntryHash = entry.Hash;
                OnPicked?.Invoke(entry);
            }

            if (ImGui.MenuItem(entry.IsFavorite
                    ? Strings.T("library.menu.unfavorite")
                    : Strings.T("library.menu.favorite")))
                library.SetFavorite(entry.Hash, !entry.IsFavorite);

            ImGui.Separator();
            if (ImGui.MenuItem(Strings.T("library.menu.delete")))
                pendingDeleteHash = entry.Hash;
            ImGui.EndPopup();
        }

        if (isSelected)
        {
            var border = ImGui.GetColorU32(new Vector4(0.95f, 0.80f, 0.20f, 1f));
            drawList.AddRect(cursor + new Vector2(1, 1), cursor + new Vector2(size - 1, size - 1), border, 4f, ImDrawFlags.None, 2f);
        }

        DrawWrappedText(entry.OriginalName, size, maxLabelLines);

        ImGui.EndGroup();
    }

    private void DrawLargeFolderIcon(ImDrawListPtr drawList, Vector2 cursor, float size, bool hovered)
    {
        if (folderIconTexture != null)
        {
            var wrap = folderIconTexture.GetWrapOrDefault();
            if (wrap != null)
            {
                var iconMaxSize = MathF.Max(16f, size - 10f);
                var iw = wrap.Width;
                var ih = wrap.Height;
                var maxDim = Math.Max(iw, ih);
                if (maxDim > 0)
                {
                    var scale = iconMaxSize / maxDim;
                    var dw = iw * scale;
                    var dh = ih * scale;
                    var min = new Vector2(
                        MathF.Round(cursor.X + (size - dw) * 0.5f),
                        MathF.Round(cursor.Y + (size - dh) * 0.5f));
                    var max = min + new Vector2(dw, dh);
                    drawList.AddImage(wrap.Handle, min, max, Vector2.Zero, Vector2.One);
                    return;
                }
            }
        }

        // Fallback rectangle when PNG icon is unavailable or not yet loaded.
        var pad = size * 0.2f;
        var fallbackColor = hovered
            ? ImGui.GetColorU32(new Vector4(0.85f, 0.70f, 0.25f, 0.65f))
            : ImGui.GetColorU32(new Vector4(0.70f, 0.58f, 0.18f, 0.45f));
        drawList.AddRectFilled(cursor + new Vector2(pad, pad), cursor + new Vector2(size - pad, size - pad), fallbackColor, 4f);
    }

    private static void DrawNameTypeIcon(FontAwesomeIcon icon)
    {
        using var _ = ImRaii.PushFont(UiBuilder.IconFont);
        ImGui.TextUnformatted(icon.ToIconString());
    }

    private void DrawWrappedText(string text, float width, int maxLines)
    {
        var lines = BuildWrappedLines(text, width, maxLines);
        foreach (var line in lines)
            ImGui.TextUnformatted(line);

        var missingLines = maxLines - lines.Count;
        if (missingLines > 0)
            ImGui.Dummy(new Vector2(width, missingLines * ImGui.GetTextLineHeightWithSpacing()));
    }

    private static List<string> BuildWrappedLines(string text, float width, int maxLines)
    {
        var result = new List<string>();
        if (maxLines <= 0)
            return result;

        var source = string.IsNullOrWhiteSpace(text)
            ? Strings.T("library.na")
            : text.Replace('\n', ' ').Trim();

        var remaining = source.Trim();
        while (!string.IsNullOrEmpty(remaining) && result.Count < maxLines)
        {
            if (ImGui.CalcTextSize(remaining).X <= width)
            {
                result.Add(remaining);
                remaining = string.Empty;
                break;
            }

            var fitLen = GetMaxFittingLength(remaining, width);
            var breakLen = GetPreferredBreakLength(remaining, fitLen);
            var line = remaining[..breakLen].TrimEnd();
            if (string.IsNullOrEmpty(line))
            {
                line = remaining[..Math.Max(1, fitLen)];
                breakLen = line.Length;
            }

            result.Add(line);
            remaining = remaining[breakLen..].TrimStart();
        }

        if (!string.IsNullOrEmpty(remaining) && result.Count > 0)
        {
            var last = result[^1].TrimEnd();
            while (last.Length > 1 && ImGui.CalcTextSize(last + "...").X > width)
                last = last[..^1];

            result[^1] = (string.IsNullOrWhiteSpace(last) ? Strings.T("library.na") : last) + "...";
        }

        return result;
    }

    private static int GetMaxFittingLength(string value, float width)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        int lo = 1;
        int hi = value.Length;
        int best = 1;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var sample = value[..mid];
            if (ImGui.CalcTextSize(sample).X <= width)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    private static int GetPreferredBreakLength(string value, int fitLength)
    {
        if (fitLength >= value.Length)
            return value.Length;

        for (var i = fitLength - 1; i > 0; i--)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c) || c == '_' || c == '-' || c == '.')
                return i + 1;
        }

        return Math.Max(1, fitLength);
    }

    private bool MatchesFolderSearch(string folderName)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return ContainsIgnoreCase(folderName, search)
               || ContainsIgnoreCase(Strings.T("library.detail.folder_type"), search);
    }

    private bool MatchesEntrySearch(LibraryEntry entry, string? type = null)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var name = string.IsNullOrWhiteSpace(entry.OriginalName) ? Strings.T("library.na") : entry.OriginalName;
        var typeText = type ?? GetEntryTypeText(entry);
        return ContainsIgnoreCase(name, search)
               || ContainsIgnoreCase(typeText, search);
    }

    private static bool ContainsIgnoreCase(string? value, string term)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private FolderAggregate BuildFolderAggregate(string folderPath, IReadOnlyList<LibraryEntry> allEntries)
    {
        long totalSize = 0;
        bool hasSize = false;
        DateTime latestAdded = default;
        bool hasDate = false;

        foreach (var entry in allEntries)
        {
            var entryFolder = LibraryService.NormalizeFolderPath(entry.FolderPath);
            if (!IsDescendantOrSameFolder(folderPath, entryFolder))
                continue;

            if (TryGetEntrySize(entry, out var entrySize))
            {
                totalSize += entrySize;
                hasSize = true;
            }

            if (entry.AddedAt != default && (!hasDate || entry.AddedAt > latestAdded))
            {
                latestAdded = entry.AddedAt;
                hasDate = true;
            }
        }

        var size = hasSize ? FormatFileSize(totalSize) : Strings.T("library.na");
        var added = hasDate ? GetAddedText(latestAdded) : Strings.T("library.na");
        return new FolderAggregate(size, added);
    }

    private static bool IsDescendantOrSameFolder(string rootFolder, string candidateFolder)
    {
        var root = LibraryService.NormalizeFolderPath(rootFolder);
        var candidate = LibraryService.NormalizeFolderPath(candidateFolder);
        if (string.IsNullOrEmpty(candidate))
            return false;

        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private string GetEntryTypeText(LibraryEntry entry)
    {
        var ext = Path.GetExtension(entry.OriginalName);
        if (string.IsNullOrWhiteSpace(ext))
            ext = Path.GetExtension(entry.FileName);
        if (string.IsNullOrWhiteSpace(ext))
            return Strings.T("library.na");

        return ext.ToLowerInvariant();
    }

    private string GetEntrySizeText(LibraryEntry entry)
    {
        return TryGetEntrySize(entry, out var size)
            ? FormatFileSize(size)
            : Strings.T("library.na");
    }

    private bool TryGetEntrySize(LibraryEntry entry, out long size)
    {
        size = 0;
        try
        {
            var diskPath = library.ResolveDiskPath(entry.Hash);
            if (string.IsNullOrWhiteSpace(diskPath) || !File.Exists(diskPath))
                return false;

            var info = new FileInfo(diskPath);
            if (info.Length < 0)
                return false;

            size = info.Length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 0)
            return Strings.T("library.na");

        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < suffixes.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var result = unit == 0 ? $"{bytes} {suffixes[unit]}" : $"{size:0.##} {suffixes[unit]}";
        return string.IsNullOrWhiteSpace(result) ? Strings.T("library.na") : result;
    }

    private static string GetAddedText(DateTime value)
    {
        if (value == default || value.Year < 1900)
            return Strings.T("library.na");

        try
        {
            return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return Strings.T("library.na");
        }
    }

    private void DrawFolderDeleteConfirm()
    {
        if (pendingDeleteFolder == null) return;

        ImGui.OpenPopup("##lib_folder_delete_confirm");
        using var popup = ImRaii.PopupModal("##lib_folder_delete_confirm",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar);
        if (!popup.Success) return;

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.65f, 0.1f, 1f));
        ImGui.TextUnformatted(Strings.T("library.folder.delete_warning"));
        ImGui.PopStyleColor();
        ImGui.Spacing();

        ImGui.TextUnformatted(Strings.T("library.folder.delete_prompt", pendingDeleteFolder));
        ImGui.Spacing();

        var confirmPressed = ImGui.Button(Strings.T("button.confirm"), new Vector2(120, 0));
        if (confirmPressed)
        {
            var deleted = pendingDeleteFolder;
            library.DeleteFolder(deleted);
            // If the deleted folder (or any of its children) was the active view, snap back to root.
            if (selectedFolder.Equals(deleted, StringComparison.OrdinalIgnoreCase) ||
                selectedFolder.StartsWith(deleted + "/", StringComparison.OrdinalIgnoreCase))
                selectedFolder = string.Empty;
            pendingDeleteFolder = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.cancel"), new Vector2(120, 0)))
        {
            pendingDeleteFolder = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawDeleteConfirm()
    {
        if (pendingDeleteHash == null) return;
        var entry = library.Get(pendingDeleteHash);
        if (entry == null)
        {
            pendingDeleteHash = null;
            return;
        }

        ImGui.OpenPopup("##lib_delete_confirm");
        using var popup = ImRaii.PopupModal("##lib_delete_confirm",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar);
        if (!popup.Success) return;

        ImGui.TextUnformatted(Strings.T("library.delete_prompt", entry.OriginalName));
        ImGui.Spacing();

        var confirmPressed = ImGui.Button(Strings.T("button.confirm"), new Vector2(120, 0));
        var enterPressed = ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter);
        if (confirmPressed || enterPressed)
        {
            library.Remove(entry.Hash);
            pendingDeleteHash = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.cancel"), new Vector2(120, 0)))
        {
            pendingDeleteHash = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private void OpenImportFilesDialog()
    {
        fileDialog.OpenFileDialog(
            Strings.T("dialog.select_image"),
            "Image Files{.png,.jpg,.jpeg,.tga,.bmp,.dds}",
            (ok, paths) =>
            {
                if (!ok) return;
                foreach (var p in paths)
                {
                    var entry = library.ImportFromPath(p);
                    if (entry != null)
                    {
                        library.SetEntryFolder(entry.Hash, selectedFolder);
                        config.LastImageDir = Path.GetDirectoryName(p);
                    }
                }
                config.Save();
            },
            10, config.LastImageDir, true);
    }

    private void OpenImportFolderDialog()
    {
        fileDialog.OpenFolderDialog(
            Strings.T("dialog.select_folder"),
            (ok, path) =>
            {
                if (!ok || string.IsNullOrWhiteSpace(path)) return;
                if (!Directory.Exists(path)) return;

                library.ImportFolderTree(path);
                config.LastImageDir = path;
                config.Save();
            },
            config.LastImageDir,
            true);
    }
}
