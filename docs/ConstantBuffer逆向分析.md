# ConstantBuffer 逆向分析 — 实时 Emissive 颜色更新

> 基于 ffxiv_dx11.exe IDA 分析 (2026-04-06)，游戏版本 7.x

## 1. ConstantBuffer 内存布局 (0x70 bytes)

```
继承链: ConstantBuffer → Buffer → Resource → DelayedReleaseClassBase → ReferencedClassBase
```

| 偏移 | 大小 | 字段 | 说明 |
|------|------|------|------|
| +0x00 | 8 | vtable | ReferencedClassBase vtable (`off_14206AFA8`) |
| +0x08 | 8 | refcount etc. | 引用计数相关 |
| +0x10 | 8 | | 基类字段 |
| +0x18 | 4 | InitFlags | 创建时传入的 flags 低 4 位 |
| +0x20 | 4 | **ByteSize** | CBuffer 大小（16 字节对齐） |
| +0x24 | 4 | **Flags** | 缓冲区类型标志（见下表） |
| +0x28 | 8 | **UnsafeSourcePointer** | 当前活动的 CPU 数据指针（由 LoadSourcePointer 设置） |
| +0x30 | 4 | StagingSize | staging 缓冲区大小 |
| +0x34 | 4 | **DirtySize** | 脏区域大小（aligned），渲染提交时读取 |
| +0x38 | 8 | vtable2 | 内部 vtable (`off_14206AFD0`) |
| +0x40 | 8 | | 保留 |
| +0x48 | 8 | | 保留 |
| +0x50 | 8 | **Buffer[0]** | 三重缓冲指针 #0 |
| +0x58 | 8 | **Buffer[1]** | 三重缓冲指针 #1 |
| +0x60 | 8 | **Buffer[2]** | 三重缓冲指针 #2 |
| +0x68 | 8 | StagingPtr | staging 临时指针（GPU 路径使用） |

### Flags 含义

| Flags 位 | 含义 |
|----------|------|
| 0x0001 | Dynamic（三重缓冲，每个 Buffer[] 独立分配） |
| 0x0002 | Staging-only（不分配缓冲区，LoadSourcePointer 特殊处理） |
| 0x0004 | Static（一次上传，Buffer[0]=Buffer[1]=Buffer[2] 指向同一内存）|
| 0x0010 | GPU BindFlags 0x20000 |
| 0x0040 | GPU Usage DYNAMIC |
| 0x4000 | GPU D3D11Buffer（Buffer[] 存 ID3D11Buffer*，而非 CPU 内存）|

**材质 CBuffer (skin.shpk) 的 Flags = 0x4 (static)**：
- Buffer[0/1/2] 都指向同一块 CPU 内存
- **没有 ID3D11Buffer** 存在于 ConstantBuffer 结构体中
- GPU 缓冲区由渲染提交管线按需创建和上传

### Buffer[] 指针内容取决于 Flags

| Flags | Buffer[0/1/2] 内容 |
|-------|-------------------|
| 无 0x4000 | CPU 堆内存指针 |
| 有 0x4000 + 0x10/0x40 | ID3D11Buffer* |
| 有 0x4000 无 0x50 | GPU 缓冲区池分配 |

## 2. 关键函数

### CreateConstantBuffer — `sub_140214640`

```
调用签名（从 CLAUDE.md）: "E8 ?? ?? ?? ?? 48 89 47 ?? B0"
地址: 调用在 0x1403388fc，函数本体 sub_140214640
```

- 分配 0x70 字节对象
- 调用 `sub_14020D4C0(obj, size, flags)` 初始化缓冲区
- 根据 flags 分配 CPU 内存或 D3D11Buffer

### LoadSourcePointer — `sub_14020D9A0`

```
C# 签名: "E8 ?? ?? ?? ?? 45 0F B6 FC 48 85 C0"
原型: void* LoadSourcePointer(int byteOffset, int byteSize, byte flags = 2)
```

**作用**：获取可写 CPU 指针，并标记脏区域。

对于 Flags=0x4（静态材质 CBuffer）的流程：
1. 检查 `(Flags & 0x4003) == 0` → 通过
2. 检查 `(callFlags & 1) == 0` → 通过（默认 flags=2）
3. 检查 `(Flags & 0x4002) == 0` → 进入 CPU 路径
4. 设置 `DirtySize (+0x34) = ByteSize`（标记整个缓冲区为脏）
5. 获取 `Buffer[frameIndex] (+0x50/+0x58/+0x60)`
6. 设置 `UnsafeSourcePointer (+0x28) = Buffer[frameIndex]`
7. 返回 `Buffer[frameIndex] + byteOffset`

**帧索引**：`dword_1427F9474` 全局变量，循环 0/1/2。

### 渲染提交 — `sub_140229A10`

在 OnRenderMaterial 中被调用，构建渲染命令：

```c
// 对每个 CBuffer slot:
command.dataPtr = cbuf->UnsafeSourcePointer;  // +0x28
command.size = min(expected_size, cbuf->DirtySize >> 4);  // +0x34
```

渲染线程后续处理命令时，从 `dataPtr` 读取 CPU 数据并上传到 GPU。

**关键结论**：如果在 OnRenderMaterial 执行期间（渲染命令构建之前）修改 CPU 数据，
渲染提交会读取到更新后的数据并上传到 GPU。

## 3. OnRenderMaterial 函数

```
地址: sub_14026F790
签名: "E8 ?? ?? ?? ?? 44 0F B7 28"（通过内部调用匹配）
C# 声明: ModelRenderer.OnRenderMaterial(ushort* outFlags, OnRenderModelParams* param, Material* material, uint materialIndex)
```

### 执行流程

1. 从参数获取 Material、ShaderPackage 等信息
2. 处理 shader keys/values 替换
3. 绑定 CBuffer 到渲染上下文 slot：
   ```c
   renderContext.CBufferSlots[slotIndex] = material->CBuffer;
   ```
4. 调用 `sub_140229A10` 构建渲染命令（读取 CBuffer 数据）
5. 处理 draw call 参数
6. 返回

### Hook 时机

在步骤 4 之前修改 CBuffer 数据 = 实时生效。

## 4. 材质 → CBuffer → Emissive 的访问路径

```
Material (+0x10) → MaterialResourceHandle
    (+0xC8) → ShaderPackageResourceHandle
        ->ShaderPackage
            .MaterialElements[] → { CRC, Offset, Size }
                CRC == 0x38A64362 → g_EmissiveColor 的 Offset

Material (+0x28) → ConstantBuffer* (MaterialParameterCBuffer)
    (+0x50) → CPU Buffer 数据
        [Offset]     = float R
        [Offset + 4] = float G  
        [Offset + 8] = float B
```

### ShaderPackage.MaterialElement 结构

```c
struct MaterialElement {  // 8 bytes
    uint CRC;       // 着色器常量名的 CRC32
    ushort Offset;  // 在 CBuffer 中的字节偏移
    ushort Size;    // 大小（字节）
};
```

### 重要 CRC 值

| CRC | 名称 | 大小 | 说明 |
|-----|------|------|------|
| 0x38A64362 | g_EmissiveColor | 12 (3 floats) | 发光颜色 RGB |
| 0x380CAED0 | CategorySkinType | - | Shader key: 皮肤类型 |
| 0x72E697CD | ValueEmissive | - | Shader key value: 启用发光 |

## 5. 实时更新方案

### 方案 A: OnRenderMaterial Hook + LoadSourcePointer（已实现）

```
Hook ModelRenderer.OnRenderMaterial
  ↓
识别目标材质 (比较 MaterialResourceHandle 路径)
  ↓
获取 Material->MaterialParameterCBuffer
  ↓
从 ShaderPackage.MaterialElements 查找 g_EmissiveColor 偏移
  ↓
调用 LoadSourcePointer(offset, 12, 2) 获取可写指针 + 标记脏
  ↓
写入新的 RGB 值
  ↓
调用原始函数 → 渲染管线读取更新后的数据 → GPU 上传
```

**优势**：
- 在渲染管线内部修改，确保数据被提交到 GPU
- LoadSourcePointer 标记 DirtySize，渲染提交会包含新数据
- 不需要找到 ID3D11Buffer*

**限制**：
- 每帧每次材质渲染都会调用 hook（性能开销小但存在）
- 需要正确识别目标材质

### 方案 B: 直接 D3D11 Map/Unmap（备选）

如果方案 A 不生效（可能 DirtySize 被忽略），则需要：
1. 从渲染上下文获取 D3D11DeviceContext
2. 找到绑定的 D3D11Buffer（需要进一步逆向渲染线程命令处理函数）
3. 直接 Map/Unmap 更新 GPU 数据

### 为什么之前在 OnRenderMaterial 外调用 LoadSourcePointer 无效

1. 材质 CBuffer Flags=0x4（static），初始化后 Buffer[] 指向 CPU 内存
2. 渲染命令在 OnRenderMaterial 中构建，读取 `UnsafeSourcePointer` 和 `DirtySize`
3. 如果在 OnRenderMaterial **之外** 修改：
   - DirtySize 可能已被渲染线程清零
   - UnsafeSourcePointer 可能为 null（未被 LoadSourcePointer 设置）
   - 即使数据被修改，当前帧的渲染命令可能已经构建完成
4. 在 OnRenderMaterial **之内** 修改：
   - LoadSourcePointer 重新设置 UnsafeSourcePointer 和 DirtySize
   - 渲染命令还没有构建（在原始函数中构建）
   - 修改后的数据会被正确提交

## 6. Glamourer 的 ColorTable 方案（对比参考）

Glamourer 通过 hook `PrepareColorSet` 拦截 ColorTable 纹理准备：
- 签名: `"E8 ?? ?? ?? ?? 49 89 04 ?? 49 83 C5"`
- 读取 `MaterialResourceHandle->DataSet` 中的 ColorTable 数据
- 修改颜色行后创建新的 `R16G16B16A16_FLOAT` 纹理
- 原子替换 `CharacterBase->ColorTableTextures[]`

**局限**：skin.shpk 材质 `HasColorTable = false`，`DataSetSize = 0`，此方案不适用。

## 7. 相关 IDA 地址速查

| 功能 | 地址 | 签名 |
|------|------|------|
| CreateConstantBuffer | sub_140214640 | (caller) `E8 ?? ?? ?? ?? 48 89 47 ?? B0` |
| InitBuffer | sub_14020D4C0 | - |
| LoadSourcePointer | sub_14020D9A0 | `E8 ?? ?? ?? ?? 45 0F B6 FC 48 85 C0` |
| OnRenderMaterial | sub_14026F790 | `E8 ?? ?? ?? ?? 44 0F B7 28` |
| RenderSubmit | sub_140229A10 | - |
| Device global | qword_1427F0480 | - |
| Frame index | dword_1427F9474 | - |
