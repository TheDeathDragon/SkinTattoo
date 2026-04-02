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
    public bool AffectsMask { get; set; }
    public float GlowSpecular { get; set; } = 0.8f;
    public float GlowSmoothness { get; set; } = 0.8f;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public int HttpPort { get; set; } = 14780;
    public int TextureResolution { get; set; } = 1024;
    public Core.SkinTarget LastTarget { get; set; } = Core.SkinTarget.Body;

    public List<SavedLayer> Layers { get; set; } = [];
    public bool MainWindowOpen { get; set; }
    public bool DebugWindowOpen { get; set; }

    // User-selected target paths (set via debug window)
    public string? TargetTextureGamePath { get; set; }
    public string? TargetTextureDiskPath { get; set; }
    public string? TargetNormGamePath { get; set; }
    public string? TargetNormDiskPath { get; set; }
    public string? TargetMaskGamePath { get; set; }
    public string? TargetMaskDiskPath { get; set; }
    public string? TargetMeshDiskPath { get; set; }

    public string? LastImageDir { get; set; }
    public bool AutoPreview { get; set; }

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
