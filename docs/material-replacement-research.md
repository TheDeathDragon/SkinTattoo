# Material Replacement Route Research — mask / Metalness / Petrification

> Research conducted 2026-04-07. Sources: Penumbra-CN, Glamourer-CN, existing hook/swap implementation in this project.

## Background

User question: "Can we modify the mask? Is the mask a material? Can we change the material to metal/petrified?"
Example: `chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001_b.mtrl` + `chara/nyaughty/eve/gen3_aura_mask.tex`.

## Concept Clarification

| Term | File | Description |
|---|---|---|
| **Material** | `.mtrl` | Shader config + sampler list + constants + optional ColorTable |
| **mask** | `.tex` | A regular texture, referenced by `.mtrl` via sampler index |

`gen3_aura_mask.tex` is an auxiliary texture bundled with the Nyaughty Eve/gen3 body mod. **Vanilla `skin.shpk` does not sample mask** — it only reads diffuse + normal. Therefore this tex is only sampled after the mod has switched the shader to an extended skin shader that includes a mask channel.

## Key Fact Verification

### Fact 1: Vanilla skin.shpk still has no ColorTable
`Glamourer/Interop/Material/PrepareColorSet.cs:81-88`
```csharp
public static bool TryGetColorTable(MaterialResourceHandle* material, ...)
{
    if (material->DataSet is null
        || material->DataSetSize < sizeof(ColorTable.Table)
        || !material->HasColorTable)
    {
        table = default;
        return false;
    }
```
Glamourer short-circuits using `HasColorTable`, returning false for `mt_c1401b0001_b.mtrl`. **The ColorTable route cannot be used directly for body skin**. Our earlier conclusion in `CLAUDE.md` — "skin.shpk has no ColorTable" — still holds.

### Fact 2: Post-Dawntrail ColorTableRow added native Roughness / Metalness fields
`Penumbra.GameData/Files/MaterialStructs/ColorTableRow.cs:7-19`
```
#       |    X (+0)    |    Y (+1)    |    Z (+2)    |    W (+3)    |
0 (+ 0) |    Diffuse.R |    Diffuse.G |   Diffuse.B  |     Unk      |
1 (+ 4) |   Specular.R |   Specular.G |  Specular.B  |     Unk      |
2 (+ 8) |   Emissive.R |   Emissive.G |  Emissive.B  |     Unk      |
3 (+12) |   Sheen Rate |   Sheen Tint |  Sheen Apt.  |     Unk      |
4 (+16) |  Roughness?  |              |  Metalness?  |  Anisotropy  |
5 (+20) |          Unk |  Sphere Mask |          Unk |          Unk |
6 (+24) |   Shader Idx |   Tile Index |  Tile Alpha  |  Sphere Idx  |
7 (+28) |   Tile XF UU |   Tile XF UV |  Tile XF VU  |  Tile XF VV  |
```
- Each row = 8 vec4 = **32 Halfs = 64 bytes**
- Full table: 32 rows × 64 bytes = **2048 bytes** (old legacy was 16 rows × 16 bytes = 256 bytes)
- Half offset quick reference:
  - SpecularColor `[4][5][6]`
  - EmissiveColor `[8][9][10]`
  - SheenRate `[12]` / SheenTint `[13]` / SheenAperture `[14]`
  - **Roughness `[16]`**
  - **Metalness `[18]`**

### Fact 3: Dawntrail vs Legacy is determined solely by ShaderPackage name
`Glamourer/Interop/Material/PrepareColorSet.cs:144-149`
```csharp
public static ColorRow.Mode GetMode(MaterialResourceHandle* handle)
    => handle == null ? ColorRow.Mode.Dawntrail
        : handle->ShpkName.AsSpan().SequenceEqual("characterlegacy.shpk"u8)
            ? ColorRow.Mode.Legacy
            : ColorRow.Mode.Dawntrail;
```
Only `characterlegacy.shpk` uses the legacy layout; everything else (including `character.shpk` / `skin.shpk` / `hair.shpk` / `iris.shpk`) uses the Dawntrail layout.

### Fact 4: Glamourer's ColorTable real-time replacement is the complete reference implementation
| Operation | File | Lines |
|---|---|---|
| GPU → CPU read ColorTable | `Glamourer/Interop/Material/DirectXService.cs` | 49-154 (D3D11 staging + memcpy) |
| CPU → GPU write ColorTable | `Glamourer/Interop/Material/DirectXService.cs` | 21-47 (R16G16B16A16Float) |
| Real-time edit one row and push | `Glamourer/State/StateApplier.cs` | 335-356 `ChangeMaterialValue` |
| Single row write | `Glamourer/Interop/Material/MaterialValueManager.cs` | 79-155 `ColorRow.Apply` |

Our `TextureSwapService.UpdateEmissiveViaColorTable` follows the same path, but only writes the emissive field.

### Fact 5: Penumbra provides a complete .mtrl CRUD API
| Capability | File |
|---|---|
| Parse | `Penumbra.GameData/Files/MtrlFile.cs:154-217` |
| Serialize | `Penumbra.GameData/Files/MtrlFile.Write.cs:7-90` |
| `FindOrAddShaderKey` | `Penumbra.GameData/Files/MtrlFile.AddRemove.cs:103-126` |
| `FindOrAddConstant` / `GetConstantValue<T>` | `Penumbra.GameData/Files/MtrlFile.AddRemove.cs:12-51` |
| `FindOrAddSampler` | `Penumbra.GameData/Files/MtrlFile.AddRemove.cs:53-90` |
| ColorTable UI editor | `Penumbra/UI/FileEditing/Materials/MaterialEditor.ColorTable.cs` |
| ShaderPackage switch UI | `Penumbra/UI/FileEditing/Materials/MaterialEditor.ShaderPackage.cs` |

Our current `MtrlFileWriter.RebuildMtrl` is a simplified emissive-only version. If full material rewriting is needed in the future, use Penumbra's `MtrlFile` directly.

### Fact 6: Neither Penumbra nor Glamourer has a "runtime mask channel editor"
`Penumbra/Import/Models/Export/MaterialExporter.cs:83-93` only splits channels by `TextureUsage.SamplerMask` during glTF export (R=AO, A=Specular factor). Neither project has a mask editing UI — this would need to be implemented from scratch if desired.

## Route Comparison

| Goal | Route | What to Reuse | Effort | Notes |
|---|---|---|---|---|
| Metalness or petrification for equipment/eyes/hair | **A. Extend ColorTable swap** — write Metalness/Roughness/SpecularColor | Existing `UpdateEmissiveViaColorTable` + Penumbra `ColorTableRow` field table | **Small** | **Preferred starting point** |
| Metalness for vanilla body (skin.shpk) | B. Same approach as EmissiveCBufferHook — hook OnRenderMaterial to modify g_Specular*/g_Material* CBuffer constants | Existing hook; requires reverse-engineering skin.shpk constant CRC meanings | Medium | skin shader style is hardcoded, low ceiling on visual results |
| Completely restyle body material | C. Use Penumbra `MtrlFile` to rewrite `.mtrl`, change `ShaderPackageName` from skin.shpk to character.shpk + rebuild sampler/ColorTable + use Penumbra temp mod | Penumbra `MtrlFile` + `MtrlFile.AddRemove` | Large | Prone to crashes, but ready-made tooling exists |
| Modify mask `.tex` content | D. Use existing `TextureSwapService.SwapTexture` to directly swap the GPU texture | Existing swap pipeline | Small | **Only works if the current material actually references a mask sampler** |

## Decision

**Start with Route A (minimum viable experiment)**: Add `UpdateMaterialPbr(metalness, roughness, specularColor)` to `TextureSwapService`, using ColorTable swap to write Dawntrail's Metalness/Roughness/SpecularColor for character.shpk-class materials. Validate on equipment/hair first, then decide whether to invest more engineering effort for the body.

## Latent Bug Warning

`TextureSwapService.UpdateEmissiveViaColorTable` uses `int rowStride = ctWidth * 4`:
- emissive is at +8/+9/+10 per row, which is correct for both 8 vec4/row (Dawntrail) and 4 vec4/row (legacy) layouts (since emissive falls within vec4 #2)
- However, **writing Metalness (+18) and Roughness (+16) is only valid for the Dawntrail 8-vec4 layout**
- When implementing `UpdateMaterialPbr`, you must first use `ctWidth >= 8` to distinguish the layout — in legacy mode, use `Scalar7` (SpecularMask) + `Scalar3` (Shininess) instead of Metalness/Roughness
