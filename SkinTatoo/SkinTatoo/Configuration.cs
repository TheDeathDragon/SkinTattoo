using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SkinTatoo;

[Serializable]
public class SavedLayer
{
    public string Name { get; set; } = "New Decal";
    public string? ImagePath { get; set; }
    public float UvCenterX { get; set; } = 0.5f;
    public float UvCenterY { get; set; } = 0.5f;
    public float UvScaleX { get; set; } = 0.2f;
    public float UvScaleY { get; set; } = 0.2f;
    public float RotationDeg { get; set; }
    public float Opacity { get; set; } = 1f;
    public int BlendMode { get; set; }
    public bool IsVisible { get; set; } = true;
    public bool AffectsDiffuse { get; set; } = true;

    // Emissive
    public bool AffectsEmissive { get; set; }
    public float EmissiveColorR { get; set; } = 1f;
    public float EmissiveColorG { get; set; } = 1f;
    public float EmissiveColorB { get; set; } = 1f;
    public float EmissiveIntensity { get; set; } = 1f;
    public int EmissiveMask { get; set; }
    public float EmissiveMaskFalloff { get; set; } = 0.5f;
    public float GradientAngleDeg { get; set; }
    public float GradientScale { get; set; } = 1f;
    public float GradientOffset { get; set; }
}

[Serializable]
public class SavedTargetGroup
{
    public string Name { get; set; } = "";
    public string? DiffuseGamePath { get; set; }
    public string? DiffuseDiskPath { get; set; }
    public string? NormGamePath { get; set; }
    public string? NormDiskPath { get; set; }
    public string? MtrlGamePath { get; set; }
    public string? MtrlDiskPath { get; set; }
    public string? MeshDiskPath { get; set; }
    public string? OrigDiffuseDiskPath { get; set; }
    public string? OrigNormDiskPath { get; set; }
    public string? OrigMtrlDiskPath { get; set; }
    public List<SavedLayer> Layers { get; set; } = [];
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public int HttpPort { get; set; } = 14780;
    public int TextureResolution { get; set; } = 1024;

    public List<SavedTargetGroup> TargetGroups { get; set; } = [];

    public bool MainWindowOpen { get; set; }
    public bool DebugWindowOpen { get; set; }
    public string? LastImageDir { get; set; }
    public bool AutoPreview { get; set; }
    public bool UseGpuSwap { get; set; } = true;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
