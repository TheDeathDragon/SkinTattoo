using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SkinTatoo.Http;
using SkinTatoo.Interop;
using SkinTatoo.Services;

namespace SkinTatoo.Gui;

public class DebugWindow : Window
{
    private readonly PenumbraBridge penumbra;
    private readonly PreviewService previewService;
    private readonly ITextureProvider textureProvider;
    private readonly Configuration config;

    // Resource tab state
    private List<ResourceEntry>? cachedResources;
    private int resourceFilter; // 0=all, 1=mdl, 2=tex, 3=mtrl
    private static readonly string[] FilterLabels = ["全部", "模型", "贴图", "材质"];
    private static readonly string[] FilterExts = ["", ".mdl", ".tex", ".mtrl"];

    // Log tab state
    private string logFilter = string.Empty;
    private bool logAutoScroll = true;

    public DebugWindow(
        PenumbraBridge penumbra,
        PreviewService previewService,
        ITextureProvider textureProvider,
        Configuration config)
        : base("SkinTatoo 调试###SkinTatooDebug",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.penumbra = penumbra;
        this.previewService = previewService;
        this.textureProvider = textureProvider;
        this.config = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(650, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##DebugTabs");
        if (!tabs.Success) return;

        using (var tab1 = ImRaii.TabItem("玩家资源"))
        {
            if (tab1.Success)
                DrawResourceTab();
        }

        using (var tab2 = ImRaii.TabItem("日志"))
        {
            if (tab2.Success)
                DrawLogTab();
        }
    }

    private void DrawResourceTab()
    {
        // Toolbar
        if (ImGui.Button("刷新"))
            RefreshResources();

        ImGui.SameLine();
        ImGui.TextDisabled(penumbra.IsAvailable ? "Penumbra 已连接" : "Penumbra 未连接");

        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();

        // Filter buttons
        for (var i = 0; i < FilterLabels.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            var selected = resourceFilter == i;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
            if (ImGui.SmallButton(FilterLabels[i]))
                resourceFilter = i;
            if (selected) ImGui.PopStyleColor();
        }

        ImGui.Separator();

        if (!penumbra.IsAvailable)
        {
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "Penumbra 未连接");
            return;
        }

        if (cachedResources == null || cachedResources.Count == 0)
        {
            ImGui.TextDisabled("点击「刷新」查询玩家资源");
            return;
        }

        // Filtered list
        var filtered = resourceFilter == 0
            ? cachedResources
            : cachedResources.Where(e => e.Extension == FilterExts[resourceFilter]).ToList();

        if (ImGui.BeginTable("##ResourceTable", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
            new Vector2(-1, -1)))
        {
            ImGui.TableSetupColumn("预览", ImGuiTableColumnFlags.WidthFixed, 68);
            ImGui.TableSetupColumn("信息", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            for (var i = 0; i < filtered.Count; i++)
            {
                var entry = filtered[i];
                ImGui.PushID(i);
                ImGui.TableNextRow(ImGuiTableRowFlags.None, 68);

                // Preview thumbnail (64x64)
                ImGui.TableNextColumn();
                if (entry.Extension == ".tex")
                {
                    try
                    {
                        var shared = File.Exists(entry.DiskPath)
                            ? textureProvider.GetFromFile(entry.DiskPath)
                            : textureProvider.GetFromGame(entry.GamePath);
                        var wrap = shared.GetWrapOrDefault();
                        if (wrap != null)
                        {
                            ImGui.Image(wrap.Handle, new Vector2(64, 64));
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.BeginTooltip();
                                ImGui.Image(wrap.Handle, new Vector2(384, 384));
                                ImGui.Text($"{wrap.Width}x{wrap.Height}");
                                ImGui.EndTooltip();
                            }
                        }
                    }
                    catch { }
                }

                // Info: game path + disk path (click to copy)
                ImGui.TableNextColumn();
                var badgeColor = entry.Extension switch
                {
                    ".mdl" => new Vector4(0.3f, 0.7f, 1f, 1f),
                    ".tex" => new Vector4(0.3f, 1f, 0.5f, 1f),
                    ".mtrl" => new Vector4(1f, 0.8f, 0.3f, 1f),
                    _ => new Vector4(0.7f, 0.7f, 0.7f, 1f),
                };

                // Game path (click to copy)
                ImGui.TextColored(badgeColor, entry.GamePath);
                if (ImGui.IsItemClicked())
                    ImGui.SetClipboardText(entry.GamePath);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("点击复制游戏路径");

                // Disk path (click to copy)
                if (entry.DiskPath != entry.GamePath)
                {
                    ImGui.TextDisabled(entry.DiskPath);
                    if (ImGui.IsItemClicked())
                        ImGui.SetClipboardText(entry.DiskPath);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("点击复制磁盘路径");
                }

                // Type
                ImGui.TableNextColumn();
                ImGui.TextColored(badgeColor, entry.Extension);

                // Actions — unified "使用" button for both .mdl and .tex
                ImGui.TableNextColumn();
                if (entry.Extension == ".mdl")
                {
                    var isCurrent = config.TargetMeshDiskPath == entry.DiskPath;
                    if (isCurrent)
                    {
                        ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "✓ 网格");
                    }
                    else if (ImGui.SmallButton("使用"))
                    {
                        previewService.LoadMesh(entry.DiskPath);
                        config.TargetMeshDiskPath = entry.DiskPath;
                        config.Save();
                        DebugServer.AppendLog($"[DebugWindow] Mesh set: {entry.DiskPath}");
                    }
                }
                else if (entry.Extension == ".tex")
                {
                    // Show current role of this texture
                    var isDiffuse = config.TargetTextureGamePath == entry.GamePath;
                    var isNorm = config.TargetNormGamePath == entry.GamePath;
                    var isMask = config.TargetMaskGamePath == entry.GamePath;

                    if (isDiffuse) ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "漫反射");
                    else if (isNorm) ImGui.TextColored(new Vector4(0.5f, 0.5f, 1f, 1f), "法线");
                    else if (isMask) ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "遮罩");
                    else if (ImGui.SmallButton("使用"))
                    {
                        // Auto-detect type by name
                        var gp = entry.GamePath.ToLowerInvariant();
                        if (gp.Contains("norm"))
                        {
                            config.TargetNormGamePath = entry.GamePath;
                            config.TargetNormDiskPath = entry.DiskPath;
                            DebugServer.AppendLog($"[DebugWindow] Normal set: {entry.GamePath}");
                        }
                        else if (gp.Contains("mask"))
                        {
                            config.TargetMaskGamePath = entry.GamePath;
                            config.TargetMaskDiskPath = entry.DiskPath;
                            DebugServer.AppendLog($"[DebugWindow] Mask set: {entry.GamePath}");
                        }
                        else
                        {
                            config.TargetTextureGamePath = entry.GamePath;
                            config.TargetTextureDiskPath = entry.DiskPath;
                            DebugServer.AppendLog($"[DebugWindow] Diffuse set: {entry.GamePath}");

                            // Clear caches and old targets when changing diffuse
                            previewService.ClearTextureCache();
                            penumbra.ClearRedirect();
                            config.TargetNormGamePath = null;
                            config.TargetNormDiskPath = null;
                            config.TargetMaskGamePath = null;
                            config.TargetMaskDiskPath = null;

                            // Auto-find norm/mask with same prefix
                            AutoAssociateTextures(entry.GamePath, entry.DiskPath);
                            AutoLoadAssociatedMesh(entry.GamePath);
                        }
                        config.Save();
                    }
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private void DrawLogTab()
    {
        // Toolbar
        if (ImGui.Button("清空"))
        {
            while (DebugServer.LogBuffer.TryDequeue(out _)) { }
        }

        ImGui.SameLine();
        ImGui.Checkbox("自动滚动", ref logAutoScroll);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##LogFilter", "过滤...", ref logFilter, 256);

        ImGui.SameLine();
        ImGui.TextDisabled($"({DebugServer.LogBuffer.Count} 条)");

        ImGui.Separator();

        using var child = ImRaii.Child("##LogViewer", new Vector2(-1, -1), true);
        if (!child.Success) return;

        var hasFilter = !string.IsNullOrEmpty(logFilter);
        foreach (var line in DebugServer.LogBuffer)
        {
            if (hasFilter && !line.Contains(logFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            ImGui.TextWrapped(line);
        }

        if (logAutoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
            ImGui.SetScrollHereY(1.0f);
    }

    private void RefreshResources()
    {
        cachedResources = [];
        var resources = penumbra.GetPlayerResources();
        if (resources == null) return;

        foreach (var (objIdx, paths) in resources)
        {
            foreach (var (diskPath, gamePaths) in paths)
            {
                var ext = Path.GetExtension(diskPath).ToLowerInvariant();
                if (ext is not (".mdl" or ".tex" or ".mtrl")) continue;

                foreach (var gp in gamePaths)
                {
                    cachedResources.Add(new ResourceEntry
                    {
                        DiskPath = diskPath,
                        GamePath = gp,
                        Extension = ext,
                    });
                }
            }
        }
    }

    // Auto-find norm and mask textures — prefer paths sharing the same directory prefix as diffuse
    private void AutoAssociateTextures(string diffuseGamePath, string diffuseDiskPath)
    {
        if (cachedResources == null) return;

        // Extract directory prefix from diffuse path for matching
        // e.g., "chara/nyaughty/eve/gen3_raen_base.tex" → "chara/nyaughty/eve/"
        var diffuseDir = diffuseGamePath.Contains('/')
            ? diffuseGamePath[..(diffuseGamePath.LastIndexOf('/') + 1)]
            : "";

        string? bestNorm = null, bestNormDisk = null;
        string? bestMask = null, bestMaskDisk = null;

        foreach (var res in cachedResources)
        {
            if (res.Extension != ".tex") continue;
            var gp = res.GamePath.ToLowerInvariant();

            // Skip face, eye, hair, weapon, tail textures
            if (gp.Contains("/face/") || gp.Contains("eye") || gp.Contains("/hair/") ||
                gp.Contains("weapon/") || gp.Contains("/tail/")) continue;

            var sameDir = !string.IsNullOrEmpty(diffuseDir) &&
                          res.GamePath.StartsWith(diffuseDir, StringComparison.OrdinalIgnoreCase);

            if (gp.Contains("norm"))
            {
                // Prefer same directory match over any match
                if (bestNorm == null || (sameDir && !bestNorm.StartsWith(diffuseDir, StringComparison.OrdinalIgnoreCase)))
                {
                    bestNorm = res.GamePath;
                    bestNormDisk = res.DiskPath;
                }
            }
            else if (gp.Contains("mask"))
            {
                if (bestMask == null || (sameDir && !bestMask.StartsWith(diffuseDir, StringComparison.OrdinalIgnoreCase)))
                {
                    bestMask = res.GamePath;
                    bestMaskDisk = res.DiskPath;
                }
            }
        }

        if (bestNorm != null)
        {
            config.TargetNormGamePath = bestNorm;
            config.TargetNormDiskPath = bestNormDisk;
            DebugServer.AppendLog($"[DebugWindow] Auto-associated norm: {bestNorm}");
        }
        if (bestMask != null)
        {
            config.TargetMaskGamePath = bestMask;
            config.TargetMaskDiskPath = bestMaskDisk;
            DebugServer.AppendLog($"[DebugWindow] Auto-associated mask: {bestMask}");
        }
    }

    // When user selects a tex target, try to find and load the body mdl from the same group
    private void AutoLoadAssociatedMesh(string texGamePath)
    {
        if (cachedResources == null) return;

        // Strategy: find a body .mdl in the same resource list
        // For body textures (b0001), look for e0000_top.mdl (equipment body slot) or b00XX_top.mdl
        string? bestMdl = null;

        foreach (var res in cachedResources)
        {
            if (res.Extension != ".mdl") continue;

            // Body slot equipment model (most common for body mods)
            if (res.GamePath.Contains("e0000") && res.GamePath.Contains("_top.mdl"))
            {
                bestMdl = res.DiskPath;
                break;
            }

            // Direct body model
            if (res.GamePath.Contains("obj/body/") && res.GamePath.Contains("_top.mdl"))
                bestMdl ??= res.DiskPath;
        }

        if (bestMdl != null)
        {
            var ok = previewService.LoadMesh(bestMdl);
            if (ok)
            {
                config.TargetMeshDiskPath = bestMdl;
                DebugServer.AppendLog($"[DebugWindow] Auto-loaded mesh: {bestMdl}");
            }
        }
    }

    private class ResourceEntry
    {
        public string DiskPath { get; init; } = "";
        public string GamePath { get; init; } = "";
        public string Extension { get; init; } = "";
    }
}
