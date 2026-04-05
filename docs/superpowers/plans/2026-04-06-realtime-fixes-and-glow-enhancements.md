# SkinTatoo 实时预览修复与发光增强 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复实时参数更新的 bug，完善发光实时调整，添加高亮预览按钮，自动加载模型，增强发光遮罩模式和预览效果。

**Architecture:** 5 个独立任务：(1) 修复遗漏的 MarkPreviewDirty 调用，(2) 为"贴图"checkbox 添加 tooltip 说明，(3) 参考 Glamourer 实现实时发光 CBuffer 写入 + 高亮按钮，(4) 插件初始化时自动加载模型，(5) 增强发光遮罩模式（新增类型 + 羽化参数 + 彩色贴花预览）。

**Tech Stack:** C# / Dalamud / ImGui / Penumbra IPC / Direct3D unsafe pointer

---

## 文件结构

| 操作 | 文件 | 职责 |
|------|------|------|
| 修改 | `SkinTatoo/SkinTatoo/Gui/MainWindow.cs` | 修复 dirty 调用、tooltip、高亮按钮、遮罩预览、自动加载 |
| 修改 | `SkinTatoo/SkinTatoo/Services/PreviewService.cs` | 新增发光遮罩模式、优化 emissive CBuffer 路径 |
| 修改 | `SkinTatoo/SkinTatoo/Interop/TextureSwapService.cs` | 高亮 RGB 循环写入 |
| 修改 | `SkinTatoo/SkinTatoo/Core/DecalLayer.cs` | 新增 EmissiveMask 枚举值 |
| 修改 | `SkinTatoo/SkinTatoo/Plugin.cs` | 初始化时自动加载模型 |
| 修改 | `SkinTatoo/SkinTatoo/CLAUDE.md` | 更新已知限制说明 |

---

### Task 1: 修复实时参数更新 — 遗漏的 MarkPreviewDirty 调用

**问题根因分析：**
- 图层可见性切换 (`MainWindow.cs:343-344`) 不触发 `MarkPreviewDirty()`
- 图片路径手动输入 (`MainWindow.cs:754-758`) 不触发 `MarkPreviewDirty()`
- 图片路径文件选择对话框回调 (`MainWindow.cs:774-779`) 不触发 `MarkPreviewDirty()`
- 这些是 UV 位置/角度/透明度"概率性没有实时生效"的根本原因 — 因为某些操作后 dirty flag 未被设置

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Gui/MainWindow.cs:343-344, 754-758, 774-779`

- [ ] **Step 1: 修复图层可见性切换**

在 `MainWindow.cs` 第 343-344 行，给可见性切换添加 `MarkPreviewDirty()` 调用：

```csharp
// Before (line 343-344):
if (ImGuiComponents.IconButton(100 + li, visIcon))
    layer.IsVisible = !layer.IsVisible;

// After:
if (ImGuiComponents.IconButton(100 + li, visIcon))
{ layer.IsVisible = !layer.IsVisible; MarkPreviewDirty(); }
```

- [ ] **Step 2: 修复图片路径手动输入**

在 `MainWindow.cs` 第 754-758 行，给路径输入添加 `MarkPreviewDirty()` 调用并清除图片缓存：

```csharp
// Before (line 754-758):
if (ImGui.InputText("##ImagePath", ref imagePathBuf, 512))
{
    layer.ImagePath = imagePathBuf;
    lastEditedLayerIndex = idx;
}

// After:
if (ImGui.InputText("##ImagePath", ref imagePathBuf, 512))
{
    layer.ImagePath = imagePathBuf;
    lastEditedLayerIndex = idx;
    MarkPreviewDirty();
}
```

- [ ] **Step 3: 修复文件选择对话框回调**

在 `MainWindow.cs` 第 774-779 行文件对话框回调内添加 `MarkPreviewDirty()`：

```csharp
// Before (line 774-779):
var path = paths[0];
g.Layers[capturedLi].ImagePath = path;
imagePathBuf = path;
lastEditedLayerIndex = capturedLi;
config.LastImageDir = Path.GetDirectoryName(path);
config.Save();

// After:
var path = paths[0];
g.Layers[capturedLi].ImagePath = path;
imagePathBuf = path;
lastEditedLayerIndex = capturedLi;
config.LastImageDir = Path.GetDirectoryName(path);
config.Save();
MarkPreviewDirty();
```

- [ ] **Step 4: 构建验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 5: 提交**

```bash
git add SkinTatoo/SkinTatoo/Gui/MainWindow.cs
git commit -m "fix: 修复可见性切换和图片路径变更未触发预览更新"
```

---

### Task 2: 为"贴图"checkbox 添加 tooltip 并确认功能正确性

**问题分析：**
- "贴图" checkbox 控制 `AffectsDiffuse`，决定贴花是否合成到漫反射贴图
- 功能本身已正确连线 (`CpuUvComposite` 第 662 行检查 `AffectsDiffuse`)
- 但缺少 tooltip，用户不理解其作用
- 如果只勾选"发光"不勾选"贴图"，贴花颜色不上色但发光区域仍生效 — 这是设计意图但用户不明确

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Gui/MainWindow.cs:852-854`

- [ ] **Step 1: 添加 tooltip**

在 `MainWindow.cs` 第 854 行之后添加 tooltip：

```csharp
// Before (line 852-854):
var affDiff = layer.AffectsDiffuse;
if (ImGui.Checkbox("贴图", ref affDiff))
{ layer.AffectsDiffuse = affDiff; MarkPreviewDirty(); }

// After:
var affDiff = layer.AffectsDiffuse;
if (ImGui.Checkbox("贴图", ref affDiff))
{ layer.AffectsDiffuse = affDiff; MarkPreviewDirty(); }
if (ImGui.IsItemHovered()) ImGui.SetTooltip("将贴花颜色合成到漫反射贴图。\n关闭后贴花不上色，但仍可作为发光区域。");
```

- [ ] **Step 2: 构建验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 3: 提交**

```bash
git add SkinTatoo/SkinTatoo/Gui/MainWindow.cs
git commit -m "fix: 为贴图checkbox添加tooltip说明"
```

---

### Task 3: 实时发光调整 + 高亮预览按钮

**问题分析：**
当前发光颜色/强度修改需要 Full Redraw（闪烁），因为 emissive 存储在 .mtrl shader 常量中。但实际上 `TextureSwapService.UpdateEmissiveColor()` 已经实现了 CBuffer 直写。问题在于：
1. `StartAsyncInPlace` 中 emissive CBuffer 更新依赖 `emissiveOffsets` 字典，该字典仅在 `UpdatePreviewFull` 中的 `TryBuildEmissiveMtrl` 填充
2. 首次 full redraw 后 offset 已知，后续 GPU swap 路径中 emissive 更新其实已经在工作
3. 但如果 emissive 参数变化后触发了 full redraw（而非 GPU swap），就会闪烁

**Glamourer 参考实现：**
- Glamourer 的 `LiveColorTablePreviewer.cs` 通过 `IFramework.Update` 每帧更新颜色表纹理
- 使用 HSV 色环循环生成 rainbow 颜色
- 通过 `DirectXService.ReplaceColorTable()` 原子替换纹理
- 鼠标悬浮时激活，离开时重置

**我们的实现方案：**
- 在贴花列表每个图层旁添加"高亮"按钮
- 悬浮时通过 `TextureSwapService.UpdateEmissiveColor()` 写入 RGB 循环色
- 离开悬浮时恢复原色
- 同时确保发光颜色/强度的普通修改也走 GPU swap 路径（不闪烁）

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Gui/MainWindow.cs`
- Modify: `SkinTatoo/SkinTatoo/Interop/TextureSwapService.cs`
- Modify: `SkinTatoo/SkinTatoo/Services/PreviewService.cs`

- [ ] **Step 1: 在 TextureSwapService 添加 HSV→RGB 工具方法**

在 `TextureSwapService.cs` 末尾（`RgbaToBgra` 方法之后）添加：

```csharp
/// <summary>Convert HSV (h 0-1, s 0-1, v 0-1) to RGB Vector3.</summary>
public static Vector3 HsvToRgb(float h, float s, float v)
{
    h = ((h % 1f) + 1f) % 1f;
    int hi = (int)(h * 6f) % 6;
    float f = h * 6f - (int)(h * 6f);
    float p = v * (1f - s);
    float q = v * (1f - f * s);
    float t = v * (1f - (1f - f) * s);
    return hi switch
    {
        0 => new Vector3(v, t, p),
        1 => new Vector3(q, v, p),
        2 => new Vector3(p, v, t),
        3 => new Vector3(p, q, v),
        4 => new Vector3(t, p, v),
        _ => new Vector3(v, p, q),
    };
}
```

- [ ] **Step 2: 在 MainWindow 添加高亮状态字段**

在 `MainWindow.cs` 字段区域（约第 48 行 `showHelpWindow` 之后）添加：

```csharp
// Highlight glow state
private bool highlightActive;
private int highlightFrameCounter;
private const int HighlightCycleSteps = 400; // full rainbow cycle length
```

- [ ] **Step 3: 在图层列表中添加高亮按钮**

在 `MainWindow.cs` 图层列表循环中（第 346 行 `ImGui.SameLine();` 之前），添加高亮按钮：

```csharp
// After visibility button, before SameLine to Selectable
// Check if this layer has emissive enabled and the system is initialized
if (layer.AffectsEmissive && previewService.CanSwapInPlace)
{
    ImGui.SameLine();
    ImGuiComponents.IconButton(200 + li, FontAwesomeIcon.Lightbulb);
    if (ImGui.IsItemHovered())
    {
        ImGui.SetTooltip("高亮显示此贴花的发光区域");
        highlightActive = true;
        highlightFrameCounter++;
        var hue = (highlightFrameCounter % HighlightCycleSteps) / (float)HighlightCycleSteps;
        var rainbowColor = TextureSwapService.HsvToRgb(hue, 1f, 1f);
        HighlightEmissive(rainbowColor);
    }
    else if (highlightActive)
    {
        highlightActive = false;
        highlightFrameCounter = 0;
        // Restore original emissive color by triggering a normal preview update
        MarkPreviewDirty();
    }
}
```

- [ ] **Step 4: 添加 HighlightEmissive 方法**

在 `MainWindow.cs` 的 `MarkPreviewDirty()` 方法之后添加：

```csharp
private unsafe void HighlightEmissive(Vector3 color)
{
    var group = project.SelectedGroup;
    if (group == null || string.IsNullOrEmpty(group.MtrlGamePath)) return;

    var charBase = previewService.GetCharacterBase();
    if (charBase == null) return;

    previewService.HighlightEmissiveColor(charBase, group, color);
}
```

- [ ] **Step 5: 在 PreviewService 暴露 GetCharacterBase 和 HighlightEmissiveColor**

在 `PreviewService.cs` 添加两个公共方法（在 `ApplyPendingSwaps` 之后）：

```csharp
/// <summary>Get local player CharacterBase pointer for direct manipulation.</summary>
public unsafe FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* GetCharacterBase()
    => textureSwap?.GetLocalPlayerCharacterBase();

/// <summary>Write highlight emissive color directly to CBuffer (no recomposite needed).</summary>
public unsafe void HighlightEmissiveColor(
    FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* charBase,
    TargetGroup group, Vector3 color)
{
    if (textureSwap == null) return;
    if (!emissiveOffsets.TryGetValue(group.MtrlGamePath ?? "", out var offset) || offset <= 0) return;

    var diskPath = previewDiskPaths.GetValueOrDefault(group.DiffuseGamePath ?? "");
    textureSwap.UpdateEmissiveColor(charBase, group.DiffuseGamePath!, diskPath, color, offset);
}
```

- [ ] **Step 6: 构建验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 7: 提交**

```bash
git add SkinTatoo/SkinTatoo/Gui/MainWindow.cs SkinTatoo/SkinTatoo/Interop/TextureSwapService.cs SkinTatoo/SkinTatoo/Services/PreviewService.cs
git commit -m "feat: 实时发光高亮按钮, 参考Glamourer HSV色环实现"
```

---

### Task 4: 插件初始化时自动加载模型

**问题分析：**
当前每次打开插件都需要手动点击"加载模型"按钮。模型加载是从配置恢复项目后才能进行的操作。我们在 `Plugin.cs` 初始化末尾、窗口创建后自动加载，同时保留手动按钮作为重新加载入口。

**注意：** 需要在 `IFramework.Update` 中延迟执行，因为插件构造函数执行时游戏可能还没完全准备好（LocalPlayer 为 null）。

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Plugin.cs`
- Modify: `SkinTatoo/SkinTatoo/Gui/MainWindow.cs:942-946`

- [ ] **Step 1: 在 Plugin.cs 添加延迟自动加载逻辑**

在 `Plugin.cs` 添加 `IFramework` 依赖和自动加载：

首先在构造函数参数添加 `IFramework framework`，添加字段：

```csharp
// 在字段区域添加:
private readonly IFramework framework;
private bool autoLoadAttempted;
```

在构造函数中保存引用并注册 Update 事件（在 log.Information 之前）：

```csharp
this.framework = framework;
framework.Update += OnFrameworkUpdate;
```

添加 OnFrameworkUpdate 方法：

```csharp
private void OnFrameworkUpdate(IFramework _)
{
    if (autoLoadAttempted) return;
    if (ObjectTable.LocalPlayer == null) return;

    autoLoadAttempted = true;

    // Auto-load mesh for each group that has a saved mesh path
    foreach (var group in project.Groups)
    {
        if (!string.IsNullOrEmpty(group.MeshDiskPath) && previewService.CurrentMesh == null)
        {
            previewService.LoadMesh(group.MeshDiskPath);
            break; // only load first group's mesh (single mesh at a time)
        }
    }
}
```

在 `Dispose()` 中注销（在 `CommandManager.RemoveHandler` 之前）：

```csharp
framework.Update -= OnFrameworkUpdate;
```

- [ ] **Step 2: 修改 MainWindow 的"加载模型"按钮文字**

在 `MainWindow.cs` 第 942-946 行，将条件从"模型未加载"改为始终显示按钮但文字不同：

```csharp
// Before:
if (group != null && !string.IsNullOrEmpty(group.MeshDiskPath) && previewService.CurrentMesh == null)
{
    if (ImGui.Button("加载模型", new Vector2(-1, 24)))
        previewService.LoadMesh(group.MeshDiskPath);
}

// After:
if (group != null && !string.IsNullOrEmpty(group.MeshDiskPath))
{
    var meshLabel = previewService.CurrentMesh == null ? "加载模型" : "重新加载模型";
    if (ImGui.Button(meshLabel, new Vector2(-1, 24)))
        previewService.LoadMesh(group.MeshDiskPath);
}
```

- [ ] **Step 3: 构建验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 4: 提交**

```bash
git add SkinTatoo/SkinTatoo/Plugin.cs SkinTatoo/SkinTatoo/Gui/MainWindow.cs
git commit -m "feat: 插件启动后自动加载模型, 保留手动重载按钮"
```

---

### Task 5: 增强发光遮罩模式 + 羽化参数 + 彩色贴花预览

**需求分析：**
1. 新增遮罩模式：径向渐变(可设角度/大小)、高斯羽化
2. 现有遮罩预览是黑白的 — 需要添加彩色贴花预览（显示实际发光颜色叠加效果）
3. 现有 falloff 参数重命名为"羽化程度"使其更接近 PS 术语

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Core/DecalLayer.cs`
- Modify: `SkinTatoo/SkinTatoo/Services/PreviewService.cs`
- Modify: `SkinTatoo/SkinTatoo/Gui/MainWindow.cs`

- [ ] **Step 1: 在 DecalLayer.cs 新增枚举值和属性**

```csharp
// 修改 EmissiveMask 枚举:
public enum EmissiveMask
{
    Uniform,           // flat glow across entire decal
    RadialFadeOut,     // center bright → edge dim
    RadialFadeIn,      // edge bright → center dim (ring glow)
    EdgeGlow,          // only edges glow, interior dark
    DirectionalGradient, // linear gradient at configurable angle
    GaussianFeather,   // gaussian blur-style soft falloff from decal edges
}

// 在 DecalLayer 类添加新属性 (EmissiveMaskFalloff 之后):
public float GradientAngleDeg { get; set; } = 0f;    // angle for DirectionalGradient (degrees)
public float GradientScale { get; set; } = 1f;        // size/spread for DirectionalGradient
```

- [ ] **Step 2: 更新 EmissiveMaskNames 数组**

在 `MainWindow.cs` 第 57 行更新：

```csharp
// Before:
private static readonly string[] EmissiveMaskNames = ["均匀", "中心扩散", "边缘光环", "边缘描边"];

// After:
private static readonly string[] EmissiveMaskNames = ["均匀", "中心扩散", "边缘光环", "边缘描边", "方向渐变", "高斯羽化"];
```

- [ ] **Step 3: 实现新遮罩模式的计算**

在 `PreviewService.cs` 的 `ComputeEmissiveMask` 方法（第 782-813 行），在 `EdgeGlow` case 之后添加新 case：

```csharp
case Core.EmissiveMask.DirectionalGradient:
    // gradientAngle and gradientScale are passed via falloff encoding:
    // We use a separate overload — see Step 4
    m = 1f;
    break;
case Core.EmissiveMask.GaussianFeather:
    // Gaussian falloff from edges
    float sigma = MathF.Max(f, 0.01f) * 0.5f;
    float gEdge = MathF.Min(0.5f - MathF.Abs(ru), 0.5f - MathF.Abs(rv));
    gEdge = MathF.Max(gEdge, 0f);
    m = 1f - MathF.Exp(-(gEdge * gEdge) / (2f * sigma * sigma));
    break;
```

- [ ] **Step 4: 添加方向渐变的扩展计算方法**

在 `PreviewService.cs` 的 `ComputeEmissiveMask` 方法之后添加：

```csharp
/// <summary>
/// Compute directional gradient mask with angle and scale parameters.
/// </summary>
public static float ComputeDirectionalGradient(float ru, float rv, float da,
    float angleDeg, float scale, float falloff)
{
    float rad = angleDeg * (MathF.PI / 180f);
    float cosA = MathF.Cos(rad);
    float sinA = MathF.Sin(rad);

    // Project position onto gradient direction
    float projected = ru * cosA + rv * sinA;
    // Normalize to 0-1 range based on scale
    float s = MathF.Max(scale, 0.01f);
    float t = (projected / s + 0.5f);
    t = MathF.Max(0f, MathF.Min(1f, t));

    float f = MathF.Max(falloff, 0.01f);
    float m = Smoothstep(0.5f - f * 0.5f, 0.5f + f * 0.5f, t);

    return da * m;
}
```

- [ ] **Step 5: 更新 CompositeEmissiveNorm 中的遮罩调用**

在 `PreviewService.cs` 的 `CompositeEmissiveNorm` 方法中，找到调用 `ComputeEmissiveMask` 的地方，添加对 `DirectionalGradient` 的特殊处理：

```csharp
// 在 CompositeEmissiveNorm 内的遮罩计算处，替换单行调用为:
float emMask;
if (layer.EmissiveMask == EmissiveMask.DirectionalGradient)
    emMask = ComputeDirectionalGradient(ru, rv, da,
        layer.GradientAngleDeg, layer.GradientScale, layer.EmissiveMaskFalloff);
else
    emMask = ComputeEmissiveMask(layer.EmissiveMask, layer.EmissiveMaskFalloff, ru, rv, da);
```

- [ ] **Step 6: 在 MainWindow 发光设置区域添加方向渐变参数 UI**

在 `MainWindow.cs` 的发光遮罩 falloff 控件之后（约第 892 行），添加方向渐变专用参数：

```csharp
if (layer.EmissiveMask == EmissiveMask.DirectionalGradient)
{
    ImGui.SetNextItemWidth(-1);
    var gAngle = layer.GradientAngleDeg;
    if (ImGui.DragFloat("##gradAngle", ref gAngle, 1f, -180f, 180f, "角度 %.1f°"))
    { layer.GradientAngleDeg = gAngle; MarkPreviewDirty(); }
    if (ScrollAdjust(ref gAngle, 1f, -180f, 180f))
    { layer.GradientAngleDeg = gAngle; MarkPreviewDirty(); }
    if (ImGui.IsItemHovered()) ImGui.SetTooltip("渐变方向角度");

    ImGui.SetNextItemWidth(-1);
    var gScale = layer.GradientScale;
    if (ImGui.DragFloat("##gradScale", ref gScale, 0.01f, 0.1f, 2f, "范围 %.2f"))
    { layer.GradientScale = gScale; MarkPreviewDirty(); }
    if (ScrollAdjust(ref gScale, 0.05f, 0.1f, 2f))
    { layer.GradientScale = gScale; MarkPreviewDirty(); }
    if (ImGui.IsItemHovered()) ImGui.SetTooltip("渐变扩散范围");
}
```

- [ ] **Step 7: 更新 falloff tooltip 为"羽化程度"**

在 `MainWindow.cs` 第 891 行：

```csharp
// Before:
if (ImGui.IsItemHovered()) ImGui.SetTooltip("渐变范围");

// After:
if (ImGui.IsItemHovered()) ImGui.SetTooltip("羽化程度 (0=锐利, 1=柔和)");
```

- [ ] **Step 8: 在遮罩预览旁添加彩色贴花预览**

修改 `DrawEmissiveMaskPreview` 方法，在黑白预览旁添加彩色预览：

```csharp
private void DrawEmissiveMaskPreview(EmissiveMask mask, float falloff, DecalLayer layer)
{
    ImGui.TextDisabled("遮罩预览:");
    var previewSize = 100f;
    var pos = ImGui.GetCursorScreenPos();
    var drawList = ImGui.GetWindowDrawList();

    var cellCount = 32;
    var cellSize = previewSize / cellCount;

    // Black-white mask preview
    for (int y = 0; y < cellCount; y++)
    {
        for (int x = 0; x < cellCount; x++)
        {
            float ru = (x + 0.5f) / cellCount - 0.5f;
            float rv = (y + 0.5f) / cellCount - 0.5f;
            float val;
            if (mask == EmissiveMask.DirectionalGradient)
                val = PreviewService.ComputeDirectionalGradient(ru, rv, 1f,
                    layer.GradientAngleDeg, layer.GradientScale, falloff);
            else
                val = PreviewService.ComputeEmissiveMask(mask, falloff, ru, rv, 1f);

            var color = ImGui.GetColorU32(new Vector4(val, val, val, 1f));
            var p0 = pos + new Vector2(x * cellSize, y * cellSize);
            var p1 = p0 + new Vector2(cellSize + 0.5f, cellSize + 0.5f);
            drawList.AddRectFilled(p0, p1, color);
        }
    }

    drawList.AddRect(pos, pos + new Vector2(previewSize),
        ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));

    // Color preview (showing actual emissive color applied through mask)
    var colorPos = pos + new Vector2(previewSize + 8, 0);
    var emColor = layer.EmissiveColor * layer.EmissiveIntensity;
    for (int y = 0; y < cellCount; y++)
    {
        for (int x = 0; x < cellCount; x++)
        {
            float ru = (x + 0.5f) / cellCount - 0.5f;
            float rv = (y + 0.5f) / cellCount - 0.5f;
            float val;
            if (mask == EmissiveMask.DirectionalGradient)
                val = PreviewService.ComputeDirectionalGradient(ru, rv, 1f,
                    layer.GradientAngleDeg, layer.GradientScale, falloff);
            else
                val = PreviewService.ComputeEmissiveMask(mask, falloff, ru, rv, 1f);

            var cr = Math.Min(emColor.X * val, 1f);
            var cg = Math.Min(emColor.Y * val, 1f);
            var cb = Math.Min(emColor.Z * val, 1f);
            var color = ImGui.GetColorU32(new Vector4(cr, cg, cb, 1f));
            var p0 = colorPos + new Vector2(x * cellSize, y * cellSize);
            var p1 = p0 + new Vector2(cellSize + 0.5f, cellSize + 0.5f);
            drawList.AddRectFilled(p0, p1, color);
        }
    }

    drawList.AddRect(colorPos, colorPos + new Vector2(previewSize),
        ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f)));
    ImGui.Dummy(new Vector2(previewSize * 2 + 8, previewSize + 4));

    // Labels
    var textY = pos.Y + previewSize + 2;
    drawList.AddText(new Vector2(pos.X, textY),
        ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), "遮罩");
    drawList.AddText(new Vector2(colorPos.X, textY),
        ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), "效果");
}
```

- [ ] **Step 9: 更新 DrawEmissiveMaskPreview 调用签名**

在 `MainWindow.cs` 第 895 行，更新调用以传入整个 layer：

```csharp
// Before:
DrawEmissiveMaskPreview(layer.EmissiveMask, layer.EmissiveMaskFalloff);

// After:
DrawEmissiveMaskPreview(layer.EmissiveMask, layer.EmissiveMaskFalloff, layer);
```

- [ ] **Step 10: 更新 LayerSnapshot 以包含新属性**

在 `PreviewService.cs` 的 `LayerSnapshot` 类中添加：

```csharp
// 在字段区域添加:
public float GradientAngleDeg;
public float GradientScale;

// 在构造函数添加:
GradientAngleDeg = l.GradientAngleDeg; GradientScale = l.GradientScale;

// 在 ToDecalLayer 添加:
GradientAngleDeg = GradientAngleDeg, GradientScale = GradientScale,
```

- [ ] **Step 11: 更新 Configuration 序列化**

检查 `Configuration.cs` 和 `DecalProject.cs` 中图层序列化是否包含新属性。由于 DecalLayer 的属性通过反射序列化，新的 `GradientAngleDeg` 和 `GradientScale` 应该自动被序列化/反序列化。但需要确认 `DecalProject.SaveToConfig`/`LoadFromConfig` 使用的序列化方式。

- [ ] **Step 12: 构建验证**

Run: `cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release`
Expected: Build succeeded

- [ ] **Step 13: 提交**

```bash
git add SkinTatoo/SkinTatoo/Core/DecalLayer.cs SkinTatoo/SkinTatoo/Services/PreviewService.cs SkinTatoo/SkinTatoo/Gui/MainWindow.cs
git commit -m "feat: 新增方向渐变/高斯羽化遮罩模式, 彩色预览, 羽化参数"
```

---

## 实施顺序

Task 1 → Task 2 → Task 3 → Task 4 → Task 5

Task 1-2 是 bug 修复，优先级最高。Task 3 依赖对 CBuffer 机制的理解。Task 4 独立。Task 5 是增强功能，最后实施。

## 注意事项

- 所有 `unsafe` 指针访问必须通过 `CanRead` / `IsBadReadPtr` 验证
- GPU swap 首次需要 Full Redraw 初始化（写入磁盘文件 + Penumbra 重定向 + 角色重绘），之后才能走无闪烁路径
- `emissiveOffsets` 字典仅在 `UpdatePreviewFull` 执行 `TryBuildEmissiveMtrl` 时填充 — 高亮按钮依赖此 offset 存在
- 彩色预览需要额外的 ImGui 绘制空间，确保窗口约束允许
