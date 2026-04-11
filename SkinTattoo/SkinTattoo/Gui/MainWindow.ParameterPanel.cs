using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using SkinTattoo.Core;
using SkinTattoo.Http;
using SkinTattoo.Services;

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
                ImGui.TextDisabled("选择或添加一个图层");
            else
                ImGui.TextDisabled("先添加一个贴花组");
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
        if (!ImGui.CollapsingHeader("贴花图片", ImGuiTreeNodeFlags.DefaultOpen)) return;

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
                "选择贴花图片",
                "图片文件{.png,.jpg,.jpeg,.tga,.bmp,.dds}",
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
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("浏览...");
    }

    private void DrawTransformSection(DecalLayer layer)
    {
        if (!ImGui.CollapsingHeader("UV 位置", ImGuiTreeNodeFlags.DefaultOpen)) return;

        const float labelW = 56f;
        var cx = layer.UvCenter.X;
        var cy = layer.UvCenter.Y;

        ImGui.AlignTextToFramePadding(); ImGui.Text("中心 X"); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat("##centerX", ref cx, 0.005f, 0f, 1f, "%.3f"))
        { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }
        if (ScrollAdjust(ref cx, 0.001f, 0f, 1f))
        { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }

        ImGui.AlignTextToFramePadding(); ImGui.Text("中心 Y"); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.DragFloat("##centerY", ref cy, 0.005f, 0f, 1f, "%.3f"))
        { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }
        if (ScrollAdjust(ref cy, 0.001f, 0f, 1f))
        { layer.UvCenter = new Vector2(cx, cy); MarkPreviewDirty(); }

        ImGui.AlignTextToFramePadding(); ImGui.Text("大小"); ImGui.SameLine(labelW);
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
            if (ImGui.DragFloat("##scaleLocked", ref s, 0.005f, 0.01f, 2f, "%.3f"))
            { layer.UvScale = new Vector2(s, s * texAspect); MarkPreviewDirty(); }
            if (ScrollAdjust(ref s, 0.005f, 0.01f, 2f))
            { layer.UvScale = new Vector2(s, s * texAspect); MarkPreviewDirty(); }
        }
        else
        {
            if (ImGui.DragFloat2("##scaleUnlocked", ref uvScale, 0.005f, 0.01f, 2f, "%.3f"))
            { layer.UvScale = uvScale; MarkPreviewDirty(); }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(scaleLocked ? "大小（像素等比）" : "大小（UV 独立）");
        ImGui.SameLine();
        var lockIcon = scaleLocked ? FontAwesomeIcon.Link : FontAwesomeIcon.Unlink;
        if (ImGuiComponents.IconButton(30, lockIcon))
            scaleLocked = !scaleLocked;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(scaleLocked ? "比例锁定" : "比例解锁");

        ImGui.AlignTextToFramePadding(); ImGui.Text("旋转"); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(-1);
        var rot = layer.RotationDeg;
        if (ImGui.DragFloat("##rot", ref rot, 1f, -180f, 180f, "%.1f\u00b0"))
        { layer.RotationDeg = rot; MarkPreviewDirty(); }
        if (ScrollAdjust(ref rot, 1f, -180f, 180f))
        { layer.RotationDeg = rot; MarkPreviewDirty(); }
    }

    private void DrawRenderSection(DecalLayer layer)
    {
        if (!ImGui.CollapsingHeader("渲染", ImGuiTreeNodeFlags.DefaultOpen)) return;

        const float labelW = 56f;

        ImGui.AlignTextToFramePadding(); ImGui.Text("透明度"); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(-1);
        var opacity = layer.Opacity;
        if (ImGui.DragFloat("##opacity", ref opacity, 0.01f, 0f, 1f, "%.2f"))
        { layer.Opacity = opacity; MarkPreviewDirty(); }
        if (ScrollAdjust(ref opacity, 0.02f, 0f, 1f))
        { layer.Opacity = opacity; MarkPreviewDirty(); }

        ImGui.AlignTextToFramePadding(); ImGui.Text("混合"); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(-1);
        var blendIdx = Array.IndexOf(BlendModeValues, layer.BlendMode);
        if (blendIdx < 0) blendIdx = 0;
        if (ImGui.Combo("##blend", ref blendIdx, BlendModeNames, BlendModeNames.Length))
        { layer.BlendMode = BlendModeValues[blendIdx]; MarkPreviewDirty(); }

        ImGui.AlignTextToFramePadding(); ImGui.Text("裁剪"); ImGui.SameLine(labelW);
        ImGui.SetNextItemWidth(-1);
        var clipIdx = (int)layer.Clip;
        if (ImGui.Combo("##clip", ref clipIdx, ClipModeNames, ClipModeNames.Length))
        { layer.Clip = (ClipMode)clipIdx; MarkPreviewDirty(); }
    }

    private void DrawPbrSection(TargetGroup group, DecalLayer layer)
    {
        if (!ImGui.CollapsingHeader("PBR 属性", ImGuiTreeNodeFlags.DefaultOpen)) return;

        var alloc = previewService.GetOrCreateAllocator(group);
        bool exhausted = alloc.AvailableSlots == 0 && layer.AllocatedRowPair < 0;
        bool pbrSupported = previewService.MaterialSupportsPbr(group);

        if (!pbrSupported && !string.IsNullOrEmpty(group.MtrlGamePath))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.7f, 0.3f, 1f));
            ImGui.TextWrapped("当前材质 (skin.shpk) 无 ColorTable");
            ImGui.PopStyleColor();
            ImGui.TextDisabled("仅「贴花」与「发光」可用");
        }
        else if (exhausted)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.6f, 0.2f, 1f));
            ImGui.TextWrapped($"PBR 行号已满 (vanilla 占 {alloc.VanillaOccupiedCount})");
            ImGui.PopStyleColor();
            ImGui.TextDisabled("请关闭其他图层的 PBR 字段后再试");
        }

        ImGui.Spacing();

        // ── Diffuse ──
        DrawPbrField(group, layer, "贴花/漫反射",
            () => layer.AffectsDiffuse, v => layer.AffectsDiffuse = v,
            supported: true, requiresRowPair: pbrSupported, exhausted: exhausted,
            drawValue: () =>
            {
                using (ImRaii.Disabled(!pbrSupported))
                {
                    var d = layer.DiffuseColor;
                    if (ImGui.ColorEdit3("##diffColor", ref d,
                        ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.NoInputs))
                    { layer.DiffuseColor = d; MarkPreviewDirty(); }
                    if (ImGui.IsItemHovered() && !pbrSupported)
                        ImGui.SetTooltip("skin.shpk 不支持漫反射颜色调制 (贴花仍会显示)");
                }
            });

        // ── Specular ──
        DrawPbrField(group, layer, "镜面反射",
            () => layer.AffectsSpecular, v => layer.AffectsSpecular = v,
            supported: pbrSupported, requiresRowPair: true, exhausted: exhausted,
            drawValue: () =>
            {
                using (ImRaii.Disabled(!pbrSupported))
                {
                    var sc = layer.SpecularColor;
                    if (ImGui.ColorEdit3("##specColor", ref sc,
                        ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.NoInputs))
                    { layer.SpecularColor = sc; MarkPreviewDirty(); }
                }
            });

        // ── Emissive ──
        DrawPbrField(group, layer, "发光",
            () => layer.AffectsEmissive, v => layer.AffectsEmissive = v,
            supported: true, requiresRowPair: pbrSupported, exhausted: exhausted,
            onDisabled: () => previewService.InvalidateEmissiveForGroup(group),
            onEnabled: () => previewService.InvalidateEmissiveForGroup(group),
            drawValue: () =>
            {
                var emColor = layer.EmissiveColor;
                if (ImGui.ColorEdit3("##emColor", ref emColor,
                    ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.NoInputs))
                { layer.EmissiveColor = emColor; MarkPreviewDirty(); TryDirectEmissiveUpdate(group, layer); }
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled("强度");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(-1);
                var emI = layer.EmissiveIntensity;
                if (ImGui.DragFloat("##emI", ref emI, 0.05f, 0.1f, 10f, "%.2f"))
                { layer.EmissiveIntensity = emI; MarkPreviewDirty(); TryDirectEmissiveUpdate(group, layer); }
            });

        // ── Roughness ──
        DrawPbrField(group, layer, "粗糙度",
            () => layer.AffectsRoughness, v => layer.AffectsRoughness = v,
            supported: pbrSupported, requiresRowPair: true, exhausted: exhausted,
            drawValue: () =>
            {
                using (ImRaii.Disabled(!pbrSupported))
                {
                    ImGui.SetNextItemWidth(-1);
                    var r = layer.Roughness;
                    if (ImGui.SliderFloat("##rough", ref r, 0f, 1f, "%.2f"))
                    { layer.Roughness = r; MarkPreviewDirty(); }
                }
            });

        // ── Metalness ──
        DrawPbrField(group, layer, "金属度",
            () => layer.AffectsMetalness, v => layer.AffectsMetalness = v,
            supported: pbrSupported, requiresRowPair: true, exhausted: exhausted,
            drawValue: () =>
            {
                using (ImRaii.Disabled(!pbrSupported))
                {
                    ImGui.SetNextItemWidth(-1);
                    var mt = layer.Metalness;
                    if (ImGui.SliderFloat("##metal", ref mt, 0f, 1f, "%.2f"))
                    { layer.Metalness = mt; MarkPreviewDirty(); }
                }
            });

        // ── Sheen ──
        DrawPbrField(group, layer, "光泽",
            () => layer.AffectsSheen, v => layer.AffectsSheen = v,
            supported: pbrSupported, requiresRowPair: true, exhausted: exhausted,
            drawValue: () =>
            {
                using (ImRaii.Disabled(!pbrSupported))
                {
                    var avail = ImGui.GetContentRegionAvail().X;
                    var third = (avail - ImGui.GetStyle().ItemSpacing.X * 2) / 3f;
                    ImGui.SetNextItemWidth(third);
                    var sr = layer.SheenRate;
                    if (ImGui.SliderFloat("##sheenRate", ref sr, 0f, 1f, "R %.2f"))
                    { layer.SheenRate = sr; MarkPreviewDirty(); }
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(third);
                    var st = layer.SheenTint;
                    if (ImGui.SliderFloat("##sheenTint", ref st, 0f, 1f, "T %.2f"))
                    { layer.SheenTint = st; MarkPreviewDirty(); }
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(-1);
                    var sa = layer.SheenAperture;
                    if (ImGui.SliderFloat("##sheenAp", ref sa, 0f, 20f, "A %.1f"))
                    { layer.SheenAperture = sa; MarkPreviewDirty(); }
                }
            });

        // ── Layer fade mask ──
        ImGui.Spacing();
        DrawSectionLabel("图层羽化");

        const float fadeLabW = 56f;

        ImGui.AlignTextToFramePadding(); ImGui.Text("形状"); ImGui.SameLine(fadeLabW);
        ImGui.SetNextItemWidth(-1);
        var maskIdx = (int)layer.FadeMask;
        if (ImGui.Combo("##fadeMask", ref maskIdx, LayerFadeMaskNames, LayerFadeMaskNames.Length))
        { layer.FadeMask = (LayerFadeMask)maskIdx; MarkPreviewDirty(); }

        if (layer.FadeMask != LayerFadeMask.Uniform)
        {
            ImGui.AlignTextToFramePadding(); ImGui.Text("羽化"); ImGui.SameLine(fadeLabW);
            ImGui.SetNextItemWidth(-1);
            var f = layer.FadeMaskFalloff;
            if (ImGui.SliderFloat("##fadeFalloff", ref f, 0.01f, 1f, "%.2f"))
            { layer.FadeMaskFalloff = f; MarkPreviewDirty(); }
        }

        if (layer.FadeMask == LayerFadeMask.DirectionalGradient)
        {
            ImGui.AlignTextToFramePadding(); ImGui.Text("角度"); ImGui.SameLine(fadeLabW);
            ImGui.SetNextItemWidth(-1);
            var a = layer.GradientAngleDeg;
            if (ImGui.SliderFloat("##gAng", ref a, -180f, 180f, "%.1f\u00b0"))
            { layer.GradientAngleDeg = a; MarkPreviewDirty(); }

            ImGui.AlignTextToFramePadding(); ImGui.Text("范围"); ImGui.SameLine(fadeLabW);
            ImGui.SetNextItemWidth(-1);
            var gs = layer.GradientScale;
            if (ImGui.SliderFloat("##gScl", ref gs, 0.1f, 2f, "%.2f"))
            { layer.GradientScale = gs; MarkPreviewDirty(); }

            ImGui.AlignTextToFramePadding(); ImGui.Text("偏移"); ImGui.SameLine(fadeLabW);
            ImGui.SetNextItemWidth(-1);
            var go = layer.GradientOffset;
            if (ImGui.SliderFloat("##gOff", ref go, -1f, 1f, "%.2f"))
            { layer.GradientOffset = go; MarkPreviewDirty(); }
        }

        if (layer.FadeMask != LayerFadeMask.Uniform)
            DrawFadeMaskPreview(layer.FadeMask, layer.FadeMaskFalloff, layer);

        if (string.IsNullOrEmpty(group.MtrlGamePath))
            ImGui.TextColored(new Vector4(1, 0.5f, 0.3f, 1), "需要选择材质(.mtrl)");
    }

    /// <summary>Draw a single PBR field row with unified layout: checkbox + label + value widget.</summary>
    private void DrawPbrField(TargetGroup group, DecalLayer layer, string label,
        Func<bool> get, Action<bool> set,
        bool supported, bool requiresRowPair, bool exhausted,
        Action drawValue, Action? onDisabled = null, Action? onEnabled = null)
    {
        var was = get();
        var v = was;
        var disableCheckbox = (!supported || exhausted) && !was;

        if (disableCheckbox) ImGui.BeginDisabled();
        if (ImGui.Checkbox(label, ref v))
        {
            if (v && !was)
            {
                bool needsAlloc = requiresRowPair && layer.AllocatedRowPair < 0;
                set(true);
                onEnabled?.Invoke();
                if (needsAlloc)
                {
                    if (!previewService.TryAllocateRowPairForLayer(group, layer, out var failure))
                    {
                        set(false);
                        rowPairToast = failure switch
                        {
                            PreviewService.RowPairAllocFailure.Unsupported =>
                                $"无法启用 {label}：当前材质不支持该 PBR 字段。",
                            _ => $"无法启用 {label}：PBR 行号已满。请关闭其他图层后再试。",
                        };
                        rowPairToastUntil = DateTime.UtcNow.AddSeconds(4);
                    }
                    else
                    {
                        MarkPreviewDirty(immediate: true);
                    }
                }
                else
                {
                    MarkPreviewDirty(immediate: true);
                }
            }
            else if (!v && was)
            {
                set(false);
                previewService.ReleaseRowPairIfUnused(group, layer);
                onDisabled?.Invoke();
                MarkPreviewDirty(immediate: true);
            }
        }
        if (disableCheckbox) ImGui.EndDisabled();

        if (was) drawValue();
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
        ImGui.TextDisabled("遮罩预览:");
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
            ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), "遮罩");
        drawList.AddText(new Vector2(colorPos.X, textY),
            ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), "效果");
        ImGui.Dummy(new Vector2(0, 14));
    }
}
