using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using SkinTattoo.Services;

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

    public SavedProjectSnapshot CreateSnapshot()
    {
        var snapshot = new SavedProjectSnapshot
        {
            SelectedGroupIndex = SelectedGroupIndex,
        };

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
                    ImageHash = l.ImageHash,
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
                    TargetMap = (int)l.TargetMap,
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
            snapshot.TargetGroups.Add(sg);
        }

        return snapshot;
    }

    public void ApplySnapshot(SavedProjectSnapshot snapshot)
    {
        Groups.Clear();
        foreach (var sg in snapshot.TargetGroups)
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
                    ImageHash = s.ImageHash,
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
                    TargetMap = (TargetMap)s.TargetMap,
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
            RepairDiskPaths(g);
            Groups.Add(g);
        }

        SelectedGroupIndex = snapshot.SelectedGroupIndex >= 0 && snapshot.SelectedGroupIndex < Groups.Count
            ? snapshot.SelectedGroupIndex : (Groups.Count > 0 ? 0 : -1);
    }

    public void ApplySnapshot(SavedProjectSnapshot snapshot, LibraryService? library)
    {
        ApplySnapshot(snapshot);
        if (library != null)
            ReconcileLibraryRefs(library);
    }

    private static void RepairDiskPaths(TargetGroup group)
    {
        static string? Repair(string? path, string? original)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return path;
            if (!string.IsNullOrEmpty(original) && File.Exists(original))
                return original;
            return path;
        }

        group.DiffuseDiskPath = Repair(group.DiffuseDiskPath, group.OrigDiffuseDiskPath);
        group.NormDiskPath = Repair(group.NormDiskPath, group.OrigNormDiskPath);
        group.MtrlDiskPath = Repair(group.MtrlDiskPath, group.OrigMtrlDiskPath);
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
                    ImageHash = l.ImageHash,
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
                    TargetMap = (int)l.TargetMap,
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
                    ImageHash = s.ImageHash,
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
                    TargetMap = (TargetMap)s.TargetMap,
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

    /// <summary>After LoadFromConfig: resolve each layer's ImageHash to a library
    /// disk path, overwriting stale ImagePath. Also backfills ImageHash for old
    /// projects whose on-disk source still exists, so future saves become portable.</summary>
    public void ReconcileLibraryRefs(LibraryService library)
    {
        foreach (var g in Groups)
        foreach (var l in g.Layers)
        {
            if (!string.IsNullOrEmpty(l.ImageHash))
            {
                var resolved = library.ResolveDiskPath(l.ImageHash);
                if (resolved != null)
                {
                    l.ImagePath = resolved;
                    continue;
                }
                // Hash was set but library no longer has it: the blob the layer pointed
                // at is gone for good. Clear ImagePath too so the layer reverts to "no
                // image" instead of dangling and spamming load errors every frame.
                l.ImageHash = null;
                l.ImagePath = null;
                continue;
            }
            if (!string.IsNullOrEmpty(l.ImagePath))
            {
                if (File.Exists(l.ImagePath))
                {
                    var entry = library.ImportFromPath(l.ImagePath);
                    if (entry != null)
                    {
                        l.ImageHash = entry.Hash;
                        l.ImagePath = library.ResolveDiskPath(entry.Hash) ?? l.ImagePath;
                    }
                }
                else
                {
                    l.ImagePath = null;
                }
            }
        }
    }
}
