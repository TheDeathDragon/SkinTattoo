using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SkinTatoo.Gui;

public class ConfigWindow : Window
{
    private readonly Configuration config;

    private static readonly int[] ResolutionOptions = [512, 1024, 2048, 4096];
    private static readonly string[] ResolutionNames = ["512", "1024", "2048", "4096"];

    // Slider drag state — SliderInt would otherwise fire every frame during drag, saving
    // config every tick AND taking effect mid-drag (so dragging to 0 immediately spams
    // 60Hz full SwapTexture which chokes the main thread). We buffer the value in
    // pendingSwapInterval and only commit on IsItemDeactivatedAfterEdit (mouse release).
    private int pendingSwapInterval;
    private bool draggingSwapInterval;

    // Floor the slider at 33ms ≈ 30Hz — the compose loop itself caps at ~30Hz so swapping
    // faster than that is pure main-thread waste.
    private const int SwapIntervalMin = 33;
    private const int SwapIntervalMax = 500;
    private const int SwapIntervalDefault = 150;

    public ConfigWindow(Configuration config)
        : base("SkinTatoo 设置###SkinTatooConfig")
    {
        this.config = config;

        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar;
        Size = new System.Numerics.Vector2(360, 320);
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
        ImGui.TextDisabled("游戏贴图刷新");
        ImGui.Separator();

        // Only sync the buffered value from config when the user is NOT mid-drag, so the
        // slider visibly "holds" the value the user is currently pointing at between frames.
        if (!draggingSwapInterval)
            pendingSwapInterval = System.Math.Clamp(config.GameSwapIntervalMs, SwapIntervalMin, SwapIntervalMax);

        ImGui.Text("刷新间隔");
        ImGui.SetNextItemWidth(200);
        ImGui.SliderInt("##SwapIntervalSlider", ref pendingSwapInterval, SwapIntervalMin, SwapIntervalMax, "%d ms");
        draggingSwapInterval = ImGui.IsItemActive();
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.GameSwapIntervalMs = pendingSwapInterval;
            config.Save();
            draggingSwapInterval = false;
        }

        ImGui.SameLine();
        if (ImGui.Button($"恢复默认 ({SwapIntervalDefault})"))
        {
            pendingSwapInterval = SwapIntervalDefault;
            config.GameSwapIntervalMs = SwapIntervalDefault;
            config.Save();
            draggingSwapInterval = false;
        }

        ImGui.TextDisabled("拖动时游戏侧贴图的最小刷新间隔。");
        ImGui.TextDisabled("数值越小越实时、主线程负担越高。");
        ImGui.TextDisabled("松开鼠标后才会应用。3D 编辑器预览不受此限制。");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(new System.Numerics.Vector4(1, 0.8f, 0.3f, 1),
            "修改端口或分辨率后需重启插件才能生效。");
    }
}
