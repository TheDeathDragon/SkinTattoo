using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            snapshot.TargetGroups.Add(SerializeGroup(g));
        return snapshot;
    }

    public void ApplySnapshot(SavedProjectSnapshot snapshot)
    {
        Groups.Clear();
        foreach (var sg in snapshot.TargetGroups)
        {
            var g = DeserializeGroup(sg, legacyFallback: false);
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

    /// <summary>Fast-path snapshot apply that reuses existing TargetGroup/DecalLayer
    /// instances when the snapshot structure matches. Preserves runtime-only state
    /// (MeshSlots, LiveTreeHash, TargetMatIdx, AllocatedRowPair, HiddenMeshPaths) so
    /// undo of a param edit doesn't trigger a full character redraw.
    /// Returns false when groups/layers were added/removed or a texture/mesh path
    /// changed, in which case the caller must fall back to ApplySnapshot.</summary>
    public bool TryApplySnapshotInPlace(SavedProjectSnapshot snapshot, LibraryService? library)
    {
        if (!SnapshotStructureMatches(snapshot)) return false;

        for (var i = 0; i < Groups.Count; i++)
        {
            var sg = snapshot.TargetGroups[i];
            var g = Groups[i];
            g.Name = sg.Name;
            g.SelectedLayerIndex = sg.SelectedLayerIndex;
            for (var j = 0; j < g.Layers.Count; j++)
                ApplyLayerDto(sg.Layers[j], g.Layers[j], legacyFallback: false);
        }

        SelectedGroupIndex = snapshot.SelectedGroupIndex >= 0 && snapshot.SelectedGroupIndex < Groups.Count
            ? snapshot.SelectedGroupIndex : (Groups.Count > 0 ? 0 : -1);

        if (library != null)
            ReconcileLibraryRefs(library);
        return true;
    }

    private bool SnapshotStructureMatches(SavedProjectSnapshot snapshot)
    {
        if (snapshot.TargetGroups.Count != Groups.Count) return false;
        for (var i = 0; i < Groups.Count; i++)
        {
            var sg = snapshot.TargetGroups[i];
            var g = Groups[i];
            if (sg.Layers.Count != g.Layers.Count) return false;
            if (sg.DiffuseGamePath != g.DiffuseGamePath) return false;
            if (sg.DiffuseDiskPath != g.DiffuseDiskPath) return false;
            if (sg.NormGamePath != g.NormGamePath) return false;
            if (sg.NormDiskPath != g.NormDiskPath) return false;
            if (sg.MtrlGamePath != g.MtrlGamePath) return false;
            if (sg.MtrlDiskPath != g.MtrlDiskPath) return false;
            if (sg.MeshDiskPath != g.MeshDiskPath) return false;
            if (!sg.MeshDiskPaths.SequenceEqual(g.MeshDiskPaths)) return false;
        }
        return true;
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
            config.TargetGroups.Add(SerializeGroup(g));
        config.SelectedGroupIndex = SelectedGroupIndex;
        config.Save();
    }

    public void LoadFromConfig(Configuration config)
    {
        Groups.Clear();
        foreach (var sg in config.TargetGroups)
            Groups.Add(DeserializeGroup(sg, legacyFallback: true));
        SelectedGroupIndex = config.SelectedGroupIndex >= 0 && config.SelectedGroupIndex < Groups.Count
            ? config.SelectedGroupIndex : (Groups.Count > 0 ? 0 : -1);
    }

    private static SavedTargetGroup SerializeGroup(TargetGroup g)
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
            SelectedLayerIndex = g.SelectedLayerIndex,
        };
        foreach (var l in g.Layers)
            sg.Layers.Add(SerializeLayer(l));
        return sg;
    }

    private static TargetGroup DeserializeGroup(SavedTargetGroup sg, bool legacyFallback)
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
            SelectedLayerIndex = sg.SelectedLayerIndex,
        };
        foreach (var s in sg.Layers)
            g.Layers.Add(DeserializeLayer(s, legacyFallback));
        return g;
    }

    private static SavedLayer SerializeLayer(DecalLayer l) => new()
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
        EyeSplitEnabled = l.EyeSplitEnabled,
        EmissiveColorRight_R = l.EmissiveColorRight.X,
        EmissiveColorRight_G = l.EmissiveColorRight.Y,
        EmissiveColorRight_B = l.EmissiveColorRight.Z,
        EmissiveIntensityRight = l.EmissiveIntensityRight,
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
        UseProjector = l.UseProjector,
        ProjOriginX = l.ProjOrigin.X,
        ProjOriginY = l.ProjOrigin.Y,
        ProjOriginZ = l.ProjOrigin.Z,
        ProjNormalX = l.ProjNormal.X,
        ProjNormalY = l.ProjNormal.Y,
        ProjNormalZ = l.ProjNormal.Z,
        ProjTangentX = l.ProjTangent.X,
        ProjTangentY = l.ProjTangent.Y,
        ProjTangentZ = l.ProjTangent.Z,
        ProjSizeX = l.ProjSize.X,
        ProjSizeY = l.ProjSize.Y,
        ProjDepth = l.ProjDepth,
        ProjWrapAngleDeg = l.ProjWrapAngleDeg,
        ProjPaddingRadius = l.ProjPaddingRadius,
    };

    private static DecalLayer DeserializeLayer(SavedLayer s, bool legacyFallback)
    {
        var l = new DecalLayer();
        ApplyLayerDto(s, l, legacyFallback);
        return l;
    }

    // Core layer DTO -> runtime assignment. Shared between DeserializeLayer (fresh
    // instance) and TryApplySnapshotInPlace (reuses existing instance, preserving
    // AllocatedRowPair and other runtime-only fields).
    private static void ApplyLayerDto(SavedLayer s, DecalLayer l, bool legacyFallback)
    {
        // legacyFallback branches protect old on-disk projects where missing fields
        // deserialize to 0. Snapshot replay (in-memory undo) must preserve zeros verbatim.
        var emissiveColor = legacyFallback
            && s.EmissiveColorR == 0 && s.EmissiveColorG == 0 && s.EmissiveColorB == 0 && s.AffectsEmissive
            ? new Vector3(1f, 1f, 1f)
            : new Vector3(s.EmissiveColorR, s.EmissiveColorG, s.EmissiveColorB);
        var emissiveIntensity = legacyFallback && s.EmissiveIntensity <= 0 ? 1f : s.EmissiveIntensity;
        var animSpeed = legacyFallback && s.AnimSpeed <= 0 ? 1f : s.AnimSpeed;
        var animFreq = legacyFallback && s.AnimFreq <= 0 ? 20f : s.AnimFreq;

        l.Name = s.Name;
        l.ImagePath = s.ImagePath;
        l.ImageHash = s.ImageHash;
        l.UvCenter = new Vector2(s.UvCenterX, s.UvCenterY);
        l.UvScale = new Vector2(s.UvScaleX, s.UvScaleY);
        l.RotationDeg = s.RotationDeg;
        l.Opacity = s.Opacity;
        l.BlendMode = (BlendMode)s.BlendMode;
        l.IsVisible = s.IsVisible;
        l.AffectsDiffuse = s.AffectsDiffuse;
        l.AffectsEmissive = s.AffectsEmissive;
        l.EmissiveColor = emissiveColor;
        l.EmissiveIntensity = emissiveIntensity;
        l.AnimMode = (EmissiveAnimMode)s.AnimMode;
        l.AnimSpeed = animSpeed;
        l.AnimAmplitude = s.AnimAmplitude;
        l.EmissiveColorB = new Vector3(s.EmissiveColorB_R, s.EmissiveColorB_G, s.EmissiveColorB_B);
        l.AnimFreq = animFreq;
        l.AnimDirMode = (RippleDirMode)s.AnimDirMode;
        l.AnimDirAngle = s.AnimDirAngle;
        l.AnimDualColor = s.AnimDualColor;
        l.EyeSplitEnabled = s.EyeSplitEnabled;
        l.EmissiveColorRight = new Vector3(s.EmissiveColorRight_R, s.EmissiveColorRight_G, s.EmissiveColorRight_B);
        l.EmissiveIntensityRight = legacyFallback && s.EmissiveIntensityRight <= 0 ? 1f : s.EmissiveIntensityRight;
        l.FadeMask = (LayerFadeMask)s.EmissiveMask;
        l.FadeMaskFalloff = s.EmissiveMaskFalloff;
        l.GradientAngleDeg = s.GradientAngleDeg;
        l.GradientScale = s.GradientScale;
        l.GradientOffset = s.GradientOffset;
        l.Clip = (ClipMode)s.Clip;
        l.Kind = (LayerKind)s.Kind;
        l.TargetMap = (TargetMap)s.TargetMap;
        l.AffectsSpecular = s.AffectsSpecular;
        l.AffectsRoughness = s.AffectsRoughness;
        l.AffectsMetalness = s.AffectsMetalness;
        l.AffectsSheen = s.AffectsSheen;
        l.DiffuseColor = new Vector3(s.DiffuseColorR, s.DiffuseColorG, s.DiffuseColorB);
        l.SpecularColor = new Vector3(s.SpecularColorR, s.SpecularColorG, s.SpecularColorB);
        l.Roughness = s.Roughness;
        l.Metalness = s.Metalness;
        l.SheenRate = s.SheenRate;
        l.SheenTint = s.SheenTint;
        l.SheenAperture = s.SheenAperture;
        l.UseProjector = s.UseProjector;
        l.ProjOrigin = new Vector3(s.ProjOriginX, s.ProjOriginY, s.ProjOriginZ);
        l.ProjNormal = new Vector3(s.ProjNormalX, s.ProjNormalY, s.ProjNormalZ);
        l.ProjTangent = new Vector3(s.ProjTangentX, s.ProjTangentY, s.ProjTangentZ);
        l.ProjSize = new Vector2(s.ProjSizeX, s.ProjSizeY);
        l.ProjDepth = s.ProjDepth;
        // Legacy saves default to 0 (deserialized) but should treat that as the
        // default wrap angle since the field didn't exist when saved.
        l.ProjWrapAngleDeg = s.ProjWrapAngleDeg > 0f ? s.ProjWrapAngleDeg : 78f;
        l.ProjPaddingRadius = Math.Max(0, s.ProjPaddingRadius);
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
