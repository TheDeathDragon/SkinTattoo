using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using SkinTattoo.Services;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public partial class MainWindow
{
    private static readonly Vector4 ChangelogLinkColor = new(0.45f, 0.75f, 1f, 1f);

    private void DrawChangelogTab()
    {
        using var scroll = ImRaii.Child("##ChangelogScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) return;

        var entries = changelogService.Entries;
        if (entries.Count == 0)
        {
            ImGui.TextDisabled(Strings.T("changelog.empty"));
            return;
        }

        var lang = Strings.Manager.CurrentLanguage;
        var versionColor = new Vector4(1f, 0.8f, 0.3f, 1f);
        var dateColor = new Vector4(0.6f, 0.6f, 0.6f, 1f);

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var header = $"v{entry.Version}##chglog{i}";
            var flags = i == 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;

            ImGui.PushStyleColor(ImGuiCol.Text, versionColor);
            var open = ImGui.CollapsingHeader(header, flags);
            ImGui.PopStyleColor();

            if (open)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, dateColor);
                ImGui.TextUnformatted(entry.Date);
                ImGui.PopStyleColor();
                ImGui.Spacing();

                foreach (var bullet in entry.BulletsFor(lang))
                    DrawBullet(bullet);

                ImGui.Spacing();
            }
        }
    }

    private static void DrawBullet(ChangelogBullet bullet)
    {
        if (bullet.Links.Count == 0)
        {
            ImGui.BulletText(bullet.Text);
            return;
        }

        var text = bullet.Text;
        int cursor = 0;
        bool bulletDrawn = false;

        foreach (var link in bullet.Links)
        {
            if (string.IsNullOrEmpty(link.Label)) continue;
            int idx = text.IndexOf(link.Label, cursor, StringComparison.Ordinal);
            if (idx < 0) continue;

            var pre = idx > cursor ? text.Substring(cursor, idx - cursor) : "";
            if (!bulletDrawn)
            {
                // Use BulletText to match the glyph/spacing of link-less bullets.
                ImGui.BulletText(pre);
                bulletDrawn = true;
            }
            else if (pre.Length > 0)
            {
                ImGui.SameLine(0, 0);
                ImGui.TextUnformatted(pre);
            }
            ImGui.SameLine(0, 0);
            DrawLink(link.Label, link.Url);
            cursor = idx + link.Label.Length;
        }

        if (!bulletDrawn)
        {
            ImGui.BulletText(text);
            return;
        }

        if (cursor < text.Length)
        {
            ImGui.SameLine(0, 0);
            ImGui.TextUnformatted(text.Substring(cursor));
        }
    }

    private static void DrawLink(string label, string url)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ChangelogLinkColor);
        ImGui.TextUnformatted(label);
        ImGui.PopStyleColor();

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(min.X, max.Y - 1),
            new Vector2(max.X, max.Y - 1),
            ImGui.ColorConvertFloat4ToU32(ChangelogLinkColor));

        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip(url);
        }
        if (ImGui.IsItemClicked())
            OpenUrl(url);
    }
}
