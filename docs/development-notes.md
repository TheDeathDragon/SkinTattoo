# SkinTattoo Development Notes

> This is a "lessons learned" memo documenting details that **you can only discover by running into them**. Written for newcomers; does not repeat what is already covered in the API docs.

## Table of Contents
- [CN (China) Environment](#cn-china-environment)
- [Penumbra IPC](#penumbra-ipc)
- [.tex File Format](#tex-file-format)
- [Compositing vs Projection](#uv-space-decal-vs-3d-projection)
- [In-Game Texture Dimensions](#in-game-texture-dimensions)
- [Threading Model / State Isolation](#threading-model--state-isolation)
- [Mod Export](#mod-exportpmp-packaging)

## CN (China) Environment

### Dalamud SDK Path
- The CN client uses `XIVLauncherCN`, path: `%AppData%\XIVLauncherCN\addon\Hooks\dev\`
- Not the international `XIVLauncher\addon\Hooks\dev\`
- Set `DALAMUD_HOME` to the correct path via `Directory.Build.props`

### Lumina Version Conflict
- **Dalamud CN ships with Lumina 2.4.2** — do not reference any other version via NuGet
- NuGet `Lumina 4.*` causes `IDataManager.GetFile<MdlFile>()` to return null (type mismatch)
- Using the `AtmoOmen/Lumina` fork as a submodule resolves `.tex` file decoding

### SqPack Read Limitations
- `IDataManager.FileExists()` returns false for CN-client `.mdl` and `.tex` files
- `.mtrl` files can be read normally
- Reason: the CN SqPack's Model/Texture file encoding format may be incompatible with Lumina 2.4.2
- **Solution**: use Meddle's SqPack reader (`Meddle.Utils.Files.SqPack`), which has its own Model/Texture file parsing implementation

### Extracting Mesh Data from Meddle
- `Meddle.Utils.Export.Model` correctly handles vertex declarations, position/normal/UV parsing
- FFXIV **UV V coordinates are negative** (range [-1, 0]) and must be negated: `uv.Y = -rawUv.Y`
- Skipping the negation causes `PositionMapGenerator` to produce an empty position map (0 valid pixels)

## Penumbra IPC

### Temporary Mod Approach
- **Do not use** `CreateTemporaryCollection` + `AssignTemporaryCollection`
  - This **replaces** the player's entire Penumbra collection, disabling all existing mods (e.g. Eve body mod)
- **Use** `AddTemporaryModAll` instead
  - Overlays globally without affecting existing mod collections
  - Adds temporary file redirects on top of all existing collections

### Path Format Returned by GetPlayerResources
- Returns `Dictionary<ushort, Dictionary<string, HashSet<string>>>`
- Format: `objectIndex → diskPath → Set<gamePath>`
- **Note**: after applying a temporary mod, the disk path may become our own output file (preview.tex), creating a self-reference
- Cache the original paths before creating the temporary mod

### Special Path Behaviour for Body Mods like Eve
- Eve does not use the standard path `chara/human/cXXXX/obj/body/b0001/texture/...`
- Eve uses custom paths such as `chara/nyaughty/eve/gen3_raen_base.tex`
- `.mtrl` material files reference these custom paths
- Do not hardcode race codes to locate them; let the user select manually in the debug window

## .tex File Format

### Header (80 bytes)
- offset 0: `uint32` attributes — must include `0x00800000` (TextureType2D)
- offset 4: `uint32` format — `0x1450` = B8G8R8A8
- offset 8: `uint16` width
- offset 10: `uint16` height
- offset 12: `uint16` depth (1)
- offset 14: `uint16` mip count (1)
- offset 24: `uint32` surface 0 offset (80)

### Common Mistakes
- Writing 0 for attributes causes the game to not display the texture (appears transparent)
- Pixel byte order must be **BGRA** (not RGBA); format 0x1450 corresponds to B8G8R8A8
- Use `FileShare.Read` when writing the file to prevent file locking when Penumbra/the game reads it

### .tex Decoding
- Use `Lumina.GameData.GetFileFromDisk<TexFile>(path)` to decode BC-compressed .tex files
- `TexFile.ImageData` returns B8G8R8A8 data; you need to manually convert it to RGBA

## UV Space Decal vs 3D Projection

### Problems with 3D Projection
- Orthographic projection into UV space causes decal fragmentation — because UV islands are scattered
- A continuous 3D region may be spread across multiple UV islands
- The result looks like a "shattered sticker"

### UV Space Direct Compositing (Current Approach)
- Place the decal image directly in UV coordinate space
- Parameters: UV center, UV size, rotation angle
- Simple, precise, no fragmentation
- Similar to placing artwork on a UV unwrap in Photoshop
- Drawback: users need to understand the UV layout (assisted by the texture preview)

## In-Game Texture Dimensions
- Vanilla body texture: 1024x1024 or 1024x2048
- HD mods such as Eve: 4096x4096
- Output resolution should match the base texture or be configurable
- The base texture needs to be scaled to the map resolution (nearest-neighbour interpolation is sufficient)

## Threading Model / State Isolation

### Concurrency Constraints in `PreviewService`
- `UpdatePreviewFull` runs on the main thread; `StartAsyncInPlace` runs on a background `Task.Run`; `CompositeForExport` is also called via background `Task.Run`
- All three paths share `baseTextureCache` / `compositeResults` / `previewDiskPaths` / `previewMtrlDiskPaths` / `emissiveOffsets` / `initializedRedirects`
- **Must use `ConcurrentDictionary`** — using a plain `Dictionary` previously caused intermittent `InvalidOperationException` when sliding sliders, and live preview / export contaminating each other's state
- `EmissiveCBufferHook.targets` / `offsetCache` are also `ConcurrentDictionary` because the detour runs on the render thread while `SetTargetByPath` runs on the main thread

### Lumina Temp Files Must Not Use Fixed Filenames
- `TryBuildEmissiveMtrl` / `LoadGameTexture` write SqPack byte streams to `outputDir/temp_*.mtrl|tex` and then call `Lumina.GetFileFromDisk`
- **Must use a GUID suffix** (`temp_{Guid:N}.mtrl`); otherwise the main thread and background thread calling simultaneously will write to the same file, overwriting each other and causing parse corruption

### `EmissiveCBufferHook.Dispose` Order
- Call `Disable()` before `hook.Dispose()`; otherwise `hook.Dispose()` may encounter an already-cleared dictionary on a new thread executing the detour
- The rate-limit counter `errorCount` does not reset automatically — after the first 5 errors, logging goes silent; if "no new errors appear" during debugging, restart the plugin first

## Mod Export (.pmp Packaging)

### `InstallMod` IPC is Asynchronous
- `Penumbra.Api.IpcSubscribers.InstallMod` returning `Success` only means the request has been **queued**; Penumbra actually reads the .pmp file on a worker thread **after** the IPC call returns
- Therefore the .pmp for "install to Penumbra" **must not** be written to a temp file and immediately deleted
- **Solution**: write to the fixed path `<pluginConfigDir>/export_temp/install_pending.pmp`, which is overwritten on the next install and cleaned up in `ModExportService.Dispose()`. Also clean up in the constructor to handle leftovers from a previous crash

### `default_mod.json` Field Set
- Penumbra's deserializer tolerates missing fields, but **omitting the composite fields `FileSwaps` / `Manipulations` causes them to use wrong defaults**
- Full field list: `Version`, `Name`, `Description`, `Priority`, `Files`, `FileSwaps`, `Manipulations`
- Keys in `Files` are game paths; values are paths relative to the mod root — **use forward slashes on both sides** (even on Windows)

### HTTP `/api/export` Must Be Wrapped in `Task.Run`
- `ModExportService.Export` is synchronous; compositing + writing the zip takes several seconds
- `await`-ing it directly in the HTTP endpoint **blocks the EmbedIO listener thread**, causing all other HTTP requests to hang during that time
- Fix: `var result = await Task.Run(() => _exportService.Export(options));`
