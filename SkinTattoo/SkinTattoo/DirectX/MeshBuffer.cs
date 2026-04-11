using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SkinTattoo.Mesh;
using Device = SharpDX.Direct3D11.Device;

namespace SkinTattoo.DirectX;

[StructLayout(LayoutKind.Sequential)]
public struct GpuVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 UV;

    public static readonly int SizeInBytes = Utilities.SizeOf<GpuVertex>();
}

public class MeshBuffer : IDisposable
{
    private SharpDX.Direct3D11.Buffer? vertexBuffer;
    private SharpDX.Direct3D11.Buffer? indexBuffer;
    private int indexCount;

    public int IndexCount => indexCount;
    public bool IsLoaded => vertexBuffer != null && indexCount > 0;

    public void Upload(Device device, MeshData mesh)
    {
        var vertices = new GpuVertex[mesh.Vertices.Length];
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            var v = mesh.Vertices[i];
            vertices[i] = new GpuVertex
            {
                Position = new Vector3(v.Position.X, v.Position.Y, v.Position.Z),
                Normal = new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z),
                UV = new Vector2(v.UV.X, v.UV.Y),
            };
        }

        var newVb = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.VertexBuffer, vertices);
        var newIb = SharpDX.Direct3D11.Buffer.Create(device, BindFlags.IndexBuffer, mesh.Indices);

        vertexBuffer?.Dispose();
        indexBuffer?.Dispose();

        vertexBuffer = newVb;
        indexBuffer = newIb;
        indexCount = mesh.Indices.Length;
    }

    public void Bind(DeviceContext ctx)
    {
        if (vertexBuffer == null || indexBuffer == null) return;
        ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vertexBuffer, GpuVertex.SizeInBytes, 0));
        ctx.InputAssembler.SetIndexBuffer(indexBuffer, Format.R32_UInt, 0);
    }

    public void DrawIndexed(DeviceContext ctx)
    {
        if (indexCount <= 0) return;
        ctx.DrawIndexed(indexCount, 0, 0);
    }

    public void Dispose()
    {
        vertexBuffer?.Dispose();
        vertexBuffer = null;
        indexBuffer?.Dispose();
        indexBuffer = null;
        indexCount = 0;
    }
}
