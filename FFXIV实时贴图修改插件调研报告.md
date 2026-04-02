# FFXIV 实时贴图修改插件调研报告
## 通过 Dalamud 插件实现 Final Fantasy XIV 的运行时贴图替换

---

## 核心结论速览

**Penumbra 是目前 FFXIV 中最成熟的运行时贴图修改插件**。它通过在内存中拦截游戏的文件加载请求，将资源重定向至本地修改后的文件，全程不修改游戏磁盘数据。支持实时替换贴图、3D 模型、材质、着色器、元数据等几乎所有游戏资源文件。

Dalamud 框架本身提供了 `ITextureSubstitutionProvider` 和 `IGameInteropProvider` 等一方 API，从引擎层面使实时贴图修改成为可能。这不仅是"技术可行"——它已经是一个**成熟的、生产级的能力**，每天被数十万玩家使用。截至 2026 年 3 月，Penumbra 已发布超过 **435 个版本**。

---

## 一、Penumbra：FFXIV Mod 生态的基石

**仓库地址：** [github.com/xivdev/Penumbra](https://github.com/xivdev/Penumbra)

Penumbra 是一个 C# 编写的 Dalamud 插件，拥有约 **758 星标、3190 次提交**，保持多周更新频率。最新稳定版为 **1.6.0.2**（2026 年 3 月 26 日），由 Ottermandias 在 xivdev 组织下维护。

### 核心机制：运行时文件拦截

Penumbra 的核心原理是**运行时文件拦截（Runtime File Interception）**。当游戏请求加载任何资源时（比如 `chara/equipment/e0591/texture/v01_c0201e0591_sho_n.tex`），Penumbra 会 Hook 资源加载函数，查询预计算的 `CollectionCache` 路径映射表，决定是重定向到本地 Mod 文件还是放行加载原始数据。

关键特点：
- 所有路径映射在集合加载时**预计算完毕**，每次请求的解析时间在**微秒级**
- 游戏进程本身完全不知道收到的是替换后的文件
- 不修改磁盘上的任何游戏文件

### 支持的资源类型

| 资源类型 | 格式 | 说明 |
|---------|------|------|
| 贴图 | `.tex`（支持 `.dds`/`.png`/`.tga` 导入导出） | 核心贴图替换 |
| 3D 模型 | `.mdl`（支持 glTF/GLB 与 Blender 互通） | 角色和装备模型 |
| 材质 | `.mtrl`（支持着色器常量编辑） | 材质参数调整 |
| 视觉特效 | `.avfx` | 技能和环境特效 |
| 动画 | 多种格式 | 角色动作动画 |
| 元数据 | IMC、EQDP、EQP、EST、GMP、RSP、ATCH | 装备规则和配置 |

### 集合（Collection）系统

Penumbra 的集合系统允许**按角色、按场景**配置不同的 Mod。同一场景中，角色 A 和角色 B 可以显示完全不同的贴图。

### IPC 与 HTTP API

Penumbra 提供了丰富的插件间通信接口（IPC）和 HTTP API，使其成为整个 Mod 生态的基础设施：
- Glamourer 通过 Penumbra 投递外观更改所需的资源
- Heliosphere 将 Mod 直接安装到 Penumbra 目录
- VFXEditor 通过临时修改 API 注册文件替换
- 支持临时修改（configurable priority）、非持久化集合、设置锁定等高级功能

### 已知限制

- **仅客户端可见**：没有同步插件的情况下，其他玩家看不到你的 Mod
- 大多数更改需要**切换区域**才能对已加载的资源生效
- TexTools Modpack 的兼容性为尽力兼容而非完全保证
- 非常大的 Mod 集合可能延长初始加载时间
- 同步加载要求（`LoadPriority: 69420`）意味着 Penumbra 必须在游戏继续之前完成初始化

---

## 二、丰富的配套插件生态

Penumbra 并非孤立运作。一系列 Dalamud 插件扩展了其在整个视觉 Mod 流水线中的能力。

### VFXEditor — 最直接的游戏内实时编辑工具

**仓库：** [github.com/0ceal0t/Dalamud-VFXEditor](https://github.com/0ceal0t/Dalamud-VFXEditor)

支持在游戏内实时修改：
- `.atex` / `.tex` 贴图文件
- `.mtrl` 材质文件
- `.shpk` 着色器包
- `.avfx` 视觉特效
- `.mdl` 模型文件
- 动画和音效文件

操作方式：选中已加载的资源 → 在编辑器 UI 中修改 → 点击 UPDATE 即时应用。完成后可导出为 `.pmp` 或 `.ttmp2` 格式供 Penumbra 使用。

### Glamourer — 角色外观编排

由 Ottermandias 开发，负责协调角色外观变更（装备、种族、性别、自定义选项），并触发 Penumbra 的贴图和模型替换。高级自定义功能允许超出游戏正常范围的视觉参数，吸收了已停止维护的 Palette+ 插件的自定义发色、肤色和瞳色功能。

### Customize+

**仓库：** [github.com/Aether-Tools/CustomizePlus](https://github.com/Aether-Tools/CustomizePlus)

提供逐骨骼的身体比例缩放，影响贴图在修改后角色模型上的映射方式。

### Meddle — 游戏资源提取

**仓库：** [github.com/PassiveModding/Meddle](https://github.com/PassiveModding/Meddle)

实验性插件，可从运行中的游戏导出 3D 模型、原始贴图和动画，并保留着色器/材质数据，配套 Blender 插件用于设置游戏级材质。

### Heliosphere — Mod 分发平台

**网站：** [heliosphere.app](https://heliosphere.app)

FFXIV 社区的主要 Mod 分发平台，配套 Dalamud 插件实现一键安装、压缩下载和自动更新，直接安装到 Penumbra 目录。

### Brio & Ktisis — GPose 增强

允许在 GPose 拍照模式中为不同生成的角色分配不同的 Penumbra 集合（不同的贴图集），以及模型 ID 切换。

### Mod 同步工具（Mare Synchronos → Lightless Sync）

**Mare Synchronos** 曾是最主要的 Mod 同步工具，允许配对玩家实时看到对方的修改贴图。但在 **2025 年 8 月**，因 Square Enix 的法律介入而关停。目前由 **Lightless Sync** 及多个社区分支填补空缺。

---

## 三、FFXIV 贴图管线的技术原理

### SqPack 存储格式

FFXIV 将所有游戏数据存储在 **SqPack** 归档中——一种专有格式，文件以分块压缩存储，通过 CRC32 路径哈希进行索引。各资源类别有独立的索引文件，数据文件上限为 2GB。

| 类别 | 编号 | 说明 |
|------|------|------|
| 角色资源 | `0x04` | 角色模型、贴图 |
| UI 资源 | `0x06` | 界面贴图 |
| VFX 资源 | `0x08` | 特效贴图 |
| 着色器 | `0x05` | 渲染着色器 |

### .tex 贴图格式

游戏的贴图格式（`.tex`）本质上是带有 Square Enix 专有 **80 字节头**的 DDS 数据，包含类型标志、像素格式、尺寸、mipmap 数量和 LOD 偏移。支持的像素格式直接映射到 DXGI 标准：**BC1 至 BC7** 块压缩、`R32G32B32A32_FLOAT`、深度格式等。

资源层级关系：`.mdl`（模型）→ `.mtrl`（材质）→ `.tex`（贴图），每层通过完整游戏路径引用。

### Dalamud 框架提供的技术能力

#### IGameInteropProvider

通过字节模式签名扫描游戏二进制文件创建 Hook。签名比硬编码地址更能经受游戏更新，因为指令模式比内存布局更稳定。支持从签名、地址、DLL 导出、导入地址表或函数指针变量创建 Hook。

#### ITextureSubstitutionProvider

Dalamud v9 引入的一方 API，使用基于委托的拦截机制，让插件在 Dalamud 加载贴图数据时替换贴图路径（指向其他游戏贴图或磁盘文件）。这是面向只需重定向贴图路径（而非任意游戏文件）的插件的轻量化方案。

### Penumbra 的具体实现

Penumbra 在 `Penumbra.Interop.Hooks.ResourceLoading` 命名空间中直接 Hook 游戏的资源加载函数。这些 Hook 在启动时同步安装——在游戏开始加载资源之前——确保没有未修改的文件漏过。`CollectionCache` 在集合加载时预计算所有解析后的路径映射，使每次请求的解析几乎零开销。

替换贴图必须匹配预期的格式和尺寸（块压缩格式有特定的对齐要求），但 Penumbra 的 `TextureManager` 可在 `.tex`、`.dds`、`.png` 和 `.tga` 之间透明转换。

---

## 四、TexTools 与 Mod 制作流水线

### TexTools — 主要的 Mod 制作工具

**仓库：** [github.com/TexTools/FFXIV_TexTools_UI](https://github.com/TexTools/FFXIV_TexTools_UI)

一个独立的 Windows 桌面应用（当前版本 **3.1.0.2**，2025 年 12 月），提供：
- 贴图查看/编辑
- 3D 模型通过 FBX 导入/导出
- 材质和色组编辑
- 元数据操作
- Modpack 打包（`.ttmp2` 和 `.pmp` 格式）
- 3.1 版新增直接"导入 Modpack 到 Penumbra"功能和 Dawntrail 升级工具

**重要区别：** TexTools 历史上直接修改磁盘上的游戏文件，有损坏风险。社区目前的共识是：**TexTools 只用于制作 Mod，Penumbra 用于加载 Mod**，不要同时使用两者进行安装。

### Loose Texture Compiler

**仓库：** [github.com/Sebane1/FFXIVLooseTextureCompiler](https://github.com/Sebane1/FFXIVLooseTextureCompiler)

简化贴图 Mod 的制作流程，特性包括：
- 自动 Penumbra 路径生成
- 主流身体 Mod 之间的转换（Bibo+、TBSE、Eve、Freyja）
- 自动生成法线贴图
- 拖放式工作流

### Aetherment

**仓库：** [github.com/Sevii77/aetherment](https://github.com/Sevii77/aetherment)

跨平台 Mod 制作工具，提供高级自动贴图合成，并托管 Material UI 的延续版本"Material UI Reborn"。

### 标准 Mod 制作流程

```
提取资源（TexTools）
    ↓
编辑贴图（Photoshop / GIMP / Substance Painter）
或编辑模型（Blender）
    ↓
重新导入并转换（TexTools 或 Loose Texture Compiler）
    ↓
打包为 .pmp 格式
    ↓
分发（Heliosphere / XIV Mod Archive / Glamour Dresser）
    ↓
用户一键安装到 Penumbra
```

### Mod 格式对比

| 格式 | 说明 | 来源 |
|------|------|------|
| `.ttmp2` | TexTools v2 Modpack，类 ZIP 归档 + JSON 元数据 | TexTools |
| `.pmp` | Penumbra 原生格式，含 `meta.json`、`default_mod.json` 和选项组文件 | Penumbra |
| 松散文件夹 Mod | 镜像游戏路径的目录结构 | 手动创建 |

Penumbra 可导入以上所有三种格式，TexTools 3.1+ 支持跨格式转换。

---

## 五、生态系统近期变动

### Mare Synchronos 关停事件

2025 年 8 月，Square Enix 对 Mare Synchronos 发起法律行动，导致这个最受欢迎的 Mod 同步工具关停。这消除了主要的多人 Mod 同步层。**Lightless Sync** 及社区分支已填补空缺，但此事件表明发行商对 Mod 社区的关注度在提高。

尽管如此，**Penumbra 本身（v1.6.0.2，持续发布中）、TexTools（v3.1.0.2）、VFXEditor** 以及更广泛的工具链均未受影响。

---

## 六、开发者建议

如果你正在评估这个领域或考虑开发相关插件：

1. **Penumbra 的 IPC API 是核心集成点**——其临时修改系统、集合管理和路径解析 API 使任何 Dalamud 插件都能参与贴图修改流水线，无需重新实现文件拦截逻辑。

2. **Dalamud 的 `ITextureSubstitutionProvider`** 是更轻量的替代方案，适合只需重定向贴图路径（而非任意游戏文件）的插件。

3. **替换贴图需要注意格式匹配**：块压缩格式有特定对齐要求，替换文件必须匹配原始文件的预期格式和尺寸。

---

## 关键仓库汇总

| 项目 | 仓库 | 功能定位 |
|------|------|---------|
| **Penumbra** | [xivdev/Penumbra](https://github.com/xivdev/Penumbra) | 运行时文件拦截与 Mod 加载 |
| **Dalamud** | [goatcorp/Dalamud](https://github.com/goatcorp/Dalamud) | FFXIV 插件框架和 API |
| **VFXEditor** | [0ceal0t/Dalamud-VFXEditor](https://github.com/0ceal0t/Dalamud-VFXEditor) | 游戏内 VFX/贴图/材质/动画编辑 |
| **Glamourer** | [Ottermandias/Glamourer](https://github.com/Ottermandias/Glamourer) | 角色外观编排 |
| **Customize+** | [Aether-Tools/CustomizePlus](https://github.com/Aether-Tools/CustomizePlus) | 骨骼比例缩放 |
| **TexTools** | [TexTools/FFXIV_TexTools_UI](https://github.com/TexTools/FFXIV_TexTools_UI) | Mod 制作桌面工具 |
| **Loose Texture Compiler** | [Sebane1/FFXIVLooseTextureCompiler](https://github.com/Sebane1/FFXIVLooseTextureCompiler) | 贴图 Mod 简化制作 |
| **Aetherment** | [Sevii77/aetherment](https://github.com/Sevii77/aetherment) | 跨平台 Mod 制作 + Material UI |
| **Heliosphere** | [heliosphere-xiv](https://github.com/heliosphere-xiv) | Mod 分发平台 |
| **Brio** | [Etheirys/Brio](https://github.com/Etheirys/Brio) | GPose 增强 |
| **Lumina** | [NotAdam/Lumina](https://github.com/NotAdam/Lumina) | FFXIV 数据文件解析库 |
| **SeaOfStars** | [Ottermandias/SeaOfStars](https://github.com/Ottermandias/SeaOfStars) | Mod/自定义/摆姿套件 |
