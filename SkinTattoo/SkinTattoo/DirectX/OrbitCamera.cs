using System;
using SharpDX;

namespace SkinTattoo.DirectX;

public class OrbitCamera
{
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Distance { get; set; } = 3f;
    public Vector3 Target { get; set; } = Vector3.Zero;
    public Vector3 PanOffset { get; set; } = Vector3.Zero;

    public Matrix ViewMatrix { get; private set; }
    public Matrix ProjMatrix { get; private set; }
    public Vector3 CameraPosition { get; private set; }

    private float aspect = 1f;

    public void SetAspect(float w, float h)
    {
        aspect = w / Math.Max(h, 1f);
    }

    public void Update()
    {
        var rotation = Quaternion.RotationYawPitchRoll(Yaw, Pitch, 0f);
        var forward = Vector3.Transform(-Vector3.UnitZ, rotation);
        CameraPosition = Target + PanOffset - Distance * forward;
        var up = Vector3.Transform(Vector3.UnitY, rotation);

        ViewMatrix = Matrix.LookAtLH(CameraPosition, Target + PanOffset, up);
        ProjMatrix = Matrix.PerspectiveFovLH((float)Math.PI / 4f, aspect, 0.01f, 100f);
    }

    public void Rotate(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = Math.Clamp(Pitch + deltaPitch, -1.55f, 1.55f);
        Update();
    }

    public void Pan(float deltaX, float deltaY)
    {
        var rotation = Quaternion.RotationYawPitchRoll(Yaw, Pitch, 0f);
        var right = Vector3.Transform(Vector3.UnitX, rotation);
        var up = Vector3.Transform(Vector3.UnitY, rotation);
        PanOffset += right * deltaX * Distance * 0.002f + up * deltaY * Distance * 0.002f;
        Update();
    }

    public void Zoom(float delta)
    {
        Distance = Math.Max(0.01f, Distance - delta * 0.2f);
        Update();
    }

    public void Reset()
    {
        Yaw = 0;
        Pitch = 0;
        Distance = 3f;
        PanOffset = Vector3.Zero;
        Update();
    }

    public (Vector3 Origin, Vector3 Direction) ScreenToRay(
        float screenX, float screenY, float viewportWidth, float viewportHeight)
    {
        float ndcX = (2f * screenX / viewportWidth) - 1f;
        float ndcY = 1f - (2f * screenY / viewportHeight);

        var invViewProj = Matrix.Invert(ViewMatrix * ProjMatrix);

        var nearPoint = Vector3.TransformCoordinate(new Vector3(ndcX, ndcY, 0f), invViewProj);
        var farPoint = Vector3.TransformCoordinate(new Vector3(ndcX, ndcY, 1f), invViewProj);

        var direction = Vector3.Normalize(farPoint - nearPoint);
        return (nearPoint, direction);
    }
}
