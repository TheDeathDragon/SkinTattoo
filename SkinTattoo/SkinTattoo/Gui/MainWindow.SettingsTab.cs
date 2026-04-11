using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace SkinTattoo.Gui;

public partial class MainWindow
{
    private int settingsPendingSwapInterval;
    private bool settingsDraggingSwapInterval;

    private const int SwapIntervalMin = 33;
    private const int SwapIntervalMax = 500;
    private const int SwapIntervalDefault = 150;

    private void DrawSettingsTab()
    {
        using var scroll = ImRaii.Child("##SettingsScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) return;

        var enabled = config.PluginEnabled;
        if (ImGui.Checkbox("启用 SkinTattoo", ref enabled))
        {
            config.PluginEnabled = enabled;
            config.Save();
            if (!enabled)
            {
                penumbra.ClearRedirect();
                penumbra.RedrawPlayer();
                previewService.ClearTextureCache();
                previewService.ResetSwapState();
                Http.DebugServer.AppendLog("[Settings] Plugin disabled  -- cleared all effects");
            }
            else
            {
                TriggerPreview();
                Http.DebugServer.AppendLog("[Settings] Plugin enabled  -- re-applied preview");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("全局启用或禁用插件的贴花效果。\n关闭后会还原所有贴图到原始状态。");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        const float labelW = 110f;

        if (ImGui.CollapsingHeader("刷新设置", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();

            if (!settingsDraggingSwapInterval)
                settingsPendingSwapInterval = Math.Clamp(
                    config.GameSwapIntervalMs, SwapIntervalMin, SwapIntervalMax);

            ImGui.AlignTextToFramePadding();
            ImGui.Text("刷新间隔");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("拖拽调整参数时，将修改同步到游戏贴图的最小间隔（毫秒）。\n"
                               + "数值越小越实时，但在大分辨率下会占用更多主线程时间。\n"
                               + "3D 编辑器预览不受此限制，松手时总会立即同步。");
            ImGui.SameLine(labelW);
            ImGui.SetNextItemWidth(160);
            ImGui.SliderInt("##SwapInt", ref settingsPendingSwapInterval,
                            SwapIntervalMin, SwapIntervalMax, "%d ms");
            settingsDraggingSwapInterval = ImGui.IsItemActive();
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                config.GameSwapIntervalMs = settingsPendingSwapInterval;
                config.Save();
                settingsDraggingSwapInterval = false;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("拖动滑条修改数值。");

            ImGui.SameLine();
            if (ImGui.SmallButton("默认"))
            {
                settingsPendingSwapInterval = SwapIntervalDefault;
                config.GameSwapIntervalMs = SwapIntervalDefault;
                config.Save();
                settingsDraggingSwapInterval = false;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"恢复默认值 ({SwapIntervalDefault}ms)");

            ImGui.Unindent();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("UV 网格线框", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();

            var uvAA = config.UvWireframeAntiAlias;
            if (ImGui.Checkbox("抗锯齿##uvAA", ref uvAA))
            {
                config.UvWireframeAntiAlias = uvAA;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("开启后 UV 网格线框使用抗锯齿渲染，线条更平滑。\n"
                               + "关闭可显著提升线框渲染性能，适用于高面数模型。");

            var uvCull = config.UvWireframeCulling;
            if (ImGui.Checkbox("视口外剔除##uvCull", ref uvCull))
            {
                config.UvWireframeCulling = uvCull;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("跳过完全在画布可见区域之外的三角形。\n"
                               + "开启可减少画布滚动/缩放时的 CPU 绘制开销。");

            var uvDedup = config.UvWireframeDedup;
            if (ImGui.Checkbox("共享边去重##uvDedup", ref uvDedup))
            {
                config.UvWireframeDedup = uvDedup;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("相邻三角形的共享边只绘制一次，减少约 50%% 绘制量。\n"
                               + "会增加少量 CPU 预处理开销，网格面数极高时效益最明显。");

            ImGui.Unindent();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("HTTP 调试服务器"))
        {
            ImGui.Indent();

            var httpEnabled = config.HttpEnabled;
            if (ImGui.Checkbox("启用 HTTP 服务器##httpEn", ref httpEnabled))
            {
                config.HttpEnabled = httpEnabled;
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("开启后在本地端口提供 REST API，\n可用于外部工具自动化操作。\n修改后需重启插件生效。");

            ImGui.Spacing();

            ImGui.AlignTextToFramePadding();
            ImGui.Text("端口号");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("本地 HTTP 调试服务器监听端口。\n修改后需重启插件生效。\n默认: 12580");
            ImGui.SameLine(labelW);
            ImGui.SetNextItemWidth(120);
            var port = config.HttpPort;
            if (ImGui.InputInt("##port", ref port, 1, 100))
            {
                if (port is >= 1024 and <= 65535)
                {
                    config.HttpPort = port;
                    config.Save();
                }
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("有效范围: 1024 - 65535");

            ImGui.Unindent();
        }

        ImGui.Spacing();

        if (ImGui.CollapsingHeader("调试"))
        {
            ImGui.Indent();

            if (ImGui.Button("打开调试日志窗口"))
            {
                if (DebugWindowRef != null)
                    DebugWindowRef.IsOpen = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("查看插件运行时日志。");

            ImGui.Unindent();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1),
            "修改 HTTP 开关或端口后需重启插件生效。");
    }
}
