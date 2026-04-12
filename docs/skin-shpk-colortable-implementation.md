# skin.shpk + ColorTable 自制实现方案

> 研究日期：2026-04-13 ~ 2026-04-14。
> 基于 `skin-to-character-shader-swap-research.md` 的全部结论。
> 新增内容：IDA 逆向引擎 ColorTable 纹理创建路径、ShpkFile 二进制格式完整解析、DXBC 字节码修改策略。
> 2026-04-14 更新：DXBC patcher + shpk patcher + MtrlFileWriter ColorTable 注入 + per-layer 独立发光全部完成并在游戏中验证通过。

## 一、核心发现（IDA 逆向）

### 1.1 引擎不检查 shader 是否使用 ColorTable

`PrepareColorTable`（0x140417730）只检查 mtrl 的 `HasColorTable` 标志（`AdditionalData[0] & 4`）和 `DataSetSize > 0`。如果条件满足，引擎**无条件**创建 GPU 纹理：

```
if (AdditionalData[0] & 4) == 0 → return null  // no ColorTable flag
if (DataSetSize == 0)           → return null  // no data

// Detect layout
if (widthLog != 0) → width = 1 << widthLog, height = 1 << heightLog
else               → width = 4, height = 16 (Legacy)

// Upconvert Legacy → Dawntrail
if (width == 4) → expand 4-vec4 rows to 8-vec4 rows (512B → 2048B)

// Dawntrail pass-through
if (width == 8 && height == 32) → use data directly

// Create R16G16B16A16_FLOAT texture (8 × 32)
texture = Device.CreateTexture2D(...)
UploadData(texture, expandedData)
return texture
```

**结论：只要在 skin.shpk 的 mtrl 中设置 `HasColorTable=true` + 写入 ColorTable 数据，引擎就会创建 GPU 纹理。不需要修改引擎。**

### 1.2 g_SamplerTable 在 CharacterBase 初始化时解析

`sub_140412A40`（CharacterBase 初始化）调用 `sub_140224D90` 将字符串名解析为 slot ID：

```c
a1[1320 / 8] = ResolveSlotID(samplerRegistry, "g_SamplerTable");
```

这个 slot ID 用于渲染提交时将 ColorTable 纹理绑定到 shader 的 texture register。

**结论：引擎的纹理绑定系统是数据驱动的——只要 shader 的资源列表中声明了 g_SamplerTable，引擎就会自动将 ColorTable 纹理绑定到对应的 register slot。**

### 1.3 OnRenderMaterial ShaderPackage Fast-Path

`sub_14026EE10`（OnRenderMaterial）比较 `material->ShaderPackage` 指针与 5 个缓存指针：
- `a1+536` → 第一个 ShaderPackage（推测 character.shpk）
- `a1+544` → 第二个
- `a1+552`, `a1+560` → 第三、四个
- `a1+568` → 第五个

不同的 ShaderPackage 走不同的渲染标志位分支。**替换 skin.shpk 本身（而非在 mtrl 中改 ShaderPackageName）可以保持 fast-path 行为不变。**

---

## 二、.shpk 二进制格式（Penumbra + VFXEditor 源码确认）

### 2.1 变体选择机制

```
MaterialKey values × SceneKey values × SystemKey values × SubViewKey values
                    ↓ (polynomial hash, multiplier=31)
                  Selector (uint32)
                    ↓ (lookup in NodeSelectors dictionary)
                  Node index
                    ↓
                  Pass[subViewIndex]
                    ↓
                  { VertexShader index, PixelShader index }
```

每个 Node 包含：
- `Selector`（uint32）：由所有 key 值的多项式哈希计算
- `PassIndices[16]`：SubView × 8 的索引表
- 各类 key 的当前值
- `Passes[]`：每个 pass 包含 VS index + PS index

### 2.2 Selector 计算公式

```csharp
uint BuildSelector(uint sysKey, uint sceneKey, uint matKey, uint subViewKey)
{
    return sysKey + sceneKey * 31 + matKey * 961 + subViewKey * 29791;
}
```

### 2.3 关键常量

| 名称 | CRC32 | 说明 |
|------|-------|------|
| CategorySkinType | 0x380CAED0 | skin.shpk 的 MaterialKey |
| ValueEmissive | 0x72E697CD | EMISSIVE 变体值 |
| g_SamplerTable | 0x2005679F | ColorTable 采样器 |
| g_SamplerNormal | 0x0C5EC1F1 | 法线贴图采样器 |
| g_SamplerDiffuse | 0x115306BE | 漫反射贴图采样器 |
| g_EmissiveColor | 0x38A64362 | 发光颜色常量 |

---

## 三、实现方案

### 3.0 总体策略

**创建修改版 skin.shpk**，通过 Penumbra 临时 mod 替换原版。修改内容：

1. 添加新的 MaterialKey 值 `ValueColorTable`
2. 添加新的 PS 变体（基于 EMISSIVE PS 修改）
3. 在新 PS 的资源列表中添加 `g_SamplerTable`
4. 修改 DXBC 字节码，让新 PS 从 ColorTable 纹理读取 per-row emissive
5. 添加对应的 Node 条目映射新 key 值到新 PS

### 3.1 新增 MaterialKey 值

在 `CategorySkinType`（0x380CAED0）下定义新值：

```
ValueColorTable = 0xSKINTATT  // 选择一个不与 vanilla/ALum 冲突的 CRC32
```

现有值：
- Body = 0x2BDB45F1
- Face = 0xF5673524
- BodyJJM (HRO) = 0x57FF3B64
- Emissive = 0x72E697CD

### 3.2 新增 PS 变体（DXBC 修改）

基于 PS[19]（EMISSIVE lighting pass），需要修改 DXBC：

**A. 添加资源声明**（SHEX chunk 头部）：
```hlsl
dcl_sampler s5, mode_default                              // new: ColorTable sampler
dcl_resource_texture2d (float,float,float,float) t10      // new: ColorTable texture
```

**B. 修改 emissive 计算**（替换原版 lines 267-269）：

原版：
```
sample_b t5.zxwy, s1 → r0.xz  (r0.z = normal.alpha = emissive mask)
mul r1.xyz, cb0[3], cb0[3]     (g_EmissiveColor²)
mul r1.xyz, r0.z, r1           (emissive = mask * color²)
```

新版：
```
sample_b t5.zxwy, s1 → r0.xz  (r0.z = normal.alpha = row index)
// Convert normal.alpha to row UV: rowUV = (r0.z * 31.0 + 0.5) / 32.0
mad rN.y, r0.z, 31.0, 0.5
mul rN.y, rN.y, 0.03125        // 1/32
// Sample emissive from ColorTable row: column 2 (emissive R/G/B at vec4 index 2)
mov rN.x, 0.3125               // (2 + 0.5) / 8 = column for emissive
sample t10, rN.xy, s5 → rTemp  // sample ColorTable
mov r1.xyz, rTemp.xyz           // emissive color from ColorTable
// Keep the rest of the shader identical
```

**C. 更新 RDEF chunk**：添加 g_SamplerTable 资源绑定。

**D. 更新 DXBC checksum**。

### 3.3 更新 Node 映射

对于每个现有的 EMISSIVE Node（CategorySkinType = ValueEmissive），克隆一份并：
- 修改 MaterialKey 值为 ValueColorTable
- 修改 Pass 中的 PS index 为新 PS 变体的 index
- 重新计算 Selector

### 3.4 .shpk 重建流程

```
1. parse_shpk(vanilla_skin.shpk)
2. 克隆 EMISSIVE PS 变体 → 修改 DXBC → 添加为新 PS
3. 在新 PS 的 Samplers 列表添加 g_SamplerTable
4. 克隆 EMISSIVE Node 条目 → 修改 key 值和 PS index
5. 重新计算所有 Selector
6. write_shpk(modified_skin.shpk)
```

### 3.5 Plugin 集成管线

```
Full Redraw:
  1. MtrlFileWriter 设置 CategorySkinType = ValueColorTable
  2. MtrlFileWriter 设置 HasColorTable = true, 写入 ColorTable 数据
  3. CompositeNormalAlpha: normal.alpha = row pair index (不再是 emissive mask)
  4. ColorTableBuilder.Build() 生成 modified ColorTable (per-layer emissive/PBR)
  5. Penumbra redirect: modified skin.shpk + modified mtrl + textures
  6. Character redraw → 引擎加载新 shader + 创建 ColorTable 纹理

In-place Update:
  1. ColorTableBuilder.Build() with updated layer params
  2. TextureSwapService.ReplaceColorTableRaw() 原子替换 GPU 纹理
  3. EmissiveCBufferHook 可选：保留 CBuffer 兜底（全局 emissive 颜色叠加）
```

---

## 四、DXBC 修改策略（已实现）

### 4.1 采用方案 C（混合方案）

保留原 DXBC 大部分不变，仅 patch emissive 计算区域 + 添加声明。已验证通过。

实现步骤：
1. 解析 DXBC 容器（RDEF/ISGN/OSGN/SHEX/STAT 5 个 chunks）
2. 在 SHEX chunk 声明区末尾注入 `dcl_sampler s5` + `dcl_resource_texture2d t10`
3. 通过 byte pattern matching 定位 emissive 的 `mul cb0[3]*cb0[3]` + `mul r0.z*r1` 指令对
4. 替换为 `mad` + `mov` + `sample` 三条 ColorTable 采样指令
5. 重建 DXBC 容器（更新 chunk offsets、total size、SHEX token count）
6. 用自定义 padding MD5 算法重算 DXBC 校验和
7. 通过 D3DCompiler `D3DDisassemble` 端到端验证

**注意**：RDEF chunk 和 STAT chunk 暂未修改。D3DDisassemble 不严格校验 RDEF 与 SHEX 的一致性，但游戏引擎可能会。如果游戏加载失败，需要补充 RDEF 更新。

### 4.2 DXBC 容器结构

```
DXBC Header (32 bytes):
  [0x00] "DXBC" magic
  [0x04] uint32[4] checksum (128-bit MD5-based hash)
  [0x14] uint32 = 1 (always)
  [0x18] uint32 totalSize
  [0x1C] uint32 chunkCount

Chunk Offsets (4 bytes × chunkCount)

Chunks:
  RDEF - Resource Definitions (constant buffers, samplers, textures)
  ISGN - Input Signature
  OSGN - Output Signature
  SHEX - Shader Extended (actual bytecode)
  STAT - Statistics
```

### 4.3 SHEX 指令编码（关键指令）

```
sample_b_indexable(texture2d) dest, src0, src1, src2, src3
  Opcode: 0x31 (SAMPLE_B)
  Extended: indexable flag
  dest: register + mask
  src0: UV coordinates
  src1: texture register (t5, t10, etc.)
  src2: sampler register (s1, s5, etc.)
  src3: mip bias

dcl_sampler s5, mode_default
  Opcode: 0x5A (DCL_SAMPLER)
  
dcl_resource_texture2d (float,float,float,float) t10
  Opcode: 0x58 (DCL_RESOURCE)
```

### 4.4 emissive 修改的精确指令替换

原始 3 条指令（PS[19] lines 267-269 in disasm）：
```
sample_b_indexable(texture2d)(float,float,float,float) r0.xz, v2.xyxx, t5.zxwy, s1, r0.x
mul r1.xyz, cb0[3].xyzx, cb0[3].xyzx
mul r1.xyz, r0.zzzz, r1.xyzx
```

替换为（约 6 条指令）：
```
sample_b_indexable(texture2d)(float,float,float,float) r0.xz, v2.xyxx, t5.zxwy, s1, r0.x
// r0.z = normal.alpha (now: row index 0.0-1.0)
// Convert to ColorTable UV: y = (r0.z * 31 + 0.5) / 32
mad r1.x, r0.z, l(31.0), l(0.5)
mul r1.y, r1.x, l(0.03125)
// Emissive is at column 2 of 8-wide table: x = (2 + 0.5) / 8 = 0.3125
mov r1.x, l(0.3125)
// Sample ColorTable: r1.xyz = emissive RGB from row
sample_indexable(texture2d)(float,float,float,float) r1.xyzw, r1.xyxx, t10.xyzw, s5
// r1.xyz now contains per-row emissive color (no gamma² needed, already linear in table)
```

注意：
- 原版对 `cb0[3]` 做了 γ² 转换（`color * color`），ColorTable 中的值已经是线性的
- 替换后增加了 3 条指令（从 3 → 6），需要更新 SHEX 长度和 STAT 指令计数
- `r0.z`（normal.alpha）从 "emissive mask intensity" 变为 "row pair index"
- `r1.xyz` 输出保持一致，后续代码无需修改

---

## 五、Normal Alpha 的复用策略

### 5.1 当前管线（EMISSIVE 模式）

```
normal.alpha = emissive mask (0.0 = no glow, 1.0 = full glow)
g_EmissiveColor = uniform color for entire material
result: per-pixel intensity, per-material color
```

### 5.2 新管线（ColorTable 模式）

```
normal.alpha = row pair index (0/31 = row 0, 1/31 = row 1, ... 15/31 = row 15)
ColorTable[row].Emissive = per-row emissive RGB
result: per-pixel color (via row assignment)
```

### 5.3 Composite 管线改动

`PreviewService.CompositeEmissiveNorm()` 当前写入 emissive mask → normal.alpha。

ColorTable 模式下改为写入 row pair index → normal.alpha：
```csharp
// For each pixel covered by a decal layer:
normalAlpha[pixel] = (byte)(layer.AllocatedRowPair * (255.0 / 15.0));
// Row 0 → 0, Row 1 → 17, Row 2 → 34, ... Row 15 → 255
```

这与 character.shpk 的 `tablePair = round(normal.a / 17)` 公式一致。

---

## 六、工作量估计

| 任务 | 说明 | 复杂度 |
|------|------|--------|
| DXBC Patcher 工具 | 解析 DXBC 容器 + SHEX 指令编码 + 修改 + 校验和 | 高 |
| .shpk Patcher | 解析/修改/重建 skin.shpk（Node/Selector/PS variant） | 中 |
| MtrlFileWriter 改造 | 写入 ColorTable + 新 shader key + HasColorTable 标志 | 低 |
| PreviewService 改造 | normal.alpha 写 row index 而非 mask，启用 CT 管线 | 中 |
| ColorTableBuilder 扩展 | 支持 skin.shpk 材质的 row pair 分配 | 低 |
| TextureSwapService 改造 | 去除 skin.shpk HasColorTable 跳过逻辑 | 低 |
| 测试 | 多种族/性别/体型 + body mod 兼容性 | 中 |

### 关键风险

1. **DXBC 修改**：最复杂的部分。如果 patch 错误，shader 编译/执行失败会导致模型不渲染或崩溃。需要充分的错误处理和 fallback。

2. **游戏更新**：每次 SE 更新 skin.shpk 时，需要重新 patch。可以通过自动化 patcher 缓解（每次启动时检查 + 重新生成）。

3. **与其他 mod 冲突**：如果用户同时安装了 ALum（也替换 skin.shpk），会冲突。需要检测并提示用户。

4. **Register 冲突**：添加 s5/t10 到 EMISSIVE PS 需要确保这些 register 未被使用。PS[19] 用到 s0-s4 和 t0-t9，所以 s5/t10 应该是安全的。

---

## 七、实验验证清单

- [x] 用 Python 解析 vanilla skin.shpk → 确认 PS[19] 为 EMISSIVE 变体（VS=72, PS=384, version=0x0D01）
- [x] 用 D3DCompiler 反编译 PS[19] → 成功，750 行反汇编输出，emissive 在 line 267-268
- [x] 手动构造最小修改的 DXBC（添加 s5/t10 声明 + NOP 占位）→ D3DCompiler 验证通过
- [x] 完整的 emissive 替换 DXBC（mad + mov + sample ColorTable）→ D3DCompiler 验证通过
- [x] .shpk 级别重建（PS[19] blob 替换 + g_SamplerTable 资源 + offset 修正）
- [x] 在 Penumbra 中用修改后的 skin.shpk → 角色渲染不崩溃 ✅
- [x] 构造带 ColorTable 的 skin.shpk mtrl → PrepareColorTable 创建纹理，emissive 可见 ✅
- [x] per-layer 独立 emissive 颜色 ✅ (row pair 分配 + normal.alpha row index + per-layer CT)
- [ ] in-place ColorTable GPU swap（无闪烁颜色更新）
- [ ] skin.shpk mod 兼容性检测与警告
- [ ] 导出支持（带 CT 的 mtrl + patched shpk）

---

## 八、与 ALum 的兼容性策略

| 场景 | 行为 |
|------|------|
| 无 ALum，无 SkinTattoo shader | 使用原版 skin.shpk，fallback 到全局 emissive |
| 有 SkinTattoo shader，无 ALum | 使用 SkinTattoo 修改的 skin.shpk，完整 per-layer PBR |
| 有 ALum，无 SkinTattoo shader | 不干预，ALum 正常工作 |
| 有 ALum 和 SkinTattoo | **冲突**：两者都替换 skin.shpk。检测后提示用户选择一个 |

检测方法：加载 skin.shpk 后检查是否存在 ALum 特有的 shader key（0x9D4A3204）。如果存在，禁用 SkinTattoo 的 shader 替换。

---

## 九、实现进展日志

### 2026-04-14：DXBC Patcher 原型验证通过

#### 9.1 工具链

| 工具 | 路径 | 用途 |
|------|------|------|
| `dxbc_patcher.py` | `_shpk_analysis/` | DXBC 容器解析 + SHEX 指令流解析 + PS 提取 |
| `dxbc_patch_colortable.py` | `_shpk_analysis/` | DXBC patch 主工具：注入声明 + 替换指令 + 校验和 |
| `D3DCompiler_47.dll` | System32 | 反汇编验证（通过 ctypes 调用 `D3DDisassemble`） |

#### 9.2 PS[19] EMISSIVE 变体精确分析

**文件**：从 vanilla skin.shpk 提取，20796 bytes DXBC。

**资源绑定**（已确认）：
```
Samplers:  s0 (GBuffer) s1 (Normal) s2 (TileOrb) s3 (ReflectionArray) s4 (Occlusion)
Textures:  t0 (LightDiffuse) t1 (LightSpecular) t2 (GBuffer1) t3 (GBuffer2)
           t4 (GBuffer.T) t5 (Normal.T) t6 (Mask.T) t7 (TileOrb.T, 2darray)
           t8 (ReflectionArray.T, cubearray) t9 (Occlusion.T)
CBuffers:  cb0 (MaterialParameter) cb1 (CommonParameter) cb2 (PbrParameterCommon)
           cb3 (CameraParameter) cb4 (InstanceParameter) cb5 (MaterialParameterDynamic)
           cb6 (AmbientParam) cb7 (ShaderTypeParameter, dynamicIndexed)
```

**s5/t10 空闲可用** ✅

**Emissive 指令位置**（SHEX 字节偏移 + D3DDisassemble 行号对应）：
```
SHEX @0x025C → disasm line 267: mul r1.xyz, cb0[3].xyzx, cb0[3].xyzx
SHEX @0x0280 → disasm line 268: mul r1.xyz, r0.zzzz, r1.xyzx
```

**指令 token 序列（patch 匹配用）**：
```
Instruction 35 (9 tokens): 09000038 00100072 00000001 00208246 00000000 00000003 00208246 00000000 00000003
Instruction 36 (7 tokens): 07000038 00100072 00000001 00100AA6 00000000 00100246 00000001
```

#### 9.3 DXBC 校验和算法

**算法**：自定义 padding 的 MD5（标准 MD5 transform + DXBC 特有 padding 规则）。

**来源**：vkd3d-proton `checksum.c` / GPUOpen `DXBCChecksum.cpp`。

**关键差异**：
- 输入 = `blob[20:]`（跳过 magic + checksum 字段）
- padding 块中 `word[0] = num_bits`，`word[15] = (num_bits >> 2) | 1`
- 非 RFC 1321 标准 padding

**验证**：原版 PS[19] 校验和 `47A03F7D AF721DA0 CF4B6373 51BF3682` 计算匹配 ✅

#### 9.4 完整 ColorTable 采样 Patch 结果

**两轮验证全部通过：**

**第一轮（NOP 原型）**：验证 patch 框架——声明注入 + NOP 占位 + 校验和重算 + D3DCompiler 验证 ✅

**第二轮（完整采样指令）**：替换 emissive 计算为 ColorTable 纹理采样 ✅

成功完成的操作：
1. ✅ 在 SHEX 声明区末尾注入 `dcl_sampler s5, mode_default` + `dcl_resource_texture2d t10`（+7 tokens = 28 bytes）
2. ✅ 将 emissive 的 2 条 mul 指令（16 tokens）替换为 3 条 ColorTable 采样指令（25 tokens）
3. ✅ 重建 DXBC 容器（chunk 偏移、总大小、token count 正确更新）
4. ✅ 计算正确的 DXBC 校验和
5. ✅ D3DCompiler `D3DDisassemble` 验证通过（753 行反汇编输出，指令流完整）

**patched DXBC 反汇编确认**（20860 bytes, +64 bytes vs 原版）：
```
line 263: dcl_sampler s5, mode_default                                           ← 新增
line 264: dcl_resource_texture2d (float,float,float,float) t10                   ← 新增
...
line 268: sample_b_indexable(texture2d) r0.xz, v2.xyxx, t5.zxwy, s1, r0.x      ← 保留（r0.z = normal.alpha）
line 269: mad r1.y, r0.z, l(0.937500), l(0.015625)                              ← 新：row UV = alpha×0.9375+0.015625
line 270: mov r1.x, l(0.312500)                                                 ← 新：column UV = 0.3125（emissive 列中心）
line 271: sample_indexable(texture2d)(float,float,float,float) r1.xyzw, r1.xyxx, t10.xyzw, s5  ← 新：ColorTable 采样
line 272: mul r0.x, r0.x, cb0[9].x                                              ← 后续代码不变 ✓
```

**替换指令 SM5 token 编码详细记录**：
```
; mad r1.y, r0.z, l(0.9375), l(0.015625) — 9 tokens
09000032 00100022 00000001 0010002A 00000000 00004001 3F700000 00004001 3C800000

; mov r1.x, l(0.3125) — 5 tokens
05000036 00100012 00000001 00004001 3EA00000

; sample_indexable(texture2d)(float4) r1.xyzw, r1.xyxx, t10.xyzw, s5 — 11 tokens
8B000045 800000C2 00155543 001000F2 00000001 00100046 00000001 00107E46 0000000A 00106000 00000005
```

**SM5 operand 编码规则总结**（实战验证）：

| 操作数 | Token | 含义 |
|--------|-------|------|
| `r1.x` (dest) | `00100012 00000001` | type=1(temp), mask=.x(0x1), reg=1 |
| `r1.y` (dest) | `00100022 00000001` | mask=.y(0x2) |
| `r1.xyz` (dest) | `00100072 00000001` | mask=.xyz(0x7) |
| `r1.xyzw` (dest) | `001000F2 00000001` | mask=.xyzw(0xF) |
| `r0.z` (src select) | `0010002A 00000000` | select_1 mode, component=z(2) |
| `r1.xyxx` (src swizzle) | `00100046 00000001` | swizzle mode, x=0,y=1,z=0,w=0 |
| `l(float)` (imm scalar) | `00004001 <IEEE754>` | type=4(immediate), 1-component |
| `t10.xyzw` (tex) | `00107E46 0000000A` | type=7(resource), swizzle=xyzw, reg=10 |
| `s5` (sampler) | `00106000 00000005` | type=6(sampler), reg=5 |

**ColorTable UV 映射公式**：
```
normal.alpha ∈ [0.0, 1.0]  →  rowPair ∈ {0..15}
rowPair = round(normal.alpha × 15)

ColorTable 纹理布局: 8 columns × 32 rows, R16G16B16A16_FLOAT
Emissive 在 column 2 (vec4 index 2)

UV.x = (2 + 0.5) / 8 = 0.3125        → 命中 emissive 列中心
UV.y = (rowPair × 2 + 0.5) / 32       → 命中 lower row 中心（layer override row）
     = normal.alpha × 15 × 2/32 + 0.5/32
     = normal.alpha × 0.9375 + 0.015625
```

#### 9.5 SM5 指令关键知识（备忘）

**Opcode token**（第一个 uint32）：
- bits [0:10] = opcode ID（0x32=mad, 0x36=mov, 0x38=mul, 0x45=sample, 0x58=dcl_resource, 0x5A=dcl_sampler）
- bit [11] = extended opcode flag（sample 等指令为 1）
- bits [24:30] = instruction length (tokens)
- bit [31] = 某些指令的 extended flag（sample 类为 1）

**dcl_resource 特殊编码**：bits [11:15] = resource dimension（3=texture2d, 8=texture2darray, 10=texturecubearray）

**Extended opcode tokens**（sample 类指令）：从 reference sample 指令复制 `800000C2 00155543`，表示 `_indexable(texture2d)(float,float,float,float)`。

#### 9.6 输出文件

| 文件 | 路径 | 说明 |
|------|------|------|
| `ps_019_EMISSIVE.dxbc` | `_shpk_analysis/extracted_ps/` | 原版 PS[19] 提取（20796 bytes） |
| `ps_019_PATCHED.dxbc` | `_shpk_analysis/extracted_ps/` | patched PS（20860 bytes, +64） |
| `ps_019_PATCHED_disasm.txt` | `_shpk_analysis/extracted_ps/` | patched PS 反汇编（D3DCompiler 输出） |
| `ps_019_EMISSIVE_disasm.txt` | `_shpk_analysis/extracted_ps/` | 原版 PS 反汇编（之前的手动提取） |

#### 9.7 .shpk Patcher 完成 + 游戏内验证

**shpk_patcher.py** 完成了完整的 skin.shpk 重建：
- 原版 PS[19] 的 DXBC blob 替换为 patched 版本
- g_SamplerTable (CRC 0x2005679F) 添加到 PS[19] 的 sampler (s5) + texture (t10) 资源列表
- 所有后续 shader 的 blob offset 正确调整
- re-parse + D3DCompiler 双重验证通过

**输出**: `_shpk_analysis/skin_patched.shpk` (10,527,709 bytes, +111 vs 原版)

### 2026-04-14：端到端管线验证通过 🎉

#### 9.8 MtrlFileWriter ColorTable 注入

修改 `WriteEmissiveMtrlViaLumina()`：
- 检测 skin.shpk 材质时**不再 strip ColorTable**
- 改为**注入** Dawntrail ColorTable（2048 bytes, 8×32 R16G16B16A16_FLOAT）
- `BuildSkinColorTable()` 在所有 32 行写入用户 emissive 颜色
- AdditionalData flags 设置：`HasColorTable(bit2) | widthLog=3(bits4-7) | heightLog=5(bits8-11)` = 0x534

#### 9.9 游戏内验证结果

| 测试项 | 结果 |
|--------|------|
| patched skin.shpk 引擎加载 | ✅ 不崩溃，Penumbra redirect 生效 |
| 非 EMISSIVE 材质渲染 | ✅ 正常，不受影响 |
| EMISSIVE 无 ColorTable（首次测试） | ✅ emissive 消失（t10 未绑定，采样=0）— 确认 shader 在工作 |
| EMISSIVE + ColorTable 注入 | ✅ **身体发光可见！** ColorTable emissive 数据被 shader 正确读取 |
| per-layer 独立颜色 | ✅ **每层贴花独立 emissive 颜色！** |

**完整管线（含 per-layer）**：
```
Python shpk patcher → patched skin.shpk (Penumbra deploy)
                           ↓
RowPairAllocator → 每个 emissive 层分配独立 row pair (1-15)
                           ↓
CompositeRowIndexNorm → normal.alpha = rowPair * 17 (离散行索引)
                           ↓
BuildSkinColorTablePerLayer → per-layer emissive 写入对应 CT 行
                           ↓
MtrlFileWriter → mtrl + ColorTable 2048B (HasColorTable + Dawntrail dims)
                           ↓
Engine PrepareColorTable → GPU texture (R16G16B16A16_FLOAT 8×32)
                           ↓
Patched PS[19] → normal.alpha×0.9375+0.015625 → sample(t10) → per-row emissive
                           ↓
                     per-layer 独立发光 ✅
```

#### 9.10 Per-layer 独立发光实现

**关键改动**：

1. **`ProcessGroup()` 新增 `useSkinColorTable` 分支**：
   - 检测条件：`hasEmissive && patchedSkinShpkPath != null && IsSkinMaterial(group)`
   - `TryDeployPatchedSkinShpk` 移到 group 循环**之前**（否则首次 patchedSkinShpkPath 为 null）
   - 走独立管线：row pair 分配 → per-layer CT → row index norm → 专用 mtrl 构建

2. **`CompositeRowIndexNorm()`（新增）**：
   - 与 `CompositeEmissiveNorm` 类似的 UV-space 扫描
   - 写入离散 row byte（`rowPair * 17`）而非连续 mask 值
   - 阈值 `da >= 0.5`（二值化，不是渐变）
   - 非贴花区域 alpha=0 → row 0 → 默认无发光

3. **`BuildSkinColorTablePerLayer()`（新增静态方法，MtrlFileWriter）**：
   - 接受 `List<DecalLayer>`，遍历已分配 row pair 的 emissive 层
   - 每层的 `EmissiveColor * EmissiveIntensity` 写入对应的 lower row（`rowPair * 2`）
   - Row 0 保持零 emissive（默认背景）
   - 其他字段（diffuse/specular/roughness）填充安全默认值

4. **`WriteEmissiveMtrlWithColorTable()`（新增静态方法，MtrlFileWriter）**：
   - 接受预构建的 ColorTable bytes
   - 设置 AdditionalData flags（HasColorTable + Dawntrail 8×32 dimensions）
   - 注入 CategorySkinType=Emissive shader key + g_EmissiveColor constant
   - 调用 `RebuildMtrl` 输出完整 mtrl

5. **Row pair 0 保留**：
   - skin.shpk ColorTable 模式下，allocator 预占 row pair 0
   - 非贴花区域 normal.alpha=0 → 映射到 row 0 → emissive=(0,0,0) → 不发光
   - 第一个 emissive 层从 row pair 1 开始（normal.alpha=17）

**关键 bug 修复**：首版 per-layer 实现全身发光，原因是 row pair 0 被分配给了 emissive 层，而非贴花区域也映射到 row 0 → 全部读到同一颜色。修复：预占 row 0 作为默认背景行。

#### 9.11 已知限制与后续优化

| 项 | 当前状态 | 后续 |
|---|---|---|
| Full Redraw per-layer | ✅ 工作 | — |
| In-place ColorTable GPU swap | ❌ 未实现 | TextureSwapService 解除 skin.shpk 跳过 |
| EmissiveCBufferHook | ⚠️ 仍在运行 | skin CT 模式下应跳过 |
| 导出 (ModExport) | ❌ 未适配 | ModExportService 需输出带 CT 的 mtrl + patched shpk |
| skin.shpk mod 检测 | ❌ 未实现 | 提示用户冲突 |
| 颜色实时拖拽更新 | ❌ 需 Full Redraw | 需 GPU swap 支持 |

#### 9.12 Per-layer 容量限制

| 资源 | 总量 | 保留 | 可用 | 说明 |
|------|------|------|------|------|
| ColorTable row pairs | 16 (0-15) | 1 (row 0 = 默认不发光) | **15** | 每个独立 emissive 颜色占 1 个 |
| normal.alpha 编码 | 256 级 | 0 = 默认 | 17,34,...255 | `rowPair * 17`，16 级离散值 |

**单个 skin 材质组最多 15 个独立 emissive 贴花层。**

不受限的情况：
- 纯 diffuse 贴花层（不开 emissive）不占 row pair
- 多层共享相同 emissive 颜色可合并到同一 row pair
- character.shpk（装备/虹膜）有独立的 ColorTable 系统，互不影响

UI 层面应在 skin 材质组限制 emissive 层数到 15，超出时提示用户。

#### 9.13 skin.shpk mod 兼容性问题

**问题**：用户可能安装了修改 skin.shpk 的 mod（ALum、body mod 自带 shader 等）。SkinTattoo 的 patched skin.shpk 通过 Penumbra 临时 mod（优先级 99）部署，**会覆盖**用户的 mod shader。

**影响**：
- ALum 的虹彩/湿润/Level T ColorTable 功能在 preview 期间失效
- body mod 依赖的 shader 特性可能丢失（如 HRO 体毛渲染差异）

**解决策略（按优先级）**：

1. **检测 + 警告**（推荐）：
   - Full Redraw 前用 `penumbra.ResolvePlayer("shader/sm5/shpk/skin.shpk")` 查询 resolved path
   - 如果不是游戏原版路径 → 说明有第三方 mod 替换了 shader
   - 在 UI 中显示警告，让用户决定是否启用 patched shader

2. **Patch 用户的 mod shader**（后续优化）：
   - 读取用户 mod 的 skin.shpk（而非 vanilla）
   - 对它应用同样的 DXBC patch（添加 s5/t10 + ColorTable 采样）
   - 只要 PS[19] 的 emissive 指令 token pattern 匹配就能 patch 成功
   - 匹配失败时 fallback 到全局 emissive

注：ALum 已停更且无法在当前版本运行，无需兼容。
