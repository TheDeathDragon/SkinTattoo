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
    public int Clip { get; set; }

    // v1 PBR: layer kind
    public int Kind { get; set; } = 0;   // 0 = Decal, 1 = WholeMaterial

    // Field-level affect switches
    public bool AffectsSpecular { get; set; }
    public bool AffectsRoughness { get; set; }
    public bool AffectsMetalness { get; set; }
    public bool AffectsSheen { get; set; }

    // PBR field values
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
    public int HttpPort { get; set; } = 14780;
    public int TextureResolution { get; set; } = 1024;

    public List<SavedTargetGroup> TargetGroups { get; set; } = [];
    public int SelectedGroupIndex { get; set; } = -1;

    public bool MainWindowOpen { get; set; }
    public bool DebugWindowOpen { get; set; }
    public bool ModelEditorWindowOpen { get; set; }
    public bool PbrInspectorWindowOpen { get; set; }
    public string? LastImageDir { get; set; }
    public bool AutoPreview { get; set; }
    public bool UseGpuSwap { get; set; } = true;

    // Game-side GPU swap throttle. Each SwapTexture call at 4096² is ~5-15ms main-thread
    // work (GpuTexture.CreateTexture2D + 64MB InitializeContents + Interlocked.Exchange),
    // so running it at the full 30Hz drag rate eats the frame budget. The 3D editor preview
    // already shows the latest state; the game-side swap only needs to catch up periodically
    // plus a final flush when the user stops interacting.
    public int GameSwapIntervalMs { get; set; } = 150;

    // Mod export defaults
    public string DefaultAuthor { get; set; } = "";
    public string DefaultVersion { get; set; } = "1.0";
    public string? LastExportDir { get; set; }

    // v1 PBR: if true, MainWindow shows a one-time dialog explaining the
    // EmissiveMask → LayerFadeMask semantics change (widens to all PBR fields).
    // Set to true on first load of any v3-saved project; cleared after user acks.
    public bool ShowLayerFadeMaskMigrationNotice { get; set; } = false;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;

        // v3 → v4: EmissiveMask semantics widened (now fades all PBR fields, not just emissive).
        // Trigger a one-time notice dialog on next MainWindow draw.
        if (Version < 4 && TargetGroups.Count > 0)
        {
            ShowLayerFadeMaskMigrationNotice = true;
        }
        Version = 4;

        // Clamp swap interval to a sane floor on load. Earlier slider allowed 0 which
        // would make ApplyPendingSwaps fire every frame (~60Hz × 64MB × N textures =
        // unplayable). 33ms ≈ 30Hz matches the compose throttle.
        if (GameSwapIntervalMs < 33) GameSwapIntervalMs = 33;
        if (GameSwapIntervalMs > 500) GameSwapIntervalMs = 500;
    }
    public void Save() => pluginInterface?.SavePluginConfig(this);
}
