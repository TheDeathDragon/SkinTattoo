using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace SkinTattoo.Gui;

internal static class UiHelpers
{
    private const float HelpWrapMul = 35f;

    public static bool CheckboxWithHelp(string label, ref bool value, string help)
    {
        var changed = ImGui.Checkbox(label, ref value);
        DrawHelpMarker(help);
        return changed;
    }

    public static void LabelWithHelp(string label, string help)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        DrawHelpMarker(help);
    }

    public static void DrawHelpMarker(string help)
    {
        if (string.IsNullOrEmpty(help)) return;
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * HelpWrapMul);
        ImGui.TextUnformatted(help);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
