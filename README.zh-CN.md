<p align="center">
  <img src="images/icon.png" width="128" height="128" alt="SkinTattoo">
</p>

<h1 align="center">SkinTattoo</h1>

<p align="center"><a href="README.md">English</a> | 简体中文</p>

SkinTattoo 是一个 Dalamud 插件，可将图片贴花实时合成到《最终幻想14》角色的皮肤贴图上。完全在 UV 空间运算，通过 Penumbra 进行预览，不会修改游戏本体。目前还在开发中，有很多 Bug 是正常的，可以通过 GitHub Issue 或 [Discord](https://discord.gg/FPY94anSRN) 反馈。

## 功能截图

<p align="center">
  <img src="images/screenshot_cn.png" width="820" alt="SkinTattoo 编辑器">
</p>

## 功能特性

* 将 PNG 贴花投影到角色皮肤（漫反射 + 法线），通过 Penumbra 实时预览。
* 每个图层独立的发光色与强度；基于 ColorTable 的 PBR 编辑（character.shpk / skin.shpk / iris.shpk）。
* 每个图层独立的发光动画：**脉冲**、**闪烁**、**双色渐变**、**水波纹**（同心圆 / 线性 / 双向扩散，可选双色）；基于引擎原生 `m_LoopTime` 时间变量的 DXBC 注入实现，无需逐帧 CPU hook。
* 虹膜发光支持：从原版 mask 自动生成 mask 红通道；虹膜同样支持脉冲/闪烁/双色渐变动画，通过实时 CBuffer 调制。
* 多模型 UV 匹配：自动收集共享同一材质的所有网格，兼容非标准身型 mod（bibo 等）。
* 内置 3D 编辑器：直接在模型上点选放置贴花；UV 画布带线框叠加，支持半边裁剪（处理镜像 UV 布局）。
* 首次预览后使用零闪烁 GPU 纹理替换；后续参数调整无需角色重绘。
* Mod 导出：可导出标准 `.pmp` 包，或通过 IPC 直接安装到 Penumbra。

## 安装方式

插件通过自定义 Dalamud 仓库分发。提供两份清单：`repo.cn.json`（中文描述）和 `repo.json`（英文描述），二进制完全一致，按你习惯的安装器卡片语言选一个即可。

1. 游戏内输入 `/xlsettings`，打开**测试版**（Experimental）标签页。
2. 在**自定义插件仓库**中粘贴以下任一 URL，点击 `+` 按钮，然后**保存并关闭**：

   ```
   https://raw.githubusercontent.com/TheDeathDragon/SkinTattoo/repo/repo.cn.json
   ```

   或者英文卡片：

   ```
   https://raw.githubusercontent.com/TheDeathDragon/SkinTattoo/repo/repo.json
   ```

3. 打开 `/xlplugins`，在**所有插件**标签页搜索 **SkinTattoo**，点击安装。

插件运行时的界面语言独立于安装卡片，可以随时在设置 Tab 顶部的语言下拉框切换。

请勿手动解压 Releases 压缩包到 `devPlugins` 目录 —— 这样做无法收到更新，并且可能与已安装的副本冲突。

## 使用方式

* 游戏内输入 `/skintattoo` 打开编辑器。
* 加载角色（在任意角色可见的地图即可），然后在资源浏览器中选择目标材质。
* 添加贴花图层，在 UV 画布拖动，或直接在 3D 模型上点击放置。
* 参数调整（位置、缩放、旋转、颜色、发光）会实时反映到运行中的游戏。

## 源码构建

需要 Dalamud SDK 14（XIVLauncher dev hooks）：

```bash
git clone --recursive https://github.com/TheDeathDragon/SkinTattoo.git
cd SkinTattoo
dotnet build -c Release
```

构建默认从 `%AppData%\XIVLauncherCN\addon\Hooks\dev\` 解析 Dalamud 引用（详见 `Directory.Build.props`）。如果你用的是国际服启动器，可通过 `DALAMUD_HOME` 环境变量覆盖。

## 贡献

欢迎贡献代码。欢迎提交 PR。欢迎提交 Issue。

## 参考项目

SkinTattoo 的实现参考了以下项目的研究与先行工作：

* [Penumbra](https://github.com/Ottermandias/Penumbra)
* [Glamourer](https://github.com/Ottermandias/Glamourer)
* [Meddle](https://github.com/PassiveModding/Meddle)
* [Lumina](https://github.com/NotAdam/Lumina)

## 许可证

SkinTattoo 使用 Apache License 2.0 许可证发布。完整条款详见 [LICENSE](LICENSE)。
