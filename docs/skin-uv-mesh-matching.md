# Skin UV Mesh Matching Research

> Research date: 2026-04-11
> Test character: Au Ra Raen Female (c1401)
> Test mod: Eve (a typical "smallclothes-slot" body replacement mod)

## 1. Background

To place decals on skin textures (body / face / iris / tail), we need to load a correct mesh as a canvas in the UV editor. The current `MainWindow.AddTargetGroupFromMtrl` logic works as follows:

1. Find a mtrl node in the Penumbra resource tree that references the target tex
2. Walk up to find its parent mdl node
3. Use that mdl as the UV mesh

This flow fails or returns the wrong mesh in the following situations:

- The player is wearing equipment: the body mdl may not be loaded at all
- The player has a body mod installed (e.g. Eve / bibo): the mesh is injected via a non-standard path
- When multiple mdls share the same mtrl, one is picked at random

To understand the root causes, we dumped the full resource tree and candidate resolution in 4 scenarios:

| # | Scenario |
|---|------|
| 1 | No mod + no equipment |
| 2 | No mod + wearing equipment |
| 3 | Eve mod + no equipment |
| 4 | Eve mod + wearing equipment |

## 2. Key Findings

### 2.1 `c1401b0001_top.mdl` Does Not Exist in SqPack

In all 4 scenarios, every inferred "canonical body mdl candidate" is marked `[SqPack ✗]`:

```
chara/human/c1401/obj/body/b0001/model/c1401b0001_top.mdl  [SqPack ✗]
chara/human/c1401/obj/body/b0001/model/c1401b0001.mdl      [SqPack ✗]
chara/human/c1401/obj/body/b0001/model/c1401b0001_etc.mdl  [SqPack ✗]
```

Based on `IDataManager.FileExists`. **FFXIV does not have a "vanilla naked body mdl" at all** — the b0001 slot is merely a mtrl naming convention with no corresponding mdl file.

> ⚠️ To confirm: do a direct Meddle SqPack probe as final verification (IDataManager is occasionally inaccurate).

**Conclusion**: The original plan of "derive from tex path → load vanilla `c1401b0001_top.mdl`" is completely dead.

### 2.2 tail's mtrl/tex/mdl Suffixes Are Inconsistent With Each Other

| Location | Filename | type suffix |
|---|---|---|
| mtrl | `mt_c1401t0004_a.mtrl` | none |
| tex | `c1401t0004_etc_base.tex` | `_etc` |
| mdl | `c1401t0004_til.mdl` | `_til` |

The previous `TexPathParser` was using `_etc` from the tex name to construct `c1401t0004_etc.mdl` as the mdl suffix — this is wrong.

**Correct rule**: The mdl suffix is determined solely by the **slot type**, and has nothing to do with the `_xxx` segment in the tex name.
Correspondence (reference: Penumbra `CustomizationType.ToSuffix()`):

| slot | mdl suffix |
|---|---|
| body | `_top` |
| face | `_fac` |
| iris | _(same face mdl, see §2.3)_ |
| hair | `_hir` |
| tail | `_til` |
| ear (zear) | `_zer` |

### 2.3 face / iris / etc Share the Same face mdl

The raw resource tree dump shows:

```
chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl
  ├── mt_c1401f0001_fac_a.mtrl    (face skin)
  ├── mt_c1401f0001_fac_b.mtrl    (face skin)
  ├── mt_c1401f0001_fac_c.mtrl    (face skin)
  ├── mt_c1401f0001_iri_a.mtrl    (eyeball!!!  ← same mdl)
  ├── mt_c1401f0001_etc_a.mtrl    (eyelashes/mouth/...)
  ├── mt_c1401f0001_etc_b.mtrl
  └── mt_c1401f0001_etc_c.mtrl
```

The face mdl contains 7 material slots at once; the eyeball's UV is a mesh subset corresponding to a certain matIdx in the face mdl — **no separate `_iri.mdl` file is needed**.

The UV editor canvas for iris textures (including the shared `chara/common/texture/eye/eye10_base.tex`) should use `c1401f0001_fac.mdl` + filter for matIdx == the slot that uses the iri material.

### 2.4 body Has No b0001 mdl; Full-Body Skin Is Assembled From 7 mdls Sharing the Same mtrl

In scenario 1 (naked, no mod), 7 mdls reference `mt_c1401b0001_a.mtrl`:

```
4 smallclothes pieces:
  chara/equipment/e0000/model/c0201e0000_top.mdl  ← visible chest/shoulder/arm skin
  chara/equipment/e0000/model/c0201e0000_glv.mdl  ← wrist/back of hand
  chara/equipment/e0000/model/c0201e0000_dwn.mdl  ← thighs/shins
  chara/equipment/e0000/model/c0201e0000_sho.mdl  ← ankles

3 cross-race shared "undergarment cutout" chunks:
  chara/human/c0201/obj/body/b0005/model/c0201b0005_top.mdl  ← Hyur F b5
  chara/human/c1401/obj/body/b0002/model/c1401b0002_top.mdl  ← Aura F b2
  chara/human/c0801/obj/body/b0003/model/c0801b0003_top.mdl  ← Lala F b3
```

These 7 mdls each contribute a portion of the body, and **all of them together constitute the complete body skin UV coverage**. This explains the user's observation that "the chest and crotch areas have underwear, so those parts are separately cut out" — the game uses mtrl sharing + multi-mdl assembly to represent the concept of "skin"; there is no single "body mdl".

**When wearing equipment** (scenarios 2 / 4), the 4 smallclothes pieces are replaced with actual equipment mdls (e.g. `c1401e6085_met.mdl`, `c0201e6108_top.mdl`), but they still reference the same `mt_c1401b0001_a.mtrl`, because equipment models also need to sample body skin to draw the skin exposed at necklines/sleeves/pant legs. The 3 c0201b0005 / c1401b0002 / c0801b0003 entries are always in the tree, unaffected by equipment.

### 2.5 mtrl game path Is the Only Stable Anchor

4 scenarios × 4 textures = 16 combinations, and the **mtrl game path is 100% vanilla** in every case:

```
mt_c1401b0001_a.mtrl       (body)
mt_c1401f0001_fac_a.mtrl   (face skin)
mt_c1401f0001_iri_a.mtrl   (iris)
mt_c1401t0004_a.mtrl       (tail)
```

Even mods like Eve that rewrite the mtrl to a path like `D:\FF14Mod\Eve\vanilla materials\raen_a.mtrl` still have a **vanilla game path** — the mod replaces the mtrl file itself, not the path.

Contrast with tex game path:
- Without mod: `chara/human/c1401/obj/body/b0001/texture/c1401b0001_base.tex`
- With Eve mod: `chara/nyaughty/eve/vanilla_raen_base.tex` ← **entirely mod-custom path**

→ The anchor for resolution must be the mtrl game path; the tex game path **cannot** be used.

### 2.6 Eve Mod = "Smallclothes-Slot Redirect" Body Replacement

Scenario 3 makes this very clear:

```
chara/equipment/e0000/model/c0201e0000_top.mdl → D:\FF14Mod\Eve\models - body\body - milky.mdl
chara/equipment/e0000/model/c0201e0000_glv.mdl → D:\FF14Mod\Eve\models - hands\hands - natural.mdl
chara/equipment/e0000/model/c0201e0000_dwn.mdl → D:\FF14Mod\Eve\models - legs\legs - default.mdl
chara/equipment/e0000/model/c0201e0000_sho.mdl → D:\FF14Mod\Eve\models - feet\feet - natural.mdl
```

Eve **does not redirect the vanilla body mdl path** (because that mdl doesn't exist at all, see §2.1); it replaces the mesh of the 4 smallclothes slots with "individual parts of a full nude body". When the player is wearing only smallclothes (no other equipment), these 4 mod mdls render as Eve's complete nude body.

→ Any "load mdl as UV canvas" logic must go through **Penumbra's resolved `mdl_node.ActualPath`** in order to get the mod-replaced mesh. Using vanilla paths directly would give the wrong UV (though mods like Eve that use vanilla-compatible UV might still look fine, mods like bibo that change the UV would break).

### 2.7 ⭐ Key Finding: The Game Engine Forces skin mtrl's `bXXXX` to `b0001` at Runtime

Source: FFXIV_TexTools_UI's `Views/FileControls/ModelFileControl.xaml.cs:455-496` (`AdjustSkinMaterial`). TexTools comments state directly:

> // XIV automatically forces skin materials to instead reference the
> // appropriate one for the character wearing it.
> race = XivRaceTree.GetSkinRace(race);
> ...
> mtrl = Regex.Replace(mtrl, "b[0-9]{4}", "b0001");

**Meaning**: The mdl file **internally** references its original body slot id (e.g. `c1401b0002_top.mdl` most likely references `mt_c1401b0002_a.mtrl` internally), but the game engine forcibly replaces `bXXXX` with `b0001` via regex at load time. So what we see in the Penumbra resource tree is the **already engine-rewritten** form:

```
Mdl chara/human/c1401/obj/body/b0002/model/c1401b0002_top.mdl
  └─ Mtrl chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001_a.mtrl
                                          ↑↑↑↑↑
                                       This segment was rewritten from b0002 to b0001 by the engine
```

This explains why "7 completely unrelated mdls all reference the same mtrl" — each originally referenced a different body slot, but the engine normalized them all to `b0001`, so in the tree they appear to share the same mtrl. **They do not actually share the same UV mesh**.

### 2.8 ⭐ Key Finding: Each Race Has Exactly One Canonical Body mdl

Source: VFXEditor-CN's `VFXEditor/Files/common_racial` (complete vanilla asset list). For c1401 (Au Ra Female), only **one** body mdl is listed:

```
$ grep "c1401" common_racial | grep "/body/" | grep ".mdl$"
chara/human/c1401/obj/body/b0002/model/c1401b0002_top.mdl    ← the only one
```

All other entries are just mtrl files, no mdl.

**Au Ra Female's entire race has exactly one vanilla body mdl: `c1401b0002_top.mdl`.**

The previously seen `c0201b0005_top.mdl` / `c0801b0003_top.mdl` are body mdls of other races (Hyur F b5, possibly Lala F b3). They appear in the player's tree because they reference the same skin mtrl that was rewritten to `b0001`, but they are **not the c1401 player's body**.

> Hyur Midlander Male (c0101) has 5 body slots in the list: b0001 (4 pieces: top/glv/dwn/sho) + one _top each for b0002/b0003/b0005/b0006. b0001 is the piecewise "naked smallclothes-style" body used for NPCs and similar characters without equipment; b0002+ are the full-body mdls used by normal adult characters. Different races / genders have different canonical body slot ids (aura f is b0002, hyur m is b0001 4-piece, etc.) — this needs to be data-driven rather than hardcoded guessing.

### 2.9 Preliminary Synthesis of §2.7 + §2.8

Combining the two findings:

| What we see | Reality |
|---|---|
| 7 mdls all hanging off `mt_c1401b0001_a.mtrl` | Each originally references a different body slot; all were normalized to b0001 by the engine |
| `c1401b0001_top.mdl` not found | b0001 simply doesn't exist for c1401 |
| Which mdl is "the c1401 player's body"? | Appears to be the single answer after filtering by race + body path pattern: `c1401b0002_top.mdl` |

But **this conclusion was later disproved by actual testing** — see §2.10.

### 2.10 ⭐ Testing Correction: `c1401b0002_top.mdl` Is a 24-Vertex Stub, **Not the Body**

After ReadMaterialFileNames + Meddle ExtractMesh, actually loading `c1401b0002_top.mdl` gives:

```
[MeshExtractor] mesh[0]: matIdx=0 verts=24 indices=60 submeshes=1
[MeshExtractor] Done: 24 verts, 20 tris
```

VFXEditor's `common_racial` lists it as c1401's body mdl, but it has **only 24 vertices** — not enough to render even a single hand. This tells us:

> **The mdls under `chara/human/cXXXX/obj/body/...` paths are racial deformer / cross-race geometry stub meshes used internally by the engine (typically 24~400 vertex micro-patches), not visible rendered bodies**.

So where is the actual body geometry? **It's all in the equipment slot mdls**:

```
chara/equipment/e0000/model/c0201e0000_top.mdl   ← upper body mesh (torso + chest + arms)
chara/equipment/e0000/model/c0201e0000_glv.mdl   ← hands
chara/equipment/e0000/model/c0201e0000_dwn.mdl   ← legs
chara/equipment/e0000/model/c0201e0000_sho.mdl   ← feet
```

These 4 are the equipment mdls for **e0000 = "smallclothes (naked base)"**. FFXIV's character rendering pipeline always dresses the body slot as equipment (even e0000 underwear is equipment). These 4 mdls are deformed into the appropriate body shape for the wearer's race via the **racial deformer** system, so the same c0201e0000 mesh can serve all races.

**How body mods work** is now fully clear: mods like Eve **wholesale replace** these 4 equipment mdl files with "corresponding parts of a complete nude body", so a player wearing smallclothes sees the mod's mesh, while vanilla b0002_top.mdl stubs remain 24-vertex internal engine state.

**Summary**: Body geometry = mdls under the `chara/equipment/...` path. **Any mdl under a `chara/human/.../body/...` path should be filtered out**, regardless of race match.

### 2.11 ⭐ face mdl Material Slots Share the Same Set of Textures

The face mdl `c1401f0001_fac.mdl` has 7 material slots. The resource tree confirms that all three fac_a/b/c mtrls **reference the same set** of `c1401f0001_fac_base.tex` / `_fac_norm.tex` / `_fac_mask.tex`:

```
- Mtrl mt_c1401f0001_fac_a.mtrl     ┐
  - Tex c1401f0001_fac_base.tex     │
  - Tex c1401f0001_fac_norm.tex     │
  - Tex c1401f0001_fac_mask.tex     │
- Mtrl mt_c1401f0001_fac_b.mtrl     │
  - Tex c1401f0001_fac_base.tex     ├ all share the same fac textures
  - ...                             │
- Mtrl mt_c1401f0001_fac_c.mtrl     │
  - Tex c1401f0001_fac_base.tex     │
  - ...                             ┘
```

This means: a decal painted on `fac_base.tex` will simultaneously affect all mesh groups corresponding to the fac_a/b/c material slots. **Au Ra's horns/scales are in the fac_b slot** — if matIdx filtering only takes fac_a, the horns will be missed.

→ face must match matIdx by **role suffix**: when target = `_fac_a`, hit all slots whose internal name has role `fac` (i.e. fac_a + fac_b + fac_c), not just the one with the exact same name.

iris (`_iri_a`) and etc (`_etc_*`) each have their own role and don't interfere: painting iris only hits `iri_*` slots and won't paint onto the face.

### 2.12 UDIM Tiles: Different Race Body Stubs May Use Different UV Tiles

Per-slot UV bounds measured (3 stub mdls parsed from vanilla `_a.mtrl`):

```
c0201b0005_top.mdl: X=[1.034, 1.971] Y=[0.017, 0.984]   ← UV tile 1 (X∈[1,2])
c1401b0002_top.mdl: X=[1.245, 1.511] Y=[0.008, 0.049]   ← UV tile 1
c0801b0003_top.mdl: X=[0.439, 0.846] Y=[0.050, 0.950]   ← UV tile 0 (X∈[0,1])
```

c0801b0003 (Miqo'te F stub)'s UV is in tile 0, a different tile from the other stubs. The canvas previously used a "global `floor(min(UV))`" to normalize the tile base (to accommodate body models' UV X∈[1,2] tile 1 convention). But when merging multi-mdl results that span tiles, **cross-tile merging breaks** — after taking the floor of the minimum, only one tile's vertices fall within the canvas texture region; the other tile's vertices are pushed outside.

→ Fix the canvas to use **per-vertex `fract()`** (`uv - floor(uv)`), which correctly handles UDIM tiles: two vertices in different tiles but at the same texel will map to the same canvas position — this is standard UDIM semantics.

## 3. Final Algorithm (v3)

```
Input:
  - target_mtrl_game_path  (e.g. mt_c1401b0001_a.mtrl, in the engine-rewritten form)
  - playerRace             (from Penumbra ResourceTreeDto.RaceCode, e.g. 1401)

1. Parse mtrl path to extract race / slot kind / slot id / role suffix.

2. Scan the live resource tree to collect all Mdl nodes whose child Mtrl list has a GamePath
   equal to target_mtrl_game_path ("all referers").

3. Slot-aware filter on referers:

   - body:
       FILTER OUT all chara/human/c\d{4}/obj/body/... paths
       (these are all racial deformer stubs, not visible geometry).
       Keep chara/equipment/* / chara/accessory/* etc.
       Fallback: if empty after filtering (rare, character creator etc.), keep stubs.

   - face / hair:
       race filter, keep chara/human/c{playerRace}/obj/{slot}/.../*_{fac|hir}\.mdl
       (in practice, the live tree usually has only 1 match; filter is defensive).

   - tail:
       race filter, keep chara/human/c{playerRace}/obj/tail/.../*_til\.mdl

4. For each retained mdl, load the .mdl file and read MaterialFileNames[], then find
   all matIdx values that match the target:

   - body / tail (target has no role suffix):
       Strict normalized name match (c\d{4} → c????, b\d{4} → b???? then compare)
       e.g.: target mt_c1401b0001_a.mtrl → mt_c????b????_a.mtrl
             internal /mt_c0201b0001_a.mtrl → mt_c????b????_a.mtrl  ✓

   - face / hair (target has role suffix):
       Role-based match: extract the role suffix from the internal name, compare with
       the target's role suffix.
       e.g.: target mt_c1401f0001_fac_a.mtrl → role "fac"
             internal /mt_c1401f0001_fac_a.mtrl → role "fac"  ✓
             internal /mt_c1401f0001_fac_b.mtrl → role "fac"  ✓  (Au Ra horns)
             internal /mt_c1401f0001_fac_c.mtrl → role "fac"  ✓
             internal /mt_c1401f0001_iri_a.mtrl → role "iri"  ✗

5. Output List<MeshSlot>, each MeshSlot = (mdl_game_path, mdl_disk_path, matIdx[]).

6. Load phase: for each MeshSlot, load the corresponding mdl, extract mesh groups by matIdx,
   then merge all together.
```

### Applicability Validation (v3 Live Test)

| target | Resolution result | Notes |
|---|---|---|
| body `_a` (vanilla state) | 4 equipment + 0 stubs = **4 slots** | 4 smallclothes mdls each at matIdx [0], forming complete body |
| body `_b` (Eve mod state) | 4 equipment (Eve redirected to mod files) = **4 slots** | mod mdl's matIdx [0] is the gen3 skin slot |
| body `_a` (Eve mod state) | 4 equipment have no `_a` reference = **0 from filter, fallback to stub = 1 slot** | After Eve rewrites, mat slots only have `_b`/`_eve-piercing`; vanilla `_a` is non-visible material in mod state |
| face skin `_fac_a` | 1 mdl × **3 matIdx [0,1,2]** | fac_a + fac_c + fac_b (including Au Ra horns) |
| iris `_iri_a` | 1 mdl × **1 matIdx [3]** | only iri_a, no cross-contamination |
| tail `_a` | 1 mdl `c1401t0004_til.mdl` × matIdx [0] | tail mtrl/tex/mdl suffix inconsistency is irrelevant |

### Key Properties

- **mtrl game path is the only anchor**: stable across all 4 states: vanilla / mod / wearing equipment / not wearing equipment
- **No path derivation**: all data comes from the live tree; no "guessing the mdl path"
- **Automatic mod redirect support**: each MeshSlot's disk path is ResourceNode.ActualPath, already resolved by Penumbra's mod chain
- **Handles both race deformer and mod rewrites**: body uses equipment-only filter (works for both vanilla and mods like Eve that redirect equipment slots); face/hair/tail uses race filter (only takes the player's own race's face/hair/tail)

## 4. UV Canvas Rendering: Per-Vertex fract Instead of Global floor

`MainWindow.Canvas.cs`'s wireframe rendering originally took a single `uvBase = floor(min(UV))` for the entire mesh to normalize (to accommodate body model UV X∈[1,2] tile 1 convention). But after merging multiple mdls, **vertices from different mdls may come from different tiles** (see §2.12); global floor pushes some vertices outside the canvas.

Fix: **per-vertex `fract()`**:

```csharp
Vector2 ToScreen(Vector2 uv)
{
    var fract = new Vector2(uv.X - MathF.Floor(uv.X), uv.Y - MathF.Floor(uv.Y));
    return uvOrigin + (texOffset + fract * uvScale) * fitSize;
}
```

`(1.6, 0.5)` and `(0.6, 0.5)` both map to the same pixel on the canvas — this is the standard UDIM convention: two vertices in different tiles pointing to the same texel.

## 5. Load Chain: All Mesh Load Entry Points Go Through PreviewService.LoadMeshForGroup

Previously there were 3 different callsites each loading meshes independently:

| callsite | Old logic | Problem |
|---|---|---|
| `MainWindow.ResourceBrowser.AddTargetGroupFromMtrl` | Call resolver then LoadMeshSlots | OK |
| `MainWindow.cs` toolbar "Reload Model" button | Call LoadMeshes(group.AllMeshPaths) | Uses legacy single-mdl path, loses matIdx |
| `Plugin.cs InitializeProjectPreview` (startup) | Same as above | Same as above |
| `ModelEditorWindow.TryUploadMesh` (group switch) | Same as above | **Critical bug**: user clicks Add which triggers LoadMeshSlots loading 4 mdls, but the next frame group switch detection uses the legacy path to overwrite back to a single mdl, causing "only upper body visible" |

Fix: centralize dispatch logic in `PreviewService.LoadMeshForGroup(group)`:

```csharp
public bool LoadMeshForGroup(TargetGroup group)
{
    if (group.MeshSlots.Count > 0)
        return LoadMeshSlots(group.MeshSlots);   // new resolver path

    if (!string.IsNullOrEmpty(group.MeshGamePath))
        return LoadMeshWithMatIdx(group.MeshGamePath!,
            group.TargetMatIdx.Length > 0 ? group.TargetMatIdx : null,
            group.MeshDiskPath);                  // intermediate compat

    if (group.AllMeshPaths.Count > 0)
        return LoadMeshes(group.AllMeshPaths);    // old config compat

    return false;
}
```

All 3 callsites go through it; the new resolver path can no longer be silently overwritten.

## 6. `TexPathParser` Role Adjustment

The new algorithm no longer needs "derive mdl from path". `TexPathParser` retains only:

1. `ParseFromMtrl(mtrlGamePath)` — parses race / slot / slotId / role suffix
2. `ParseFromTex(texGamePath)` — used for UI display (unreliable; may fail in mod state)
3. `ParseBest(tex, mtrl)` — prefer mtrl, fall back to tex

**Removed**: the previous `CandidateModelTypes` fallback list (hardcoded candidates for guessing body→[top, "", etc], tail→[etc, til], etc.). The slot→suffix correspondence is now hardcoded in `SkinMeshResolver.BuildCanonicalMdlPattern` (face→fac, tail→til, hair→hir).

## 7. Reference Code Sources

- **TexTools runtime rewrite rule** (§2.7):
  `FFXIV_TexTools_UI/FFXIV_TexTools/Views/FileControls/ModelFileControl.xaml.cs:455-496`
  `AdjustSkinMaterial()` method, with authoritative comments
- **TexTools `XivRaceTree.GetSkinRace`** — race → skin race projection
  (`xivModdingFramework`, submodule not locally checked out)
- **VFXEditor canonical body list** (§2.8):
  `VFXEditor-CN/VFXEditor/Files/common_racial`
  Parsing code: `VFXEditor/Select/Data/RacialData.cs:37-49`
- **Meddle skipping b0003_top**:
  `Meddle/Meddle.Plugin/Models/Composer/CharacterComposer.cs:44-47`
