using System.Collections.Generic;
using System.Numerics;

namespace SkinTattoo.Core;

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
                MeshDiskPaths = new List<string>(g.MeshDiskPaths),
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
                    AnimMode = (int)l.AnimMode,
                    AnimSpeed = l.AnimSpeed,
                    AnimAmplitude = l.AnimAmplitude,
                    EmissiveColorB_R = l.EmissiveColorB.X,
                    EmissiveColorB_G = l.EmissiveColorB.Y,
                    EmissiveColorB_B = l.EmissiveColorB.Z,
                    AnimFreq = l.AnimFreq,
                    AnimDirMode = (int)l.AnimDirMode,
                    AnimDirAngle = l.AnimDirAngle,
                    AnimDualColor = l.AnimDualColor,
                    EmissiveMask = (int)l.FadeMask,
                    EmissiveMaskFalloff = l.FadeMaskFalloff,
                    GradientAngleDeg = l.GradientAngleDeg,
                    GradientScale = l.GradientScale,
                    GradientOffset = l.GradientOffset,
                    Clip = (int)l.Clip,
                    Kind = (int)l.Kind,
                    AffectsSpecular = l.AffectsSpecular,
                    AffectsRoughness = l.AffectsRoughness,
                    AffectsMetalness = l.AffectsMetalness,
                    AffectsSheen = l.AffectsSheen,
                    DiffuseColorR = l.DiffuseColor.X,
                    DiffuseColorG = l.DiffuseColor.Y,
                    DiffuseColorB = l.DiffuseColor.Z,
                    SpecularColorR = l.SpecularColor.X,
                    SpecularColorG = l.SpecularColor.Y,
                    SpecularColorB = l.SpecularColor.Z,
                    Roughness = l.Roughness,
                    Metalness = l.Metalness,
                    SheenRate = l.SheenRate,
                    SheenTint = l.SheenTint,
                    SheenAperture = l.SheenAperture,
                });
            }
            sg.SelectedLayerIndex = g.SelectedLayerIndex;
            config.TargetGroups.Add(sg);
        }
        config.SelectedGroupIndex = SelectedGroupIndex;
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
                MeshDiskPaths = new List<string>(sg.MeshDiskPaths),
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
                    AnimMode = (EmissiveAnimMode)s.AnimMode,
                    AnimSpeed = s.AnimSpeed > 0 ? s.AnimSpeed : 1f,
                    AnimAmplitude = s.AnimAmplitude,
                    EmissiveColorB = new Vector3(s.EmissiveColorB_R, s.EmissiveColorB_G, s.EmissiveColorB_B),
                    AnimFreq = s.AnimFreq > 0 ? s.AnimFreq : 20f,
                    AnimDirMode = (RippleDirMode)s.AnimDirMode,
                    AnimDirAngle = s.AnimDirAngle,
                    AnimDualColor = s.AnimDualColor,
                    FadeMask = (LayerFadeMask)s.EmissiveMask,
                    FadeMaskFalloff = s.EmissiveMaskFalloff,
                    GradientAngleDeg = s.GradientAngleDeg,
                    GradientScale = s.GradientScale,
                    GradientOffset = s.GradientOffset,
                    Clip = (ClipMode)s.Clip,
                    Kind = (LayerKind)s.Kind,
                    AffectsSpecular = s.AffectsSpecular,
                    AffectsRoughness = s.AffectsRoughness,
                    AffectsMetalness = s.AffectsMetalness,
                    AffectsSheen = s.AffectsSheen,
                    DiffuseColor = new Vector3(s.DiffuseColorR, s.DiffuseColorG, s.DiffuseColorB),
                    SpecularColor = new Vector3(s.SpecularColorR, s.SpecularColorG, s.SpecularColorB),
                    Roughness = s.Roughness,
                    Metalness = s.Metalness,
                    SheenRate = s.SheenRate,
                    SheenTint = s.SheenTint,
                    SheenAperture = s.SheenAperture,
                });
            }
            g.SelectedLayerIndex = sg.SelectedLayerIndex;
            Groups.Add(g);
        }
        SelectedGroupIndex = config.SelectedGroupIndex >= 0 && config.SelectedGroupIndex < Groups.Count
            ? config.SelectedGroupIndex : (Groups.Count > 0 ? 0 : -1);
    }
}
