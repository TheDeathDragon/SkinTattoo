using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using SkinTatoo.Core;
using SkinTatoo.Http;

namespace SkinTatoo.Mesh;

/// <summary>
/// Given a target skin mtrl game path (e.g. mt_c1401b0001_a.mtrl) and the
/// player's live Penumbra resource trees, resolve the set of mdls that own
/// the UV layout for that skin material, plus per-mdl matIdx + the physical
/// file (Penumbra-redirected disk vs vanilla SqPack) to load it from.
///
/// See docs/皮肤UV网格匹配调研.md for the full reasoning. Short version:
///
/// 1. Collect every Mdl in the live tree that has the target mtrl as a Mtrl
///    child (post-redirect GamePath comparison).
/// 2. For body, split referers into "equipment" (visible body geometry) and
///    "stub" (chara/human/.../body/... 24-vert engine internals). Equipment
///    is the primary set; stubs are kept as a fallback.
/// 3. For each equipment referer, run two letter-aware match attempts:
///       (a) Read mat slots from the disk file Penumbra resolved (could be
///           a body-mod redirect like Eve's body-milky.mdl). If a slot's
///           normalized name equals the target's, use that file + matIdx.
///       (b) If no slot matches, re-read the same gamepath from SqPack with
///           no disk override — that gets the vanilla, unmodded version.
///           If a vanilla slot matches, store MeshSlot with DiskPath=null
///           so MeshExtractor later loads vanilla geometry too. This is the
///           "vanilla skin texture under a body mod" case (Eve gen3 covers
///           the visible body, but vanilla `_a.mtrl` should still resolve
///           to the vanilla equipment mdls so its texture lines up with
///           the vanilla UV layout).
/// 4. If equipment matched nothing at all, fall back to body stubs.
/// 5. Face / hair use a separate role-based matching path so painting one
///    fac_a tex lights up fac_b/c sibling slots too (Au Ra horns, lips).
/// </summary>
public sealed class SkinMeshResolver
{
    private readonly MeshExtractor meshExtractor;

    public SkinMeshResolver(MeshExtractor meshExtractor)
    {
        this.meshExtractor = meshExtractor;
    }

    public sealed class Resolution
    {
        public string MtrlGamePath = "";
        public string? PlayerRace;
        public string? SlotKind;
        public string? SlotId;

        public List<MeshSlot> MeshSlots = [];
        public List<string>   Diagnostics = [];

        /// <summary>
        /// Stable hash over the resolved MeshSlots, used by the editor's
        /// 1Hz polling to detect equipment / body-mod swaps. Computed at
        /// the end of Resolve(); null if Resolve was never called.
        /// </summary>
        public string? LiveTreeHash;

        public bool Success => MeshSlots.Count > 0;

        // Convenience accessors for the diagnostic UI which still wants to
        // show "the primary mdl" when there's only one.
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
        res.SlotId   = parsed.SlotId;

        if (trees == null || trees.Count == 0)
        {
            res.Diagnostics.Add("no live trees");
            return res;
        }

        var playerRaceCode = trees.Values.First().RaceCode;
        var playerRace = playerRaceCode.ToString("D4");
        res.PlayerRace = playerRace;

        // Step 1: collect every mdl in the tree that has our target mtrl as a child.
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

        // Step 2: split referers into "equipment" (the visible body
        // geometry — smallclothes / actual gear / mod-injected body mdls)
        // and "stub" (the chara/human/cXXXX/obj/body/... mdls, which are
        // tiny 24-vert engine internal racial-deformer dummies, NOT visible
        // body parts).
        //
        // Equipment is the primary source. Stubs are kept as a *fallback*
        // for textures whose canonical skin mtrl exists in the live tree
        // but is shadowed by another variant on the visible body — e.g.,
        // when Eve is loaded, vanilla_raen_base.tex is parented by the
        // canonical `_a.mtrl`, but Eve's equipment mdls only reference
        // `_b.mtrl`. Letter-aware matching (Step 3) will find no `_a` slots
        // in the equipment mdls; we then fall through to the stubs, which
        // DO reference `_a.mtrl`, so the user gets a non-empty (if tiny)
        // mesh that signals "this texture isn't on your visible body".
        //
        // For face / tail / hair: only race filter, no stub vs equipment
        // distinction (the live tree only contains one mdl per slot).
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
                : $"race filter excluded everything → using all {allReferers.Count} referers");
        }
        else
        {
            equipmentReferers = allReferers;
            stubReferers = new List<ResourceNodeDto>();
            res.Diagnostics.Add("no race pattern for this slot → using all referers");
        }

        // Step 3: per-mdl matIdx resolution.
        //
        // Matching strategy depends on the slot:
        //
        // - face / hair: match by role suffix (fac / iri / etc / hir / ...).
        //   The face mdl groups its 7 mat slots into 3 roles (fac_a/b/c,
        //   iri_a, etc_a/b/c) and within a role they all share the same
        //   diffuse/normal/mask textures. So painting on the face_base.tex
        //   affects all three fac_a/b/c mesh groups (e.g. main skin face_a +
        //   Au Ra horns face_b + lips face_c).
        //
        // - body / tail: letter-AWARE normalized exact match (so `_a` only
        //   matches `_a` slots, not `_b`). For each referer we make TWO
        //   matching attempts:
        //
        //     (a) Read mat slots from the disk file Penumbra gave us
        //         (mdl.ActualPath — could be a mod-redirected file like
        //         Eve's body-milky.mdl). If a slot matches the target's
        //         letter, use that file + matIdx. This is the gen3 case:
        //         target `_b.mtrl` finds Eve's `_b` slot in the redirected
        //         disk file.
        //
        //     (b) If the redirected disk file has no slot matching the
        //         target letter, fall back to reading the *vanilla* version
        //         of the same gamepath via SqPack (passing null diskPath
        //         bypasses Penumbra's redirect). The vanilla file has the
        //         canonical `_a` slot. We then store the MeshSlot with
        //         DiskPath=null, which makes ExtractMesh load the vanilla
        //         geometry too — important because the vanilla mdl's UV
        //         layout matches the vanilla `_a.mtrl` texture, while Eve's
        //         gen3 mdl has a different UV.
        //
        //   This way, vanilla `_a.mtrl` and gen3 `_b.mtrl` both resolve to
        //   the same 4 equipment gamepaths under Eve, but each loads its
        //   own physical mdl file (vanilla SqPack vs Eve disk) so the UV
        //   the user paints on always matches the texture's UV layout.
        var targetRole   = ExtractRoleSuffix(mtrlGamePath);
        var useRoleMatch = (parsed.SlotKind == "face" || parsed.SlotKind == "hair")
                           && targetRole != null;
        var targetNorm   = useRoleMatch ? null : NormalizeSkinMtrlName(mtrlGamePath);
        res.Diagnostics.Add(useRoleMatch
            ? $"target role: {targetRole}  (face/hair role-based name match)"
            : $"target normalized: {targetNorm}  (body/tail letter-aware exact match, with SqPack fallback)");

        ResolveAgainst(equipmentReferers, "equipment");

        if (res.MeshSlots.Count == 0 && stubReferers.Count > 0)
        {
            res.Diagnostics.Add($"no equipment mdl matched → falling back to {stubReferers.Count} body stub(s)");
            ResolveAgainst(stubReferers, "stub");
        }

        res.LiveTreeHash = ComputeMeshSlotsHash(res.MeshSlots);
        DebugServer.AppendLog($"[SkinMeshResolver] {mtrlGamePath} → {res.MeshSlots.Count} slot(s)");
        return res;

        void ResolveAgainst(List<ResourceNodeDto> referers, string sourceLabel)
        {
            foreach (var mdl in referers)
            {
                var label = mdl.GamePath ?? mdl.ActualPath;

                // Attempt (a): read the actual file Penumbra resolved
                // (could be a mod redirect). The matched slots refer to
                // mat indices in *that* physical file.
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
                        MatIdx   = diskMatched.ToArray(),
                    });
                    continue;
                }

                // Attempt (b): the disk file has no slot of the target's
                // letter. Re-read the same gamepath from SqPack with no
                // disk override — that gets the vanilla, unmodded version,
                // whose mat slots are the canonical layout. If a vanilla
                // slot matches, store the MeshSlot with DiskPath=null so
                // ExtractMesh later also reads the vanilla geometry.
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
                        MatIdx   = vanillaMatched.ToArray(),
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
                        res.Diagnostics.Add($"    matIdx {i} = {fileMatNames[i]} ✓ (role)");
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
                        res.Diagnostics.Add($"    matIdx {i} = {fileMatNames[i]} ✓ (norm)");
                    }
                }
            }
            return matched;
        }
    }

    /// <summary>
    /// Normalize a skin mtrl filename so two paths the engine considers the
    /// same logical material hash to the same string. Strips race code and
    /// slot id digits, KEEPS the letter suffix verbatim — `_a` and `_b`
    /// must remain distinct so vanilla and gen3 skin variants don't collapse.
    ///
    ///   /mt_c0201b0001_a.mtrl       → mt_c????b????_a.mtrl
    ///   mt_c1401b0001_a.mtrl        → mt_c????b????_a.mtrl
    ///   mt_c0201b0001_b.mtrl        → mt_c????b????_b.mtrl
    ///   mt_c0201b0001_eve-pierc.mtrl → mt_c????b????_eve-pierc.mtrl
    ///   mt_c1401f0001_iri_a.mtrl    → mt_c????f????_iri_a.mtrl
    /// </summary>
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

    /// <summary>
    /// Walk a node + descendants and append every Mdl node that has a Mtrl
    /// child whose GamePath equals the target.
    /// </summary>
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

    /// <summary>
    /// Extract the role suffix from a skin mtrl filename. Returns
    /// "fac"/"iri"/"etc"/"hir"/"til"/"top"/"zer" if the filename has the
    /// form mt_cXXXXyYYYY_{role}_{a|b|c}.mtrl, otherwise null (e.g. body
    /// skin mtrls like mt_c1401b0001_a.mtrl have no role segment).
    /// </summary>
    public static string? ExtractRoleSuffix(string mtrlPath)
    {
        if (string.IsNullOrEmpty(mtrlPath)) return null;
        var name = System.IO.Path.GetFileName(mtrlPath.TrimStart('/'));
        var m = Regex.Match(name,
            @"^mt_c\d{4}[bfthz]\d{4}_(?<role>[a-z]+)_[a-z]\.mtrl$",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["role"].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Stable hash over a resolved MeshSlot list. Used by the editor's 1Hz
    /// polling to detect equipment / mod swaps: the player's live tree changes
    /// when they put on/take off gear or toggle a body mod, which makes the
    /// resolver pick a different set of (gamePath, diskPath, matIdx) — this
    /// hash captures exactly that. Sorted by GamePath so insertion order
    /// doesn't matter.
    /// </summary>
    public static string ComputeMeshSlotsHash(List<MeshSlot> slots)
    {
        if (slots.Count == 0) return "";
        var parts = slots
            .OrderBy(s => s.GamePath, System.StringComparer.OrdinalIgnoreCase)
            .Select(s => $"{s.GamePath}|{s.DiskPath ?? ""}|{string.Join(",", s.MatIdx)}");
        return string.Join(";", parts);
    }
}
