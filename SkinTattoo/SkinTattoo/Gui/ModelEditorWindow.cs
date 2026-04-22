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
using SkinTattoo.Core;
using SkinTattoo.DirectX;
using SkinTattoo.Interop;
using SkinTattoo.Mesh;
using SkinTattoo.Services;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public class ModelEditorWindow : Window, IDisposable
{
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly SkinMeshResolver skinMeshResolver;
    private readonly DxRenderer renderer;
    private readonly MeshBuffer meshBuffer;
    private readonly OrbitCamera camera;

    private SharpDX.Direct3D11.ShaderResourceView? diffuseSrv;
    private SharpDX.Direct3D11.Texture2D? diffuseTex;
    private int diffuseTexW;
    private int diffuseTexH;
    private long lastCompositeVersion = -1;
    private string? lastDiffuseGamePath;
    private TargetGroup? lastTextureGroup;
    private bool showingBaseFallback;

    private bool isDraggingCamera;
    private Vector2 lastMousePos;
    private MeshData? uploadedMesh;
    private List<(string name, string diskPath)>? cachedMdlList;

    private static SharpDX.Vector3 ToSDX(Vector3 v) => new(v.X, v.Y, v.Z);
    private static Vector3 FromSDX(SharpDX.Vector3 v) => new(v.X, v.Y, v.Z);

    public ModelEditorWindow(DecalProject project, PreviewService previewService, PenumbraBridge penumbra, SkinMeshResolver skinMeshResolver, nint deviceHandle)
        : base(Strings.T("window.editor3d.title") + "###SkinTattooModelEditor", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.project = project;
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.skinMeshResolver = skinMeshResolver;

        renderer = new DxRenderer(deviceHandle);
        meshBuffer = new MeshBuffer();
        camera = new OrbitCamera();
        camera.Update();

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 350),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        previewService.MeshChanged += OnPreviewMeshChanged;
    }

    // Reset only GPU upload token so the next TryUploadMesh re-uploads into
    // meshBuffer. Path/group tracking now lives in MainWindow.TickMeshState.
    private void OnPreviewMeshChanged()
    {
        uploadedMesh = null;
        lastCompositeVersion = -1;
    }

    public void MarkTexturesDirty() { } // kept for API compat; version tracking handles sync

    public override void Draw()
    {
        // Mesh state tracking (group ref, pathKey diff, live tree poll) is owned
        // by MainWindow.TickMeshState so the UV canvas stays in sync regardless
        // of whether this window is open. Here we just mirror currentMesh into
        // our meshBuffer when it changes.
        TryUploadMesh();
        TryUpdateTexture();

        DrawToolbar();
        ImGui.Separator();
        DrawViewport();
    }

    private void DrawToolbar()
    {
        if (ImGui.Button(Strings.T("button.reset_camera")))
            camera.Reset();

        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.add_model")))
        {
            RefreshMdlList();
            ImGui.OpenPopup("AddMdlPopup");
        }

        ImGui.SameLine();
        var reGroup = project.SelectedGroup;
        var canReResolve = reGroup != null && !string.IsNullOrEmpty(reGroup.MtrlGamePath);
        using (ImRaii.Disabled(!canReResolve))
        {
            if (ImGui.Button(Strings.T("button.re_resolve")))
                previewService.ReResolveAndReloadMesh(reGroup!, skinMeshResolver);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Strings.T("tooltip.re_resolve_tip"));

        if (ImGui.BeginPopup("AddMdlPopup"))
        {
            var group = project.SelectedGroup;
            if (cachedMdlList == null || cachedMdlList.Count == 0)
            {
                ImGui.TextDisabled(Strings.T("hint.no_model_detected"));
            }
            else
            {
                ImGui.Text(Strings.T("hint.select_model"));
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
                            previewService.LoadMeshForGroup(group);
                            previewService.NotifyMeshChanged();
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
            if (ImGui.Button(Strings.T("button.manage_models")))
                ImGui.OpenPopup("MeshListPopup");

            if (ImGui.BeginPopup("MeshListPopup"))
            {
                ImGui.Text(Strings.T("hint.model_list"));
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
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Strings.T("tooltip.show_hide_model"));

                    ImGui.SameLine();
                    ImGui.Text(Path.GetFileName(p));
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(p);

                    ImGui.SameLine();
                    if (ImGui.SmallButton(Strings.T("button.remove")))
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
                {
                    previewService.LoadMeshForGroup(group2);
                    previewService.NotifyMeshChanged();
                }
                ImGui.EndPopup();
            }
        }

        // Model info on the right side of buttons
        ImGui.SameLine();
        var mesh = previewService.CurrentMesh;
        var pathCount = group2?.AllMeshPaths.Count ?? 0;
        if (mesh != null)
        {
            ImGui.TextDisabled(Strings.T("label.model_info", pathCount, mesh.TriangleCount));
            var layer = project.SelectedLayer;
            if (layer != null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"| {layer.Name}");
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(1, 0.5f, 0.3f, 1), Strings.T("error.no_mesh_loaded"));
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
            var line2 = Strings.T("label.size_rotation_fmt", $"{layer.UvScale.X:F3}", $"{layer.RotationDeg:F1}\u00b0");
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
            var hint1 = Strings.T("hint.viewport_ops");
            var hint2 = Strings.T("hint.viewport_scroll");
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
        if (!meshBuffer.IsLoaded)
        {
            // Empty BeginFrame/EndFrame clears the render target to the background
            // color so the viewport doesn't keep showing a stale last-rendered mesh
            // after the group/layer that owned it is deleted.
            renderer.BeginFrame(out var rR, out var rRtv, out var rDsv, out var rDss);
            renderer.EndFrame(rR, rRtv, rDsv, rDss);
            return;
        }

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
                        (layer.UvScale.X < 0f ? -1f : 1f) * Math.Clamp(MathF.Abs(layer.UvScale.X), 0.01f, 10f),
                        (layer.UvScale.Y < 0f ? -1f : 1f) * Math.Clamp(MathF.Abs(layer.UvScale.Y), 0.01f, 10f));
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
                        // Convert raw mesh UV to texture space [0,1].
                        // Body models have UV X in [1,2] (tile 1), frac maps to [0,1].
                        var rawUv = hit.Value.UV;
                        layer.UvCenter = new Vector2(
                            rawUv.X - MathF.Floor(rawUv.X),
                            rawUv.Y - MathF.Floor(rawUv.Y));
                        previewService.MarkDirty();
                    }
                }
            }
        }
    }

    private void TryUploadMesh()
    {
        var group = project.SelectedGroup;
        var mesh = previewService.CurrentMesh;

        if (group == null || mesh == null)
        {
            if (uploadedMesh != null)
            {
                meshBuffer.Dispose();
                uploadedMesh = null;
            }
            if (group == null)
            {
                diffuseSrv?.Dispose();
                diffuseSrv = null;
                diffuseTex?.Dispose();
                diffuseTex = null;
                diffuseTexW = 0;
                diffuseTexH = 0;
                lastCompositeVersion = -1;
            }
            return;
        }

        if (ReferenceEquals(mesh, uploadedMesh)) return;

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
        var group = project.SelectedGroup;
        var diffPath = group?.DiffuseGamePath;

        // Detect group switch by object reference OR by diffuse path change.
        // Reference comparison covers: switching between groups that share the
        // same DiffuseGamePath, and delete-then-add at the same index (new
        // object instance). Path comparison covers: same group whose resolver
        // re-resolved to a different material.
        var needReset = !ReferenceEquals(group, lastTextureGroup)
            || !string.Equals(diffPath, lastDiffuseGamePath, StringComparison.OrdinalIgnoreCase);

        if (needReset)
        {
            lastTextureGroup = group;
            lastDiffuseGamePath = diffPath;
            lastCompositeVersion = -1;
            showingBaseFallback = false;
            diffuseSrv?.Dispose();
            diffuseSrv = null;
            diffuseTex?.Dispose();
            diffuseTex = null;
            diffuseTexW = 0;
            diffuseTexH = 0;
        }

        var ver = previewService.CompositeVersion;
        if (ver == lastCompositeVersion && diffuseTex != null) return;

        // Try composite first; fall back to base diffuse so the 3D editor
        // shows the skin texture immediately instead of a blank model.
        var texData = previewService.GetCompositeForGroup(diffPath);
        if (texData != null)
        {
            lastCompositeVersion = ver;
            showingBaseFallback = false;
            var (data, w, h, dirty) = texData.Value;

            if (diffuseTex == null || diffuseTexW != w || diffuseTexH != h)
            {
                diffuseSrv?.Dispose();
                diffuseTex?.Dispose();
                (diffuseTex, diffuseSrv) = renderer.CreateUpdatableRgbaTexture(w, h);
                diffuseTexW = w;
                diffuseTexH = h;
                renderer.UpdateRgbaRegion(diffuseTex, data, w, 0, 0, w, h);
                return;
            }

            if (dirty.IsEmpty) return;
            renderer.UpdateRgbaRegion(diffuseTex, data, w, dirty.X, dirty.Y, dirty.W, dirty.H);
            return;
        }

        // No composite available  -- show base diffuse texture as fallback.
        // Skip re-upload if already showing the base (avoids per-frame churn
        // when CompositeVersion bumps for other groups).
        lastCompositeVersion = ver;
        if (diffuseTex != null && showingBaseFallback) return;

        var baseTex = previewService.TryGetBaseTexture(group);
        if (baseTex == null) return;
        var (bd, bw, bh) = baseTex.Value;
        diffuseSrv?.Dispose();
        diffuseTex?.Dispose();
        (diffuseTex, diffuseSrv) = renderer.CreateUpdatableRgbaTexture(bw, bh);
        diffuseTexW = bw;
        diffuseTexH = bh;
        renderer.UpdateRgbaRegion(diffuseTex, bd, bw, 0, 0, bw, bh);
        showingBaseFallback = true;
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
        previewService.MeshChanged -= OnPreviewMeshChanged;
        diffuseSrv?.Dispose();
        diffuseTex?.Dispose();
        meshBuffer.Dispose();
        renderer.Dispose();
    }
}
