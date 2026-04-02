using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Models.Models;
using static Lumina.Models.Models.Model;

namespace SkinTatoo.Mesh;

public class MeshExtractor
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    public MeshExtractor(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public MeshData? ExtractMesh(string gamePath)
    {
        var mdlFile = dataManager.GetFile<MdlFile>(gamePath);
        if (mdlFile == null)
        {
            log.Error("Failed to load .mdl file: {0}", gamePath);
            return null;
        }

        try
        {
            var model = new Model(mdlFile, Model.ModelLod.High);
            var allVertices = new List<MeshVertex>();
            var allIndices = new List<ushort>();

            foreach (var mesh in model.Meshes)
            {
                var baseVertex = (ushort)allVertices.Count;

                foreach (var v in mesh.Vertices)
                {
                    allVertices.Add(new MeshVertex
                    {
                        Position = v.Position.HasValue ? ToVector3(v.Position.Value) : Vector3.Zero,
                        Normal = v.Normal ?? Vector3.UnitY,
                        UV = v.UV.HasValue ? ToVector2(v.UV.Value) : Vector2.Zero,
                    });
                }

                foreach (var idx in mesh.Indices)
                {
                    allIndices.Add((ushort)(idx + baseVertex));
                }
            }

            var result = new MeshData
            {
                Vertices = allVertices.ToArray(),
                Indices = allIndices.ToArray(),
            };

            log.Information("Extracted mesh: {0} vertices, {1} triangles from {2}",
                result.Vertices.Length, result.TriangleCount, gamePath);

            return result;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to parse .mdl file: {0}", gamePath);
            return null;
        }
    }

    private static Vector3 ToVector3(Vector4 v) => new(v.X, v.Y, v.Z);
    private static Vector2 ToVector2(Vector4 v) => new(v.X, v.Y);
}
