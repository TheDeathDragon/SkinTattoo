using System.Numerics;

namespace SkinTatoo.Core;

public enum BlendMode
{
    Normal,
    Multiply,
    Overlay,
    SoftLight,
}

public class DecalLayer
{
    public string Name { get; set; } = "New Decal";
    public string? ImagePath { get; set; }

    // UV-space placement (0-1 range)
    public Vector2 UvCenter { get; set; } = new(0.5f, 0.5f);
    public Vector2 UvScale { get; set; } = new(0.2f, 0.2f);
    public float RotationDeg { get; set; } = 0f;

    public float Opacity { get; set; } = 1.0f;
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;
    public bool IsVisible { get; set; } = true;

    // Which texture channels to affect
    public bool AffectsDiffuse { get; set; } = true;
    public bool AffectsMask { get; set; } = false;

    // Glow/specular override for mask texture (when AffectsMask=true)
    // These values are written to the mask R/G channels in the decal area
    public float GlowSpecular { get; set; } = 0.8f;   // Mask R channel (0=matte, 1=mirror)
    public float GlowSmoothness { get; set; } = 0.8f;  // Mask G channel inverted (0=rough, 1=smooth → stored as 1-roughness)
}
