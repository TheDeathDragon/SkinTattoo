using System.Collections.Generic;
using System.Numerics;

namespace SkinTatoo.Core;

public class DecalProject
{
    public List<TargetGroup> Groups { get; } = [];
    public int SelectedGroupIndex { get; set; } = -1;

    public TargetGroup? SelectedGroup =>
        SelectedGroupIndex >= 0 && SelectedGroupIndex < Groups.Count
            ? Groups[SelectedGroupIndex] : null;

    public DecalLayer? SelectedLayer => SelectedGroup?.SelectedLayer;

    public TargetGroup AddGroup(string name)
    {
        var group = new TargetGroup { Name = name };
        Groups.Add(group);
        SelectedGroupIndex = Groups.Count - 1;
        return group;
    }

    public void RemoveGroup(int index)
    {
        if (index < 0 || index >= Groups.Count) return;
        Groups.RemoveAt(index);
        if (SelectedGroupIndex >= Groups.Count)
            SelectedGroupIndex = Groups.Count - 1;
    }

    public bool HasEmissiveLayers()
    {
        foreach (var g in Groups)
            if (g.HasEmissiveLayers()) return true;
        return false;
    }

    public void SaveToConfig(Configuration config)
    {
        config.TargetGroups.Clear();
        foreach (var g in Groups)
        {
            var sg = new SavedTargetGroup
            {
                Name = g.Name,
                DiffuseGamePath = g.DiffuseGamePath,
                DiffuseDiskPath = g.DiffuseDiskPath,
                NormGamePath = g.NormGamePath,
                NormDiskPath = g.NormDiskPath,
                MtrlGamePath = g.MtrlGamePath,
                MtrlDiskPath = g.MtrlDiskPath,
                MeshDiskPath = g.MeshDiskPath,
                OrigDiffuseDiskPath = g.OrigDiffuseDiskPath,
                OrigNormDiskPath = g.OrigNormDiskPath,
                OrigMtrlDiskPath = g.OrigMtrlDiskPath,
            };
            foreach (var l in g.Layers)
            {
                sg.Layers.Add(new SavedLayer
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
                    AffectsEmissive = l.AffectsEmissive,
                    EmissiveColorR = l.EmissiveColor.X,
                    EmissiveColorG = l.EmissiveColor.Y,
                    EmissiveColorB = l.EmissiveColor.Z,
                    EmissiveIntensity = l.EmissiveIntensity,
                    EmissiveMask = (int)l.EmissiveMask,
                    EmissiveMaskFalloff = l.EmissiveMaskFalloff,
                    GradientAngleDeg = l.GradientAngleDeg,
                    GradientScale = l.GradientScale,
                    GradientOffset = l.GradientOffset,
                });
            }
            config.TargetGroups.Add(sg);
        }
        config.Save();
    }

    public void LoadFromConfig(Configuration config)
    {
        Groups.Clear();
        foreach (var sg in config.TargetGroups)
        {
            var g = new TargetGroup
            {
                Name = sg.Name,
                DiffuseGamePath = sg.DiffuseGamePath,
                DiffuseDiskPath = sg.DiffuseDiskPath,
                NormGamePath = sg.NormGamePath,
                NormDiskPath = sg.NormDiskPath,
                MtrlGamePath = sg.MtrlGamePath,
                MtrlDiskPath = sg.MtrlDiskPath,
                MeshDiskPath = sg.MeshDiskPath,
                OrigDiffuseDiskPath = sg.OrigDiffuseDiskPath,
                OrigNormDiskPath = sg.OrigNormDiskPath,
                OrigMtrlDiskPath = sg.OrigMtrlDiskPath,
            };
            foreach (var s in sg.Layers)
            {
                g.Layers.Add(new DecalLayer
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
                    AffectsEmissive = s.AffectsEmissive,
                    EmissiveColor = (s.EmissiveColorR == 0 && s.EmissiveColorG == 0 && s.EmissiveColorB == 0 && s.AffectsEmissive)
                        ? new Vector3(1f, 1f, 1f) : new Vector3(s.EmissiveColorR, s.EmissiveColorG, s.EmissiveColorB),
                    EmissiveIntensity = s.EmissiveIntensity > 0 ? s.EmissiveIntensity : 1f,
                    EmissiveMask = (EmissiveMask)s.EmissiveMask,
                    EmissiveMaskFalloff = s.EmissiveMaskFalloff,
                    GradientAngleDeg = s.GradientAngleDeg,
                    GradientScale = s.GradientScale,
                    GradientOffset = s.GradientOffset,
                });
            }
            Groups.Add(g);
        }
        SelectedGroupIndex = Groups.Count > 0 ? 0 : -1;
    }
}
