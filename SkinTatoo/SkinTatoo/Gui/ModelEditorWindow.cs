using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using SkinTatoo.Core;
using SkinTatoo.DirectX;
using SkinTatoo.Interop;
using SkinTatoo.Mesh;
using SkinTatoo.Services;

namespace SkinTatoo.Gui;

public class ModelEditorWindow : Window, IDisposable
{
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly DxRenderer renderer;
    private readonly MeshBuffer meshBuffer;
    private readonly OrbitCamera camera;

    private SharpDX.Direct3D11.ShaderResourceView? diffuseSrv;
    private SharpDX.Direct3D11.Texture2D? diffuseTex;
    private int diffuseTexW;
    private int diffuseTexH;
    private long lastCompositeVersion = -1;

    private bool isDraggingCamera;
    private Vector2 lastMousePos;
    private MeshData? uploadedMesh;
    private string? lastMeshPath;
    private int lastGroupIndex = -2;

    private List<(string name, string diskPath)>? cachedMdlList;

    private static SharpDX.Vector3 ToSDX(Vector3 v) => new(v.X, v.Y, v.Z);
    private static Vector3 FromSDX(SharpDX.Vector3 v) => new(v.X, v.Y, v.Z);

    public ModelEditorWindow(
        DecalProject project,
        PreviewService previewService,
        PenumbraBridge penumbra,
        nint deviceHandle)
        : base("3D 编辑器###SkinTatooModelEditor",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.project = project;
        this.previewService = previewService;
        this.penumbra = penumbra;

        renderer = new DxRenderer(deviceHandle);
        meshBuffer = new MeshBuffer();
        camera = new OrbitCamera();
        camera.Update();

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 350),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void MarkTexturesDirty() { } // kept for API compat; version tracking handles sync

    public void OnMeshChanged()
    {
        uploadedMesh = null;
        lastCompositeVersion = -1;
    }

    public override void Draw()
    {
        TryUploadMesh();
        TryUpdateTexture();

        DrawToolbar();
        ImGui.Separator();
        DrawViewport();
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("重置相机"))
            camera.Reset();

        ImGui.SameLine();
        if (ImGui.Button("添加模型"))
        {
            RefreshMdlList();
            ImGui.OpenPopup("AddMdlPopup");
        }

        if (ImGui.BeginPopup("AddMdlPopup"))
        {
            var group = project.SelectedGroup;
            if (cachedMdlList == null || cachedMdlList.Count == 0)
            {
                ImGui.TextDisabled("未检测到角色模型");
            }
            else
            {
                ImGui.Text("选择要添加的模型:");
                ImGui.Separator();
                var existing = group?.AllMeshPaths ?? [];
                foreach (var (name, diskPath) in cachedMdlList)
                {
                    var alreadyAdded = existing.Contains(diskPath);
                    if (alreadyAdded)
                    {
                        ImGui.TextDisabled($"\u2713 {name}");
                    }
                    else if (ImGui.Selectable(name))
                    {
                        if (group != null)
                        {
                            if (string.IsNullOrEmpty(group.MeshDiskPath))
                                group.MeshDiskPath = diskPath;
                            else if (!group.MeshDiskPaths.Contains(diskPath))
                                group.MeshDiskPaths.Add(diskPath);
                            OnMeshChanged();
                        }
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(diskPath);
                }
            }
            ImGui.EndPopup();
        }

        var group2 = project.SelectedGroup;
        if (group2 != null && group2.AllMeshPaths.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("管理模型"))
                ImGui.OpenPopup("MeshListPopup");

            if (ImGui.BeginPopup("MeshListPopup"))
            {
                ImGui.Text("模型列表:");
                ImGui.Separator();
                var changed = false;
                string? removeTarget = null;
                var allPaths = group2.AllMeshPaths;
                for (var mi = 0; mi < allPaths.Count; mi++)
                {
                    var p = allPaths[mi];
                    ImGui.PushID(mi);

                    var visible = !group2.HiddenMeshPaths.Contains(p);
                    if (ImGui.Checkbox("##vis", ref visible))
                    {
                        if (visible)
                            group2.HiddenMeshPaths.Remove(p);
                        else
                            group2.HiddenMeshPaths.Add(p);
                        changed = true;
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("显示/隐藏");

                    ImGui.SameLine();
                    ImGui.Text(Path.GetFileName(p));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(p);

                    ImGui.SameLine();
                    if (ImGui.SmallButton("移除"))
                        removeTarget = p;

                    ImGui.PopID();
                }
                if (removeTarget != null)
                {
                    if (removeTarget == group2.MeshDiskPath)
                    {
                        if (group2.MeshDiskPaths.Count > 0)
                        {
                            group2.MeshDiskPath = group2.MeshDiskPaths[0];
                            group2.MeshDiskPaths.RemoveAt(0);
                        }
                        else
                            group2.MeshDiskPath = null;
                    }
                    else
                        group2.MeshDiskPaths.Remove(removeTarget);
                    group2.HiddenMeshPaths.Remove(removeTarget);
                    changed = true;
                }
                if (changed)
                    OnMeshChanged();
                ImGui.EndPopup();
            }
        }

        // Model info on the right side of buttons
        ImGui.SameLine();
        var mesh = previewService.CurrentMesh;
        var pathCount = group2?.AllMeshPaths.Count ?? 0;
        if (mesh != null)
        {
            ImGui.TextDisabled($"模型: {pathCount}  三角面: {mesh.TriangleCount}");
            var layer = project.SelectedLayer;
            if (layer != null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"| {layer.Name}");
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(1, 0.5f, 0.3f, 1), "未加载网格");
        }
    }

    private void DrawViewport()
    {
        var size = ImGui.GetContentRegionAvail();
        if (size.X < 1 || size.Y < 1) return;

        var iw = (int)size.X;
        var ih = (int)size.Y;
        renderer.Resize(iw, ih);
        camera.SetAspect(iw, ih);

        RenderScene();

        var outputPtr = renderer.OutputPointer;
        if (outputPtr == nint.Zero) return;

        var cursorBefore = ImGui.GetCursorScreenPos();
        ImGui.ImageButton(new ImTextureID(outputPtr), size, new Vector2(0, 0), new Vector2(1, 1), 0);

        var isHovered = ImGui.IsItemHovered();
        if (isHovered)
            HandleInteraction(cursorBefore, size);
        else
            isDraggingCamera = false;

        DrawViewportOverlay(cursorBefore, size);
    }

    private void DrawViewportOverlay(Vector2 viewportPos, Vector2 viewportSize)
    {
        var drawList = ImGui.GetWindowDrawList();
        var bgColor = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f));
        var textColor = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.9f));
        var dimColor = ImGui.GetColorU32(new Vector4(0.7f, 0.7f, 0.7f, 0.7f));

        // Bottom-left: current layer UV params (stacked lines)
        var layer = project.SelectedLayer;
        if (layer != null)
        {
            var line1 = $"UV: {layer.UvCenter.X:F3}, {layer.UvCenter.Y:F3}";
            var line2 = $"大小: {layer.UvScale.X:F3}  旋转: {layer.RotationDeg:F1}\u00b0";
            var pos2 = viewportPos + new Vector2(4, viewportSize.Y - 20);
            var pos1 = pos2 - new Vector2(0, 18);
            var size1 = ImGui.CalcTextSize(line1);
            var size2 = ImGui.CalcTextSize(line2);
            drawList.AddRectFilled(pos1 - new Vector2(2, 1), pos1 + new Vector2(size1.X + 4, 17), bgColor);
            drawList.AddText(pos1, textColor, line1);
            drawList.AddRectFilled(pos2 - new Vector2(2, 1), pos2 + new Vector2(size2.X + 4, 17), bgColor);
            drawList.AddText(pos2, dimColor, line2);
        }

        // Bottom-right: operation hints (stacked lines)
        {
            var hint1 = "左键:放置贴花  右键:旋转相机  中键:平移相机";
            var hint2 = "滚轮:缩放贴花  Ctrl+滚轮:缩放相机";
            var hint1Size = ImGui.CalcTextSize(hint1);
            var hint2Size = ImGui.CalcTextSize(hint2);
            var pos2 = viewportPos + new Vector2(viewportSize.X - hint2Size.X - 6, viewportSize.Y - 20);
            var pos1 = viewportPos + new Vector2(viewportSize.X - hint1Size.X - 6, viewportSize.Y - 38);
            drawList.AddRectFilled(pos1 - new Vector2(2, 1), pos1 + new Vector2(hint1Size.X + 4, 17), bgColor);
            drawList.AddText(pos1, dimColor, hint1);
            drawList.AddRectFilled(pos2 - new Vector2(2, 1), pos2 + new Vector2(hint2Size.X + 4, 17), bgColor);
            drawList.AddText(pos2, dimColor, hint2);
        }

        // Top-left: group name
        var group = project.SelectedGroup;
        if (group != null)
        {
            var groupText = group.Name;
            var groupPos = viewportPos + new Vector2(4, 4);
            var groupSize = ImGui.CalcTextSize(groupText);
            drawList.AddRectFilled(groupPos - new Vector2(2, 1), groupPos + new Vector2(groupSize.X + 4, 17), bgColor);
            drawList.AddText(groupPos, textColor, groupText);
        }
    }

    private void RenderScene()
    {
        if (!meshBuffer.IsLoaded) return;

        renderer.BeginFrame(out var oldR, out var oldRtv, out var oldDsv, out var oldDss);

        var world = SharpDX.Matrix.Scaling(-1, 1, 1);
        var viewProj = camera.ViewMatrix * camera.ProjMatrix;
        viewProj.Transpose();
        world.Transpose();

        var vsData = new VSConstants
        {
            WorldMatrix = world,
            ViewProjectionMatrix = viewProj,
        };
        renderer.UpdateVSConstants(ref vsData);

        var camPos = camera.CameraPosition;
        var psData = new PSConstants
        {
            LightDir = new SharpDX.Vector3(0.3f, -1f, 0.5f),
            CameraPos = camPos,
            HasTexture = diffuseSrv != null ? 1 : 0,
        };
        renderer.UpdatePSConstants(ref psData);

        renderer.BindDiffuseTexture(diffuseSrv);
        meshBuffer.Bind(renderer.Context);
        meshBuffer.DrawIndexed(renderer.Context);

        renderer.EndFrame(oldR, oldRtv, oldDsv, oldDss);
    }

    private void HandleInteraction(Vector2 viewportPos, Vector2 viewportSize)
    {
        var mousePos = ImGui.GetMousePos();
        var localMouse = mousePos - viewportPos;
        var io = ImGui.GetIO();

        // Right mouse: orbit camera
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Right))
        {
            if (!isDraggingCamera)
            {
                isDraggingCamera = true;
                lastMousePos = mousePos;
            }
            var delta = mousePos - lastMousePos;
            camera.Rotate(delta.X * 0.01f, -delta.Y * 0.01f);
            lastMousePos = mousePos;
        }
        else
            isDraggingCamera = false;

        // Middle mouse: pan
        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
        {
            var delta = io.MouseDelta;
            camera.Pan(delta.X, delta.Y);
        }

        // Scroll: Ctrl = camera zoom, otherwise = decal scale
        if (io.MouseWheel != 0)
        {
            if (io.KeyCtrl)
            {
                camera.Zoom(io.MouseWheel);
            }
            else
            {
                var layer = project.SelectedLayer;
                if (layer != null)
                {
                    float factor = 1f + io.MouseWheel * 0.05f;
                    layer.UvScale *= factor;
                    layer.UvScale = new Vector2(
                        Math.Clamp(layer.UvScale.X, 0.01f, 2f),
                        Math.Clamp(layer.UvScale.Y, 0.01f, 2f));
                    previewService.MarkDirty();
                }
            }
        }

        // Left click/drag: place/move decal
        if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && !isDraggingCamera)
        {
            var mesh = previewService.CurrentMesh;
            if (mesh != null)
            {
                var (rayOrig, rayDirSharp) = camera.ScreenToRay(
                    localMouse.X, localMouse.Y, viewportSize.X, viewportSize.Y);

                var orig = FromSDX(rayOrig);
                var dir = FromSDX(rayDirSharp);
                orig.X = -orig.X;
                dir.X = -dir.X;

                var hit = RayPicker.Pick(mesh, orig, dir);
                if (hit.HasValue)
                {
                    var layer = project.SelectedLayer;
                    if (layer != null)
                    {
                        layer.UvCenter = hit.Value.UV;
                        previewService.MarkDirty();
                    }
                }
            }
        }
    }

    private void TryUploadMesh()
    {
        var groupIdx = project.SelectedGroupIndex;
        var group = project.SelectedGroup;

        // Detect group switch (including deletion)
        if (groupIdx != lastGroupIndex)
        {
            lastGroupIndex = groupIdx;
            lastMeshPath = null;
            lastCompositeVersion = -1;

            // Clear display if no group selected
            if (group == null)
            {
                meshBuffer.Dispose();
                uploadedMesh = null;
                diffuseSrv?.Dispose();
                diffuseSrv = null;
                diffuseTex?.Dispose();
                diffuseTex = null;
                diffuseTexW = 0;
                diffuseTexH = 0;
                return;
            }
        }

        // Load mesh when paths change
        var paths = group?.VisibleMeshPaths;
        var pathKey = paths != null && paths.Count > 0 ? string.Join("|", paths) : null;
        if (pathKey != lastMeshPath)
        {
            lastMeshPath = pathKey;
            if (pathKey != null)
            {
                previewService.LoadMeshes(paths!);
                uploadedMesh = null;
            }
            else
            {
                meshBuffer.Dispose();
                uploadedMesh = null;
            }
        }

        var mesh = previewService.CurrentMesh;
        if (mesh == null || mesh == uploadedMesh) return;

        meshBuffer.Upload(renderer.Device, mesh);
        uploadedMesh = mesh;
        AutoFitCamera(mesh);
    }

    private void AutoFitCamera(MeshData mesh)
    {
        if (mesh.Vertices.Length == 0) return;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in mesh.Vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }
        var center = (min + max) * 0.5f;
        var extent = (max - min).Length();

        camera.Target = ToSDX(center);
        camera.Distance = extent * 0.8f;
        camera.PanOffset = SharpDX.Vector3.Zero;
        camera.Yaw = 0;
        camera.Pitch = 0;
        camera.Update();
    }

    private void TryUpdateTexture()
    {
        var ver = previewService.CompositeVersion;
        if (ver == lastCompositeVersion) return;

        var group = project.SelectedGroup;
        var texData = previewService.GetCompositeForGroup(group?.DiffuseGamePath);
        if (texData == null) return;

        lastCompositeVersion = ver;
        var (data, w, h, dirty) = texData.Value;

        // Recreate only when size changes. Otherwise keep the persistent Texture2D and
        // upload only the dirty sub-region — this kills the ~15ms/frame main-thread
        // allocator + 64MB upload spike that used to hit on every drag frame.
        if (diffuseTex == null || diffuseTexW != w || diffuseTexH != h)
        {
            diffuseSrv?.Dispose();
            diffuseTex?.Dispose();
            (diffuseTex, diffuseSrv) = renderer.CreateUpdatableRgbaTexture(w, h);
            diffuseTexW = w;
            diffuseTexH = h;
            // Seed the whole texture on the first upload after (re)creation.
            renderer.UpdateRgbaRegion(diffuseTex, data, w, 0, 0, w, h);
            return;
        }

        if (dirty.IsEmpty) return;
        renderer.UpdateRgbaRegion(diffuseTex, data, w, dirty.X, dirty.Y, dirty.W, dirty.H);
    }

    private void RefreshMdlList()
    {
        cachedMdlList = [];
        var trees = penumbra.GetPlayerTrees();
        if (trees == null) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, tree) in trees)
        {
            foreach (var topNode in tree.Nodes)
                CollectMdlNodes(topNode, tree.Name, seen);
        }
    }

    private void CollectMdlNodes(ResourceNodeDto node, string treeName, HashSet<string> seen)
    {
        if (node.Type == ResourceType.Mdl)
        {
            var diskPath = Path.IsPathRooted(node.ActualPath) ? node.ActualPath : (node.GamePath ?? node.ActualPath);
            if (!string.IsNullOrEmpty(diskPath) && seen.Add(diskPath))
            {
                var fileName = Path.GetFileName(node.GamePath ?? node.ActualPath);
                var label = $"[{treeName}] {fileName}";
                cachedMdlList!.Add((label, diskPath));
            }
        }
        foreach (var child in node.Children)
            CollectMdlNodes(child, treeName, seen);
    }

    public void Dispose()
    {
        diffuseSrv?.Dispose();
        diffuseTex?.Dispose();
        meshBuffer.Dispose();
        renderer.Dispose();
    }
}
