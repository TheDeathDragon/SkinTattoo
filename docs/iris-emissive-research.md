# Iris (眼睛虹膜) 发光功能研究报告

## 结论

**完全可以实现**，且 SkinTattoo 现有架构（EmissiveCBufferHook）无需大幅修改即可支持 iris 材质的实时发光控制。

---

## 一、参考 mod 分析："Glowing Eye Color Picker"

### 1.1 mod 结构

- 格式：`.pmp` (Penumbra mod package)
- 每种族/性别一个 option group，每组 8 个颜色预设 + Off
- 每个预设映射所有脸型的 `iri_a.mtrl` 到同一个修改过的 mtrl 文件
- 映射路径示例：`chara/human/c0101/obj/face/f0001/material/mt_c0101f0001_iri_a.mtrl` → `chara/Hyur/Midlander/Male/Preset 1 - Red.mtrl`

### 1.2 mtrl 文件解析

每个 preset mtrl 文件 548 字节，结构：

| 字段 | 值 |
|---|---|
| ShaderPackage | **iris.shpk** (未更换 shader) |
| Textures | `eye11_base.tex`, `eye01_norm.tex`, `eye01_mask.tex` |
| ColorSet | colorSet1 (DataSetSize=0，无内嵌 ColorTable) |
| ShaderKey | `0x63030C80 = 0xEFDEA8F6` |
| ShaderValues | 136 bytes, 21 个常量 |

### 1.3 各颜色预设的关键常量差异

| 常量 | Red | Blue | Green | Yellow | Purple | Pink | White | Light Blue |
|---|---|---|---|---|---|---|---|---|
| **g_EmissiveColor** | `[0.45, 0, 0]` | `[0, 0, 0.45]` | `[0.09, 0.45, 0]` | - | - | - | `[0.35, 0.35, 0.35]` | - |
| g_DiffuseColor | `[1, 1, 1]` | `[1.4, 1.4, 1.4]` | `[1.4, 1.4, 1.4]` | - | - | - | `[1.4, 1.4, 1.4]` | - |

**核心发现**：mod 仅修改 `g_EmissiveColor` 来控制发光颜色/强度，其余参数几乎不变。

### 1.4 完整常量表 (Preset 1 - Red)

```
0x29AC0223:          offset=0,   size=4   -> [0.000000]
g_Roughness:         offset=4,   size=4   -> [1.000000]
g_DiffuseColor:      offset=8,   size=12  -> [1.000000, 1.000000, 1.000000]
g_NormalScale:       offset=20,  size=12  -> [0.800000, 0.800000, 0.800000]
g_WhiteEyeColor:     offset=32,  size=12  -> [0.720000, 0.693120, 0.669600]
0x074953E9:          offset=44,  size=4   -> [0.000000]
g_EmissiveColor:     offset=48,  size=12  -> [0.450000, 0.000000, 0.000000]
0xB7FA33E2:          offset=60,  size=4   -> [1.000000]
0xB5545FBB:          offset=64,  size=4   -> [1.000000]
g_IrisUvRadius:      offset=68,  size=4   -> [0.200000]
g_IrisRingUvRadius:  offset=72,  size=8   -> [0.150000, 0.182000]
g_IrisRingUvFadeWidth: offset=80, size=8  -> [0.010000, 0.010000]
g_IrisRingEmissiveIntensity: offset=88, size=4 -> [1.000000]
g_IrisRingOddRate:   offset=92,  size=4   -> [1.000000]
g_IrisRingForceColor: offset=96, size=12  -> [0.000000, 0.000000, 0.000000]
g_IrisThickness:     offset=108, size=4   -> [1.000000]
g_IrisOptionColorRate: offset=112, size=4 -> [0.000000]
g_IrisOptionColorEmissiveRate: offset=116, size=4 -> [0.000000]
g_IrisOptionColorEmissiveIntensity: offset=120, size=4 -> [1.000000]
0x1A60F60E:          offset=124, size=8   -> [0.300000, 0.000000]
0x39551220:          offset=132, size=4   -> [0.000000]
```

---

## 二、iris.shpk Shader 常量定义

### 2.1 Emissive 相关常量

| 常量名 | CRC32 | 类型 | 默认值 | 说明 |
|---|---|---|---|---|
| **g_EmissiveColor** | 0x38A64362 | float3 (12B) | `[0,0,0]` | 主发光色 RGB |
| g_IrisRingEmissiveIntensity | 0x7DABA471 | float (4B) | 0.25 | Limbal ring 发光强度 |
| g_IrisOptionColorEmissiveRate | 0x8EA14846 | float (4B) | 0.0 | 自定义颜色发光混合率 |
| g_IrisOptionColorEmissiveIntensity | 0x7918D232 | float (4B) | 1.0 | 自定义颜色发光乘数 |

### 2.2 外观控制常量

| 常量名 | CRC32 | 类型 | 默认值 | 说明 |
|---|---|---|---|---|
| g_WhiteEyeColor | 0x11C90091 | float3 (12B) | `[1,1,1]` | 巩膜颜色 (可做黑巩膜) |
| g_IrisRingColor | 0x50E36D56 | float3 (12B) | `[1,1,1]` | Limbal ring 颜色 |
| g_IrisRingForceColor | 0x58DE06E2 | float3 (12B) | `[0,0,0]` | 强制 ring 颜色覆盖 |
| g_DiffuseColor | 0x2C2A34DD | float3 (12B) | `[1,1,1]` | 漫反射颜色乘数 |

### 2.3 几何/Parallax 常量

| 常量名 | CRC32 | 类型 | 默认值 | 说明 |
|---|---|---|---|---|
| g_IrisUvRadius | 0x37DEA328 | float (4B) | 0.2 | 瞳孔 UV 半径 |
| g_IrisThickness | 0x66C93D3E | float (4B) | 0.5 | 虹膜深度/厚度 (视差效果) |
| g_IrisRingUvRadius | 0xE18398AE | float2 (8B) | `[0.158, 0.174]` | Limbal ring 内外半径 |
| g_IrisRingUvFadeWidth | 0x5B608CFE | float2 (8B) | `[0.04, 0.02]` | Limbal ring 羽化宽度 |
| g_IrisRingOddRate | 0x285F72D2 | float (4B) | ~0 | Ring 条纹奇偶比率 |

---

## 三、Emissive Mask 机制

### 3.1 发光前提条件

iris.shpk pixel shader 的发光公式（推断）：

```hlsl
float emissiveMask = maskTexture.Sample(sampler, uv).r;  // mask 红通道
float3 finalEmissive = g_EmissiveColor * emissiveMask;
```

**关键**：如果 mask 纹理的红通道为 0，无论 g_EmissiveColor 设多大都不会发光。

### 3.2 "Glow Compatible" 眼睛

mod 作者说明：
> "This should work with any eyes deemed 'glow compatible' or 'glow ready'
> (this means the eye's mask layer has the red channel, emissive, set up correctly)."

- 原版眼睛的 mask 红通道可能为 0 → 无法发光
- 许多第三方眼睛 mod 已预设好 emissive mask → 直接可用
- 解决方案：可以额外生成/修改 mask 纹理注入白色红通道

### 3.3 纹理引用

mod 中使用的纹理路径（所有预设共享）：
- `chara/common/texture/eye/eye11_base.tex` — 基础颜色纹理
- `chara/common/texture/eye/eye01_norm.tex` — 法线贴图
- `chara/common/texture/eye/eye01_mask.tex` — Mask 纹理（红通道 = emissive mask）

---

## 四、SkinTattoo 架构适配分析

### 4.1 现有 EmissiveCBufferHook 直接兼容 iris.shpk

| 特性 | skin.shpk (已支持) | iris.shpk (待支持) | 差异 |
|---|---|---|---|
| g_EmissiveColor | ✅ CRC 0x38A64362 | ✅ CRC 0x38A64362 | **完全相同** |
| DataSetSize | 0 (无 ColorTable) | 0 (无 ColorTable) | 相同 |
| CBuffer hook 可用 | ✅ | ✅ | 相同 |
| LoadSourcePointer | ✅ | ✅ | 通用机制 |

**EmissiveCBufferHook 的 CRC 查表机制是通用的**：
1. 从 `MaterialResourceHandle` 获取 `ShaderPackageResourceHandle`
2. 遍历 `ShaderPackage.MaterialElementsSpan` 查找 CRC = 0x38A64362
3. 获得 byte offset，通过 `LoadSourcePointer` 写入新值

这个流程对任何 shader package 都适用，不区分 skin/iris/character/hair。

### 4.2 iris 材质路径模式

游戏中虹膜材质的路径格式：
```
chara/human/c{raceGenderCode}/obj/face/f{faceId}/material/mt_c{code}f{faceId}_iri_a.mtrl
```

示例：
- `chara/human/c0101/obj/face/f0001/material/mt_c0101f0001_iri_a.mtrl` (Midlander Male, Face 1)
- `chara/human/c0201/obj/face/f0001/material/mt_c0201f0001_iri_a.mtrl` (Midlander Female, Face 1)

`TexPathParser` 已识别 `_iri_` 后缀（`RoleSuffix = "iri"`）。

### 4.3 iris 材质在 CharacterBase 中的位置

角色的 face model 通常在 `CharacterBase.Models[1]`（slot 1 = face），iris 材质是 face model 的第 3 或 4 个 material slot（`MaterialIdx` varies by face model）。

可通过遍历 face model 的所有 materials，检查 `MaterialResourceHandle` 文件名是否包含 `_iri_` 来定位。

---

## 五、实现方案

### 方案 A：实时 CBuffer Hook（推荐）

零闪烁，1 帧延迟，无需 Penumbra redraw。

**改动清单：**

1. **EmissiveCBufferHook.cs** — 支持多个 CRC 常量（当前只有 g_EmissiveColor）
   - 新增 CRC 字典：`g_IrisRingEmissiveIntensity`, `g_WhiteEyeColor` 等
   - `SetTargetByPath` 扩展为接受 `Dictionary<uint, float[]>` 参数

2. **PreviewService.cs** — 新增 iris 材质查找
   - 遍历 face model materials 查找 `_iri_` 路径
   - 注册到 EmissiveCBufferHook

3. **UI** — Iris 发光控制面板
   - Emissive 颜色选择器 + 强度滑条
   - 可选：Limbal ring 发光控制、巩膜颜色
   - 可选：Iris 几何参数（thickness, radius）

**数据流：**
```
UI: Iris 颜色/强度滑条
  ↓
SetIrisEmissiveTarget(charBase, color)
  ↓ 查找 face model 的 _iri_ 材质
  ↓ 获取 MaterialResourceHandle 指针
  ↓
EmissiveCBufferHook.SetTargetByPath(...)
  ↓
OnRenderMaterial detour → 匹配 MRH → LoadSourcePointer → 写入 g_EmissiveColor
  ↓
GPU upload → 发光效果实时生效
```

### 方案 B：静态 mtrl 替换

与 mod 做法相同，生成修改过的 `iri_a.mtrl` → Penumbra temp mod。

- 优点：简单，证实可行
- 缺点：需 redraw（闪烁），路径取决于种族/性别/脸型

---

## 六、IDA 反编译确认

### 6.1 OnRenderMaterial (0x14026EE10)

- iris.shpk 的 ShaderPackage 指针不匹配 ModelRenderer 的前两个 cache slot (skin/character)
- 走通用渲染分支（`else` branch），设置标准渲染标志
- CBuffer 的 LoadSourcePointer 机制对所有材质类型通用

### 6.2 Shader 包字符串数组

引擎内建 shader 包字符串数组 (@ 0x14279fc00)：
- [0] skin.shpk (0x14206d3a0)
- [1] character.shpk (0x14206d3c0)
- [2] iris.shpk (0x14206d3e0)
- [3] hair.shpk (0x14206d400)
- [4+] characterglass.shpk, charactertattoo.shpk, ...

iris.shpk 是引擎原生支持的核心 shader 之一。

---

## 七、社区实践确认（联网搜索 2026-04-13）

### 7.1 社区对 iris 发光的共识

来源：[Dawntrail Shader Reference Table](https://xivmodding.com/books/ff14-asset-reference-document/page/dawntrail-shader-reference-table)、[7.0 Glowing Vanilla Eyes](https://www.xivmodarchive.com/modid/113113)、[Glowing Eye Color Picker](https://xivmodarchive.com/modid/136799)、[Asym Sclera Guide](https://xivmodding.com/books/general-mod-creation/page/asym-sclera)

**社区确认的发光机制：**
> "Emissive is now included in the eye shader for ALL eyes, but in order to activate it, you need to mask out where you want glow on the mask RED channel, AND turn the Emissive shader constant on by changing the 3 values to not 0."

- Dawntrail (7.0+) 之后，**所有眼睛都内置了发光功能**，只需满足两个条件
- Mask 红通道 ≠ 0（控制发光区域）+ g_EmissiveColor ≠ 0（控制发光颜色）
- 高于 0.9 的 emissive 值可能产生视觉瑕疵，建议 0.3-0.8 范围
- 所有 pre-DT 眼睛 mod 必须通过 Loose Texture Compiler 或 TexTools Eye Saver 转换

### 7.2 Iris 纹理通道完整定义（社区文档）

| 通道 | 用途 |
|---|---|
| Mask Red | **Emissive Mask**（发光遮罩，控制哪里发光） |
| Mask Green | Reflection Mask / Cubemap Intensity（反射遮罩） |
| Mask Blue | Iris Mask（虹膜遮罩，控制虹膜 vs 巩膜区域） |
| Normal R/G | 标准切线空间法线 |
| Normal Blue | Unused |
| Diffuse R/G/B | 标准颜色 |
| Vertex Color 1 Red | 左眼颜色影响（异色瞳） |
| Vertex Color 1 Green | 右眼颜色影响（异色瞳） |

### 7.3 IRI A / IRI B 双眼独立

来源：[Asym Sclera](https://xivmodding.com/books/general-mod-creation/page/asym-sclera)

- **IRI A material** 控制左眼
- **IRI B material** 控制右眼
- 两只眼睛使用独立材质 → **可以独立设置不同的 g_EmissiveColor！**
- 这意味着异色发光瞳是可行的

### 7.4 Shader 常量 CRC 修正

社区文档中 g_EmissiveColor 的 CRC 写作 `3BA64362`（可能是笔误），我们代码中使用 `38A64362` —— 后者与 Meddle/Penumbra 一致，以代码中的为准。

### 7.5 Atramentum Luminis (ALum) 对眼睛的扩展

- ALum 框架将眼睛的发光信息放在 mask/multi 的 **alpha 通道**
- 与原版 iris.shpk 的 mask **red** 通道不冲突（不同通道）
- ALum 兼容眼睛 mod 在没有 ALum 时会 fallback 到非发光版本（安全降级）

## 八、风险与注意事项

1. **Emissive mask 依赖**：Dawntrail 后所有眼睛内置了 emissive 支持，只要 mask 红通道非零即可。大部分第三方眼睛 mod 已预设好。原版眼睛的 mask 红通道可能为 0，需测试。
2. **双眼独立控制**：IRI A / IRI B 是独立材质，CBuffer g_EmissiveColor 可以独立设置 → **双眼可以不同颜色** ✅
3. **与 Glamourer 兼容**：Glamourer 通过 CustomizeParameter 修改眼睛颜色（diffuse 层面），与 emissive CBuffer 修改不冲突
4. **与 ALum 兼容**：ALum 使用 mask alpha 通道控制眼睛发光，SkinTattoo 使用 CBuffer g_EmissiveColor 控制，不同机制不冲突
5. **Emissive 值范围**：建议 0.3-0.8，超过 0.9 可能出现视觉问题
6. **性能**：每帧 OnRenderMaterial hook 中增加一次 ConcurrentDictionary 查找，开销可忽略
