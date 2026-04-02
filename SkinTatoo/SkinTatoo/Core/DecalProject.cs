using System.Collections.Generic;
using System.Numerics;

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

    public void SaveToConfig(Configuration config)
    {
        config.Layers.Clear();
        config.LastTarget = Target;
        foreach (var l in Layers)
        {
            config.Layers.Add(new SavedLayer
            {
                Name = l.Name,
                ImagePath = l.ImagePath,
                UvCenterX = l.UvCenter.X,
                UvCenterY = l.UvCenter.Y,
                UvScaleX = l.UvScale.X,
                UvScaleY = l.UvScale.Y,
                RotationDeg = l.RotationDeg,
                Opacity = l.Opacity,
                BlendMode = (int)l.BlendMode,
                IsVisible = l.IsVisible,
                AffectsDiffuse = l.AffectsDiffuse,
                AffectsMask = l.AffectsMask,
                GlowSpecular = l.GlowSpecular,
                GlowSmoothness = l.GlowSmoothness,
            });
        }
        config.Save();
    }

    public void LoadFromConfig(Configuration config)
    {
        Layers.Clear();
        Target = config.LastTarget;
        foreach (var s in config.Layers)
        {
            Layers.Add(new DecalLayer
            {
                Name = s.Name,
                ImagePath = s.ImagePath,
                UvCenter = new Vector2(s.UvCenterX, s.UvCenterY),
                UvScale = new Vector2(s.UvScaleX, s.UvScaleY),
                RotationDeg = s.RotationDeg,
                Opacity = s.Opacity,
                BlendMode = (BlendMode)s.BlendMode,
                IsVisible = s.IsVisible,
                AffectsDiffuse = s.AffectsDiffuse,
                AffectsMask = s.AffectsMask,
                GlowSpecular = s.GlowSpecular,
                GlowSmoothness = s.GlowSmoothness,
            });
        }
        SelectedLayerIndex = Layers.Count > 0 ? 0 : -1;
    }
}
