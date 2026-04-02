using System.Collections.Generic;

namespace SkinTatoo.Core;

public enum SkinTarget
{
    Body,
    Face,
}

public class DecalProject
{
    public List<DecalLayer> Layers { get; } = [];
    public SkinTarget Target { get; set; } = SkinTarget.Body;
    public int SelectedLayerIndex { get; set; } = -1;

    public DecalLayer? SelectedLayer =>
        SelectedLayerIndex >= 0 && SelectedLayerIndex < Layers.Count
            ? Layers[SelectedLayerIndex]
            : null;

    public DecalLayer AddLayer(string name = "New Decal")
    {
        var layer = new DecalLayer { Name = name };
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
}
