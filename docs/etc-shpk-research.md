# etc mtrl shpk 研究（龙角等附件发光）

> 研究起点：2026-04-14
> 状态：**搁置 — Tier 1 注入失败，per-layer 暂无可行路径**
> 前置：`skin-shpk-rename-and-multi-ps-patch.md`（脸部已上线）

## 一、背景

skin.shpk 的 multi-PS ColorTable 方案让脸/身体 emissive 正常工作，但奥拉族龙角（以及其他 `_etc_a/b/c` mtrl）不受影响。需要扩展到这些独立 shpk。

## 二、shpk 路径与结构（运行时确认）

| mtrl | shpk | vs | ps | matKeys |
|------|------|----|----|---------|
| `mt_*_etc_a.mtrl`（龙角主体） | **shader/sm5/shpk/hair.shpk** | 112 | 104 | 3 |
| `mt_*_etc_b.mtrl`（面部纹路） | shader/sm5/shpk/charactertattoo.shpk | 8 | 8 | 2 |
| `mt_*_etc_c.mtrl`（遮挡 / AO） | shader/sm5/shpk/characterocclusion.shpk | 8 | 4 | 2 |
| _（参考）_ skin.shpk | — | 72 | 384 | 3 |

ShpkDiag 显示三者都有 `g_EmissiveColor=True(off=48,sz=12)` —— 说明 CBuffer slot 0 (g_MaterialParameter) 字节 48..60 是 g_EmissiveColor。

## 三、hair.shpk 节点结构

NodeDump 输出（vanilla hair.shpk）：
- nodes=640, sysKeys=0, sceneKeys=9, matKeys=3
- MatKey[0] id=`0x24826489` 默认 `0xF7B8956E` —— 仅 2 个唯一值：`0x6E5B8F10`, `0xF7B8956E`
- MatKey[1] id=`0xD2777173` (= skin 的 CategoryDecalMode) —— 全部 node 固定 `0x584265DD` (`ValueDecalEmissive`)
- MatKey[2] id=`0xF52CCF05` (= skin 的 CategoryVertexColorMode) —— 全部 node 固定 `0xDFE74BAC`
- maxPassCount=6（比 skin 多 1 个 pass slot）
- pass[2] 在 2 个 MatKey[0] 变体中各贡献 16 个 PS，共 32 unique lighting PS

PassIndices 映射：`[255,5,0,255,1,255,2,255,4,255,3,255,...]` —— SubViewIndex 6 → pass 槽 2（与 skin.shpk 一致）。

## 四、Tier 1 实施 + 失败结论

### 4.1 实施

按 skin.shpk 的方法做 Tier 1：
- `HairShpkPatcher`：复用 SkinShpkPatcher 的 DXBC + shpk 解析基础设施
- 找到所有 32 个 lighting PS 的尾部统一字节模式：
  ```
  08000038 00102072 00000000 00100246 00000000 00208006 00000001 00000003
  ; mul o0.xyz, r0.xyz, cb1[3].x  (cb1 = g_CommonParameter)
  ```
- 改成 mad 注入 g_EmissiveColor（cb0[3]，cb0 = g_MaterialParameter）：
  ```
  0B000032 00102072 00000000 00100246 00000000 00208006 00000001 00000003 00208246 00000000 00000003
  ; mad o0.xyz, r0.xyz, cb1[3].x, cb0[3].xyz
  ```
- 部署为 `hair_ct.shpk`（rename + cache-miss）
- mtrl 重写 ShaderPackageName 到 hair_ct.shpk + 添加 g_EmissiveColor 常量

### 4.2 验证（全链路 OK，但视觉无效）

- HairShpkPatcher 报告 PS patched 32/32 ✓
- mtrl 重写后 ShaderPackageName="hair_ct.shpk", g_EmissiveColor=(测试值) ✓
- ShpkDiag 显示 hair mtrl 指向新 shpk 指针 ✓
- **但游戏里龙角颜色完全没变化** ✗

### 4.3 进一步诊断

把 patch 改成 `mov o0.xyz, l(10.0, 0.0, 0.0)`（强制纯红覆盖），扩到所有 104 个 PS（56 个找到模式被改），龙角仍**完全无变化**。

**结论**：hair.shpk 的可见 lighting 输出**不在我们能匹配的 `mul o0.xyz, ?, cb1[3].x` 模式里**。可能：
- 真正的 visible PS 用 mad、add、或写到 o1/o2（MRT），而不是 o0
- pass[2] 在 hair 上 SubViewIndex 6 不被龙角渲染流程命中
- 龙角可能由 pass[5]（hair 独有的额外 pass）或 pass[3] 决定，而不是 pass[2]

## 五、为何搁置

要继续推进需要：
1. 为 hair.shpk 的所有 PS 反汇编（D3DCompiler 或自写 disassembler）
2. 找出哪个 pass / 哪个 PS 真正写入龙角的可见输出
3. 识别该 PS 中 emissive 应注入的位置
4. 写新的字节模式 + patch

工作量远超 Tier 1 预估（半天 → 一周），收益不确定（可能依然需要 ColorTable 才能 per-layer），暂时不做。

## 六、可能的替代路径（未实施）

- **CBuffer Hook 单色**：仅给 hair mtrl 用 `EmissiveCBufferHook.SetTargetByPath`，所有图层合并成一色。简单但弱（无 per-layer 独立色，且如果上面的诊断说明 cb0[3] 根本没被读取，这条路也走不通）。
- **Penumbra Material edit + ALum 风格**：换 hair shpk 到一个支持 emissive 的 shpk（比如 character.shpk）。需要适配 mtrl 完全不同的 mat keys / texture 结构。

## 七、当前代码状态

为保持代码整洁，**Tier 1 hair 实现已全部回滚**（2026-04-14）：
- 删除 `Services/HairShpkPatcher.cs`
- `MtrlFileWriter.WriteEmissiveMtrlForHair` 已删
- `PreviewService` 的 hair 相关 deploy / detect / 分支已删
- `MainWindow.ResourceBrowser` hair 白名单条目已撤回（hair mtrl 不再出现在卡片列表）
- `Http/DebugServer` 的 `POST /api/group` 已删（Tier 1 测试时新增）
- `SkinShpkPatcher` 的研究专用 dump 工具（DumpGeneric / DumpShaderResources / DumpPsBlobsToDisk / DumpCbReferencesInPs）已清

未来若重启此课题，参照本文档 + git 历史（branch master 上 ~2026-04-14 的"Tier 1 hair"系列提交）恢复脚手架。
