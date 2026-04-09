using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SkinTatoo.Core;
using SkinTatoo.Services;

namespace SkinTatoo.Gui;

/// <summary>
/// Glamourer-style read-only viewer for the PBR pipeline state of a target group:
/// shows the four texture channels (diffuse / normal / index map / vanilla copies)
/// plus a tabular view of the vanilla and currently-built ColorTable rows.
/// Read-only; no mutation of plugin state.
/// </summary>
public class PbrInspectorWindow : Window
{
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly ITextureProvider textureProvider;

    private bool showStaged = true;     // false = vanilla, true = current mod (staged)
    private float thumbSize = 256f;

    public PbrInspectorWindow(
        DecalProject project,
        PreviewService previewService,
        ITextureProvider textureProvider)
        : base("PBR 通道查看器###SkinTatooPbrInspector",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.project = project;
        this.previewService = previewService;
        this.textureProvider = textureProvider;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        DrawToolbar();
        ImGui.Separator();

        var group = project.SelectedGroup;
        if (group == null)
        {
            ImGui.TextDisabled("未选择 TargetGroup");
            return;
        }

        using var scroll = ImRaii.Child("##PbrInspectorScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) return;

        DrawHeader(group);
        ImGui.Separator();
        DrawTextureGrid(group);
        ImGui.Separator();
        DrawColorTableTable(group);
    }

    private void DrawToolbar()
    {
        ImGui.Checkbox("显示当前 Mod (取消勾选 = vanilla)", ref showStaged);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.SliderFloat("缩略图大小", ref thumbSize, 128f, 512f, "%.0f");
    }

    private void DrawHeader(TargetGroup group)
    {
        var supports = previewService.MaterialSupportsPbr(group);
        ImGui.Text($"目标: {group.Name}");
        if (!string.IsNullOrEmpty(group.MtrlGamePath))
            ImGui.TextDisabled($"mtrl: {group.MtrlGamePath}");
        ImGui.TextColored(
            supports ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(1f, 0.7f, 0.3f, 1f),
            supports ? "✓ 支持 PBR (有 g_SamplerIndex 与 ColorTable)"
                     : "ⓘ 不支持 PBR (skin.shpk 类材质，仅发光可用)");
    }

    private void DrawTextureGrid(TargetGroup group)
    {
        ImGui.TextDisabled("纹理通道（左：合成后 / 右：原始 vanilla）");
        var indexGamePath = previewService.GetIndexMapGamePath(group);

        // 2x2 layout: top row = Diffuse + Normal, bottom row = Index + (info)
        DrawThumbRow("漫反射 (Diffuse)", group.DiffuseGamePath);
        DrawThumbRow("法线 (Normal)", group.NormGamePath);
        DrawThumbRow("索引图 (Index Map)", indexGamePath);
    }

    private void DrawThumbRow(string label, string? gamePath)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.85f, 0.85f, 1f, 1f), label);
        if (string.IsNullOrEmpty(gamePath))
        {
            ImGui.TextDisabled("  (该组未配置此纹理)");
            return;
        }

        var stagedDisk = previewService.GetStagedDiskPath(gamePath);
        var resolvedPath = (showStaged && !string.IsNullOrEmpty(stagedDisk)) ? stagedDisk : null;

        try
        {
            // Use staged disk file if available and the user is viewing the mod;
            // otherwise fall back to the vanilla SqPack copy via game path.
            var shared = !string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath)
                ? textureProvider.GetFromFile(resolvedPath)
                : textureProvider.GetFromGame(gamePath!);
            var wrap = shared.GetWrapOrDefault();
            if (wrap == null)
            {
                ImGui.TextDisabled("  (纹理未就绪)");
                return;
            }

            var imgSize = new Vector2(thumbSize, thumbSize);
            ImGui.Image(wrap.Handle, imgSize);
            ImGui.SameLine();
            using (ImRaii.Group())
            {
                ImGui.TextDisabled($"游戏路径: {gamePath}");
                if (showStaged && !string.IsNullOrEmpty(stagedDisk))
                    ImGui.TextDisabled($"Mod 文件: {stagedDisk}");
                else
                    ImGui.TextDisabled("(显示 vanilla)");
                ImGui.TextDisabled($"实际像素: {wrap.Width}x{wrap.Height}");
            }
        }
        catch (Exception ex)
        {
            ImGui.TextDisabled($"  (加载失败: {ex.Message})");
        }
    }

    private void DrawColorTableTable(TargetGroup group)
    {
        ImGui.TextDisabled("ColorTable 行（仅 character.shpk 类材质）");
        var vanilla = previewService.GetVanillaColorTable(group);
        var current = previewService.GetLastBuiltColorTable(group);

        if (vanilla == null)
        {
            ImGui.TextDisabled("vanilla ColorTable 尚未缓存——触发一次预览后再查看");
            return;
        }
        if (!ColorTableBuilder.IsDawntrailLayout(vanilla.Value.Width, vanilla.Value.Height))
        {
            ImGui.TextDisabled($"非 Dawntrail 布局: {vanilla.Value.Width}×{vanilla.Value.Height}（暂不可视化）");
            return;
        }

        // Pick which table to show — staged-current if available, otherwise vanilla.
        var table = (showStaged && current.HasValue) ? current.Value : vanilla.Value;
        bool isCurrent = (showStaged && current.HasValue);
        ImGui.TextDisabled(isCurrent ? "（显示当前 mod 修改后的）" : "（显示 vanilla / 未修改）");

        // Dawntrail layout: 32 rows × 8 vec4. Render each row as a colored swatch
        // showing diffuse / specular / emissive vec4 RGB and the scalar Roughness/Metalness.
        int width = table.Width;
        int height = table.Height;
        int rowStride = width * 4;   // halves per row
        var data = table.Data;

        if (ImGui.BeginTable("##PbrColorTable", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("行", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableSetupColumn("Pair", ImGuiTableColumnFlags.WidthFixed, 36);
            ImGui.TableSetupColumn("漫反射", ImGuiTableColumnFlags.WidthFixed, 64);
            ImGui.TableSetupColumn("镜面", ImGuiTableColumnFlags.WidthFixed, 64);
            ImGui.TableSetupColumn("发光", ImGuiTableColumnFlags.WidthFixed, 64);
            ImGui.TableSetupColumn("粗糙度", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("金属度", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();

            for (int row = 0; row < height; row++)
            {
                int baseIdx = row * rowStride;
                int pair = row / 2;

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(row.ToString());
                ImGui.TableNextColumn();
                ImGui.Text(pair.ToString());

                // Dawntrail offsets per ColorTableBuilder layout
                ImGui.TableNextColumn(); DrawColorSwatch(
                    (float)data[baseIdx + 0], (float)data[baseIdx + 1], (float)data[baseIdx + 2]);
                ImGui.TableNextColumn(); DrawColorSwatch(
                    (float)data[baseIdx + 4], (float)data[baseIdx + 5], (float)data[baseIdx + 6]);
                ImGui.TableNextColumn(); DrawColorSwatch(
                    (float)data[baseIdx + 8], (float)data[baseIdx + 9], (float)data[baseIdx + 10]);

                ImGui.TableNextColumn();
                ImGui.Text($"{(float)data[baseIdx + 16]:F2}");
                ImGui.TableNextColumn();
                ImGui.Text($"{(float)data[baseIdx + 18]:F2}");
            }
            ImGui.EndTable();
        }
    }

    private static void DrawColorSwatch(float r, float g, float b)
    {
        var col = new Vector4(
            Math.Clamp(r, 0f, 1f),
            Math.Clamp(g, 0f, 1f),
            Math.Clamp(b, 0f, 1f),
            1f);
        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(56, ImGui.GetFrameHeight());
        draw.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(col), 2f);
        draw.AddRect(pos, pos + size,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.5f)), 2f);
        ImGui.Dummy(size);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"R={r:F3}\nG={g:F3}\nB={b:F3}");
    }
}
