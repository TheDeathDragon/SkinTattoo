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

### 阶段 2：Flicker 闪烁（未实施）
**目标**：随机闪烁，类似坏灯泡／电光效果。

实现思路（ColorTable 方案续）：
- 每层 mode 字段（0=Pulse, 1=Flicker, ...）存到 CT row column 3 的第 3 个 half（.z）
- Flicker 公式：`k = 1 + amp * (frac(sin(floor(speed*t)) * 43758.5453) - 0.5) * 2`
- DXBC 分支：`if_nz mode == 1` → flicker 路径；else pulse 路径（使用 `movc` 避免真分支）

新增 ColorTable 槽位：
- half 14 = mode (float 强转)
- half 15 = 备用（flicker 子参数如 duty cycle）

**退出条件**：切换模式枚举 → Pulse/Flicker 切换生效，频率稳定不周期可辨。

### 阶段 3：Ripple 水波纹（未实施）
**目标**：从中心扩散的同心圆波纹。

实现思路：
- CT row column 4（halfs 16-19）新增 `(centerU, centerV, frequency, mode=2)`
- DXBC 计算：`dist = length(v2.xy - center); k = 1 + amp*sin(freq*dist - 2pi*speed*t)`
- `mul/sqrt/dp2` 实现距离，组合到 phase

**退出条件**：选择波纹模式 → 中心自动对齐贴花 → 调频率和速度 → 从中心向外扩散的光波。

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

ColorTable 是 8 × 32 的 half4 纹理，每 rowPair 两行写相同值（bilinear 填充）。列 0..10 + 16 为 vanilla PBR 字段；列 3 (halfs 12-15) 专供动画参数使用：

| Half | 含义 | 阶段 |
|---|---|---|
| 12 | speed (Hz) | 1 |
| 13 | amplitude (0..1) | 1 |
| 14 | mode (0=Pulse, 1=Flicker, 2=Ripple) | 2+ |
| 15 | 备用 | 2+ |

未来若需更多字段可扩展到 column 4 (halfs 16-19)，vanilla 只用到 half 16（roughness），17-19 空闲。

**最初的 cb0[19] 方案已废弃**（参见阶段 1 架构决策记录）。

---

## 四、DXBC Patcher 现状

生产实现在 **C#** (`SkinShpkPatcher.cs`)，不走独立 Python 脚本。Python 工具 (`ShaderPatcher/`) 保留为研究辅助：
- `parse_shpk.py` — dump vanilla shpk 结构
- `gen_pulse_tokens.py` — 编译参考 HLSL，提取 DXBC token 模板
- `scan_cb0_usage.py` — 风险扫描：确认新 cb 索引不冲突
- `verify_cb0_extended.py` — 运行后抽检 PS 反汇编是否符合预期

C# 侧注入流程（`PatchSinglePs`）按以下顺序串联：
1. `PatchShexAddDeclarations` — 添加 s5 sampler + t10 texture
2. `PatchShexReplaceEmissive` — 把 vanilla 的 `mul cb0[3]*cb0[3]` + `mul r0.z*r1` 替换为 `mad V + mov U + sample r1` 读 ColorTable
3. `PatchShexExtendCb0` — 仍保留（stage 0 遗留，让 cb0[19] 可读，虽然 stage 1 实际没用到）
4. `PatchShexInjectPulseModulation` — 在 emissive sample 之后插入 pulse 8 指令

失败回退：若 emissive 模式匹配失败（24/32 PS 有此情况），整个 PS 跳过。pulse 注入失败不阻塞，记录日志继续。

---

## 五、风险与未决问题

1. **Flicker 的 hash 质量**：`frac(sin(x)*43758)` 在某些 GPU 上条带化。备选用 Hash32 LUT（更多指令）
2. **Ripple 中心在镜像 UV 问题**：FFXIV 身体 UV 在 [1,2] 镜像区，波纹需在正确的 UV 子空间扩散。可能需要 UV pre-clamp 到 [0,1] 或用贴花局部空间坐标
3. **Mod 导出**：动画参数存在 ColorTable 里随 mtrl 导出，其他客户端若没有 patched skin.shpk 不会看到动画（等效静态发光，行为安全）

---

## 六、进度

| 阶段 | 状态 | 备注 |
|---|---|---|
| 0 — 基础设施 | ✅ 2026-04-17 | CB0 dcl [18]→[20]，零视觉影响 |
| 1 — Pulse | ✅ 2026-04-17 | 每层独立 via ColorTable column 3 |
| 2 — Flicker | 未实施 | 按 ColorTable 方案可增量扩展 |
| 3 — Ripple | 未实施 | 需要 mode 字段 + 距离计算 |
| 4 — Iris pulse | ✅ 2026-04-17 | 路线 4B：EmissiveCBufferHook + Stopwatch 实时调制 |
