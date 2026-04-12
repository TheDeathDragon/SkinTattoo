# SkinTattoo - FFXIV Real-Time Skin Decal Plugin

## Project Overview

Dalamud plugin that composites PNG decals onto FFXIV character skin UV textures with real-time preview via Penumbra temporary mods.

## Build

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTattoo
dotnet build -c Release
```

Dalamud SDK path is auto-configured via `Directory.Build.props` to point to `XIVLauncherCN`. If the build fails, check that `%AppData%\XIVLauncherCN\addon\Hooks\dev\` exists.

**Always run `dotnet build -c Release` after code changes to verify compilation.**

## Project Structure

```
SkinTattoo.slnx                       # Solution
Penumbra.Api/                         # git submodule (MeowZWR cn-temp)
SkinTattoo/SkinTattoo/                  # Main project
  Plugin.cs                           # Entry point
  Configuration.cs                    # Persistent configuration
  Core/                               # Data models (DecalLayer, DecalProject, TargetGroup)
  Mesh/                               # Mesh extraction (MeshExtractor, MeshData, RayPicker)
  DirectX/                            # DX11 offscreen rendering (DxRenderer, MeshBuffer, OrbitCamera)
  Interop/                            # PenumbraBridge, TextureSwapService, EmissiveCBufferHook
  Services/                           # PreviewService, TexFileWriter, DecalImageLoader, MtrlFileWriter
  Shaders/                            # HLSL shaders (Model.hlsl)
  Http/                               # EmbedIO HTTP debug server (localhost:12580)
  Gui/                                # ImGui windows
    MainWindow.cs                     # Main window core (partial: tabs/toolbar/layout/init)
    MainWindow.Canvas.cs              # UV canvas + wireframe
    MainWindow.LayerPanel.cs          # Left panel: layer list
    MainWindow.ParameterPanel.cs      # Right panel: parameters (decal attributes)
    MainWindow.SettingsTab.cs         # Settings tab: master toggle/texture/wireframe/HTTP
    MainWindow.ResourceBrowser.cs     # Resource browser + model detection
    ModelEditorWindow.cs              # 3D editor
    DebugWindow.cs / ModExportWindow.cs
ShaderPatcher/                        # Python tools: patch skin.shpk for ColorTable emissive
  parse_shpk.py                       # .shpk binary parser (material params, keys, nodes)
  dxbc_patcher.py                     # DXBC container parser + SHEX instruction stream + PS extraction
  dxbc_patch_colortable.py            # Patch PS[19]: inject s5/t10 + ColorTable sampling + checksum
  shpk_patcher.py                     # Rebuild skin.shpk: replace PS[19] blob + add g_SamplerTable resource
  skin_patched.shpk                   # Output: patched skin.shpk (deployed via Penumbra at runtime)
  reference/                          # Disassembly reference (D3DCompiler output)
docs/                                 # Technical documentation
  development-notes.md                # Development pitfalls (CN environment, Penumbra IPC, threading, etc.)
  constant-buffer-analysis.md         # CBuffer memory layout and render pipeline analysis
  material-replacement-research.md    # PBR/metallic/petrify feasibility assessment
  pbr-material-research.md            # ColorTable row selection + Glamourer data flow
  pbr-material-design-decisions.md    # Brainstorming Q&A record
  route-c-ida-research.md             # IDA decompilation of vanilla shader package loading
  skin-uv-mesh-matching.md            # SkinMeshResolver design rationale (body mod UV matching strategy)
  skin-shpk-colortable-implementation.md  # skin.shpk + ColorTable implementation (IDA逆向 + DXBC patch + 验证)
```

## Key Conventions

- Namespace: `SkinTattoo.*` (e.g. `SkinTattoo.Mesh`, `SkinTattoo.Core`)
- UI language: Chinese
- Code comments: English only, at critical points
- Commit messages: no Co-Authored-By

## Dependencies

| Library | Purpose |
|---|---|
| Penumbra.Api (submodule) | Type-safe Penumbra IPC wrapper |
| Lumina | .mdl/.tex file parsing |
| StbImageSharp | Image loading |
| EmbedIO | HTTP debug server |
| SharpDX 4.2.0 | DX11 offscreen rendering (Direct3D11, D3DCompiler, DXGI, Mathematics) |

## Reference Projects (same directory)

| Project | Purpose |
|---|---|
| FFXIVClientStructs | Game struct definitions (CharacterBase, Material, ConstantBuffer, etc.) |
| Glamourer-CN | Material live-edit reference (ColorTable texture swap, PrepareColorSet hook) |
| Meddle | Material/texture read reference (OnRenderMaterial interception) |
| VFXEditor-CN | VFX editor reference |

## HTTP Debug API

The plugin serves a REST API at `http://localhost:12580/` after startup:

- `GET /api/status` — Plugin status
- `GET /api/project` — Current project JSON
- `POST /api/layer` — Add layer
- `PUT /api/layer/{id}` — Modify layer params
- `DELETE /api/layer/{id}` — Delete layer
- `POST /api/preview` — Trigger preview (auto-selects inplace/full)
- `POST /api/preview/full` — Force full redraw
- `POST /api/preview/inplace` — Force inplace swap
- `POST /api/mesh/load` — Load mesh
- `GET /api/mesh/info` — Mesh info
- `GET /api/log` — Recent log entries
- `POST /api/export` — Export mod (body: `{name, author?, version?, description?, target: "local"|"penumbra", outputPath?, groupIndices?: [int]}`)

## Core Pipeline

### First Preview (Full Redraw)
```
PNG image → CPU UV composite → write temp .tex/.mtrl → Penumbra temp mod → character redraw (flashes once)
```

### Subsequent Adjustments (GPU Swap, flicker-free)
```
Parameter change → async CPU composite (background thread) → atomic GPU texture swap (main thread, instant)
```

`TextureSwapService` directly manipulates `CharacterBase→Model→Material→TextureResourceHandle→Texture*` pointers.
Uses `Device.CreateTexture2D` + `InitializeContents` + `Interlocked.Exchange` for zero-flicker texture replacement.
`ReplaceColorTableRaw` / `UpdateEmissiveViaColorTable` scan **all** matching material slots and replace each one (same mtrl may be referenced by multiple Model slots).

### Mod Export
- `Services/ModExportService.cs` orchestrates export; results notified via Dalamud `INotificationManager`
- Reuses `PreviewService.CompositeForExport` entry point (visible layers only, no runtime state pollution)
- `Services/PmpPackageWriter.cs` packs staging dir into `.pmp` zip (meta.json + default_mod.json + game path mirror)
- Two paths: local save / `PenumbraBridge.InstallMod` IPC
- **install pmp path**: `<pluginConfigDir>/export_temp/install_pending.pmp`, fixed location, overwritten on next install, cleaned on `ModExportService.Dispose()`. Cannot delete immediately after IPC return — `InstallMod` is async-queued, Penumbra reads the file after IPC returns.

Decals composite directly in UV space: positioned by `UvCenter`/`UvScale`/`RotationDeg`, sampling each output texture pixel from the decal, alpha-blended onto the base texture.

Decal half-clip preprocessing: `ClipMode` (None/ClipLeft/ClipRight/ClipTop/ClipBottom) clips in decal-local space, solving the FFXIV mirrored texture problem.

### Emissive System

**Initialization path (executed once during Full Redraw):**
- `MtrlFileWriter` modifies .mtrl file:
  - Sets `CategorySkinType` shader key → `ValueEmissive` (0x72E697CD)
  - Writes `g_EmissiveColor` (0x38A64362) shader constant
  - Preserves original `AdditionalData` bytes (Lumina skips this field)
- Normal map alpha channel as emissive mask (UV-space composite)
- Records `emissiveOffset` (g_EmissiveColor byte offset in CBuffer)

**Real-time update path (per-frame, flicker-free):**
- `EmissiveCBufferHook` hooks `ModelRenderer.OnRenderMaterial`
- Modifies CBuffer data via `LoadSourcePointer` within the render pipeline
- UI color/intensity sliders call `TryDirectEmissiveUpdate()` directly, 1-frame latency
- Materials with ColorTable (character.shpk etc.) → ColorTable texture atomic swap (ref. Glamourer)
- skin.shpk materials (no ColorTable) → EmissiveCBufferHook real-time CBuffer update

**State cleanup:**
- Unchecking "emissive" or hiding a layer calls `InvalidateEmissiveForGroup()` which clears only that group's hook target (not all groups)
- `ResetSwapState()` clears all GPU swap and hook state

### Iris (Eye) Emissive System

Iris emissive is auto-detected when TargetGroup.MtrlGamePath contains `_iri_`.
Uses the same EmissiveCBufferHook (g_EmissiveColor CRC 0x38A64362 exists in iris.shpk).

**Full Redraw path:**
- `TryPatchEmissiveRaw` patches g_EmissiveColor + g_IrisRingEmissiveIntensity (0x7DABA471, default 0.25 → 1.0) in mtrl
- `CompositeIrisMask` composites decal shapes into mask texture red channel (emissive mask)
  - Loads vanilla mask via g_SamplerMask (CRC 0x8A4E82B6) from mtrl
  - Red channel = decal alpha × blue channel (iris area clip)
- Redirects patched mask + mtrl via Penumbra

**In-place swap path:**
- Re-composites iris mask on UV changes, GPU-swaps mask texture
- CBuffer hook updates g_EmissiveColor in real-time

**Key constraint:** iris emissive requires mask red channel ≠ 0. Vanilla eyes may have red=0; glow-compatible eye mods have it pre-set. Plugin auto-generates mask from blue channel.

### skin.shpk Emissive Limitation

skin.shpk has a single g_EmissiveColor CBuffer constant per material. All layers sharing the same body material share one emissive color (combined/accumulated). Per-layer independent emissive colors require shader swap (Route C) which is not implemented.

## Verified Technical Conclusions

### Material CBuffer can be live-updated via LoadSourcePointer inside OnRenderMaterial
- Calling LoadSourcePointer **outside** OnRenderMaterial: CPU write succeeds but GPU doesn't re-upload (render command already built)
- Calling LoadSourcePointer **inside** OnRenderMaterial: sets DirtySize + UnsafeSourcePointer, subsequent render submission reads updated data
- `ConstantBuffer.Flags` initial value is `0x4` (static), Buffer[0/1/2] point to CPU memory (not ID3D11Buffer)
- Detailed analysis in `docs/constant-buffer-analysis.md`

### skin.shpk materials have no ColorTable
- `DataSetSize = 0`, `HasColorTable = false`
- Emissive color is entirely in CBuffer shader constant `g_EmissiveColor`
- ColorTable texture swap is not applicable to skin.shpk

### Penumbra-redirected MaterialResourceHandle.FileName format
- Format: `|prefix_hash|disk_path.mtrl` (e.g. `|1_2_FE59D746_0893|C:/.../preview_gBD8558DD.mtrl`)
- No longer a game path; matching requires both game path and disk path

### character.shpk ColorTable row number is determined by normal.a
- 32-row ColorTable is actually 16 row pairs, indexed by normal map alpha channel
- Row formula: `tablePair = round(normal.a / 17)` → 0-15
- Intra-pair interpolation weight: `rowBlend = 1 - normal.g / 255`
- Both rows in a pair are lerped, all PBR fields (diffuse/specular/emissive/roughness/metalness) interpolate together
- Source: `Penumbra/Import/Models/Export/MaterialExporter.cs:136-149`
- This is the physical basis for "independent PBR per decal layer" — assign each layer an independent row pair, write the corresponding row index in normal.a during composite

### Vanilla engine includes charactertattoo.shpk
- Shader package string array @ `0x14206d3a0` (IDA: ffxiv_dx11.exe), 57 entries
- Contains `shader/sm5/shpk/charactertattoo.shpk` @ `0x14206dca8`
- Name suggests this is vanilla's own "character tattoo" shader, potentially the best conversion target for body material PBR (lighter than character.shpk)
- See `docs/route-c-ida-research.md`

### MaterialResourceHandle field offsets (key data beyond vtable)
- `+200` (0xC8) = ShaderPackage handle (QWORD)
- `+232` (0xE8) = ShaderPackage flags (DWORD, render branch bits `& 0x4000` / `& 0x8000`)
- Confirmed via IDA decompilation of OnRenderMaterial (sub_14026EE10), consistent with FFXIVClientStructs definitions

### Vanilla engine does fast-path pointer comparison on ShaderPackage
- Inside OnRenderMaterial, `material->ShaderPackage` is compared against 5 cached ShaderPackage pointers in ModelRenderer
- Different render flag branches
- **Implication**: switching a .mtrl's ShaderPackage to another valid shader causes the engine to automatically route via fast-path — no hook intervention needed, **just changing ShaderPackageName in the .mtrl file is enough to switch shaders**
- This is the basis for greatly simplifying Route C (skin.shpk → character.shpk conversion)

### ConstantBuffer Memory Layout (confirmed, 0x70 bytes)
```
+0x00  vtable pointer (ReferencedClassBase)
+0x18  int InitFlags (passed at creation)
+0x20  int ByteSize (16-byte aligned)
+0x24  int Flags (0x4 = static, 0x1 = dynamic, 0x4000 = GPU D3D11Buffer)
+0x28  void* UnsafeSourcePointer (set by LoadSourcePointer)
+0x30  int StagingSize
+0x34  int DirtySize (render submission reads this to determine upload size)
+0x38  vtable2 pointer
+0x50  void* Buffer[0] (triple-buffered CPU memory pointers / or ID3D11Buffer* when flags & 0x4000)
+0x58  void* Buffer[1]
+0x60  void* Buffer[2]
+0x68  void* StagingPtr
```
**Conclusion**: Material CBuffer (Flags=0x4) Buffer[] is CPU memory, not ID3D11Buffer*.
GPU buffers are created/uploaded on-demand by the render submission pipeline.

## EmissiveCBufferHook Implementation Details

**File**: `Interop/EmissiveCBufferHook.cs`

**Hook target**: `ModelRenderer.OnRenderMaterial` (signature `"E8 ?? ?? ?? ?? 44 0F B7 28"`)

**How it works**:
1. Hook detour executes **before** the original function call
2. Matches target material via `MaterialResourceHandle` pointer (ConcurrentDictionary, thread-safe)
3. Looks up `g_EmissiveColor` (CRC 0x38A64362) offset from `ShaderPackage.MaterialElements` (result cached)
4. Calls `LoadSourcePointer(offset, 12, 2)` to mark dirty and get writable pointer
5. Writes new RGB values
6. Calls original function → render submission reads updated data → GPU upload

**Thread safety**: `ConcurrentDictionary` ensures UI thread writes / render thread reads don't conflict

**Error handling**: Throttle mechanism, logs at most 5 errors to prevent log flooding

**Related IDA addresses**: see `docs/constant-buffer-analysis.md`

## 3D Decal Editor

Standalone ImGui window (`ModelEditorWindow`), DX11 offscreen rendering → `ImGui.ImageButton` display.

### Architecture
```
DxRenderer (offscreen render) → ShaderResourceView → ImGui.ImageButton
OrbitCamera (orbit camera) → View/Proj matrices
MeshBuffer (GPU buffers) ← MeshData (CPU)
RayPicker (ray picking) → UV coordinates → DecalLayer.UvCenter → composite pipeline
```

### Coordinate System Conventions
- **World matrix**: `Scaling(-1, 1, 1)` — FFXIV model X-axis mirrored
- **Camera default**: `Yaw = 0` — model faces camera
- **Mesh UV**: `uv = rawUv` — Meddle returns raw UV, used directly (FFXIV body model UV X is typically in [1,2], canvas maps to the right half of virtual square space via `uvScale`)
- **Ray picking**: Ray X negated (matches World X mirror), UV used directly (no flip)
- **Shader**: `output.uv = input.uv` (no flip)

### Multi-Model Support
- `TargetGroup.AllMeshPaths` merges primary model + additional model paths
- `TargetGroup.VisibleMeshPaths` excludes models in `HiddenMeshPaths`
- `MeshExtractor.ExtractAndMerge()` merges multiple .mdl files
- Auto-detects all Mdl sharing the same texture when importing projection targets
- Model selection from Penumbra resource tree list (not manual file dialog)
- Model management popup supports visibility toggle and removal
- Only loads meshes with `MaterialIdx == 0` (skips accessories/shadows etc.)
- Resolver fallback: when `SkinMeshResolver` fails (non-standard mtrl filenames like `_bibo`), uses `LiveMdls` from resource tree card to collect all mdl disk paths. Does NOT set `MeshGamePath` (would override `AllMeshPaths` in `LoadMeshForGroup`).
- "重新解析" preserves manually added `MeshDiskPaths` not covered by resolver results

### Controls
| Input | Action |
|-------|--------|
| Right-drag | Rotate camera |
| Middle-drag | Pan camera |
| Ctrl+Scroll | Camera zoom |
| Left-click model | Place decal |
| Scroll | Scale decal |

## In-Game Testing

1. `/xlsettings` → Dev Plugin Locations → add output directory
2. `/xlplugins` → enable SkinTattoo
3. `/skintattoo` → open editor
4. HTTP: `curl http://localhost:12580/api/status`
