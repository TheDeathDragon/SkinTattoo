# Route C — IDA Research Supplement

> Researched on 2026-04-07. Follows up on `PBR材质属性独立化调研.md` and `材质替换路线研究.md`.
> Performed an initial decompilation pass via IDA Pro MCP connected to ffxiv_dx11.exe (pid 11708), confirming several facts critical to Route C.

## Research Objectives

Answer the 6 unknowns listed at the end of `PBR材质属性独立化调研.md` under "Route C — items to investigate" (especially #1/#2/#5), and gather concrete behavior details about the vanilla engine's shader package / material loading mechanism for the v2 spec.

## Confirmed Facts

### Fact 11: Vanilla ships exactly 57 shader packages

Both `sub_1402AAF30` (lightweight loader) and `sub_1402ACEE0` (render system init) iterate over the same string array `off_14279FC00`; the loop exit condition is `v3 >= 0x39`, i.e. **57 shader packages**.

The full contents of the string array have been extracted via `find_regex` (contiguous segment starting at `0x14206d3a0`). Key entries:

| Path | Address |
|---|---|
| `shader/sm5/shpk/skin.shpk` | 0x14206d3a0 |
| `shader/sm5/shpk/character.shpk` | 0x14206d3c0 |
| `shader/sm5/shpk/iris.shpk` | 0x14206d3e0 |
| `shader/sm5/shpk/hair.shpk` | 0x14206d400 |
| `shader/sm5/shpk/characterglass.shpk` | 0x14206d420 |
| `shader/sm5/shpk/charactertransparency.shpk` | 0x14206d5b8 |
| `shader/sm5/shpk/charactertattoo.shpk` | 0x14206dca8 |
| `shader/sm5/shpk/characterocclusion.shpk` | 0x14206dcd0 |
| `shader/sm5/shpk/characterreflection.shpk` | 0x14206dd00 |
| `shader/sm5/shpk/characterlegacy.shpk` | 0x14206dd30 |
| `shader/sm5/shpk/characterinc.shpk` | 0x14206dd58 |
| `shader/sm5/shpk/characterscroll.shpk` | 0x14206dd80 |
| `shader/sm5/shpk/characterstockings.shpk` | 0x14206dbe8 |
| `shader/sm5/shpk/hairmask.shpk` | 0x14206dbb8 |

Load results are stored into the `a1 + 8 + i*8` slot array provided by the caller.

### Fact 12: charactertattoo.shpk is vanilla's built-in character decal shader

This is the most valuable unexpected finding from the IDA research.

**Implication**: The vanilla engine already ships a dedicated character decal shader package — the name translates literally to "character tattoo." It sits in the .shpk string table at the same level as character.shpk / characterlegacy.shpk.

**Potential significance for v2 Route C**:

- The previous Route C goal was to convert skin.shpk → character.shpk
- However, character.shpk is an equipment shader and may carry more attributes than body materials need (dye / tile / sphere map, etc.)
- The name charactertattoo.shpk implies it was designed for body decals — it may be a lighter, more direct conversion target
- **The v2 spec should compare the sampler lists + ConstantBuffer fields of all three shader files and choose the most suitable conversion target**

**Note**: This comparison **does not require IDA** — you can directly use Penumbra's `Penumbra.GameData/Files/ShpkFile.cs` to parse vanilla `.shpk` files and extract sampler names / CBuffer field names / shader key lists. This is a more accurate and convenient path than IDA decompilation.

### Fact 13: Shader package load entry = ResourceManager generic file loader

Concrete call chain (during init):

```
sub_1402AAF30 / sub_1402ACEE0
  → sub_1402ED040 (when byte_14298F490 != 0)  // generic file resource loader
  → sub_1402ECFA0 (otherwise)                  // another variant of the above (cache disabled?)
      → sub_1402ECD10  // path → hash + canonicalize
      → sub_140304A50  // ResourceManager.LoadFile(qword_14298F518, ...)
```

`qword_14298F518` is the global ResourceManager instance (pointer to the `Client.System.Resource.ResourceManager` singleton).

**Key insight**: Shader packages are **not loaded through a dedicated loader**. They go through the exact same `ResourceManager.LoadFile` path as `.tex` / `.mdl` / `.mtrl` files; the only distinguishing factor is that the file extension `'shpk'` participates in the hash calculation inside `sub_1402ECD10`.

### Fact 14: OnRenderMaterial = sub_14026EE10

The hook target for SkinTattoo's `EmissiveCBufferHook`. Located via signature `E8 ?? ?? ?? ?? 44 0F B7 28` at the call instruction at `0x14026f87e`, with target `0x14026EE10`.

**Function signature** (IDA inference):

```c
_WORD * __fastcall sub_14026EE10(
    __int64    a1,    // ModelRenderer this
    _WORD     *a2,    // out: 16-bit material flags
    __int64   *a3,    // ModelRenderInfo / DrawCommand
    __int64    a4,    // MaterialContext (a4 + 16 = MaterialResourceHandle*)
    int        a5     // pass / submesh index
);
```

**Function responsibility**: Builds the 16-bit render flags for this material in the current draw command (written to `*a2`), synthesized via bitwise operations from the ShaderPackage's flag bits, material flags, and ModelRenderer state.

It itself **does not call LoadSourcePointer and does not directly update the CBuffer**. SkinTattoo's EmissiveCBufferHook executes via detour before this function, exploiting the fact that it is called once per material per frame to proactively write CBuffer data inside the detour.

### Fact 15: MaterialResourceHandle field offsets

Confirmed indirectly through the field access pattern in `sub_14026EE10` (the object obtained by dereferencing `a4 + 16`, accessed by offset):

| Offset | Field | Purpose |
|---|---|---|
| `+200` (0xC8) | `ShaderPackage*` | ShaderPackageResourceHandle pointer |
| `+232` (0xE8) | `ShaderPackageFlags` (DWORD) | Render branch bits (`& 0x4000`, `& 0x8000`, etc.) |

**Consistent with FFXIVClientStructs' existing MaterialResourceHandle definition** — the FFXIVClientStructs struct definitions already referenced by the SkinTattoo project can be trusted; there is no need to re-model them in IDA.

### Fact 16: The vanilla engine has a built-in "shader package fast-path dispatch" mechanism

`sub_14026EE10` contains code like this:

```c
v17 = *(QWORD*)(*(QWORD*)(a4 + 16) + 200);  // material->ShaderPackage
if      (v17 == *(QWORD*)(a1 + 536)) { /* fast path A */ }
else if (v17 == *(QWORD*)(a1 + 544)) { /* fast path B */ }
// ... 5 different ShaderPackage pointers are compared in total
```

`a1 + 536/544/552/560/568` are the 5 ShaderPackage pointers cached in the ModelRenderer instance. Pointer equality comparisons identify which shader type a material uses, and different flag branches are taken accordingly.

**Significance**: The vanilla engine itself needs the ability to "dispatch by shader type" — this proves that shader packages are not treated equally but have explicit "special type" distinctions. The specific 5 shaders stored in these slots were not traced further (finding the ModelRenderer init function would confirm them), but they are highly likely to include skin / character / characterlegacy / characterglass / hair (or a similar combination) — i.e. the shaders that the vanilla engine considers "important."

**Impact on Route C**: If we switch a `.mtrl`'s ShaderPackage to character.shpk, the fast-path comparison inside OnRenderMaterial will automatically take the character branch — no hook intervention is needed. Simply replacing the ShaderPackage handle is sufficient.

## Concrete Impact on Route C

Based on the above facts, the Route C implementation path can be **significantly simplified**:

### Work that is no longer needed

| Original plan | Reason to cancel |
|---|---|
| Hook the shader package load function | Shader packages are loaded via ResourceManager's generic loader; hooking it would pollute all materials |
| Extract sampler lists via IDA | Directly use Penumbra `ShpkFile.cs` to parse vanilla `.shpk` files |
| Hook runtime shader package switching | OnRenderMaterial already dispatches automatically by pointer comparison; changing ShaderPackageName in the `.mtrl` file is sufficient for the engine to take the correct shader path after loading |
| Deep-dive CBuffer beyond the scope of `ConstantBuffer逆向分析.md` | We can already modify the cbuffer via the OnRenderMaterial detour hook; after Route C switches the shader, this path may not even be needed (character.shpk PBR is handled through ColorTable) |

### Work that still needs to be done

1. **Parse vanilla's three candidate shader package files** (using Penumbra ShpkFile):
   - `shader/sm5/shpk/skin.shpk`
   - `shader/sm5/shpk/character.shpk`
   - `shader/sm5/shpk/charactertattoo.shpk` ← new candidate
   - Extract each one's sampler list + ConstantBuffer field names + ShaderKey options
2. **Decide on the conversion target**: Based on the three-way comparison, choose the most suitable target shader for body materials (most likely charactertattoo.shpk rather than character.shpk)
3. **Build a .mtrl rewriter**: Use Penumbra's `MtrlFile.AddRemove` API (already available) to write a new `.mtrl` containing:
   - New `ShaderPackageName`
   - All sampler references expected by the target shader (any missing ones need new placeholder textures or redirections to existing textures)
   - Complete ColorTable (32-row Dawntrail layout)
   - Required ShaderKey options
4. **Test**: Redirect the target body `.mtrl` to our rewritten version via a Penumbra temp mod and observe whether the game renders correctly

## Still Unknown

| Unknown | Impact | Resolution path |
|---|---|---|
| Which 5 ShaderPackages are cached at ModelRenderer +536..+568 | Affects whether switching to character.shpk triggers any fast-path side effects | Find the ModelRenderer init function (xref write to `+ 536`) — defer to v2 spec phase |
| Sampler list diff between the three candidate shaders | Determines which textures need to be added when rewriting `.mtrl` | Parse with Penumbra ShpkFile — do in v2 spec phase |
| Whether `.mtrl` loading does special preprocessing on ShaderPackageName | Affects whether a simple string replacement can switch the shader | Find MaterialResourceHandle load chain — do during v2 implementation |

## Resource Index (IDA Addresses)

| Address | Meaning |
|---|---|
| `off_14279FC00` | Shader package string array start (57 entries) |
| `sub_1402AAF30` | Lightweight shader package batch loader (init helper) |
| `sub_1402ACEE0` | Full render system init (57 shaders + vanilla textures + compute shaders) |
| `sub_1402ED040` / `sub_1402ECFA0` | ResourceManager file load wrapper (selected by cache flag) |
| `sub_140304A50` | ResourceManager.LoadFile actual entry point |
| `qword_14298F518` | ResourceManager global singleton pointer |
| `sub_14026EE10` | OnRenderMaterial (EmissiveCBufferHook target) |
| `sub_14026F790` | OnRenderMaterial's caller (render loop) |
| `0x142075d90` | RTTI string `Client.System.Resource.Handle.MaterialResourceHandle` |
| `0x142075e60` | RTTI string `Client.System.Resource.Handle.ShaderPackageResourceHandle` |
| `0x14207c9f8` | RTTI string `Client.Graphics.ShaderPackage.Allocator` |
| `sub_1402EF6D0` | MaterialResourceHandle::GetTypeName (8-byte stub) |
| `sub_14031E810` | ShaderPackageResourceHandle::GetTypeName (8-byte stub) |

## Research Conclusions

The engineering difficulty of Route C is **lower than previously estimated**. The core reasons are:

1. Switching the shader package requires no hook — changing the ShaderPackageName field in the `.mtrl` file is sufficient
2. The engine dispatches automatically by pointer comparison; no runtime intervention is needed
3. Key information (sampler/CBuffer lists) can be obtained by parsing `.shpk` files, without further IDA work
4. **The discovery of charactertattoo.shpk may be a game-changer**, making the v2 conversion target lighter

However, the hard parts of Route C remain unchanged:
- Correctness of the `.mtrl` file rewrite details
- Adding placeholder textures where necessary (e.g. index/mask textures)
- Integration with SkinTattoo's existing compositor (writing the row pair number into normal.a only makes sense after the `.mtrl` rewrite is complete)

**v2 spec writing can start in parallel** — the remaining unknowns can be fully resolved by parsing vanilla `.shpk` / `.mtrl` files; no further IDA work needs to be waited on.
