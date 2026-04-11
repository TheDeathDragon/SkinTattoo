namespace SkinTattoo.Core;

public class MeshSlot
{
    public string GamePath { get; set; } = "";
    // null = load vanilla SqPack (bypasses Penumbra redirect)
    public string? DiskPath { get; set; }
    public int[] MatIdx { get; set; } = [];
}
