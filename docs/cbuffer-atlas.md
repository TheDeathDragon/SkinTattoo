# CBuffer 全景图谱（跨 shpk：skin / iris / charactertattoo / hair）

> 2026-07-04。目标：为"导出 mod 保留动态发光"（iris.shpk 注入）和后续 shader 工作提供
> cbuffer 层面的完整地基。与 `skin-shpk-deep-dive/04-cbuffers.md`（skin 单包视角）互补，
> 本文覆盖跨包对照、RDEF 成员级布局、时间源实证与 iris 注入输入参数。
> 工具：`ShaderPatcher/cbuffer_atlas.py`（本次入库），数据源为 2026.06.18 CN 版本各 shpk。

## 1. shader 资源名字哈希算法（实证）

FFXIV shpk 里所有资源/参数/键的 CRC = **反射 CRC-32，poly 0xEDB88320，init=0，无最终异或**。
既非标准 IEEE CRC32（init/final 0xFFFFFFFF）也非 JAMCRC。Python 等价式：

```python
ffcrc = lambda name: ~zlib.crc32(name.encode(), 0xFFFFFFFF) & 0xFFFFFFFF
# 校验: ffcrc('g_SamplerNormal') == 0x0C5EC1F1; ffcrc('g_EmissiveColor') == 0x38A64362
```

由此反查出材质参数表大部分名字（见 §5），也能为新增资源自造合法 CRC。

## 2. 引擎全局 cbuffer 成员级布局（RDEF 反射实测）

DXBC blob 的 RDEF 块保留了完整反射信息（cbuffer → struct 成员名 + 偏移）。以下取自
iris.shpk PS3（pass[2] 光照），skin.shpk 同名缓冲一致：

### g_PbrParameterCommon（80B，5 vec4）— 时间源所在

| 偏移 | 成员 | 说明 |
|---|---|---|
| +0x00 | `m_LoopTime` | **全局循环时间（秒），shader 动画的时钟** |
| +0x04 | `m_LoopTimePrev` | 上一帧时间（速度缓冲用） |
| +0x08 | `m_SubSurfaceSSAOMaskMaxRate` | SSS/SSAO 限幅 |
| +0x0C | `m_MipBias` | 全局 mip 偏移 |
| +0x10 | `m_Reserve[4]` | 4 vec4 保留区 |

### g_CommonParameter（64B）

`m_RenderTarget` / `m_Viewport` / `m_Misc` / `m_Misc2` 各 1 vec4 —— 与 FFXIVClientStructs
`Shader/CommonParameter.cs` 一致（该文件是准的）。

### g_InstanceParameter（iris 版 176B —— FFXIVClientStructs 定义已过时，只有 0x50）

| 偏移 | 成员 |
|---|---|
| +0x00 | m_MulColor |
| +0x10 | m_EnvParameter |
| +0x20 | m_CameraLight { m_DiffuseSpecular, m_Rim } |
| +0x40 | m_Wetness |
| +0x50 | m_Wind |
| +0x60 | m_PrevWind |
| +0x70 | **m_IrisParam[2]**（左右眼参数） |
| +0x90 | m_Param |
| +0xA0 | m_HeadUpVector |

### g_MaterialParameterDynamic（16B）

单成员 `m_EmissiveColor`（vec4）。skin PS[19] 中与 mtrl 静态 g_EmissiveColor **相乘**
（见 deep-dive Ch4 §4.6）。这就是 `EmissiveCBufferHook` 之外引擎自己的动态发光通道。

### g_CustomizeParameter（112B）

m_SkinColor / m_LipColor / m_MainColor / m_MeshColor / **m_LeftColor / m_RightColor**
（异色瞳左右眼）/ m_OptionColor0 / m_Padding。

## 3. 时间源判定：pulse payload 的 cb2[0].x = m_LoopTime

skin_ct 注入的动画 payload 相位指令（`SkinShpkPatcher.PulsePayload`）：

```
mul r9.x, r2.x, cb2[0].x        ; token 0x0020800A, idx=(2, 0), select .x
```

- skin.shpk：`g_PbrParameterCommon` 在 288 个 PS 绑 **b2**（含全部 4 族光照 PS）；
- iris.shpk：在 64 个 PS 绑 **b2**（恰好 = 全部发光读取 PS，见 §5），另 24 个 PS 绑 b1
  （pass[0]/[1]，不读发光，无需注入）。

结论：**cb2[0].x = g_PbrParameterCommon.m_LoopTime，且 skin 与 iris 的注入目标 PS
同 slot，payload 时间源无需重定向**。行为验证：CT 烘焙 speed 改变呼吸/涟漪频率（游戏内）。
待 IDA 确认（低优先）：m_LoopTime 写入函数、回绕周期（"Loop"暗示有限周期，回绕瞬间
sin 相位可能跳变一次）。

### 3.1 IDA 补充（2026-07-04，CN 2026.06.18 二进制）

- `sub_140277430`（0x140277430）= 角色渲染管线子系统初始化：`+3320` 创建 80 字节的
  g_PbrParameterCommon CBuffer（`+3312` 存其名字 CRC），同函数还创建 `+3328` 的
  **0x8000 字节 g_ShaderTypeParameter 结构化缓冲**（即皮肤 32KB LUT）与
  g_SamplerDetailNormalMap/DetailColorMap/CharaToon 采样器——这个对象就是角色/皮肤
  渲染管线本体；`+36352` 附近是 PbrParameterCommon 的默认值块。
- 每帧写入 m_LoopTime 的代码位于巨型渲染准备函数（sub_140B30E90 一带，5000+ 指令），
  未逐条定位；回绕周期未测。续查路径：xrefs to sub_140277430 → 持有对象的帧更新方法。
  对补丁正确性无影响（RDEF 反射 + 实机动画走时已双重证实）。

## 4. slot 分布法则：同名缓冲 ≠ 固定 slot

cb/s/t 的 slot 是 **每个 shader 独立分配** 的（D3D 编译器按声明顺序压缩），同名缓冲在
不同 PS 落在不同寄存器。摘要（详表可用 `cbuffer_atlas.py` 重新生成）：

| 缓冲 | skin.shpk | iris.shpk |
|---|---|---|
| g_MaterialParameter | PS:b0 ×376 | PS:b0 ×88 |
| g_CommonParameter | PS:b1 ×288 | PS:b1 ×64 |
| g_PbrParameterCommon | PS:b1 ×88, **b2 ×288** | PS:b1 ×24, **b2 ×64** |
| g_CameraParameter | b2 ×80, b3 ×288 | b2 ×24, b3 ×64 |
| g_MaterialParameterDynamic | b5 ×64（仅 Emissive 族） | b5 ×64 |
| g_ShaderTypeParameter | （t 槽结构化缓冲） | t0..t5（结构化缓冲） |

任何注入都必须**逐 PS 从资源表+SHEX 声明取 slot**（v13 固定槽位假设的教训，v11e 已过）。

## 5. iris.shpk 结构全景

- 容器：v14.1 (0x0E01)，VS 72 / PS 96 / nodes 288，matParamsSize 0x1A0。
- matKeys：`0x63030C80`（默认 `0x3839C7E4`，iris 族选择键，名字未反查出）、
  DecalMode、VertexColorMode —— 无 CategorySkinType。
- **材质参数表与 skin.shpk 完全相同**（81 项共享布局）：g_EmissiveColor 在 +0x30
  （elem 3）、g_IrisRingEmissiveIntensity +0xD0 默认 0.25、g_IrisRingColor +0x40、
  g_WhiteEyeColor +0x20 等（完整命名表见 `cbuffer_atlas.py` 输出）。
- pass 结构（slot, id → PS 数）：

| pass | id | PS 数 | 备注 |
|---|---|---:|---|
| 0 | 0x03AC862E | 12 | GBuffer 类，pbr=b1，不读发光 |
| 0/4 | 0xE412A2D4 | 4+4 | 共享小 PS，**不绑材质 cb → 无 skin pass[4] 式发光泄漏** |
| 1 | 0x6006067F | 12 | 不读发光 |
| **2** | **0x955C0B73** | **16** | 光照 pass（与 skin 同 id），全部读发光 |
| **3** | **0xC885BBD3** | **48** | 半透明/复合 pass，全部读发光 |

- **发光读取 = 64/96 PS = pass[2] + pass[3] 全体**，每 PS 恰一条发光指令（两个 cb 操作数）。
- 锚点形态（两 pass 家族同构，仅寄存器不同）：

```
mul  rE.xyz, cb0[3].xyzx, cb0[3].xyzx   ; 发光色平方（原生 gamma 预处理）
sample_b rM, uv, tMask, sN              ; 虹膜遮罩（mask.r = 发光覆盖）
mul  rE.xyz, rM.x, rE.xyzx              ; 发光 = 色² × mask.r   ← 注入点
```

pass[2] 典型 rE=r2 / rM=r0.x；pass[3] 典型 rE=r5 / rM=r6.x —— **必须逐 PS 提取**。

- 注入资源余量：temps 14–18（skin payload 需要 9 个连续临时寄存器 → 用"temp 基址平移"，
  不能像 skin 那样用固定 r2–r10 —— skin 注入点在输出锚点处这些寄存器已死，iris 注入点
  在 shader 中部它们还活着）；maxT ≤ 16、maxS ≤ 9 → CT 槽位 max+1 充足（上限 127/15）。
- v2（UV）输入 64 个 PS 全部声明 → 涟漪空间相位可保留。

## 6. iris_ct 注入设计（实现输入）

- 部署：与 skin_ct 同款**重命名部署** `shader/sm5/shpk/iris_ct.shpk`；mtrl 的
  ShaderPackageName 改写 + 附加 2048B ColorTable 数据集；引擎按 shpk 资源表里的
  g_SamplerTable（CRC 0x2005679F）自动建纹理并绑定（skin_ct 已验证该链路是通用机制）。
- payload = skin PulsePayload 的改造版：
  1. 行 V 固定 0.09375（row pair 1 中点，rows 2/3 内容相同），去掉 normal.a → V 的 mad；
  2. 发光寄存器 rE、遮罩标量 rM.c 从锚点指令逐 PS 提取，模板参数化；
  3. temp 索引整体平移到原 dcl_temps 之上，dcl_temps += 9；
  4. colorB **预平方**写入 CT col4（原生路径发光色是 cb0[3]²，保持一致量纲）；
  5. 静态发光色继续走原生 cb0[3]（mtrl g_EmissiveColor）——iris 无泄漏 pass，无需置黑。
- 运行时分工：颜色实时改动 = EmissiveCBufferHook（每帧写 cb0[3]）；**动画 = shader 内
  m_LoopTime**。预览部署 iris_ct 后 hook 的 AnimMode 必须传 None，避免双重调制。
- 模式支持与 skin 相同：Pulse(0)/Flicker(1)/Gradient(2)/Ripple(3)+方向+双色。

### 6.1 iris_ct v1 实现记录（2026-07-04，已落地）

- `Services/IrisShpkPatcher.cs`：复用 SkinShpkPatcher 的容器/DXBC 基建（改为 internal），
  按 pass id 从节点表收集 64 个目标 PS，逐 PS 锚点识别（mul cb0[3]² → mask 采样 →
  mul mask，emReg / mask 操作数逐 PS 提取原样复用）、temp 基址平移（dcl_temps +4，
  payload 用 rN..rN+3）、CT 槽位 = max(资源表 ∪ SHEX 声明)+1、cb 布局守卫
  （mat=b0、pbr=b2 不符即跳过该 PS）。
- `MtrlFileWriter.BuildIrisColorTable` / `WriteIrisEmissiveMtrlWithColorTable`：固定
  row pair 1 烘 speed/amp/mode/colorB²/dualActive；原生 iris 键不动；g_EmissiveColor
  照写（iris 无泄漏 pass 不置黑）+ ring intensity=1；ShaderPackageName → iris_ct.shpk。
- 导出接线：`CompositeForExport` iris 分支在 animMode != None 时走 CT 路径（失败回退
  静态路径），iris_ct.shpk 作为 shader/ 共享资产进 default_mod（常开）。
- 预览不变：仍由 EmissiveCBufferHook 驱动动画；预览临时 mod 的普通 iris mtrl 优先级
  高于导出 mod，两套机制不会同时作用（无双重调制）。
- 验证：离线 patchtest 64/64 + verify_iris.py 全绿（指令流完整性、token 计数、temps、
  槽位一致、3 次 CT 采样、cb2[0].x 读取 +1、32 个非目标 PS 字节不变）；游戏内
  dry-export：mtrl=iris_ct.shpk + CT rows2/3 参数精确（colorB 平方值逐位吻合）；
  本地 pmp 打包结构正确。
- 已知取舍：Ripple 在 iris 上无空间相位（眼睛太小不值得），表现等同方波闪烁；
  两个我们导出的 mod 若 schema 不同会在 iris_ct.shpk/skin_ct.shpk 游戏路径上冲突
  （Penumbra 按优先级取一，同 schema 字节一致无害）。

### 6.2 双眼独立发光（iris_ct v2 —— 已实现 2026-07-04，schema iris2）

**左右眼判别源已实证**：虹膜网格的**顶点色 v1.x / v1.y** 编码眼别（左眼 v1=(1,0)、
右眼 v1=(0,1)），原版 shader 用它混合捏人异色瞳的左右色（PS4 @0x0A0C 起）：

```
mul r3.xyz, v1.yyyy, cb7[5].xyz      ; g_CustomizeParameter.m_RightColor × v1.y
mad r3, cb7[4], v1.xxxx, r3          ; + m_LeftColor × v1.x
```

辅助通道：材质参数 cb0[13].y（CRC `0x285F72D2`，默认位模式 int 1）做
`lerp(m_LeftColor.w, m_RightColor.w, cb0[13].y)`；`g_InstanceParameter.m_IrisParam[0]`
参与每眼虹膜大小（捏人的虹膜尺寸）。所有 64 个发光 PS 均声明 v1 输入。

**v2 实现**（schema iris2，2026-07-04）：
1. payload 的 CT 行 V = `mad rA.y, v1.y, l(0.0625), l(0.09375)` —— 左眼 rows 2/3、
   右眼 rows 4/5；per-PS 守卫要求 v1.y 已声明（64/64 满足）；
2. 发光色入 CT col2（halfs 8..10 预平方），payload 用 `ctColor² × mask` **替换**原生
   rE（movc 双分支均为 CT 值）——mtrl g_EmissiveColor 保留非零，仅供虹膜环语义，
   不再驱动遮罩发光；temps +5（rA..rE 基址平移）、27 指令 210 token、4 次 CT 采样；
3. `MtrlFileWriter.BuildIrisColorTable(mode,speed,amp,colorB,left,right)` 烘双行组；
   DecalLayer 新增 EyeSplitEnabled/EmissiveColorRight/EmissiveIntensityRight，
   SavedLayer/DecalProject 映射/LayerSnapshot/HTTP 均已同步；参数面板对 `_iri_`
   组显示"双眼独立"控件；动画参数 v2 双眼共享（CT 结构支持将来分开）；
4. **预览统一化（同日完成）**：与导出同门槛（动画或双眼独立启用）时，预览临时 mod
   直接部署 iris_ct.shpk + CT mtrl（ProcessGroup legacy 分支内 irisCtPreview 路径，
   `irisCtMaterials` 标记）；实时改动经 `TryWriteIrisCtDirect`/inplace 工作线程重建
   CT 并 `ReplaceColorTableRaw(8x32)` 热交换，CBuffer hook 对 iris-CT 材质全面跳过
   （UpdateEmissiveViaColorTable 会用均匀表踩掉双眼行，Emissives 批次有防御性跳过）。
   预览、实时编辑、导出三条链路现为同一 shader 机制；


5. 虹膜环（g_IrisRingColor/强度）是 cb0 共享常量，per-eye 环发光需另找环项读点做
   同样的 v1 加权，v2 不含。
6. 验证：离线 64/64 全绿（temps+5/4 采样/流完整性/非目标 PS 不变）；游戏内
   dry-export：row4 = ((0.1,0.9,0.2)×2)² = (0.04,3.24,0.16) 逐位精确。

## 7. 其他 shpk 速览

- **charactertattoo.shpk**：VS8/PS8/nodes48，matKeys 仅 DecalMode+VertexColorMode，
  材质参数表同布局。原生贴花叠加通道（未来架构候选，见 shader-emissive-vfx-exploration.md §1）。
- **hair.shpk / characterlegacy.shpk**：图谱脚本可直接跑，本轮未深入。

## 8. 联网调研结论（2026-07-04）

公开资料没有 g_PbrParameterCommon / m_LoopTime 级别的文档：
[xivmodding Dawntrail Shader Reference Table](https://xivmodding.com/books/ff14-asset-reference-document/page/dawntrail-shader-reference-table)
只覆盖 mtrl 常量的用户语义（如 iris 的 g_EmissiveColor/白眼色）；GitHub 检索结果被
ReShade/GShade 预设仓库淹没（[gposingway](https://github.com/gposingway/gposingway)、
[AcerolaFX](https://github.com/GarrettGunnell/AcerolaFX) 等）。**ReShade 路线与本项目
不相干**：后处理注入拿不到材质身份/UV 空间，无法做逐材质发光；帧级验证用 RenderDoc
更合适。本文的 RDEF 反射法 + 名字哈希破解即为目前最完整的一手资料。

## 9. 待办

- [ ] IDA（MCP 重连后）：m_LoopTime 写入点与回绕周期；iris_ct.shpk 在 ModelRenderer
      5 指针 fast-path 之外的通用路径确认（skin_ct 先例已工作，预期无问题）。
- [ ] characterlegacy.shpk 图谱（39MB，跑一次入档）。
- [ ] shpk 顶层全局 Constants/Samplers/Textures 段的"默认 slot"语义与引擎绑定逻辑。
- [ ] 0x63030C80（iris 族键）名字反查。
