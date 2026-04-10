# 皮肤 UV 网格匹配调研

> 调研时间：2026-04-11
> 测试角色：敖龙族 Raen 女性 (c1401)
> 测试 mod：Eve（典型的"走小衣槽"body 替换 mod）

## 1. 背景

为皮肤贴图（body / face / iris / tail）放置贴花，需要在 UV 编辑器里加载一份正确的 mesh
作为画布。当前 `MainWindow.AddTargetGroupFromMtrl` 的逻辑是：

1. 在 Penumbra 资源树里找一个挂着目标 tex 的 mtrl 节点
2. 向上找它的父 mdl 节点
3. 把这个 mdl 当作 UV 网格

这个流程在以下情况下会失败或拿到错误的 mesh：

- 玩家穿了装备：身体 mdl 可能根本没被加载
- 玩家装了 body mod（如 Eve / bibo）：网格通过非常规路径注入
- 多个 mdl 共享同一份 mtrl 时随机挑了一个

为了搞清楚根因，我们在 4 种场景下分别 dump 了完整的资源树和 candidate 解析：

| # | 场景 |
|---|------|
| 1 | 不打 mod + 不穿装备 |
| 2 | 不打 mod + 穿装备 |
| 3 | 打 Eve mod + 不穿装备 |
| 4 | 打 Eve mod + 穿装备 |

## 2. 关键发现

### 2.1 `c1401b0001_top.mdl` 不存在于 SqPack

4 个场景下推导出的"规范身体 mdl 候选"全部标 `[SqPack ✗]`：

```
chara/human/c1401/obj/body/b0001/model/c1401b0001_top.mdl  [SqPack ✗]
chara/human/c1401/obj/body/b0001/model/c1401b0001.mdl      [SqPack ✗]
chara/human/c1401/obj/body/b0001/model/c1401b0001_etc.mdl  [SqPack ✗]
```

依据 `IDataManager.FileExists`。**FFXIV 根本不存在"vanilla 裸体身体 mdl"这种东西**——
b0001 这个槽位只是一个 mtrl 命名约定，没有对应的 mdl 文件。

> ⚠️ 待补：用 Meddle SqPack 直接探测一次做最终确认（IDataManager 偶尔不准）。

**结论**：原计划"从 tex 路径推导 → 加载 vanilla `c1401b0001_top.mdl`"的方案彻底死亡。

### 2.2 tail 的 mtrl/tex/mdl 后缀互不一致

| 位置 | 文件名 | type 后缀 |
|---|---|---|
| mtrl | `mt_c1401t0004_a.mtrl` | 无 |
| tex | `c1401t0004_etc_base.tex` | `_etc` |
| mdl | `c1401t0004_til.mdl` | `_til` |

之前的 `TexPathParser` 把 tex 里的 `_etc` 当 mdl 后缀去构造 `c1401t0004_etc.mdl`，这是错的。

**正确规则**：mdl 的后缀由 **slot 类型** 唯一决定，与 tex 名称里的 `_xxx` 段无关。
对应关系（参考 Penumbra `CustomizationType.ToSuffix()`）：

| slot | mdl 后缀 |
|---|---|
| body | `_top` |
| face | `_fac` |
| iris | _(同 face mdl，见 §2.3)_ |
| hair | `_hir` |
| tail | `_til` |
| ear (zear) | `_zer` |

### 2.3 face / iris / etc 共用同一个 face mdl

资源树原始 dump 显示：

```
chara/human/c1401/obj/face/f0001/model/c1401f0001_fac.mdl
  ├── mt_c1401f0001_fac_a.mtrl    (脸皮肤)
  ├── mt_c1401f0001_fac_b.mtrl    (脸皮肤)
  ├── mt_c1401f0001_fac_c.mtrl    (脸皮肤)
  ├── mt_c1401f0001_iri_a.mtrl    (眼球!!!  ← 同一个 mdl)
  ├── mt_c1401f0001_etc_a.mtrl    (眼睫毛/嘴/...)
  ├── mt_c1401f0001_etc_b.mtrl
  └── mt_c1401f0001_etc_c.mtrl
```

face mdl 一次性包含 7 个 material slot，眼球的 UV 是 face mdl 里某个 matIdx 对应的 mesh
子集，**不需要单独的 `_iri.mdl` 文件**。

iris 贴图（包括共享的 `chara/common/texture/eye/eye10_base.tex`）的 UV 编辑画布
应该使用 `c1401f0001_fac.mdl` + 筛选 matIdx == 用 iri 材质的那个槽位。

### 2.4 body 没有 b0001 mdl，靠 7 个共享 mtrl 的 mdl 拼出全身皮肤

场景 1（裸装无 mod）下引用 `mt_c1401b0001_a.mtrl` 的 mdl 共 7 个：

```
4 件小衣 (smallclothes)：
  chara/equipment/e0000/model/c0201e0000_top.mdl  ← 露出来的胸/肩/胳膊那点皮
  chara/equipment/e0000/model/c0201e0000_glv.mdl  ← 手腕/手背
  chara/equipment/e0000/model/c0201e0000_dwn.mdl  ← 大腿/小腿
  chara/equipment/e0000/model/c0201e0000_sho.mdl  ← 脚踝

3 个跨种族共享的"内衣抠出去"块：
  chara/human/c0201/obj/body/b0005/model/c0201b0005_top.mdl  ← Hyur F b5
  chara/human/c1401/obj/body/b0002/model/c1401b0002_top.mdl  ← Aura F b2
  chara/human/c0801/obj/body/b0003/model/c0801b0003_top.mdl  ← Lala F b3
```

这 7 个 mdl 各自贡献身体的一部分，**全部加起来才是完整的身体皮肤 UV 覆盖**。
这就解释了用户提到的"胸部和胯部是有内衣的，所以这部分是单独抠出去的"——
游戏靠 mtrl 共享 + 多 mdl 拼接来表达"皮肤"这个概念，没有单一的"身体 mdl"。

**穿装备的场景**（场景 2 / 4）下，4 件小衣会被替换成实际的装备 mdl
（如 `c1401e6085_met.mdl`、`c0201e6108_top.mdl` 等），但它们仍然引用同一份
`mt_c1401b0001_a.mtrl`，因为装备模型也需要采样身体皮肤来画领口/袖口/裤腿露出的肉。
那 3 个 c0201b0005 / c1401b0002 / c0801b0003 始终在树里，不受装备影响。

### 2.5 mtrl game path 是唯一稳定的锚点

4 场景 × 4 张贴图 = 16 个组合，**mtrl game path 100% 都是 vanilla 的**：

```
mt_c1401b0001_a.mtrl       (body)
mt_c1401f0001_fac_a.mtrl   (face skin)
mt_c1401f0001_iri_a.mtrl   (iris)
mt_c1401t0004_a.mtrl       (tail)
```

即使是 Eve mod 这种把 mtrl 重写到 `D:\FF14Mod\Eve\vanilla materials\raen_a.mtrl`
的 mod，**game path 仍然是 vanilla**——mod 替换的是 mtrl 文件本身，不是路径。

对比 tex game path：
- 不打 mod 时：`chara/human/c1401/obj/body/b0001/texture/c1401b0001_base.tex`
- 装 Eve mod 时：`chara/nyaughty/eve/vanilla_raen_base.tex` ← **完全 mod 自定义路径**

→ 推导锚点必须用 mtrl game path，**不能**用 tex game path。

### 2.6 Eve mod = 走"小衣槽重定向"的 body 替换

场景 3 显示得很清楚：

```
chara/equipment/e0000/model/c0201e0000_top.mdl → D:\FF14Mod\Eve\models - body\body - milky.mdl
chara/equipment/e0000/model/c0201e0000_glv.mdl → D:\FF14Mod\Eve\models - hands\hands - natural.mdl
chara/equipment/e0000/model/c0201e0000_dwn.mdl → D:\FF14Mod\Eve\models - legs\legs - default.mdl
chara/equipment/e0000/model/c0201e0000_sho.mdl → D:\FF14Mod\Eve\models - feet\feet - natural.mdl
```

Eve **不重定向 vanilla body mdl 路径**（因为根本没有那个 mdl，见 §2.1），它把
4 件小衣的 mesh 替换成"完整裸体的各个部位"。
当玩家不穿装备只穿小衣时，这 4 个 mod mdl 渲染出来就是 Eve 的完整裸体。

→ 任何"加载 mdl 作为 UV 画布"的逻辑都必须通过 **Penumbra 已解析的
`mdl_node.ActualPath`**，才能拿到 mod 替换后的 mesh。
直接走 vanilla 路径会拿到错的 UV（虽然 Eve 这种用 vanilla-compatible UV 的 mod
看起来还能用，但形如 bibo 这类改 UV 的 mod 就会出问题）。

### 2.7 ⭐ 关键发现：游戏引擎在运行时强制把 skin mtrl 的 `bXXXX` 换成 `b0001`

来源：FFXIV_TexTools_UI 的 `Views/FileControls/ModelFileControl.xaml.cs:455-496`
（`AdjustSkinMaterial`）。TexTools 注释直接说明：

> // XIV automatically forces skin materials to instead reference the
> // appropriate one for the character wearing it.
> race = XivRaceTree.GetSkinRace(race);
> ...
> mtrl = Regex.Replace(mtrl, "b[0-9]{4}", "b0001");

**意思**：mdl 文件**内部**写的是它原本的 body slot id（例如 `c1401b0002_top.mdl`
里大概率引用的是 `mt_c1401b0002_a.mtrl`），但游戏引擎在加载时强行把 `bXXXX`
正则替换成 `b0001`。所以 Penumbra 资源树里我们看到的是**已经被引擎改写过**的
形态：

```
Mdl chara/human/c1401/obj/body/b0002/model/c1401b0002_top.mdl
  └─ Mtrl chara/human/c1401/obj/body/b0001/material/v0001/mt_c1401b0001_a.mtrl
                                          ↑↑↑↑↑
                                       这一段被引擎从 b0002 改写成了 b0001
```

这就解释了之前"为什么 7 个完全不相干的 mdl 都引用同一份 mtrl"——它们各自原本引用
不同的 body slot，但都被引擎规范化到了 `b0001`，于是在树里看起来都共享同一份
mtrl。**它们其实不共享同一个 UV mesh**。

### 2.8 ⭐ 关键发现：每个种族的 canonical body mdl 是唯一的

来源：VFXEditor-CN 的 `VFXEditor/Files/common_racial`（vanilla 资源全清单）。
对 c1401（敖龙女）只列出**一个** body mdl：

```
$ grep "c1401" common_racial | grep "/body/" | grep ".mdl$"
chara/human/c1401/obj/body/b0002/model/c1401b0002_top.mdl    ← 唯一一个
```

其他几个 entry 都只是 mtrl，没有 mdl。

**Au Ra Female 整个种族唯一的 vanilla body mdl 就是 `c1401b0002_top.mdl`。**

之前看到的 `c0201b0005_top.mdl` / `c0801b0003_top.mdl` 是别的种族的 body
（Hyur F b5、可能是 Lala F b3），它们出现在玩家树里是因为引用了被改写到 `b0001`
的相同 skin mtrl，但它们**不是 c1401 玩家的身体**。

> Hyur Midlander Male (c0101) 在清单里有 5 个 body slot：b0001 (top/glv/dwn/sho 4 件) +
> b0002/b0003/b0005/b0006 各一个 _top。其中 b0001 是分件式的"裸体小衣"型 body，
> 通常用于 NPC 等不穿装备的角色；b0002+ 才是正常成年角色用的整体 body mdl。
> 不同种族 / 性别有不同的 canonical body slot id（aura f 是 b0002，hyur m 是
> b0001 4-piece 等），需要靠数据驱动而不是硬编码猜测。

### 2.9 §2.7 + §2.8 的初步综合

把两件事合在一起：

| 我们看到的 | 真相 |
|---|---|
| 7 个 mdl 都挂着 `mt_c1401b0001_a.mtrl` | 它们各自的真实 mtrl 引用是不同 body slot，被引擎统一改写到了 b0001 |
| 找不到 `c1401b0001_top.mdl` | b0001 对 c1401 来说本来就不存在 |
| 哪个 mdl 才是"c1401 玩家的身体"？ | 看起来应该是 race + body 路径模式过滤后的唯一答案 `c1401b0002_top.mdl` |

但**这个结论后来被实测推翻了**——见 §2.10。

### 2.10 ⭐ 实测纠正：`c1401b0002_top.mdl` 是 24 顶点的 stub，**不是身体**

ReadMaterialFileNames + Meddle ExtractMesh 之后实际加载 `c1401b0002_top.mdl`：

```
[MeshExtractor] mesh[0]: matIdx=0 verts=24 indices=60 submeshes=1
[MeshExtractor] Done: 24 verts, 20 tris
```

VFXEditor `common_racial` 把它列为 c1401 的 body mdl，但它**只有 24 个顶点**——
连一只手都画不出来。这告诉我们：

> **`chara/human/cXXXX/obj/body/...` 路径下的 mdl 是引擎内部 racial deformer / 跨种族
> 共享几何用的 stub mesh（一般 24~400 顶点的微小补丁），不是渲染用的可见身体**。

那真正的身体几何在哪？**全都在 equipment slot mdls 里**：

```
chara/equipment/e0000/model/c0201e0000_top.mdl   ← 上半身 mesh（顶部 + 胸 + 手臂）
chara/equipment/e0000/model/c0201e0000_glv.mdl   ← 手部
chara/equipment/e0000/model/c0201e0000_dwn.mdl   ← 腿部
chara/equipment/e0000/model/c0201e0000_sho.mdl   ← 脚部
```

这 4 个是 **e0000 = "smallclothes（裸装）"** 的 equipment mdl。FFXIV 的角色渲染管线
对 body slot 永远穿着 equipment（即使 e0000 内衣也是 equipment）。这 4 个 mdl 通过
**racial deformer** 系统按穿戴者的种族变形成对应的身材，所以同一份 c0201e0000 mesh
能服务所有种族。

**body mod 的工作原理**就豁然开朗了：Eve 这类 mod 把这 4 个 equipment mdl 文件**整体
替换**成"完整裸体的对应部位"，于是穿小衣的玩家看到的就是 mod 提供的 mesh，而 vanilla
b0002_top.mdl 这种 stub 仍然是 24 顶点的引擎内部状态。

**总结**：身体几何 = `chara/equipment/...` 路径下的 mdl，**任何 `chara/human/.../body/...`
路径下的 mdl 都应该过滤掉**，无论种族是否匹配。

### 2.11 ⭐ face mdl 的 mat slot 共享同一组贴图

face mdl `c1401f0001_fac.mdl` 有 7 个 mat slot。资源树证实 fac_a/b/c 三个 mtrl
**全部引用同一组** `c1401f0001_fac_base.tex` / `_fac_norm.tex` / `_fac_mask.tex`：

```
- Mtrl mt_c1401f0001_fac_a.mtrl     ┐
  - Tex c1401f0001_fac_base.tex     │
  - Tex c1401f0001_fac_norm.tex     │
  - Tex c1401f0001_fac_mask.tex     │
- Mtrl mt_c1401f0001_fac_b.mtrl     │
  - Tex c1401f0001_fac_base.tex     ├ 共享同一组 fac 贴图
  - ...                             │
- Mtrl mt_c1401f0001_fac_c.mtrl     │
  - Tex c1401f0001_fac_base.tex     │
  - ...                             ┘
```

意思是：在 `fac_base.tex` 上画的贴花会同时影响 fac_a/b/c 这 3 个 mat slot 对应的所有
mesh group。**Au Ra 的龙角/鳞片就在 fac_b 槽位**——如果 matIdx 过滤只取 fac_a，
龙角就会被漏掉。

→ face 必须按 **role suffix** 匹配 matIdx：target = `_fac_a` 时，命中所有 internal name
里 role 是 `fac` 的槽位（即 fac_a + fac_b + fac_c），而不是只命中精确同名的那一个。

iris (`_iri_a`) 和 etc (`_etc_*`) 各有自己的 role，互不污染：painting iris 只命中
`iri_*` 槽位，不会画到脸上。

### 2.12 UDIM tile：不同种族的 body stub 可能使用不同 UV tile

Per-slot UV bounds 实测（vanilla `_a.mtrl` 解析出的 3 个 stub mdl）：

```
c0201b0005_top.mdl: X=[1.034, 1.971] Y=[0.017, 0.984]   ← UV tile 1（X∈[1,2]）
c1401b0002_top.mdl: X=[1.245, 1.511] Y=[0.008, 0.049]   ← UV tile 1
c0801b0003_top.mdl: X=[0.439, 0.846] Y=[0.050, 0.950]   ← UV tile 0（X∈[0,1]）
```

c0801b0003（Miqo'te F 的 stub）的 UV 在 tile 0，跟其他 stub 不在同一个 tile。
canvas 之前用"全局 floor(min(UV))"做 tile base 归一化，对单 mdl 没问题，但**合并
跨 tile 的多 mdl 时会出错**——floor 取最小值后只有一个 tile 的顶点能落在画布的纹理
区域内，另一个 tile 的顶点被推到画布外。

→ canvas 改成**每顶点单独取 fract**（`uv - floor(uv)`）即可正确处理 UDIM tile，
两个不同 tile 但在同一 texel 的顶点会映射到同一画布位置，这正是 UDIM 的语义。

## 3. 最终算法（v3）

```
输入：
  - target_mtrl_game_path  (例如 mt_c1401b0001_a.mtrl，已被引擎改写过的形态)
  - playerRace             (从 Penumbra ResourceTreeDto.RaceCode 拿，例如 1401)

1. 解析 mtrl path 得到 race / slot kind / slot id / role suffix.

2. 扫描 live resource tree 收集所有 Mdl 节点，其子 Mtrl 列表里有 GamePath
   等于 target_mtrl_game_path 的（"all referers"）.

3. slot-aware 过滤 referers:

   - body:
       FILTER 掉所有 chara/human/c\d{4}/obj/body/... 路径
       （这些都是 racial deformer stub，不是可见几何）。
       保留 chara/equipment/* / chara/accessory/* 之类。
       退化：如果过滤后为空（罕见，character creator 等场景），保留 stub。

   - face / hair：
       race filter，保留 chara/human/c{playerRace}/obj/{slot}/.../*_{fac|hir}\.mdl
       （实际上 live tree 通常只有 1 个匹配，filter 是防御性的）。

   - tail：
       race filter，保留 chara/human/c{playerRace}/obj/tail/.../*_til\.mdl

4. 对每个保留下来的 mdl，加载 .mdl 文件读取 MaterialFileNames[]，
   找出所有匹配 target 的 matIdx：

   - body / tail（target 没有 role suffix）:
       严格 normalized name match（c\d{4} → c????, b\d{4} → b???? 之后比较）
       例: target mt_c1401b0001_a.mtrl → mt_c????b????_a.mtrl
            internal /mt_c0201b0001_a.mtrl → mt_c????b????_a.mtrl  ✓

   - face / hair（target 有 role suffix）:
       role-based match：提取 internal name 的 role suffix，跟 target 的 role
       suffix 比较。
       例: target mt_c1401f0001_fac_a.mtrl → role "fac"
            internal /mt_c1401f0001_fac_a.mtrl → role "fac"  ✓
            internal /mt_c1401f0001_fac_b.mtrl → role "fac"  ✓  (Au Ra 龙角)
            internal /mt_c1401f0001_fac_c.mtrl → role "fac"  ✓
            internal /mt_c1401f0001_iri_a.mtrl → role "iri"  ✗

5. 输出 List<MeshSlot>，每个 MeshSlot = (mdl_game_path, mdl_disk_path, matIdx[])。

6. 加载阶段：对每个 MeshSlot 加载对应 mdl 并按 matIdx 提取 mesh group，最后合并。
```

### 适用性验证（v3 实测）

| target | 解析结果 | 备注 |
|---|---|---|
| body `_a` (vanilla state) | 4 equipment + 0 stubs = **4 slots** | 4 件小衣 mdl 各 matIdx [0]，构成完整身体 |
| body `_b` (Eve mod state) | 4 equipment（Eve 重定向到 mod 文件）= **4 slots** | mod mdl 的 matIdx [0] 是 gen3 skin 槽位 |
| body `_a` (Eve mod state) | 4 equipment 都没 `_a` 引用 = **0 from filter, fallback to stub = 1 slot** | Eve 改写后 mat 槽里只剩 `_b`/`_eve-piercing`；vanilla `_a` 在 mod 状态下是非可见材质 |
| face skin `_fac_a` | 1 mdl × **3 matIdx [0,1,2]** | fac_a + fac_c + fac_b（含 Au Ra 龙角）|
| iris `_iri_a` | 1 mdl × **1 matIdx [3]** | 仅 iri_a，互不污染 |
| tail `_a` | 1 mdl `c1401t0004_til.mdl` × matIdx [0] | tail mtrl/tex/mdl 后缀不一致也无所谓 |

### 关键性质

- **mtrl game path 是唯一锚点**：在 vanilla / mod / 穿装备 / 不穿装备 4 种状态下都不变
- **不依赖路径推导**：所有数据都从 live tree 来，没有"猜 mdl 路径"
- **自动支持 mod 重定向**：每个 MeshSlot 的 disk path 是 ResourceNode.ActualPath，
  Penumbra 已经解析过 mod 链
- **同时处理 race deformer 和 mod 重写**：body 走 equipment-only filter（vanilla 和
  Eve 这种重定向 equipment 槽的 mod 都 work）；face/hair/tail 走 race filter
  （只取玩家自己种族的 face/hair/tail）

## 4. UV 画布渲染：per-vertex fract 而不是全局 floor

`MainWindow.Canvas.cs` 的 wireframe 渲染原本对整个 mesh 取一个 `uvBase = floor(min(UV))`
来归一化（兼容 body 模型的 UV X∈[1,2] tile 1 约定）。但合并多个 mdl 后，**不同 mdl
的顶点可能来自不同 tile**（见 §2.12），全局 floor 会把一部分顶点推到画布外。

修复：**每顶点单独 `fract()`**：

```csharp
Vector2 ToScreen(Vector2 uv)
{
    var fract = new Vector2(uv.X - MathF.Floor(uv.X), uv.Y - MathF.Floor(uv.Y));
    return uvOrigin + (texOffset + fract * uvScale) * fitSize;
}
```

`(1.6, 0.5)` 和 `(0.6, 0.5)` 都映射到画布的同一像素 —— 这是 UDIM 的标准约定，
两个不同 tile 的顶点指向同一 texel。

## 5. 加载链路：所有 mesh 加载入口都走 PreviewService.LoadMeshForGroup

之前有 3 个不同的 callsite 各自加载 mesh：

| callsite | 老逻辑 | 问题 |
|---|---|---|
| `MainWindow.ResourceBrowser.AddTargetGroupFromMtrl` | 调 resolver 然后 LoadMeshSlots | OK |
| `MainWindow.cs` 工具栏"重新加载模型"按钮 | 调 LoadMeshes(group.AllMeshPaths) | 用 legacy 单 mdl 路径，丢 matIdx |
| `Plugin.cs InitializeProjectPreview`（启动） | 同上 | 同上 |
| `ModelEditorWindow.TryUploadMesh`（group 切换） | 同上 | **关键 bug**：用户点添加触发 LoadMeshSlots 加载 4 个 mdl，但下一帧 group switch 检测会用 legacy 路径覆盖回单 mdl，造成"只看到上半身" |

修复：把分发逻辑收到 `PreviewService.LoadMeshForGroup(group)`：

```csharp
public bool LoadMeshForGroup(TargetGroup group)
{
    if (group.MeshSlots.Count > 0)
        return LoadMeshSlots(group.MeshSlots);   // 新 resolver 路径

    if (!string.IsNullOrEmpty(group.MeshGamePath))
        return LoadMeshWithMatIdx(group.MeshGamePath!,
            group.TargetMatIdx.Length > 0 ? group.TargetMatIdx : null,
            group.MeshDiskPath);                  // 中间形态兼容

    if (group.AllMeshPaths.Count > 0)
        return LoadMeshes(group.AllMeshPaths);    // 老 config 兼容

    return false;
}
```

3 个 callsite 全部走它，新 resolver 路径再也不会被静默覆盖。

## 6. `TexPathParser` 的角色调整

新算法不再需要"从路径推导 mdl"。`TexPathParser` 只保留：

1. `ParseFromMtrl(mtrlGamePath)` 解析 race / slot / slotId / role suffix
2. `ParseFromTex(texGamePath)` 用于 UI 显示（不可靠，mod 状态下可能解析失败）
3. `ParseBest(tex, mtrl)` 优先 mtrl，回退 tex

**已删除**：之前的 `CandidateModelTypes` fallback 列表（猜 body→[top, "", etc]、
tail→[etc, til] 之类的硬编码候选）。slot→suffix 的对应由 SkinMeshResolver 在
`BuildCanonicalMdlPattern` 里硬编码处理（face→fac、tail→til、hair→hir）。

## 7. 参考代码出处

- **TexTools 运行时改写规则** (§2.7):
  `FFXIV_TexTools_UI/FFXIV_TexTools/Views/FileControls/ModelFileControl.xaml.cs:455-496`
  `AdjustSkinMaterial()` 方法，含权威注释
- **TexTools `XivRaceTree.GetSkinRace`** —— 种族 → skin 种族的投影
  （`xivModdingFramework`，submodule 未本地 checkout）
- **VFXEditor canonical body 清单** (§2.8):
  `VFXEditor-CN/VFXEditor/Files/common_racial`
  解析代码：`VFXEditor/Select/Data/RacialData.cs:37-49`
- **Meddle 跳过 b0003_top**:
  `Meddle/Meddle.Plugin/Models/Composer/CharacterComposer.cs:44-47`
  硬编码跳过 cross-race 的 stub mdl。我们的方案做 equipment-only filter，
  所以这种 stub 自然被过滤掉，不需要 hardcode skip
- **Penumbra GenderRace + GamePaths**:
  `Penumbra-CN/Penumbra.GameData/Enums/Race.cs`
  `Penumbra-CN/Penumbra.GameData/Data/GamePaths.cs`

## 9. 算法 v4：Eve 实测后的修正

v3 发布后用 Eve 跑实测，又暴露了三个新问题。逐个 fix 后进入 v4。

### 9.1 ⭐ Eve mod 的 mat slot 字符串不是 vanilla 形式

Eve 的 4 个装备 mdl 文件里 mat slot 字符串实际是：

```
c0201e0000_top.mdl: [#0=/mt_c0201b0001_b.mtrl, #1=/mt_c0201b0001_eve-piercing.mtrl]
c0201e0000_glv.mdl: [#0=/mt_c0201b0001_b.mtrl]
c0201e0000_dwn.mdl: [#0=/mt_c0201b0001_b.mtrl, #1=/mt_c0201b0001_eve-piercing.mtrl, #2=/mt_c0201b0001_secretgarden.mtrl]
c0201e0000_sho.mdl: [#0=/mt_c0201b0001_b.mtrl]
```

`_b.mtrl`（gen3 皮肤）+ 各种 Eve 自定义 mat 名（`_eve-piercing.mtrl` / `_secretgarden.mtrl`）。
**没有任何一个槽位是 `_a.mtrl`**。但 live tree 又显示这 4 个 mdl 都"引用了" `mt_c1401b0001_a.mtrl`——
这是因为 Penumbra 的 `ResolveContext.CreateNodeFromModel` 在所有真槽 Mtrl 子节点之后**追加**了
一个 `skinMtrlHandle` 子节点（`Penumbra/Interop/ResourceTree/ResolveContext.cs:223-227`），
GamePath 是引擎规范化的 skin mtrl 路径，跟真实文件 mat slot 表无关。

→ 不能再用 live tree 的"Mtrl 子节点位置"做 matIdx 匹配（位置会因为 null 槽 + 追加节点漂移）。

### 9.2 Penumbra `ResolveMaterialPath` 的字母触发条件

`Penumbra/Interop/ResourceTree/ResolveContext.PathResolution.cs:98`：

```csharp
ModelType.Human when IsEquipmentOrAccessorySlot(SlotIndex) && mtrlFileName[8] != (byte)'b'
    => ResolveEquipmentMaterialPath(...),  // 拼装备路径
_  => ResolveMaterialPathNative(mtrlFileName),  // 走游戏原生函数（含运行时种族 / 字母替换）
```

`mtrlFileName[8]` 是文件名第 9 个字符。`mt_c0201b0001_b.mtrl` 第 9 个字符是 `'b'`，进 native 分支；
`eve-piercing.mtrl` 第 9 个字符不是 `'b'`，被拼成 `chara/equipment/e0000/material/v0001/eve-piercing.mtrl`。

native 分支的实际行为依赖游戏函数，对 Eve 的 `_b.mtrl` 槽位**不一定**会解析到 vanilla `_a.mtrl`——
也就是 Phase 1 "用 live tree GamePath 等于 target" 在 mod 状态下不可靠。

### 9.3 ⭐ 算法 v4：letter-aware 归一化 + SqPack vanilla 回落

新的 body / tail 匹配逻辑：

```
对每个 referer：
  Attempt (a): ReadMaterialFileNames(gamePath, ActualPath)
                read Penumbra 解析后的 disk 文件（可能是 mod 重定向文件，比如
                Eve 的 body-milky.mdl）。letter-aware 归一化匹配：
                  target  mt_c1401b0001_a.mtrl → mt_c????b????_a.mtrl
                  Eve槽   /mt_c0201b0001_b.mtrl → mt_c????b????_b.mtrl  ✗
                  vanilla /mt_c0201b0001_a.mtrl → mt_c????b????_a.mtrl  ✓
                命中就用这个 disk 文件 + matIdx，结束。

  Attempt (b): 如果 (a) 没匹中，ReadMaterialFileNames(gamePath, null)
                同 gamepath 但 diskPath 传 null，让 MeshExtractor 走 SqPack
                lookup，**绕开 Penumbra 重定向**，拿 vanilla 原始文件。
                letter-aware 匹配，命中就存 MeshSlot { DiskPath = null }，
                后续 ExtractMesh 也走 SqPack 加载 vanilla 几何。
```

**结果（Eve 状态下打 vanilla `_a.mtrl`）**：

| Mdl | (a) Eve disk 文件 | (b) SqPack vanilla 文件 | 用哪个 |
|---|---|---|---|
| top | `[_b, _eve-piercing]` ✗ | `[_a, _top_a]` → matIdx 0 ✓ | vanilla SqPack |
| glv | `[_b]` ✗ | `[_a]` → matIdx 0 ✓ | vanilla SqPack |
| dwn | `[_b, _eve-piercing, _secretgarden]` ✗ | `[_a, _dwn_a]` → matIdx 0 ✓ | vanilla SqPack |
| sho | `[_b]` ✗ | `[_a, _sho_a]` → matIdx 0 ✓ | vanilla SqPack |

**结果（Eve 状态下打 gen3 `_b.mtrl`）**：

| Mdl | (a) Eve disk 文件 | 用哪个 |
|---|---|---|
| top | `[_b, _eve-piercing]` → matIdx 0 ✓ | Eve disk |
| glv | `[_b]` → matIdx 0 ✓ | Eve disk |
| dwn | `[_b, _eve-piercing, _secretgarden]` → matIdx 0 ✓ | Eve disk |
| sho | `[_b]` → matIdx 0 ✓ | Eve disk |

**关键性质**：vanilla `_a` 和 gen3 `_b` 都解析到同一组 4 个 gamepath，但**加载的是各自对应的物理文件**——
vanilla 走 SqPack 拿 vanilla 几何 + UV 布局，gen3 走 Eve disk 拿 gen3 几何 + UV 布局。
这样画面上 vanilla 纹理对 vanilla UV、gen3 纹理对 gen3 UV，永远不会错位。

> 副作用：穿真实装备时（不打 mod，e6108 top / e6064 glv / e0737 dwn / e6085 met / e6085 sho），
> resolver 自动识别 `met` 和 `sho` 没有 body skin 槽（只有头盔 / 鞋子材质，归一化后不匹中 `_a`），
> 跳过这两个 mdl，最终只返回 top + glv + dwn 三块身体几何。这是对的——脚和头被装备包住，
> 没有可见 body skin。

### 9.4 equipment-first / stub-fallback

v3 的"装备 vs stub 二选一"改成两阶段：

```
1. 先在 equipment referer 集合上跑 (a)+(b) 匹配
2. equipment 一个都没匹中才回落到 stub referer 集合
```

正常 Eve 场景下 vanilla `_a` 走 SqPack vanilla 也能在装备 mdl 里找到，不会触发 stub 回落。
stub 回落是 character creator / 自定义 NPC 等没有装备的边缘场景的兜底。

### 9.5 ⭐ ClearMesh 竞态 bug

`MainWindow.DrawActionsSection` 之前在 group 切换时无条件 ClearMesh：

```csharp
if (project.SelectedGroupIndex != lastSelectedGroupIndex) {
    previewService.ClearMesh();   // ← 清掉刚加载好的 mesh
    ModelEditorWindowRef?.OnMeshChanged();
}
```

时序：

1. `AddTargetGroupFromMtrl` 创建新 group + `LoadMeshSlots` → currentMesh = 1440 verts
2. 同帧 `ModelEditorWindow.TryUploadMesh`：groupIdx 变了 → reload → currentMesh OK
3. 同帧 `MainWindow.DrawActionsSection`：groupIdx 变了 → ClearMesh → currentMesh = null
4. 下一帧 `TryUploadMesh`：groupIdx 没变 + pathKey 没变 → 不重 load → 永远卡 null
5. `DrawHeader` 显示"未加载网格"

**两处修复**：

- `MainWindow.DrawActionsSection`：只在 `SelectedGroup == null`（删除 / 空选）才 ClearMesh，
  正常 group 切换由 `TryUploadMesh` 自己的 pathKey diff 处理
- `ModelEditorWindow.OnMeshChanged()`：除了 reset `uploadedMesh`，也 reset `lastMeshPath = null`，
  这样即便其它路径外部 ClearMesh，下一帧 `TryUploadMesh` 的 pathKey diff 能正常重 load

## 8. 实施清单

- [x] 简化 `TexPathParser`：删 candidate 生成，加 `ParseFromMtrl`
- [x] `MeshExtractor.ExtractMesh` 加 `int[]? allowedMatIdx` 过滤参数
- [x] `MeshExtractor.ReadMaterialFileNames` 读取 mdl 内部 mat 列表
- [x] `MeshExtractor.NormalizeGamePath`：兼容 backslash 相对路径
- [x] 新建 `Mesh/SkinMeshResolver`：input mtrl path → output List<MeshSlot>
- [x] 新建 `Core/MeshSlot`：(GamePath, DiskPath, MatIdx[])
- [x] `TargetGroup.MeshSlots`：持久化字段
- [x] `PreviewService.LoadMeshSlots` + `LoadMeshForGroup`：统一加载入口
- [x] `MeshExtractor.ExtractAndMergeSlots`：多 mdl + 各自 matIdx filter 合并
- [x] `MainWindow.AddTargetGroupFromMtrl` 接 SkinMeshResolver
- [x] `MainWindow.cs` / `Plugin.cs` / `ModelEditorWindow.cs` 改走 LoadMeshForGroup
- [x] body 的 equipment-only filter（drop 所有 `chara/human/.../body/...` stub）
- [x] face/hair 的 role-based matIdx 匹配（含 Au Ra 龙角的 fac_b 槽位）
- [x] canvas wireframe per-vertex fract 归一化
- [x] HTTP `/debug/skin-chain` endpoint 接受 `?mtrl=` 参数 + 完整 resolver dump
- [x] 资源浏览器卡片"路径"诊断弹窗 + "导出全部诊断"按钮
- [x] body / tail letter-aware 归一化匹配（§9.3）
- [x] body / tail SqPack vanilla 回落：mod 状态下 vanilla `_a.mtrl` 走 SqPack 拿 vanilla 几何（§9.3）
- [x] `MeshSlot.DiskPath` 改 nullable，null = 走 SqPack vanilla
- [x] equipment-first / stub-fallback 两阶段匹配（§9.4）
- [x] ClearMesh 竞态修复：`MainWindow` 不再无条件清，`OnMeshChanged` reset `lastMeshPath`（§9.5）
- [ ] 边缘情况：character creator 状态下没有 equipment mdl，需要 fallback 到 stub
      （目前已有兜底，但未实测）
- [ ] 玩家切换装备 / 切换 mod 之后缓存的 MeshSlots 过期，需要触发刷新机制
