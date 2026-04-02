using System;
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
    public Vector3 Position { get; set; } = Vector3.Zero;
    public Vector3 Rotation { get; set; } = Vector3.Zero;
    public Vector2 Scale { get; set; } = Vector2.One;
    public float Depth { get; set; } = 0.5f;
    public float Opacity { get; set; } = 1.0f;
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;
    public float BackfaceCullingThreshold { get; set; } = 0.1f;
    public float GrazingAngleFade { get; set; } = 0.3f;
    public bool IsVisible { get; set; } = true;
    public bool AffectsDiffuse { get; set; } = true;
    public bool AffectsNormal { get; set; } = false;

    public Matrix4x4 GetProjectionMatrix()
    {
        var view = Matrix4x4.CreateLookAt(
            Position,
            Position + GetForwardDirection(),
            Vector3.UnitY);
        var proj = Matrix4x4.CreateOrthographic(Scale.X, Scale.Y, 0, Depth);
        return view * proj;
    }

    public Vector3 GetForwardDirection()
    {
        var pitch = Rotation.X * (MathF.PI / 180f);
        var yaw = Rotation.Y * (MathF.PI / 180f);
        return new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw));
    }
}
