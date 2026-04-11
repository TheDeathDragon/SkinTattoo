# PBR Material Property Isolation — Design Decision Record

> 2026-04-07 brainstorming in progress. This document records all design consensus reached between the user and Claude during the brainstorming phase.
> Companion document: `PBR材质属性独立化调研.md` (technical research / physical facts).
> Next action: once remaining clarification questions are answered, consolidate consensus into a formal spec under `docs/superpowers/specs/`.

## Status Overview

| Item | Status |
|---|---|
| Research (Glamourer data flow + ColorTable row selection mechanism) | ✅ Done (written to `PBR材质属性独立化调研.md`) |
| Design clarification Q&A | 🔄 In progress (Q1–Q8 answered, ~1–2 questions remaining) |
| Propose 2–3 candidate approaches | ⏸️ Pending |
| Write formal spec → `docs/superpowers/specs/` | ⏸️ Pending |
| User sign-off on spec | ⏸️ Pending |
| Move to writing-plans for implementation plan | ⏸️ Pending |

## Scope Breakdown

### v1 (this iteration): Route A — Full support for character.shpk-class materials

**Target material scope**: all materials with a built-in ColorTable
- character.shpk
- characterlegacy.shpk
- hair.shpk
- iris.shpk
- other equipment / hair / eye / eyebrow materials, etc.

**Explicitly out of scope for v1**: vanilla body skin.shpk
- Keep the existing `EmissiveCBufferHook` as a fallback — body materials can still have their emissive changed
- But v1 does not support PBR fields on body materials
- Also no "per-layer independent emissive" — because `g_EmissiveColor` is a CBuffer global constant, multiple layers are physically forced to merge

### v2 (next iteration): Route C — skin.shpk → character.shpk conversion

Reasons for deferral:
- Route C still has 6 open research items that need IDA verification (see research report "Route C Open Items")
- After Route A is working we will have first-hand experience of "writing row numbers into normal.a + modifying ColorTable" running in-game, which greatly reduces the risk of doing Route C
- All data model / compositor / UI / HTTP changes from Route A are necessary prerequisites for v2

Parallel task: during v1 implementation, investigate Route C unknowns in parallel via IDA + Penumbra data, and move directly into v2 spec once v1 wraps up.

## Reached Design Consensus

### Q1 / Q3 — Data model shape: single DecalLayer + LayerKind enum

```
enum LayerKind {
    Decal,           // existing PNG decal
    WholeMaterial,   // new: entire material as a layer, no UV transform
}

class DecalLayer {
    LayerKind Kind;

    // Decal-only (hidden in UI / ignored in serialization when Kind == WholeMaterial)
    string ImagePath;
    Vector2 UvCenter, UvScale;
    float RotationDeg;
    ClipMode Clip;

    // Common: layer-level
    float Opacity;
    bool IsVisible;

    // Common: affect toggles (field-level G1)
    bool AffectsDiffuse;
    bool AffectsSpecular;
    bool AffectsEmissive;
    bool AffectsRoughness;
    bool AffectsMetalness;
    bool AffectsSheen;        // Sheen Rate / Tint / Aperture combined into one toggle

    // Common: PBR fields (written to ColorTable only when corresponding Affects* is true)
    Vector3 DiffuseColor;
    Vector3 SpecularColor;
    Vector3 EmissiveColor;
    float   EmissiveIntensity;     // multiplied directly into EmissiveColor when writing Half ColorTable
    float   Roughness;
    float   Metalness;
    float   SheenRate;
    float   SheenTint;             // single Half at offset [13], verified against Penumbra ColorTableRow.cs:117
    float   SheenAperture;

    // Common: layer fade mask (renamed from EmissiveMask in Q7)
    LayerFadeMask FadeMask;        // renamed from old EmissiveMask enum
    float FadeMaskFalloff;
    float GradientAngleDeg;
    float GradientScale;
    float GradientOffset;
}
```

**Rationale**: decal layers and material layers share ~80% of their fields (PBR, emissive, opacity, Affects toggles, target row pair). A unified type means the compositor has only one pipeline, the HTTP API only needs one extra `kind` field, and serialization is as simple as possible. A WholeMaterial layer is physically just a special decal that writes its own row pair number into every pixel.

### Q4 — Row pair allocation = A1 fully automatic + B3 preserve vanilla outside decal

**A1 fully automatic allocation**:
- When a layer is added the plugin automatically assigns an unused row pair (0–15)
- The user is completely unaware of row number concepts; they are not exposed in the UI
- "Two layers sharing the same row for linked behavior" is not supported — if the user wants linked behavior, copying PBR values is sufficient

**B3 preserve vanilla outside decal**:
- When compositing normal.a, first scan the histogram of the original normal.a to mark which row pairs are already used by vanilla
- Pixels inside the decal coverage area have their normal.a written with the newly assigned row pair number
- Pixels outside the decal coverage area have their normal.a left at the vanilla original value
- Newly assigned row pairs must not overlap with vanilla-occupied ones
- ColorTable writes only overwrite the rows we have been assigned; vanilla rows are left intact

**Result**: vanilla visuals are completely unaffected; available row pair count ≈ 16 − vanilla-occupied count (typically enough for 8–12 layers).

### Q5 — Soft edge transitions + multi-layer overlap = Solution Y

**Solution Y: hard-cut row pair + G-channel within row pair for soft edges**

Each layer occupies one **complete row pair** (two rows):

- **Row 0** = the layer's adjusted PBR (values the user sets in the UI)
- **Row 1** = vanilla base PBR (taken from the original PBR of the covered area, used as a fallback)

Pixel write rules:

- `normal.a = row pair index × 17` (maps precisely to indices 0–15)
- `normal.g = png_alpha × fade_mask_value` (decal center = 1.0, fully uses layer PBR; edge = 0.x, blends with vanilla)

Multi-layer overlap areas: **z-order, last wins**. The later layer's row pair number completely overwrites the earlier one. The earlier layer's PBR is invisible in the overlap region.

**Cost of two rows per layer**: 16 row pairs / 2 = at most ~16 layers per material (in practice ~8–12 after subtracting vanilla-occupied ones), which is sufficient for a single material.

**Why not Solution Z (one row per layer)**: that would cause "the layer PBR to interpolate with vanilla within a row pair", which is a bug, not a feature.

### Q6 — PBR field scope + override semantics = F2 + G1 + Semantic P

**F2: Full Dawntrail field set (8 items)**:

| Field | Half offset | Type |
|---|---|---|
| Diffuse | [0][1][2] | Vector3 RGB |
| Specular | [4][5][6] | Vector3 RGB |
| Emissive | [8][9][10] | Vector3 RGB |
| Sheen Rate | [12] | float (single Half) |
| Sheen Tint | [13] | float (single Half, **not RGB** — cross-verified against Penumbra ColorTableRow.cs:117 + Glamourer MaterialValueManager.cs:14) |
| Sheen Aperture | [14] | float (single Half) |
| Roughness | [16] | float |
| Metalness | [18] | float |

**Not in v1**: Legacy-mode GlossStrength [3] / SpecularStrength [7]. Will be added when there is actual user demand; the cost is low (following Glamourer's ModeToggle pattern).

**G1: field-level toggles**:

One `Affects*` bool per PBR field (Sheen triple combined into one toggle). In the UI, one checkbox before each slider; when unchecked, the vanilla value is used (i.e. row 1 = vanilla, row 0 also writes the vanilla value).

**Semantic P: overlapping z-order, last wins**:

When multiple layers overlap on the same pixel, the later layer completely overrides the earlier one. Even if the later layer has only some PBR fields enabled (other fields Affects=false), the row pair number in the overlap area is still the later layer's, and those disabled fields also fall back to the vanilla value (row 1 content) — they do not "punch through" to the earlier layer's PBR.

**Rationale**: consistent with the row pair physical model — a pixel can only belong to one row pair, and all fields of that row pair belong to it. Punch-through merging would require field-level merging on the CPU side, which violates the physical model and is complex to implement.

### Q7 — EmissiveMask migration = M1 rename + field mapping

**M1: rename EmissiveMask → LayerFadeMask (Chinese UI: "图层羽化")**

The old 7 enum values are preserved as-is; their meaning changes from "emissive intensity shape" to "overall layer participation shape":

| Old field name | New field name |
|---|---|
| `EmissiveMask` | `FadeMask` |
| `EmissiveMaskFalloff` | `FadeMaskFalloff` |
| `GradientAngleDeg` | `GradientAngleDeg` (unchanged) |
| `GradientScale` | `GradientScale` (unchanged) |
| `GradientOffset` | `GradientOffset` (unchanged) |
| `AffectsEmissive` | `AffectsEmissive` (unchanged, semantics still "whether to override emissive") |

**Project file migration**: when deserializing DecalProject, detect old field names and map them to new ones. After a single write in the new format the old field names no longer appear.

**Physical side effect (users must be aware)**:

- Old semantics: mask shape only affects emissive intensity
- New semantics: mask shape affects **all PBR fields** of the layer (diffuse / specular / emissive / roughness / metalness / sheen) — all fade together according to the mask shape
- This is the physical nature of row-pair interpolation: a single G weight applies to all fields simultaneously; there is no way to have only one field fade independently
- Visually more natural — a decal should blend into vanilla at its edges as a unified whole

**Normal.g write formula**:

```
normal.g = clamp(png_alpha × fade_mask_value, 0, 1)
```

PNG's built-in alpha edge + user-selected fade mask shape are both applied.

**EmissiveIntensity handling**:

Multiplied directly into the three EmissiveColor Halfs when writing to ColorTable row 0:

```
row[0].EmissiveR = (Half)(EmissiveColor.X × EmissiveIntensity)
row[0].EmissiveG = (Half)(EmissiveColor.Y × EmissiveIntensity)
row[0].EmissiveB = (Half)(EmissiveColor.Z × EmissiveIntensity)
```

Half supports values >1, so it works fine for HDR.

### Q8 — Scope split = Split 1 (v1 = Route A only)

See "Scope Breakdown" above.

## Remaining Questions to Clarify

In order of importance:

1. **Row number limit degradation behavior** — what happens when the user adds more layers than available row pairs? Error / reuse oldest / refuse to create?
2. **PBR field adjustment inplace swap boundary** — when the user drags a Roughness slider, does it follow Glamourer's pattern of a full-field ColorTable update (should be flicker-free)? Does a mask shape change require a full recomposite? Which path does a field toggle switch take?
3. **Route C IDA research trigger timing** — run in parallel during v1 implementation? Or start after v1 wraps up?

Once the remaining questions are answered, move into the "propose 2–3 candidate approaches + user decision + write formal spec" phase.

## Key Technical Constraints (Finalized)

Sourced from the research report; listed here for convenient reference in the upcoming spec:

1. **ColorTable row number = round(normal.a / 17)** (Penumbra `MaterialExporter.cs:136`)
2. **Row-pair interpolation weight = 1 - normal.g / 255** (ibid. :137)
3. **Dawntrail layout = 32 rows × 64 bytes = 2048 bytes**; Legacy = 16 rows × 16 bytes = 256 bytes
4. **Shader mode detection**: `ShpkName == "characterlegacy.shpk"` → Legacy, otherwise Dawntrail (`Glamourer/Interop/Material/PrepareColorSet.cs:144`)
5. **skin.shpk has no ColorTable**, `HasColorTable=false` (v1 skips PBR processing for these materials)
6. **Glamourer's ColorTable full-field write reference** = `ReplaceColorTable` in `DirectXService.cs:21-46`, D3D11 UpdateSubresource, R16G16B16A16Float
7. **Glamourer's ColorTable read-back reference** = staging texture + memcpy in `DirectXService.cs:49-154`
8. **Latent bug**: `TextureSwapService.UpdateEmissiveViaColorTable`'s `int rowStride = ctWidth * 4` is correct for emissive in both layouts, but writing Roughness/Metalness/Sheen must follow the Dawntrail 8-vec4/row layout; distinguish by `ctWidth >= 8` during implementation
