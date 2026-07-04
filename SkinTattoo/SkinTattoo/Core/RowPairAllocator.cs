using System;
using System.Linq;

namespace SkinTattoo.Core;

/// <summary>
/// Per-TargetGroup allocator for ColorTable row pairs (0-15).
/// Scans vanilla normal.a to mark occupied slots, hands out unused slots on demand.
/// </summary>
public class RowPairAllocator
{
    private readonly bool[] vanillaOccupied = new bool[16];
    private readonly bool[] assigned = new bool[16];

    private bool scanned;

    public bool Scanned => scanned;

    /// <summary>
    /// Scan vanilla index map R channel: row pair = round(R / 17).
    /// Marks row pairs covering >=0.5% of pixels as occupied. Idempotent.
    /// </summary>
    public void ScanVanillaOccupation(byte[] vanillaIndexRgba, int width, int height)
    {
        Array.Clear(vanillaOccupied, 0, 16);
        scanned = true;

        if (vanillaIndexRgba == null || vanillaIndexRgba.Length < 4) return;
        if (width <= 0 || height <= 0) return;

        var histogram = new int[16];
        int totalPixels = width * height;
        for (int i = 0; i < vanillaIndexRgba.Length; i += 4)
        {
            int rowPair = (int)Math.Round(vanillaIndexRgba[i] / 17.0);
            if (rowPair >= 0 && rowPair < 16)
                histogram[rowPair]++;
        }

        int threshold = Math.Max(1, totalPixels / 200);
        for (int i = 0; i < 16; i++)
        {
            if (histogram[i] > threshold)
                vanillaOccupied[i] = true;
        }
    }

    /// <summary>
    /// Scan a normal map's ALPHA channel for EXACT k*17 plateau values (skin CT path).
    /// Only exact grid values can pass the v11f in-shader gate, so pairs whose exact
    /// value covers >=0.5% of pixels (vanilla/mod skin-ID plateaus like Eve's) are
    /// marked occupied -- painting a decal with that same row key would light the
    /// whole plateau. Sparse exact hits (anti-aliased bands) stay allocatable; the
    /// composite nudges those baseline pixels off-grid instead. Idempotent.
    /// </summary>
    public void ScanVanillaOccupationAlpha(byte[] normRgba, int width, int height)
    {
        Array.Clear(vanillaOccupied, 0, 16);
        scanned = true;

        if (normRgba == null || normRgba.Length < 4) return;
        if (width <= 0 || height <= 0) return;

        var histogram = new int[16];
        for (int i = 3; i < normRgba.Length; i += 4)
        {
            int a = normRgba[i];
            if (a != 0 && a < 255 && a % 17 == 0)
                histogram[a / 17]++;
        }

        int threshold = Math.Max(1, width * height / 200);
        for (int i = 1; i < 15; i++)
        {
            if (histogram[i] > threshold)
                vanillaOccupied[i] = true;
        }
    }

    public int? TryAllocate()
    {
        // Pair 15 (alpha 255) is never assigned: the v11f in-shader grid gate treats
        // k=15 as "vanilla lip mask" and forces it to row 0, so a decal placed there
        // would go dark.
        for (int i = 0; i < 15; i++)
        {
            if (!vanillaOccupied[i] && !assigned[i])
            {
                assigned[i] = true;
                return i;
            }
        }
        return null;
    }

    public bool TryAllocate(DecalLayer layer)
    {
        if (layer.AllocatedRowPair >= 0) return true;
        var slot = TryAllocate();
        if (slot == null) return false;
        layer.AllocatedRowPair = slot.Value;
        return true;
    }

    public void Release(int rowPair)
    {
        if (rowPair >= 0 && rowPair < 16)
            assigned[rowPair] = false;
    }

    public int AvailableSlots
    {
        get
        {
            int count = 0;
            for (int i = 0; i < 15; i++)
                if (!vanillaOccupied[i] && !assigned[i]) count++;
            return count;
        }
    }

    public int VanillaOccupiedCount => vanillaOccupied.Count(b => b);

    public void ReleaseAll()
    {
        Array.Clear(assigned, 0, 16);
    }

    public void Reset()
    {
        Array.Clear(vanillaOccupied, 0, 16);
        Array.Clear(assigned, 0, 16);
        scanned = false;
    }
}
