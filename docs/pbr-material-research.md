# PBR Material Property Isolation Research

> Research dated 2026-04-07. Follows up on `材质替换路线研究.md`, further investigating the feasibility of "per-decal-layer independent PBR + emissive".
> Source material: Glamourer-CN, Penumbra-CN's MaterialExporter, SkinTattoo's existing hook/swap implementation.

## Background

Two user requirements:

1. A Glamourer-style PBR parameter UI (Diffuse / Specular / Emissive / Roughness / Metalness / Sheen Rate / Sheen Tint / Sheen Aperture), allowing decal layers to carry these properties.
2. A concept of "treating an entire material as a single layer" — no move/rotate/scale, but adjustable opacity, emissive, and all the PBR properties above.

The current pain point: under the same TargetGroup, multiple layers share a single emissive — **changing one layer's emissive drags all others with it**.

## Core Mechanism: ColorTable Row Index is Sampled from normal.a

This is the physical foundation of the entire isolation scheme. Before this research we always assumed "PBR is material-level" — but the character.shpk family of shaders actually organizes the PBR parameter table (ColorTable) into "row pairs", and **which row pair a pixel belongs to is entirely determined by the alpha channel of the normal map**.

### Plain-text Evidence in Penumbra's Export Code

`Penumbra/Import/Models/Export/MaterialExporter.cs:136-149`

```csharp
var tablePair = (int) Math.Round(indexPixel.R / 17f);   // R channel (derived from normal.a) → row pair index
var rowBlend  = 1.0f - indexPixel.G / 255f;             // G channel → blend weight within a row pair

var prevRow = table[tablePair * 2];
var nextRow = table[Math.Min(tablePair * 2 + 1, ColorTable.NumRows)];

var lerpedDiffuse       = Vector3.Lerp((Vector3)prevRow.DiffuseColor,  (Vector3)nextRow.DiffuseColor,  rowBlend);
var lerpedSpecularColor = Vector3.Lerp((Vector3)prevRow.SpecularColor, (Vector3)nextRow.SpecularColor, rowBlend);
var lerpedEmissive      = Vector3.Lerp((Vector3)prevRow.EmissiveColor, (Vector3)nextRow.EmissiveColor, rowBlend);
// roughness / metalness follow the same pattern
```

`Penumbra.GameData/Files/StainService/ColorTableSet.cs:155,159`:

```csharp
public const int NumRows = 32;
public static readonly (int Width, int Height) TextureSize = (8, 32);
```

That means 32 rows = 16 row pairs (Dawntrail layout; legacy is 16 rows = 8 row pairs).

### Translation Table

| Observation | Physical Explanation |
|---|---|
| Different body regions have different PBR on the same material | Those regions have different normal.a values, mapping to different row pairs |
| Glamourer can "tune PBR per row" | Its UI is indexed by row pair; each row you edit corresponds to the matching ColorTable entry |
| Our current emissive cannot be separated per layer | The current implementation writes all emissive layers into the same value in normal.a, so all layers fall on the same row — PBR fields are necessarily shared |
| **To make each decal layer independent** | **Assign each layer its own dedicated row pair; when compositing normal.a, write the corresponding row index into the decal region** |

The G-channel interpolation within a row pair gives us a hidden bonus: **soft transitions at decal edges can be achieved by lerping between the two rows of the same row pair** — no hard cut required.

## Glamourer's PBR UI Data Flow (a directly replicable reference)

### UI Entry Points

`Glamourer/UI/Materials/MaterialDrawer.cs:84-331` (single row) and `:110-135` (multi-row)

Supported adjustable fields:
- Three RGB color pickers: Diffuse / Specular / Emissive
- Legacy-mode extra fields: GlossStrength / SpecularStrength
- Dawntrail-mode extra fields: Roughness / Metalness / Sheen / SheenTint / SheenAperture
- Single UI panel + mode toggle button (lines 180-211 ModeToggle), not two separate panels

`Glamourer/UI/Materials/AdvancedDyePopup.cs:335-503` is the advanced window; the Sheen trio each have independent drags:
- DragSheen (592-603)
- DragSheenTint (606-618)
- DragSheenRoughness (621-633)

### Data Model

`Glamourer/State/Material/ColorRow.cs:14-182`

| ColorRow Property | Half offset | Notes |
|---|---|---|
| Diffuse | [0][1][2] | Common to both layouts |
| Specular | [4][5][6] | Common to both layouts |
| Emissive | [8][9][10] | Common to both layouts |
| GlossStrength | [3] | Legacy only |
| SpecularStrength | [7] | Legacy only |
| Sheen | [12] | Dawntrail only |
| SheenTint | [13] | Dawntrail only |
| SheenAperture | [14] | Dawntrail only |
| Roughness | [16] | Dawntrail only |
| Metalness | [18] | Dawntrail only |

`ColorRow.Apply(ref ColorTableRow row, Mode mode)` uses an internal mode switch to decide which offsets to write.

### Write Chain

```
UI slider drag
  → designManager.ChangeMaterialValue(design, index, tmp)              MaterialDrawer.cs:329
  → DesignEditor.ChangeMaterialValue                                    DesignEditor.cs:273-311
  → StateApplier.ChangeMaterialValue                                    StateApplier.cs:335-357
      ↳ PrepareColorSet.TryGetColorTable(actor, index, out base, out mode)  // read back current table from GPU
      ↳ changedValue.Apply(ref baseTable[row], mode)                         // modify one row on the CPU copy
      ↳ directX.ReplaceColorTable(texture, baseTable)                        // write the entire table back to GPU
  → DirectXService.ReplaceColorTable                                    DirectXService.cs:21-46
      ↳ D3D11 UpdateSubresource, R16G16B16A16Float
```

Our current `TextureSwapService.UpdateEmissiveViaColorTable` is a simplified version of this path (only writes the emissive three Halves); expanding it to full fields is nearly mechanical work.

### Read-back (initializing sliders)

`DirectXService.cs:49-154`

`AdvancedDyePopup.cs:110-111` calls `directX.TryGetColorTable(*texture, out table)`, then `ColorRow.From(...)` converts `ColorTableRow` into UI state. Mechanism: D3D11 creates a staging texture → CopyResource → Map → memcpy into `ColorTable.Table` → cache.

## SkinTattoo Current State Summary

| Module | File | Current Status |
|---|---|---|
| Layer data | `Core/DecalLayer.cs:41-67` | Has emissive five-tuple (Color/Intensity/Mask/Falloff/Gradient*), **no PBR fields whatsoever** |
| Material binding | `Core/TargetGroup.cs:5-58` | One group corresponds to one .mtrl, with multiple layers hanging off it |
| Composite entry | `Services/PreviewService.cs:200-218` (full) / `:338-371` (async inplace) | Async thread compositing → batch swap on main thread |
| Emissive merge | `PreviewService.GetCombinedEmissiveColor` | **Pain point**: all layer emissive RGB values are weighted-merged into a single value |
| ColorTable write | `Interop/TextureSwapService.cs:209-342` | Only writes emissive [8][9][10], depends on `mtrlHandle->HasColorTable` |
| skin.shpk fallback | `Interop/EmissiveCBufferHook.cs:139-178` | Hooks OnRenderMaterial to modify g_EmissiveColor CBuffer, emissive only |
| Routing decision | `PreviewService.ApplyPendingSwaps:185-192` | Tries ColorTable first, falls back to CBuffer hook on failure |

## Route Comparison (Updated)

| Route | Description | Reuse | Effort | Limitations |
|---|---|---|---|---|
| **A. character.shpk-family materials with full PBR + per-layer independent row pairs** | Write unique row index into decal region's normal.a; write PBR fields to corresponding ColorTable rows; expand ColorTable swap to write all fields; single-material cap of 16 independent layers (minus vanilla-occupied rows) | Existing compositor + ColorTable swap + Glamourer's ColorRow field table | **Medium** | Does not cover vanilla body (skin.shpk) |
| **B. skin.shpk full-field CBuffer hook** | Hook OnRenderMaterial to modify g_DiffuseColor / g_SpecularColor / g_Material* etc.; body material can have PBR modified but cannot be per-layer (CBuffer is a global constant) | Existing EmissiveCBufferHook framework | Medium | Still shares one set of values across all layers, **does not solve isolation** |
| **C. Rewrite skin.shpk as character/charactertattoo shpk** | Use MtrlFile to change the body material's ShaderPackageName, construct the sampler / ColorTable expected by the target shader, so the body material also benefits from Route A | Penumbra's `MtrlFile` + `MtrlFile.AddRemove` + `ShpkFile` (already exist); vanilla `charactertattoo.shpk` as a potential conversion target | **Medium** (downgraded after IDA research) | Correctness of .mtrl rewrite + may need placeholder textures |

> **Route C effort has been significantly downgraded** (IDA research result): originally estimated as "large, crash-prone, requires hooking" — IDA decompilation confirmed the vanilla engine fast-paths by ShaderPackage pointer comparison, **simply setting the correct ShaderPackageName in the .mtrl file is enough to switch shaders, no hook required**. Full details in `docs/路线C-IDA调研补充.md`.

## Decision

**The user has selected Route C**, with the goal of supporting per-layer independent PBR on body materials.

Route A is a subset of Route C — even doing A first is necessary prerequisite work, because only after A is complete do we know whether "a normal character.shpk material + our written normal.a row indices + modified ColorTable" actually works in-game. The natural execution order is therefore **A first, then C**:

1. Establish the "per-layer independent row pair + full-field PBR swap" pipeline on equipment / eyes / hair
2. Verify correct rendering, controllable edges, sufficient row index budget
3. Then go back and research the skin.shpk → character.shpk conversion

## Route C Open Research Items (Post-IDA Status)

| # | Item | Status | Conclusion |
|---|---|---|---|
| 1 | Sampler/CBuffer list differences between the two shaders | 🔄 Deferred to v2 | **No IDA needed** — parse vanilla `.shpk` files directly with Penumbra's `ShpkFile.cs` to obtain sampler names / CBuffer field names / ShaderKey lists. A third candidate added: `charactertattoo.shpk` |
| 2 | Load chain after ShaderPackageName rewrite | ✅ Confirmed | Shader packages go through ResourceManager's generic loader (`sub_140304A50`), same path as .tex/.mtrl; Penumbra redirection causes .mtrl to be re-parsed |
| 3 | ColorTable physical size change compatibility | ⏸️ Reserved for v2 implementation verification | Requires in-game testing; MtrlFile.Write can already extend DataSet |
| 4 | Meaning change of normal.a channel | ✅ Confirmed | Depends on ShaderPackage — vanilla skin.shpk's BuildSkin resets normal.a to 255; after switching to character.shpk the meaning automatically becomes row pair index |
| 5 | Hook point for runtime shader package switch | ✅ Confirmed | **No hook needed** — OnRenderMaterial automatically fast-path dispatches by ShaderPackage pointer; as long as ShaderPackageName is correct in the .mtrl file, the engine automatically takes the right branch. This is the basis for the Route C effort reduction |
| 6 | Vanilla row index occupancy scan | ✅ No IDA needed | Scan the vanilla normal.a histogram inside the compositor and avoid high-frequency values; already written into v1 spec |

Detailed IDA decompilation results (including specific addresses in ffxiv_dx11.exe, shader package string array, OnRenderMaterial function signature, MaterialResourceHandle field offsets) are in `docs/路线C-IDA调研补充.md`.

## Confirmed Key Facts

### Fact 7 (New): character.shpk row selection = round(normal.a / 17)

`Penumbra/Import/Models/Export/MaterialExporter.cs:136`: `Math.Round(indexPixel.R / 17f)` maps 0-255 to 0-15. This means when writing decal row indices we must use precise values like `(rowPair * 17).Clamp(0, 255)`.

### Fact 8 (New): Intra-row-pair interpolation is carried by the G channel

`MaterialExporter.cs:137`: `1.0f - indexPixel.G / 255f`. This means if we ever want "soft PBR transitions at decal edges", we can implement it using the two rows of the same row pair plus G-channel gradients — no need to span across row pairs.

### Fact 9 (New): skin.shpk internally resets normal.a to 255

The `BuildSkin` comment in `MaterialExporter.cs:359-393` explicitly states "removes skin color influence and wetness mask", confirming that the vanilla body's normal.a is not a row index under skin.shpk. This is the fundamental reason Route C must rewrite the normal.a meaning.

### Fact 10 (New): Glamourer handles both layouts with a single UI

The ModeToggle in `MaterialDrawer.cs:180-211` + the mode switch in `ColorRow.Apply`. When building our PBR UI, copy this pattern directly — do not build two separate windows.

## Latent Bug Reminders (Carried Over + New)

- `TextureSwapService.UpdateEmissiveViaColorTable`'s `int rowStride = ctWidth * 4`: emissive is at vec4 #2, correct for both layouts; however writing Roughness [16] / Metalness [18] / Sheen [12-14] is only valid for the Dawntrail 8-vec4 layout. Implementing `UpdateMaterialPbr` must first distinguish layouts using `ctWidth >= 8`.
- When implementing Route A, "per-layer independent row pair" means the swap entry point must change from "write 1 row" to "write N rows (one per layer)". Recommend directly copying Glamourer's `ColorRow + ReplaceColorTable` full-table write-back pattern — do not keep the single-row patch approach.
- When implementing Route C, after rewriting .mtrl, `MaterialResourceHandle.FileName` uses the Penumbra redirection format (`|prefix_hash|disk_path.mtrl`); the existing "game path + disk path dual-match" logic must be carried over to the new material.
