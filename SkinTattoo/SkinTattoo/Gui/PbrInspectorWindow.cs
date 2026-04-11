using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SkinTattoo.Core;
using SkinTattoo.Services;

namespace SkinTattoo.Gui;

public class PbrInspectorWindow : Window
{
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly ITextureProvider textureProvider;

    private bool showStaged = true;
    private float thumbSize = 200f;
    private int selectedTexTab;
    private static readonly string[] TexTabNames = ["漫反射", "法线", "索引图"];

    public PbrInspectorWindow(
        DecalProject project,
        PreviewService previewService,
        ITextureProvider textureProvider)
        : base("PBR 通道查看器###SkinTattooPbrInspector",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.project = project;
        this.previewService = previewService;
        this.textureProvider = textureProvider;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var group = project.SelectedGroup;
        if (group == null)
        {
            ImGui.TextDisabled("未选择 TargetGroup");
            return;
        }

        DrawHeader(group);
        ImGui.Separator();

        using var scroll = ImRaii.Child("##PbrInspectorScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) return;

        DrawTextureSection(group);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawColorTableSection(group);
    }

    private void DrawHeader(TargetGroup group)
    {
        // Target info + toggle
        var supports = previewService.MaterialSupportsPbr(group);

        ImGui.AlignTextToFramePadding();
        var statusColor = supports
            ? new Vector4(0.3f, 0.9f, 0.3f, 1f)
            : new Vector4(1f, 0.7f, 0.3f, 1f);
        ImGui.TextColored(statusColor, supports ? "[PBR]" : "[Emissive]");
        ImGui.SameLine();
        ImGui.Text(group.Name);

        if (!string.IsNullOrEmpty(group.MtrlGamePath))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"| {group.MtrlGamePath}");
        }

        // Right-aligned toggle
        var toggleText = showStaged ? "Mod" : "Vanilla";
        var toggleSize = ImGui.CalcTextSize(toggleText);
        ImGui.SameLine(ImGui.GetContentRegionMax().X - toggleSize.X - 16);
        if (ImGui.SmallButton(toggleText))
            showStaged = !showStaged;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(showStaged ? "当前显示 Mod 修改后  -- 点击切换到 Vanilla" : "当前显示 Vanilla  -- 点击切换到 Mod");
    }

    private void DrawTextureSection(TargetGroup group)
    {
        // Tab bar for texture channels
        if (ImGui.BeginTabBar("##TexTabs"))
        {
            var indexGamePath = previewService.GetIndexMapGamePath(group);
            string?[] paths = [group.DiffuseGamePath, group.NormGamePath, indexGamePath];

            for (int i = 0; i < TexTabNames.Length; i++)
            {
                var tabFlags = (i == selectedTexTab) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                if (ImGui.BeginTabItem(TexTabNames[i], tabFlags))
                {
                    selectedTexTab = i;
                    ImGui.Spacing();
                    DrawTexturePreview(TexTabNames[i], paths[i]);
                    ImGui.EndTabItem();
                }
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawTexturePreview(string label, string? gamePath)
    {
        if (string.IsNullOrEmpty(gamePath))
        {
            ImGui.TextDisabled("该组未配置此纹理");
            return;
        }

        var stagedDisk = previewService.GetStagedDiskPath(gamePath);
        var resolvedPath = (showStaged && !string.IsNullOrEmpty(stagedDisk)) ? stagedDisk : null;

        try
        {
            var shared = !string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath)
                ? textureProvider.GetFromFile(resolvedPath)
                : textureProvider.GetFromGame(gamePath!);
            var wrap = shared.GetWrapOrDefault();
            if (wrap == null)
            {
                ImGui.TextDisabled("纹理未就绪");
                return;
            }

            // Size slider
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("大小");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.SliderFloat("##thumbSz", ref thumbSize, 128f, 512f, "%.0f");

            // Texture display
            var avail = ImGui.GetContentRegionAvail().X;
            var displaySize = Math.Min(thumbSize, avail);
            ImGui.Image(wrap.Handle, new Vector2(displaySize, displaySize));

            // Info below
            ImGui.TextDisabled($"像素: {wrap.Width} x {wrap.Height}");
            ImGui.TextDisabled($"路径: {gamePath}");
            if (showStaged && !string.IsNullOrEmpty(stagedDisk))
                ImGui.TextDisabled($"Mod:  {stagedDisk}");
        }
        catch (Exception ex)
        {
            ImGui.TextDisabled($"加载失败: {ex.Message}");
        }
    }

    private void DrawColorTableSection(TargetGroup group)
    {
        ImGui.TextColored(new Vector4(0.85f, 0.85f, 1f, 1f), "ColorTable");
        ImGui.SameLine();
        ImGui.TextDisabled("(仅 character.shpk 类材质)");

        var vanilla = previewService.GetVanillaColorTable(group);
        var current = previewService.GetLastBuiltColorTable(group);

        if (vanilla == null)
        {
            ImGui.TextDisabled("触发一次预览后再查看");
            return;
        }
        if (!ColorTableBuilder.IsDawntrailLayout(vanilla.Value.Width, vanilla.Value.Height))
        {
            ImGui.TextDisabled($"非 Dawntrail 布局: {vanilla.Value.Width} x {vanilla.Value.Height}");
            return;
        }

        var table = (showStaged && current.HasValue) ? current.Value : vanilla.Value;
        bool isCurrent = (showStaged && current.HasValue);
        ImGui.TextDisabled(isCurrent ? "(显示 Mod 修改后)" : "(显示 Vanilla)");

        int width = table.Width;
        int height = table.Height;
        int rowStride = width * 4;
        var data = table.Data;

        ImGui.Spacing();

        if (ImGui.BeginTable("##PbrColorTable", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit
            | ImGuiTableFlags.ScrollY,
            new Vector2(-1, ImGui.GetContentRegionAvail().Y)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("行", ImGuiTableColumnFlags.WidthFixed, 28);
            ImGui.TableSetupColumn("对", ImGuiTableColumnFlags.WidthFixed, 28);
            ImGui.TableSetupColumn("漫反射", ImGuiTableColumnFlags.WidthFixed, 56);
            ImGui.TableSetupColumn("镜面", ImGuiTableColumnFlags.WidthFixed, 56);
            ImGui.TableSetupColumn("发光", ImGuiTableColumnFlags.WidthFixed, 56);
            ImGui.TableSetupColumn("粗糙", ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn("金属", ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableHeadersRow();

            for (int row = 0; row < height; row++)
            {
                int baseIdx = row * rowStride;
                int pair = row / 2;

                ImGui.TableNextRow();

                // Row index
                ImGui.TableNextColumn();
                ImGui.TextDisabled(row.ToString());

                // Pair index
                ImGui.TableNextColumn();
                ImGui.TextDisabled(pair.ToString());

                // Diffuse
                ImGui.TableNextColumn();
                DrawColorSwatch(
                    (float)data[baseIdx + 0], (float)data[baseIdx + 1], (float)data[baseIdx + 2]);

                // Specular
                ImGui.TableNextColumn();
                DrawColorSwatch(
                    (float)data[baseIdx + 4], (float)data[baseIdx + 5], (float)data[baseIdx + 6]);

                // Emissive
                ImGui.TableNextColumn();
                DrawColorSwatch(
                    (float)data[baseIdx + 8], (float)data[baseIdx + 9], (float)data[baseIdx + 10]);

                // Roughness
                ImGui.TableNextColumn();
                var rough = (float)data[baseIdx + 16];
                ImGui.Text($"{rough:F2}");

                // Metalness
                ImGui.TableNextColumn();
                var metal = (float)data[baseIdx + 18];
                ImGui.Text($"{metal:F2}");
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
        var size = new Vector2(48, ImGui.GetFrameHeight() - 2);
        draw.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(col), 3f);
        draw.AddRect(pos, pos + size,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.3f, 0.3f, 0.6f)), 3f);
        ImGui.Dummy(size);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"R={r:F3}  G={g:F3}  B={b:F3}");
    }
}
