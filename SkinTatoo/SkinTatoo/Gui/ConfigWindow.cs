using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SkinTatoo.Gui;

public class ConfigWindow : Window
{
    private readonly Configuration config;

    private static readonly int[] ResolutionOptions = [512, 1024, 2048, 4096];
    private static readonly string[] ResolutionNames = ["512", "1024", "2048", "4096"];

    public ConfigWindow(Configuration config)
        : base("SkinTatoo 设置###SkinTatooConfig")
    {
        this.config = config;

        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar;
        Size = new System.Numerics.Vector2(320, 180);
    }

    public override void Draw()
    {
        ImGui.TextDisabled("HTTP 调试服务器");
        ImGui.Separator();

        var port = config.HttpPort;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("端口号", ref port, 1, 100))
        {
            if (port is >= 1024 and <= 65535)
            {
                config.HttpPort = port;
                config.Save();
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("贴图分辨率");
        ImGui.Separator();

        var resIdx = System.Array.IndexOf(ResolutionOptions, config.TextureResolution);
        if (resIdx < 0) resIdx = 1;
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("分辨率", ref resIdx, ResolutionNames, ResolutionNames.Length))
        {
            config.TextureResolution = ResolutionOptions[resIdx];
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new System.Numerics.Vector4(1, 0.8f, 0.3f, 1),
            "修改端口或分辨率后需重启插件才能生效。");
    }
}
