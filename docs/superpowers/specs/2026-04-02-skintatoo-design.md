# SkinTatoo 插件设计文档

## 概述

SkinTatoo 是一个 Dalamud 插件，用于在 FFXIV 游戏内实时将 PNG 贴花投影到角色皮肤上。通过 GPU 计算着色器将贴花从 3D 空间投影到 UV 空间，合成到皮肤贴图后经由 Penumbra 临时 Mod 系统实时预览。支持漫反射和法线通道修改，可实现纹身、伤疤、发光标记等效果。

内嵌 HTTP 调试服务器（EmbedIO），暴露 REST API 供外部工具调用。

## 范围

**包含：**
- 皮肤贴花投影（Body、Face）
- 漫反射通道合成（颜色贴花）
- 法线通道合成（RNM 混合，实现凸起/发光质感）
- 多图层叠加
- 通过 Penumbra 临时 Mod 实时预览
- 内嵌 HTTP 调试服务器
- 身体 Mod 类型检测（Vanilla/Gen3/Bibo+/TBSE）

**不包含：**
- 装备/武器贴花
- 游戏内 3D 叠加层定位（OverlayWindow）
- 内置绘画工具
- 3D 视口预览
- .pmp Mod 导出（后续版本）
- BC 块压缩（输出未压缩 RGBA32 .tex，后续版本添加 BCnEncoder）

## 项目结构

```
SkinTatoo/
├── SkinTatoo.sln
├── Penumbra.Api/                     # git submodule (MeowZWR/Penumbra.Api cn-temp)
├── docs/
├── SkinTatoo/
│   ├── SkinTatoo.csproj
│   ├── SkinTatoo.json
│   ├── Plugin.cs                     # 入口，初始化所有服务
│   ├── Configuration.cs              # 持久化配置
│   ├── Core/
│   │   ├── DecalLayer.cs             # 贴花参数
│   │   └── DecalProject.cs           # 图层集合，目标仅 Body/Face
│   ├── Gpu/
│   │   ├── DxManager.cs              # 独立 D3D11 设备
│   │   ├── ComputeShaderPipeline.cs  # 投影→合成→膨胀 3 Pass
│   │   ├── StagingReadback.cs        # GPU→CPU 回读
│   │   └── Shaders/
│   │       ├── ProjectionPass.hlsl
│   │       ├── CompositePass.hlsl
│   │       └── DilationPass.hlsl
│   ├── Mesh/
│   │   ├── MeshData.cs
│   │   ├── MeshExtractor.cs          # Lumina .mdl 解析
│   │   └── PositionMapGenerator.cs   # UV 位置图/法线图
│   ├── Interop/
│   │   ├── PenumbraBridge.cs         # Penumbra.Api.IpcSubscribers 封装
│   │   └── BodyModDetector.cs        # 身体 Mod 类型检测
│   ├── Services/
│   │   ├── PreviewService.cs         # 串联管线：GPU→.tex→Penumbra 预览
│   │   ├── TexFileWriter.cs          # .tex 文件输出
│   │   └── DecalImageLoader.cs       # 图片导入
│   ├── Http/
│   │   └── DebugServer.cs            # EmbedIO HTTP 服务器
│   └── Gui/
│       ├── MainWindow.cs             # 图层列表 + 参数面板
│       └── ConfigWindow.cs           # 设置
```

## 依赖

| 库 | 版本 | 用途 |
|---|---|---|
| Dalamud.NET.Sdk | 14.0.2 | 插件框架 |
| Penumbra.Api | submodule (cn-temp) | Penumbra IPC 类型安全封装 |
| Lumina | 4.* | .mdl/.tex 文件解析 |
| StbImageSharp | 2.* | PNG/JPG/TGA 图片加载 |
| TerraFX.Interop.Windows | 10.* | DX11 计算着色器互操作 |
| EmbedIO | 3.5.2 | 内嵌 HTTP 服务器 |

## 核心模块设计

### GPU 管线（从 Sigillum 迁移）

三个计算着色器 Pass，每个以 `[numthreads(8,8,1)]` 调度：

**Pass 1 — ProjectionPass.hlsl：**
输入位置图、法线图、贴花贴图。对每个 UV 像素，将其 3D 位置变换到贴花投影器空间，范围检查，背面剔除，掠角淡出，采样贴花贴图，写入 DecalBuffer。

**Pass 2 — CompositePass.hlsl：**
将 DecalBuffer 与基础贴图混合。漫反射支持 4 种混合模式（Normal/Multiply/Overlay/SoftLight）。法线通道使用 Reoriented Normal Mapping（RNM）混合——计算最短弧旋转，约 5-6 ALU 指令，完整保留两个法线的强度。

**Pass 3 — DilationPass.hlsl：**
8 邻域最近邻泛洪填充，迭代 8 次，填充 UV 岛间隙防止 Mipmap 接缝。

GPU 资源全部在独立 D3D11 设备上创建（DxManager），不干扰游戏渲染状态。

### Penumbra 集成（PenumbraBridge）

使用 `Penumbra.Api.IpcSubscribers` 替代 Sigillum 的手写 IPC 签名：

```csharp
// 类型安全，无签名错误风险
var createCollection = new CreateTemporaryCollection(pluginInterface);
var addTempMod = new AddTemporaryMod(pluginInterface);
var redrawObject = new RedrawObject(pluginInterface);
var resolvePlayer = new ResolvePlayerPath(pluginInterface);
```

预览流程：
1. `CreateTemporaryCollection` — 创建临时集合
2. `AddTemporaryMod` — 注册 gameTexturePath → localFilePath 映射
3. `RedrawObject` — 触发角色重绘

### 网格提取（从 Sigillum 迁移）

通过 Lumina 解析 .mdl 文件，提取顶点位置、法线、UV。然后在 CPU 端将所有三角形光栅化到 UV 空间，通过重心坐标插值生成位置图和法线图。

皮肤贴图路径约定：
```
chara/human/c{raceCode}/obj/body/b0001/texture/--c{raceCode}b0001_{type}.tex
```

### 预览服务（PreviewService）

串联完整管线：
1. 加载网格 → 生成位置图/法线图（首次/切换时）
2. 加载贴花图片 → 上传 GPU
3. 遍历可见图层，逐层执行 Projection + Composite
4. 执行 Dilation（8 次迭代）
5. GPU 回读 → TexFileWriter 写临时 .tex
6. PenumbraBridge 设置重定向 + 重绘

### HTTP 调试服务器

基于 EmbedIO（Brio 已验证可用），监听 `http://localhost:14780/`：

| 端点 | 方法 | 功能 |
|------|------|------|
| `/api/status` | GET | 插件状态（GPU、Penumbra、网格加载） |
| `/api/project` | GET | 当前项目 JSON |
| `/api/layer` | POST | 添加图层 |
| `/api/layer/{id}` | PUT | 修改图层参数 |
| `/api/layer/{id}` | DELETE | 删除图层 |
| `/api/layer/{id}/image` | POST | 上传贴花图片 |
| `/api/preview` | POST | 触发预览更新 |
| `/api/mesh/load` | POST | 加载网格 |
| `/api/mesh/info` | GET | 网格信息 |
| `/api/log` | GET | 最近日志 |

### GUI

**MainWindow** — 单窗口双面板布局：
- 左侧：图层列表（添加/删除/可见性/排序）+ 目标选择（Body/Face）
- 右侧：选中图层的参数（名称、图片路径、位置/旋转/缩放、深度、透明度、混合模式、通道开关）
- 底部：预览按钮、状态指示

**ConfigWindow** — HTTP 服务器端口配置、临时文件目录。

### Configuration

持久化到 Dalamud 配置目录：
- HTTP 端口（默认 14780）
- 上次使用的贴花目标
- 贴图分辨率设置（默认 1024）

## 从 Sigillum 迁移的模块

| 源文件 | 目标 | 变更 |
|--------|------|------|
| `Gpu/DxManager.cs` | `Gpu/DxManager.cs` | 改命名空间 |
| `Gpu/ComputeShaderPipeline.cs` | `Gpu/ComputeShaderPipeline.cs` | 改命名空间，嵌入资源名更新 |
| `Gpu/StagingReadback.cs` | `Gpu/StagingReadback.cs` | 改命名空间 |
| `Gpu/Shaders/*.hlsl` | `Gpu/Shaders/*.hlsl` | 原样迁移 |
| `Mesh/MeshData.cs` | `Mesh/MeshData.cs` | 改命名空间 |
| `Mesh/MeshExtractor.cs` | `Mesh/MeshExtractor.cs` | 改命名空间 |
| `Mesh/PositionMapGenerator.cs` | `Mesh/PositionMapGenerator.cs` | 改命名空间 |
| `Preview/TexFileWriter.cs` | `Services/TexFileWriter.cs` | 改命名空间 |
| `Core/DecalLayer.cs` | `Core/DecalLayer.cs` | 改命名空间 |
| `Library/DecalImporter.cs` | `Services/DecalImageLoader.cs` | 改命名空间+类名 |

## 砍掉的模块

| 模块 | 原因 |
|------|------|
| `Drawing/BrushEngine.cs` | 不需要内置绘画，用现成 PNG |
| `Drawing/DrawingCanvas.cs` | 同上 |
| `Windows/DrawingWindow.cs` | 同上 |
| `Windows/OverlayWindow.cs` | 复杂度高，依赖 FFXIVClientStructs，后续版本考虑 |
| `Windows/LibraryWindow.cs` | 图片选择集成到 MainWindow |
| `Preview/ViewportRenderer.cs` | 后续版本考虑 |
| `Library/DecalLibrary.cs` | 简化为直接路径输入 |
| `Localization/Loc.cs` | 直接硬编码中文 |

## 新增模块

| 模块 | 说明 |
|------|------|
| `Http/DebugServer.cs` | EmbedIO HTTP 服务器 + REST API 路由 |
| `Interop/PenumbraBridge.cs` | 基于 Penumbra.Api.IpcSubscribers 的类型安全封装 |
| `Services/PreviewService.cs` | 从 Sigillum PreviewManager 重构，职责更清晰 |
