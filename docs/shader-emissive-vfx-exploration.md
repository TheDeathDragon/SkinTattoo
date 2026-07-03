# Shader / 发光 / VFX 扩展可行性调研

时间：2026-07-03，游戏版本 CN 2026.06.18（skin.shpk sha8=4e2f77df，与 7.5 相同）。
工具：ida-pro-mcp（当前 CN 二进制）、FFXIVClientStructs、自制 v14.1 shpk 分析脚本、VFXEditor-CN 源码。

## 1. charactertattoo.shpk — 原版自带的贴花叠加通道

### 结构

- 169,854 字节（skin.shpk 是 11.7MB），v14.1（0xE01），**VS 8 / PS 8**
- 字符串区资源：`g_SamplerDecal`、`g_SamplerNormal`、`g_SamplerDither`、`g_DecalColor`、
  `g_CustomizeParameter`、`g_MaterialParameter`、dissolve 系列（溶解出场用）
- 没有 diffuse/mask/ColorTable —— 它不是皮肤光照模型，是**专用叠加贴花 pass**

### 引擎侧接线（IDA + FFXIVClientStructs 确认）

ModelRenderer 预载 4 个特殊 shpk（FFXIVClientStructs `ModelRenderer.cs`）：

```
+0x218 CharacterGlassShaderPackage
+0x220 CharacterTransparencyShaderPackage
+0x228 CharacterTattooShaderPackage
+0x230 CharacterOcclusionShaderPackage
```

OnRenderMaterial（新地址 sub_140281540）对材质的 ShaderPackage 做指针比较：

- == Glass        -> flags = f & 0xFFFFFF5C | 0xA2
- == Transparency -> flags = f & 0xFFFFFF7C | 0x82（+ 子视图位处理）
- == Tattoo 或 Occlusion -> flags = f & 0xFFFFF679 | 0x180

即 **mtrl 指定 charactertattoo.shpk 时引擎自动走 |0x180 叠加渲染分支**，无需任何 hook。

### 贴花系统输入（Human 结构，FFXIVClientStructs `Human.cs`）

```
+0xAF8 _slotDecals[10]     // 每装备槽贴花（部队徽章系统）
+0xBF8 DecalColorCBuffer   // ConstantBuffer*，Vector4 颜色（脸绘颜色）
+0xC00 Decal               // TextureResourceHandle*，脸绘贴图
+0xC08 LegacyBodyDecal     // TextureResourceHandle*，旧版刻印（画在身体上）
```

脸绘通过 **UV2** 映射：CustomizeParameter.LeftColor.w = 脸绘 U 乘数、RightColor.w = U 偏移
（镜像翻转就是这么做的）。

### 可能性评估

| 路线 | 说明 | 评估 |
|---|---|---|
| 替换脸绘贴图 | Penumbra 重定向 `chara/common/texture/decal_face/*`，游戏内选脸绘 | 已可行，无需代码；限制：仅脸、单色染色、UV2 映射 |
| 运行时写 Decal 句柄 | 插件直接换 `Human.Decal`/`LegacyBodyDecal` 指向自制贴图 + 实时写 DecalColorCBuffer（CBuffer 写入能力已有） | 值得 POC：引擎原生叠加、不改皮肤贴图、无 z-fighting；未知点是 pass 激活条件（脸绘选"无"时 pass 是否跑） |
| mtrl 直接用 charactertattoo.shpk | 身体 mod 附加网格（如 Eve 的 piercing 槽模式）挂 tattoo 材质 | fast-path 路由确认可达；未知点是该 pass 的 cb/贴图喂给方式是否兼容 mtrl 常规通道 |

**下一步 IDA 方向**：xref ModelRenderer+0x228 的读取点，找 tattoo pass 的 setup（谁绑
g_SamplerDecal / g_DecalColor、激活条件、用哪路 UV）。

## 2. Face 族原生发光注入 — 修掉唇缘发光 + 脖子接缝的明确路径

对当前 skin.shpk 全部 704 节点按 CategorySkinType 分族、扫描每族每 pass 的 PS 是否读
g_EmissiveColor（cb0[3]，offset 48）：

| 族 | pass[2]（主光照，id 0x955C0B73）32 个 PS | 结论 |
|---|---|---|
| Emissive | 全部含 `mul rN.xyz, cb0[3].xyzx, cb0[3].xyzx` | 现有 v11c 补丁目标，索引表验证正确 |
| Face | **零读取**（唯一 cb0[3] 命中是 .w = g_LipRoughnessScale） | 原版脸部光照根本不算 emissive —— 这就是当初被迫强改 CategorySkinType=Emissive 的根因 |
| Body | 零读取 | v13 ValBody 模式已为它实现输出锚点注入 |
| BodyJJM | 零读取 | 同上 |

关键结论：**v13 模式已经解决了"给无 emissive 代码的族注入发光"这个问题**（输出锚点
mul o0 + ret 定位）。把同一机制套到 Face 族 pass[2]（PS 索引 2, 21, 32, 51, 62, 81,
92, 111, ... 共 32 个）即可：

- face mtrl 保持 CategorySkinType=Face -> 原生 PS 路由 -> 唇缘发光、脖子接缝直接消失
- 脸部同样获得 CT 逐层颜色 + 动画
- 注意：Face 族 cb0[3].w（唇部粗糙度）是活的，注入代码不能覆盖 .w

### 顺带发现：pass[3] 盲区

pass[3]（id 0xC885BBD3，半透明/前向变体）在 Emissive 族**同样全部含 emissive 代码**，
但现有补丁只处理 pass[2] + gbuffer pass[0]。湿身/水下等半透路径下发光可能缺失/不一致。
其中 4 个 PS（338/347/374/383）是非平方读取的新变体，模式匹配需单独适配。

## 3. iris.shpk 导出动画 — 直接可移植

- iris.shpk：5.78MB，v14.1，VS 72 / PS 96，g_EmissiveColor 同样在 cb0[3]
- **96 个 PS 中 64 个含有与 skin.shpk 完全相同的 mul+mul 模式** —— v11 补丁的
  模式替换手法原样适用
- 动画参数不需要 CT/新采样器：把 animSpeed 等烧进 mtrl 常量区的空闲偏移
  （mtrl constants 机制，MtrlFileWriter 已会写），注入代码读 cb0[N] + 场景时间
- 容器读写零风险：C# 解析器对 v14.1 已字节级 roundtrip（iris 同版本）

工作量：中。主要是把 skin 版 pulse 注入（CB0 dcl 定位 + pulse anchor）参数化复用，
以及 iris 的 mask 红通道 gating 与注入代码的衔接。完成后"导出虹膜动画丢失"限制消除。

## 4. VFX — 运行时挂载完全可行

VFXEditor 的机制（`Interop/ResourceLoader.Vfx.cs` + `Spawn/VfxSpawn.cs`）：

```
VfxObject* ActorVfxCreate(string avfxPath, IntPtr caster, IntPtr target,
                          float a4=-1, char a5=0, ushort a6=0, char a7=0)
void       ActorVfxRemove(VfxObject* vfx, char a2=1)
```

两个签名在当前 CN 二进制**唯一命中**（ActorVfxCreate @ 0x140853FC0）：

```
ActorVfxCreateSig = "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8"
ActorVfxRemoveSig = "0F 11 48 10 48 8D 05"
```

### 玩法设计空间

- 插件内置/生成 .avfx 模板（体表辉光、环绕粒子），Penumbra 临时 mod 把虚拟路径重定向到
  生成文件，`ActorVfxCreate(虚拟路径, player, player)` 挂到玩家身上
- 循环：avfx 播完会回调 remove，VFXEditor 的 loop 模式是"移除后延时重生成"，可照搬
- 颜色/强度联动纹身发光色：改 avfx 粒子颜色字段后重生成；运行时直接改 VfxObject 内存
  未有先例，需另行逆向
- AVFX 支持骨骼挂点 -> 可以做"纹身部位附近的粒子"（按骨骼近似，无法精确贴合 UV 形状）

### 限制（与虹膜动画同类）

导出的 .pmp 只能替换**已存在**的游戏 avfx（技能/emote/状态/装备 VFX）。裸体常驻 VFX
没有原生槽位，所以这是插件运行时特性；导出方案只能绑到玩家可主动触发的载体上
（emote、时尚配饰等），且对其他 mod 有覆盖面。

## 5. 发光系统低成本扩展清单（现有 CT 注入架构内）

| 扩展 | 机制 | 成本 |
|---|---|---|
| 更多波形（呼吸/心跳/闪烁） | pulse 注入代码换波形函数，CT 行编码波形选择位 | 低 |
| 色相循环（彩虹流动） | 时间驱动 hue 旋转，注入若干 ALU 指令 | 低-中 |
| 昼夜/场景响应 | shpk 有 8 个 SceneKeys，可按 key gate 不同节点 | 中（需理解 SceneKey 语义） |
| UV 流动发光 | mask 采样 UV 加时间偏移 | 中-高（动 UV 而非只动颜色） |
| pass[3] 半透明路径补齐 | 同 pass[2] 手法 + 4 个新变体模式适配 | 低-中 |

## 优先级建议

1. **Face 族 ValFace 注入模式**（修复两个已知 7.5 限制，机制已验证存在）—— **已实现（v11d），待游戏内验证**
2. **iris.shpk 补丁**（消除导出动画缺失，64/96 PS 模式完全一致）
3. VFX 运行时挂载 POC（新特性，签名已验证）
4. charactertattoo 原生贴花路线深挖（潜在架构级升级，先做 IDA pass setup 分析）
5. 波形/色相扩展（随 1/2 顺带做）

## v11d 实现记录（2026-07-03）

Face 族注入已实现并离线验证（32/32 PS 全部通过字节级检查）：

- `SkinShpkPatcher`：新增 `DeriveLightingPsIndices`（按 LightingPassId 0x955C0B73 从节点表推导族 PS 集合，
  不再新增硬编码；同时校验 Emissive 硬编码表与节点推导一致，不一致时告警）、
  `ScanShexDecls`（SHEX 声明区扫描：最大 s/t 槽位 + dcl_temps；处理 customdata 与
  raw/structured resource 0xA1/0xA2）、`PatchSingleFacePs` + `BuildFaceEmissiveInjection`。
- **关键工程事实**（离线分析发现）：
  - 光照 PS 的 SHEX 声明里有引擎按槽位直接绑定的 pass 输入（gbuffer/阴影等），**不出现在
    shpk 资源表**。Face 族 t10 全部被占、s5 多数被占 —— CT 槽位必须按每 PS 取
    `max(声明+资源表)+1`（例：PS2 -> s5/t11，PS375 -> s8/t15）。
  - 法线贴图槽位在同族内有 4 种变体（s1/t4、s1/t6、s2/t5、s2/t7，随阴影/dissolve/aura
    变体变化），从 PS 资源表按 CRC 0x0C5EC1F1 逐 PS 解析。v13 固定 t5/s1 的假设不成立
    （这大概是 v13 "阴影级联异常"的根因）。
  - 现网 v11 的 Emissive 注入固定 s5/t10，与多数 Emissive 变体的已占槽位冲突（CT 赢得绑定，
    原读 t10 的原版代码读到 CT 数据）—— 已定位，留作后续用同一 per-PS 机制修复。
  - 现网 pulse 动画锚点只匹配 r1 目标寄存器，Emissive 族 32 个 PS 中仅 8 个带动画；
    Face 注入固定产出 r1 -> 32/32 全带动画。
  - 注入块把 mask（normal.a）暂存 r0.w（尾部死槽），动画 payload 的 mask 源操作数从
    r0.z 改写为 r0.w（v11 站点 r0.z=mask，Face 站点 r0.z=最终颜色）。
- `MtrlFileWriter`：mtrl 已有 `CategorySkinType=ValFace` 时跳过三键强制（保持原生 Face 路由，
  消除唇缘发光与脖子接缝的根因）；身体 mtrl 行为不变（仍强制 Emissive 族）。
- 法线 alpha 语义对 Face 无缝衔接：合成器 RowIndex 模式基线 alpha=0（原版脸部 norm.a 99% 为 0），
  行对 0 预留黑色，CT 内容构建与身体共用。
- SchemaVersion v11c -> v11d（缓存自动失效重建）。
