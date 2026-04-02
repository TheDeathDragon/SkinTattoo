using System;
using System.Numerics;

namespace SkinTatoo.Mesh;

public class PositionMapGenerator
{
    public int Width { get; }
    public int Height { get; }
    public float[] PositionMap { get; }
    public float[] NormalMap { get; }

    public PositionMapGenerator(int width, int height)
    {
        Width = width;
        Height = height;
        PositionMap = new float[width * height * 4];
        NormalMap = new float[width * height * 4];
    }

    public void Generate(MeshData mesh)
    {
        Array.Clear(PositionMap);
        Array.Clear(NormalMap);

        for (var tri = 0; tri < mesh.TriangleCount; tri++)
        {
            var (v0, v1, v2) = mesh.GetTriangle(tri);
            RasterizeTriangle(v0, v1, v2);
        }
    }

    private void RasterizeTriangle(MeshVertex v0, MeshVertex v1, MeshVertex v2)
    {
        var p0 = UvToPixel(v0.UV);
        var p1 = UvToPixel(v1.UV);
        var p2 = UvToPixel(v2.UV);

        var minX = Math.Max(0, (int)MathF.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X))));
        var maxX = Math.Min(Width - 1, (int)MathF.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
        var minY = Math.Max(0, (int)MathF.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))));
        var maxY = Math.Min(Height - 1, (int)MathF.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var px = new Vector2(x + 0.5f, y + 0.5f);
                var bary = Barycentric(px, p0, p1, p2);

                if (bary.X < 0 || bary.Y < 0 || bary.Z < 0)
                    continue;

                var pos = v0.Position * bary.X + v1.Position * bary.Y + v2.Position * bary.Z;
                var nor = Vector3.Normalize(v0.Normal * bary.X + v1.Normal * bary.Y + v2.Normal * bary.Z);

                var idx = (y * Width + x) * 4;
                PositionMap[idx + 0] = pos.X;
                PositionMap[idx + 1] = pos.Y;
                PositionMap[idx + 2] = pos.Z;
                PositionMap[idx + 3] = 1.0f;

                NormalMap[idx + 0] = nor.X;
                NormalMap[idx + 1] = nor.Y;
                NormalMap[idx + 2] = nor.Z;
                NormalMap[idx + 3] = 0.0f;
            }
        }
    }

    private Vector2 UvToPixel(Vector2 uv)
    {
        return new Vector2(uv.X * Width, uv.Y * Height);
    }

    private static Vector3 Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var v0 = c - a;
        var v1 = b - a;
        var v2 = p - a;

        var dot00 = Vector2.Dot(v0, v0);
        var dot01 = Vector2.Dot(v0, v1);
        var dot02 = Vector2.Dot(v0, v2);
        var dot11 = Vector2.Dot(v1, v1);
        var dot12 = Vector2.Dot(v1, v2);

        var inv = 1.0f / (dot00 * dot11 - dot01 * dot01);
        var u = (dot11 * dot02 - dot01 * dot12) * inv;
        var v = (dot00 * dot12 - dot01 * dot02) * inv;
        var w = 1.0f - u - v;

        return new Vector3(w, v, u);
    }
}
