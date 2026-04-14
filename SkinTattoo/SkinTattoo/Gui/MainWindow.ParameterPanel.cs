using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using SkinTattoo.Core;
using SkinTattoo.Services;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public partial class MainWindow
{
    // ── Right Panel: Parameters ──────────────────────────────────────────────

    private void DrawParameterPanel()
    {
        var group = project.SelectedGroup;
        var layer = group?.SelectedLayer;
        if (layer == null)
        {
            if (group != null)
                ImGui.TextDisabled(Strings.T("error.no_layer_selected"));
            else
                ImGui.TextDisabled(Strings.T("error.no_group_hint"));
            ImGui.Separator();
            DrawActionsSection();
            return;
        }

        var idx = group!.SelectedLayerIndex;
        if (lastEditedLayerIndex != idx)
            SyncImagePathBuf();

        // Layer name
        ImGui.SetNextItemWidth(-1);
        var name = layer.Name;
        if (ImGui.InputText("##LayerName", ref name, 128))
            layer.Name = name;

        ImGui.Spacing();

        // ── Image section ──
        DrawImageSection(group, layer, idx);

        var hasImage = !string.IsNullOrEmpty(layer.ImagePath);

        using (ImRaii.Disabled(!hasImage))
        {
            DrawTransformSection(layer);
            DrawRenderSection(layer);
            DrawPbrSection(group, layer);
        }

        ImGui.Separator();
        DrawActionsSection();
    }

    private void DrawImageSection(TargetGroup group, DecalLayer layer, int idx)
    {
        if (!ImGui.CollapsingHeader(Strings.T("section.image"), ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 30);
        if (ImGui.InputText("##ImagePath", ref imagePathBuf, 512))
        {
            layer.ImagePath = imagePathBuf;
            AutoFitLayerScale(group, layer);
            lastEditedLayerIndex = idx;
            MarkPreviewDirty();
        }
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(20, FontAwesomeIcon.FolderOpen))
        {
            var capturedGi = project.SelectedGroupIndex;
            var capturedLi = idx;
            fileDialog.OpenFileDialog(
                Strings.T("dialog.select_image"),
                "Image Files{.png,.jpg,.jpeg,.tga,.bmp,.dds}",
                (ok, paths) =>
                {
                    if (ok && paths.Count > 0 && capturedGi < project.Groups.Count)
                    {
                        var g = project.Groups[capturedGi];
                        if (capturedLi < g.Layers.Count)
                        {
                            var path = paths[0];
                            var picked = g.Layers[capturedLi];
                            picked.ImagePath = path;
                            AutoFitLayerScale(g, picked);
                            imagePathBuf = path;
                            lastEditedLayerIndex = capturedLi;
                            config.LastImageDir = System.IO.Path.GetDirectoryName(path);
                            config.Save();
                            MarkPreviewDirty();
                        }
                    }
                },
                1, config.LastImageDir, false);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.browse_image"));
    }

    private void DrawTransformSection(DecalLayer layer)
    {
        if (!ImGui.CollapsingHeader(Strings.T("section.transform"), ImGuiTreeNodeFlags.DefaultOpen)) return;

        const float labelW = 56f;
        var cx = layer.UvCenter.X;
        var cy = layer.UvCenter.Y;

        ImGui.AlignTextToFramePadding(); ImGui.Text(Strings.T("label.center_x")); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat("##centerX", ref cx, 0.005f, 0f, 1f, "%.3f"))
        { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }
        if (ScrollAdjust(ref cx, 0.001f, 0f, 1f))
        { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }

        ImGui.AlignTextToFramePadding(); ImGui.Text(Strings.T("label.center_y")); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat("##centerY", ref cy, 0.005f, 0f, 1f, "%.3f"))
        { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }
        if (ScrollAdjust(ref cy, 0.001f, 0f, 1f))
        { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }

        ImGui.AlignTextToFramePadding(); ImGui.Text(Strings.T("label.size")); ImGui.SameLine(labelW);
        var uvScale = layer.UvScale;
        var lockBtnWidth = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - lockBtnWidth - ImGui.GetStyle().ItemSpacing.X);
        // Aspect ratio correction: locked scale should be square in pixel space, not UV space.
        // For 1024x2048: texAspect = 0.5, so UvScale.Y = s * 0.5 keeps the decal square.
        var selGroup = project.SelectedGroup;
        var (texAspW, texAspH) = selGroup != null ? previewService.GetBaseTextureSize(selGroup) : (0, 0);
        float texAspect = (texAspW > 0 && texAspH > 0) ? (float)texAspW / texAspH : 1f;
        if (scaleLocked)
        {
            var s = uvScale.X;
            if (ImGui.DragFloat("##scaleLocked", ref s, 0.005f, 0.01f, 10f, "%.3f"))
            { layer.UvScale = new Vector2(s, s * texAspect); MarkPreviewDirty(); }
            if (ScrollAdjust(ref s, 0.005f, 0.01f, 10f))
            { layer.UvScale = new Vector2(s, s * texAspect); MarkPreviewDirty(); }
        }
        else
        {
            if (ImGui.DragFloat2("##scaleUnlocked", ref uvScale, 0.005f, 0.01f, 10f, "%.3f"))
            { layer.UvScale = uvScale; MarkPreviewDirty(); }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(scaleLocked ? Strings.T("tooltip.scale_locked") : Strings.T("tooltip.scale_unlocked"));
        ImGui.SameLine();
        var lockIcon = scaleLocked ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink;
        if (ImGuiComponents.IconButton(30, lockIcon))
            scaleLocked = !scaleLocked;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(scaleLocked ? Strings.T("tooltip.lock_ratio") : Strings.T("tooltip.unlock_ratio"));

        ImGui.AlignTextToFramePadding(); ImGui.Text(Strings.T("label.rotation")); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(-1);
        var rot = layer.RotationDeg;
        if (ImGui.DragFloat("##rot", ref rot, 1f, -180f, 180f, "%.1f\u00b0"))
        { layer.RotationDeg = rot; MarkPreviewDirty(); }
        if (ScrollAdjust(ref rot, 1f, -180f, 180f))
        { layer.RotationDeg = rot; MarkPreviewDirty(); }
    }

    private void DrawRenderSection(DecalLayer layer)
    {
        if (!ImGui.CollapsingHeader(Strings.T("section.render"), ImGuiTreeNodeFlags.DefaultOpen)) return;

        const float labelW = 56f;

        ImGui.AlignTextToFramePadding(); ImGui.Text(Strings.T("label.opacity")); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(-1);
        var opacity = layer.Opacity;
        if (ImGui.DragFloat("##opacity", ref opacity, 0.01f, 0f, 1f, "%.2f"))
        { layer.Opacity = opacity; MarkPreviewDirty(); }
        if (ScrollAdjust(ref opacity, 0.02f, 0f, 1f))
        { layer.Opacity = opacity; MarkPreviewDirty(); }

        ImGui.AlignTextToFramePadding(); ImGui.Text(Strings.T("label.blend")); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(-1);
        var blendModeNames = GetBlendModeNames();
        var blendIdx = Array.IndexOf(BlendModeValues, layer.BlendMode);
        if (blendIdx < 0) blendIdx = 0;
        if (ImGui.Combo("##blend", ref blendIdx, blendModeNames, blendModeNames.Length))
        { layer.BlendMode = BlendModeValues[blendIdx]; MarkPreviewDirty(); }

        ImGui.AlignTextToFramePadding(); ImGui.Text(Strings.T("label.clip")); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(-1);
        var clipModeNames = GetClipModeNames();
        var clipIdx = (int)layer.Clip;
        if (ImGui.Combo("##clip", ref clipIdx, clipModeNames, clipModeNames.Length))
        { layer.Clip = (ClipMode)clipIdx; MarkPreviewDirty(); }
    }

    private void DrawPbrSection(TargetGroup group, DecalLayer layer)
    {
        if (!ImGui.CollapsingHeader(Strings.T("section.pbr"), ImGuiTreeNodeFlags.DefaultOpen)) return;

        ImGui.Spacing();

        {
            var was = layer.AffectsDiffuse;
            var v = was;
            if (ImGui.Checkbox(Strings.T("checkbox.show_decal"), ref v))
            {
                layer.AffectsDiffuse = v;
                MarkPreviewDirty(immediate: true);
            }
        }

        {
            var was = layer.AffectsEmissive;
            var v = was;
            if (ImGui.Checkbox(Strings.T("checkbox.emissive"), ref v))
            {
                if (v && !was)
                {
                    layer.AffectsEmissive = true;
                    previewService.InvalidateEmissiveForGroup(group);
                    MarkPreviewDirty(immediate: true);
                }
                else if (!v && was)
                {
                    layer.AffectsEmissive = false;
                    previewService.InvalidateEmissiveForGroup(group);
                    MarkPreviewDirty(immediate: true);
                }
            }
            if (layer.AffectsEmissive)
            {
                var emColor = layer.EmissiveColor;
                if (ImGui.ColorEdit3("##emColor", ref emColor,
                    ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.NoInputs))
                { layer.EmissiveColor = emColor; MarkPreviewDirty(); TryDirectEmissiveUpdate(group); }
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled(Strings.T("label.intensity"));
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                var emI = layer.EmissiveIntensity;
                if (ImGui.DragFloat("##emI", ref emI, 0.05f, 0.1f, 10f, "%.2f"))
                { layer.EmissiveIntensity = emI; MarkPreviewDirty(); TryDirectEmissiveUpdate(group); }
            }
        }

        // ── Layer fade mask ──
        ImGui.Spacing();
        DrawSectionLabel(Strings.T("section.fade"));

        const float fadeLabW = 56f;

        ImGui.AlignTextToFramePadding(); ImGui.Text(Strings.T("label.shape")); ImGui.SameLine(fadeLabW);
        ImGui.SetNextItemWidth(-1);
        var fadeMaskNames = GetFadeMaskNames();
        var maskIdx = (int)layer.FadeMask;
        if (ImGui.Combo("##fadeMask", ref maskIdx, fadeMaskNames, fadeMaskNames.Length))
        { layer.FadeMask = (LayerFadeMask)maskIdx; MarkPreviewDirty(); }

        if (layer.FadeMask != LayerFadeMask.Uniform)
        {
            ImGui.AlignTextToFramePadding(); ImGui.Text(Strings.T("label.falloff")); ImGui.SameLine(fadeLabW);
            ImGui.SetNextItemWidth(-1);
            var f = layer.FadeMaskFalloff;
            if (ImGui.SliderFloat("##fadeFalloff", ref f, 0.01f, 1f, "%.2f"))
            { layer.FadeMaskFalloff = f; MarkPreviewDirty(); }
        }

        if (layer.FadeMask == LayerFadeMask.DirectionalGradient)
        {
            ImGui.AlignTextToFramePadding(); ImGui.Text(Strings.T("label.angle")); ImGui.SameLine(fadeLabW);
            ImGui.SetNextItemWidth(-1);
            var a = layer.GradientAngleDeg;
            if (ImGui.SliderFloat("##gAng", ref a, -180f, 180f, "%.1f\u00b0"))
            { layer.GradientAngleDeg = a; MarkPreviewDirty(); }

            ImGui.AlignTextToFramePadding(); ImGui.Text(Strings.T("label.range")); ImGui.SameLine(fadeLabW);
            ImGui.SetNextItemWidth(-1);
            var gs = layer.GradientScale;
            if (ImGui.SliderFloat("##gScl", ref gs, 0.1f, 2f, "%.2f"))
            { layer.GradientScale = gs; MarkPreviewDirty(); }

            ImGui.AlignTextToFramePadding(); ImGui.Text(Strings.T("label.offset")); ImGui.SameLine(fadeLabW);
            ImGui.SetNextItemWidth(-1);
            var go = layer.GradientOffset;
            if (ImGui.SliderFloat("##gOff", ref go, -1f, 1f, "%.2f"))
            { layer.GradientOffset = go; MarkPreviewDirty(); }
        }

        if (layer.FadeMask != LayerFadeMask.Uniform)
            DrawFadeMaskPreview(layer.FadeMask, layer.FadeMaskFalloff, layer);

        if (string.IsNullOrEmpty(group.MtrlGamePath))
            ImGui.TextColored(new Vector4(1, 0.5f, 0.3f, 1), Strings.T("error.need_mtrl"));
    }


    private static void DrawSectionLabel(string text)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var textSize = ImGui.CalcTextSize(text);
        var lineY = pos.Y + textSize.Y * 0.5f;
        var lineColor = ImGui.GetColorU32(new Vector4(0.35f, 0.35f, 0.4f, 0.6f));
        var textEnd = pos.X + textSize.X + 4;
        drawList.AddLine(new Vector2(textEnd, lineY), new Vector2(pos.X + avail, lineY), lineColor);
        ImGui.TextDisabled(text);
    }

    private void DrawFadeMaskPreview(LayerFadeMask mask, float falloff, DecalLayer layer)
    {
        ImGui.TextDisabled(Strings.T("label.mask_preview"));
        var previewSize = 100f;
        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var cellCount = 32;
        var cellSize = previewSize / cellCount;

        for (int y = 0; y < cellCount; y++)
        {
            for (int x = 0; x < cellCount; x++)
            {
                float ru = (x + 0.5f) / cellCount - 0.5f;
                float rv = (y + 0.5f) / cellCount - 0.5f;
                float val;
                if (mask == LayerFadeMask.DirectionalGradient)
                    val = PreviewService.ComputeDirectionalGradient(ru, rv, 1f,
                        layer.GradientAngleDeg, layer.GradientScale, falloff, layer.GradientOffset);
                else if (mask == LayerFadeMask.ShapeOutline)
                {
                    float fakeDa = (MathF.Abs(ru) < 0.3f && MathF.Abs(rv) < 0.3f) ? 1f : 0f;
                    float fakeNeighbor = ((MathF.Abs(ru) < 0.29f && MathF.Abs(rv) < 0.29f) ||
                                          (MathF.Abs(ru) > 0.31f || MathF.Abs(rv) > 0.31f)) ? fakeDa : 0.5f;
                    val = PreviewService.ComputeShapeOutline(fakeDa, falloff, fakeNeighbor);
                }
                else
                    val = PreviewService.ComputeFadeMaskWeight(mask, falloff, ru, rv, 1f);

                var color = ImGui.GetColorU32(new Vector4(val, val, val, 1f));
                var p0 = pos + new Vector2(x * cellSize, y * cellSize);
                var p1 = p0 + new Vector2(cellSize + 0.5f, cellSize + 0.5f);
                drawList.AddRectFilled(p0, p1, color);
            }
        }

        drawList.AddRect(pos, pos + new Vector2(previewSize),
            ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));

        // Color preview showing actual emissive color through mask
        var colorPos = pos + new Vector2(previewSize + 8, 0);
        var emColor = layer.EmissiveColor * layer.EmissiveIntensity;
        for (int y = 0; y < cellCount; y++)
        {
            for (int x = 0; x < cellCount; x++)
            {
                float ru = (x + 0.5f) / cellCount - 0.5f;
                float rv = (y + 0.5f) / cellCount - 0.5f;
                float val;
                if (mask == LayerFadeMask.DirectionalGradient)
                    val = PreviewService.ComputeDirectionalGradient(ru, rv, 1f,
                        layer.GradientAngleDeg, layer.GradientScale, falloff, layer.GradientOffset);
                else if (mask == LayerFadeMask.ShapeOutline)
                {
                    float fakeDa = (MathF.Abs(ru) < 0.3f && MathF.Abs(rv) < 0.3f) ? 1f : 0f;
                    float fakeNeighbor = ((MathF.Abs(ru) < 0.29f && MathF.Abs(rv) < 0.29f) ||
                                          (MathF.Abs(ru) > 0.31f || MathF.Abs(rv) > 0.31f)) ? fakeDa : 0.5f;
                    val = PreviewService.ComputeShapeOutline(fakeDa, falloff, fakeNeighbor);
                }
                else
                    val = PreviewService.ComputeFadeMaskWeight(mask, falloff, ru, rv, 1f);

                var cr = Math.Min(emColor.X * val, 1f);
                var cg = Math.Min(emColor.Y * val, 1f);
                var cb = Math.Min(emColor.Z * val, 1f);
                var color = ImGui.GetColorU32(new Vector4(cr, cg, cb, 1f));
                var p0 = colorPos + new Vector2(x * cellSize, y * cellSize);
                var p1 = p0 + new Vector2(cellSize + 0.5f, cellSize + 0.5f);
                drawList.AddRectFilled(p0, p1, color);
            }
        }

        drawList.AddRect(colorPos, colorPos + new Vector2(previewSize),
            ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));
        ImGui.Dummy(new Vector2(previewSize * 2 + 8, previewSize + 4));

        var textY = pos.Y + previewSize + 2;
        drawList.AddText(new Vector2(pos.X, textY),
            ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), Strings.T("label.mask_label"));
        drawList.AddText(new Vector2(colorPos.X, textY),
            ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), Strings.T("label.effect_label"));
        ImGui.Dummy(new Vector2(0, 14));
    }
}
