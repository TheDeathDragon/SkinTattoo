using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using SkinTattoo.Core;
using SkinTattoo.Http;
using SkinTattoo.Interop;

namespace SkinTattoo.Mesh;

/// <summary>
/// Resolves mtrl game path -> set of mdls owning the UV layout for that skin material.
/// See docs/skin-uv-mesh-matching.md for the full reasoning.
/// </summary>
public sealed class SkinMeshResolver
{
    private readonly MeshExtractor meshExtractor;

    public SkinMeshResolver(MeshExtractor meshExtractor)
    {
        this.meshExtractor = meshExtractor;
    }

    /// <summary>
    /// Re-resolve against the current Penumbra resource tree and write fresh mesh slot
    /// state into <paramref name="group"/>. Preserves manually-added MeshDiskPaths that
    /// aren't covered by the resolver result. Returns false if the resolver couldn't
    /// produce a valid resolution (in which case group fields are left untouched).
    /// </summary>
    public bool ReResolveInto(TargetGroup group, PenumbraBridge penumbra)
    {
        if (string.IsNullOrEmpty(group.MtrlGamePath)) return false;

        var trees = penumbra.GetPlayerTrees(withUiData: false);
        if (trees == null) return false;

        var resolution = Resolve(group.MtrlGamePath!, trees);
        if (!resolution.Success) return false;

        var resolvedPaths = new HashSet<string>(
            resolution.MeshSlots.Select(s => s.DiskPath ?? s.GamePath),
            StringComparer.OrdinalIgnoreCase);
        var manualExtras = group.MeshDiskPaths
            .Where(p => !resolvedPaths.Contains(p))
            .ToList();

        group.MeshSlots = resolution.MeshSlots;
        group.LiveTreeHash = resolution.LiveTreeHash;
        group.MeshGamePath = resolution.PrimaryMdlGamePath;
        group.MeshDiskPath = resolution.PrimaryMdlDiskPath;
        group.TargetMatIdx = resolution.MeshSlots[0].MatIdx;
        foreach (var extra in manualExtras)
            if (!group.MeshDiskPaths.Contains(extra))
                group.MeshDiskPaths.Add(extra);

        return true;
    }

    public sealed class Resolution
    {
        public string MtrlGamePath = "";
        public string? PlayerRace;
        public string? SlotKind;
        public string? SlotId;

        public List<MeshSlot> MeshSlots = [];
        public List<string> Diagnostics = [];

        public string? LiveTreeHash;

        public bool Success => MeshSlots.Count > 0;

        public string? PrimaryMdlGamePath => MeshSlots.Count > 0 ? MeshSlots[0].GamePath : null;
        public string? PrimaryMdlDiskPath => MeshSlots.Count > 0 ? MeshSlots[0].DiskPath : null;
    }

    public Resolution Resolve(
        string mtrlGamePath,
        Dictionary<ushort, ResourceTreeDto>? trees)
    {
        var res = new Resolution { MtrlGamePath = mtrlGamePath };

        var parsed = TexPathParser.ParseFromMtrl(mtrlGamePath);
        if (!parsed.IsValid)
        {
            res.Diagnostics.Add($"mtrl path doesn't parse: {mtrlGamePath}");
            return res;
        }
        res.SlotKind = parsed.SlotKind;
        res.SlotId = parsed.SlotId;

        if (trees == null || trees.Count == 0)
        {
            res.Diagnostics.Add("no live trees");
            return res;
        }

        var playerRaceCode = trees.Values.First().RaceCode;
        var playerRace = playerRaceCode.ToString("D4");
        res.PlayerRace = playerRace;

        var allReferers = new List<ResourceNodeDto>();
        foreach (var (_, tree) in trees)
            foreach (var top in tree.Nodes)
                CollectMdlsReferencing(top, null, mtrlGamePath, allReferers);

        if (allReferers.Count == 0)
        {
            res.Diagnostics.Add("no mdl in live tree references this mtrl");
            return res;
        }
        res.Diagnostics.Add($"{allReferers.Count} mdl(s) reference target mtrl");

        // Split body referers into equipment (visible geometry) vs stub (engine internals).
        // Equipment is primary; stubs are fallback when letter-aware matching finds nothing.
        var pattern = BuildCanonicalMdlPattern(playerRace, parsed);
        List<ResourceNodeDto> equipmentReferers;
        List<ResourceNodeDto> stubReferers;
        if (parsed.SlotKind == "body")
        {
            var humanBodyStubRegex = new Regex(
                @"^chara/human/c\d{4}/obj/body/",
                RegexOptions.IgnoreCase);
            equipmentReferers = allReferers
                .Where(m => string.IsNullOrEmpty(m.GamePath) || !humanBodyStubRegex.IsMatch(m.GamePath))
                .ToList();
            stubReferers = allReferers
                .Where(m => !string.IsNullOrEmpty(m.GamePath) && humanBodyStubRegex.IsMatch(m.GamePath))
                .ToList();
            res.Diagnostics.Add($"body slot: {equipmentReferers.Count} equipment, {stubReferers.Count} stub(s) (fallback)");
        }
        else if (pattern != null)
        {
            var pathRegex = new Regex(pattern, RegexOptions.IgnoreCase);
            var filtered = allReferers
                .Where(m => !string.IsNullOrEmpty(m.GamePath) && pathRegex.IsMatch(m.GamePath))
                .ToList();
            equipmentReferers = filtered.Count > 0 ? filtered : allReferers;
            stubReferers = new List<ResourceNodeDto>();
            res.Diagnostics.Add(filtered.Count > 0
                ? $"race filter kept {filtered.Count}/{allReferers.Count} mdl(s)"
                : $"race filter excluded everything -> using all {allReferers.Count} referers");
        }
        else
        {
            equipmentReferers = allReferers;
            stubReferers = new List<ResourceNodeDto>();
            res.Diagnostics.Add("no race pattern for this slot -> using all referers");
        }

        // Face/hair: role-based match (fac/iri/etc/hir). Body/tail: letter-aware exact match
        // with disk->SqPack fallback so vanilla UV layout matches vanilla textures.
        var targetRole = ExtractRoleSuffix(mtrlGamePath);
        var useRoleMatch = (parsed.SlotKind == "face" || parsed.SlotKind == "hair")
                           && targetRole != null;
        var targetNorm = useRoleMatch ? null : NormalizeSkinMtrlName(mtrlGamePath);
        res.Diagnostics.Add(useRoleMatch
            ? $"target role: {targetRole}  (face/hair role-based name match)"
            : $"target normalized: {targetNorm}  (body/tail letter-aware exact match, with SqPack fallback)");

        ResolveAgainst(equipmentReferers, "equipment");

        if (res.MeshSlots.Count == 0 && stubReferers.Count > 0)
        {
            res.Diagnostics.Add($"no equipment mdl matched -> falling back to {stubReferers.Count} body stub(s)");
            ResolveAgainst(stubReferers, "stub");
        }

        res.LiveTreeHash = ComputeMeshSlotsHash(res.MeshSlots);
        return res;

        void ResolveAgainst(List<ResourceNodeDto> referers, string sourceLabel)
        {
            foreach (var mdl in referers)
            {
                var label = mdl.GamePath ?? mdl.ActualPath;

                var diskNames = meshExtractor.ReadMaterialFileNames(
                    mdl.GamePath ?? "",
                    mdl.ActualPath);
                var diskMatched = MatchSlots(diskNames, label, sourceLabel, "disk");
                if (diskMatched.Count > 0)
                {
                    res.MeshSlots.Add(new MeshSlot
                    {
                        GamePath = mdl.GamePath ?? "",
                        DiskPath = mdl.ActualPath,
                        MatIdx = diskMatched.ToArray(),
                    });
                    continue;
                }

                // Fallback: read vanilla SqPack version (DiskPath=null bypasses Penumbra)
                if (string.IsNullOrEmpty(mdl.GamePath))
                {
                    res.Diagnostics.Add($"    no GamePath, can't fall back to SqPack");
                    continue;
                }
                var vanillaNames = meshExtractor.ReadMaterialFileNames(
                    mdl.GamePath, null);
                var vanillaMatched = MatchSlots(vanillaNames, label, sourceLabel, "sqpack-vanilla");
                if (vanillaMatched.Count > 0)
                {
                    res.MeshSlots.Add(new MeshSlot
                    {
                        GamePath = mdl.GamePath,
                        DiskPath = null,
                        MatIdx = vanillaMatched.ToArray(),
                    });
                    continue;
                }

                res.Diagnostics.Add($"    [{sourceLabel}] {label}: no matIdx matched in disk OR vanilla SqPack");
            }
        }

        List<int> MatchSlots(string[]? fileMatNames, string label, string sourceLabel, string fileLabel)
        {
            var matched = new List<int>();
            if (fileMatNames == null)
            {
                res.Diagnostics.Add($"  [{sourceLabel}/{fileLabel}] {label}: ReadMaterialFileNames failed");
                return matched;
            }

            var dump = string.Join(", ",
                fileMatNames.Select((n, i) => $"#{i}={n}"));
            res.Diagnostics.Add($"  [{sourceLabel}/{fileLabel}] {label}: ({fileMatNames.Length}) [{dump}]");

            if (useRoleMatch)
            {
                for (var i = 0; i < fileMatNames.Length; i++)
                {
                    if (ExtractRoleSuffix(fileMatNames[i]) == targetRole)
                    {
                        matched.Add(i);
                        res.Diagnostics.Add($"    matIdx {i} = {fileMatNames[i]} (ok) (role)");
                    }
                }
            }
            else
            {
                for (var i = 0; i < fileMatNames.Length; i++)
                {
                    if (NormalizeSkinMtrlName(fileMatNames[i]) == targetNorm)
                    {
                        matched.Add(i);
                        res.Diagnostics.Add($"    matIdx {i} = {fileMatNames[i]} (ok) (norm)");
                    }
                }
            }
            return matched;
        }
    }

    /// <summary>Normalize skin mtrl filename: strip race/slot digits, keep letter suffix.</summary>
    public static string NormalizeSkinMtrlName(string mtrlPath)
    {
        if (string.IsNullOrEmpty(mtrlPath)) return "";
        var name = System.IO.Path.GetFileName(mtrlPath.TrimStart('/'));
        name = Regex.Replace(name, @"c\d{4}", "c????");
        name = Regex.Replace(name, @"([bfthz])\d{4}", "$1????");
        return name;
    }

    private static string? BuildCanonicalMdlPattern(string playerRace, TexPathParser.Parsed parsed)
    {
        return parsed.SlotKind switch
        {
            "body" => $@"^chara/human/c{playerRace}/obj/body/b\d{{4}}/model/c\d{{4}}b\d{{4}}_top\.mdl$",
            "face" => $@"^chara/human/c{playerRace}/obj/face/f{parsed.SlotId}/model/c\d{{4}}f{parsed.SlotId}_fac\.mdl$",
            "tail" => $@"^chara/human/c{playerRace}/obj/tail/t{parsed.SlotId}/model/c\d{{4}}t{parsed.SlotId}_til\.mdl$",
            "hair" => $@"^chara/human/c{playerRace}/obj/hair/h{parsed.SlotId}/model/c\d{{4}}h{parsed.SlotId}_hir\.mdl$",
            _ => null,
        };
    }

    /// <summary>Collect every Mdl node with a Mtrl child matching targetMtrlGamePath.</summary>
    private static void CollectMdlsReferencing(
        ResourceNodeDto node, ResourceNodeDto? currentMdl,
        string targetMtrlGamePath, List<ResourceNodeDto> sink)
    {
        var thisMdl = node.Type == ResourceType.Mdl ? node : currentMdl;

        if (node.Type == ResourceType.Mtrl
            && string.Equals(node.GamePath, targetMtrlGamePath, System.StringComparison.OrdinalIgnoreCase)
            && thisMdl != null
            && !sink.Contains(thisMdl))
        {
            sink.Add(thisMdl);
        }

        foreach (var child in node.Children)
            CollectMdlsReferencing(child, thisMdl, targetMtrlGamePath, sink);
    }

    /// <summary>Extract role suffix (fac/iri/etc/hir/...) from skin mtrl filename, or null.</summary>
    public static string? ExtractRoleSuffix(string mtrlPath)
    {
        if (string.IsNullOrEmpty(mtrlPath)) return null;
        var name = System.IO.Path.GetFileName(mtrlPath.TrimStart('/'));
        var m = Regex.Match(name,
            @"^mt_c\d{4}[bfthz]\d{4}_(?<role>[a-z]+)_[a-z]\.mtrl$",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["role"].Value.ToLowerInvariant() : null;
    }

    /// <summary>Stable hash over MeshSlots for detecting equipment/mod swaps via polling.</summary>
    public static string ComputeMeshSlotsHash(List<MeshSlot> slots)
    {
        if (slots.Count == 0) return "";
        var parts = slots
            .OrderBy(s => s.GamePath, System.StringComparer.OrdinalIgnoreCase)
            .Select(s => $"{s.GamePath}|{s.DiskPath ?? ""}|{string.Join(",", s.MatIdx)}");
        return string.Join(";", parts);
    }
}
