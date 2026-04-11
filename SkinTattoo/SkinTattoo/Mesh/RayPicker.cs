using System.Numerics;

namespace SkinTattoo.Mesh;

public struct RayHit
{
    public int TriangleIndex;
    public float Distance;
    public Vector2 UV;
    public Vector3 WorldPosition;
    public Vector3 Normal;
}

public static class RayPicker
{
    public static RayHit? Pick(MeshData mesh, Vector3 rayOrigin, Vector3 rayDir)
    {
        float bestDist = float.MaxValue;
        RayHit? bestHit = null;

        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            var (v0, v1, v2) = mesh.GetTriangle(t);

            if (RayTriangleIntersect(rayOrigin, rayDir, v0.Position, v1.Position, v2.Position,
                    out float dist, out float u, out float v))
            {
                if (dist > 0 && dist < bestDist)
                {
                    bestDist = dist;
                    float w0 = 1f - u - v;
                    float w1 = u;
                    float w2 = v;

                    bestHit = new RayHit
                    {
                        TriangleIndex = t,
                        Distance = dist,
                        UV = w0 * v0.UV + w1 * v1.UV + w2 * v2.UV,
                        WorldPosition = w0 * v0.Position + w1 * v1.Position + w2 * v2.Position,
                        Normal = Vector3.Normalize(w0 * v0.Normal + w1 * v1.Normal + w2 * v2.Normal),
                    };
                }
            }
        }

        return bestHit;
    }

    private static bool RayTriangleIntersect(
        Vector3 orig, Vector3 dir,
        Vector3 v0, Vector3 v1, Vector3 v2,
        out float t, out float u, out float v)
    {
        t = u = v = 0;
        const float epsilon = 1e-8f;

        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var h = Vector3.Cross(dir, e2);
        float a = Vector3.Dot(e1, h);

        if (a > -epsilon && a < epsilon) return false;

        float f = 1f / a;
        var s = orig - v0;
        u = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return false;

        var q = Vector3.Cross(s, e1);
        v = f * Vector3.Dot(dir, q);
        if (v < 0f || u + v > 1f) return false;

        t = f * Vector3.Dot(e2, q);
        return t > epsilon;
    }
}
