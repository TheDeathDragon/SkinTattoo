# SkinTatoo Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Dalamud plugin that projects PNG decals onto FFXIV character skin textures in real-time, with an embedded HTTP debug server.

**Architecture:** Migrate proven GPU pipeline from Sigillum (compute shader projection → compositing → dilation), replace hand-written Penumbra IPC with type-safe Penumbra.Api submodule, add EmbedIO HTTP server for external debugging. Plugin runs a standalone D3D11 device for GPU work, writes temporary .tex files, and uses Penumbra temporary mods for live preview.

**Tech Stack:** C# / .NET 10, Dalamud SDK 14.0.2, Penumbra.Api (git submodule), Lumina, TerraFX.Interop.Windows (DX11), StbImageSharp, EmbedIO, HLSL compute shaders.

**Note on testing:** This is a Dalamud plugin that runs inside the FFXIV game process — no unit test runner is available. Verification is done through: (1) successful compilation, (2) plugin loads in-game without crash, (3) HTTP debug API returns expected responses. The HTTP server IS the test harness.

---

## File Map

| File | Responsibility | Source |
|------|---------------|--------|
| `SkinTatoo.sln` | Solution file | New |
| `SkinTatoo/SkinTatoo.csproj` | Project config + dependencies | New |
| `SkinTatoo/SkinTatoo.json` | Plugin metadata | New |
| `SkinTatoo/Plugin.cs` | Entry point, wires all services | New |
| `SkinTatoo/Configuration.cs` | Persisted settings | New |
| `SkinTatoo/Core/DecalLayer.cs` | Single decal parameters | Migrate from Sigillum |
| `SkinTatoo/Core/DecalProject.cs` | Layer collection, skin target only | Migrate + simplify |
| `SkinTatoo/Gpu/DxManager.cs` | Standalone D3D11 device | Migrate from Sigillum |
| `SkinTatoo/Gpu/ComputeShaderPipeline.cs` | 3-pass shader dispatch | Migrate from Sigillum |
| `SkinTatoo/Gpu/StagingReadback.cs` | GPU→CPU readback | Migrate from Sigillum |
| `SkinTatoo/Gpu/Shaders/ProjectionPass.hlsl` | Decal projection shader | Copy from Sigillum |
| `SkinTatoo/Gpu/Shaders/CompositePass.hlsl` | Blend + RNM normal mixing | Copy from Sigillum |
| `SkinTatoo/Gpu/Shaders/DilationPass.hlsl` | UV seam fill | Copy from Sigillum |
| `SkinTatoo/Mesh/MeshData.cs` | Vertex struct + mesh container | Migrate from Sigillum |
| `SkinTatoo/Mesh/MeshExtractor.cs` | Lumina .mdl parser | Migrate from Sigillum |
| `SkinTatoo/Mesh/PositionMapGenerator.cs` | UV-space position/normal maps | Migrate from Sigillum |
| `SkinTatoo/Interop/PenumbraBridge.cs` | Penumbra.Api IPC wrapper | New (replaces hand-written IPC) |
| `SkinTatoo/Interop/BodyModDetector.cs` | Detect body mod type | Migrate from Sigillum |
| `SkinTatoo/Services/TexFileWriter.cs` | Write .tex files | Migrate from Sigillum |
| `SkinTatoo/Services/DecalImageLoader.cs` | Load PNG/JPG/TGA/DDS | Migrate from Sigillum |
| `SkinTatoo/Services/PreviewService.cs` | Orchestrate full pipeline | Rewrite from Sigillum PreviewManager |
| `SkinTatoo/Http/DebugServer.cs` | EmbedIO HTTP server + routes | New |
| `SkinTatoo/Gui/MainWindow.cs` | Layer list + parameter panel | Rewrite (simplified) |
| `SkinTatoo/Gui/ConfigWindow.cs` | Settings UI | New |

---

### Task 1: Project Scaffolding

**Files:**
- Create: `SkinTatoo/SkinTatoo.sln`
- Create: `SkinTatoo/SkinTatoo/SkinTatoo.csproj`
- Create: `SkinTatoo/SkinTatoo/SkinTatoo.json`
- Create: `SkinTatoo/.gitignore`

- [ ] **Step 1: Initialize git repo**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo
git init
```

- [ ] **Step 2: Create .gitignore**

Create `SkinTatoo/.gitignore`:

```
bin/
obj/
.vs/
*.user
*.suo
packages/
```

- [ ] **Step 3: Add Penumbra.Api submodule**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo
git submodule add -b cn-temp https://github.com/MeowZWR/Penumbra.Api.git Penumbra.Api
```

- [ ] **Step 4: Create SkinTatoo.csproj**

Create `SkinTatoo/SkinTatoo/SkinTatoo.csproj`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project Sdk="Dalamud.NET.Sdk/14.0.2">
  <PropertyGroup>
    <Version>0.1.0.0</Version>
    <PackageLicenseExpression>AGPL-3.0-or-later</PackageLicenseExpression>
    <IsPackable>false</IsPackable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Lumina" Version="4.*" />
    <PackageReference Include="StbImageSharp" Version="2.*" />
    <PackageReference Include="TerraFX.Interop.Windows" Version="10.*" />
    <PackageReference Include="EmbedIO" Version="3.5.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Penumbra.Api\Penumbra.Api.csproj" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="Gpu\Shaders\*.hlsl" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Create SkinTatoo.json**

Create `SkinTatoo/SkinTatoo/SkinTatoo.json`:

```json
{
  "Author": "Shiro",
  "Name": "SkinTatoo",
  "Punchline": "实时皮肤贴花投影与预览",
  "Description": "将 PNG 贴花投影到角色皮肤上，支持漫反射和法线通道，通过 Penumbra 实时预览。使用 /skintatoo 打开。",
  "ApplicableVersion": "any",
  "Tags": [
    "texture",
    "decal",
    "tattoo",
    "skin",
    "penumbra"
  ]
}
```

- [ ] **Step 6: Create solution file**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo
dotnet new sln -n SkinTatoo
dotnet sln SkinTatoo.sln add SkinTatoo/SkinTatoo.csproj
dotnet sln SkinTatoo.sln add Penumbra.Api/Penumbra.Api.csproj
```

- [ ] **Step 7: Create minimal Plugin.cs stub for build test**

Create `SkinTatoo/SkinTatoo/Plugin.cs`:

```csharp
using Dalamud.Plugin;

namespace SkinTatoo;

public sealed class Plugin : IDalamudPlugin
{
    public Plugin() { }
    public void Dispose() { }
}
```

- [ ] **Step 8: Verify build**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo
dotnet build -c Release
```

Expected: Build succeeded. 0 Error(s).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "feat: project scaffolding with Penumbra.Api submodule"
```

---

### Task 2: Core Data Models

**Files:**
- Create: `SkinTatoo/SkinTatoo/Core/DecalLayer.cs`
- Create: `SkinTatoo/SkinTatoo/Core/DecalProject.cs`
- Create: `SkinTatoo/SkinTatoo/Configuration.cs`

- [ ] **Step 1: Create DecalLayer.cs**

Create `SkinTatoo/SkinTatoo/Core/DecalLayer.cs`:

```csharp
using System;
using System.Numerics;

namespace SkinTatoo.Core;

public enum BlendMode
{
    Normal,
    Multiply,
    Overlay,
    SoftLight,
}

public class DecalLayer
{
    public string Name { get; set; } = "New Decal";
    public string? ImagePath { get; set; }
    public Vector3 Position { get; set; } = Vector3.Zero;
    public Vector3 Rotation { get; set; } = Vector3.Zero;
    public Vector2 Scale { get; set; } = Vector2.One;
    public float Depth { get; set; } = 0.5f;
    public float Opacity { get; set; } = 1.0f;
    public BlendMode BlendMode { get; set; } = BlendMode.Normal;
    public float BackfaceCullingThreshold { get; set; } = 0.1f;
    public float GrazingAngleFade { get; set; } = 0.3f;
    public bool IsVisible { get; set; } = true;
    public bool AffectsDiffuse { get; set; } = true;
    public bool AffectsNormal { get; set; } = false;

    public Matrix4x4 GetProjectionMatrix()
    {
        var view = Matrix4x4.CreateLookAt(
            Position,
            Position + GetForwardDirection(),
            Vector3.UnitY);
        var proj = Matrix4x4.CreateOrthographic(Scale.X, Scale.Y, 0, Depth);
        return view * proj;
    }

    public Vector3 GetForwardDirection()
    {
        var pitch = Rotation.X * (MathF.PI / 180f);
        var yaw = Rotation.Y * (MathF.PI / 180f);
        return new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw));
    }
}
```

- [ ] **Step 2: Create DecalProject.cs**

Create `SkinTatoo/SkinTatoo/Core/DecalProject.cs`:

```csharp
using System.Collections.Generic;

namespace SkinTatoo.Core;

public enum SkinTarget
{
    Body,
    Face,
}

public class DecalProject
{
    public List<DecalLayer> Layers { get; } = [];
    public SkinTarget Target { get; set; } = SkinTarget.Body;
    public int SelectedLayerIndex { get; set; } = -1;

    public DecalLayer? SelectedLayer =>
        SelectedLayerIndex >= 0 && SelectedLayerIndex < Layers.Count
            ? Layers[SelectedLayerIndex]
            : null;

    public DecalLayer AddLayer(string name = "New Decal")
    {
        var layer = new DecalLayer { Name = name };
        Layers.Add(layer);
        SelectedLayerIndex = Layers.Count - 1;
        return layer;
    }

    public void RemoveLayer(int index)
    {
        if (index < 0 || index >= Layers.Count) return;
        Layers.RemoveAt(index);
        if (SelectedLayerIndex >= Layers.Count)
            SelectedLayerIndex = Layers.Count - 1;
    }
}
```

- [ ] **Step 3: Create Configuration.cs**

Create `SkinTatoo/SkinTatoo/Configuration.cs`:

```csharp
using System;
using Dalamud.Configuration;

namespace SkinTatoo;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public int HttpPort { get; set; } = 14780;
    public int TextureResolution { get; set; } = 1024;
    public SkinTatoo.Core.SkinTarget LastTarget { get; set; } = SkinTatoo.Core.SkinTarget.Body;

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface?.SavePluginConfig(this);
}
```

Note: `IDalamudPluginInterface` is in `Dalamud.Plugin` namespace. Add `using Dalamud.Plugin;` at the top of the file.

- [ ] **Step 4: Verify build**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release
```

- [ ] **Step 5: Commit**

```bash
git add SkinTatoo/SkinTatoo/Core/ SkinTatoo/SkinTatoo/Configuration.cs
git commit -m "feat: core data models (DecalLayer, DecalProject, Configuration)"
```

---

### Task 3: GPU Pipeline (migrate from Sigillum)

**Files:**
- Create: `SkinTatoo/SkinTatoo/Gpu/DxManager.cs`
- Create: `SkinTatoo/SkinTatoo/Gpu/ComputeShaderPipeline.cs`
- Create: `SkinTatoo/SkinTatoo/Gpu/StagingReadback.cs`
- Create: `SkinTatoo/SkinTatoo/Gpu/Shaders/ProjectionPass.hlsl`
- Create: `SkinTatoo/SkinTatoo/Gpu/Shaders/CompositePass.hlsl`
- Create: `SkinTatoo/SkinTatoo/Gpu/Shaders/DilationPass.hlsl`

- [ ] **Step 1: Copy HLSL shaders from Sigillum**

Copy the three shader files verbatim — they have no namespace dependencies:

```bash
mkdir -p /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo/SkinTatoo/Gpu/Shaders
cp /c/Users/Shiro/Desktop/FF14Plugins/Sigillum/Sigillum/Gpu/Shaders/ProjectionPass.hlsl \
   /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo/SkinTatoo/Gpu/Shaders/
cp /c/Users/Shiro/Desktop/FF14Plugins/Sigillum/Sigillum/Gpu/Shaders/CompositePass.hlsl \
   /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo/SkinTatoo/Gpu/Shaders/
cp /c/Users/Shiro/Desktop/FF14Plugins/Sigillum/Sigillum/Gpu/Shaders/DilationPass.hlsl \
   /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo/SkinTatoo/Gpu/Shaders/
```

- [ ] **Step 2: Create DxManager.cs**

Copy from `Sigillum/Sigillum/Gpu/DxManager.cs` and change namespace from `Sigillum.Gpu` to `SkinTatoo.Gpu`. No other changes needed — the entire file is self-contained D3D11 device management.

- [ ] **Step 3: Create ComputeShaderPipeline.cs**

Copy from `Sigillum/Sigillum/Gpu/ComputeShaderPipeline.cs` with two changes:
1. Namespace: `Sigillum.Gpu` → `SkinTatoo.Gpu`
2. Embedded resource prefix in `LoadShaderBytecode`: `"Sigillum.Gpu.Shaders.{name}"` → `"SkinTatoo.Gpu.Shaders.{name}"`

- [ ] **Step 4: Create StagingReadback.cs**

Copy from `Sigillum/Sigillum/Gpu/StagingReadback.cs`, change namespace `Sigillum.Gpu` → `SkinTatoo.Gpu`.

- [ ] **Step 5: Verify build**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release
```

- [ ] **Step 6: Commit**

```bash
git add SkinTatoo/SkinTatoo/Gpu/
git commit -m "feat: GPU pipeline (DxManager, ComputeShaderPipeline, 3 HLSL shaders)"
```

---

### Task 4: Mesh Extraction

**Files:**
- Create: `SkinTatoo/SkinTatoo/Mesh/MeshData.cs`
- Create: `SkinTatoo/SkinTatoo/Mesh/MeshExtractor.cs`
- Create: `SkinTatoo/SkinTatoo/Mesh/PositionMapGenerator.cs`

- [ ] **Step 1: Create MeshData.cs**

Copy from `Sigillum/Sigillum/Mesh/MeshData.cs`, change namespace `Sigillum.Mesh` → `SkinTatoo.Mesh`.

- [ ] **Step 2: Create MeshExtractor.cs**

Copy from `Sigillum/Sigillum/Mesh/MeshExtractor.cs`, change namespace `Sigillum.Mesh` → `SkinTatoo.Mesh`.

- [ ] **Step 3: Create PositionMapGenerator.cs**

Copy from `Sigillum/Sigillum/Mesh/PositionMapGenerator.cs`, change namespace `Sigillum.Mesh` → `SkinTatoo.Mesh`.

- [ ] **Step 4: Verify build**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release
```

- [ ] **Step 5: Commit**

```bash
git add SkinTatoo/SkinTatoo/Mesh/
git commit -m "feat: mesh extraction (MeshData, MeshExtractor, PositionMapGenerator)"
```

---

### Task 5: Services (TexFileWriter + DecalImageLoader)

**Files:**
- Create: `SkinTatoo/SkinTatoo/Services/TexFileWriter.cs`
- Create: `SkinTatoo/SkinTatoo/Services/DecalImageLoader.cs`

- [ ] **Step 1: Create TexFileWriter.cs**

Copy from `Sigillum/Sigillum/Preview/TexFileWriter.cs`, change namespace `Sigillum.Preview` → `SkinTatoo.Services`.

- [ ] **Step 2: Create DecalImageLoader.cs**

Copy from `Sigillum/Sigillum/Library/DecalImporter.cs` with changes:
1. Namespace: `Sigillum.Library` → `SkinTatoo.Services`
2. Class name: `DecalImporter` → `DecalImageLoader`

- [ ] **Step 3: Verify build**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release
```

- [ ] **Step 4: Commit**

```bash
git add SkinTatoo/SkinTatoo/Services/
git commit -m "feat: services (TexFileWriter, DecalImageLoader)"
```

---

### Task 6: Penumbra Integration

**Files:**
- Create: `SkinTatoo/SkinTatoo/Interop/PenumbraBridge.cs`
- Create: `SkinTatoo/SkinTatoo/Interop/BodyModDetector.cs`

- [ ] **Step 1: Create PenumbraBridge.cs**

Create `SkinTatoo/SkinTatoo/Interop/PenumbraBridge.cs`:

```csharp
using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.IpcSubscribers;

namespace SkinTatoo.Interop;

public class PenumbraBridge : IDisposable
{
    private readonly IPluginLog log;

    private readonly ApiVersion apiVersion;
    private readonly CreateTemporaryCollection createTempCollection;
    private readonly DeleteTemporaryCollection deleteTempCollection;
    private readonly AddTemporaryMod addTempMod;
    private readonly RemoveTemporaryMod removeTempMod;
    private readonly RedrawObject redrawObject;
    private readonly ResolvePlayerPath resolvePlayerPath;

    private Guid collectionId = Guid.Empty;
    private const string Identity = "SkinTatoo";
    private const string CollectionName = "SkinTatoo Preview";
    private const string TempModTag = "SkinTatooDecal";

    public bool IsAvailable { get; private set; }

    public PenumbraBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        apiVersion = new ApiVersion(pluginInterface);
        createTempCollection = new CreateTemporaryCollection(pluginInterface);
        deleteTempCollection = new DeleteTemporaryCollection(pluginInterface);
        addTempMod = new AddTemporaryMod(pluginInterface);
        removeTempMod = new RemoveTemporaryMod(pluginInterface);
        redrawObject = new RedrawObject(pluginInterface);
        resolvePlayerPath = new ResolvePlayerPath(pluginInterface);

        CheckAvailability();
    }

    private void CheckAvailability()
    {
        try
        {
            var version = apiVersion.Invoke();
            IsAvailable = true;
            log.Information("Penumbra IPC available (v{0}.{1}).", version.Breaking, version.Features);
        }
        catch
        {
            IsAvailable = false;
            log.Warning("Penumbra IPC not available.");
        }
    }

    public bool EnsureCollection()
    {
        if (!IsAvailable) return false;
        if (collectionId != Guid.Empty) return true;

        try
        {
            var ret = createTempCollection.Invoke(Identity, CollectionName);
            collectionId = ret.Item2;
            log.Information("Created Penumbra temp collection: {0}", collectionId);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to create temp collection");
            return false;
        }
    }

    public bool SetTextureRedirect(string gameTexturePath, string localFilePath)
    {
        if (!EnsureCollection()) return false;

        try
        {
            var paths = new Dictionary<string, string> { { gameTexturePath, localFilePath } };
            addTempMod.Invoke(TempModTag, collectionId, paths, string.Empty, 99);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to set texture redirect");
            return false;
        }
    }

    public void RedrawPlayer()
    {
        if (!IsAvailable) return;
        try { redrawObject.Invoke(0); }
        catch (Exception ex) { log.Error(ex, "Failed to redraw player"); }
    }

    public string? ResolvePlayer(string gamePath)
    {
        if (!IsAvailable) return null;
        try
        {
            var resolved = resolvePlayerPath.Invoke(gamePath);
            return string.IsNullOrEmpty(resolved) ? null : resolved;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to resolve player path");
            return null;
        }
    }

    public void Dispose()
    {
        if (collectionId != Guid.Empty)
        {
            try { deleteTempCollection.Invoke(collectionId); }
            catch (Exception ex) { log.Error(ex, "Failed to delete temp collection"); }
            collectionId = Guid.Empty;
        }
    }
}
```

- [ ] **Step 2: Create BodyModDetector.cs**

Create `SkinTatoo/SkinTatoo/Interop/BodyModDetector.cs`:

```csharp
using Dalamud.Plugin.Services;

namespace SkinTatoo.Interop;

public enum BodyModType
{
    Vanilla,
    Gen3,
    BiboPlus,
    TBSE,
    Unknown,
}

public class BodyModDetector
{
    private readonly PenumbraBridge penumbra;
    private readonly IPluginLog log;

    private const string BodyPathTemplate = "chara/human/c{0}/obj/body/b0001/model/c{0}b0001.mdl";

    public BodyModDetector(PenumbraBridge penumbra, IPluginLog log)
    {
        this.penumbra = penumbra;
        this.log = log;
    }

    public (BodyModType Type, string MdlPath) DetectBodyMod(string raceCode)
    {
        var vanillaPath = string.Format(BodyPathTemplate, raceCode);

        if (!penumbra.IsAvailable)
        {
            log.Warning("Penumbra not available, defaulting to Vanilla body.");
            return (BodyModType.Vanilla, vanillaPath);
        }

        var resolvedPath = penumbra.ResolvePlayer(vanillaPath);
        if (resolvedPath == null || resolvedPath == vanillaPath)
            return (BodyModType.Vanilla, vanillaPath);

        var type = ClassifyByPath(resolvedPath);
        log.Information("Body mod: {0} ({1})", type, resolvedPath);
        return (type, resolvedPath);
    }

    private static BodyModType ClassifyByPath(string resolvedPath)
    {
        var lower = resolvedPath.ToLowerInvariant();
        if (lower.Contains("bibo")) return BodyModType.BiboPlus;
        if (lower.Contains("gen3") || lower.Contains("tight") || lower.Contains("firm")) return BodyModType.Gen3;
        if (lower.Contains("tbse") || lower.Contains("thebody")) return BodyModType.TBSE;
        return BodyModType.Unknown;
    }

    public static string GetBodyTexturePath(string raceCode, string textureType)
    {
        return $"chara/human/c{raceCode}/obj/body/b0001/texture/--c{raceCode}b0001{textureType}.tex";
    }
}
```

- [ ] **Step 3: Verify build**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release
```

- [ ] **Step 4: Commit**

```bash
git add SkinTatoo/SkinTatoo/Interop/
git commit -m "feat: Penumbra integration (PenumbraBridge, BodyModDetector)"
```

---

### Task 7: Preview Service

**Files:**
- Create: `SkinTatoo/SkinTatoo/Services/PreviewService.cs`

- [ ] **Step 1: Create PreviewService.cs**

Create `SkinTatoo/SkinTatoo/Services/PreviewService.cs`:

```csharp
using System;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SkinTatoo.Core;
using SkinTatoo.Gpu;
using SkinTatoo.Interop;
using SkinTatoo.Mesh;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.DXGI;

namespace SkinTatoo.Services;

public unsafe class PreviewService : IDisposable
{
    private readonly DxManager dx;
    private readonly ComputeShaderPipeline pipeline;
    private readonly StagingReadback readback;
    private readonly PenumbraBridge penumbra;
    private readonly MeshExtractor meshExtractor;
    private readonly DecalImageLoader imageLoader;
    private readonly IPluginLog log;
    private readonly string tempDir;

    private PositionMapGenerator? positionMapGen;
    private MeshData? currentMesh;
    public MeshData? CurrentMesh => currentMesh;
    public bool IsReady => pipeline.IsInitialized && currentMesh != null;

    // GPU resources
    private ID3D11Texture2D* positionMapTex;
    private ID3D11ShaderResourceView* positionMapSRV;
    private ID3D11Texture2D* normalMapTex;
    private ID3D11ShaderResourceView* normalMapSRV;
    private ID3D11Texture2D* decalBufferTex;
    private ID3D11ShaderResourceView* decalBufferSRV;
    private ID3D11UnorderedAccessView* decalBufferUAV;
    private ID3D11Texture2D* outputTex;
    private ID3D11ShaderResourceView* outputSRV;
    private ID3D11UnorderedAccessView* outputUAV;
    private ID3D11Texture2D* dilationPingTex;
    private ID3D11ShaderResourceView* dilationPingSRV;
    private ID3D11UnorderedAccessView* dilationPingUAV;

    private uint texWidth;
    private uint texHeight;
    private bool disposed;

    public PreviewService(
        DxManager dx, ComputeShaderPipeline pipeline, StagingReadback readback,
        PenumbraBridge penumbra, MeshExtractor meshExtractor, DecalImageLoader imageLoader,
        IDalamudPluginInterface pluginInterface, IPluginLog log, int resolution = 1024)
    {
        this.dx = dx;
        this.pipeline = pipeline;
        this.readback = readback;
        this.penumbra = penumbra;
        this.meshExtractor = meshExtractor;
        this.imageLoader = imageLoader;
        this.log = log;
        this.texWidth = (uint)resolution;
        this.texHeight = (uint)resolution;
        this.tempDir = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "temp");
        Directory.CreateDirectory(tempDir);
    }

    public bool LoadMesh(string gameMdlPath)
    {
        currentMesh = meshExtractor.ExtractMesh(gameMdlPath);
        if (currentMesh == null) return false;

        positionMapGen = new PositionMapGenerator((int)texWidth, (int)texHeight);
        positionMapGen.Generate(currentMesh);
        UploadMapsToGpu();
        log.Information("Mesh loaded: {0} ({1} tris)", gameMdlPath, currentMesh.TriangleCount);
        return true;
    }

    public string? UpdatePreview(DecalProject project, string gameTexturePath)
    {
        if (!IsReady) return null;

        bool isFirstLayer = true;
        foreach (var layer in project.Layers)
        {
            if (!layer.IsVisible || string.IsNullOrEmpty(layer.ImagePath)) continue;

            var imageData = imageLoader.LoadImage(layer.ImagePath);
            if (imageData == null) continue;

            // Upload decal image to GPU
            var decalTex = CreateAndUploadDecalTexture(imageData.Value.Data, imageData.Value.Width, imageData.Value.Height);
            if (decalTex.tex == null) continue;

            var projParams = new ProjectionCB
            {
                ViewProjection = layer.GetProjectionMatrix(),
                ProjectionDir = layer.GetForwardDirection(),
                BackfaceThreshold = layer.BackfaceCullingThreshold,
                GrazingFade = layer.GrazingAngleFade,
                Opacity = layer.Opacity,
            };
            pipeline.DispatchProjection(positionMapSRV, normalMapSRV, decalTex.srv, decalBufferUAV,
                projParams, texWidth, texHeight);

            var compParams = new CompositeCB
            {
                BlendMode = (uint)layer.BlendMode,
                IsNormalMap = layer.AffectsNormal ? 1u : 0u,
            };
            var baseSRV = isFirstLayer ? positionMapSRV : outputSRV;
            pipeline.DispatchComposite(baseSRV, decalBufferSRV, outputUAV, compParams, texWidth, texHeight);

            // Cleanup per-layer decal texture
            decalTex.srv->Release();
            decalTex.tex->Release();
            isFirstLayer = false;
        }

        if (isFirstLayer) return null; // no visible layers

        // Dilation: 8 iterations ping-pong
        for (var i = 0; i < 8; i++)
        {
            var inSRV = (i % 2 == 0) ? outputSRV : dilationPingSRV;
            var outUAV = (i % 2 == 0) ? dilationPingUAV : outputUAV;
            pipeline.DispatchDilation(inSRV, positionMapSRV, outUAV, texWidth, texHeight);
        }

        var finalTex = outputTex; // 8 is even, result in dilationPing, but last write was to outputUAV
        var pixelData = readback.Readback(finalTex, texWidth, texHeight);
        if (pixelData == null) return null;

        var texPath = Path.Combine(tempDir, "preview_d.tex");
        TexFileWriter.WriteUncompressed(texPath, pixelData, (int)texWidth, (int)texHeight);

        penumbra.SetTextureRedirect(gameTexturePath, texPath);
        penumbra.RedrawPlayer();

        log.Debug("Preview updated: {0}", gameTexturePath);
        return texPath;
    }

    private (ID3D11Texture2D* tex, ID3D11ShaderResourceView* srv) CreateAndUploadDecalTexture(
        byte[] rgbaData, int width, int height)
    {
        var fmt = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT;
        var tex = dx.CreateTexture2D((uint)width, (uint)height, fmt,
            (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
        if (tex == null) return (null, null);

        // Convert byte RGBA to float RGBA
        var floatData = new float[width * height * 4];
        for (int i = 0; i < width * height * 4; i++)
            floatData[i] = rgbaData[i] / 255f;

        dx.UploadTextureData(tex, floatData, (uint)width, (uint)height);
        var srv = dx.CreateSRV(tex, fmt);
        return (tex, srv);
    }

    private void UploadMapsToGpu()
    {
        if (positionMapGen == null) return;
        ReleaseGpuResources();

        var fmt = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT;
        var srvBind = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        var srvUavBind = srvBind | (uint)D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS;

        positionMapTex = dx.CreateTexture2D(texWidth, texHeight, fmt, srvBind);
        dx.UploadTextureData(positionMapTex, positionMapGen.PositionMap, texWidth, texHeight);
        positionMapSRV = dx.CreateSRV(positionMapTex, fmt);

        normalMapTex = dx.CreateTexture2D(texWidth, texHeight, fmt, srvBind);
        dx.UploadTextureData(normalMapTex, positionMapGen.NormalMap, texWidth, texHeight);
        normalMapSRV = dx.CreateSRV(normalMapTex, fmt);

        decalBufferTex = dx.CreateTexture2D(texWidth, texHeight, fmt, srvUavBind);
        decalBufferSRV = dx.CreateSRV(decalBufferTex, fmt);
        decalBufferUAV = dx.CreateUAV(decalBufferTex, fmt);

        outputTex = dx.CreateTexture2D(texWidth, texHeight, fmt, srvUavBind);
        outputSRV = dx.CreateSRV(outputTex, fmt);
        outputUAV = dx.CreateUAV(outputTex, fmt);

        dilationPingTex = dx.CreateTexture2D(texWidth, texHeight, fmt, srvUavBind);
        dilationPingSRV = dx.CreateSRV(dilationPingTex, fmt);
        dilationPingUAV = dx.CreateUAV(dilationPingTex, fmt);
    }

    private void ReleaseGpuResources()
    {
        void SafeRelease(ref ID3D11Texture2D* p) { if (p != null) { p->Release(); p = null; } }
        void SafeReleaseSRV(ref ID3D11ShaderResourceView* p) { if (p != null) { p->Release(); p = null; } }
        void SafeReleaseUAV(ref ID3D11UnorderedAccessView* p) { if (p != null) { p->Release(); p = null; } }

        SafeReleaseSRV(ref positionMapSRV); SafeRelease(ref positionMapTex);
        SafeReleaseSRV(ref normalMapSRV); SafeRelease(ref normalMapTex);
        SafeReleaseSRV(ref decalBufferSRV); SafeReleaseUAV(ref decalBufferUAV); SafeRelease(ref decalBufferTex);
        SafeReleaseSRV(ref outputSRV); SafeReleaseUAV(ref outputUAV); SafeRelease(ref outputTex);
        SafeReleaseSRV(ref dilationPingSRV); SafeReleaseUAV(ref dilationPingUAV); SafeRelease(ref dilationPingTex);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseGpuResources();
    }
}
```

- [ ] **Step 2: Verify build**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release
```

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/Services/PreviewService.cs
git commit -m "feat: PreviewService orchestrating full GPU→tex→Penumbra pipeline"
```

---

### Task 8: HTTP Debug Server

**Files:**
- Create: `SkinTatoo/SkinTatoo/Http/DebugServer.cs`

- [ ] **Step 1: Create DebugServer.cs**

Create `SkinTatoo/SkinTatoo/Http/DebugServer.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using SkinTatoo.Core;
using SkinTatoo.Interop;
using SkinTatoo.Services;

namespace SkinTatoo.Http;

public class DebugServer : IDisposable
{
    private readonly IPluginLog log;
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly BodyModDetector bodyModDetector;
    private readonly DecalImageLoader imageLoader;
    private readonly Configuration config;
    private WebServer? server;
    private CancellationTokenSource? cts;

    private static readonly ConcurrentQueue<string> LogBuffer = new();
    private const int MaxLogEntries = 200;

    public DebugServer(
        DecalProject project, PreviewService previewService, PenumbraBridge penumbra,
        BodyModDetector bodyModDetector, DecalImageLoader imageLoader,
        Configuration config, IPluginLog log)
    {
        this.project = project;
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.bodyModDetector = bodyModDetector;
        this.imageLoader = imageLoader;
        this.config = config;
        this.log = log;
    }

    public static void AppendLog(string message)
    {
        LogBuffer.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (LogBuffer.Count > MaxLogEntries)
            LogBuffer.TryDequeue(out _);
    }

    public void Start()
    {
        cts = new CancellationTokenSource();
        var url = $"http://localhost:{config.HttpPort}/";

        server = new WebServer(o => o
                .WithUrlPrefix(url)
                .WithMode(HttpListenerMode.EmbedIO))
            .WithWebApi("/api", m => m.WithController(() =>
                new ApiController(project, previewService, penumbra, bodyModDetector, imageLoader, config)))
            .WithModule(new ActionModule("/", HttpVerbs.Any, ctx =>
            {
                ctx.Response.ContentType = "text/plain";
                return ctx.SendStringAsync("SkinTatoo Debug Server", "text/plain", System.Text.Encoding.UTF8);
            }));

        server.StateChanged += (_, e) => log.Information("HTTP server: {0}", e.NewState);

        _ = server.RunAsync(cts.Token);
        log.Information("Debug server started on {0}", url);
    }

    public void Dispose()
    {
        cts?.Cancel();
        server?.Dispose();
        cts?.Dispose();
    }
}

public class ApiController : WebApiController
{
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly BodyModDetector bodyModDetector;
    private readonly DecalImageLoader imageLoader;
    private readonly Configuration config;

    public ApiController(
        DecalProject project, PreviewService previewService, PenumbraBridge penumbra,
        BodyModDetector bodyModDetector, DecalImageLoader imageLoader, Configuration config)
    {
        this.project = project;
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.bodyModDetector = bodyModDetector;
        this.imageLoader = imageLoader;
        this.config = config;
    }

    [Route(HttpVerbs.Get, "/status")]
    public object GetStatus() => new
    {
        plugin = "SkinTatoo",
        gpuReady = previewService.IsReady,
        penumbraAvailable = penumbra.IsAvailable,
        meshLoaded = previewService.CurrentMesh != null,
        layerCount = project.Layers.Count,
        target = project.Target.ToString(),
        resolution = config.TextureResolution,
    };

    [Route(HttpVerbs.Get, "/project")]
    public object GetProject() => new
    {
        target = project.Target.ToString(),
        selectedLayerIndex = project.SelectedLayerIndex,
        layers = project.Layers.ConvertAll(l => new
        {
            l.Name, l.ImagePath,
            position = new { l.Position.X, l.Position.Y, l.Position.Z },
            rotation = new { l.Rotation.X, l.Rotation.Y, l.Rotation.Z },
            scale = new { l.Scale.X, l.Scale.Y },
            l.Depth, l.Opacity,
            blendMode = l.BlendMode.ToString(),
            l.IsVisible, l.AffectsDiffuse, l.AffectsNormal,
            l.BackfaceCullingThreshold, l.GrazingAngleFade,
        }),
    };

    [Route(HttpVerbs.Post, "/layer")]
    public async Task<object> AddLayer()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        var name = "New Decal";
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("name", out var n))
                    name = n.GetString() ?? name;
            }
            catch { /* use default name */ }
        }
        var layer = project.AddLayer(name);
        return new { ok = true, index = project.Layers.Count - 1, layer.Name };
    }

    [Route(HttpVerbs.Put, "/layer/{id}")]
    public async Task<object> UpdateLayer(int id)
    {
        if (id < 0 || id >= project.Layers.Count)
        {
            Response.StatusCode = 404;
            return new { error = "layer not found" };
        }

        var layer = project.Layers[id];
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        if (root.TryGetProperty("name", out var name)) layer.Name = name.GetString() ?? layer.Name;
        if (root.TryGetProperty("imagePath", out var img)) layer.ImagePath = img.GetString();
        if (root.TryGetProperty("opacity", out var op)) layer.Opacity = op.GetSingle();
        if (root.TryGetProperty("depth", out var dp)) layer.Depth = dp.GetSingle();
        if (root.TryGetProperty("visible", out var vis)) layer.IsVisible = vis.GetBoolean();
        if (root.TryGetProperty("affectsDiffuse", out var ad)) layer.AffectsDiffuse = ad.GetBoolean();
        if (root.TryGetProperty("affectsNormal", out var an)) layer.AffectsNormal = an.GetBoolean();
        if (root.TryGetProperty("blendMode", out var bm))
            if (Enum.TryParse<BlendMode>(bm.GetString(), true, out var mode)) layer.BlendMode = mode;
        if (root.TryGetProperty("position", out var pos))
        {
            layer.Position = new System.Numerics.Vector3(
                pos.TryGetProperty("x", out var px) ? px.GetSingle() : layer.Position.X,
                pos.TryGetProperty("y", out var py) ? py.GetSingle() : layer.Position.Y,
                pos.TryGetProperty("z", out var pz) ? pz.GetSingle() : layer.Position.Z);
        }
        if (root.TryGetProperty("rotation", out var rot))
        {
            layer.Rotation = new System.Numerics.Vector3(
                rot.TryGetProperty("x", out var rx) ? rx.GetSingle() : layer.Rotation.X,
                rot.TryGetProperty("y", out var ry) ? ry.GetSingle() : layer.Rotation.Y,
                rot.TryGetProperty("z", out var rz) ? rz.GetSingle() : layer.Rotation.Z);
        }
        if (root.TryGetProperty("scale", out var scl))
        {
            layer.Scale = new System.Numerics.Vector2(
                scl.TryGetProperty("x", out var sx) ? sx.GetSingle() : layer.Scale.X,
                scl.TryGetProperty("y", out var sy) ? sy.GetSingle() : layer.Scale.Y);
        }

        return new { ok = true };
    }

    [Route(HttpVerbs.Delete, "/layer/{id}")]
    public object DeleteLayer(int id)
    {
        if (id < 0 || id >= project.Layers.Count)
        {
            Response.StatusCode = 404;
            return new { error = "layer not found" };
        }
        project.RemoveLayer(id);
        return new { ok = true, remaining = project.Layers.Count };
    }

    [Route(HttpVerbs.Post, "/preview")]
    public async Task<object> TriggerPreview()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        string? gameTexturePath = null;
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("texturePath", out var tp))
                    gameTexturePath = tp.GetString();
            }
            catch { /* use default */ }
        }
        gameTexturePath ??= BodyModDetector.GetBodyTexturePath("0201", "_d");

        var result = previewService.UpdatePreview(project, gameTexturePath);
        return new { ok = result != null, outputPath = result };
    }

    [Route(HttpVerbs.Post, "/mesh/load")]
    public async Task<object> LoadMesh()
    {
        var body = await HttpContext.GetRequestBodyAsStringAsync();
        string? mdlPath = null;
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("path", out var p))
                    mdlPath = p.GetString();
            }
            catch { /* use default */ }
        }
        mdlPath ??= "chara/human/c0201/obj/body/b0001/model/c0201b0001.mdl";

        var ok = previewService.LoadMesh(mdlPath);
        return new
        {
            ok,
            path = mdlPath,
            triangles = previewService.CurrentMesh?.TriangleCount ?? 0,
            vertices = previewService.CurrentMesh?.Vertices.Length ?? 0,
        };
    }

    [Route(HttpVerbs.Get, "/mesh/info")]
    public object GetMeshInfo() => new
    {
        loaded = previewService.CurrentMesh != null,
        triangles = previewService.CurrentMesh?.TriangleCount ?? 0,
        vertices = previewService.CurrentMesh?.Vertices.Length ?? 0,
    };

    [Route(HttpVerbs.Get, "/log")]
    public object GetLog() => new { entries = DebugServer.LogBuffer.ToArray() };
}
```

- [ ] **Step 2: Verify build**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release
```

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/Http/
git commit -m "feat: HTTP debug server with full REST API (EmbedIO)"
```

---

### Task 9: GUI Windows

**Files:**
- Create: `SkinTatoo/SkinTatoo/Gui/MainWindow.cs`
- Create: `SkinTatoo/SkinTatoo/Gui/ConfigWindow.cs`

- [ ] **Step 1: Create MainWindow.cs**

Create `SkinTatoo/SkinTatoo/Gui/MainWindow.cs`:

```csharp
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SkinTatoo.Core;
using SkinTatoo.Interop;
using SkinTatoo.Services;

namespace SkinTatoo.Gui;

public class MainWindow : Window, IDisposable
{
    private readonly DecalProject project;
    private readonly PreviewService previewService;
    private readonly PenumbraBridge penumbra;
    private readonly Configuration config;
    private string imagePathInput = "";
    private string statusMessage = "";

    public MainWindow(DecalProject project, PreviewService previewService,
        PenumbraBridge penumbra, Configuration config)
        : base("SkinTatoo##Main", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.project = project;
        this.previewService = previewService;
        this.penumbra = penumbra;
        this.config = config;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var contentSize = ImGui.GetContentRegionAvail();
        var panelWidth = contentSize.X * 0.35f;

        using (var leftChild = ImRaii.Child("LeftPanel", new Vector2(panelWidth, contentSize.Y), true))
        {
            if (leftChild.Success)
                DrawLayerPanel();
        }

        ImGui.SameLine();

        using (var rightChild = ImRaii.Child("RightPanel", new Vector2(0, contentSize.Y), true))
        {
            if (rightChild.Success)
                DrawParameterPanel();
        }
    }

    private void DrawLayerPanel()
    {
        ImGui.Text("图层");
        ImGui.Separator();

        // Target selection
        var targetNames = new[] { "身体", "面部" };
        var targetIndex = (int)project.Target;
        if (ImGui.Combo("目标", ref targetIndex, targetNames, targetNames.Length))
            project.Target = (SkinTarget)targetIndex;

        ImGui.Spacing();

        if (ImGui.Button("+", new Vector2(24 * ImGuiHelpers.GlobalScale, 0)))
            project.AddLayer($"贴花 {project.Layers.Count + 1}");

        ImGui.SameLine();
        if (ImGui.Button("-", new Vector2(24 * ImGuiHelpers.GlobalScale, 0)) && project.SelectedLayer != null)
            project.RemoveLayer(project.SelectedLayerIndex);

        ImGui.Separator();

        for (var i = 0; i < project.Layers.Count; i++)
        {
            var layer = project.Layers[i];
            using (ImRaii.PushId(i))
            {
                var visible = layer.IsVisible;
                if (ImGui.Checkbox("##vis", ref visible))
                    layer.IsVisible = visible;

                ImGui.SameLine();
                if (ImGui.Selectable(layer.Name, project.SelectedLayerIndex == i))
                    project.SelectedLayerIndex = i;
            }
        }

        ImGui.Spacing();
        ImGui.Separator();

        // Status
        if (!penumbra.IsAvailable)
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "Penumbra 不可用");
        else if (!previewService.IsReady)
            ImGui.TextColored(new Vector4(1, 0.8f, 0.3f, 1), "未加载网格");
        else
            ImGui.TextColored(new Vector4(0.3f, 1, 0.3f, 1), "就绪");

        if (!string.IsNullOrEmpty(statusMessage))
            ImGui.TextWrapped(statusMessage);
    }

    private void DrawParameterPanel()
    {
        ImGui.Text("参数");
        ImGui.Separator();

        if (project.SelectedLayer == null)
        {
            ImGui.TextWrapped("选择一个图层来编辑参数。");
            return;
        }

        var layer = project.SelectedLayer;

        var name = layer.Name;
        if (ImGui.InputText("名称", ref name, 128))
            layer.Name = name;

        ImGui.Spacing();
        ImGui.Text("贴花图片");
        imagePathInput = layer.ImagePath ?? "";
        if (ImGui.InputText("路径", ref imagePathInput, 512))
            layer.ImagePath = imagePathInput;

        ImGui.Spacing();
        ImGui.Text("位置");
        var pos = layer.Position;
        if (ImGui.DragFloat3("##pos", ref pos, 0.01f))
            layer.Position = pos;

        ImGui.Text("旋转");
        var rot = layer.Rotation;
        if (ImGui.DragFloat3("##rot", ref rot, 1.0f, -180, 180))
            layer.Rotation = rot;

        ImGui.Text("缩放");
        var scale = layer.Scale;
        if (ImGui.DragFloat2("##scale", ref scale, 0.01f, 0.01f, 10.0f))
            layer.Scale = scale;

        ImGui.Spacing();
        var depth = layer.Depth;
        if (ImGui.SliderFloat("深度", ref depth, 0.01f, 5.0f))
            layer.Depth = depth;

        var opacity = layer.Opacity;
        if (ImGui.SliderFloat("不透明度", ref opacity, 0.0f, 1.0f))
            layer.Opacity = opacity;

        ImGui.Spacing();
        var bfThreshold = layer.BackfaceCullingThreshold;
        if (ImGui.SliderFloat("背面剔除", ref bfThreshold, 0.0f, 1.0f))
            layer.BackfaceCullingThreshold = bfThreshold;

        var grazingFade = layer.GrazingAngleFade;
        if (ImGui.SliderFloat("掠射衰减", ref grazingFade, 0.0f, 1.0f))
            layer.GrazingAngleFade = grazingFade;

        ImGui.Spacing();
        var blendNames = new[] { "正常", "正片叠底", "叠加", "柔光" };
        var blendIndex = (int)layer.BlendMode;
        if (ImGui.Combo("混合模式", ref blendIndex, blendNames, blendNames.Length))
            layer.BlendMode = (BlendMode)blendIndex;

        ImGui.Spacing();
        var diffuse = layer.AffectsDiffuse;
        if (ImGui.Checkbox("漫反射", ref diffuse))
            layer.AffectsDiffuse = diffuse;

        ImGui.SameLine();
        var normal = layer.AffectsNormal;
        if (ImGui.Checkbox("法线", ref normal))
            layer.AffectsNormal = normal;

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("预览更新", new Vector2(-1, 0)))
        {
            var texPath = BodyModDetector.GetBodyTexturePath("0201", "_d");
            var result = previewService.UpdatePreview(project, texPath);
            statusMessage = result != null ? "预览已更新" : "预览失败";
        }
    }
}
```

- [ ] **Step 2: Create ConfigWindow.cs**

Create `SkinTatoo/SkinTatoo/Gui/ConfigWindow.cs`:

```csharp
using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SkinTatoo.Gui;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration config;

    public ConfigWindow(Configuration config)
        : base("SkinTatoo 设置###SkinTatooConfig")
    {
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Size = new Vector2(350, 180);
        SizeCondition = ImGuiCond.Always;
        this.config = config;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var port = config.HttpPort;
        if (ImGui.InputInt("HTTP 端口", ref port))
        {
            config.HttpPort = Math.Clamp(port, 1024, 65535);
            config.Save();
        }

        var resolution = config.TextureResolution;
        var resOptions = new[] { "512", "1024", "2048", "4096" };
        var resValues = new[] { 512, 1024, 2048, 4096 };
        var resIndex = Array.IndexOf(resValues, resolution);
        if (resIndex < 0) resIndex = 1;
        if (ImGui.Combo("贴图分辨率", ref resIndex, resOptions, resOptions.Length))
        {
            config.TextureResolution = resValues[resIndex];
            config.Save();
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "修改端口需重启插件生效。");
    }
}
```

- [ ] **Step 3: Verify build**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release
```

- [ ] **Step 4: Commit**

```bash
git add SkinTatoo/SkinTatoo/Gui/
git commit -m "feat: GUI (MainWindow with layer/parameter panels, ConfigWindow)"
```

---

### Task 10: Plugin Entry Point

**Files:**
- Modify: `SkinTatoo/SkinTatoo/Plugin.cs`

- [ ] **Step 1: Replace Plugin.cs stub with full implementation**

Replace `SkinTatoo/SkinTatoo/Plugin.cs` with:

```csharp
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SkinTatoo.Core;
using SkinTatoo.Gpu;
using SkinTatoo.Gui;
using SkinTatoo.Http;
using SkinTatoo.Interop;
using SkinTatoo.Mesh;
using SkinTatoo.Services;

namespace SkinTatoo;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/skintatoo";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("SkinTatoo");
    internal DecalProject Project { get; } = new();

    // Core services
    private DxManager DxManager { get; init; }
    private ComputeShaderPipeline ShaderPipeline { get; init; }
    private StagingReadback StagingReadback { get; init; }
    private PenumbraBridge PenumbraBridge { get; init; }
    private BodyModDetector BodyModDetector { get; init; }
    private MeshExtractor MeshExtractor { get; init; }
    private DecalImageLoader ImageLoader { get; init; }
    internal PreviewService PreviewService { get; init; }

    // HTTP
    private DebugServer DebugServer { get; init; }

    // Windows
    private MainWindow MainWindow { get; init; }
    private ConfigWindow ConfigWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        // GPU
        DxManager = new DxManager(Log);
        DxManager.InitializeStandaloneDevice();
        ShaderPipeline = new ComputeShaderPipeline(DxManager, Log);
        ShaderPipeline.Initialize();
        StagingReadback = new StagingReadback(DxManager, Log);

        // Interop
        PenumbraBridge = new PenumbraBridge(PluginInterface, Log);
        BodyModDetector = new BodyModDetector(PenumbraBridge, Log);

        // Services
        MeshExtractor = new MeshExtractor(DataManager, Log);
        ImageLoader = new DecalImageLoader(Log);
        PreviewService = new PreviewService(
            DxManager, ShaderPipeline, StagingReadback, PenumbraBridge,
            MeshExtractor, ImageLoader, PluginInterface, Log,
            Configuration.TextureResolution);

        // HTTP
        DebugServer = new DebugServer(
            Project, PreviewService, PenumbraBridge, BodyModDetector,
            ImageLoader, Configuration, Log);
        DebugServer.Start();

        // Windows
        MainWindow = new MainWindow(Project, PreviewService, PenumbraBridge, Configuration);
        ConfigWindow = new ConfigWindow(Configuration);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开 SkinTatoo 贴花编辑器",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information("SkinTatoo loaded. Debug server: http://localhost:{0}/", Configuration.HttpPort);
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        ConfigWindow.Dispose();

        DebugServer.Dispose();
        PreviewService.Dispose();
        PenumbraBridge.Dispose();
        StagingReadback.Dispose();
        ShaderPipeline.Dispose();
        DxManager.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args) => MainWindow.Toggle();
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
```

- [ ] **Step 2: Verify build**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo && dotnet build -c Release
```

Expected: Build succeeded. 0 Error(s).

- [ ] **Step 3: Commit**

```bash
git add SkinTatoo/SkinTatoo/Plugin.cs
git commit -m "feat: Plugin entry point wiring all services together"
```

---

### Task 11: Final Build Verification + HTTP Smoke Test Plan

- [ ] **Step 1: Full clean build**

```bash
cd /c/Users/Shiro/Desktop/FF14Plugins/SkinTatoo
dotnet clean -c Release
dotnet build -c Release
```

Expected: Build succeeded. 0 Error(s).

- [ ] **Step 2: Verify output artifacts**

```bash
ls SkinTatoo/bin/x64/Release/SkinTatoo/
```

Expected files: `SkinTatoo.dll`, `SkinTatoo.json`, `EmbedIO.dll`, `StbImageSharp.dll`, etc.

- [ ] **Step 3: Document in-game smoke test procedure**

After loading the plugin in-game via `/xlsettings` → Dev Plugin Locations:

1. Plugin loads → check Dalamud log for "SkinTatoo loaded. Debug server: http://localhost:14780/"
2. Test HTTP: `curl http://localhost:14780/api/status` → returns JSON with plugin status
3. Test layer management:
   - `curl -X POST http://localhost:14780/api/layer -d '{"name":"test"}'` → adds layer
   - `curl http://localhost:14780/api/project` → shows layer in project
4. Test `/skintatoo` command → opens MainWindow
5. Test mesh load: `curl -X POST http://localhost:14780/api/mesh/load -d '{"path":"chara/human/c0201/obj/body/b0001/model/c0201b0001.mdl"}'`
6. Test preview: `curl -X POST http://localhost:14780/api/preview`

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "chore: final build verification"
```
