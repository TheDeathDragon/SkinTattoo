<p align="center">
  <img src="images/icon.png" width="128" height="128" alt="SkinTattoo">
</p>

<h1 align="center">SkinTattoo</h1>

<p align="center">English | <a href="README.zh-CN.md">简体中文</a></p>

SkinTattoo is a Dalamud plugin that composites image decals onto Final Fantasy XIV character skin textures in real time. It operates entirely in UV space, previews through Penumbra, and does not modify the game installation. The plugin is still under active development; bugs are expected. Feedback is welcome via GitHub Issues or [Discord](https://discord.gg/FPY94anSRN).

## Screenshots

<p align="center">
  <img src="images/screenshot_en.png" width="820" alt="SkinTattoo editor">
</p>

## Features

* Project PNG decals onto character skin (diffuse + normal) with live preview through Penumbra.
* Per-layer emissive with independent color and intensity; ColorTable-based PBR editing (character.shpk / skin.shpk / iris.shpk).
* Per-layer emissive animation: **Pulse**, **Flicker**, **Gradient** (two-color lerp), and **Ripple** (radial / linear / bidirectional wave with optional dual color) — all driven by the engine's native `m_LoopTime` via a custom DXBC injection, no per-frame CPU hook.
* Iris glow support via automatic mask-red-channel generation from vanilla masks; iris supports Pulse / Flicker / Gradient via real-time CBuffer modulation.
* Multi-model UV matching: collects all meshes sharing the same material, including non-standard body mods (bibo, etc.).
* Built-in 3D editor for placing decals by clicking on the model; UV canvas with wireframe overlay and half-clip preprocessing (for mirrored UV layouts).
* Zero-flicker GPU texture swap after the first preview; no character redraw needed for subsequent parameter changes.
* Mod packaging: export a standard `.pmp` package or install directly into Penumbra through IPC.

## Installation

This plugin is distributed through a custom Dalamud repository. Two manifests are published: `repo.json` (English card) and `repo.cn.json` (Chinese card). They ship the same binary — pick whichever language you prefer for the plugin installer description.

1. Type `/xlsettings` in chat, open the **Experimental** tab.
2. Add one of the URLs below under **Custom Plugin Repositories**, click the `+` button, then **Save and Close**:

   ```
   https://raw.githubusercontent.com/TheDeathDragon/SkinTattoo/repo/repo.json
   ```

   Or for the Chinese installer card:

   ```
   https://raw.githubusercontent.com/TheDeathDragon/SkinTattoo/repo/repo.cn.json
   ```

3. Open `/xlplugins` and search for **SkinTattoo** in the **All Plugins** tab, then install.

The plugin's in-game UI language is independent from the installer card — you can switch it at any time via the language dropdown on the Settings tab.

Do not manually unpack release archives into `devPlugins` — you will not receive updates and may conflict with installed copies.

## Usage

* Type `/skintattoo` in chat to open the editor.
* Load your character (stand on any map where your character is visible), then select a target material from the resource browser.
* Add decal layers, drag them on the UV canvas, or click directly on the 3D model.
* Parameter tweaks (position, scale, rotation, color, emissive) update the running game instantly.

## Build from Source

Requires Dalamud SDK 14 (XIVLauncher dev hooks):

```bash
git clone --recursive https://github.com/TheDeathDragon/SkinTattoo.git
cd SkinTattoo
dotnet build -c Release
```

The build resolves Dalamud references from `%AppData%\XIVLauncherCN\addon\Hooks\dev\` by default (see `Directory.Build.props`). Override with `DALAMUD_HOME` env var if you run the international launcher.

## Contributing

Pull requests and issues are welcome.

## Reference Projects

SkinTattoo builds on research and prior art from the following projects:

* [Penumbra](https://github.com/Ottermandias/Penumbra)
* [Glamourer](https://github.com/Ottermandias/Glamourer)
* [Meddle](https://github.com/PassiveModding/Meddle)
* [Lumina](https://github.com/NotAdam/Lumina)

## License

SkinTattoo is licensed under the Apache License 2.0. See [LICENSE](LICENSE) for the full text.
