using System.Collections.Generic;

namespace SkinTattoo.Core;

public class TargetGroup
{
    public string Name { get; set; } = "";

    public string? DiffuseGamePath { get; set; }
    public string? DiffuseDiskPath { get; set; }
    public string? NormGamePath { get; set; }
    public string? NormDiskPath { get; set; }
    public string? MtrlGamePath { get; set; }
    public string? MtrlDiskPath { get; set; }
    public string? MeshDiskPath { get; set; }
    public List<string> MeshDiskPaths { get; set; } = [];
    public HashSet<string> HiddenMeshPaths { get; set; } = [];

    public string? MeshGamePath { get; set; }
    public int[] TargetMatIdx { get; set; } = [];
    public List<MeshSlot> MeshSlots { get; set; } = [];

    // Stable hash for detecting equipment/body-mod swaps via 1Hz polling
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
            if (l.IsVisible && (l.TargetMap == TargetMap.Diffuse || l.TargetMap == TargetMap.Normal)
                && l.AffectsEmissive && !string.IsNullOrEmpty(l.ImagePath))
                return true;
        return false;
    }

    /// Visibility-agnostic counterpart of <see cref="HasEmissiveLayers"/>. The redirect
    /// build pipeline keys mtrl + patched shpk emission off this so Penumbra's temp-mod
    /// set stays stable across visibility/hover/composite cycles -- otherwise a single
    /// frame where every emissive layer happens to look hidden produces a slim redirect
    /// set, AddTemporaryModAll replaces the previous full set, and the next drag falls
    /// back to Full Redraw because previewDiskPaths/initializedRedirects can no longer
    /// gate CheckCanSwapInPlace correctly.
    public bool HasEmissiveConfiguredAny()
    {
        foreach (var l in Layers)
            if ((l.TargetMap == TargetMap.Diffuse || l.TargetMap == TargetMap.Normal)
                && l.AffectsEmissive && !string.IsNullOrEmpty(l.ImagePath))
                return true;
        return false;
    }

    public bool HasPbrLayers()
    {
        foreach (var l in Layers)
            if (l.IsVisible && l.TargetMap == TargetMap.Diffuse
                && l.RequiresRowPair && !string.IsNullOrEmpty(l.ImagePath))
                return true;
        return false;
    }

    public void ReplaceLayersFrom(TargetGroup source)
    {
        Layers.Clear();
        foreach (var layer in source.Layers)
            Layers.Add(layer.Clone());

        SelectedLayerIndex = source.SelectedLayerIndex >= 0 && source.SelectedLayerIndex < Layers.Count
            ? source.SelectedLayerIndex
            : (Layers.Count > 0 ? 0 : -1);
    }
}
