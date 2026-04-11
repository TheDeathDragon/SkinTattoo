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
        if (!ImGui.Begin("添加贴花组###SkinTattooResources", ref resourceWindowOpen))
        {
            ImGui.End();
            return;
        }

        if (ImGui.Button("刷新资源"))
            RefreshResources();
        ImGui.SameLine();
        if (ImGui.Button("导出全部诊断"))
            ExportAllDiagnostics();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("把所有皮肤/iris 卡片的完整诊断 (含 SqPack 存在性 / Penumbra 重定向) dump 成 markdown 到剪贴板");
        ImGui.SameLine();
        ImGui.TextDisabled(penumbra.IsAvailable ? "Penumbra 已连接" : "Penumbra 未连接");

        if (!penumbra.IsAvailable)
        {
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "Penumbra 未连接");
            ImGui.End();
            return;
        }

        if (cachedTrees == null || cachedTrees.Count == 0)
        {
            ImGui.TextDisabled("点击「刷新资源」查询玩家资源");
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
            .ToList();
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
            ImGui.Text($"已添加: {addedGroup.Name}");
            ImGui.PopStyleColor();
        }
        else
        {
            if (ImGui.Button("添加"))
                AddTargetGroupFromMtrl(card.FirstMtrlNode!, card.FirstParentMdl);
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
            ImGui.TextDisabled($"[{card.MtrlPaths.Count}材质合并]");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", card.MtrlPaths.Select(p => "• " + Path.GetFileName(p))));
        }

        // Diagnostics popup: copy tex / mtrl / live mdl / derived candidate mdl
        ImGui.SameLine();
        if (ImGui.SmallButton("路径"))
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

        ImGui.TextDisabled("贴图 (Diffuse)");
        DrawCopyRow("游戏路径", card.DiffuseGamePath);
        if (!string.IsNullOrEmpty(card.DiffuseActualPath))
            DrawCopyRow("实际路径", card.DiffuseActualPath);

        if (!string.IsNullOrEmpty(card.NormGamePath))
        {
            ImGui.Separator();
            ImGui.TextDisabled("贴图 (Normal)");
            DrawCopyRow("游戏路径", card.NormGamePath);
            if (!string.IsNullOrEmpty(card.NormActualPath))
                DrawCopyRow("实际路径", card.NormActualPath);
        }

        if (card.MtrlPaths.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextDisabled($"材质 ({card.MtrlPaths.Count})");
            foreach (var mp in card.MtrlPaths)
                DrawCopyRow("mtrl", mp);
        }

        ImGui.Separator();
        ImGui.TextDisabled($"模型 (资源树当前挂载: {card.LiveMdls.Count})");
        if (card.LiveMdls.Count == 0)
        {
            ImGui.TextColored(new Vector4(1, 0.6f, 0.3f, 1), "(资源树里没有 mdl  -- -- 装备覆盖或未加载)");
        }
        else
        {
            foreach (var (gp, ap) in card.LiveMdls)
            {
                if (!string.IsNullOrEmpty(gp))
                    DrawCopyRow("游戏路径", gp);
                if (!string.IsNullOrEmpty(ap) && ap != gp)
                    DrawCopyRow("实际路径", ap);
            }
        }

        ImGui.Separator();
        ImGui.TextDisabled("模型 (SkinMeshResolver 解析)");
        if (derived.IsSharedIris)
        {
            ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1),
                "共享 iris 贴图：需要从 mtrl 路径解析");
        }
        else if (!derived.IsValid)
        {
            ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1),
                "(贴图/材质路径不是标准 chara/human/... 形式)");
        }
        else
        {
            ImGui.TextDisabled(
                $"race={derived.Race} slot={derived.SlotKind}/{derived.SlotAbbr}{derived.SlotId}" +
                (derived.BodySlotIdIsRewritten ? " (b id 已被引擎改写，资源树重新解析)" : ""));

            var resolution = firstMtrl != null ? GetCachedResolution(firstMtrl) : null;
            if (resolution != null && resolution.Success)
            {
                ImGui.TextDisabled($"{resolution.MeshSlots.Count} mesh slot(s):");
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
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), "解析失败");
                foreach (var diag in resolution.Diagnostics)
                    ImGui.TextDisabled("  " + diag);
            }
        }

        ImGui.Separator();
        if (ImGui.MenuItem("一键复制全部 (markdown)"))
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
        sb.AppendLine($"# SkinTattoo 诊断 dump @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- penumbra: {(penumbra.IsAvailable ? "ok" : "unavailable")}");
        sb.AppendLine($"- trees: {cachedTrees?.Count ?? 0}");
        sb.AppendLine();

        var cards = BuildMtrlCards();
        sb.AppendLine($"# 卡片 ({cards.Count})");
        sb.AppendLine();
        foreach (var card in cards)
        {
            sb.Append(BuildCardMarkdown(card));
            sb.AppendLine();
        }

        // Probe shared paths that may not have a card (eye10 etc.)
        sb.AppendLine("# 共享路径探测");
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
        sb.AppendLine("# 资源树原始 dump (mdl/mtrl/tex)");
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
        if (ImGui.SmallButton("复制"))
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
                        if (ImGui.Selectable("复制游戏路径"))
                            ImGui.SetClipboardText(gamePath);
                        if (!string.IsNullOrEmpty(actualPath) && ImGui.Selectable("复制实际路径"))
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
            if (!hasPath) ImGui.TextDisabled("(无)");
        }

        // Show filename below preview
        if (hasPath)
            ImGui.TextDisabled(Path.GetFileName(gamePath));

        ImGui.EndGroup();
    }

    // ── Selection logic ──────────────────────────────────────────────────────

    private void AddTargetGroupFromMtrl(ResourceNodeDto mtrlNode, ResourceNodeDto? parentMdl)
    {
        previewService.ClearTextureCache();
        previewService.ResetSwapState();
        penumbra.ClearRedirect();
        penumbra.RedrawPlayer();

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
        else if (parentMdl != null)
        {
            DebugServer.AppendLog("[MainWindow] resolver failed -> legacy parentMdl fallback");
            tg.MeshGamePath = parentMdl.GamePath;
            tg.MeshDiskPath = GetDiskPath(parentMdl);
            tg.TargetMatIdx = [];
            tg.MeshSlots = [];
            previewService.LoadMesh(parentMdl.GamePath ?? GetDiskPath(parentMdl));
            previewService.NotifyMeshChanged();
            if (ModelEditorWindowRef != null) ModelEditorWindowRef.IsOpen = true;
        }
        else
        {
            DebugServer.AppendLog("[MainWindow] resolver failed and no fallback parentMdl  -- no mesh loaded");
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
