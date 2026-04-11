using System.Numerics;

namespace SkinTattoo.Mesh;

public struct MeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 UV;
}

public class MeshData
{
    public MeshVertex[] Vertices { get; init; } = [];
    public int[] Indices { get; init; } = [];
    public int TriangleCount => Indices.Length / 3;

    public (MeshVertex V0, MeshVertex V1, MeshVertex V2) GetTriangle(int triangleIndex)
    {
        var i = triangleIndex * 3;
        return (Vertices[Indices[i]], Vertices[Indices[i + 1]], Vertices[Indices[i + 2]]);
    }
}
