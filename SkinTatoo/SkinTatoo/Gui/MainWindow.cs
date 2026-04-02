using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SkinTatoo.Core;
using SkinTatoo.Interop;
using SkinTatoo.Services;

namespace SkinTatoo.Gui;

public class MainWindow : Window, IDisposable
{
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;

    // 右侧面板图片路径输入缓冲
    private string imagePathBuf = string.Empty;
    private int lastEditedLayerIndex = -1;

    // 混合模式选项列表
    private static readonly string[] BlendModeNames = ["正常", "正片叠底", "叠加", "柔光"];
    private static readonly BlendMode[] BlendModeValues = [BlendMode.Normal, BlendMode.Multiply, BlendMode.Overlay, BlendMode.SoftLight];

    // 皮肤目标选项
    private static readonly string[] TargetNames = ["身体", "面部"];
    private static readonly SkinTarget[] TargetValues = [SkinTarget.Body, SkinTarget.Face];

    public MainWindow(
        DecalProject project,
        PreviewService previewService,
        PenumbraBridge penumbra,
        Configuration config)
        : base("SkinTatoo 纹身编辑器###SkinTatooMain",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.project = project;
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.config = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700, 450),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var totalWidth = ImGui.GetContentRegionAvail().X;
        var leftWidth = totalWidth * 0.35f;
        var rightWidth = totalWidth - leftWidth - ImGui.GetStyle().ItemSpacing.X;

        DrawLeftPanel(leftWidth);
        ImGui.SameLine();
        DrawRightPanel(rightWidth);
    }

    // 左侧面板：图层列表 + 目标选择
    private void DrawLeftPanel(float width)
    {
        using var child = ImRaii.Child("##LeftPanel", new Vector2(width, 0), true);
        if (!child.Success) return;

        // 皮肤目标选择
        ImGui.TextDisabled("投影目标");
        ImGui.SameLine();
        var targetIdx = Array.IndexOf(TargetValues, project.Target);
        if (targetIdx < 0) targetIdx = 0;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##Target", ref targetIdx, TargetNames, TargetNames.Length))
        {
            project.Target = TargetValues[targetIdx];
            config.LastTarget = project.Target;
            config.Save();
        }

        ImGui.Separator();

        // GPU / Penumbra 状态指示
        var gpuColor = previewService.IsReady ? new Vector4(0, 1, 0.3f, 1) : new Vector4(1, 0.3f, 0.3f, 1);
        var penColor = penumbra.IsAvailable ? new Vector4(0, 1, 0.3f, 1) : new Vector4(1, 0.8f, 0, 1);
        ImGui.TextColored(gpuColor, previewService.IsReady ? "● GPU 就绪" : "● GPU 未就绪");
        ImGui.SameLine();
        ImGui.TextColored(penColor, penumbra.IsAvailable ? "● Penumbra 已连接" : "● Penumbra 未连接");

        ImGui.Separator();

        // 图层列表标题 + 增减按钮
        ImGui.Text($"图层 ({project.Layers.Count})");
        ImGui.SameLine();
        if (ImGui.SmallButton("+##AddLayer"))
            project.AddLayer("新贴花");
        ImGui.SameLine();
        using (ImRaii.Disabled(project.SelectedLayerIndex < 0 || project.Layers.Count == 0))
        {
            if (ImGui.SmallButton("-##RemoveLayer"))
                project.RemoveLayer(project.SelectedLayerIndex);
        }

        ImGui.Separator();

        // 图层列表滚动区
        var listHeight = ImGui.GetContentRegionAvail().Y;
        using var listChild = ImRaii.Child("##LayerList", new Vector2(-1, listHeight), false);
        if (!listChild.Success) return;

        for (var i = 0; i < project.Layers.Count; i++)
        {
            var layer = project.Layers[i];

            // 可见性复选框
            var vis = layer.IsVisible;
            if (ImGui.Checkbox($"##vis{i}", ref vis))
                layer.IsVisible = vis;
            ImGui.SameLine();

            // 选中状态高亮
            var isSelected = project.SelectedLayerIndex == i;
            if (isSelected)
                ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.26f, 0.59f, 0.98f, 0.5f));

            if (ImGui.Selectable($"{layer.Name}##layer{i}", isSelected, ImGuiSelectableFlags.None, new Vector2(-1, 0)))
            {
                project.SelectedLayerIndex = i;
                SyncImagePathBuf(i);
            }

            if (isSelected)
                ImGui.PopStyleColor();
        }
    }

    // 右侧面板：选中图层的参数编辑
    private void DrawRightPanel(float width)
    {
        using var child = ImRaii.Child("##RightPanel", new Vector2(width, 0), true);
        if (!child.Success) return;

        var layer = project.SelectedLayer;
        if (layer == null)
        {
            ImGui.TextDisabled("请在左侧选择一个图层");
            return;
        }

        var idx = project.SelectedLayerIndex;

        // 同步 imagePathBuf（防止选中不同层时缓冲未刷新）
        if (lastEditedLayerIndex != idx)
            SyncImagePathBuf(idx);

        ImGui.TextDisabled("图层参数");
        ImGui.Separator();

        // 名称
        var name = layer.Name;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##LayerName", ref name, 128))
            layer.Name = name;
        ImGui.SameLine(0, 4);
        ImGui.TextDisabled("名称");

        ImGui.Spacing();

        // 图片路径
        ImGui.Text("图片路径");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60);
        if (ImGui.InputText("##ImagePath", ref imagePathBuf, 512))
        {
            layer.ImagePath = imagePathBuf;
            lastEditedLayerIndex = idx;
        }

        ImGui.SameLine();
        if (ImGui.Button("应用##ApplyPath", new Vector2(-1, 0)))
        {
            layer.ImagePath = imagePathBuf;
        }

        ImGui.Spacing();
        ImGui.Separator();

        // 位置 / 旋转 / 缩放
        var pos = layer.Position;
        if (ImGui.DragFloat3("位置", ref pos, 0.01f))
            layer.Position = pos;

        var rot = layer.Rotation;
        if (ImGui.DragFloat3("旋转 (°)", ref rot, 0.5f))
            layer.Rotation = rot;

        var scale = layer.Scale;
        if (ImGui.DragFloat2("缩放", ref scale, 0.005f, 0.001f, 10f))
            layer.Scale = scale;

        var depth = layer.Depth;
        if (ImGui.DragFloat("深度", ref depth, 0.005f, 0.001f, 5f))
            layer.Depth = depth;

        ImGui.Spacing();
        ImGui.Separator();

        // 透明度
        var opacity = layer.Opacity;
        if (ImGui.SliderFloat("不透明度", ref opacity, 0f, 1f))
            layer.Opacity = opacity;

        // 背面剔除阈值
        var backface = layer.BackfaceCullingThreshold;
        if (ImGui.SliderFloat("背面剔除", ref backface, -1f, 1f))
            layer.BackfaceCullingThreshold = backface;

        // 掠射角渐隐
        var grazing = layer.GrazingAngleFade;
        if (ImGui.SliderFloat("掠射角渐隐", ref grazing, 0f, 1f))
            layer.GrazingAngleFade = grazing;

        ImGui.Spacing();
        ImGui.Separator();

        // 混合模式
        var blendIdx = Array.IndexOf(BlendModeValues, layer.BlendMode);
        if (blendIdx < 0) blendIdx = 0;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("混合模式", ref blendIdx, BlendModeNames, BlendModeNames.Length))
            layer.BlendMode = BlendModeValues[blendIdx];

        ImGui.Spacing();

        // 影响通道复选框
        var affDiff = layer.AffectsDiffuse;
        if (ImGui.Checkbox("影响漫反射", ref affDiff))
            layer.AffectsDiffuse = affDiff;
        ImGui.SameLine();
        var affNorm = layer.AffectsNormal;
        if (ImGui.Checkbox("影响法线", ref affNorm))
            layer.AffectsNormal = affNorm;

        ImGui.Spacing();
        ImGui.Separator();

        // 预览按钮
        var gpuReady = previewService.IsReady;
        using (ImRaii.Disabled(!gpuReady))
        {
            if (ImGui.Button("更新预览", new Vector2(-1, 28)))
            {
                // 根据目标确定游戏贴图路径（使用种族码 0101 作为默认）
                var gameTexPath = config.LastTarget == SkinTarget.Body
                    ? "chara/human/c0101/obj/body/b0001/texture/--c0101b0001_d.tex"
                    : "chara/human/c0101/obj/face/f0001/texture/--c0101f0001_d.tex";
                _ = previewService.UpdatePreview(project, gameTexPath);
            }
        }

        if (!gpuReady)
            ImGui.TextColored(new Vector4(1, 0.5f, 0.5f, 1), "GPU 管线未初始化，无法预览");
    }

    private void SyncImagePathBuf(int idx)
    {
        lastEditedLayerIndex = idx;
        imagePathBuf = (idx >= 0 && idx < project.Layers.Count)
            ? (project.Layers[idx].ImagePath ?? string.Empty)
            : string.Empty;
    }

    public void Dispose() { }
}
