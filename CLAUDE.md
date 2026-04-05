# SkinTatoo - FFXIV 实时皮肤贴花插件

## 项目概述

Dalamud 插件，将 PNG 贴花合成到 FFXIV 角色皮肤 UV 贴图上，通过 Penumbra 临时 Mod 实时预览。

## 构建

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo
dotnet build -c Release
```

Dalamud SDK 路径通过 `Directory.Build.props` 自动指向 `XIVLauncherCN`。如果构建失败检查 `%AppData%\XIVLauncherCN\addon\Hooks\dev\` 是否存在。

**每次修改代码后必须执行 `dotnet build -c Release` 验证编译通过。**

## 项目结构

```
SkinTatoo.slnx                       # 解决方案
Penumbra.Api/                         # git submodule (MeowZWR cn-temp)
SkinTatoo/SkinTatoo/                  # 主项目
  Plugin.cs                           # 入口
  Configuration.cs                    # 持久化配置
  Core/                               # 数据模型 (DecalLayer, DecalProject)
  Mesh/                               # 网格提取 (MeshExtractor, MeshData)
  Interop/                            # PenumbraBridge, TextureSwapService, EmissiveCBufferHook
  Services/                           # PreviewService, TexFileWriter, DecalImageLoader, MtrlFileWriter
  Http/                               # EmbedIO HTTP 调试服务器 (localhost:14780)
  Gui/                                # ImGui 窗口 (MainWindow, ConfigWindow, DebugWindow)
docs/                                 # 技术文档
  ConstantBuffer逆向分析.md           # CBuffer 内存布局和渲染管线分析
```

## 关键约定

- 命名空间：`SkinTatoo.*`（如 `SkinTatoo.Mesh`, `SkinTatoo.Core`）
- UI 语言：中文
- 代码注释：仅在关键处写英文注释
- 提交信息：不写 Co-Authored-By

## 依赖

| 库 | 用途 |
|---|---|
| Penumbra.Api (submodule) | Penumbra IPC 类型安全封装 |
| Lumina | .mdl/.tex 文件解析 |
| StbImageSharp | 图片加载 |
| EmbedIO | HTTP 调试服务器 |

## 参考项目（同目录下）

| 项目 | 用途 |
|---|---|
| FFXIVClientStructs | 游戏结构体定义（CharacterBase, Material, ConstantBuffer 等） |
| Glamourer-CN | 材质实时修改参考（ColorTable 纹理交换、PrepareColorSet hook） |
| Meddle | 材质/纹理读取参考（OnRenderMaterial 拦截） |
| VFXEditor-CN | VFX 编辑器参考 |

## HTTP 调试 API

插件启动后在 `http://localhost:14780/` 提供 REST API：

- `GET /api/status` — 插件状态
- `GET /api/project` — 当前项目 JSON
- `POST /api/layer` — 添加图层
- `PUT /api/layer/{id}` — 修改图层参数
- `DELETE /api/layer/{id}` — 删除图层
- `POST /api/preview` — 触发预览（自动选择 inplace/full）
- `POST /api/preview/full` — 强制 Full Redraw
- `POST /api/preview/inplace` — 强制 inplace swap
- `POST /api/mesh/load` — 加载网格
- `GET /api/mesh/info` — 网格信息
- `GET /api/log` — 最近日志

## 核心管线流程

### 首次预览（Full Redraw）
```
PNG 图片 → CPU UV 合成 → 写临时 .tex/.mtrl → Penumbra 临时 Mod → 角色重绘（闪一次）
```

### 后续参数调整（GPU Swap，无闪烁）
```
参数变化 → 异步 CPU 合成（后台线程） → GPU 纹理原子替换（主线程，瞬间完成）
```

通过 `TextureSwapService` 直接操作 `CharacterBase→Model→Material→TextureResourceHandle→Texture*` 指针，
用 `Device.CreateTexture2D` + `InitializeContents` + `Interlocked.Exchange` 实现零闪烁纹理替换。

贴花直接在 UV 空间合成：以 `UvCenter`/`UvScale`/`RotationDeg` 定位，对每个输出纹理像素采样贴花，alpha blend 叠加到基础贴图上。

### 发光（Emissive）系统

**初始化路径（Full Redraw 时执行一次）：**
- `MtrlFileWriter` 修改 .mtrl 文件：
  - 设置 `CategorySkinType` shader key → `ValueEmissive` (0x72E697CD)
  - 写入 `g_EmissiveColor` (0x38A64362) 着色器常量
  - 保留原始 `AdditionalData` 字节（Lumina 跳过此字段）
- 法线贴图 Alpha 通道作为发光遮罩（UV 空间合成）
- 记录 `emissiveOffset`（g_EmissiveColor 在 CBuffer 中的字节偏移）

**实时更新路径（每帧，无闪烁）：**
- `EmissiveCBufferHook` hook `ModelRenderer.OnRenderMaterial`
- 在渲染管线内通过 `LoadSourcePointer` 修改 CBuffer 数据
- UI 颜色/强度滑块直接调用 `TryDirectEmissiveUpdate()`，延迟仅 1 帧
- 有 ColorTable 的材质（character.shpk 等）→ ColorTable 纹理原子交换（参考 Glamourer）
- skin.shpk 材质（无 ColorTable）→ EmissiveCBufferHook 实时 CBuffer 更新

**状态清理：**
- 取消勾选"发光"或隐藏图层时，调用 `ClearEmissiveHookTargets()` 清除 hook 状态
- `ResetSwapState()` 清除所有 GPU swap 和 hook 状态

## 已验证的技术结论

### 材质 CBuffer 通过 OnRenderMaterial 内 LoadSourcePointer 可实时更新
- 在 OnRenderMaterial **外部** 调用 LoadSourcePointer：CPU 写入成功但 GPU 不重新上传（渲染命令已构建）
- 在 OnRenderMaterial **内部** 调用 LoadSourcePointer：设置 DirtySize + UnsafeSourcePointer，随后的渲染提交会读取更新后的数据
- `ConstantBuffer.Flags` 初始值为 `0x4`（静态），Buffer[0/1/2] 指向 CPU 内存（非 ID3D11Buffer）
- 详细分析见 `docs/ConstantBuffer逆向分析.md`

### skin.shpk 材质无 ColorTable
- `DataSetSize = 0`，`HasColorTable = false`
- emissive 颜色完全存在 CBuffer 着色器常量 `g_EmissiveColor` 中
- ColorTable 纹理交换对 skin.shpk 不适用

### Penumbra 重定向后 MaterialResourceHandle.FileName 格式
- 格式：`|prefix_hash|disk_path.mtrl`（如 `|1_2_FE59D746_0893|C:/.../preview_gBD8558DD.mtrl`）
- 不再是游戏路径，匹配时需同时用游戏路径和磁盘路径

### ConstantBuffer 内存布局（已确认，0x70 bytes）
```
+0x00  vtable pointer (ReferencedClassBase)
+0x18  int InitFlags (创建时传入)
+0x20  int ByteSize (16 字节对齐)
+0x24  int Flags (0x4 = static, 0x1 = dynamic, 0x4000 = GPU D3D11Buffer)
+0x28  void* UnsafeSourcePointer (由 LoadSourcePointer 设置)
+0x30  int StagingSize
+0x34  int DirtySize (渲染提交读取此值决定上传大小)
+0x38  vtable2 pointer
+0x50  void* Buffer[0] (三重缓冲 CPU 内存指针 / 或 ID3D11Buffer* 当 flags & 0x4000)
+0x58  void* Buffer[1]
+0x60  void* Buffer[2]
+0x68  void* StagingPtr
```
**结论**：材质 CBuffer (Flags=0x4) 的 Buffer[] 是 CPU 内存，不含 ID3D11Buffer*。
GPU 缓冲区由渲染提交管线按需创建上传。

## EmissiveCBufferHook 实现细节

**文件**: `Interop/EmissiveCBufferHook.cs`

**Hook 目标**: `ModelRenderer.OnRenderMaterial`（签名 `"E8 ?? ?? ?? ?? 44 0F B7 28"`）

**工作原理**:
1. hook detour 在原始函数调用**之前**执行
2. 通过 `MaterialResourceHandle` 指针匹配目标材质（ConcurrentDictionary，线程安全）
3. 从 `ShaderPackage.MaterialElements` 查找 `g_EmissiveColor` (CRC 0x38A64362) 的偏移（结果缓存）
4. 调用 `LoadSourcePointer(offset, 12, 2)` 标记脏并获取可写指针
5. 写入新的 RGB 值
6. 调用原始函数 → 渲染提交读取更新后的数据 → GPU 上传

**线程安全**: `ConcurrentDictionary` 保证 UI 线程写 / 渲染线程读不冲突

**异常处理**: 限流机制，最多记录 5 次错误防止日志爆炸

**相关 IDA 地址**: 见 `docs/ConstantBuffer逆向分析.md`

## 游戏内测试

1. `/xlsettings` → Dev Plugin Locations 添加输出目录
2. `/xlplugins` 启用 SkinTatoo
3. `/skintatoo` 打开编辑器
4. HTTP: `curl http://localhost:14780/api/status`
