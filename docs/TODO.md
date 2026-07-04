# 待办清单

> 2026-07-04 整理。按优先级粗排，完成后移除。

## 短期

- **调试端点补全**：`/api/debug/cbuffer?group=N` 实时 dump 目标材质 cb0 字节（借
  hook/TextureSwap 指针基建 + 材质参数表命名标注）、`/api/debug/shpk-info?path=`
  任意 shpk 的 pass/资源结构 dump（cbuffer_atlas.py 的 C# 内置版）、DebugWindow
  CBuffer 查看面板。
- **iris 静态路径并入 CT**：无动画、无双眼独立的静态眼睛发光仍走 legacy CBuffer
  hook 路径；并入 iris_ct 后预览/导出全链路只剩一种机制，hook 可退役为兜底。

## 中期

- **双眼独立动画参数**：CT rows 2/3 与 4/5 已天然支持每眼独立 speed/amp/mode/colorB，
  缺 DecalLayer 字段（注意 SavedLayer/LayerSnapshot DTO 同步）与 UI。
- **虹膜环 per-eye 发光**：g_IrisRingColor/强度是 cb0 共享常量，需定位环项在 PS 里的
  读点，做与主项相同的 v1.x/v1.y 加权注入。
- **pass[3] (0xC885BBD3) 发光补齐**：skin 半透明/复合通道未注入 CT 发光，半透明
  材质场景下贴花发光会缺失（Emissive 族历史盲区，native 族同样未覆盖）。
- **多导出 mod 冲突提示**：两个不同 schema 版本导出的 mod 同时启用时，
  skin_ct.shpk/iris_ct.shpk 游戏路径冲突由 Penumbra 优先级仲裁，旧 shpk + 新 mtrl
  组合可能槽位不匹配。导出时可在 meta 描述里记 schema，检测到冲突时提示重导。

## 长期 / 调研级

- **charactertattoo.shpk 原生贴花通道**：VS8/PS8 的原生贴花叠加管线
  （docs/shader-emissive-vfx-exploration.md §1），作为架构升级候选可摆脱 UV 合成。
- **VFX 运行时挂载 PoC**：ActorVfxCreate 路线（调研文档已记 sig）。
- **Emissive 族 pulse 锚点 r1 限制**：PatchShexReplaceEmissive 旧路径仅剩强制键
  场景使用，锚点扫描已修但该族 pass[0]/UV 重写等仍是 v11b 时代实现。
- **m_LoopTime 写入点与回绕周期**：IDA 续查路径见 docs/cbuffer-atlas.md §3.1，
  回绕瞬间 sin 相位理论上跳变一次（好奇级）。
- **characterlegacy.shpk 图谱**：cbuffer_atlas.py 直接可跑，39MB 未入档。
