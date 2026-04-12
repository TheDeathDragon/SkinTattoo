# skin.shpk → character.shpk 替换可行性研究

> 研究日期：2026-04-12 ~ 2026-04-13。
> 基于前序文档 `route-c-ida-research.md`、`pbr-material-research.md`、`material-replacement-research.md`。
> 新增内容：社区联网搜索（ALum、发光纹身 mod）、mod pmp 逆向分析（TBSE vs AetherFlux）、原版 .shpk 文件完整解析（skin.shpk vs character.shpk 60 项 MaterialParams 对比）。

## 核心问题

**当前痛点**：skin.shpk 材质没有 ColorTable，emissive 通过 CBuffer 中的单一 `g_EmissiveColor` 常量控制。同一材质上的所有贴花层共享同一个发光颜色，无法独立。

**Route C 目标**：将 skin.shpk 换成 character.shpk（或 charactertattoo.shpk），获得 ColorTable 支持，实现 per-layer 独立 PBR。

---

## 一、CBuffer 材质常量对比

### 1.1 共享常量（skin.shpk 和 character.shpk 完全相同）

从 Meddle 的 Names.cs 提取，以下常量在 skin.shpk 和 character.shpk（以及几乎所有 character-family shader）中**完全相同**：

| 常量名 | CRC32 | 默认值 | 用途 |
|---|---|---|---|
| g_DiffuseColor | 0x2C2A34DD | 1,1,1 | 漫反射颜色乘数 |
| g_EmissiveColor | 0x38A64362 | 0,0,0 | 发光颜色 |
| g_AlphaAperture | 0xD62BF368 | 2 | Alpha 测试 |
| g_AlphaOffset | 0xD07A6A65 | 0 | Alpha 偏移 |
| g_NormalScale | 0xB5545FBB | 1 | 法线强度 |
| g_SpecularColorMask | 0xCB0338DC | 1,1,1 | 高光颜色遮罩 |
| g_SSAOMask | 0xB7FA33E2 | 1 | SSAO 遮罩 |
| g_GlassIOR | 0x7801E004 | 1 | 玻璃折射率 |
| g_SphereMapIndex | 0x074953E9 | 0 | 球形贴图索引 |
| g_TileIndex | 0x4255F2F4 | 0 | Tile 贴图索引 |
| g_TileScale | 0x2E60B071 | 16,16 | Tile 缩放 |
| g_TileAlpha | 0x12C6AC9F | 1 | Tile 透明度 |
| g_ShaderID | 0x59BDA0B1 | 0 | Shader 变体 ID |
| g_OutlineColor | 0x623CC4FE | 0,0,0 | 轮廓线颜色 |
| g_OutlineWidth | 0x8870C938 | 0 | 轮廓线宽度 |
| g_SheenRate | 0x800EE35F | 0 | 光泽率 |
| g_SheenTintRate | 0x1F264897 | 0 | 光泽色调 |
| g_SheenAperture | 0xF490F76E | 1 | 光泽孔径 |
| g_LipRoughnessScale | 0x3632401A | 0.7 | 嘴唇粗糙度 |
| g_ToonIndex / g_ToonLightScale / g_ToonReflectionScale | various | various | 卡通渲染参数 |
| g_Iris* (全部 iris 相关) | various | various | 虹膜参数（所有 shader 共享） |
| g_TextureMipBias | 0x39551220 | 0 | Mip 偏移 |
| g_ShadowPosOffset | 0x5351646E | 0 | 阴影位置偏移 |

共约 **30+ 个常量完全共享**。

### 1.2 character.shpk 独占常量

| CRC32 | 默认值 | 备注 |
|---|---|---|
| 0x25C3F71B | (unknown) | **仅** character.shpk |
| 0x2F5837E2 | (unknown) | **仅** character.shpk |
| g_ScatteringLevel (0xB500BB24) | - | bg / character / characterlegacy / hair (不在 skin 和 charactertattoo 中) |
| g_AmbientOcclusionMask (0x575ABFB2) | - | character / characterglass / characterlegacy / characterscroll / charactertransparency / hair / skin（skin 也有！） |

**结论**：CBuffer 常量层面差异极小，skin.shpk 和 character.shpk 的 CBuffer 布局高度重合。转换后大部分常量可直接复用。

---

## 二、关键结构性差异

### 2.1 ColorTable（最大差异）

| 特性 | skin.shpk | character.shpk | charactertattoo.shpk |
|---|---|---|---|
| HasColorTable | **false** | true | true (推测) |
| DataSetSize | 0 | 2048 (Dawntrail) | 2048 (推测) |
| 行数 | N/A | 32 行 = 16 row pairs | 32 行 (推测) |
| PBR 字段 | N/A | Diffuse/Specular/Emissive/Roughness/Metalness/Sheen per row | 同上 (推测) |
| Row 选择机制 | N/A | `normal.a / 17` → row pair index 0-15 | 同上 (推测) |

**这是整个 Route C 的核心价值**：获得 ColorTable 后，每个贴花层可以分配独立的 row pair，实现 per-layer PBR 独立。

### 2.2 纹理/Sampler 差异

| Sampler | skin.shpk | character.shpk |
|---|---|---|
| g_SamplerDiffuse (漫反射) | ✅ | ✅ |
| g_SamplerNormal (法线) | ✅ | ✅ |
| g_SamplerMask (遮罩) | ❌ | ✅ |
| g_SamplerSpecular (高光) | ❌ | ✅ |
| g_SamplerIndex (索引) | ❌ | ✅ |
| g_SamplerTable (ColorTable) | ❌ | ✅ |

**转换需要补全至少 4 个 sampler**。skin mtrl 只引用 diffuse + normal 两个纹理；character mtrl 需要 mask + specular + index + ColorTable 纹理。

解决方案：
- mask → 生成全白/默认 mask 纹理
- specular → 生成默认高光纹理或复用 normal 通道
- index → normal.a 已有 row pair 索引信息，由 compositor 写入
- ColorTable → 在 mtrl 中内嵌 2048 字节默认 ColorTable + 运行时 TextureSwapService 写入

### 2.3 CustomizeParameter（皮肤染色系统）

**这是最大的风险点**。

```csharp
// Human.cs
[FieldOffset(0xBF0)] public ConstantBuffer* CustomizeParameterCBuffer;

// CustomizeParameter.cs (0x90 字节)
SkinColor      (+0x00): 皮肤漫反射颜色 (XYZ) + 肌肉色调 (W)
SkinFresnelValue0 (+0x10): 皮肤高光颜色 (XYZ)
LipColor       (+0x20): 嘴唇颜色 (XYZ) + 不透明度 (W)
MainColor      (+0x30): 头发主颜色
HairFresnelValue0 (+0x40): 头发高光颜色
MeshColor      (+0x50): 头发挑染颜色
LeftColor      (+0x60): 左眼颜色 (XYZ) + 面部彩绘 UV (W)
RightColor     (+0x70): 右眼颜色 (XYZ) + 面部彩绘 UV (W)
OptionColor    (+0x80): 种族特征颜色
```

- **skin.shpk pixel shader** 读取 `SkinColor` 来染皮肤颜色，读取 `SkinFresnelValue0` 控制皮肤高光
- **character.shpk pixel shader** 可能不读取这些字段（设计给装备用）
- **风险**：切换后皮肤颜色可能变白/变灰/丢失色调

### 2.4 Shader Key 差异

skin.shpk 特有的 ShaderKey：

| Key | CRC32 | 值 |
|---|---|---|
| CategorySkinType | 0x380CAED0 | GetMaterialValueFace (0xF5673524) / GetMaterialValueBody (0x2BDB45F1) / GetMaterialValueBodyJJM (0x57FF3B64) / **GetMaterialValueFaceEmissive (0x72E697CD)** |

character.shpk 的 ShaderKey 系统完全不同（走 ColorTable 路线，不需要 SkinType 区分）。

### 2.5 Subsurface Scattering (SSS)

skin.shpk 的核心视觉特性是 **次表面散射**：光线穿透皮肤产生半透明温暖质感。这是 shader 内部的像素着色逻辑，不是 CBuffer 参数能控制的。

character.shpk 设计给金属/布料/皮革等装备材质，没有 SSS → 皮肤会显得像塑料/哑光涂料。

### 2.6 SlotSkinMaterials 系统

```csharp
// Human.cs
[FieldOffset(0xB48)] internal FixedSizeArray5<Pointer<MaterialResourceHandle>> _slotSkinMaterials;
```

游戏有一个特殊的 "skin overlay" 系统：当装备材质使用 `characterstockings.shpk` 时，引擎会从 `SlotSkinMaterials` 复制皮肤纹理覆盖到装备材质上。

**这说明引擎原生就认为 skin.shpk 和 character-family shaders 是不同的渲染域**。直接替换可能绕过这个系统的某些隐含前提。

---

## 三、三个候选目标 shader 对比

| 特性 | character.shpk | charactertattoo.shpk | characterlegacy.shpk |
|---|---|---|---|
| ColorTable | ✅ 32 行 (Dawntrail) | ✅ (推测) | ✅ 16 行 (legacy) |
| SSS/皮肤渲染 | ❌ 装备级渲染 | ❓ 未知（名字暗示适合身体） | ❌ 装备级 |
| 独占常量 | 2 个 unknown + g_ScatteringLevel | 无 | 无 |
| 引擎内建 | ✅ slot 1 | ✅ 已收录在 57 shpk 列表 | ✅ |
| 社区使用案例 | 大量 | 极少/未知 | 中等 |
| 视觉风险 | 高（皮肤→塑料） | **中（可能有皮肤特化？）** | 高 |

**charactertattoo.shpk 是最值得测试的目标**：
- 名字字面意思 "角色纹身"，暗示它可能是专门设计用于在身体上渲染贴花的 shader
- 如果它内部保留了 skin-like 的 SSS 渲染但添加了 ColorTable 支持，那它就是完美的转换目标
- **但目前没有任何社区代码使用过它，风险在于完全未知**

---

## 四、可行性评估

### 4.1 技术上可行

已由 IDA 研究确认：
- 只需在 .mtrl 文件中修改 `ShaderPackageName`，引擎自动 fast-path dispatch
- Penumbra `MtrlFile` API 已提供完整的 .mtrl 重写能力
- 不需要 hook shader loading

### 4.2 实际困难

| 困难 | 严重程度 | 说明 |
|---|---|---|
| **皮肤颜色丢失** | 🔴 高 | CustomizeParameter.SkinColor 可能不再被读取，角色皮肤变白 |
| **SSS 丢失** | 🟡 中 | 皮肤失去半透明质感，看起来像塑料 |
| **补全纹理** | 🟢 低 | 需要生成 mask/specular/index 占位纹理，工作量可控 |
| **ColorTable 初始化** | 🟢 低 | 需要写入默认 ColorTable 数据到 mtrl，已有 API |
| **兼容性测试** | 🟡 中 | 需要测试所有种族/性别/体型的渲染正确性 |
| **与 body mod 兼容** | 🟡 中 | 第三方 body mod 可能依赖 skin.shpk 特有行为 |

### 4.3 charactertattoo.shpk 是否有 SSS？

**这是决定 Route C 成败的关键未知量**。

验证方法：
1. 用 Penumbra 的 ShpkFile.cs 解析 `shader/sm5/shpk/charactertattoo.shpk`，比较其 pixel shader 变体数量和 pass 结构
2. 或者直接实验：在 Penumbra 高级编辑中手动将一个 skin mtrl 的 ShaderPackageName 改成 `charactertattoo.shpk`，观察渲染效果
3. 用 IDA 反编译 charactertattoo 的 pixel shader 入口，看是否有 SSS 采样

---

## 五、社区实践调研（联网搜索 2026-04-13）

### 5.1 skin.shpk 原版 Emissive 模式（已确认可用）

来源：[Dawntrail Shader Reference Table](https://xivmodding.com/books/ff14-asset-reference-document/page/dawntrail-shader-reference-table)

**skin.shpk 的完整 Shader Key 定义：**

| Key CRC | 名称 | 可选值 |
|---|---|---|
| **380CAED0** | Skin Type | **BODY**（湿度/tile mask）, **FACE**（嘴唇 mask）, **HRO**（体毛 mask + Mask Alpha 启用）, **EMISSIVE** |
| F52CCF05 | Vertex Color Mode | MASK, COLOR |
| B616DC5A | Texture Mode | DEFAULT, COMPATIBILITY, SIMPLE |
| D2777173 | Decal Mode | OFF, COLOR, ALPHA |

**关键发现：EMISSIVE 是 skin.shpk 原生支持的 shader key 变体！**

当 CategorySkinType = EMISSIVE (0x72E697CD) 时：
- `g_EmissiveColor` 材质常量控制发光颜色
- **Diffuse Alpha 通道**变为发光遮罩（不透明=不发光，透明=全强度发光）
- SkinTattoo 现有的 `MtrlFileWriter` 已经设置了这个 key！

**但有一个关键限制：**
> "As emissive is a shader key on the same level as hrothgar (body hair on skin) you cannot use both emissive and dyeable body hair on skin at the same time using vanilla shaders."

- EMISSIVE 和 HRO（体毛）互斥
- 男性 body mod（TBSE）默认使用 HRO key → 不能同时有体毛和发光
- 女性 body mod（Bibo+, Gen3）默认使用 BODY key → 可以切换到 EMISSIVE

### 5.2 skin.shpk 完整纹理通道表（社区文档确认）

| 通道 | BODY/FACE 模式 | EMISSIVE 模式 | HRO 模式 |
|---|---|---|---|
| Normal R/G | 切线空间法线 | 同左 | 同左 |
| Normal Blue | 皮肤颜色影响（去色化） | 同左 | 皮肤颜色影响 AND 体毛颜色选择 |
| Normal Alpha | Tilemap Mask | Tilemap Mask | 体毛 alpha（与 blue 联动） |
| Mask Red | 高光强度 | 高光强度 | 同左 |
| Mask Green | 粗糙度 | 粗糙度 | 同左 |
| Mask Blue | SSS 厚度 | SSS 厚度 | 体毛视差 |
| Mask Alpha | - | - | 发色高光影响 |
| Diffuse R/G/B | 颜色 | 颜色 | 颜色 |
| **Diffuse Alpha** | **不透明度** | **发光遮罩**（透明=发光） | 不透明度 |

### 5.3 Atramentum Luminis (ALum) 框架

来源：[Atramentum Luminis](https://www.xivmodarchive.com/modid/68013)、社区搜索

**ALum 是社区标准的皮肤发光框架**，被大部分发光纹身 mod 依赖。

工作原理：
- ALum **替换 .shpk 文件本身**（修改版 shader），不仅仅是材质编辑
- 皮肤发光信息放在 **Diffuse Alpha 通道**（全不透明=不发光，全透明=全强度发光）
- 虹膜发光信息放在 **Mask Alpha 通道**
- 原版 shader 忽略这些通道 → ALum 兼容纹理在没有 ALum 时安全降级
- ALum3 版本提供 "16 行可自定义的着色"（类似 colorset 系统）

**对 SkinTattoo 的启示：**
- 证明了 skin.shpk 框架下通过 Diffuse Alpha 作为 emissive mask 是社区验证的方案
- SkinTattoo 可以兼容 ALum（检测 ALum 存在时利用其增强能力）
- SkinTattoo 也可以独立实现类似功能（使用原版 EMISSIVE shader key）

### 5.3.1 ALum3 .pmp 逆向分析（2026-04-13 实物解析）

对比 `The_Body_SE - Specular Restored.pmp`（标准 TBSE）和 `Colorset Tattoo ALum3 Level T AetherFlux.pmp`：

| 特性 | TBSE Specular Restored | AetherFlux (ALum3 Level T) |
|---|---|---|
| ShaderPackage | skin.shpk | **skin.shpk**（未换shader!） |
| DataSetSize | **0** (无 ColorTable) | **512** (有 ColorTable!) |
| ColorTable 格式 | N/A | Legacy 16 rows × 32B/row |
| 纹理数 | 5 | 4 |
| Tex 3 | transparent.tex | **blue.tex**（效果遮罩） |
| Tex 4 | tbse_e.tex | (无) |
| UV Sets | 1 | **2** |
| Shader Key `0x9D4A3204` | `0x1E8ABB16` | **`0xCD5C4FED`** (ALum3 版本) |
| 常量 `0x8F6498D1` | `[0.0, 1.0]` | **`[-5.0, 0.34]`** (ALum emissive 参数) |

**ALum3 ColorTable 内容（16 rows Legacy）:**
```
Row 0-7:  Diff=[1,1,1] Spec=[1,1,1] Emis=[0,0,0]  (默认白色)
Row 8:    Diff=[1,0,0] Spec=[0.07,1,0] Emis=[0.035,0,1]  (纹身行，彩色!)
Row 9-15: Diff=[1,1,1] Spec=[1,1,1] Emis=[0,0,0]  (默认白色)
```

**关键发现：**

1. **ALum3 在 skin.shpk 中塞入了 ColorTable**。原版 skin.shpk 的 DataSetSize=0，但 ALum3 的 mtrl 有 512 字节 ColorTable。这意味着 ALum 修改了 skin.shpk 的 pixel shader 使其能读取 ColorTable 数据。

2. **Shader Key `0x9D4A3204` 是 ALum 的功能开关**。不同的值对应不同的 ALum 版本/功能等级：
   - `0x1E8ABB16` = 标准（无 ColorTable）
   - `0xCD5C4FED` = ALum3 Level T（有 ColorTable, 16 行可自定义着色）

3. **常量 `0x8F6498D1` 是 ALum 的 emissive 控制参数**。`[-5.0, 0.34]` 对应 mod 描述中的 "暗环境阈值" 和 "发光强度" 默认值。

4. **第四个纹理 `blue.tex` 是效果遮罩**。对应 mod 描述中 "a blue cutout of the tattoo instead of common/texture/blue.tex"。

5. **Row 8 是纹身数据行**。Emis=[0.035, 0, 1] 说明纹身区域有蓝色发光。这正是 "16 行可自定义着色" 的实现——每行独立控制 Diffuse/Specular/Emissive。

**结论：ALum3 通过修改 skin.shpk 本身，在保持 skin shader 的 SSS/皮肤渲染质量的同时，为 skin 材质添加了 ColorTable 支持。这是目前最理想的 "既保持皮肤质感又支持 per-area PBR" 的方案，但它是闭源的。**

### 5.4 社区发光纹身 mod 实现方式

| Mod | 方法 | 是否换 shader |
|---|---|---|
| [Colorset tattoo AetherFlux](https://www.xivmodarchive.com/modid/102593) | ALum3 框架 + Diffuse Alpha mask + 16 行 colorset | ❌ 仍然是 skin.shpk + ALum |
| [Glowing Tattoos](https://www.xivmodarchive.com/modid/74793) | ALum 框架 + Bibo+ 纹理 | ❌ skin.shpk + ALum |
| [TBSE Specular Restored](https://www.xivmodarchive.com/modid/88778) | ALum3 + multi 纹理 + 发光纹身参数 | ❌ skin.shpk (HRO key) + ALum |

**结论：没有发现任何社区 mod 将 body 的 skin.shpk 替换为 character.shpk。所有发光纹身都使用 ALum 框架或原版 EMISSIVE shader key。**

### 5.5 charactertattoo.shpk 的真实用途

来源：[Dawntrail Shader Reference](https://xivmodding.com/books/ff14-asset-reference-document/page/dawntrail-shader-reference-table)

**charactertattoo.shpk 是用于面部纹身/ETC 纹理的 shader，不是 body 纹身！**

- Normal Blue = "Mole or Tattoo Color Influence"
- Normal Alpha = Opacity
- Shader Key `24826489` (Sub Color Map): FACE (纹身颜色影响), HAIR (发色影响)
- 用于面部 ETC 材质（痣、刺青、面部彩绘等）
- **不适合作为 body 材质的转换目标**

这推翻了之前 IDA 研究中"charactertattoo.shpk 可能是轻量级 body 转换目标"的假设。

### 5.6 character.shpk 纹理通道表（社区确认）

| 通道 | 用途 |
|---|---|
| Normal R/G | 切线空间法线 |
| Normal Blue | **不透明度**（≠ skin.shpk 的皮肤颜色影响） |
| Mask Red | 高光强度（更接近 metallic） |
| Mask Green | 粗糙度 |
| Mask Blue | 环境遮蔽 AO |
| Diffuse R/G/B | 颜色 |
| Index Red | **Colorset Row Pair (0-16)** |
| Index Green | Colorset Even/Odd Blending |
| Vertex Color 1 Red | Specular Mask |
| Vertex Color 1 Green | Roughness |
| Vertex Color 1 Blue | Diffuse Mask |
| Vertex Color 1 Alpha | Opacity |

**character.shpk vs skin.shpk 的纹理通道差异非常大**：
- Normal Blue 含义完全不同（不透明度 vs 皮肤颜色影响）
- character.shpk 额外需要 Index 纹理（Row pair 选择）
- character.shpk 没有 SSS 厚度通道
- Diffuse Alpha 在 character.shpk 中不是 emissive mask

### 5.7 Shader ID 10: 金属/世界反射

两个 shader 都支持 Shader ID 10：
- **skin.shpk ID 10**: "Metallic/World Reflection" — 将 SSS/视差替换为金属感；blue 通道必须纯黑（非金属区域），中灰获得最佳光泽
- **character.shpk ID 10**: "Wet look/Specular change"

> "Only recommended for power users" — 社区警告 skin.shpk ID 10 的使用需要极高的纹理精度

---

## 六、替代方案：简化 PBR，不做 shader swap

### 6.1 方案描述

放弃 per-layer PBR 独立，接受 skin.shpk 的限制：

- 保留 `g_EmissiveColor` CBuffer hook（所有层共享一个发光颜色）
- **删除 per-layer 的 Roughness / Metalness / Specular / Sheen UI**
- 只保留：
  - Emissive 颜色 + 强度（全局，所有层共享）
  - Normal map alpha 的 emissive mask（per-layer 形状/渐变）
  - DiffuseColor（per-layer，通过合成纹理控制）

### 6.2 用户体验

对于 body skin 贴花：
- ✅ 每层有独立的位置/大小/旋转/颜色/透明度
- ✅ 每层有独立的 emissive **形状**（通过 normal.a mask）
- ❌ 所有层共享同一个 emissive **颜色**
- ❌ 无法 per-layer 控制 roughness/metalness

对于 equipment/hair/iris 材质（已有 ColorTable）：
- ✅ 完整 per-layer PBR（通过 ColorTable row pairs）
- ✅ 完整 emissive 颜色独立

### 6.3 工作量

极小 — 只需删除 skin.shpk 材质在 UI 中的 PBR 滑条，保留 emissive 滑条。现有代码几乎不需要改动。

---

## 七、原版 .shpk 文件逆向解析（2026-04-13 Penumbra 导出）

使用 Python 解析器（基于 Penumbra `ShpkFile.cs` 格式定义）解析了从游戏中导出的原版 shader 包。

### 7.1 文件概况

| | skin.shpk | character.shpk |
|---|---|---|
| 文件大小 | 10,527,598 bytes (~10MB) | 24,786,261 bytes (~24MB) |
| Vertex Shaders | 72 | 176 |
| Pixel Shaders | 384 | 1,038 |
| MatParamsSize | **320** | **320** (相同!) |
| MatParams Count | **60** | **60** (相同!) |
| HasDefaults | true | true |
| Constants (cbuffer) | 22 | 18 |
| Samplers | 13 | 15 |
| Textures | 27 | 27 |
| System Keys | 0 | 0 |
| Scene Keys | 8 | 9 |
| **Material Keys** | **3** | **4** |

### 7.2 Material Params（CBuffer 常量）—— 完全相同

**skin.shpk 和 character.shpk 的 60 个 material params CRC、offset、size 完全一致。** 这证实了之前 Names.cs 分析的结论——两个 shader 共享同一套 CBuffer 布局。

完整常量表（共 60 项，两个 shader 完全相同）：

```
CRC          名称                              offset  size  默认值
0x29AC0223   (unknown)                         12      4     [0.000]
0xD925FF32   (unknown)                         28      4     [0.500]
0x59BDA0B1   g_ShaderID                        44      4     [0.000]
0x2C2A34DD   g_DiffuseColor                    0       12    [1.000, 1.000, 1.000]
0xCB0338DC   g_SpecularColorMask               16      12    [1.000, 1.000, 1.000]
0x3632401A   g_LipRoughnessScale               60      4     [0.700]
0x11C90091   g_WhiteEyeColor                   32      12    [1.000, 1.000, 1.000]
0x074953E9   g_SphereMapIndex                  76      4     [0.000]
0x38A64362   g_EmissiveColor                   48      12    [0.000, 0.000, 0.000]
0xB7FA33E2   g_SSAOMask                        92      4     [1.000]
0x4255F2F4   g_TileIndex                       108     4     [0.000]
0x2E60B071   g_TileScale                       112     8     [16.000, 16.000]
0x12C6AC9F   g_TileAlpha                       144     4     [1.000]
0x15B70E35   (unknown)                         148     4     [0.000]
0xB5545FBB   g_NormalScale                     152     4     [1.000]
0x800EE35F   g_SheenRate                       156     4     [0.000]
0x1F264897   g_SheenTintRate                   160     4     [0.000]
0xF490F76E   g_SheenAperture                   164     4     [1.000]
0x641E0F22   (unknown)                         168     4     [1.000]
0xD26FF0AE   (unknown)                         172     4     [1.000]
0x37DEA328   g_IrisUvRadius                    176     4     [0.200]
0xE18398AE   g_IrisRingUvRadius                120     8     [0.158, 0.174]
0x5B608CFE   g_IrisRingUvFadeWidth             128     8     [0.040, 0.020]
0x50E36D56   g_IrisRingColor                   64      12    [1.000, 1.000, 1.000]
0x7DABA471   g_IrisRingEmissiveIntensity       180     4     [0.250]
0x285F72D2   g_IrisRingOddRate                 184     4     [0.000]
0x58DE06E2   g_IrisRingForceColor              80      12    [0.000, 0.000, 0.000]
0x66C93D3E   g_IrisThickness                   188     4     [0.500]
0x29253809   g_IrisOptionColorRate             192     4     [0.000]
0x8EA14846   g_IrisOptionColorEmissiveRate     196     4     [0.000]
0x7918D232   g_IrisOptionColorEmissiveIntens.  200     4     [1.000]
0x1A60F60E   (unknown)                         136     8     [0.000, 0.000]
0xD62BF368   g_AlphaAperture                   204     4     [2.000]
0xD07A6A65   g_AlphaOffset                     208     4     [0.000]
0xAD94E254   (unknown)                         212     4     [0.000]
0xAE4F649C   (unknown)                         216     4     [0.000]
0x7801E004   g_GlassIOR                        220     4     [1.000]
0xC4647F37   g_GlassThicknessMax               224     4     [0.010]
0x39551220   g_TextureMipBias                  228     4     [0.000]
0xB61D7498   (unknown)                         232     4     [0.000]
0x623CC4FE   g_OutlineColor                    96      12    [0.000, 0.000, 0.000]
0x8870C938   g_OutlineWidth                    236     4     [0.000]
0xDF15112D   g_ToonIndex                       240     4     [0.000]
0x00A680BC   (unknown)                         244     4     [0.000]
0x2B5EB116   (unknown, angle?)                 248     4     [-45.000]
0x5C598180   (unknown, angle?)                 252     4     [45.000]
0x3CCE9E4C   g_ToonLightScale                  256     4     [2.000]
0x759036EE   g_ToonLightSpecAperture           260     4     [50.000]
0x6C159E95   (unknown)                         264     4     [0.850]
0xD96FAF7A   g_ToonReflectionScale             268     4     [2.500]
0x5351646E   g_ShadowPosOffset                 272     4     [0.000]
0x6421DD30   g_TileMipBiasOffset               276     4     [0.000]
0x43345395   (unknown)                         280     4     [1.000]
0x4172EDCC   (unknown)                         284     4     [1.000]
0x738A241C   (unknown)                         288     4     [0.000]
0x71CC9A45   (unknown)                         292     4     [0.000]
0xDA3D022F   (unknown)                         296     4     [1.000]
0xD87BBC76   (unknown)                         300     4     [1.000]
0xEA8375A6   (unknown)                         304     4     [0.000]
0xE8C5CBFF   (unknown)                         308     4     [0.000]
```

**g_EmissiveColor 在两个 shader 中 offset=48, size=12，完全一致** → EmissiveCBufferHook 的 CRC 查表机制对两个 shader 通用。

### 7.3 Material Keys —— 核心差异

| skin.shpk (3 keys) | character.shpk (4 keys) |
|---|---|
| **CategorySkinType** (0x380CAED0) default=ValFace | **TextureMode** (0xB616DC5A) default=0x5CC605B5 |
| DecalMode (0xD2777173) | **FlowMapMode** (0x40D1481E) default=0x337C6BC4 |
| VertexColorMode (0xF52CCF05) | DecalMode (0xD2777173) |
| | VertexColorMode (0xF52CCF05) |

**CategorySkinType** 是 skin.shpk 独有的 material key，控制 Body/Face/HRO/Emissive 四种变体。character.shpk 用 **TextureMode** 和 **FlowMapMode** 替代。这两个 key 系统完全不兼容。

### 7.4 Constants (CBuffer Descriptors) —— 重大差异

**skin.shpk 独有的 constants（22 个中 character.shpk 没有的）：**

| CRC | 名称 | 用途 |
|---|---|---|
| 0xD35B646A | **g_ConnectionVertex** | 顶点连接（身体接缝？） |
| 0xE6E8672F | **g_ShapeDeformParam** | 体型变形参数 |
| 0x2A4B3583 | **g_CustomizeParameter** | ⚠️ **皮肤染色！** 读取角色的 SkinColor/LipColor 等 |
| 0x17FB799E | **g_WrinklessWeightRate** | 皱纹权重（面部细节） |

**character.shpk 独有的 constants：**

无额外 constant（character 的 18 个 constant 都是 skin 22 个的子集）。

**⚠️ g_CustomizeParameter 是最关键的差异**：skin.shpk 通过这个 cbuffer 读取 `Human.CustomizeParameterCBuffer`（包含 SkinColor、LipColor、EyeColor 等角色定制数据）。character.shpk 没有这个 constant → **切换后角色皮肤颜色、嘴唇颜色全部丢失**。

### 7.5 Samplers —— 差异

**skin.shpk 独有的 samplers（13 个中）：**

| 名称 | 用途 |
|---|---|
| g_SamplerNormal2 | 第二法线贴图（细节法线？） |
| g_SamplerWrinklesMask | 皱纹遮罩（面部表情） |

**character.shpk 独有的 samplers（15 个中）：**

| 名称 | 用途 |
|---|---|
| **g_SamplerIndex** | ColorTable row 选择纹理 |
| **g_SamplerTable** | ColorTable 纹理本体 |
| g_SamplerSphereMap | 球形贴图（装备光泽） |
| tPerlinNoise2D | Perlin 噪声纹理 |

**共享的 samplers：**
g_SamplerNormal、g_SamplerDecal、g_SamplerTileOrb、g_SamplerGBuffer、g_SamplerReflectionArray、g_SamplerOcclusion、g_SkySampler、g_FogWeightLutSampler、g_SamplerDither、g_DissolveSampler、g_CompositeCommonSampler

### 7.6 Textures —— 差异

**skin.shpk 独有的 textures：**

| 名称 | 用途 |
|---|---|
| g_InputConnectionVertex | 连接顶点输入 |
| g_InputConnectionVertexPrev | 前帧连接顶点 |
| g_ShapeDeformVertex | 体型变形顶点 |
| g_ShapeDeformIndex | 体型变形索引 |
| g_SamplerNormal2 | 第二法线（texture ref） |
| g_SamplerWrinklesMask | 皱纹遮罩（texture ref） |

**character.shpk 独有的 textures：**

| 名称 | 用途 |
|---|---|
| **g_SamplerIndex** | ColorTable 行选择（Index 纹理） |
| **g_SamplerTable** | ColorTable 纹理 |
| g_SamplerSphereMap | 球形贴图 |
| tPerlinNoise2D | 噪声纹理 |
| g_SamplerFlow | Flow map 纹理 |
| g_SamplerDepthWithWater | 水深纹理 |

**共享的 material textures：**
g_SamplerDiffuse、g_SamplerNormal、g_SamplerMask

### 7.7 解析结论

1. **CBuffer 常量 100% 相同**：两个 shader 的 60 个 material params 完全一致（CRC、offset、size、默认值全部相同）。这意味着 CBuffer hook（EmissiveCBufferHook）对两个 shader 通用。

2. **Material Keys 不兼容**：skin.shpk 的 CategorySkinType (Body/Face/HRO/Emissive) 在 character.shpk 中不存在。换 shader 后 EMISSIVE 模式不可用。

3. **g_CustomizeParameter 是无法逾越的鸿沟**：skin.shpk 依赖这个 cbuffer 读取角色皮肤颜色。character.shpk 没有这个 constant，且无法通过添加 material params 来模拟（因为 cbuffer 是 shader 内部硬编码绑定的）。

4. **g_SamplerIndex + g_SamplerTable 是 ColorTable 的物理基础**：character.shpk 通过 Index 纹理选择 ColorTable 行，通过 Table 纹理提供 ColorTable 数据。skin.shpk 没有这两个 sampler → 原版 skin.shpk 无法读取 ColorTable。

5. **ALum3 的方案被进一步证实**：ALum3 在 skin.shpk mtrl 中塞入了 ColorTable 数据（DataSetSize=512）。但原版 skin.shpk 没有 g_SamplerTable 采样器 → ALum 必须修改了 skin.shpk 本身来添加 ColorTable 读取能力。这与 "ALum 替换 .shpk 文件" 的社区描述吻合。

---

## 八、ALum skin.shpk 逆向分析（2026-04-13 实物）

### 8.1 ALum 修改的 shader 文件一览

ALum 替换了 **7 个** shader 包，并附带 devkit JSON：

| 文件 | 大小 | 原版大小 | 版本 |
|---|---|---|---|
| skin.shpk | 797KB | 10MB | 0x0B01 (旧版格式!) |
| skin1.shpk | 283KB | N/A | 用途未知 |
| character.shpk | 2.5MB | 24MB | - |
| characterglass.shpk | 113KB | - | - |
| charactershadowoffset.shpk | 2.5MB | - | - |
| hair.shpk | 469KB | - | - |
| iris.shpk | 244KB | - | - |
| iris1.shpk | 96KB | N/A | 用途未知 |

**ALum 的 skin.shpk 版本是 0x0B01，远小于原版的 0x0D01。** 这是旧版 shader 格式（Penumbra 代码中 `version < 0x0D01` 走不同解析路径）。ALum 可能基于旧版 shader 修改后重新打包。

### 8.2 ALum Shader Key: "Atramentum Luminis Mode"

DevKit JSON 完整定义了 ALum 的三级功能系统：

**Shader Key `0x9D4A3204` — Atramentum Luminis Mode：**

| 值 | 名称 | 说明 |
|---|---|---|
| `0x698D8B80` | **Level 2** | 添加效果，纹理数量与原版相同 |
| `0x1E8ABB16` | **Level 3** | 更多效果，需要额外纹理 |
| `0xCD5C4FED` | **Level T** | 添加 **Color Table**，需要额外纹理 |

**Level T 是关键** — 这就是 AetherFlux 纹身 mod 使用的模式，它在 skin.shpk 中启用了 ColorTable 支持。

### 8.3 纹理通道定义（随 ALum Mode 变化）

**Normal Map (`0x0C5EC1F1`) — Alpha 通道随模式变化：**

| Level 2/3 | Level T |
|---|---|
| Alpha: **Unused** | Alpha: **Color row index** ← ColorTable 行选择！ |
| Blue: Opacity (threshold) | Blue: Opacity (threshold) |

**Diffuse Map (`0x115306BE`) — Alpha 通道随模式变化：**

| Level 2/3 | Level T |
|---|---|
| Alpha: **Emissive conversion (inverted)** | Alpha: **Diffuse Brightness when Emissive on** |

**Effect Map (`0xFF254304`, ALum 新增纹理) — 随模式变化：**

| Level 2/3 | Level T |
|---|---|
| R: Iridescence | R: Iridescence |
| G: Wetness | G: Wetness |
| B: **Metallic finish** | B: **Emissive Brightness** |
| A: Legacy bloom | A: Legacy bloom |

**Emissive Map (`0x2596EE18`, ALum 新增纹理)：**
- R/G/B: Emissive map（发光颜色贴图）
- A: Level 2 emissive 透过遮罩

### 8.4 ALum Emissive 控制常量

**常量 `0x8F6498D1` — 随 ALum Mode 含义变化：**

| Level 2/3 | Level T |
|---|---|
| [0]: Emissive **Conversion** (well-lit) | [0]: Emissive **Strength** (well-lit) |
| [1]: Emissive **Conversion** (dark) | [1]: Emissive **Strength** (dark) |

AetherFlux 中的值 `[-5.0, 0.34]` = [暗环境 Strength, 亮环境 Strength]

### 8.5 ALum Level T 的 ColorTable 机制（确认）

从 devkit JSON 确认了完整的工作流程：

1. **Normal Alpha → Color row index**：Level T 模式下，法线贴图的 alpha 通道作为 ColorTable 行索引（与 character.shpk 的 Index 纹理类似功能，但复用了 normal alpha）
2. **ColorTable 内嵌在 mtrl 中**：DataSetSize=512 = Legacy 16 行 × 32 bytes/行
3. **每行独立 Diffuse/Specular/Emissive**：从 AetherFlux 解析确认（Row 8 有彩色数据）
4. **Emissive Brightness 在 Effect Map Blue 通道**：Level T 用 Effect 纹理的蓝通道控制每像素的发光强度
5. **Emissive Map 提供发光颜色贴图**：独立的 RGB emissive 纹理（`0x2596EE18`）

### 8.6 ALum 其他功能

**Iridescence（虹彩效果）— 常量 `0x4103FEEF`：**
- 8 个参数：Effect Strength, Scale Detection, Normal Z Bias, Chroma, Hue Shift, Hue Multiplier
- 通过 Effect Map Red 通道控制区域

**Wetness（湿润效果）：** Effect Map Green 通道
**Metallic（金属效果）：** Effect Map Blue 通道（Level 2/3 only）
**Legacy Bloom — 常量 `0xA5EDBE5C`：** 模拟 ALum 2.x 的 bloom 效果

**Hair Influence — 常量 `0xD367C386`：** Body 模式下的体毛启用开关（替代原版 HRO key）
**Asymmetry Adapter — 常量 `0x5E3ABDFB`：** 对称/非对称模型+纹理适配

### 8.7 对 SkinTattoo 的重大启示

1. **ALum Level T 证明了 "skin.shpk + ColorTable" 路线是可行的**，且不会丢失 SSS/皮肤颜色（因为 ALum 仍然基于 skin shader 的渲染管线）

2. **Normal Alpha 作为 Color row index** 是 ALum 的关键创新 — 原版 skin.shpk 中 normal alpha 是 tilemap mask，ALum 在 Level T 模式下将其重新定义为 ColorTable 行选择器

3. **ALum 使用旧版 shader 格式 (0x0B01)**，这意味着它不是在原版 0x0D01 基础上修改的，而是从更早的 shader 版本分叉开发的。这可能导致与未来游戏更新的兼容性问题。

4. **SkinTattoo 的可选路径：**
   - **依赖 ALum**：检测 ALum Level T 是否激活 → 直接利用 ColorTable + normal.a row index → per-layer 独立 PBR
   - **自制 skin.shpk**：参考 ALum 的 devkit 设计，自己修改 skin.shpk 添加 ColorTable 支持。ALum 的 devkit JSON 几乎就是完整的设计规格书。
   - **ALum 兼容模式**：SkinTattoo 不修改 shader，但生成的 mtrl/纹理兼容 ALum 格式。如果用户装了 ALum 就有 per-layer PBR，没装就 fallback 到全局 emissive。

---

## 九、DXBC 反编译：skin.shpk Emissive 精确计算公式（2026-04-13）

使用 D3DCompiler_47.dll 的 D3DDisassemble API 反编译了原版 skin.shpk 的 384 个 PS 变体。

### 9.1 扫描结果

- 384 个 PS 中，**66 个引用了 g_EmissiveColor (cb0[3])**
- PS[19] 被确认为 EMISSIVE 变体的 lighting pass shader
- PS[0] 是 Face 默认变体（不含 emissive 逻辑）

### 9.2 PS[19] Resource Bindings

```
Texture Bindings:
  t0 = g_SamplerLightDiffuse     (光照漫反射 GBuffer)
  t1 = g_SamplerLightSpecular    (光照高光 GBuffer)
  t2 = g_SamplerGBuffer1         (GBuffer 1)
  t3 = g_SamplerGBuffer2         (GBuffer 2)
  t4 = g_SamplerGBuffer.T        (GBuffer 主)
  t5 = g_SamplerNormal.T         ← 法线贴图 (emissive mask 来源!)
  t6 = g_SamplerMask.T           (遮罩贴图)
  t7 = g_SamplerTileOrb.T        (Tile 贴图数组)
  t8 = g_SamplerReflectionArray  (环境反射)
  t9 = g_SamplerOcclusion.T      (遮蔽贴图)

CBuffer Bindings:
  cb0 = g_MaterialParameter      ← 包含 g_EmissiveColor
  cb1 = g_CommonParameter
  cb2 = g_PbrParameterCommon
  cb3 = g_CameraParameter
  cb4 = g_InstanceParameter
  cb5 = g_MaterialParameterDynamic
  cb6 = g_AmbientParam
  cb7 = g_ShaderTypeParameter
```

### 9.3 Emissive 精确计算公式（DXBC 反编译）

```hlsl
// Line 267: 采样法线贴图
float2 normalSample = SampleBias(g_SamplerNormal, uv, mipBias).wz;
float emissiveMask = normalSample.y;  // = Normal.alpha (t5.w)

// Line 268-269: 计算 emissive 颜色
float3 emissiveColor = g_EmissiveColor.rgb * g_EmissiveColor.rgb;  // 自乘 (gamma² → linear)
float3 emissive = emissiveMask * emissiveColor;

// Line 725: 乘以动态材质参数
emissive *= g_MaterialParameterDynamic[0].rgb;  // cb5[0].xyz

// Line 728-733: 混入最终光照输出
float luminance = dot(lighting.rgb, float3(0.299, 0.587, 0.114));
float3 emissiveContrib = emissive * max(luminance, 1.0);
finalColor += emissiveContrib;

// Line 744-747: 后处理输出
finalColor *= InstanceParameter.MulColor.rgb;  // 实例颜色
finalColor = sqrt(max(finalColor, 0));          // linear → gamma
output = finalColor * CommonParameter.scale;    // 最终缩放
```

### 9.4 核心结论

**Emissive Mask = Normal Map Alpha (t5.w)**
- 这与 SkinTattoo 现有实现一致 — 已经将 emissive mask 写入 normal.a composite
- 社区文档说 "Diffuse Alpha = emissive mask" 可能是指 ALum 的行为，不是原版 shader

**Emissive Color = g_EmissiveColor.rgb² (per-material)**
- CBuffer 常量，经过自乘转换（gamma→linear）
- 对整个材质统一，**不存在 per-pixel 颜色变化**
- 没有任何纹理 RGB 参与 emissive 颜色计算

**g_MaterialParameterDynamic 是 per-material cbuffer**
- 不是 per-pixel，无法用于颜色差异化

**结论：原版 skin.shpk 中 per-pixel emissive 颜色不可能实现。**
- Per-pixel 能控制的：**强度**（Normal Alpha mask）
- Per-material 能控制的：**颜色**（g_EmissiveColor CBuffer）
- 同一 skin 材质上所有贴花层共享同一发光颜色

### 9.5 对实现方案的影响

| 方案 | 可行性 | 说明 |
|---|---|---|
| 全局 emissive 颜色 + per-layer mask | ✅ 完全可行 | 现有架构直接支持 |
| Per-layer 独立 emissive 颜色 | ❌ 不可能（原版 shader） | 需要修改 .shpk 或用 ALum |
| 白色 emissive + 彩色 diffuse 模拟 | 🟡 部分可行 | g_EmissiveColor=[1,1,1]，在 diffuse 中烘焙颜色，视觉上接近但非真正彩色发光 |

**"白色 emissive + 彩色 diffuse" 方案解释：**
- 设置 g_EmissiveColor = (1, 1, 1)（白色发光）
- 在 diffuse 纹理的发光区域烘焙用户选择的颜色
- 效果：发光区域的 diffuse 是彩色的，emissive 是白色但被 diffuse 颜色调制
- 在暗环境中看起来接近彩色发光，但亮环境中 diffuse 颜色可能不自然
- 这实际上也是很多 mod 作者使用的技巧

---

## 十、最终决策建议

### 方案全景对比

| | Route C (skin->character) | 原版 EMISSIVE Key | ALum 兼容 | 简化 PBR |
|---|---|---|---|---|
| 开发成本 | 🔴 高 | 🟢 低 | 🟡 中 | 🟢 极低 |
| 视觉质量 | 🔴 SSS 丢失 | ✅ 保持原版 | ✅ 保持原版 | ✅ 保持原版 |
| Per-layer emissive 颜色 | ✅ 完全独立 | ❌ 全局颜色 | ❓ 取决于 ALum | ❌ 全局颜色 |
| Per-layer emissive 形状 | ✅ ColorTable rows | ✅ Diffuse Alpha | ✅ Diffuse Alpha | ✅ Normal Alpha |
| Per-layer PBR | ✅ 完整 | ❌ 无 | ❓ ALum3 部分支持 | ❌ 无 |
| 与 HRO 体毛兼容 | 🟡 未知 | ❌ **互斥** | 🟡 取决于 ALum | ✅ 不影响 |
| 社区先例 | ❌ **无人做过** | ✅ 少量使用 | ✅ **社区标准** | N/A |
| 兼容性风险 | 🔴 高 | 🟡 中 | 🟡 依赖外部 mod | ✅ 无 |

### 推荐路径（更新后）

**短期**（当前版本）：

→ **简化 skin.shpk 的 PBR UI，保留全局 emissive。**

- 删除 skin.shpk 材质上的 per-layer Roughness/Metalness/Specular/Sheen
- 保留全局 emissive 颜色 + per-layer emissive mask（现有 normal.a composite）
- equipment/iris/hair 材质保持完整 per-layer PBR

理由：
1. 社区搜索证实 **没有人成功做过 skin->character shader swap**
2. **charactertattoo.shpk 是面部纹身 shader，不适合 body**（推翻之前假设）
3. 所有社区发光纹身 mod 都使用 ALum 或原版 EMISSIVE key，不换 shader
4. 纹理通道含义差异过大，换 shader 后皮肤渲染必然异常

**中期**（增强 skin.shpk emissive 能力）：

→ **利用原版 EMISSIVE shader key + Diffuse Alpha 作为 emissive mask**

- 将 emissive mask 从 normal.a 迁移到 **diffuse alpha**（与社区标准一致）
- 利用 Diffuse Alpha 的 per-pixel 精度实现更精细的发光图案
- g_EmissiveColor 仍然是全局颜色，但 mask 形状可以 per-layer
- 注意：与 HRO（体毛）互斥，需要在 UI 中提示用户

**长期探索**（如果确实需要 per-layer 独立 emissive 颜色）：

→ **研究 ALum3 的 16 行 colorset 实现机制**

- ALum3 在 skin.shpk 框架下实现了 "16 rows of completely customizable coloring"
- 暗示 ALum 修改了 skin.shpk 本身来支持 colorset-like 行为
- 如果能理解 ALum 技术方案，SkinTattoo 可以实现类似功能
- ALum 是闭源的，需要逆向分析其修改的 .shpk 文件

→ **Route C 降为最低优先级**

- 社区零先例 + 纹理通道语义完全不同 + SSS 丢失 → 风险过高

---

## 十一、附录

### 11.1 参考来源

- [Dawntrail Shader Reference Table](https://xivmodding.com/books/ff14-asset-reference-document/page/dawntrail-shader-reference-table)
- [7.0 Colorsetting Guide](https://xivmodding.com/books/general-mod-creation/page/70-colorsetting-guide)
- [Atramentum Luminis (ALum)](https://www.xivmodarchive.com/modid/68013)
- [Colorset tattoo AetherFlux](https://www.xivmodarchive.com/modid/102593)
- [TBSE Specular Restored](https://www.xivmodarchive.com/modid/88778)
- [7.0 Glowing Vanilla Eyes](https://www.xivmodarchive.com/modid/113113)
- [Glowing Eye Color Picker](https://xivmodarchive.com/modid/136799)
- [Asym Sclera Guide](https://xivmodding.com/books/general-mod-creation/page/asym-sclera)
- [7.0+ Eyes shader DevKit](https://www.xivmodarchive.com/modid/112543)
- [Penumbra Advanced Editing](https://github.com/xivdev/Penumbra/wiki/Advanced-Editing)

### 11.2 工具与脚本

- `_shpk_analysis/parse_shpk.py` — Python .shpk 解析器（基于 Penumbra ShpkFile.cs 格式）
- `_shpk_analysis/extract_shpk.py` — SqPack 提取工具（未完成，CRC 匹配问题）
- `_shpk_analysis/aetherflux/` — AetherFlux (ALum3) pmp 解压
- `_shpk_analysis/tbse/` — TBSE Specular Restored pmp 解压

### 11.3 实验验证清单（如仍要推进 Route C）

- [ ] 手动在 Penumbra 高级编辑中改 body mtrl ShaderPackageName -> `character.shpk`
- [ ] 观察：皮肤颜色是否正确？SSS 是否保留？基础渲染是否崩溃？
- [ ] 补全 Index 纹理 + ColorTable -> 测试 row pair 选择
- [ ] 用 TextureSwapService 写入不同 emissive 到不同 row -> 验证 per-layer 独立
- [ ] **注意**：需要重新映射所有纹理通道（Normal Blue, Diffuse Alpha 含义不同）
