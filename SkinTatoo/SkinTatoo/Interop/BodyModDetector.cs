using Dalamud.Plugin.Services;

namespace SkinTatoo.Interop;

public enum BodyModType
{
    Vanilla,
    Gen3,
    BiboPlus,
    TBSE,
    Unknown,
}

public class BodyModDetector
{
    private readonly PenumbraBridge penumbra;
    private readonly IPluginLog log;

    private const string BodyPathTemplate = "chara/human/c{0}/obj/body/b0001/model/c{0}b0001.mdl";

    public BodyModDetector(PenumbraBridge penumbra, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.log      = log;
    }

    public (BodyModType Type, string MdlPath) DetectBodyMod(string raceCode)
    {
        var vanillaPath = string.Format(BodyPathTemplate, raceCode);

        if (!penumbra.IsAvailable)
        {
            log.Warning("Penumbra not available, defaulting to Vanilla body.");
            return (BodyModType.Vanilla, vanillaPath);
        }

        var resolvedPath = penumbra.ResolvePlayer(vanillaPath);
        if (resolvedPath == null || resolvedPath == vanillaPath)
            return (BodyModType.Vanilla, vanillaPath);

        var type = ClassifyByPath(resolvedPath);
        log.Information("Body mod: {0} ({1})", type, resolvedPath);
        return (type, resolvedPath);
    }

    private static BodyModType ClassifyByPath(string resolvedPath)
    {
        var lower = resolvedPath.ToLowerInvariant();
        if (lower.Contains("bibo"))                                          return BodyModType.BiboPlus;
        if (lower.Contains("gen3") || lower.Contains("tight") || lower.Contains("firm")) return BodyModType.Gen3;
        if (lower.Contains("tbse") || lower.Contains("thebody"))            return BodyModType.TBSE;
        return BodyModType.Unknown;
    }

    public static string GetBodyTexturePath(string raceCode, string textureType)
        => $"chara/human/c{raceCode}/obj/body/b0001/texture/--c{raceCode}b0001{textureType}.tex";
}
