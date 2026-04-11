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
    /// Marks row pairs covering ≥0.5% of pixels as occupied. Idempotent.
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

    public int? TryAllocate()
    {
        for (int i = 0; i < 16; i++)
        {
            if (!vanillaOccupied[i] && !assigned[i])
            {
                assigned[i] = true;
                return i;
            }
        }
        return null;
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
            for (int i = 0; i < 16; i++)
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
