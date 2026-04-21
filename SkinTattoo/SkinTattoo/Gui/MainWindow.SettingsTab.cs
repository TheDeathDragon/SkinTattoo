using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public partial class MainWindow
{
    private int settingsPendingSwapInterval;
    private bool settingsDraggingSwapInterval;

    private const int SwapIntervalMin = 33;
    private const int SwapIntervalMax = 500;
    private const int SwapIntervalDefault = 150;

    private const string RepoUrl = "https://github.com/TheDeathDragon/SkinTattoo";
    private const string DiscordUrl = "https://discord.gg/FPY94anSRN";

    private void DrawLanguageSelector()
    {
        var mgr = Strings.Manager;
        var current = mgr.CurrentLanguage;
        var supported = mgr.SupportedLanguages;
        var available = mgr.AvailableLanguages;

        var preview = supported.TryGetValue(current, out var dn) ? dn : current;

        ImGui.AlignTextToFramePadding();
        ImGui.Text(Strings.T("label.language"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200f);
        if (ImGui.BeginCombo("##LanguageCombo", preview))
        {
            foreach (var kv in supported)
            {
                var code = kv.Key;
                var display = kv.Value;
                var selected = code == current;
                var exists = available.ContainsKey(code);
                if (!exists) display += " (missing)";

                if (ImGui.Selectable(display, selected))
                {
                    if (code != current && exists)
                    {
                        try
                        {
                            mgr.LoadLanguage(code);
                            config.Language = code;
                            config.Save();
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log?.Error(ex, "Language switch failed");
                        }
                    }
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
    }

    private void DrawSettingsTab()
    {
        var topY = ImGui.GetCursorPosY();

        using (var scroll = ImRaii.Child("##SettingsScroll", new Vector2(-1, -1), false))
        {
            if (scroll.Success)
            {
                DrawGeneralSection();

                var shpkConflict = previewService.SkinShpkModConflict;
                if (!string.IsNullOrEmpty(shpkConflict))
                {
                    ImGui.Spacing();
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
                    ImGui.TextWrapped("Detected another Mod modifying skin.shpk:");
                    ImGui.PopStyleColor();
                    ImGui.TextWrapped(shpkConflict);
                    ImGui.TextWrapped("When emissive is enabled, this plugin's skin.shpk will override that Mod's shader. " +
                        "If rendering issues occur, disable the conflicting Mod in Penumbra.");
                }

                ImGui.Spacing();
                ImGui.Spacing();

                DrawInterfaceSection();
                DrawPerformanceSection();
                DrawAdvancedSection();
            }
        }

        // Draw the about cluster AFTER the scroll child so it always paints on top.
        DrawTopRightAboutCluster(topY);
    }

    private void DrawGeneralSection()
    {
        var enabled = config.PluginEnabled;
        if (UiHelpers.CheckboxWithTextAndHelp("##enablePlugin", Strings.T("checkbox.enable_plugin"),
                Strings.T("tooltip.global_enable"), ref enabled))
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

        DrawLanguageSelector();
    }

    private void DrawInterfaceSection()
    {
        if (!ImGui.CollapsingHeader(Strings.T("label.section_interface")))
            return;

        ImGui.Indent(8);

        var uvAA = config.UvWireframeAntiAlias;
        if (UiHelpers.CheckboxWithTextAndHelp("##uvAA", Strings.T("checkbox.uv_aa"),
                Strings.T("tooltip.uv_aa"), ref uvAA))
        {
            config.UvWireframeAntiAlias = uvAA;
            config.Save();
        }

        var uvCull = config.UvWireframeCulling;
        if (UiHelpers.CheckboxWithTextAndHelp("##uvCull", Strings.T("checkbox.uv_cull"),
                Strings.T("tooltip.uv_cull"), ref uvCull))
        {
            config.UvWireframeCulling = uvCull;
            config.Save();
        }

        var uvDedup = config.UvWireframeDedup;
        if (UiHelpers.CheckboxWithTextAndHelp("##uvDedup", Strings.T("checkbox.uv_dedup"),
                Strings.T("tooltip.uv_dedup"), ref uvDedup))
        {
            config.UvWireframeDedup = uvDedup;
            config.Save();
        }

        ImGui.Spacing();
        DrawDeleteModifierSelector();

        ImGui.Unindent(8);
        ImGui.Spacing();
    }

    private void DrawDeleteModifierSelector()
    {
        UiHelpers.LabelWithHelp(Strings.T("label.delete_modifier"), Strings.T("tooltip.delete_modifier_help"));

        var keys = config.DeleteModifierKeys;
        bool ctrl = (keys & 1) != 0;
        bool shift = (keys & 2) != 0;
        bool alt = (keys & 4) != 0;

        ImGui.SameLine();
        if (ImGui.Checkbox("Ctrl##delMod", ref ctrl)) UpdateDeleteModifier(ctrl, shift, alt);
        ImGui.SameLine();
        if (ImGui.Checkbox("Shift##delMod", ref shift)) UpdateDeleteModifier(ctrl, shift, alt);
        ImGui.SameLine();
        if (ImGui.Checkbox("Alt##delMod", ref alt)) UpdateDeleteModifier(ctrl, shift, alt);
    }

    private void UpdateDeleteModifier(bool ctrl, bool shift, bool alt)
    {
        int v = 0;
        if (ctrl) v |= 1;
        if (shift) v |= 2;
        if (alt) v |= 4;
        config.DeleteModifierKeys = v;
        config.Save();
    }

    private void DrawPerformanceSection()
    {
        if (!ImGui.CollapsingHeader(Strings.T("label.section_performance")))
            return;

        ImGui.Indent(8);

        if (!settingsDraggingSwapInterval)
            settingsPendingSwapInterval = Math.Clamp(
                config.GameSwapIntervalMs, SwapIntervalMin, SwapIntervalMax);

        UiHelpers.LabelWithHelp(Strings.T("label.swap_interval"), Strings.T("tooltip.swap_interval"));
        ImGui.SameLine();
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

        ImGui.SameLine();
        if (ImGui.SmallButton(Strings.T("button.default")))
        {
            settingsPendingSwapInterval = SwapIntervalDefault;
            config.GameSwapIntervalMs = SwapIntervalDefault;
            config.Save();
            settingsDraggingSwapInterval = false;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.restore_default", SwapIntervalDefault));

        ImGui.Unindent(8);
        ImGui.Spacing();
    }

    private void DrawAdvancedSection()
    {
        if (!ImGui.CollapsingHeader(Strings.T("label.section_advanced")))
            return;

        ImGui.Indent(8);

        // -- Debug window --
        if (ImGui.Button(Strings.T("label.open_debug_window")))
        {
            if (DebugWindowRef != null)
                DebugWindowRef.IsOpen = true;
        }
        ImGui.SameLine();
        UiHelpers.DrawInfoIcon();
        UiHelpers.AddHoverText(Strings.T("tooltip.open_debug_help"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // -- HTTP server --
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
        ImGui.TextWrapped(Strings.T("label.http_server_desc"));
        ImGui.PopStyleColor();
        ImGui.Spacing();

        var httpEnabled = config.HttpEnabled;
        if (UiHelpers.CheckboxWithTextAndHelp("##httpEn", Strings.T("checkbox.http_enable"),
                Strings.T("tooltip.http_enable"), ref httpEnabled))
        {
            config.HttpEnabled = httpEnabled;
            config.Save();
        }

        UiHelpers.LabelWithHelp(Strings.T("label.port"), Strings.T("tooltip.port"));
        ImGui.SameLine();
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

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1),
            Strings.T("label.http_restart_notice"));

        ImGui.Unindent(8);
        ImGui.Spacing();
    }

    private void DrawTopRightAboutCluster(float topY)
    {
        var pad = ImGui.GetStyle().FramePadding.X * 2f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        const string ghLabel = "GitHub";
        const string dcLabel = "Discord";
        var ghW = ImGui.CalcTextSize(ghLabel).X + pad;
        var dcW = ImGui.CalcTextSize(dcLabel).X + pad;
        var clusterWidth = ghW + dcW + spacing;
        var rightX = ImGui.GetWindowContentRegionMax().X - clusterWidth;
        if (ImGui.GetScrollMaxY() > 0)
            rightX -= ImGui.GetStyle().ScrollbarSize;
        if (rightX < 0) return;

        ImGui.SetCursorPos(new Vector2(rightX, topY));
        if (ImGui.Button(ghLabel))
            OpenUrl(RepoUrl);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.open_repo_help"));

        ImGui.SameLine();
        if (ImGui.Button(dcLabel))
            OpenUrl(DiscordUrl);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.open_discord_help"));

        // Reset cursor so the rest of the tab draws below the buttons.
        ImGui.SetCursorPos(new Vector2(ImGui.GetStyle().WindowPadding.X, topY));
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Http.DebugServer.AppendLog($"[Settings] OpenUrl failed: {ex.Message}");
        }
    }
}
