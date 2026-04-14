using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using SkinTattoo.Core;
using SkinTattoo.Http;
using SkinTattoo.Services.Localization;

namespace SkinTattoo.Gui;

public partial class MainWindow
{
    // ── Card data for deduplicated material display ──────────────────────────

    private sealed class MtrlCardInfo
    {
        public string Name = "";
        public string ShaderName = "";
        public string DedupKey = "";

        public string DiffuseGamePath = "";
        public string DiffuseActualPath = "";
        public string NormGamePath = "";
        public string NormActualPath = "";
        public string MaskGamePath = "";
        public string MaskActualPath = "";

        public List<string> Models = new();
        public List<string> MtrlPaths = new();

        // Live mdl nodes from the resource tree (current render state).
        // Each entry: (gamePath, actualPath)  -- actualPath may be a disk path
        // when Penumbra has redirected the model.
        public List<(string GamePath, string ActualPath)> LiveMdls = new();

        public ResourceNodeDto? FirstMtrlNode;
        public ResourceNodeDto? FirstParentMdl;
    }

    // ── Resource Browser Window ──────────────────────────────────────────────

    private void DrawResourceWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(680, 600), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(Strings.T("window.resource.title") + "###SkinTattooResources", ref resourceWindowOpen))
        {
            ImGui.End();
            return;
        }

        if (ImGui.Button(Strings.T("button.refresh_resources")))
            RefreshResources();
        ImGui.SameLine();
        if (ImGui.Button(Strings.T("button.export_diagnostics")))
            ExportAllDiagnostics();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Dump complete diagnostics for all skin/iris cards (SqPack existence / Penumbra redirect) as markdown to clipboard");
        ImGui.SameLine();
        ImGui.TextDisabled(penumbra.IsAvailable ? Strings.T("label.penumbra_connected") : Strings.T("label.penumbra_disconnected"));

        if (!penumbra.IsAvailable)
        {
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), Strings.T("label.penumbra_disconnected"));
            ImGui.End();
            return;
        }

        if (cachedTrees == null || cachedTrees.Count == 0)
        {
            ImGui.TextDisabled(Strings.T("hint.no_resources"));
            ImGui.End();
            return;
        }

        var cards = BuildMtrlCards();

        ImGui.Separator();

        using var scroll = ImRaii.Child("##CardScroll", new Vector2(-1, -1), false);
        if (!scroll.Success) { ImGui.End(); return; }

        for (var i = 0; i < cards.Count; i++)
        {
            ImGui.PushID(i);
            DrawMtrlCard(cards[i]);
            ImGui.PopID();
            if (i < cards.Count - 1) ImGui.Separator();
        }

        ImGui.End();
    }

    // ── Card building ────────────────────────────────────────────────────────

    private List<MtrlCardInfo> BuildMtrlCards()
    {
        var cards = new Dictionary<string, MtrlCardInfo>();
        foreach (var (_, tree) in cachedTrees!)
            foreach (var topNode in tree.Nodes)
                ScanForCards(topNode, null, cards);

        return cards.Values
            .Where(c => c.ShaderName is "skin" or "iris")
            .Where(c => !IsUnusedBodyFallback(c))
            .ToList();
    }

    /// <summary>
    /// Body skin materials must be referenced by at least one equipment model.
    /// If only referenced by stub/fallback body models, the material belongs to
    /// a different subrace variant (e.g. Raen material on a Xaela character)
    /// and should be hidden from the card list.
    /// </summary>
    private static bool IsUnusedBodyFallback(MtrlCardInfo card)
    {
        if (!card.MtrlPaths.Any(p => p.Contains("/obj/body/")))
            return false;
        return !card.LiveMdls.Any(m =>
            m.GamePath.StartsWith("chara/equipment/", StringComparison.OrdinalIgnoreCase));
    }

    private static void ScanForCards(
        ResourceNodeDto node, ResourceNodeDto? parentMdl, Dictionary<string, MtrlCardInfo> cards)
    {
        var mdl = node.Type == ResourceType.Mdl ? node : parentMdl;

        if (node.Type == ResourceType.Mtrl)
        {
            var diffuse = FindDescendant(node, IsDiffuseTexNode);
            if (diffuse?.GamePath != null)
            {
                var key = diffuse.GamePath;
                if (!cards.TryGetValue(key, out var card))
                {
                    card = new MtrlCardInfo { DedupKey = key };
                    cards[key] = card;

                    card.Name = node.Name ?? Path.GetFileName(node.GamePath ?? node.ActualPath);
                    card.FirstMtrlNode = node;
                    card.FirstParentMdl = mdl;

                    var shpk = FindDescendant(node, n => n.Type == ResourceType.Shpk);
                    card.ShaderName = shpk?.GamePath != null
                        ? Path.GetFileNameWithoutExtension(shpk.GamePath) : "";

                    card.DiffuseGamePath = diffuse.GamePath;
                    card.DiffuseActualPath = diffuse.ActualPath ?? "";

                    var norm = FindDescendant(node, n =>
                        n.Type == ResourceType.Tex && n.Name == "g_SamplerNormal");
                    if (norm != null)
                    {
                        card.NormGamePath = norm.GamePath ?? "";
                        card.NormActualPath = norm.ActualPath ?? "";
                    }

                    var mask = FindDescendant(node, n =>
                        n.Type == ResourceType.Tex && n.Name == "g_SamplerMask");
                    if (mask != null)
                    {
                        card.MaskGamePath = mask.GamePath ?? "";
                        card.MaskActualPath = mask.ActualPath ?? "";
                    }
                }

                if (mdl != null)
                {
                    var label = mdl.Name ?? Path.GetFileName(mdl.GamePath ?? mdl.ActualPath);
                    if (!card.Models.Contains(label))
                        card.Models.Add(label);

                    var mdlGp = mdl.GamePath ?? "";
                    var mdlAp = mdl.ActualPath ?? "";
                    if (!card.LiveMdls.Any(m => m.GamePath == mdlGp && m.ActualPath == mdlAp))
                        card.LiveMdls.Add((mdlGp, mdlAp));
                }

                var mtrlPath = node.GamePath ?? node.ActualPath;
                if (!string.IsNullOrEmpty(mtrlPath) && !card.MtrlPaths.Contains(mtrlPath))
                    card.MtrlPaths.Add(mtrlPath);
            }
        }

        foreach (var child in node.Children)
            ScanForCards(child, mdl, cards);
    }

    // ── Card rendering ───────────────────────────────────────────────────────

    private void DrawMtrlCard(MtrlCardInfo card)
    {
        // Header: [Add] button + name + model/mtrl info
        var addedGroup = project.Groups.FirstOrDefault(g => g.DiffuseGamePath == card.DiffuseGamePath);
        if (addedGroup != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 1f, 0.3f, 1f));
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.Text(FontAwesomeIcon.Check.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.Text(Strings.T("diag.add_already", addedGroup.Name));
            ImGui.PopStyleColor();
        }
        else
        {
            if (ImGui.Button(Strings.T("button.add")))
                AddTargetGroupFromMtrl(card.FirstMtrlNode!, card.FirstParentMdl, card.LiveMdls);
        }

        ImGui.SameLine();
        ImGui.Text(card.Name);

        // Model list
        if (card.Models.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"({string.Join(", ", card.Models)})");
        }

        // Merged mtrl tooltip
        if (card.MtrlPaths.Count > 1)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(Strings.T("diag.merged_mtrl", card.MtrlPaths.Count));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", card.MtrlPaths.Select(p => "• " + Path.GetFileName(p))));
        }

        // Diagnostics popup: copy tex / mtrl / live mdl / derived candidate mdl
        ImGui.SameLine();
        if (ImGui.SmallButton(Strings.T("button.path")))
            ImGui.OpenPopup("##PathDiag");
        DrawPathDiagPopup(card);

        // Texture previews: Diffuse | Normal | Mask
        var texSize = new Vector2(160, 160);
        DrawTexPreview("Diffuse", card.DiffuseGamePath, card.DiffuseActualPath, texSize);
        ImGui.SameLine();
        DrawTexPreview("Normal", card.NormGamePath, card.NormActualPath, texSize);
        ImGui.SameLine();
        DrawTexPreview("Mask", card.MaskGamePath, card.MaskActualPath, texSize);
    }

    // Cache the resolver result per-card so the popup doesn't re-run the
    // resolver (which reads .mdl files from disk) every frame.
    private readonly Dictionary<string, Mesh.SkinMeshResolver.Resolution> diagResolutionCache = new();

    private Mesh.SkinMeshResolver.Resolution? GetCachedResolution(string mtrlGamePath)
    {
        if (string.IsNullOrEmpty(mtrlGamePath)) return null;
        if (!diagResolutionCache.TryGetValue(mtrlGamePath, out var cached))
        {
            cached = skinMeshResolver.Resolve(mtrlGamePath, cachedTrees);
            diagResolutionCache[mtrlGamePath] = cached;
        }
        return cached;
    }

    private void DrawPathDiagPopup(MtrlCardInfo card)
    {
        if (!ImGui.BeginPopup("##PathDiag")) return;

        var firstMtrl = card.MtrlPaths.Count > 0 ? card.MtrlPaths[0] : null;
        var derived = Core.TexPathParser.ParseBest(card.DiffuseGamePath, firstMtrl);

        ImGui.TextDisabled(Strings.T("label.tex_diffuse_header"));
        DrawCopyRow(Strings.T("label.game_path"), card.DiffuseGamePath);
        if (!string.IsNullOrEmpty(card.DiffuseActualPath))
            DrawCopyRow(Strings.T("label.actual_path"), card.DiffuseActualPath);

        if (!string.IsNullOrEmpty(card.NormGamePath))
        {
            ImGui.Separator();
            ImGui.TextDisabled(Strings.T("label.tex_normal_header"));
            DrawCopyRow(Strings.T("label.game_path"), card.NormGamePath);
            if (!string.IsNullOrEmpty(card.NormActualPath))
                DrawCopyRow(Strings.T("label.actual_path"), card.NormActualPath);
        }

        if (card.MtrlPaths.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextDisabled(Strings.T("diag.mtrl_count", card.MtrlPaths.Count));
            foreach (var mp in card.MtrlPaths)
                DrawCopyRow("mtrl", mp);
        }

        ImGui.Separator();
        ImGui.TextDisabled(Strings.T("diag.live_mdl_count", card.LiveMdls.Count));
        if (card.LiveMdls.Count == 0)
        {
            ImGui.TextColored(new Vector4(1, 0.6f, 0.3f, 1), Strings.T("diag.no_live_mdl"));
        }
        else
        {
            foreach (var (gp, ap) in card.LiveMdls)
            {
                if (!string.IsNullOrEmpty(gp))
                    DrawCopyRow(Strings.T("label.game_path"), gp);
                if (!string.IsNullOrEmpty(ap) && ap != gp)
                    DrawCopyRow(Strings.T("label.actual_path"), ap);
            }
        }

        ImGui.Separator();
        ImGui.TextDisabled(Strings.T("diag.resolver_header"));
        if (derived.IsSharedIris)
        {
            ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1),
                Strings.T("diag.shared_iris"));
        }
        else if (!derived.IsValid)
        {
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1),
                Strings.T("diag.non_standard_path"));
        }
        else
        {
            ImGui.TextDisabled(
                $"race={derived.Race} slot={derived.SlotKind}/{derived.SlotAbbr}{derived.SlotId}" +
                (derived.BodySlotIdIsRewritten ? Strings.T("diag.body_id_rewritten") : ""));

            var resolution = firstMtrl != null ? GetCachedResolution(firstMtrl) : null;
            if (resolution != null && resolution.Success)
            {
                ImGui.TextDisabled(Strings.T("diag.slot_count", resolution.MeshSlots.Count));
                for (var i = 0; i < resolution.MeshSlots.Count; i++)
                {
                    var slot = resolution.MeshSlots[i];
                    DrawCopyRow($"mdl{i}", slot.GamePath);
                    if (!string.IsNullOrEmpty(slot.DiskPath) && slot.DiskPath != slot.GamePath)
                        DrawCopyRow("    disk", slot.DiskPath);
                    ImGui.TextDisabled($"    matIdx = [{string.Join(",", slot.MatIdx)}]");
                }
            }
            else if (resolution != null)
            {
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), Strings.T("diag.resolver_failed"));
                foreach (var diag in resolution.Diagnostics)
                    ImGui.TextDisabled("  " + diag);
            }
        }

        ImGui.Separator();
        if (ImGui.MenuItem(Strings.T("label.copy_clipboard")))
            ImGui.SetClipboardText(BuildCardMarkdown(card));

        ImGui.EndPopup();
    }

    private string BuildCardMarkdown(MtrlCardInfo card)
    {
        var firstMtrl = card.MtrlPaths.Count > 0 ? card.MtrlPaths[0] : null;
        var derived = Core.TexPathParser.ParseBest(card.DiffuseGamePath, firstMtrl);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {card.Name}");
        sb.AppendLine($"- diffuse game: `{card.DiffuseGamePath}`");
        if (!string.IsNullOrEmpty(card.DiffuseActualPath))
            sb.AppendLine($"- diffuse disk: `{card.DiffuseActualPath}`");
        if (!string.IsNullOrEmpty(card.NormGamePath))
            sb.AppendLine($"- normal game: `{card.NormGamePath}`");
        if (!string.IsNullOrEmpty(card.NormActualPath))
            sb.AppendLine($"- normal disk: `{card.NormActualPath}`");
        foreach (var mp in card.MtrlPaths)
            sb.AppendLine($"- mtrl: `{mp}`");

        sb.AppendLine($"- live mdls in tree: {card.LiveMdls.Count}");
        foreach (var (gp, ap) in card.LiveMdls)
        {
            if (!string.IsNullOrEmpty(gp)) sb.AppendLine($"  - game: `{gp}`");
            if (!string.IsNullOrEmpty(ap) && ap != gp) sb.AppendLine($"  - disk: `{ap}`");
        }

        sb.AppendLine($"- derived: race={derived.Race} slot={derived.SlotKind} id={derived.SlotId} role={derived.RoleSuffix ?? "?"} sharedIris={derived.IsSharedIris} bRewritten={derived.BodySlotIdIsRewritten}");

        if (firstMtrl != null)
        {
            var resolution = skinMeshResolver.Resolve(firstMtrl, cachedTrees);
            sb.AppendLine($"- resolver: success={resolution.Success} slots={resolution.MeshSlots.Count}");
            for (var i = 0; i < resolution.MeshSlots.Count; i++)
            {
                var slot = resolution.MeshSlots[i];
                sb.AppendLine($"  - slot {i} game: `{slot.GamePath}`");
                if (!string.IsNullOrEmpty(slot.DiskPath) && slot.DiskPath != slot.GamePath)
                    sb.AppendLine($"  - slot {i} disk: `{slot.DiskPath}`");
                sb.AppendLine($"  - slot {i} matIdx: [{string.Join(",", slot.MatIdx)}]");
            }
            foreach (var diag in resolution.Diagnostics)
                sb.AppendLine($"  - {diag}");
        }

        return sb.ToString();
    }

    private bool SafeFileExists(string gamePath)
    {
        try { return dataManager.FileExists(gamePath); }
        catch { return false; }
    }

    private void ExportAllDiagnostics()
    {
        if (cachedTrees == null || cachedTrees.Count == 0)
            RefreshResources();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# SkinTattoo diagnostic dump @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- penumbra: {(penumbra.IsAvailable ? "ok" : "unavailable")}");
        sb.AppendLine($"- trees: {cachedTrees?.Count ?? 0}");
        sb.AppendLine();

        var cards = BuildMtrlCards();
        sb.AppendLine($"# Cards ({cards.Count})");
        sb.AppendLine();
        foreach (var card in cards)
        {
            sb.Append(BuildCardMarkdown(card));
            sb.AppendLine();
        }

        // Probe shared paths that may not have a card (eye10 etc.)
        sb.AppendLine("# Shared path probe");
        string[] probes =
        [
            "chara/common/texture/eye/eye10_base.tex",
            "chara/common/texture/eye/eye10_norm.tex",
        ];
        foreach (var probe in probes)
        {
            sb.AppendLine($"- `{probe}`");
            sb.AppendLine($"    - existsInGameData: {SafeFileExists(probe)}");
            var resolved = SafeResolvePlayer(probe);
            if (!string.IsNullOrEmpty(resolved) && resolved != probe)
                sb.AppendLine($"    - penumbra -> `{resolved}`");
            else
                sb.AppendLine($"    - penumbra: vanilla");
        }

        // Raw resource trees: dump every Mdl/Mtrl/Tex node so we can spot the
        // mesh that owns the body skin. Limited to mdl/mtrl/tex to keep size sane.
        sb.AppendLine();
        sb.AppendLine("# Raw resource tree dump (mdl/mtrl/tex)");
        if (cachedTrees != null)
        {
            foreach (var (treeId, tree) in cachedTrees)
            {
                sb.AppendLine($"## tree {treeId}: {tree.Name}");
                foreach (var top in tree.Nodes)
                    DumpNode(top, sb, 0);
            }
        }

        ImGui.SetClipboardText(sb.ToString());
    }

    private static void DumpNode(ResourceNodeDto node, System.Text.StringBuilder sb, int depth)
    {
        var t = node.Type;
        var keep = t == Penumbra.Api.Enums.ResourceType.Mdl
                || t == Penumbra.Api.Enums.ResourceType.Mtrl
                || t == Penumbra.Api.Enums.ResourceType.Tex;
        if (keep)
        {
            var pad = new string(' ', depth * 2);
            var line = $"{pad}- {t} `{node.GamePath}`";
            if (!string.IsNullOrEmpty(node.ActualPath) && node.ActualPath != node.GamePath)
                line += $" -> `{node.ActualPath}`";
            sb.AppendLine(line);
        }
        foreach (var c in node.Children)
            DumpNode(c, sb, keep ? depth + 1 : depth);
    }

    private string? SafeResolvePlayer(string gamePath)
    {
        try { return penumbra.ResolvePlayer(gamePath); }
        catch { return null; }
    }

    private static void DrawCopyRow(string label, string value)
    {
        ImGui.PushID(label + "::" + value);
        if (ImGui.SmallButton(Strings.T("button.copy")))
            ImGui.SetClipboardText(value);
        ImGui.SameLine();
        ImGui.TextDisabled(label);
        ImGui.SameLine();
        ImGui.TextWrapped(value);
        ImGui.PopID();
    }

    private void DrawTexPreview(string label, string gamePath, string actualPath, Vector2 size)
    {
        ImGui.BeginGroup();
        ImGui.TextDisabled(label);

        var hasPath = !string.IsNullOrEmpty(gamePath);
        var rendered = false;
        if (hasPath)
        {
            try
            {
                var loadPath = !string.IsNullOrEmpty(actualPath) && Path.IsPathRooted(actualPath)
                    ? actualPath : gamePath;
                var normalized = loadPath.Replace('\\', '/');
                var shared = Path.IsPathRooted(loadPath)
                    ? textureProvider.GetFromFile(loadPath)
                    : textureProvider.GetFromGame(normalized);
                var wrap = shared.GetWrapOrDefault();
                if (wrap != null)
                {
                    ImGui.Image(wrap.Handle, size);
                    rendered = true;

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Image(wrap.Handle, new Vector2(384, 384));
                        ImGui.Text($"{wrap.Width}x{wrap.Height}");
                        ImGui.TextDisabled(gamePath);
                        var norm = actualPath.Replace('\\', '/');
                        if (!string.IsNullOrEmpty(actualPath) && norm != gamePath)
                            ImGui.TextDisabled(actualPath);
                        ImGui.EndTooltip();
                    }

                    if (ImGui.BeginPopupContextItem(label))
                    {
                        if (ImGui.Selectable(Strings.T("menu.copy_game_path")))
                            ImGui.SetClipboardText(gamePath);
                        if (!string.IsNullOrEmpty(actualPath) && ImGui.Selectable(Strings.T("menu.copy_actual_path")))
                            ImGui.SetClipboardText(actualPath);
                        ImGui.EndPopup();
                    }
                }
            }
            catch { /* texture load failed */ }
        }

        if (!rendered)
        {
            ImGui.Dummy(size);
            if (!hasPath) ImGui.TextDisabled("(None)");
        }

        // Show filename below preview
        if (hasPath)
            ImGui.TextDisabled(Path.GetFileName(gamePath));

        ImGui.EndGroup();
    }

    // ── Selection logic ──────────────────────────────────────────────────────

    private void AddTargetGroupFromMtrl(ResourceNodeDto mtrlNode, ResourceNodeDto? parentMdl,
        List<(string GamePath, string ActualPath)>? liveMdls = null)
    {
        // Only clear state and redraw when there are active Penumbra redirects.
        // This avoids unnecessary character reloads (flash) when rapidly adding
        // multiple groups before any preview has been triggered.
        if (penumbra.HasActiveRedirects)
        {
            previewService.ClearTextureCache();
            previewService.ResetSwapState();
            penumbra.ClearRedirect();
            penumbra.RedrawPlayer();
        }

        var mtrlGp = mtrlNode.GamePath;
        if (string.IsNullOrEmpty(mtrlGp))
        {
            DebugServer.AppendLog("[MainWindow] AddTargetGroupFromMtrl: mtrl has no GamePath, aborting");
            return;
        }

        RefreshResources();

        // Re-find the same mtrl node in the freshly fetched tree (the redraw
        // may have invalidated the original references).
        ResourceNodeDto? freshMtrl = null;
        if (cachedTrees != null)
        {
            foreach (var (_, tree) in cachedTrees)
            {
                foreach (var topNode in tree.Nodes)
                {
                    var candidates = CollectDescendants(topNode, n => n.Type == ResourceType.Mtrl);
                    var hit = candidates.FirstOrDefault(c => c.GamePath == mtrlGp);
                    if (hit != null) { freshMtrl = hit; break; }
                }
                if (freshMtrl != null) break;
            }
        }
        freshMtrl ??= mtrlNode;

        var freshDiffuse = FindDescendant(freshMtrl, IsDiffuseTexNode);
        var freshNormal = FindDescendant(freshMtrl, n =>
            n.Type == ResourceType.Tex && n.GamePath != null && IsNormalPath(n.GamePath));

        // Name the group after the diffuse texture filename (more intuitive than mtrl name)
        var groupName = freshDiffuse != null
            ? Path.GetFileNameWithoutExtension(freshDiffuse.GamePath ?? freshDiffuse.ActualPath)
            : freshMtrl.Name ?? Path.GetFileName(freshMtrl.GamePath ?? freshMtrl.ActualPath);
        var tg = project.AddGroup(groupName);

        if (freshDiffuse != null)
        {
            tg.DiffuseGamePath = freshDiffuse.GamePath!;
            tg.DiffuseDiskPath = GetDiskPath(freshDiffuse);
            tg.OrigDiffuseDiskPath = tg.DiffuseDiskPath;
        }

        if (freshNormal != null)
        {
            tg.NormGamePath = freshNormal.GamePath!;
            tg.NormDiskPath = GetDiskPath(freshNormal);
            tg.OrigNormDiskPath = tg.NormDiskPath;
        }

        tg.MtrlGamePath = freshMtrl.GamePath ?? "";
        tg.MtrlDiskPath = GetDiskPath(freshMtrl);
        tg.OrigMtrlDiskPath = tg.MtrlDiskPath;

        // Resolve all canonical mdl(s) + matIdx for this skin material.
        // For body materials this can be multiple mdls (mod-injected case).
        var resolution = skinMeshResolver.Resolve(mtrlGp, cachedTrees);

        if (resolution.Success)
        {
            tg.MeshSlots = resolution.MeshSlots;
            tg.LiveTreeHash = resolution.LiveTreeHash;
            // Mirror first slot into legacy fields so old code paths
            // (display, etc.) keep working.
            tg.MeshGamePath = resolution.PrimaryMdlGamePath;
            tg.MeshDiskPath = resolution.PrimaryMdlDiskPath;
            tg.TargetMatIdx = resolution.MeshSlots[0].MatIdx;
            previewService.LoadMeshSlots(resolution.MeshSlots);
            previewService.NotifyMeshChanged();
            if (ModelEditorWindowRef != null) ModelEditorWindowRef.IsOpen = true;
        }
        else
        {
            // Resolver failed (non-standard mtrl filename like _bibo).
            // Fall back to LiveMdls from the resource tree card, which already
            // contains all mdl nodes that reference this material.
            tg.TargetMatIdx = [];
            tg.MeshSlots = [];
            var mdlPaths = new List<string>();
            if (liveMdls != null)
            {
                foreach (var (gp, ap) in liveMdls)
                {
                    var dp = Path.IsPathRooted(ap) ? ap : gp;
                    if (!string.IsNullOrEmpty(dp) && !mdlPaths.Contains(dp))
                        mdlPaths.Add(dp);
                }
            }
            if (mdlPaths.Count == 0 && parentMdl != null)
                mdlPaths.Add(GetDiskPath(parentMdl));

            if (mdlPaths.Count > 0)
            {
                // Don't set MeshGamePath — LoadMeshForGroup checks it before
                // AllMeshPaths and would load only a single model.
                tg.MeshDiskPath = mdlPaths[0];
                for (int i = 1; i < mdlPaths.Count; i++)
                    tg.MeshDiskPaths.Add(mdlPaths[i]);
                previewService.LoadMeshForGroup(tg);
                previewService.NotifyMeshChanged();
                if (ModelEditorWindowRef != null) ModelEditorWindowRef.IsOpen = true;
            }
        }

        config.Save();
    }

    // ── Group mesh reload (resolver-aware, with legacy fallback) ─────────────

    private void ReloadGroupMesh(TargetGroup group)
        => previewService.LoadMeshForGroup(group);

    // ── Resource tree helpers ────────────────────────────────────────────────

    private void RefreshResources()
    {
        cachedTrees = penumbra.GetPlayerTrees();
        diagResolutionCache.Clear();
    }

    private static int CountNodes(List<ResourceNodeDto> nodes)
        => nodes.Sum(n => 1 + CountNodes(n.Children));

    private static ResourceNodeDto? FindDescendant(ResourceNodeDto node, Func<ResourceNodeDto, bool> predicate)
    {
        foreach (var child in node.Children)
        {
            if (predicate(child)) return child;
            var found = FindDescendant(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

    private static bool IsDiffuseTexNode(ResourceNodeDto node)
    {
        if (node.Type != ResourceType.Tex || node.GamePath == null) return false;
        if (!string.IsNullOrEmpty(node.Name) && node.Name.StartsWith("g_Sampler"))
            return node.Name == "g_SamplerDiffuse";
        return IsDiffusePath(node.GamePath);
    }

    private static bool IsDiffusePath(string gamePath)
    {
        var gp = gamePath.ToLowerInvariant();
        return gp.EndsWith(".tex") && !gp.Contains("_n.tex") && !gp.Contains("_m.tex")
            && !gp.Contains("norm") && !gp.Contains("mask")
            && !gp.Contains("_id.tex") && !gp.Contains("shadow")
            && !gp.Contains("decal");
    }

    private static string GetDiskPath(ResourceNodeDto node)
        => Path.IsPathRooted(node.ActualPath) ? node.ActualPath : (node.GamePath ?? node.ActualPath);

    private static bool IsNormalPath(string gamePath)
    {
        var gp = gamePath.ToLowerInvariant();
        return gp.EndsWith(".tex") && (gp.Contains("_n.tex") || gp.Contains("norm"));
    }

    private static List<ResourceNodeDto> CollectDescendants(ResourceNodeDto node, Func<ResourceNodeDto, bool> predicate)
    {
        var result = new List<ResourceNodeDto>();
        if (predicate(node)) result.Add(node);
        foreach (var child in node.Children)
            result.AddRange(CollectDescendants(child, predicate));
        return result;
    }

    private static ResourceNodeDto? FindAncestorMdl(ResourceNodeDto root, ResourceNodeDto target)
    {
        if (root == target) return null;
        foreach (var child in root.Children)
        {
            if (child == target)
                return root.Type == ResourceType.Mdl ? root : null;
            var found = FindAncestorMdl(child, target);
            if (found != null) return found;
            if (ContainsNode(child, target) && child.Type == ResourceType.Mdl)
                return child;
        }
        return null;
    }

    private static bool ContainsNode(ResourceNodeDto root, ResourceNodeDto target)
    {
        if (root == target) return true;
        return root.Children.Any(c => ContainsNode(c, target));
    }
}
