using System;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SkinTattoo.Http;

namespace SkinTattoo.Gui;

public class DebugWindow : Window
{
    private string logFilter = string.Empty;
    private bool logAutoScroll = true;

    public DebugWindow()
        : base("SkinTattoo 调试###SkinTattooDebug",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(500, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        // Toolbar
        if (ImGui.Button("清空"))
        {
            while (DebugServer.LogBuffer.TryDequeue(out _)) { }
        }

        ImGui.SameLine();
        if (ImGui.Button("复制全部"))
        {
            var sb = new StringBuilder();
            foreach (var line in DebugServer.LogBuffer)
                sb.AppendLine(line);
            ImGui.SetClipboardText(sb.ToString());
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

            ImGui.Selectable(line);
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                ImGui.SetClipboardText(line);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("右键复制此行");
        }

        if (logAutoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
            ImGui.SetScrollHereY(1.0f);
    }
}
