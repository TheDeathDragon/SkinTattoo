# 贴花动态发光实现计划

> 研究日期：2026-04-17
> 目标：为 SkinTattoo 贴花发光系统增加时间驱动的动态效果（呼吸脉冲、水波纹扩散、闪烁），完全基于游戏引擎自带机制，不新增任何运行时 hook。

---

## 一、核心原理

### 1.1 引擎自带时间 uniform
所有 FFXIV shader 的 `g_PbrParameterCommon` CBuffer（cb2）在偏移 0 处有 `float m_LoopTime` 字段，**引擎每帧自动写入**。PS[19] 反汇编已确认声明：

```hlsl
cbuffer g_PbrParameterCommon
{
  struct CommonParameter
  {
      float m_LoopTime;              // Offset:    0   <-- cb2[0].x
      float m_LoopTimePrev;          // Offset:    4   <-- cb2[0].y
      float m_SubSurfaceSSAOMaskMaxRate;// Offset: 8  <-- cb2[0].z
      float m_MipBias;               // Offset:   12   <-- cb2[0].w
      ...
  } g_PbrParameterCommon;
}
```

**结论**：在 DXBC 里读 `cb2[0].x` 即可获得当前循环时间（秒），无需 plugin 主动驱动。

### 1.2 现有 `g_MaterialParameter` 可扩展
当前 `skin_patched.shpk` 的 PS[19] 只用到 `cb0[0..17]`（`float4 g_MaterialParameter[20]`），`cb0[18]` / `cb0[19]` 空闲。扩展数组长度并在 RDEF + SHEX 同步声明即可新增材质参数。

### 1.3 动画公式 → DXBC 指令映射
| 模式 | 数学表达式 | DXBC 指令数（估算） |
|---|---|---|
| Pulse | `k = 1 + amp * sin(2π · speed · t)` | 3（mul/sincos/mad） |
| Ripple | `k = 1 + amp * sin(freq · ‖uv − c‖ − 2π · speed · t)` | ~8（add/dp2/sqrt/mad/sincos） |
| Flicker | `k = 1 + amp * (hash(floor(speed·t)) − 0.5)` | ~5（floor/mul/frac 哈希） |

调制方式：在 PS 末尾 `mul o0.xyz, r0.xyz, cb1[3].xxxx`（line 750）之前，对累积 emissive 的寄存器 `r0.xyz` 做一次乘法 `mul r0.xyz, r0.xyz, k`。

---

## 二、分阶段路线图

### 阶段 0：基础设施准备（已完成 2026-04-17）
**目标**：在不改变任何视觉效果的前提下把 SHEX 的 CB0 数组声明扩到 [20]，为后续动画参数读取腾出空间。

结论：
- 引擎对 CBuffer[0] 的分配基于 shpk header 的 `MatParamsSize=320` 字节（20 float4），SHEX 扩到 CB0[20] 完全安全，无需 RDEF 改动
- 全部 32 个 lighting PS 的 cb0 使用上限都是 cb0[17]，cb0[18]/[19] 空闲（`scan_cb0_usage.py` 验证）
- 实现：`SkinShpkPatcher.PatchShexExtendCb0`，在 emissive 替换流程末尾把 `dcl_constantbuffer CB0[18]` 的 size 字段原地改为 `0x14`（20）
- 实际覆盖：8/32 lighting PS（和现有 emissive 替换的成功范围一致；未匹配 emissive pattern 的 24 个 PS 保留 CB0[18]，不影响渲染）
- 游戏内验证：现有贴花发光效果与扩容前**像素级一致**，无崩溃、无错误日志

### 阶段 1：Pulse 呼吸脉冲（已完成 2026-04-17）
**目标**：最小可用动态发光，**每层独立参数**。

实现（每层独立走 ColorTable，非 CBuffer）：

1. **DXBC 注入**：在 emissive ColorTable sample 之后追加 8 条指令（62 token，248 字节）：
   ```
   sample_indexable r1.xyzw, r1.xyxx, t10, s5       ; 已有：emissive = CT[row, 8..10]
   mov    r9.x, l(0.4375)                            ; 新列 U 坐标（column 3）
   mad    r9.y, r0.z, l(0.9375), l(0.015625)         ; V 与 emissive 相同
   sample r2.xyzw, r9.xyxx, t10, s5                  ; anim = CT[row, 12..15]
   mul    r9.x, r2.x, cb2[0].x                       ; phase = speed * m_LoopTime
   mul    r9.x, r9.x, l(6.283185)                    ; *2pi
   sincos r9.x, null, r9.x                           ; sin
   mad    r9.x, r2.y, r9.x, l(1.0)                   ; k = amp*sin + 1
   mul    r1.xyz, r1.xyzx, r9.xxxx                   ; emissive *= k（仅发光项）
   ```
   注入点选择在 emissive sample 之后、`mul r1.xyz, r1.xyzx, cb5[0].xyzx`（g_MaterialParameterDynamic.m_EmissiveColor）之前，确保只调制 emissive 贡献而不影响 diffuse/specular。

2. **ColorTable 扩展**：每个 rowPair 在 column 3（halfs 12, 13）写入该层的 `(speed, amp)`：
   ```csharp
   WriteHalf(rowLower + r, 12, animSpeed);   // Hz
   WriteHalf(rowLower + r, 13, animAmp);     // 0..1
   ```
   非 Pulse 模式层写入 0/0，等效关闭动画。同一 `rowLower` 和 `rowLower+1` 写相同值以避免 bilinear 竖向过渡产生 50% 抖动。

3. **C# 改动**：
   - `Core/DecalLayer.cs`：新增 `EmissiveAnimMode { None, Pulse }`、`AnimSpeed`、`AnimAmplitude`
   - `Configuration.cs` + `Core/DecalProject.cs`：序列化字段
   - `Services/PreviewService.cs::LayerSnapshot`：透传字段
   - `Services/MtrlFileWriter.cs::BuildSkinColorTablePerLayer`：写入 speed/amp 到 column 3
   - `Services/PreviewService.cs::HighlightEmissiveColor`：highlight 临时层保留 AnimMode/Speed/Amplitude
   - `Gui/MainWindow.ParameterPanel.cs`：发光区块下加 Combo + 2 sliders
   - `Localization/*.json`：`anim_mode/speed/amplitude`、`anim.none/pulse`

4. **实时响应**：UI 拖动滑块不触发全量重绘——走现有 `RestoreSkinCtAfterHighlight` 路径，重建 ColorTable 纹理并做 GPU atomic swap；shader 每帧读新 CT，无缝过渡。

**架构决策记录**：
- 最初尝试：用 cb0[19].zw 存 speed/amp（shpk 级 material param `g_DecalAnimParam0`）+ EmissiveCBufferHook 实时写 CBuffer
- 问题：skin.shpk 单材质共享一组 cb0 → 同材质多层共用动画参数，**无法独立**
- 最终方案：把 speed/amp 塞进 ColorTable row（每层独一的 rowPair），着色器二次 sample 读取——天然每层独立，且复用已有的 ColorTable atomic swap 机制

**已知限制**：
- **眼睛（iris.shpk）不支持**：iris 材质走独立的 iris.shpk 且走 CBuffer 发光路径（g_EmissiveColor + g_IrisRingEmissiveIntensity），本阶段未 patch iris.shpk，眼睛无 pulse 动画。未来若需：需为 iris.shpk 做类似 DXBC 注入 + ColorTable 化改造。

### 阶段 2：Flicker 闪烁（已完成 2026-04-17）
**目标**：方波闪烁，类似坏灯泡／电光效果。

实现：
- 公式简化为 `k = 1 + amp * sign(sin(2π·speed·t))` — 二值方波，duty 50%
- DXBC 在 sincos 后插入 4 条指令：`ge → movc → ge → movc`，把 sin 值条件替换为 `±1`
- CT col 3 half 14 = mode（0=Pulse, 1=Flicker）；根据 mode >= 0.5 做 `movc` 选择 wave
- payload 从 62 token 扩到 94 token
- iris.shpk 路径复用 `EmissiveCBufferHook.ComputeModulatedColor`：`s >= 0 ? +1 : -1`
- shpk 缓存文件名 `skin_ct_v2.shpk` 强制重生

### 阶段 3：Gradient 双色渐变（已完成 2026-04-18）
**目标**：两个颜色之间按 sin 周期性 lerp，实现双色呼吸。

实现：
- 公式 `final = lerp(colorA, colorB, 0.5 + 0.5*amp*sin(2π·speed·t))`
- amp 控制插值幅度：amp=1 两色完全来回；amp=0 保持 colorA
- DXBC 扩展：
  - sincos 后新增 `mov r7.w, r9.x` 备份 sin（给 Gradient 用，pulse/flicker 会 clobber r9.x）
  - pulse/flicker 乘法前 `mov r7.xyz, r1.xyzx` 备份 colorA
  - 乘法后新加 9 条指令：采样 col 4 到 r3 → 计算 mix → lerp → 3-component movc 选最终结果
  - payload 从 94 token 扩到 171 token
- CT col 3 half 14 = mode (0=Pulse, 1=Flicker, 2=Gradient)
- CT col 4 halfs 17/18/19 = colorB RGB（half 16 保留给 vanilla roughness 不动）
- iris.shpk 路径 `ComputeModulatedColor` 增加 Gradient 分支，用 `Vector3.Lerp`
- DecalLayer 新增 `EmissiveColorB` 字段（默认蓝色）
- shpk 缓存文件名 `skin_ct_v3.shpk` 强制重生

### 阶段 5：Ripple 水波纹（已完成 2026-04-18）
**目标**：从贴花中心向外扩散的同心圆发光波纹。

实现：
- 公式 `phase = 2π·speed·t - freq·dist`，`k = 1 + amp·sin(phase)`，最终 `r1 *= k`
- `dist = length(v2.xy - center)`，`v2.xy` = TEXCOORD0（vanilla PS[19] 保留的 body UV0）
- ColorTable col 5 halfs 20/21/22 = `centerU`、`centerV`、`freq`；非 Ripple 模式写 0 → 空间相位偏移为 0 → 路径无条件执行但等效 Pulse，避免 shader 分支
- DXBC 新增 7 条指令（`mov col5 U` + `sample col5` + `add d = uv - center` + `dp2` + `sqrt` + `mul freq·dist` + `add phase -= ripple`），payload 从 169 → 220 token
- Center 直接取 `DecalLayer.UvCenter`（贴花自己的 UV 中心），freq 新增字段 `AnimFreq`（默认 20 rings/UV）
- iris.shpk 路径 `EmissiveCBufferHook.ComputeModulatedColor` 没法获取逐像素 UV，Ripple 模式优雅降级为 Pulse
- shpk 缓存 `skin_ct_v5.shpk`

### 阶段 4：Iris（眼部）pulse 支持（已完成 2026-04-17，路线 4B）
**目标**：让眼睛也能按相同参数 pulse。

选中 **4B（CBuffer 实时调制）**——iris 组通常只有一层贴花，不需要 per-layer 独立。

实现：
1. `Interop/EmissiveCBufferHook.cs`：
   - `targets` 的 value 从 `Vector3` 扩展为 `TargetData { BaseColor, AnimMode, AnimSpeed, AnimAmplitude }`
   - 新增 `Stopwatch clock`，Detour 每帧计算 `k = max(0, 1 + amp·sin(2π·speed·t))`，`modulatedColor = baseColor·k`
   - `SetTargetByPath` / `SetIrisEmissive` 新增可选参数 `animMode/speed/amplitude`（默认 None/0/0，向后兼容）
2. `Services/PreviewService.cs`：
   - `EmissiveEntry` record 增加三个动画字段
   - Legacy fallback 路径构造 `EmissiveEntry` 时调用 `GetDominantEmissiveAnim(layers)` 取第一个启用 pulse 的可见 emissive 层参数
   - `ApplyInPlaceSwap` 的 `SetTargetByPath` 调用透传 anim 参数
3. 时钟源：每个 hook 实例一个 Stopwatch（wall-clock seconds），无需读 `cb2[0].x`。数值不与 shader DXBC pulse 完全同步，但视觉频率一致。

**行为约束**：
- iris 路径与 skin CT 路径隔离：iris 走 EmissiveEntry → CBuffer hook；skin 走 CT entry → DXBC pulse。两条路径不会互相干扰
- amp=0 或 speed=0 自动退回静态颜色，关闭 pulse 等效于不调用动画分支

**4A 备选（未采用）**：给 iris.shpk 同样做 DXBC + ColorTable patch。工作量中等，但没有实际收益（iris 单层）

---

## 三、ColorTable 每层数据布局

ColorTable 是 8 × 32 的 half4 纹理，每 rowPair 两行写相同值（bilinear 填充）。vanilla 只用到 halfs 0..10 + 16（PBR 字段），其余对我们可用。最终布局：

| Half | 含义 | 引入阶段 |
|---|---|---|
| 8-10  | emissive RGB (colorA) | 1 |
| 12 | speed (Hz) | 1 |
| 13 | amplitude (0..1) | 1 |
| 14 | mode (0=Pulse, 1=Flicker, 2=Gradient, 3=Ripple) | 2-5 |
| 15 | 备用 | — |
| 16 | (vanilla roughness — 保留不动) | — |
| 17-19 | dualColor / Gradient colorB RGB | 3 |
| 20 | ripple centerU | 5 |
| 21 | ripple centerV | 5 |
| 22 | ripple freq (rings per UV unit) | 5 |
| 23 | ripple dirMode (0=radial, 1=linear, 2=bidir) | 5b |
| 24 | ripple dirX (cos(angle)) | 5b |
| 25 | ripple dirY (sin(angle)) | 5b |
| 26 | dualActive (1=Gradient 或 Ripple+dual) | 5b |
| 27 | 备用 | — |

**最初的 cb0[19] 方案已废弃**（参见阶段 1 架构决策记录）。

---

## 四、DXBC Patcher 现状

生产实现在 **C#** (`SkinShpkPatcher.cs`)，不走独立 Python 脚本。Python 工具 (`ShaderPatcher/`) 保留为研究辅助：
- `parse_shpk.py` — dump vanilla shpk 结构
- `gen_pulse_tokens.py` / `gen_flicker_tokens.py` / `gen_gradient_tokens.py` / `gen_ripple_tokens.py` / `gen_ripple_ext_tokens.py` — 编译参考 HLSL，提取 DXBC token 模板
- `verify_*_payload.py` — payload 字节级自检（opcode 长度字段 vs 实际 token 数），字节写错会被抓住
- `scan_cb0_usage.py` — 风险扫描
- `dump_ps19_inputs.py` / `read_vanilla_ps19.py` — 调试辅助

C# 侧注入流程（`PatchSinglePs`）按以下顺序串联：
1. `PatchShexAddDeclarations` — 添加 s5 sampler + t10 texture
2. `PatchShexReplaceEmissive` — 把 vanilla 的 `mul cb0[3]*cb0[3]` + `mul r0.z*r1` 替换为 `mad V + mov U + sample r1` 读 ColorTable
3. `PatchShexExtendCb0` — 保留（stage 0 遗留，cb0[18..19] dcl 已扩容，虽然实际没用到）
4. `PatchShexInjectPulseModulation` — 在 emissive sample 之后插入完整的动画调制 payload（当前 36 指令 / 276 token，包含 pulse / flicker / gradient / ripple + 方向 + 双色 四合一逻辑）

失败回退：若 emissive 模式匹配失败（24/32 PS 有此情况），整个 PS 跳过。pulse 注入失败不阻塞，记录日志继续。

**shpk 缓存文件名升级规则**：每次 payload 大小变化或发现 bug 修复时，`candidate` 文件名后缀 (`skin_ct_v1/v2/...`) 要 bump，否则用户机上的老 shpk 缓存不会被重新生成。当前为 `skin_ct_v6.shpk`。

---

## 五、风险与未决问题

1. **Ripple 在镜像 UV 的不连续**：FFXIV 身体 UV 某些部位在 [1,2] 镜像区；同个贴花中心在左右两半对称出现，线性 ripple 跨过 UV=1 边界时波纹会断开。暂无解（需要贴花局部空间坐标系，改动大）
2. **Mod 导出**：动画参数（包括 ripple 的 center/freq/dir 和 dualColor 等）都存在 ColorTable 里随 mtrl 导出；其他客户端若没有 patched skin.shpk 不会看到动画，等效静态发光
3. **iris 的 Ripple 降级**：`EmissiveCBufferHook` 在 CPU 端没有逐像素 UV，因此 iris 上的 Ripple 降级为 Pulse。若需真正眼球 ripple，需 iris.shpk 同样做 DXBC patch
4. **register 占用**：payload 用到 r3, r4, r5, r6, r7, r8, r9, r10；vanilla dcl_temps=16 所以有富余，但若未来 payload 扩展需要更多寄存器，应先核对 vanilla 的 r10+ 活性

---

## 六、进度

| 阶段 | 状态 | 备注 |
|---|---|---|
| 0 — 基础设施 | 完成 2026-04-17 | CB0 dcl [18]→[20]，零视觉影响 |
| 1 — Pulse | 完成 2026-04-17 | 每层独立 via ColorTable column 3 |
| 2 — Flicker | 完成 2026-04-17 | sin → sign 方波，DXBC +32 tokens，iris 同步支持 |
| 3 — Gradient | 完成 2026-04-18 | 双色 lerp，CT col 4 存 colorB，DXBC +77 tokens |
| 4 — Iris pulse | 完成 2026-04-17 | 路线 4B：EmissiveCBufferHook + Stopwatch 实时调制 |
| 5 — Ripple | 完成 2026-04-18 | v2.xy UV 距离场 + CT col 5 + col 6，三种方向（radial/linear/bidir）+ 双色，DXBC 最终 36 指令 / 276 token |
