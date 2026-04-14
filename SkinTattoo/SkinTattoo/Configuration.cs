using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SkinTattoo;

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
    public int Clip { get; set; }

    public int Kind { get; set; } = 0;

    public bool AffectsSpecular { get; set; }
    public bool AffectsRoughness { get; set; }
    public bool AffectsMetalness { get; set; }
    public bool AffectsSheen { get; set; }

    public float DiffuseColorR { get; set; } = 1f;
    public float DiffuseColorG { get; set; } = 1f;
    public float DiffuseColorB { get; set; } = 1f;

    public float SpecularColorR { get; set; } = 1f;
    public float SpecularColorG { get; set; } = 1f;
    public float SpecularColorB { get; set; } = 1f;

    public float Roughness { get; set; } = 0.5f;
    public float Metalness { get; set; } = 0f;
    public float SheenRate { get; set; } = 0.1f;
    public float SheenTint { get; set; } = 0.2f;
    public float SheenAperture { get; set; } = 5.0f;
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
    public List<string> MeshDiskPaths { get; set; } = [];
    public int SelectedLayerIndex { get; set; } = -1;
    public string? OrigDiffuseDiskPath { get; set; }
    public string? OrigNormDiskPath { get; set; }
    public string? OrigMtrlDiskPath { get; set; }
    public List<SavedLayer> Layers { get; set; } = [];
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;
    public bool PluginEnabled { get; set; } = true;
    public string Language { get; set; } = "en";
    public bool HttpEnabled { get; set; } = false;
    public int HttpPort { get; set; } = 12580;

    public List<SavedTargetGroup> TargetGroups { get; set; } = [];
    public int SelectedGroupIndex { get; set; } = -1;

    public bool MainWindowOpen { get; set; }
    public bool DebugWindowOpen { get; set; }
    public bool ModelEditorWindowOpen { get; set; }
    public string? LastImageDir { get; set; }
    public bool AutoPreview { get; set; } = true;
    public bool UseGpuSwap { get; set; } = true;

    // GPU swap throttle: 4096^2 SwapTexture costs 5-15ms, throttle to avoid exceeding frame budget
    public int GameSwapIntervalMs { get; set; } = 150;

    public string DefaultAuthor { get; set; } = "";
    public string DefaultVersion { get; set; } = "1.0";
    public string? LastExportDir { get; set; }

    public bool UvWireframeAntiAlias { get; set; } = false;
    public bool UvWireframeCulling { get; set; } = true;
    public bool UvWireframeDedup { get; set; } = false;
    public float UvWireframeColorR { get; set; } = 0f;
    public float UvWireframeColorG { get; set; } = 0.141f;
    public float UvWireframeColorB { get; set; } = 1f;
    public float UvWireframeColorA { get; set; } = 0.784f;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;

        if (GameSwapIntervalMs < 33) GameSwapIntervalMs = 33;
        if (GameSwapIntervalMs > 500) GameSwapIntervalMs = 500;
    }
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
