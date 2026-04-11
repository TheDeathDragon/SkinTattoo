using System;
using System.Collections.Generic;
using SkinTattoo.Core;

namespace SkinTattoo.Services;

/// <summary>
/// Pure-function builder: vanilla Half[] ColorTable + allocated layers -> modified copy.
/// Handles Dawntrail 8-vec4-per-row layout only. Caller must check width == 8.
///
/// Row layout within a row pair (rowPairIdx 0-15):
///   row_lower = rowPairIdx * 2      -> layer's overridden PBR
///   row_upper = rowPairIdx * 2 + 1  -> vanilla baseline (for edge blend via normal.g)
///
/// Half offsets within each 32-Half row (Dawntrail, matches Penumbra ColorTableRow):
///   Diffuse:    [0][1][2]
///   Specular:   [4][5][6]
///   Emissive:   [8][9][10]
///   SheenRate:  [12]
///   SheenTint:  [13]   (single Half, NOT RGB)
///   SheenAper:  [14]
///   Roughness:  [16]
///   Metalness:  [18]
/// </summary>
public static class ColorTableBuilder
{
    public const int DawntrailRowPairs = 16;
    public const int DawntrailRowsPerTable = 32;
    public const int DawntrailVec4PerRow = 8;
    public const int DawntrailHalvesPerRow = DawntrailVec4PerRow * 4;  // 32

    // Half offsets within a row
    public const int OffDiffuseR = 0;
    public const int OffDiffuseG = 1;
    public const int OffDiffuseB = 2;
    public const int OffSpecularR = 4;
    public const int OffSpecularG = 5;
    public const int OffSpecularB = 6;
    public const int OffEmissiveR = 8;
    public const int OffEmissiveG = 9;
    public const int OffEmissiveB = 10;
    public const int OffSheenRate = 12;
    public const int OffSheenTint = 13;
    public const int OffSheenAperture = 14;
    public const int OffRoughness = 16;
    public const int OffMetalness = 18;

    /// <summary>
    /// Returns true if the given vanilla ColorTable dimensions are Dawntrail PBR-capable.
    /// Legacy (ctWidth==4, ctHeight==16) or other shapes are not supported by v1.
    /// </summary>
    public static bool IsDawntrailLayout(int ctWidth, int ctHeight)
        => ctWidth == DawntrailVec4PerRow && ctHeight == DawntrailRowsPerTable;

    /// <summary>
    /// Clone the vanilla table and overwrite allocated row pairs with each layer's PBR.
    /// - Row pair lower row: layer PBR override (= prevRow read by Penumbra MaterialExporter:136).
    /// - Row pair upper row: vanilla baseline (= nextRow, used when normal.g produces fade-out).
    ///
    /// Per Penumbra: rowBlend = 1 - normal.g/255, lerped = lerp(prevRow, nextRow, rowBlend).
    /// We write normal.g = weight*255 -> weight=1 -> rowBlend=0 -> reads lower row (= our override).
    /// Layer override therefore lives at row pair*2 (= prev/lower), vanilla baseline at *2+1.
    /// </summary>
    public static Half[] Build(Half[] vanillaTable, int ctWidth, int ctHeight,
        IReadOnlyList<DecalLayer> allocatedLayers)
    {
        if (!IsDawntrailLayout(ctWidth, ctHeight))
            throw new ArgumentException($"ColorTableBuilder only supports Dawntrail 8x32 layout, got {ctWidth}x{ctHeight}");
        if (vanillaTable.Length < ctWidth * ctHeight * 4)
            throw new ArgumentException("vanillaTable buffer too small");

        var table = (Half[])vanillaTable.Clone();

        foreach (var layer in allocatedLayers)
        {
            int pair = layer.AllocatedRowPair;
            if (pair < 0 || pair >= DawntrailRowPairs) continue;
            if (!layer.RequiresRowPair) continue;

            int rowLower = pair * 2;         // layer override
            int rowUpper = pair * 2 + 1;     // vanilla baseline (= vanilla row 0 fallback per spec)

            int lowerBase = rowLower * DawntrailHalvesPerRow;
            int upperBase = rowUpper * DawntrailHalvesPerRow;
            int vanillaRow0Base = 0;  // simplified fallback per spec risk #1

            // Upper row = pure vanilla baseline
            for (int i = 0; i < DawntrailHalvesPerRow; i++)
                table[upperBase + i] = vanillaTable[vanillaRow0Base + i];

            // Lower row = vanilla baseline + per-field override
            for (int i = 0; i < DawntrailHalvesPerRow; i++)
                table[lowerBase + i] = vanillaTable[vanillaRow0Base + i];

            if (layer.AffectsDiffuse)
            {
                table[lowerBase + OffDiffuseR] = (Half)layer.DiffuseColor.X;
                table[lowerBase + OffDiffuseG] = (Half)layer.DiffuseColor.Y;
                table[lowerBase + OffDiffuseB] = (Half)layer.DiffuseColor.Z;
            }
            if (layer.AffectsSpecular)
            {
                table[lowerBase + OffSpecularR] = (Half)layer.SpecularColor.X;
                table[lowerBase + OffSpecularG] = (Half)layer.SpecularColor.Y;
                table[lowerBase + OffSpecularB] = (Half)layer.SpecularColor.Z;
            }
            if (layer.AffectsEmissive)
            {
                var em = layer.EmissiveColor * layer.EmissiveIntensity;
                table[lowerBase + OffEmissiveR] = (Half)em.X;
                table[lowerBase + OffEmissiveG] = (Half)em.Y;
                table[lowerBase + OffEmissiveB] = (Half)em.Z;
            }
            if (layer.AffectsRoughness)
                table[lowerBase + OffRoughness] = (Half)layer.Roughness;
            if (layer.AffectsMetalness)
                table[lowerBase + OffMetalness] = (Half)layer.Metalness;
            if (layer.AffectsSheen)
            {
                table[lowerBase + OffSheenRate] = (Half)layer.SheenRate;
                table[lowerBase + OffSheenTint] = (Half)layer.SheenTint;
                table[lowerBase + OffSheenAperture] = (Half)layer.SheenAperture;
            }
        }

        return table;
    }
}
