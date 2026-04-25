using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using SkinTattoo.Core;
using SkinTattoo.Http;
using SkinTattoo.Interop;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public partial class MainWindow
{
    // -- Left Panel: Card-based layer list ----------------------------------

    private void DrawLayerPanel()
    {
        // Group-level buttons
        if (UiHelpers.SquareIconButton(10, FontAwesomeIcon.Plus))
        {
            resourceWindowOpen = true;
            RefreshResources();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.add_group"));

        ImGui.SameLine();
        var canDeleteGroup = project.SelectedGroupIndex >= 0 && IsDeleteModifierHeld();
        using (ImRaii.Disabled(!canDeleteGroup))
        {
            if (UiHelpers.SquareIconButton(11, FontAwesomeIcon.Trash))
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
                }
                MarkPreviewDirty();
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Strings.T("tooltip.delete_group"));

        ImGui.SameLine();
        var selectedGroup = project.SelectedGroupIndex >= 0 && project.SelectedGroupIndex < project.Groups.Count
            ? project.Groups[project.SelectedGroupIndex] : null;
        var canCopyGroup = selectedGroup != null && selectedGroup.Layers.Count > 0;
        using (ImRaii.Disabled(!canCopyGroup))
        {
            if (UiHelpers.SquareIconButton(12, FontAwesomeIcon.Copy))
                CopyDecalGroup(selectedGroup!);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Strings.T("menu.copy_decal_group"));

        ImGui.SameLine();
        var canPasteGroup = copiedGroupLayers != null && project.SelectedGroupIndex >= 0
                            && project.SelectedGroupIndex < project.Groups.Count
                            && project.Groups[project.SelectedGroupIndex] != copiedGroupSource;
        using (ImRaii.Disabled(!canPasteGroup))
        {
            if (UiHelpers.SquareIconButton(13, FontAwesomeIcon.Paste))
                PasteDecalGroup(project.SelectedGroupIndex);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Strings.T("menu.paste_decal_group"));

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
            ImGui.TextDisabled(Strings.T("hint.empty_group_list"));
    }

    private void DrawGroupCard(int gi)
    {
        var group = project.Groups[gi];
        var isGroupSelected = project.SelectedGroupIndex == gi;
        var drawList = ImGui.GetWindowDrawList();
        var availWidth = ImGui.GetContentRegionAvail().X;

        // -- Card header --
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
        if (UiHelpers.SquareIconButton(50, arrowIcon))
            group.IsExpanded = !group.IsExpanded;
        ImGui.PopStyleColor(3);

        // Group name
        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2);
        var headerPad = 6f;
        var headerAddBtnSize = ImGui.GetFrameHeight();
        var nameWidth = availWidth - (ImGui.GetCursorScreenPos().X - headerStart.X) - headerAddBtnSize - (headerPad * 2f);
        if (nameWidth < 20) nameWidth = 20;
        // Suppress Selectable's own hover/active backgrounds: the header card already
        // has its own background drawn via drawList, layered hover bg looks split.
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, 0u);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, 0u);
        ImGui.PushStyleColor(ImGuiCol.Header, 0u);
        if (ImGui.Selectable($"{group.Name}##grpHdr", false, ImGuiSelectableFlags.None,
            new Vector2(nameWidth, ImGui.GetTextLineHeight())))
        {
            project.SelectedGroupIndex = gi;
            group.SelectedLayerIndex = -1;
        }
        ImGui.PopStyleColor(3);

        // Path tooltip and right-click context menu attach to the group name selectable.
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            if (!string.IsNullOrEmpty(group.DiffuseGamePath))
                ImGui.Text(Strings.T("tooltip_group.diffuse", group.DiffuseGamePath));
            if (!string.IsNullOrEmpty(group.NormGamePath))
                ImGui.Text(Strings.T("tooltip_group.normal", group.NormGamePath));
            if (!string.IsNullOrEmpty(group.MtrlGamePath))
                ImGui.Text(Strings.T("tooltip_group.material", group.MtrlGamePath));
            ImGui.TextDisabled(Strings.T("tooltip_group.copy_hint"));
            ImGui.EndTooltip();
        }

        if (ImGui.BeginPopupContextItem($"##grpCtx{gi}"))
        {
            var hasDiffuse = !string.IsNullOrEmpty(group.DiffuseGamePath);
            using (ImRaii.Disabled(!hasDiffuse))
                if (ImGui.MenuItem(Strings.T("menu.copy_diffuse_path")))
                    ImGui.SetClipboardText(group.DiffuseGamePath ?? "");

            var hasNorm = !string.IsNullOrEmpty(group.NormGamePath);
            using (ImRaii.Disabled(!hasNorm))
                if (ImGui.MenuItem(Strings.T("menu.copy_normal_path")))
                    ImGui.SetClipboardText(group.NormGamePath ?? "");

            var hasMtrl = !string.IsNullOrEmpty(group.MtrlGamePath);
            using (ImRaii.Disabled(!hasMtrl))
                if (ImGui.MenuItem(Strings.T("menu.copy_material_path")))
                    ImGui.SetClipboardText(group.MtrlGamePath ?? "");

            ImGui.Separator();

            if (ImGui.MenuItem(Strings.T("menu.copy_decal_group")))
                CopyDecalGroup(group);

            using (ImRaii.Disabled(copiedGroupLayers == null || group == copiedGroupSource))
                if (ImGui.MenuItem(Strings.T("menu.paste_decal_group")))
                    PasteDecalGroup(gi);
            ImGui.EndPopup();
        }

        var addBtnX = headerStart.X + availWidth - headerAddBtnSize - headerPad;
        ImGui.SetCursorScreenPos(new Vector2(addBtnX, headerStart.Y + 2f));
        if (UiHelpers.SquareIconButton(20, FontAwesomeIcon.Plus))
        {
            project.SelectedGroupIndex = gi;
            layerCounter++;
            var newLayer = group.AddLayer(Strings.T("layer.default.name", layerCounter));
            var (tw, th) = previewService.GetBaseTextureSize(group);
            float texAspect = (tw > 0 && th > 0) ? (float)tw / th : 1f;
            newLayer.UvScale = new Vector2(newLayer.UvScale.X, newLayer.UvScale.X * texAspect);
            SyncImagePathBuf();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.add_layer"));

        // Ensure cursor is past header
        ImGui.SetCursorScreenPos(new Vector2(headerStart.X, headerEnd.Y));

        if (!group.IsExpanded) return;

        // -- Card body --
        var bodyStart = ImGui.GetCursorScreenPos();

        // 3 channels: 0=body backdrop, 1=layer card bg/border, 2=layer card content.
        // Body backdrop is painted on channel 0 after the loop using saved bodyEnd, but
        // because channels replay in numeric order, it stays UNDER the cards on channel 1.
        drawList.ChannelsSplit(3);
        drawList.ChannelsSetCurrent(2);

        ImGui.Indent(6);

        // Layer rows
        var deleteLayerIndex = -1;
        var duplicateLayerIndex = -1;
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 3));

        float buttonSize = ImGui.GetFrameHeight();
        float gap = ImGui.GetStyle().ItemSpacing.X;
        const float cardPadX = 4f;
        const float cardPadY = 3f;
        const float layerSpacing = 2f;
        // Action strip: up, down, duplicate, delete  (4 buttons + 3 gaps).
        float actionStripWidth = buttonSize * 4 + gap * 3;
        float cardHeight = buttonSize + cardPadY * 2;

        for (int li = 0; li < group.Layers.Count; li++)
        {
            var layer = group.Layers[li];
            ImGui.PushID(li + 1000);

            bool isLayerSelected = isGroupSelected && group.SelectedLayerIndex == li;

            var layerStart = ImGui.GetCursorScreenPos();
            var rowAvail = ImGui.GetContentRegionAvail().X;
            var cardWidth = rowAvail - 2f;
            var cardEnd = layerStart + new Vector2(cardWidth, cardHeight);

            bool isHovered = ImGui.IsMouseHoveringRect(layerStart, cardEnd, false);
            bool showActions = isLayerSelected || isHovered;

            uint bgColor;
            uint borderColor;
            float borderThickness;
            if (isLayerSelected)
            {
                bgColor = ImGui.GetColorU32(new Vector4(0.22f, 0.48f, 0.85f, 0.65f));
                borderColor = ImGui.GetColorU32(new Vector4(0.55f, 0.80f, 1f, 1f));
                borderThickness = 2f;
            }
            else if (isHovered)
            {
                bgColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.08f));
                borderColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.22f));
                borderThickness = 1f;
            }
            else
            {
                bgColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.03f));
                borderColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.12f));
                borderThickness = 1f;
            }
            drawList.ChannelsSetCurrent(1);
            drawList.AddRectFilled(layerStart, cardEnd, bgColor, 4f);
            drawList.AddRect(layerStart, cardEnd, borderColor, 4f, ImDrawFlags.None, borderThickness);
            drawList.ChannelsSetCurrent(2);

            ImGui.SetCursorScreenPos(layerStart + new Vector2(cardPadX, cardPadY));
            var visIcon = layer.IsVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
            var visColor = layer.IsVisible ? new Vector4(1, 1, 1, 1) : new Vector4(0.5f, 0.5f, 0.5f, 1);
            ImGui.PushStyleColor(ImGuiCol.Text, visColor);
            if (UiHelpers.SquareIconButton(100 + li, visIcon, buttonSize))
            {
                layer.IsVisible = !layer.IsVisible;
                if (layer.AffectsEmissive || layer.RequiresRowPair)
                    previewService.InvalidateEmissiveForGroup(group);
                previewService.ForceFullRedrawNextCycle();
                MarkPreviewDirty(immediate: true);
            }
            ImGui.PopStyleColor();

            ImGui.SameLine();
            float nameStartX = ImGui.GetCursorScreenPos().X;
            float nameAvailEndX = layerStart.X + cardWidth - cardPadX
                                  - (showActions ? actionStripWidth + gap : 0f);
            float nameW = MathF.Max(20f, nameAvailEndX - nameStartX);
            float selectableHeight = ImGui.GetTextLineHeight() + ImGui.GetStyle().FramePadding.Y * 2;
            float selOffsetY = (buttonSize - selectableHeight) * 0.5f;
            if (selOffsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + selOffsetY);

            // Make Selectable's own hover/active backgrounds transparent so they don't
            // double-up with the card's hover bg drawn on channel 1.
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, 0u);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, 0u);
            ImGui.PushStyleColor(ImGuiCol.Header, 0u);
            if (ImGui.Selectable(layer.Name, false, ImGuiSelectableFlags.None,
                    new Vector2(nameW, selectableHeight)))
            {
                project.SelectedGroupIndex = gi;
                group.SelectedLayerIndex = li;
                SyncImagePathBuf();
            }
            ImGui.PopStyleColor(3);

            if (ImGui.IsItemHovered() && ImGui.CalcTextSize(layer.Name).X > nameW)
                ImGui.SetTooltip(layer.Name);

            if (showActions)
            {
                float actionStartX = layerStart.X + cardWidth - cardPadX - actionStripWidth;
                ImGui.SetCursorScreenPos(new Vector2(actionStartX, layerStart.Y + cardPadY));

                using (ImRaii.Disabled(li <= 0))
                {
                    if (UiHelpers.SquareIconButton(220 + li, FontAwesomeIcon.ArrowUp, buttonSize))
                    {
                        SelectLayer(gi, li - 1);
                        group.MoveLayerUp(li);
                        SyncImagePathBuf();
                        MarkPreviewDirty();
                    }
                }

                ImGui.SameLine();
                using (ImRaii.Disabled(li >= group.Layers.Count - 1))
                {
                    if (UiHelpers.SquareIconButton(240 + li, FontAwesomeIcon.ArrowDown, buttonSize))
                    {
                        SelectLayer(gi, li + 1);
                        group.MoveLayerDown(li);
                        SyncImagePathBuf();
                        MarkPreviewDirty();
                    }
                }

                ImGui.SameLine();
                if (UiHelpers.SquareIconButton(280 + li, FontAwesomeIcon.Copy, buttonSize))
                {
                    SelectLayer(gi, li);
                    duplicateLayerIndex = li;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("menu.duplicate_layer"));

                ImGui.SameLine();
                using (ImRaii.Disabled(!IsDeleteModifierHeld()))
                {
                    if (UiHelpers.SquareIconButton(260 + li, FontAwesomeIcon.Trash, buttonSize))
                    {
                        SelectLayer(gi, li);
                        deleteLayerIndex = li;
                    }
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(Strings.T("tooltip.delete_layer"));
            }

            ImGui.SetCursorScreenPos(layerStart + new Vector2(0, cardHeight + layerSpacing));

            ImGui.PopID();
        }
        ImGui.PopStyleVar();

        if (duplicateLayerIndex >= 0 && duplicateLayerIndex < group.Layers.Count)
            DuplicateLayer(group, duplicateLayerIndex);

        if (deleteLayerIndex >= 0 && deleteLayerIndex < group.Layers.Count)
            DeleteLayer(group, deleteLayerIndex);

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

    private void CopyDecalGroup(TargetGroup group)
    {
        copiedGroupLayers = [];
        foreach (var layer in group.Layers)
            copiedGroupLayers.Add(layer.Clone());

        copiedGroupSelectedLayerIndex = group.SelectedLayerIndex;
        copiedGroupSource = group;

        var (sw, sh) = previewService.GetBaseTextureSize(group);
        copiedGroupSrcAspect = (sw > 0 && sh > 0) ? (float)sw / sh : 0f;
    }

    private void DuplicateLayer(TargetGroup group, int index)
    {
        if (index < 0 || index >= group.Layers.Count)
            return;

        var clone = group.Layers[index].Clone();
        group.Layers.Insert(index + 1, clone);
        group.SelectedLayerIndex = index + 1;
        SyncImagePathBuf();
        MarkPreviewDirty();
    }

    private void DeleteLayer(TargetGroup group, int index)
    {
        if (index < 0 || index >= group.Layers.Count)
            return;

        var doomed = group.Layers[index];
        if (doomed.AffectsEmissive || doomed.RequiresRowPair)
            previewService.InvalidateEmissiveForGroup(group);
        previewService.ForceReleaseRowPair(group, doomed);

        group.RemoveLayer(index);
        // In-place async path silently no-ops when no visible layer paints (CpuUvComposite
        // returns null) so deleting the last decal would leave the previously baked GPU
        // texture mounted -- game keeps showing it. Force a Full Redraw so the new redirect
        // set (without this group / shrunken redirect set) actually replaces the temp mod.
        previewService.ForceFullRedrawNextCycle();
        MarkPreviewDirty(immediate: true);
    }

    private void PasteDecalGroup(int targetGroupIndex)
    {
        if (copiedGroupLayers == null || targetGroupIndex < 0 || targetGroupIndex >= project.Groups.Count)
            return;

        var targetGroup = project.Groups[targetGroupIndex];

        previewService.InvalidateEmissiveForGroup(targetGroup);
        foreach (var layer in targetGroup.Layers)
            previewService.ForceReleaseRowPair(targetGroup, layer);

        var (dw, dh) = previewService.GetBaseTextureSize(targetGroup);
        float dstAspect = (dw > 0 && dh > 0) ? (float)dw / dh : 0f;
        // Remap UvScale.Y so decals keep their pixel-space aspect across
        // materials with different texture sizes (e.g. 1024x1024 -> 1024x2048).
        bool needRescale = copiedGroupSrcAspect > 0f && dstAspect > 0f
                           && MathF.Abs(copiedGroupSrcAspect - dstAspect) > 1e-4f;

        var clipboardGroup = new TargetGroup { SelectedLayerIndex = copiedGroupSelectedLayerIndex };
        foreach (var layer in copiedGroupLayers)
        {
            var cloned = layer.Clone();
            if (needRescale)
                cloned.UvScale = new Vector2(cloned.UvScale.X,
                    cloned.UvScale.Y * dstAspect / copiedGroupSrcAspect);
            clipboardGroup.Layers.Add(cloned);
        }
        targetGroup.ReplaceLayersFrom(clipboardGroup);

        project.SelectedGroupIndex = targetGroupIndex;
        SyncImagePathBuf();
        previewService.ForceFullRedrawNextCycle();
        MarkPreviewDirty(immediate: true);
    }

    private void SelectLayer(int groupIndex, int layerIndex)
    {
        if (groupIndex < 0 || groupIndex >= project.Groups.Count) return;
        var g = project.Groups[groupIndex];
        if (layerIndex < 0 || layerIndex >= g.Layers.Count) return;
        project.SelectedGroupIndex = groupIndex;
        g.SelectedLayerIndex = layerIndex;
        SyncImagePathBuf();
    }
}
