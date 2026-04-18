using System.Numerics;

namespace SkinTattoo.Core;

public enum BlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    SoftLight,
    HardLight,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    Difference,
    Exclusion,
}

public enum LayerFadeMask
{
    Uniform,
    RadialFadeOut,
    RadialFadeIn,
    EdgeGlow,
    DirectionalGradient,
    GaussianFeather,
    ShapeOutline,
}

public enum ClipMode
{
    None,
    ClipLeft,
    ClipRight,
    ClipTop,
    ClipBottom,
}

public enum EmissiveAnimMode
{
    None = 0,
    Pulse = 1,
    Flicker = 2,
    Gradient = 3,
    Ripple = 4,
}

public enum RippleDirMode
{
    Radial = 0,
    Linear = 1,
    Bidirectional = 2,
}

public enum TargetMap
{
    Diffuse = 0,
    Mask = 1,
    Normal = 2,
}

public class DecalLayer
{
    public LayerKind Kind { get; set; } = LayerKind.Decal;

    public string Name { get; set; } = "New Decal";
    public string? ImagePath { get; set; }

    public Vector2 UvCenter { get; set; } = new(0.5f, 0.5f);
    public Vector2 UvScale { get; set; } = new(0.2f, 0.2f);
    public float RotationDeg { get; set; } = 0f;

    public float Opacity { get; set; } = 1.0f;
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;
    public ClipMode Clip { get; set; } = ClipMode.None;
    public bool IsVisible { get; set; } = true;

    // Mask/Normal targets paint RGB only so normal.alpha stays free for the
    // emissive mask / ColorTable row index written by CompositeEmissiveNorm etc.
    public TargetMap TargetMap { get; set; } = TargetMap.Diffuse;

    public bool AffectsDiffuse { get; set; } = true;
    public bool AffectsSpecular { get; set; } = false;
    public bool AffectsEmissive { get; set; } = false;
    public bool AffectsRoughness { get; set; } = false;
    public bool AffectsMetalness { get; set; } = false;
    public bool AffectsSheen { get; set; } = false;

    public Vector3 DiffuseColor { get; set; } = new(1f, 1f, 1f);
    public Vector3 SpecularColor { get; set; } = new(1f, 1f, 1f);
    public Vector3 EmissiveColor { get; set; } = new(1f, 1f, 1f);
    public float EmissiveIntensity { get; set; } = 1.0f;
    public EmissiveAnimMode AnimMode { get; set; } = EmissiveAnimMode.None;
    public float AnimSpeed { get; set; } = 1.0f;
    public float AnimAmplitude { get; set; } = 0.5f;
    public Vector3 EmissiveColorB { get; set; } = new(0f, 0f, 1f);
    // Ripple frequency (rings per UV unit). ~20 gives 20 ring peaks across the full texture.
    public float AnimFreq { get; set; } = 20f;
    // Ripple wave direction mode.
    public RippleDirMode AnimDirMode { get; set; } = RippleDirMode.Radial;
    // Ripple wave angle in degrees (0=+U axis, 90=+V axis). Only used in Linear/Bidirectional.
    public float AnimDirAngle { get; set; } = 0f;
    // When true, Ripple peaks/valleys interpolate between colorA and colorB (like Gradient in space).
    public bool AnimDualColor { get; set; } = false;
    public float Roughness { get; set; } = 0.5f;
    public float Metalness { get; set; } = 0f;
    public float SheenRate { get; set; } = 0.1f;
    public float SheenTint { get; set; } = 0.2f;
    public float SheenAperture { get; set; } = 5.0f;

    public LayerFadeMask FadeMask { get; set; } = LayerFadeMask.Uniform;
    public float FadeMaskFalloff { get; set; } = 0.5f;
    public float GradientAngleDeg { get; set; } = 0f;
    public float GradientScale { get; set; } = 1f;
    public float GradientOffset { get; set; } = 0f;

    // Runtime-only, not persisted  -- recomputed every session
    public int AllocatedRowPair { get; set; } = -1;

    /// <summary>True if any PBR field is enabled  -- gate for row pair allocation.</summary>
    public bool RequiresRowPair =>
        AffectsDiffuse || AffectsSpecular || AffectsEmissive
        || AffectsRoughness || AffectsMetalness || AffectsSheen;
}
