using System.Collections.Generic;

namespace SkinTattoo.Core;

public enum ExportTarget
{
    LocalPmp,
    InstallToPenumbra,
}

public class ModExportOptions
{
    public string ModName { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0";
    public string Description { get; set; } = "";
    public List<TargetGroup> SelectedGroups { get; set; } = [];
    public ExportTarget Target { get; set; }
    public string? OutputPmpPath { get; set; }
}

public class ModExportResult
{
    public bool Success { get; set; }
    public string? PmpPath { get; set; }
    public string Message { get; set; } = "";
    public int SuccessGroups { get; set; }
    public int SkippedGroups { get; set; }
}
