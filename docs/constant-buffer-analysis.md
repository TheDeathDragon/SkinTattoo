# ConstantBuffer Reverse Engineering Analysis — Real-time Emissive Color Updates

> Based on IDA analysis of ffxiv_dx11.exe (2026-04-06), game version 7.x

## 1. ConstantBuffer Memory Layout (0x70 bytes)

```
Inheritance chain: ConstantBuffer → Buffer → Resource → DelayedReleaseClassBase → ReferencedClassBase
```

| Offset | Size | Field | Description |
|--------|------|-------|-------------|
| +0x00 | 8 | vtable | ReferencedClassBase vtable (`off_14206AFA8`) |
| +0x08 | 8 | refcount etc. | Reference count related fields |
| +0x10 | 8 | | Base class fields |
| +0x18 | 4 | InitFlags | Low 4 bits of flags passed at creation |
| +0x20 | 4 | **ByteSize** | CBuffer size (16-byte aligned) |
| +0x24 | 4 | **Flags** | Buffer type flags (see table below) |
| +0x28 | 8 | **UnsafeSourcePointer** | Currently active CPU data pointer (set by LoadSourcePointer) |
| +0x30 | 4 | StagingSize | Staging buffer size |
| +0x34 | 4 | **DirtySize** | Dirty region size (aligned), read during render submission |
| +0x38 | 8 | vtable2 | Internal vtable (`off_14206AFD0`) |
| +0x40 | 8 | | Reserved |
| +0x48 | 8 | | Reserved |
| +0x50 | 8 | **Buffer[0]** | Triple-buffer pointer #0 |
| +0x58 | 8 | **Buffer[1]** | Triple-buffer pointer #1 |
| +0x60 | 8 | **Buffer[2]** | Triple-buffer pointer #2 |
| +0x68 | 8 | StagingPtr | Staging temporary pointer (used by GPU path) |

### Flags Meanings

| Flags bit | Meaning |
|-----------|---------|
| 0x0001 | Dynamic (triple-buffered, each Buffer[] independently allocated) |
| 0x0002 | Staging-only (no buffer allocated, LoadSourcePointer handles specially) |
| 0x0004 | Static (uploaded once, Buffer[0]=Buffer[1]=Buffer[2] point to the same memory) |
| 0x0010 | GPU BindFlags 0x20000 |
| 0x0040 | GPU Usage DYNAMIC |
| 0x4000 | GPU D3D11Buffer (Buffer[] stores ID3D11Buffer*, not CPU memory) |

**Material CBuffer (skin.shpk) Flags = 0x4 (static)**:
- Buffer[0/1/2] all point to the same CPU memory block
- **No ID3D11Buffer** exists in the ConstantBuffer struct
- GPU buffer is created and uploaded on demand by the render submission pipeline

### Buffer[] Pointer Contents Depend on Flags

| Flags | Buffer[0/1/2] Contents |
|-------|------------------------|
| No 0x4000 | CPU heap memory pointer |
| Has 0x4000 + 0x10/0x40 | ID3D11Buffer* |
| Has 0x4000, no 0x50 | GPU buffer pool allocation |

## 2. Key Functions

### CreateConstantBuffer — `sub_140214640`

```
Call signature (from CLAUDE.md): "E8 ?? ?? ?? ?? 48 89 47 ?? B0"
Address: call site at 0x1403388fc, function body sub_140214640
```

- Allocates 0x70 byte object
- Calls `sub_14020D4C0(obj, size, flags)` to initialize the buffer
- Allocates CPU memory or D3D11Buffer based on flags

### LoadSourcePointer — `sub_14020D9A0`

```
C# signature: "E8 ?? ?? ?? ?? 45 0F B6 FC 48 85 C0"
Prototype: void* LoadSourcePointer(int byteOffset, int byteSize, byte flags = 2)
```

**Purpose**: Obtains a writable CPU pointer and marks the dirty region.

Flow for Flags=0x4 (static material CBuffer):
1. Check `(Flags & 0x4003) == 0` → pass
2. Check `(callFlags & 1) == 0` → pass (default flags=2)
3. Check `(Flags & 0x4002) == 0` → enter CPU path
4. Set `DirtySize (+0x34) = ByteSize` (mark entire buffer as dirty)
5. Get `Buffer[frameIndex] (+0x50/+0x58/+0x60)`
6. Set `UnsafeSourcePointer (+0x28) = Buffer[frameIndex]`
7. Return `Buffer[frameIndex] + byteOffset`

**Frame index**: `dword_1427F9474` global variable, cycles 0/1/2.

### Render Submission — `sub_140229A10`

Called within OnRenderMaterial to build render commands:

```c
// For each CBuffer slot:
command.dataPtr = cbuf->UnsafeSourcePointer;  // +0x28
command.size = min(expected_size, cbuf->DirtySize >> 4);  // +0x34
```

The render thread then processes these commands, reading CPU data from `dataPtr` and uploading to GPU.

**Key conclusion**: If CPU data is modified during OnRenderMaterial execution (before the render command is built), the render submission will read the updated data and upload it to the GPU.

## 3. OnRenderMaterial Function

```
Address: sub_14026F790
Signature: "E8 ?? ?? ?? ?? 44 0F B7 28" (matched via internal call)
C# declaration: ModelRenderer.OnRenderMaterial(ushort* outFlags, OnRenderModelParams* param, Material* material, uint materialIndex)
```

### Execution Flow

1. Obtain Material, ShaderPackage, etc. from parameters
2. Process shader key/value substitutions
3. Bind CBuffer to render context slot:
   ```c
   renderContext.CBufferSlots[slotIndex] = material->CBuffer;
   ```
4. Call `sub_140229A10` to build render commands (reads CBuffer data)
5. Process draw call parameters
6. Return

### Hook Timing

Modifying CBuffer data before step 4 = takes effect immediately in the current frame.

## 4. Material → CBuffer → Emissive Access Path

```
Material (+0x10) → MaterialResourceHandle
    (+0xC8) → ShaderPackageResourceHandle
        ->ShaderPackage
            .MaterialElements[] → { CRC, Offset, Size }
                CRC == 0x38A64362 → Offset of g_EmissiveColor

Material (+0x28) → ConstantBuffer* (MaterialParameterCBuffer)
    (+0x50) → CPU Buffer data
        [Offset]     = float R
        [Offset + 4] = float G  
        [Offset + 8] = float B
```

### ShaderPackage.MaterialElement Structure

```c
struct MaterialElement {  // 8 bytes
    uint CRC;       // CRC32 of the shader constant name
    ushort Offset;  // Byte offset within the CBuffer
    ushort Size;    // Size in bytes
};
```

### Important CRC Values

| CRC | Name | Size | Description |
|-----|------|------|-------------|
| 0x38A64362 | g_EmissiveColor | 12 (3 floats) | Emissive color RGB |
| 0x380CAED0 | CategorySkinType | - | Shader key: skin type |
| 0x72E697CD | ValueEmissive | - | Shader key value: enable emissive |

## 5. Real-time Update Approaches

### Approach A: OnRenderMaterial Hook + LoadSourcePointer (implemented)

```
Hook ModelRenderer.OnRenderMaterial
  ↓
Identify target material (compare MaterialResourceHandle path)
  ↓
Get Material->MaterialParameterCBuffer
  ↓
Look up g_EmissiveColor offset from ShaderPackage.MaterialElements
  ↓
Call LoadSourcePointer(offset, 12, 2) to get writable pointer + mark dirty
  ↓
Write new RGB values
  ↓
Call original function → render pipeline reads updated data → GPU upload
```

**Advantages**:
- Modification happens inside the render pipeline, ensuring data is submitted to the GPU
- LoadSourcePointer marks DirtySize, so render submission will include the new data
- No need to locate the ID3D11Buffer*

**Limitations**:
- Hook is called every frame for every material render (minor but present performance overhead)
- Target material must be correctly identified

### Approach B: Direct D3D11 Map/Unmap (fallback)

If Approach A does not work (e.g., DirtySize is ignored), the alternative is:
1. Obtain D3D11DeviceContext from the render context
2. Find the bound D3D11Buffer (requires further reverse engineering of the render thread command processing function)
3. Directly Map/Unmap to update GPU data

### Why Calling LoadSourcePointer Outside OnRenderMaterial Has No Effect

1. Material CBuffer Flags=0x4 (static), after initialization Buffer[] points to CPU memory
2. Render commands are built inside OnRenderMaterial, reading `UnsafeSourcePointer` and `DirtySize`
3. If modified **outside** OnRenderMaterial:
   - DirtySize may already have been cleared to zero by the render thread
   - UnsafeSourcePointer may be null (not set by LoadSourcePointer)
   - Even if the data is modified, the current frame's render commands may already be built
4. If modified **inside** OnRenderMaterial:
   - LoadSourcePointer re-sets UnsafeSourcePointer and DirtySize
   - Render commands have not been built yet (they are built in the original function)
   - The modified data will be correctly submitted

## 6. Glamourer's ColorTable Approach (Comparison Reference)

Glamourer intercepts ColorTable texture preparation by hooking `PrepareColorSet`:
- Signature: `"E8 ?? ?? ?? ?? 49 89 04 ?? 49 83 C5"`
- Reads ColorTable data from `MaterialResourceHandle->DataSet`
- After modifying color rows, creates a new `R16G16B16A16_FLOAT` texture
- Atomically replaces `CharacterBase->ColorTableTextures[]`

**Limitation**: skin.shpk materials have `HasColorTable = false` and `DataSetSize = 0`, so this approach does not apply.

## 7. IDA Address Quick Reference

| Function | Address | Signature |
|----------|---------|-----------|
| CreateConstantBuffer | sub_140214640 | (caller) `E8 ?? ?? ?? ?? 48 89 47 ?? B0` |
| InitBuffer | sub_14020D4C0 | - |
| LoadSourcePointer | sub_14020D9A0 | `E8 ?? ?? ?? ?? 45 0F B6 FC 48 85 C0` |
| OnRenderMaterial | sub_14026F790 | `E8 ?? ?? ?? ?? 44 0F B7 28` |
| RenderSubmit | sub_140229A10 | - |
| Device global | qword_1427F0480 | - |
| Frame index | dword_1427F9474 | - |
