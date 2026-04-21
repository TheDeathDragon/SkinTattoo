using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace SkinTattoo.Gui;

// Mirrors CustomizePlus's CtrlHelper pattern: a real InfoCircle icon for help,
// rendered before the visible text. The first arg is the ImGui id (with ##),
// the second arg is the visible label.
internal static class UiHelpers
{
    private const float HelpWrapMul = 35f;

    public static bool CheckboxWithTextAndHelp(string id, string text, string helpText, ref bool value)
    {
        var changed = ImGui.Checkbox(id, ref value);
        ImGui.SameLine();
        DrawInfoIcon();
        AddHoverText(helpText);
        ImGui.SameLine();
        ImGui.TextUnformatted(StripImGuiId(text));
        AddHoverText(helpText);
        return changed;
    }

    private static string StripImGuiId(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var idx = text.IndexOf("##", System.StringComparison.Ordinal);
        return idx < 0 ? text : text[..idx];
    }

    public static void LabelWithHelp(string text, string helpText)
    {
        DrawInfoIcon();
        AddHoverText(helpText);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(text);
        AddHoverText(helpText);
    }

    public static void DrawInfoIcon()
    {
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());
        ImGui.PopFont();
    }

    public static void AddHoverText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * HelpWrapMul);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
