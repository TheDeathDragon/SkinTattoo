using System.Numerics;

namespace SkinTatoo.Core;

public enum BlendMode
{
    Normal,
    Multiply,
    Overlay,
    SoftLight,
}

public enum EmissiveMask
{
    Uniform,             // flat glow across entire decal
    RadialFadeOut,       // center bright → edge dim
    RadialFadeIn,        // edge bright → center dim (ring glow)
    EdgeGlow,            // only edges glow, interior dark
    DirectionalGradient, // linear gradient at configurable angle
    GaussianFeather,     // gaussian blur-style soft falloff from edges
    ShapeOutline,        // glow along decal alpha edges (like PS outer glow/drop shadow)
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

    public bool AffectsDiffuse { get; set; } = true;

    // Emissive glow — controlled via .mtrl shader key + g_EmissiveColor
    public bool AffectsEmissive { get; set; } = false;
    public Vector3 EmissiveColor { get; set; } = new(1f, 1f, 1f);
    public float EmissiveIntensity { get; set; } = 1.0f;
    public EmissiveMask EmissiveMask { get; set; } = EmissiveMask.Uniform;
    public float EmissiveMaskFalloff { get; set; } = 0.5f; // controls gradient steepness (0=sharp, 1=wide)
    public float GradientAngleDeg { get; set; } = 0f;      // angle for DirectionalGradient (degrees)
    public float GradientScale { get; set; } = 1f;          // size/spread for DirectionalGradient
    public float GradientOffset { get; set; } = 0f;         // offset/shift for DirectionalGradient (-1 to 1)
}
