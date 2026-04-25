# skin.shpk ValEmissive body seam (v10, v11b, v11c final)

Fix for the visible seam between the decal-applied upper body and the vanilla
lower body when Normal-target emissive is enabled.

Five investigation passes end-to-end. v11c is what ships; v10/v11b/v13 and
the cb7 slot-lock false start are retained below as postmortems so nobody
re-walks the same dead ends.

## Final state (v11c, shipped)

* `MtrlFileWriter.WriteEmissiveMtrlWithColorTable` forces mtrl keys to
  `(ValEmissive, ValDecalEmissive, ValVertexColorEmissive)` and rewrites
  `ShaderPackageName` to `skin_ct.shpk`.
* `SkinShpkPatcher` (default `Mode = ValEmissive_v11b`, plus the extra
  gbuffer pass) patches both pass-2 (32 lighting PSes) and pass-0 (8
  g-buffer PSes) of the ValEmissive family, rewriting `v2.zwzz` -> `v2.xyxx`
  on the tile-orb sample in each.
* Output file: `skin_ct_v11c.shpk`.
* mode switch at runtime: `POST /api/debug/patch-mode?mode=v11b|v13`.

## Open follow-ups

These were outside the scope of the seam fix but came up during the
investigation. Left unresolved.

### 1. Drag-to-move is not realtime when Normal emissive is enabled

When a Normal-target decal is dragged on the canvas with emissive on,
position updates stall. `CheckCanSwapInPlace` keeps returning false with
`denyReason` flipping between `new norm-only group needs full redraw` and
`diffuse not initialized`, so every cycle falls back to the 600 ms
Full-Redraw debounce instead of the GPU in-place swap. The build pipeline
emits `redirects=1|3|4` over consecutive cycles (log evidence in the
`[Build] redirects=N` messages) -- 1 or 3 redirect cycles happen when
`TryDeployPatchedSkinShpk` early-returns because `hasEmissive` flickers
to false, and `SetTextureRedirects` (using `AddTemporaryModAll` with a
fixed tag) **replaces** Penumbra's whole temp-mod set, so a short cycle
wipes the previous init and forces another Full Redraw next frame.

Fix direction: make `TryDeployPatchedSkinShpk` / `ProcessGroup` emit the
full redirect set every cycle (or switch to `AddTemporaryMod` per-path
instead of `AddTemporaryModAll`). Also consider why `hasEmissive` would
ever flicker during a drag -- it shouldn't, which suggests a concurrency
or ordering bug in `PreviewService`.

With emissive off, the flow is stable; only flipping on Normal emissive
triggers this.

### 2. ValBody v13 path: shadow artifacts + missing bloom

`SkinShpkPatcher.Mode = ValBody_v13` is retained as an opt-in because it
trivially eliminates the seam (same g-buffer pipeline as vanilla body),
but it has two regressions that make it unshippable:

* Bloom halo disappears. Adding `r0.xyz += emissive * N` at the tail of
  a ValBody lighting PS never triggers FFXIV's post-process bloom, at any
  intensity. The trigger is tied to something the ValEmissive pipeline
  does (o0.w HDR key or a separate emission-buffer write) that ValBody's
  PS[8] doesn't.
* Shadow cascades glitch into per-triangle blocky artifacts in certain
  sun angles. Cause is that ValBody pass[0] PS[5] is an almost-empty
  `mov o0.xyzw, l(0,0,0,0); ret` (500 bytes) whereas ValEmissive pass[0]
  PS[17] is a full 8712-byte g-buffer writer. The deferred pipeline needs
  several g-buffer channels populated that only PS[17] writes; leaving
  the mtrl on ValBody strips those and downstream passes see undefined
  data.

If someone wants to revive v13, either port the bloom-trigger
mechanism into the ValBody tail, or find a way to run the mtrl through
PS[17]'s g-buffer write while selecting PS[8] for lighting. Neither is
trivial.

---

## Postmortem: earlier investigation passes

Two investigation passes; first pass (cb7 slot-lock) was wrong, second pass
(CT ramp inversion + alpha preservation) is shipped. Both approaches recorded
below to save re-discovery.

## Symptom

When any Normal-target emissive layer is enabled on an upper-body mtrl (e.g.
bibo body + patched `skin_ct.shpk`), the whole body's exposed skin either
shifts to a different skin-type shading (seam at the mtrl boundary) or picks
up a uniform grey self-illumination that kills the normal skin palette.
Face is unaffected (different mtrl, not patched).

## Data pass 1

`GET /api/debug/tex-stats` against the three relevant textures:

| texture | alpha mode | coverage |
|---|---|---|
| vanilla SqPack `c1401b0001_norm` | 255 | 99.87% |
| bibo disk `c1401b0001_norm` | 255 | 100% |
| vanilla `c1401b0001_base` (diffuse) | 255 | 100% |
| plugin-written `preview_gE8DB68E3_n.tex` (before fix) | 0 | 99.33% |

Across the whole vanilla body `normal.alpha` is a constant 255, and the
plugin was zeroing it out everywhere except the decal footprint.

`GET /api/player/trees` confirmed the scope: **every body mtrl reference on
the character (upper body, sleeves, gloves, legs/dwn, shoes, extra body
slots) resolves to the same mtrl game path**. Penumbra's `AddTemporaryModAll`
redirect hits all of them at once. So once we patch this one mtrl to the
Emissive shader variant + attach the patched `skin_ct.shpk`, the patched
pipeline runs for essentially the entire exposed body skin, not just the
upper body. Only face/hair/iris avoid it because they reference different
mtrls.

## Investigation pass 1 (rejected): cb7 slot-lock

### Hypothesis

Every skin.shpk lighting PS (both `ValBody` PS[8] and `ValEmissive` PS[19],
plus every variant in `LightingPsIndices`) contains:

```
mad  r1.w, gbuffer.a, l(255.0), l(0.5)
ftou r1.w, r1.w
ishl r1.w, r1.w, l(3)
lt   ...cb7[r1.w + 0].zzzw
...
```

`cb7` is `g_ShaderTypeParameter[256]` (2048 vec4), indexed by
`normal.alpha * 255 * 8` -- i.e. each of the 256 entries is an 8-vec4 block
holding fresnel, roughness, SSS/scatter shift, specular mask, type
predicates. Vanilla `normal.alpha = 255` -> `cb7[2040..2047]` (slot 255).

Theory: patch every Emissive PS to `ishl r1.w, l(255), l(3)`, locking cb7
to the same slot 255 that the vanilla PS[8] pipeline on the lower body uses,
so upper and lower render identically.

### Implementation

Added `PatchShexLockCb7Index` in `SkinShpkPatcher.cs`. The anchor is the
unique `ftou rX.Y, rX.Y` -> `ishl rX.Y, rX.Y, l(3)` instruction pair; the
patch rewrites the ishl src0 from the temp register to the immediate literal
255, in-place (7-token instruction, no length change). Applied across all 32
Emissive pass-2 PSes. Verified via disassembly (`ishl r1.w, l(255), l(3)`).

### Why it failed

User report: whole body turned grey and kept its seam.

Two things went wrong:

1. **`g_ShaderTypeParameter` contents are not shared across shader key
   variants.** Engine pushes different cb7 payloads to `ValBody` vs
   `ValEmissive` draw calls. Slot 255 inside the `ValEmissive`-targeted
   cb7 was not the vanilla-skin parameter block -- locking to it gave a
   desaturated / off-palette skin type.
2. **The grey wasn't just the cb7 issue.** The patched shader's t10
   ColorTable sampler uses `normal.alpha` directly as the row UV:
   `mad r1.y, r0.z, l(0.9375), l(0.015625)`. With the old CT ramp
   (row 1..30 = em*(row/30), row 31 = 0), vanilla `normal.alpha = 1.0` ->
   UV.y = 0.953 -> linear-sampled between row 30 (full emissive) and row 31
   (zero), giving ~em/2 worth of self-illumination on every skin pixel.
   This by itself would have greyed out the body even with the cb7 lock in
   place.

Rolled back in v10.

## Investigation pass 2 (shipped): CT ramp inversion + alpha preservation

### Fix A -- preserve vanilla `normal.alpha` outside the decal

`OverlayNormalEmissiveAlpha` (`PreviewService.cs:2598`) used to zero the
alpha channel over the whole buffer and write `mask * 255` into the decal
footprint. That displaces every skin pixel's cb7 skin-type slot from the
vanilla value and is the root of the seam. New behaviour:

* No blanket alpha clear.
* Non-decal pixels keep whatever alpha the vanilla normal had
  (`ApplyUserNormalOverlay` loads vanilla bytes with `preserveAlpha=true`).
* Decal-covered pixels get alpha **reduced** from the current value by
  `mask * 255`, clamped at 0. Multiple decals accumulate by always taking
  the min.

Result: alpha=255 everywhere except inside the decal, which ramps down to
0 at the decal's peak -- inverse of what the code used to write.

### Fix B -- invert the ColorTable ramp to match the new alpha encoding

`BuildSkinColorTableNormalEmissive` (`MtrlFileWriter.cs:360`) used to put
full emissive in row 30 and zero in row 31, matching the old encoding where
`mask=1 -> alpha=255 -> row 30.5`. New ramp:

* Rows 0-1 -> `em` (decal peak, `alpha=0 -> UV.y=0.016 -> row 0.5`).
* Rows 2..29 -> linear falloff `em * (30 - row) / 29`.
* Rows 30-31 -> `0` (vanilla skin, `alpha=255 -> UV.y=0.953 -> row 30.5`).

Animation params (cols 12..14, 17..19, 20..26) are duplicated into all 32
rows so phase/frequency/mode don't change with the emissive intensity.

### Resulting mapping

| pixel class | `normal.alpha` | cb7 slot | CT emissive |
|---|---|---|---|
| lower body (vanilla PS[8]) | 255 | 255 | -- |
| upper body non-decal (patched PS[19]) | **255 (preserved)** | 255 | 0 |
| upper body under decal | 0..254 | 0..254 | em..0 |

Upper-body-non-decal and lower-body land on the same cb7 slot (255) and
both get zero emissive -> no seam, no grey. Inside the decal footprint the
cb7 skin-type slot drifts, but the decal RGB covers that region anyway, so
any skin-type shift is visually hidden by the decal itself.

## Investigation pass 5 (final, shipped as v11c): patch pass[0] g-buffer PS too

### v13 detour

Before the final fix we briefly migrated to a `ValBody` path (v12/v13) hoping
that staying on the vanilla body g-buffer pipeline would sidestep the seam
entirely. It did eliminate the seam, but introduced two new regressions that
made it unacceptable:

* The emissive halo / bloom vanished -- FFXIV's post-process bloom appears to
  be gated on something tied to the `ValEmissive` pipeline (o0.w HDR key,
  or a separate emission-buffer write that only happens in the emissive PS
  family). Simply adding `r0.xyz += emissive * N` at the tail of a ValBody
  PS did not trigger bloom regardless of intensity.
* Random shadow cascades broke on bibo/3BO bodies in certain sun angles,
  producing visible per-triangle patches -- the ValBody pass[0] PS[5] is
  almost empty (`mov o0.xyzw, l(0,0,0,0); ret`, 500 bytes), and the
  deferred pipeline relies on the ValEmissive pass[0] PS[17] (8712 bytes,
  full g-buffer writes) to populate several channels. Staying on ValBody
  left those channels undefined once our mtrl-override kicked in.

v13 is preserved as `SkinShpkPatcher.PatchMode.ValBody_v13` and the debug
HTTP endpoint `POST /api/debug/patch-mode?mode=v13` switches into it, but
the default and shipped path is now `ValEmissive_v11b` + the pass[0]
gbuffer UV rewrite documented below.

### The one thing pass 3 got right: UV1 -> UV0 on skin_ct.shpk PS

Pass 3 (v11) rewrote `mul rX.xy, v2.zwzz, cb0[7].xyxx` -> `mul rX.xy,
v2.xyxx, cb0[7].xyxx` in every ValEmissive **pass[2]** PS, moving the
`g_SamplerTileOrb` sample from UV1 to UV0. It helped but the seam stayed
because the same instruction pattern **also** exists in every ValEmissive
**pass[0]** g-buffer PS (PS[17, 47, 77, 107, 137, 167, 197, 227]). pass[0]
writes the tile-orb-derived data into the g-buffer; pass[2] later reads it.
UV1 being inconsistent across bibo's top/dwn meshes corrupted the g-buffer
write, and no amount of pass[2] patching could recover that.

### v11c fix

`PatchSingleGbufferPsUvRewrite` in `SkinShpkPatcher.cs`: a minimal
`PatchShexTileOrbUseUv0` applied to each of the eight pass[0] g-buffer PSes.
In-place rewrite (same instruction length), no resource additions, no
blob-offset shifting -- just flip the swizzle field on the operand token
from `0xAE` (`zwzz`) to `0x04` (`xyxx`). Log line:

```
[SkinShpkPatcher] Gbuffer PS UV-rewrite: 8/8 (skipped 0)
```

Shipped as `skin_ct_v11c.shpk`. After this rewrite:

* No waist seam -- pass[0] writes identical tile-orb data for upper-body and
  lower-body meshes because both now index into UV0 which bibo authors
  consistently.
* Full bloom halo restored -- we're on the ValEmissive pipeline end-to-end,
  so whichever state PS[19] sets up that drives the post-process bloom is
  still intact.
* Shadows behave the same as vanilla ValEmissive -- we didn't touch the
  pass[0] PS beyond the one swizzle flip, so g-buffer channels outside the
  tile-orb path are written identically to vanilla.

### Diagnostic knobs

* `GET /api/debug/patch-mode` -- current mode + list of available modes.
* `POST /api/debug/patch-mode?mode=v11b|v13` -- switch at runtime, clears
  the swap cache so the next preview regenerates the patched shpk under
  the new mode (file name differs by mode so both coexist on disk).

## Investigation pass 4 (superseded): move emissive into ValBody PS tail

### Why passes 2/3 didn't fully fix the seam

Passes 2 and 3 kept the mtrl on `ValEmissive` keys and tried to normalize PS[19]
so its output matched ValBody. It worked for the obvious seam sources (normal.a
row indexing, cb7 skin-type slot, tile-orb UV set), but a residual waist-line
seam remained on bibo/3BO bodies even after tile-orb UV1 -> UV0 rewrite.

Root cause: ValEmissive's entire g-buffer prepass is a different PS. For
example `ValBody pass[0] PS[5]` is 500 bytes (near-empty), whereas
`ValEmissive pass[0] PS[17]` is 8712 bytes with extra sampler bindings
(`g_SamplerDecal`, `g_SamplerTileNormal`) and constant buffers
(`g_DecalColor`, `g_CustomizeParameter`, `g_MaterialParameterDynamic`). The
ValEmissive pipeline reads additional vertex attributes that bibo/3BO body
meshes don't provide consistently between the upper-body mesh
(`c0201e0485_top.mdl`) and the lower-body mesh (`c1401e0050_dwn.mdl`), so
pushing the mtrl into ValEmissive exposes their inconsistency as a visible
waist seam. No amount of pass[2]-PS patching can fix that because pass[0]
has already written different g-buffer contents for the two halves.

### Fix

Stay on `ValBody` shader keys. Patch the 32 `ValBody pass[2]` lighting PSes
(PS[8, 23, 38, ... 377]) to inject a ColorTable emissive sample *after* the
final gamma `sqrt` but before the brightness-scale `mul o0.xyz`:

```
(vanilla)                 (patched)
...                       ...
sqrt r0.xyz, r0.xyzx      sqrt r0.xyz, r0.xyzx
                          sample r1.xyzw, v2.xyxx, t5, s1   ; re-sample normal for row key
                          mad r1.y, r1.w, 0.9375, 0.015625  ; CT row UV
                          mov r1.x, 0.3125                   ; CT col 2 (emissive RGB)
                          sample r1.xyzw, r1.xyxx, t10, s5  ; read CT
                          add r0.xyz, r0.xyzx, r1.xyzx      ; add HDR emissive
mul o0.xyz, r0, cb1[3]    mul o0.xyz, r0, cb1[3]
ret                       ret
```

Anchor is the invariant 9-token `mul o0.xyz + ret` tail shared by every
ValBody PS; injection is 43 tokens (172 bytes) landed immediately in front
of it via byte-level `FindPattern`. Register choice (r0 for output, r1 for
the re-sampled normal + CT read) is safe because `div o0.w, r1.x, r1.y`
some instructions earlier is the last consumer of r1 in every PS.

The matching `MtrlFileWriter.WriteEmissiveMtrlWithColorTable` change: stop
rewriting the mtrl's material keys to `(ValEmissive, ValDecalEmissive,
ValVertexColorEmissive)`. Keep whatever the body mod author set (typically
just `CategorySkinType=ValBody`). Only the ShaderPackageName gets swapped
from `skin.shpk` to `skin_ct.shpk` so the engine loads the patched shader.

### Why emissive goes after `sqrt`, not before (v12 -> v13)

Initial v12 inserted the `add r0.xyz, r0, r1` before `sqrt`. The gamma curve
mapped e.g. `em=5.3` down to `sqrt(5.3)=2.3`, which was too low to trigger
the post-process bloom pass and killed the halo glow users had come to
expect from the ValEmissive path. Moving the `add` to after `sqrt` keeps
emissive in the HDR range (`5.3 * cb1[3]`), letting bloom pick it up.

### What's missing

Animation modes (`pulse`, `flicker`, `gradient`, `ripple`) were previously
injected against PS[19]'s emissive init pair -- that anchor doesn't exist
in ValBody PS[8]. Only static per-layer emissive works on the v13 path.
Re-porting animation to ValBody would need a different anchor and would
most likely live right before the new `add r0, r1` insertion.

## Investigation pass 3 (superseded): tile-orb UV1 -> UV0 rewrite

### Residual symptom

After v10 the grey cast was gone but a seam remained along the waist (the
mesh boundary between upper- and lower-body meshes). User confirmed by A/B
test: disabling the Normal-target emissive layer or turning the plugin off
made the seam vanish, so the seam is a side effect of routing the mtrl
through the `ValEmissive` shader variant rather than vanilla `ValBody`.

### Diff between ValBody PS[8] and ValEmissive PS[19]

Both lighting PSes are nearly identical except for two things:

1. The extra emissive contribution in PS[19] (the CT path replaces this
   block cleanly, so it is not the seam source).
2. The tile-orb UV source:

```
PS[8]  (ValBody):     mul rX.xy, v2.xyxx, cb0[7].xyxx   ; UV0
PS[19] (ValEmissive): mul rX.xy, v2.zwzz, cb0[7].xyxx   ; UV1
```

`g_SamplerTileOrb` is a detail tile (skin pores / texture grain). bibo-class
body mods author UV0 per-mesh but leave UV1 inconsistent between the
upper-body and lower-body meshes (the original game never runs body meshes
through an emissive variant, so UV1 is effectively dead storage). Once we
force the mtrl to ValEmissive, PS[19] samples tile-orb with whichever
arbitrary UV1 each mesh happens to have, producing a visible discontinuity
at the mesh boundary.

### Fix

`PatchShexTileOrbUseUv0` in `SkinShpkPatcher.cs` anchors the 20-byte
invariant tail of the mul (src0 `v2.zwzz` + src1 `cb0[7].xyxx` with their
index u32s), then rewrites the src0 operand token's swizzle bits from
`0xAE` (`z,w,z,z`) to `0x04` (`x,y,x,x`). In-place, same instruction
length. Applied to all 32 emissive pass-2 PSes; log shows 32/32 PSes
patched on first run.

Result: ValEmissive tile-orb sampling becomes byte-identical to ValBody,
so every skin pixel -- upper body, lower body, non-decal, decal edge --
picks up the same UV0-derived tile, matching vanilla body rendering.

## Penumbra export parity

The patched `skin_ct.shpk` is produced at runtime by `SkinShpkPatcher.Patch`
and cached to `preview/skin_ct_v11.shpk`. The export pipeline
(`PreviewService.cs:1940`, `PmpPackageWriter`) copies this file into the
staged `.pmp` under `shader/sm5/shpk/skin_ct.shpk`, so the same shader +
same CT ramp layout ships to users regardless of whether they have the
plugin installed.

Bump the filename (`v8` -> `v9` -> `v10` -> `v11`) in
`TryDeployPatchedSkinShpk` whenever either the shader patch or the CT layout
changes -- cached files on user machines are reused via
`File.Exists(candidate)`, so a rename is the simplest forcing function.

## Diagnostics (kept)

* `GET /api/debug/tex-stats?group=<idx>&source=vanilla|current|diskmod` --
  per-channel histogram (min/max/mean/p10/median/p90/uniqueCount + top bins)
  of the group's norm texture from the SqPack original, the Penumbra-resolved
  current version, or the body-mod's disk source.
* `GET /api/debug/tex-stats?path=<game-path>` / `?disk=<absolute-path>` --
  same stats on any texture.
* `GET /api/player/trees` -- used here to confirm that every body mtrl
  reference collapses to a single disk file after the redirect.

## References

* `ShaderPatcher/extracted_ps/ps_008_disasm.txt` -- vanilla ValBody pass[2]
  PS (lower body). Same `ftou + ishl l(3)` -> `cb7[r.? + X]` sequence as
  PS[19].
* `ShaderPatcher/extracted_ps/ps_019_disasm.txt` -- current v10 patched
  PS[19]. Has the t10 ColorTable sample but **does not** lock cb7 (we
  reverted the lock).
* `ShaderPatcher/reference/ps_019_EMISSIVE_disasm.txt` /
  `ps_019_PATCHED_disasm.txt` -- vanilla ValEmissive PS[19] and the earlier
  (pre-v10) patched variant.
