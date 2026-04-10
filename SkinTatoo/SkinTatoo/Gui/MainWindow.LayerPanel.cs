using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using SkinTatoo.Core;
using SkinTatoo.Http;
using SkinTatoo.Interop;

namespace SkinTatoo.Gui;

public partial class MainWindow
{
    // ── Left Panel: Card-based layer list ──────────────────────────────────

    private void DrawLayerPanel()
    {
        // Group-level buttons
        if (ImGuiComponents.IconButton(10, FontAwesomeIcon.Plus))
        {
            resourceWindowOpen = true;
            RefreshResources();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("添加投影目标");

        ImGui.SameLine();
        var io = ImGui.GetIO();
        var canDeleteGroup = project.SelectedGroupIndex >= 0 && io.KeyCtrl && io.KeyShift;
        using (ImRaii.Disabled(!canDeleteGroup))
        {
            if (ImGuiComponents.IconButton(11, FontAwesomeIcon.Trash))
            {
                var gi2 = project.SelectedGroupIndex;
                if (gi2 >= 0 && gi2 < project.Groups.Count)
                {
                    penumbra.ClearRedirect();
                    previewService.ClearTextureCache();
                    previewService.ResetSwapState();
                    previewService.ClearMesh();
                    previewService.NotifyMeshChanged();
                    penumbra.RedrawPlayer();
                    project.RemoveGroup(gi2);
                    DebugServer.AppendLog($"[MainWindow] Removed group {gi2}, cleared redirects");
                }
                MarkPreviewDirty();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("按住 Ctrl+Shift 删除投影目标");

        ImGui.SameLine();
        ImGui.TextDisabled($"({project.Groups.Count})");

        ImGui.Separator();

        using var listChild = ImRaii.Child("##GroupList", new Vector2(-1, -1), false);
        if (!listChild.Success) return;

        for (var gi = 0; gi < project.Groups.Count; gi++)
        {
            ImGui.PushID(gi);
            DrawGroupCard(gi);
            ImGui.PopID();
            ImGui.Spacing();
        }

        if (project.Groups.Count == 0)
            ImGui.TextDisabled("点击 + 添加投影目标");
    }

    private void DrawGroupCard(int gi)
    {
        var group = project.Groups[gi];
        var isGroupSelected = project.SelectedGroupIndex == gi;
        var drawList = ImGui.GetWindowDrawList();
        var availWidth = ImGui.GetContentRegionAvail().X;

        // ── Card header ──
        var headerStart = ImGui.GetCursorScreenPos();
        var headerHeight = ImGui.GetFrameHeight() + 4;
        var headerEnd = headerStart + new Vector2(availWidth, headerHeight);

        var headerColor = isGroupSelected && group.SelectedLayerIndex < 0
            ? ImGui.GetColorU32(new Vector4(0.20f, 0.35f, 0.55f, 1f))
            : ImGui.GetColorU32(new Vector4(0.18f, 0.18f, 0.22f, 1f));

        if (group.IsExpanded)
            drawList.AddRectFilled(headerStart, headerEnd, headerColor, 4f, ImDrawFlags.RoundCornersTop);
        else
            drawList.AddRectFilled(headerStart, headerEnd, headerColor, 4f);

        // Collapse arrow
        ImGui.SetCursorScreenPos(headerStart + new Vector2(4, 2));
        var arrowIcon = group.IsExpanded ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight;
        ImGui.PushStyleColor(ImGuiCol.Button, 0);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.1f)));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0);
        if (ImGuiComponents.IconButton(50, arrowIcon))
            group.IsExpanded = !group.IsExpanded;
        ImGui.PopStyleColor(3);

        // Group name
        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2);
        var nameWidth = availWidth - (ImGui.GetCursorScreenPos().X - headerStart.X) - 8;
        if (nameWidth < 20) nameWidth = 20;
        if (ImGui.Selectable($"{group.Name}##grpHdr", false, ImGuiSelectableFlags.None,
            new Vector2(nameWidth, ImGui.GetTextLineHeight())))
        {
            project.SelectedGroupIndex = gi;
            group.SelectedLayerIndex = -1;
        }

        // Tooltip
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            if (!string.IsNullOrEmpty(group.DiffuseGamePath))
                ImGui.Text($"贴图: {group.DiffuseGamePath}");
            if (!string.IsNullOrEmpty(group.NormGamePath))
                ImGui.Text($"法线: {group.NormGamePath}");
            if (!string.IsNullOrEmpty(group.MtrlGamePath))
                ImGui.Text($"材质: {group.MtrlGamePath}");
            ImGui.TextDisabled("(右键菜单可复制路径)");
            ImGui.EndTooltip();
        }

        // Right-click context menu
        if (ImGui.BeginPopupContextItem($"##grpCtx{gi}"))
        {
            var hasDiffuse = !string.IsNullOrEmpty(group.DiffuseGamePath);
            using (ImRaii.Disabled(!hasDiffuse))
                if (ImGui.MenuItem("复制贴图路径"))
                    ImGui.SetClipboardText(group.DiffuseGamePath ?? "");

            var hasNorm = !string.IsNullOrEmpty(group.NormGamePath);
            using (ImRaii.Disabled(!hasNorm))
                if (ImGui.MenuItem("复制法线路径"))
                    ImGui.SetClipboardText(group.NormGamePath ?? "");

            var hasMtrl = !string.IsNullOrEmpty(group.MtrlGamePath);
            using (ImRaii.Disabled(!hasMtrl))
                if (ImGui.MenuItem("复制材质路径"))
                    ImGui.SetClipboardText(group.MtrlGamePath ?? "");
            ImGui.EndPopup();
        }

        // Ensure cursor is past header
        ImGui.SetCursorScreenPos(new Vector2(headerStart.X, headerEnd.Y));

        if (!group.IsExpanded) return;

        // ── Card body ──
        var bodyStart = ImGui.GetCursorScreenPos();

        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        ImGui.Indent(6);

        // Layer toolbar
        if (ImGuiComponents.IconButton(20, FontAwesomeIcon.Plus))
        {
            project.SelectedGroupIndex = gi;
            layerCounter++;
            group.AddLayer($"贴花 {layerCounter}");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("添加贴花图层");

        ImGui.SameLine();
        var io2 = ImGui.GetIO();
        var canDeleteLayer = isGroupSelected && group.SelectedLayerIndex >= 0
                             && io2.KeyCtrl && io2.KeyShift;
        using (ImRaii.Disabled(!canDeleteLayer))
        {
            if (ImGuiComponents.IconButton(21, FontAwesomeIcon.Trash))
            {
                var idx = group.SelectedLayerIndex;
                if (idx >= 0 && idx < group.Layers.Count)
                {
                    var doomed = group.Layers[idx];
                    if (doomed.AffectsEmissive)
                        previewService.ResetSwapState();
                    previewService.ForceReleaseRowPair(group, doomed);
                }
                group.RemoveLayer(idx);
                MarkPreviewDirty();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("按住 Ctrl+Shift 删除图层");

        ImGui.SameLine();
        using (ImRaii.Disabled(!isGroupSelected || group.SelectedLayerIndex <= 0))
        {
            if (ImGuiComponents.IconButton(22, FontAwesomeIcon.ArrowUp))
            {
                group.MoveLayerUp(group.SelectedLayerIndex);
                SyncImagePathBuf();
                MarkPreviewDirty();
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!isGroupSelected || group.SelectedLayerIndex < 0 || group.SelectedLayerIndex >= group.Layers.Count - 1))
        {
            if (ImGuiComponents.IconButton(23, FontAwesomeIcon.ArrowDown))
            {
                group.MoveLayerDown(group.SelectedLayerIndex);
                SyncImagePathBuf();
                MarkPreviewDirty();
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"({group.Layers.Count})");

        // Layer rows
        for (var li = 0; li < group.Layers.Count; li++)
        {
            var layer = group.Layers[li];
            ImGui.PushID(li + 1000);

            var isLayerSelected = isGroupSelected && group.SelectedLayerIndex == li;

            if (isLayerSelected)
            {
                var rowPos = ImGui.GetCursorScreenPos();
                drawList.AddRectFilled(
                    rowPos - new Vector2(2, 1),
                    rowPos + new Vector2(availWidth - 10, ImGui.GetFrameHeight() + 1),
                    ImGui.GetColorU32(new Vector4(0.24f, 0.42f, 0.65f, 0.5f)), 3f);
            }

            // Visibility toggle
            var visIcon = layer.IsVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
            var visColor = layer.IsVisible ? new Vector4(1, 1, 1, 1) : new Vector4(0.5f, 0.5f, 0.5f, 1);
            ImGui.PushStyleColor(ImGuiCol.Text, visColor);
            if (ImGuiComponents.IconButton(100 + li, visIcon))
            {
                layer.IsVisible = !layer.IsVisible;
                if (layer.AffectsEmissive) previewService.ResetSwapState();
                MarkPreviewDirty();
            }
            ImGui.PopStyleColor();

            // Highlight button
            if (!string.IsNullOrEmpty(layer.ImagePath) && !string.IsNullOrEmpty(group.MtrlGamePath))
            {
                ImGui.SameLine();
                var isThisHighlighted = highlightActive && highlightGroupIndex == gi && highlightLayerIndex == li;
                if (isThisHighlighted)
                {
                    var iconHue = (highlightFrameCounter % HighlightCycleSteps) / (float)HighlightCycleSteps;
                    var ic = TextureSwapService.HsvToRgb(iconHue, 0.8f, 1f);
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(ic.X, ic.Y, ic.Z, 1f));
                }
                ImGuiComponents.IconButton(200 + li, FontAwesomeIcon.Crosshairs);
                if (isThisHighlighted) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("高亮显示贴花");
                    if (!highlightActive || highlightGroupIndex != gi || highlightLayerIndex != li)
                    {
                        if (!previewService.HasEmissiveOffset(group.MtrlGamePath))
                            previewService.EnsureEmissiveInitialized(group);
                        highlightFrameCounter = 0;
                    }
                    highlightActive = true;
                    highlightGroupIndex = gi;
                    highlightLayerIndex = li;
                }
                else if (isThisHighlighted)
                {
                    highlightActive = false;
                    highlightGroupIndex = -1;
                    highlightLayerIndex = -1;
                    highlightFrameCounter = 0;
                    RestoreEmissiveAfterHighlight(group);
                }
            }

            ImGui.SameLine();

            if (ImGui.Selectable(layer.Name, isLayerSelected))
            {
                project.SelectedGroupIndex = gi;
                group.SelectedLayerIndex = li;
                SyncImagePathBuf();
            }

            ImGui.PopID();
        }

        ImGui.Unindent(6);
        ImGui.Spacing();

        // Draw body background on channel 0
        var bodyEnd = ImGui.GetCursorScreenPos();
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(
            bodyStart,
            new Vector2(headerStart.X + availWidth, bodyEnd.Y),
            ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.15f, 1f)), 4f, ImDrawFlags.RoundCornersBottom);
        drawList.AddRect(
            headerStart,
            new Vector2(headerStart.X + availWidth, bodyEnd.Y),
            ImGui.GetColorU32(new Vector4(0.28f, 0.28f, 0.32f, 1f)), 4f);
        drawList.ChannelsMerge();
    }
}
