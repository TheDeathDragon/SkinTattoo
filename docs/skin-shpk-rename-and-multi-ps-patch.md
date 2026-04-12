# skin.shpk Rename + Multi-PS Patch 实施方案

> 研究日期：2026-04-14
> 前置依据：`skin-shpk-colortable-implementation.md`
> 触发原因：脸部区域 ColorTable emissive 完全无效；调节发光颜色/强度也没有实时变化
> 当前 patcher 只 patch PS[19]，运行时验证发现两个根本性问题：(1) shpk 缓存命中导致 patched 文件从未被加载；(2) 即便加载，强制 mat keys 在大多数 sceneKey 状态下也命中不到 PS[19]

## 一、运行时诊断结果

### 1.1 ShpkDiag 输出（关键证据）

启动后所有皮肤类 mtrl 共享同一个 ShaderPackage 指针：

```
mt_c1401b0001_bibo.mtrl              shpk=0x1E7245E1870  ps=384  smp=13
mt_c1401f0001_fac_a.mtrl             shpk=0x1E7245E1870  ps=384  smp=13
mt_c1401f0001_fac_b.mtrl             shpk=0x1E7245E1870  ps=384  smp=13
mt_c1401f0001_fac_c.mtrl             shpk=0x1E7245E1870  ps=384  smp=13
preview_g48E654E0.mtrl (redirected)  shpk=0x1E7245E1870  ps=384  smp=13
（其他角色 c0101/c0201/c0901/c1001/c1101/c1301 同上）
```

**结论 A**：所有 skin.shpk mtrl 在内存里只有一份 ShaderPackage 实例。无论 mtrl 文件被 Penumbra redirect 到我们的 preview 文件，shpk 指针保持不变 = 还是 vanilla skin.shpk。

### 1.2 NodeDump 输出（关键证据）

```
NodeDump: nodes=768 sysKeys=0 sceneKeys=8 matKeys=3
NodeDump: MatKey[0] id=0x380CAED0  ← CategorySkinType, default=Face
NodeDump: MatKey[1] id=0xD2777173  ← CategoryDecalMode
NodeDump: MatKey[2] id=0xF52CCF05  ← CategoryVertexColorMode

NodeDump: totalMatchedForcedKeys=128 matchedWithPS19=4 otherNodesReachingPS19=0
NodeDump: matchedSceneKeyVariants=128 matchedSysKeyVariants=1
NodeDump: PS19 pass-slot histogram: slot[2]=4
NodeDump: PS19 SubViewIndex histogram: sv[6]=4

NodeDump: pass[0] (8 unique):  17,47,77,107,137,167,197,227                 [each x16]
NodeDump: pass[1] (8 unique):  18,48,78,108,138,168,198,228                 [each x16]
NodeDump: pass[2] (32 unique): 19,28,49,58,79,88,109,118,...               [each x4]
NodeDump: pass[3] (32 unique): 20,29,50,59,80,89,110,119,...               [each x4]
NodeDump: pass[4] (8 unique):  4,34,64,94,124,154,184,214                  [each x16]
```

**结论 B**：强制 3 个 mat keys 后命中 128 个 Emissive Node（覆盖 8 个 Vs 组 × 16 个 sceneKey 子组合）。其中只有 4 个 Node 的 pass[2] 引用 PS[19]，其余 124 个 Node 的 pass[2] 引用其他 31 个 PS（`28/49/58/79/...382`）。

`SubViewIndex=6 → pass[2]` 是 lighting render stage（其他 sv 是 GBuffer/shadow/zprepass）。**只 patch PS[19] 等于只覆盖 1/32 = 3% 的渲染场景。**

### 1.3 IDA 反编译验证

- `sub_140412FB0`（CharacterCommonResource init）在游戏启动时调用 `sub_1402ECFA0` 加载 `shader/sm5/shpk/skin.shpk` 到全局 slot `a1[84]`
- `sub_1403047A0`（LoadResource）首先调 `sub_140308FB0` 做 cache 查找
- `sub_140308FB0` 用纯 path hash `(category, *a3, *a4)` 走 BST，**没有内容/版本检查**
- Penumbra 的 redirect 拦截 SqPack 文件读，但 skin.shpk 启动后再也没有 cache miss，所以 redirect 永远不触发

**结论 C**：`Client::System::Resource::ResourceManager` 用纯 path 缓存，无法通过同名 redirect 替换已加载的 shpk。

## 二、修复方案

### 2.1 整体策略：Rename + Multi-PS Patch

**核心思路**：让引擎首次见到一个全新的 shpk 路径（cache miss），从而走 Penumbra 的 redirect 加载 patched 版。代价是失去 OnRenderMaterial 的 fast-path 优化。

```
patched 文件路径:  shader/sm5/shpk/skin_ct.shpk          ← 新游戏路径
mtrl ShaderPackageName: skin.shpk → skin_ct.shpk        ← 改字符串
Penumbra redirect: shader/sm5/shpk/skin_ct.shpk → 磁盘文件
patcher 范围:      PS[19] → 32 个 lighting PS 全部 patch
```

### 2.2 步骤 1：rename + 改 mtrl ShaderPackageName

**SkinShpkPatcher.cs**：保持现有 patch 逻辑，输出文件不变。

**PreviewService.cs**：
- 常量改名：`SkinShpkGamePath = "shader/sm5/shpk/skin_ct.shpk"`
- `TryDeployPatchedSkinShpk` 把 redirect 的 game path 改成新名字
- 冲突检查仍查 `shader/sm5/shpk/skin.shpk`（提示用户其他 mod 的 vanilla 替换）

**MtrlFileWriter.cs**：
- `WriteEmissiveMtrlWithColorTable` 在重建 mtrl 时改写 ShaderPackageName 字符串：
  - 在 strings table 末尾追加 `skin_ct.shpk\0`
  - 更新 `MaterialFileHeader.ShaderPackageNameOffset` 指向新位置
  - 更新 `StringTableSize`
- vanilla skin.shpk path 保持不动（其他不走 emissive 的 mtrl 不受影响）

### 2.3 步骤 2：patch 全部 32 个 lighting PS

**SkinShpkPatcher.cs**：
- 把 `PsIndex = 19` 替换成 `int[] LightingPsIndices = {...}`（32 个）
- Patch 主循环抽成 `PatchSinglePs(shpk, psIdx)` 函数
- 对每个 PS index 调用一次：
  - 提取 PS blob 的 DXBC
  - 注入 dcl_sampler s5 + dcl_resource_texture2d t10
  - 替换 emissive 计算（mul cb0[3]² + mul r0.z 改成 ColorTable 采样）
  - 重建 DXBC + 校验和
  - 替换 shpk.BlobSection 中对应区域
  - 调整后续所有 shader 的 BlobOff（patch 后体积变化）
  - 给该 PS 添加 g_SamplerTable 资源（Slot=5/Size=5 sampler + Slot=10/Size=6 texture）
- 容错：某个 PS 如果找不到 emissive byte pattern（理论上不应该），跳过并 log warning

**完整 PS 列表**（32 个）：

```
19, 28, 49, 58, 79, 88, 109, 118,
139, 148, 169, 178, 199, 208, 229, 238,
247, 256, 265, 274, 283, 292, 301, 310,
319, 328, 337, 346, 355, 364, 373, 382
```

模式：每个 Vs 组 30 个 PS，lighting 占 4 个：
- Vs=0 组 (PS 17~46):   19, 28
- 中间偏移 21:          49, 58 (实际是 Vs=0 后续的 sceneKey 组合？需验证)
- ...

### 2.4 单层最小测试预期

修完后跑测试，应观察到：

1. **ShpkDiag 输出**：脸 mtrl 的 shpk 指针 = 全新地址（不再是 `0x1E7245E1870`），`smp` 数 +1（多了 g_SamplerTable）
2. **脸单图层 emissive**：可见，颜色 = layer.EmissiveColor × Intensity
3. **多图层独立颜色**：每层独立显示自己的 emissive 颜色（normal.alpha 路由到不同 ColorTable row）
4. **滑条实时调节**：通过现有 `RestoreSkinCtAfterHighlight → ReplaceColorTableRaw` 路径，CT 纹理原子替换 → 实时可见

## 三、风险与缓解

### 3.1 Fast-path 失效

`OnRenderMaterial` (`sub_14026EE10`) 用 `material->ShaderPackage` 与 ModelRenderer 缓存的 5 个 ShaderPackage 指针比较：

```c
v17 = mat->MaterialResourceHandle->ShaderPackage;
if      (v17 == cached[0]) → branch A  (fast-path 1)
else if (v17 == cached[1]) → branch B
else if (v17 == cached[2|3]) → branch C
else if (v17 == cached[4]) → branch D
else                       → slow path (current default-flag setup)
```

**slow path 行为**：所有 cached 比较失败时，`a2` 标志位的设置方式比 fast-path 简单（少一些 branch 设置 `0x80/0x82` 之类的 bit）。但函数本身不会渲染失败 —— `a2` 只是后续 cbuf binding 决定 render slot 的标志。

**结论**：失去 fast-path 是一个性能优化损失（每帧每材质多走几条 branch），不影响渲染正确性。皮肤材质数量有限（每角色 ~6 个），实际 CPU 开销忽略不计。

### 3.2 部分 PS 找不到 byte pattern

32 个 lighting PS 应该都是 EMISSIVE 变体的同族 shader（不同 sceneKey 状态下的特化版本），DXBC 字节流的 emissive 计算指令理论上一致：

```
mul r1.xyz, cb0[3].xyzx, cb0[3].xyzx     ← gamma² 计算
mul r1.xyz, r0.zzzz, r1.xyzx              ← mask × color
```

但不同 PS 可能有：
- 不同的临时寄存器分配（`r1` 可能变成 `r2`）
- 指令重排序
- inline 不同的辅助函数

**缓解**：`PatchSinglePs` 失败时不抛异常，记 log 并继续。最终 patch 成功率应 >= 90%。完全失败的 PS 在那些 sceneKey 下退化为 vanilla uniform `g_EmissiveColor`，对单层场景行为一致。

### 3.3 ShaderPackageName 字符串改写出错

mtrl 文件格式 `MaterialFileHeader`：

```
[0]   uint32 Version
[4]   uint16 FileSize
[6]   uint16 DataSetSize
[8]   uint16 StringTableSize       ← 要更新
[10]  uint16 ShaderPackageNameOffset ← 要更新
[12]  byte   TextureCount
[13]  byte   UvSetCount
[14]  byte   ColorSetCount
[15]  byte   AdditionalDataSize
```

后续是：texture/uvset/colorset entry tables → strings table → additionalData → colorset data。

**改写策略**：在 strings table 末尾追加 `skin_ct.shpk\0`，更新 `ShaderPackageNameOffset` 指向新偏移，`StringTableSize` 增加 13 字节（"skin_ct.shpk" + null）。其余偏移用现有 `RebuildMtrl` 自动重算，不需要单独处理。

### 3.4 g_SamplerTable 资源插入位置

每个 PS 的 Resources 列表按 (Constants, Samplers, UAVs, Textures) 排列。已有的 patcher 实现了：

```c
int insertSampAt = ps.CCnt + ps.SCnt;        // 在 samplers 末尾
ps.Resources.Insert(insertSampAt, samplerRes);
ps.Resources.Add(textureRes);                 // texture 在最后
ps.SCnt++;
ps.TCnt++;
```

直接对每个 PS 复用同一段逻辑即可。

## 四、实施 checklist

- [x] **Step 1.1** PreviewService 改 `SkinShpkGamePath` = `"shader/sm5/shpk/skin_ct.shpk"`
- [x] **Step 1.2** MtrlFileWriter 增加 ShaderPackageName 改写函数（`RewriteShaderPackageName`）
- [x] **Step 1.3** WriteEmissiveMtrlWithColorTable 调用新函数；RebuildMtrl 接受 strings/offset/size override
- [x] **Step 1.4** 编译 + 单层 emissive 测试，ShpkDiag 显示脸 mtrl shpk=`0x209A9F0B840`（新地址，Penumbra redirect 生效）
- [x] **Step 2.1** SkinShpkPatcher 用 `LightingPsIndices[32]` 替换 `PsIndex`
- [x] **Step 2.2** Patch 主循环 refactor 成 `PatchSinglePs(shpk, psIdx, strOff, strSz)`
- [x] **Step 2.3** 容错处理（找不到 pattern 时 log + 跳过，32 个 PS 全部成功才完全覆盖；部分失败也保留已成功的）
- [x] **Step 2.4** 多层 emissive 测试：两个贴花独立颜色 + 实时调节生效
- [x] **Step 3** 清理临时诊断工具（DumpGeneric / DumpShaderResources / DumpPsBlobsToDisk / DumpCbReferencesInPs 全部删除；CTSwap / ShpkDiag / NodeDump 保留为常驻 debug）

### 4.1 测试后新发现 + 修复（2026-04-14 后续）

**现象 1**：多层 emissive 时，rowPair >= 1 的层在层内部亮度只有 ~50%。
**原因**：shader 的采样公式 `row = normal.a * 30/255 + 0.5` 让采样点恰好落在 pair 中心（如 rowPair=1 → row 2.5），GPU linear filter lerp(rowLower, rowLower+1)，但 `BuildSkinColorTablePerLayer` 只写 `rowLower`，rowLower+1 emissive=0 → 50% 暗化。
**修复**：`MtrlFileWriter.BuildSkinColorTablePerLayer` 对每个 pair 写两行（`rowLower` 和 `rowLower+1`）相同的 emissive 值。

**现象 2**：脸上贴花边缘出现"原贴花颜色的描边"（例：红色贴花 + 纯白发光 10 强度，边缘仍可见红色），身体（2048+ 纹理）不出现。
**原因**：
- Diffuse 合成用 `da < 0.001` 软阈值（渐变 alpha）
- Normal.a 合成用 `maskValue >= 0.5` 硬阈值
- 两者边界不对齐：diffuse 软边延伸到 emissive 硬边界之外，那一圈像素只显示贴花原始颜色（无发光）
- 脸 512×512 每个像素物理尺寸大，过渡带占好几像素；身体 2048+ 过渡带只占 1 像素，不明显
**修复**：`PreviewService.CpuUvComposite` 中对 `AffectsEmissive && AllocatedRowPair >= 0` 的 layer，在 diffuse 合成里也应用相同的 `maskValue >= 0.5` 硬阈值。diffuse 和 normal.a 边界完全对齐。

**现象 3（未修）**：贴花 2/3 边界仍可能短暂看到贴花 1 的颜色。
**原因**：GPU 对 normal 贴图的 linear filter 在两个 rowPair 之间插值时，`normal.a=17`（贴花 1 的 rowPair）可能作为贴花 2 (α=34) 与空白 (α=0) 边界的中间值出现 → shader 读到贴花 1 的 ColorTable 行。
**潜在修复**：DXBC 在采样前加 `round(normal.a * 15)` 让 rowPair 离散化。暂未实施；实际观感影响小，可留作优化。

### 4.2 超出当前 patch 的限制

**龙角 / etc mtrl 不发光**：`mt_c1401f0001_etc_a.mtrl` 等用独立 shpk（ShpkDiag 日志显示 shpk=`0x1E7245C0580` vs=112 ps=104，不是 skin.shpk）。本方案只覆盖 skin.shpk。

详细研究 + Tier 1 失败结论见 `docs/etc-shpk-research.md`（结论：搁置，需要更深入的 hair.shpk 反汇编才能继续）。

## 五、参考数据备份（避免后续重测）

完整 NodeDump（patched skin.shpk，2026-04-14 20:30）：

```
nodes=768  sysKeys=0  sceneKeys=8  matKeys=3
MatKey[0] id=0x380CAED0 default=0xF5673524  ← CategorySkinType
MatKey[1] id=0xD2777173 default=0x4242B842  ← CategoryDecalMode
MatKey[2] id=0xF52CCF05 default=0xDFE74BAC  ← CategoryVertexColorMode

vanilla SkinType 值与 PS 覆盖：
  Body(0x2BDB45F1)     nodes=256 PSes=96 unique
  Face(0xF5673524)     nodes=128 PSes=88 unique  ← 默认变体
  BodyJJM(0x57FF3B64)  nodes=128 PSes=88 unique
  Emissive(0x72E697CD) nodes=128 PSes=88 unique  ← 我们强制的目标

每个 Emissive Node 的 5 个 pass：
  pass[0] Vs=0/9/18/27/36/45/54/63   GBuffer
  pass[1] Vs=1/10/19/28/37/46/55/64  Shadow?
  pass[2] Vs=2/11/20/29/38/47/56/65  LIGHTING ← 我们要 patch
  pass[3] Vs=1/10/19/28/37/46/55/64  Alpha output?
  pass[4] Vs=3/6/12/15/...           Z-only

PassIndices[16] 一致为：
  [255,4,0,255,1,255,2,255,255,255,3,255,255,255,255,255]
  ↓
  SubViewIndex 0=skip, 1=pass4, 2=pass0, 4=pass1, 6=pass2, 10=pass3
  ↓
  SubViewIndex=6 → pass[2] → LIGHTING PS
```
