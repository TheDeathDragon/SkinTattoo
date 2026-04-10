using System.Collections.Generic;

namespace SkinTatoo.Core;

public class TargetGroup
{
    public string Name { get; set; } = "";

    // Target paths
    public string? DiffuseGamePath { get; set; }
    public string? DiffuseDiskPath { get; set; }
    public string? NormGamePath { get; set; }
    public string? NormDiskPath { get; set; }
    public string? MtrlGamePath { get; set; }
    public string? MtrlDiskPath { get; set; }
    public string? MeshDiskPath { get; set; }
    public List<string> MeshDiskPaths { get; set; } = [];
    public HashSet<string> HiddenMeshPaths { get; set; } = [];

    // Legacy single-mdl fields, kept for migration of pre-resolver groups.
    public string? MeshGamePath { get; set; }
    public int[] TargetMatIdx { get; set; } = [];

    // Set by SkinMeshResolver. May be 1 mdl (face/tail/iris/canonical body)
    // or N mdls (mod-injected body materials with no canonical owner).
    public List<MeshSlot> MeshSlots { get; set; } = [];

    /// <summary>
    /// Stable hash of the resolved MeshSlots, written by the resolver and
    /// persisted with the project. The 1Hz polling in ModelEditorWindow
    /// recomputes the resolver result and compares hashes — mismatch means
    /// the player switched gear / toggled a body mod, and the cached
    /// MeshSlots need to be refreshed. Nullable: old configs that don't
    /// have this field deserialize to null, polling short-circuits until
    /// the next manual re-resolve / re-add fills it in.
    /// </summary>
    public string? LiveTreeHash { get; set; }

    public List<string> AllMeshPaths
    {
        get
        {
            var paths = new List<string>();
            if (!string.IsNullOrEmpty(MeshDiskPath))
                paths.Add(MeshDiskPath);
            foreach (var p in MeshDiskPaths)
                if (!string.IsNullOrEmpty(p) && !paths.Contains(p))
                    paths.Add(p);
            return paths;
        }
    }

    public List<string> VisibleMeshPaths
    {
        get
        {
            var paths = new List<string>();
            foreach (var p in AllMeshPaths)
                if (!HiddenMeshPaths.Contains(p))
                    paths.Add(p);
            return paths;
        }
    }

    // Original paths before Penumbra redirect
    public string? OrigDiffuseDiskPath { get; set; }
    public string? OrigNormDiskPath { get; set; }
    public string? OrigMtrlDiskPath { get; set; }

    public List<DecalLayer> Layers { get; } = [];
    public int SelectedLayerIndex { get; set; } = -1;
    public bool IsExpanded { get; set; } = true;

    public DecalLayer? SelectedLayer =>
        SelectedLayerIndex >= 0 && SelectedLayerIndex < Layers.Count
            ? Layers[SelectedLayerIndex] : null;

    public DecalLayer AddLayer(string name = "New Decal", LayerKind kind = LayerKind.Decal)
    {
        var layer = new DecalLayer { Name = name, Kind = kind };
        Layers.Add(layer);
        SelectedLayerIndex = Layers.Count - 1;
        return layer;
    }

    public void RemoveLayer(int index)
    {
        if (index < 0 || index >= Layers.Count) return;
        Layers.RemoveAt(index);
        if (SelectedLayerIndex >= Layers.Count)
            SelectedLayerIndex = Layers.Count - 1;
    }

    public void MoveLayerUp(int index)
    {
        if (index <= 0 || index >= Layers.Count) return;
        (Layers[index], Layers[index - 1]) = (Layers[index - 1], Layers[index]);
        if (SelectedLayerIndex == index) SelectedLayerIndex = index - 1;
        else if (SelectedLayerIndex == index - 1) SelectedLayerIndex = index;
    }

    public void MoveLayerDown(int index)
    {
        if (index < 0 || index >= Layers.Count - 1) return;
        (Layers[index], Layers[index + 1]) = (Layers[index + 1], Layers[index]);
        if (SelectedLayerIndex == index) SelectedLayerIndex = index + 1;
        else if (SelectedLayerIndex == index + 1) SelectedLayerIndex = index;
    }

    public bool HasEmissiveLayers()
    {
        foreach (var l in Layers)
            if (l.IsVisible && l.AffectsEmissive && !string.IsNullOrEmpty(l.ImagePath))
                return true;
        return false;
    }

    public bool HasPbrLayers()
    {
        foreach (var l in Layers)
            if (l.IsVisible && l.RequiresRowPair && !string.IsNullOrEmpty(l.ImagePath))
                return true;
        return false;
    }
}
