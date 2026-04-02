# SkinTatoo 开发踩坑记录

## 国服 (CN) 环境

### Dalamud SDK 路径
- 国服使用 `XIVLauncherCN`，路径：`%AppData%\XIVLauncherCN\addon\Hooks\dev\`
- 不是国际服的 `XIVLauncher\addon\Hooks\dev\`
- 通过 `Directory.Build.props` 设置 `DALAMUD_HOME` 指向正确路径

### Lumina 版本冲突
- **Dalamud CN 自带 Lumina 2.4.2**，不要通过 NuGet 引用其他版本
- NuGet 的 `Lumina 4.*` 会导致 `IDataManager.GetFile<MdlFile>()` 返回 null（类型不匹配）
- 使用 `AtmoOmen/Lumina` fork 作为 submodule 可解决 `.tex` 文件解码

### SqPack 读取限制
- `IDataManager.FileExists()` 对国服的 `.mdl` 和 `.tex` 文件返回 false
- `.mtrl` 文件可以正常读取
- 原因：国服 SqPack 的 Model/Texture 类型文件解码格式可能和 Lumina 2.4.2 不兼容
- **解决方案**：使用 Meddle 的 SqPack 读取器（`Meddle.Utils.Files.SqPack`），它有独立的 Model/Texture 文件解析实现

### 从 Meddle 提取网格数据
- `Meddle.Utils.Export.Model` 类正确处理顶点声明、位置/法线/UV 解析
- FFXIV 的 **UV V 坐标是负数**（范围 [-1, 0]），需要取反：`uv.Y = -rawUv.Y`
- 不取反会导致 `PositionMapGenerator` 生成空的位置图（0 valid pixels）

## Penumbra IPC

### 临时 Mod 方式
- **不要用** `CreateTemporaryCollection` + `AssignTemporaryCollection`
  - 这会**替换**玩家的整个 Penumbra 集合，导致所有已有 Mod（如 Eve body mod）失效
- **应该用** `AddTemporaryModAll`
  - 全局叠加，不影响现有 Mod 集合
  - 在所有集合之上添加临时文件重定向

### GetPlayerResources 的路径格式
- 返回 `Dictionary<ushort, Dictionary<string, HashSet<string>>>`
- 格式：`objectIndex → diskPath → Set<gamePath>`
- **注意**：设置临时 Mod 后，disk path 可能变成我们自己的输出文件（preview.tex），形成自引用
- 应在创建临时 Mod 之前缓存原始路径

### Eve 等身体 Mod 的路径特殊性
- Eve 不使用标准路径 `chara/human/cXXXX/obj/body/b0001/texture/...`
- Eve 使用自定义路径如 `chara/nyaughty/eve/gen3_raen_base.tex`
- 材质文件 `.mtrl` 引用这些自定义路径
- 不能用种族码硬编码来查找，应让用户在调试窗口手动选择

## .tex 文件格式

### Header (80 bytes)
- offset 0: `uint32` attributes — 必须包含 `0x00800000` (TextureType2D)
- offset 4: `uint32` format — `0x1450` = B8G8R8A8
- offset 8: `uint16` width
- offset 10: `uint16` height
- offset 12: `uint16` depth (1)
- offset 14: `uint16` mip count (1)
- offset 24: `uint32` surface 0 offset (80)

### 常见错误
- attributes 写 0 会导致游戏不显示贴图（透明）
- 像素字节序必须是 **BGRA**（不是 RGBA），format 0x1450 对应 B8G8R8A8
- 写文件时用 `FileShare.Read` 防止 Penumbra/游戏读取时文件锁定

### .tex 解码
- 使用 `Lumina.GameData.GetFileFromDisk<TexFile>(path)` 可以解码 BC 压缩的 .tex 文件
- `TexFile.ImageData` 返回 B8G8R8A8 数据，需要手动转换为 RGBA

## UV 空间贴花 vs 3D 投影

### 3D 投影的问题
- 正交投影到 UV 空间会导致贴花碎片化——因为 UV 岛是分散的
- 一个连续的 3D 区域在 UV 空间中可能分布在多个岛上
- 效果类似"破碎的贴纸"

### UV 空间直接贴图（当前方案）
- 直接在 UV 坐标系中放置贴花图片
- 参数：UV 中心点、UV 大小、旋转角度
- 简单、精确、不碎片化
- 类似 Photoshop 中在 UV 展开图上贴图
- 缺点：需要用户理解 UV 布局（通过贴图预览辅助）

## 游戏内贴图尺寸
- 原版身体贴图：1024x1024 或 1024x2048
- Eve 等高清 Mod：4096x4096
- 输出分辨率应匹配或可配置
- 底图需要缩放到 map 分辨率（最近邻插值足够）
