using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility.Raii;
using StbImageWriteSharp;
using SkinTattoo.Core;
using SkinTattoo.Http;
using SkinTattoo.Mesh;

namespace SkinTattoo.Gui;

public partial class MainWindow
{
    // File watch state: track mtime per path to detect external changes
    private readonly Dictionary<string, DateTime> watchedFileMtimes = new();
    private DateTime lastFileCheckUtc = DateTime.MinValue;
    private const double FileCheckIntervalSec = 1.0;

    /// <summary>Called every frame from DrawActionsSection. Polls layer image files for changes.</summary>
    private void PollFileChanges()
    {
        var now = DateTime.UtcNow;
        if ((now - lastFileCheckUtc).TotalSeconds < FileCheckIntervalSec) return;
        lastFileCheckUtc = now;

        foreach (var group in project.Groups)
        {
            foreach (var layer in group.Layers)
            {
                if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;
                if (!File.Exists(layer.ImagePath)) continue;

                try
                {
                    var mtime = File.GetLastWriteTimeUtc(layer.ImagePath);
                    if (watchedFileMtimes.TryGetValue(layer.ImagePath, out var prev))
                    {
                        if (mtime != prev)
                        {
                            watchedFileMtimes[layer.ImagePath] = mtime;
                            MarkPreviewDirty();
                        }
                    }
                    else
                    {
                        watchedFileMtimes[layer.ImagePath] = mtime;
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>Export base texture as PNG for external editing.</summary>
    private void ExportBaseTexture(TargetGroup group)
    {
        var tex = previewService.GetBaseTextureData(group);
        if (tex == null) return;
        var (data, w, h) = tex.Value;

        fileDialog.SaveFileDialog(
            "导出底图",
            "PNG{.png}",
            $"base_{w}x{h}.png",
            ".png",
            (ok, path) =>
            {
                if (!ok || string.IsNullOrEmpty(path)) return;
                var capturedData = data;
                int cw = w, ch = h;
                Task.Run(() =>
                {
                    try
                    {
                        using var ms = new MemoryStream();
                        var writer = new ImageWriter();
                        writer.WritePng(capturedData, cw, ch, ColorComponents.RedGreenBlueAlpha, ms);
                        File.WriteAllBytes(path, ms.ToArray());
                        Notify(true, "导出成功", $"底图已保存到 {Path.GetFileName(path)}");
                    }
                    catch (Exception ex)
                    {
                        DebugServer.AppendLog($"[Export] 导出失败: {ex.Message}");
                        Notify(false, "导出失败", ex.Message);
                    }
                });
            },
            config.LastImageDir, false);
    }

    /// <summary>Export UV wireframe as a transparent PNG overlay.</summary>
    private void ExportUvWireframe(TargetGroup group)
    {
        var mesh = previewService.CurrentMesh;
        if (mesh == null) return;

        var tex = previewService.GetBaseTextureData(group);
        if (tex == null) return;
        int w = tex.Value.Width, h = tex.Value.Height;

        fileDialog.SaveFileDialog(
            "导出 UV 线框",
            "PNG{.png}",
            $"wireframe_{w}x{h}.png",
            ".png",
            (ok, path) =>
            {
                if (!ok || string.IsNullOrEmpty(path)) return;
                var capturedMesh = mesh;
                int cw = w, ch = h;
                Task.Run(() =>
                {
                    try
                    {
                        var pixels = RasterizeWireframe(capturedMesh, cw, ch);
                        using var ms = new MemoryStream();
                        var writer = new ImageWriter();
                        writer.WritePng(pixels, cw, ch, ColorComponents.RedGreenBlueAlpha, ms);
                        File.WriteAllBytes(path, ms.ToArray());
                        Notify(true, "导出成功", $"UV 线框已保存到 {Path.GetFileName(path)}");
                    }
                    catch (Exception ex)
                    {
                        DebugServer.AppendLog($"[Export] 导出失败: {ex.Message}");
                        Notify(false, "导出失败", ex.Message);
                    }
                });
            },
            config.LastImageDir, false);
    }

    private static void Notify(bool success, string title, string content)
    {
        try
        {
            Plugin.NotificationManager.AddNotification(new Notification
            {
                Title = title,
                Content = content,
                Type = success ? NotificationType.Success : NotificationType.Error,
            });
        }
        catch { }
    }

    /// <summary>Rasterize mesh UV wireframe to a transparent RGBA buffer.</summary>
    private static byte[] RasterizeWireframe(MeshData mesh, int w, int h)
    {
        var pixels = new byte[w * h * 4];

        // Find UV tile base (same logic as ComputeMeshUvBase in Canvas)
        float minU = float.MaxValue, minV = float.MaxValue;
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            if (mesh.Vertices[i].UV.X < minU) minU = mesh.Vertices[i].UV.X;
            if (mesh.Vertices[i].UV.Y < minV) minV = mesh.Vertices[i].UV.Y;
        }
        float baseU = MathF.Floor(minU), baseV = MathF.Floor(minV);

        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var i0 = mesh.Indices[i];
            var i1 = mesh.Indices[i + 1];
            var i2 = mesh.Indices[i + 2];
            if (i0 >= mesh.Vertices.Length || i1 >= mesh.Vertices.Length || i2 >= mesh.Vertices.Length)
                continue;

            var uv0 = mesh.Vertices[i0].UV;
            var uv1 = mesh.Vertices[i1].UV;
            var uv2 = mesh.Vertices[i2].UV;

            // Raw UV -> texture [0,1] -> pixel coordinates
            int x0 = (int)((uv0.X - baseU) * w), y0 = (int)((uv0.Y - baseV) * h);
            int x1 = (int)((uv1.X - baseU) * w), y1 = (int)((uv1.Y - baseV) * h);
            int x2 = (int)((uv2.X - baseU) * w), y2 = (int)((uv2.Y - baseV) * h);

            DrawLineBresenham(pixels, w, h, x0, y0, x1, y1);
            DrawLineBresenham(pixels, w, h, x1, y1, x2, y2);
            DrawLineBresenham(pixels, w, h, x2, y2, x0, y0);
        }

        return pixels;
    }

    private static void DrawLineBresenham(byte[] pixels, int w, int h, int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            if (x0 >= 0 && x0 < w && y0 >= 0 && y0 < h)
            {
                int idx = (y0 * w + x0) * 4;
                // Green wireframe on transparent background
                pixels[idx] = 76;      // R
                pixels[idx + 1] = 204; // G
                pixels[idx + 2] = 255; // B
                pixels[idx + 3] = 200; // A
            }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
