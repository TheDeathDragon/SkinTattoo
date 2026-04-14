using System;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SkinTattoo.Http;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public class DebugWindow : Window
{
    private string logFilter = string.Empty;
    private bool logAutoScroll = true;

    public DebugWindow()
        : base(Strings.T("window.debug.title") + "###SkinTattooDebug",
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
        if (ImGui.Button(Strings.T("button.clear")))
        {
            while (DebugServer.LogBuffer.TryDequeue(out _)) { }
        }

        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.copy_all")))
        {
            var sb = new StringBuilder();
            foreach (var line in DebugServer.LogBuffer)
                sb.AppendLine(line);
            ImGui.SetClipboardText(sb.ToString());
        }

        ImGui.SameLine();
        ImGui.Checkbox(Strings.T("label.auto_scroll"), ref logAutoScroll);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##LogFilter", Strings.T("label.filter_hint"), ref logFilter, 256);

        ImGui.SameLine();
        ImGui.TextDisabled($"({DebugServer.LogBuffer.Count})");

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
                ImGui.SetTooltip(Strings.T("tooltip.copy_row"));
        }

        if (logAutoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
            ImGui.SetScrollHereY(1.0f);
    }
}
