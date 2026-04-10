namespace SkinTatoo.Core;

/// <summary>
/// One mdl + the matIdx slots within it that we want to extract. The full
/// UV painting surface is built by loading every MeshSlot, applying its
/// matIdx filter, and merging the result.
///
/// Mutable plain class so it serializes cleanly to the config JSON.
/// </summary>
public class MeshSlot
{
    public string  GamePath { get; set; } = "";
    /// <summary>
    /// Optional Penumbra-resolved disk path for this mdl. When null, the
    /// MeshExtractor falls back to loading the vanilla SqPack version of
    /// GamePath, bypassing any Penumbra mod redirect — used by the
    /// SkinMeshResolver "vanilla SqPack fallback" path so vanilla skin
    /// mtrls keep using vanilla geometry/UV even when a body mod has
    /// hijacked the same equipment slot.
    /// </summary>
    public string? DiskPath { get; set; }
    public int[]   MatIdx   { get; set; } = [];
}
