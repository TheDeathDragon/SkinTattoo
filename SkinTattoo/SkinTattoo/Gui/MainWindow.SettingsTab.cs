using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
        using var scroll = ImRaii.Child("##SettingsScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) return;

        var enabled = config.PluginEnabled;
        if (ImGui.Checkbox(Strings.T("checkbox.enable_plugin"), ref enabled))
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
            ImGui.SetTooltip(Strings.T("tooltip.global_enable"));

        DrawLanguageSelector();

        ImGui.Spacing();

        var shpkConflict = previewService.SkinShpkModConflict;
        if (!string.IsNullOrEmpty(shpkConflict))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
            ImGui.TextWrapped("Detected another Mod modifying skin.shpk:");
            ImGui.PopStyleColor();
            ImGui.TextWrapped(shpkConflict);
            ImGui.TextWrapped("When emissive is enabled, this plugin's skin.shpk will override that Mod's shader. " +
                "If rendering issues occur, disable the conflicting Mod in Penumbra.");
            ImGui.Spacing();
        }

        ImGui.Separator();
        ImGui.Spacing();

        const float labelW = 110f;

        // ── Refresh ──
        ImGui.TextDisabled(Strings.T("label.refresh_settings"));
        ImGui.Spacing();

        if (!settingsDraggingSwapInterval)
            settingsPendingSwapInterval = Math.Clamp(
                config.GameSwapIntervalMs, SwapIntervalMin, SwapIntervalMax);

        ImGui.AlignTextToFramePadding();
        ImGui.Text(Strings.T("label.swap_interval"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.swap_interval"));
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── UV wireframe ──
        ImGui.TextDisabled(Strings.T("label.uv_wireframe"));
        ImGui.Spacing();

        var uvAA = config.UvWireframeAntiAlias;
        if (ImGui.Checkbox(Strings.T("checkbox.uv_aa"), ref uvAA))
        {
            config.UvWireframeAntiAlias = uvAA;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.uv_aa"));

        var uvCull = config.UvWireframeCulling;
        if (ImGui.Checkbox(Strings.T("checkbox.uv_cull"), ref uvCull))
        {
            config.UvWireframeCulling = uvCull;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.uv_cull"));

        var uvDedup = config.UvWireframeDedup;
        if (ImGui.Checkbox(Strings.T("checkbox.uv_dedup"), ref uvDedup))
        {
            config.UvWireframeDedup = uvDedup;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.uv_dedup"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── HTTP ──
        ImGui.TextDisabled(Strings.T("label.http_server"));
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
        ImGui.TextWrapped(Strings.T("label.http_server_desc"));
        ImGui.PopStyleColor();
        ImGui.Spacing();

        var httpEnabled = config.HttpEnabled;
        if (ImGui.Checkbox(Strings.T("checkbox.http_enable"), ref httpEnabled))
        {
            config.HttpEnabled = httpEnabled;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.http_enable"));

        ImGui.AlignTextToFramePadding();
        ImGui.Text(Strings.T("label.port"));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.port"));
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
            ImGui.SetTooltip(Strings.T("tooltip.port_range"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Debug ──
        ImGui.TextDisabled(Strings.T("label.debug_section"));
        ImGui.Spacing();

        if (ImGui.Button(Strings.T("button.open_debug")))
        {
            if (DebugWindowRef != null)
                DebugWindowRef.IsOpen = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── About / links ──
        ImGui.TextDisabled(Strings.T("label.about"));
        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.Text(Strings.T("label.repo"));
        ImGui.SameLine(labelW);
        if (ImGui.Button($"{RepoUrl}##repoLink"))
            OpenUrl(RepoUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.open_repo"));

        ImGui.AlignTextToFramePadding();
        ImGui.Text(Strings.T("label.discord"));
        ImGui.SameLine(labelW);
        if (ImGui.Button($"{DiscordUrl}##discordLink"))
            OpenUrl(DiscordUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Strings.T("tooltip.open_discord"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1),
            Strings.T("label.http_restart_notice"));
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
