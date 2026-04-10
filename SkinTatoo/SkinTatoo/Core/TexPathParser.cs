using System.Text.RegularExpressions;

namespace SkinTatoo.Core;

/// <summary>
/// Parse skin texture / material game paths to extract race + slot info.
///
/// We do not derive mdl paths here anymore — that's SkinMeshResolver's job.
/// This parser just gives the structured fields needed by the resolver:
/// race code, slot kind, slot id (when meaningful).
///
/// Important: the mtrl game path Penumbra exposes for body skin has been
/// rewritten by the engine (see docs/皮肤UV网格匹配调研.md §2.7) — the body
/// id is always b0001 regardless of the actual mdl. So for body, the SlotId
/// field is meaningless and the resolver must look up the canonical body
/// mdl in the live tree by race + path pattern instead.
/// </summary>
public static class TexPathParser
{
    public sealed class Parsed
    {
        public string Source = "";        // the input path
        public string? Race;              // "1401"
        public string? SlotKind;          // "body"|"face"|"tail"|"hair"|"zear"
        public string? SlotAbbr;          // "b"|"f"|"t"|"h"|"z"
        public string? SlotId;            // "0001" — meaningful for face/tail/hair, MEANINGLESS for body
        public string? RoleSuffix;        // "fac"|"iri"|"etc"|"hir"|null — what kind of skin
        public bool IsSharedIris;         // chara/common/texture/eye/eye*.tex
        public bool BodySlotIdIsRewritten; // true when parsed from a body mtrl (always b0001 lie)

        public bool IsValid => Race != null && SlotKind != null;
    }

    // chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001_a.mtrl
    // chara/human/c1401/obj/face/f0001/material/mt_c1401f0001_fac_a.mtrl
    // chara/human/c1401/obj/tail/t0004/material/v0001/mt_c1401t0004_a.mtrl
    private static readonly Regex MtrlRegex = new(
        @"^chara/human/c(?<race>\d{4})/obj/(?<slot>body|face|tail|hair|zear)/(?<abbr>[bfthz])(?<id>\d{4})/material(?:/v\d{4})?/mt_c\d{4}[bfthz]\d{4}(?:_(?<role>[a-z]+))?_[a-z]\.mtrl$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // chara/human/c1401/obj/body/b0001/texture/c1401b0001_base.tex
    // chara/human/c1401/obj/face/f0001/texture/c1401f0001_fac_base.tex
    private static readonly Regex TexRegex = new(
        @"^chara/human/c(?<race>\d{4})/obj/(?<slot>body|face|tail|hair|zear)/(?<abbr>[bfthz])(?<id>\d{4})/texture/c\d{4}[bfthz]\d{4}(?:_(?<role>[a-z]+))?_(?:base|norm|mask|d|n|m|s|id)\.tex$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SharedIrisRegex = new(
        @"^chara/common/texture/eye/eye\d+_(?:base|norm|mask|d|n|m)\.tex$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse a vanilla mtrl game path. This is the preferred entry point
    /// because mtrl game paths stay vanilla even when mods replace the file.
    /// </summary>
    public static Parsed ParseFromMtrl(string mtrlGamePath)
    {
        var p = new Parsed { Source = mtrlGamePath };
        var m = MtrlRegex.Match(mtrlGamePath);
        if (!m.Success) return p;

        p.Race                  = m.Groups["race"].Value;
        p.SlotKind              = m.Groups["slot"].Value.ToLowerInvariant();
        p.SlotAbbr              = m.Groups["abbr"].Value.ToLowerInvariant();
        p.SlotId                = m.Groups["id"].Value;
        p.RoleSuffix            = m.Groups["role"].Success ? m.Groups["role"].Value.ToLowerInvariant() : null;
        p.BodySlotIdIsRewritten = p.SlotKind == "body";
        return p;
    }

    /// <summary>
    /// Parse a vanilla tex game path. Mainly used for UI display when the tex
    /// path is the only thing we have. Mods can rewrite this to non-standard
    /// paths (e.g. chara/nyaughty/eve/...), so it returns IsValid=false in
    /// that case and callers should fall back to ParseFromMtrl.
    /// </summary>
    public static Parsed ParseFromTex(string texGamePath)
    {
        var p = new Parsed { Source = texGamePath };

        if (SharedIrisRegex.IsMatch(texGamePath))
        {
            p.IsSharedIris = true;
            return p;
        }

        var m = TexRegex.Match(texGamePath);
        if (!m.Success) return p;

        p.Race       = m.Groups["race"].Value;
        p.SlotKind   = m.Groups["slot"].Value.ToLowerInvariant();
        p.SlotAbbr   = m.Groups["abbr"].Value.ToLowerInvariant();
        p.SlotId     = m.Groups["id"].Value;
        p.RoleSuffix = m.Groups["role"].Success ? m.Groups["role"].Value.ToLowerInvariant() : null;
        return p;
    }

    /// <summary>
    /// Try mtrl first, fall back to tex. Useful for diagnostic UIs that just
    /// want to display "what is this skin material" without caring about
    /// which input format we used.
    /// </summary>
    public static Parsed ParseBest(string? texGamePath, string? mtrlGamePath)
    {
        if (!string.IsNullOrEmpty(mtrlGamePath))
        {
            var fromMtrl = ParseFromMtrl(mtrlGamePath);
            if (fromMtrl.IsValid) return fromMtrl;
        }
        if (!string.IsNullOrEmpty(texGamePath))
            return ParseFromTex(texGamePath);
        return new Parsed();
    }
}
