using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SkinTattoo.Core;
using SkinTattoo.Services;

namespace SkinTattoo.Gui;

public class ModExportWindow : Window
{
    private readonly DecalProject project;
    private readonly ModExportService exportService;
    private readonly Configuration config;
    private readonly FileDialogManager fileDialog = new();

    private ExportTarget currentTarget = ExportTarget.LocalPmp;
    private string modName = "";
    private string author = "";
    private string version = "1.0";
    private string description = "";
    private string outputPath = "";
    private readonly HashSet<int> selectedGroupIndices = new();

    // Written from background Task.Run, read from UI thread  -- volatile so UI sees the unset
    private volatile bool exporting;

    public ModExportWindow(DecalProject project, ModExportService exportService, Configuration config)
        : base("导出 Mod###SkinTattooExport",
               ImGuiWindowFlags.NoCollapse)
    {
        this.project = project;
        this.exportService = exportService;
        this.config = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 400),
            MaximumSize = new Vector2(640, 800),
        };
        IsOpen = false;
    }

    /// <summary>Open the dialog with a target preset. Resets fields from config defaults.</summary>
    public void OpenAs(ExportTarget target)
    {
        currentTarget = target;
        WindowName = target == ExportTarget.LocalPmp
            ? "导出 Mod 到本地###SkinTattooExport"
            : "安装 Mod 到 Penumbra###SkinTattooExport";

        author = config.DefaultAuthor;
        version = string.IsNullOrEmpty(config.DefaultVersion) ? "1.0" : config.DefaultVersion;

        if (string.IsNullOrEmpty(modName))
            modName = "SkinTattoo Decal";

        // Default-select all groups with layers
        selectedGroupIndices.Clear();
        for (int i = 0; i < project.Groups.Count; i++)
            if (project.Groups[i].Layers.Count > 0)
                selectedGroupIndices.Add(i);

        if (string.IsNullOrEmpty(description))
            description = $"由 SkinTattoo 生成  -- 包含 {selectedGroupIndices.Count} 个图层组";

        IsOpen = true;
    }

    public override void Draw()
    {
        fileDialog.Draw();

        if (closeAfterExport)
        {
            closeAfterExport = false;
            IsOpen = false;
            return;
        }

        ImGui.Text("Mod 名称");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##modName", ref modName, 128);

        ImGui.Text("作者");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##author", ref author, 64))
        {
            config.DefaultAuthor = author;
            config.Save();
        }

        ImGui.Text("版本");
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputText("##version", ref version, 16))
        {
            config.DefaultVersion = version;
            config.Save();
        }

        ImGui.Text("描述");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("##desc", ref description, 1024, new Vector2(-1, 60));

        if (currentTarget == ExportTarget.LocalPmp)
        {
            ImGui.Text("导出位置");
            ImGui.SetNextItemWidth(-90);
            ImGui.InputText("##outPath", ref outputPath, 512);
            ImGui.SameLine();
            if (ImGui.Button("浏览…"))
            {
                var startDir = !string.IsNullOrEmpty(config.LastExportDir) && Directory.Exists(config.LastExportDir)
                    ? config.LastExportDir
                    : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var defaultName = SafeFileName(modName) + ".pmp";
                fileDialog.SaveFileDialog("保存 Mod", ".pmp", defaultName, ".pmp", (ok, path) =>
                {
                    if (ok && !string.IsNullOrEmpty(path))
                    {
                        outputPath = path;
                        config.LastExportDir = Path.GetDirectoryName(path);
                        config.Save();
                    }
                }, startDir);
            }
        }

        ImGui.Separator();
        ImGui.Text("包含的图层组");
        if (project.Groups.Count == 0)
        {
            ImGui.TextColored(new Vector4(1, 0.6f, 0, 1), "（项目中没有图层组）");
        }
        else
        {
            using (ImRaii.Child("##groups", new Vector2(-1, 120), true))
            {
                for (int i = 0; i < project.Groups.Count; i++)
                {
                    var g = project.Groups[i];
                    bool sel = selectedGroupIndices.Contains(i);
                    var label = $"{g.Name} ({g.Layers.Count} 图层)##g{i}";
                    if (ImGui.Checkbox(label, ref sel))
                    {
                        if (sel) selectedGroupIndices.Add(i);
                        else selectedGroupIndices.Remove(i);
                    }
                }
            }
        }

        ImGui.Separator();

        var canExport = !exporting && !string.IsNullOrWhiteSpace(modName) && selectedGroupIndices.Count > 0
            && (currentTarget != ExportTarget.LocalPmp || !string.IsNullOrWhiteSpace(outputPath));

        using (ImRaii.Disabled(!canExport))
        {
            var btnLabel = currentTarget == ExportTarget.LocalPmp ? "导出到本地" : "安装到 Penumbra";
            if (exporting) btnLabel = "导出中…";
            if (ImGui.Button(btnLabel, new Vector2(160, 0)))
            {
                StartExport();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("关闭"))
            IsOpen = false;
    }

    private void StartExport()
    {
        if (exporting) return;

        var options = new ModExportOptions
        {
            ModName = modName.Trim(),
            Author = author.Trim(),
            Version = version.Trim(),
            Description = description,
            Target = currentTarget,
            OutputPmpPath = currentTarget == ExportTarget.LocalPmp ? outputPath : null,
        };
        foreach (var i in selectedGroupIndices)
            if (i >= 0 && i < project.Groups.Count)
                options.SelectedGroups.Add(project.Groups[i]);

        exporting = true;
        Task.Run(() =>
        {
            try
            {
                var result = exportService.Export(options);
                if (result.Success)
                    closeAfterExport = true;
            }
            finally { exporting = false; }
        });
    }

    private volatile bool closeAfterExport;

    private static string SafeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        var result = new string(chars).Trim();
        return string.IsNullOrEmpty(result) ? "SkinTattoo" : result;
    }
}
