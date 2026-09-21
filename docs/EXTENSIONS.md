# 扩展管理（HoYoShade Extensions）

给 HoYoShade Hub 加的一个「在客户端里装/卸 ReShade 插件」的功能。扩展本体落到**游戏目录下的 `HoYoShade`**，
管理界面在 **HoYoShade Hub** 里（设置 → 工具箱 → 扩展管理）。

---

## 1. 它解决什么

ReShade 生态里，「插件」= `.addon64`，「着色器」= `.fx`，「预设」= `.ini`。
这三样东西平时要靠手动解压、找目录、改名，而且**卸载几乎必然误删**（`reshade-shaders` 里混着官方包和用户自己放的文件）。

这个功能把这三件事变成一次点击：

| 能力 | 说明 |
| --- | --- |
| 添加扩展 | 从 GitHub Release / 直链 / 本地 zip 安装 addon、着色器、预设到 `HoYoShade` 目录 |
| 删除扩展 | 按安装时记下的 sha256 逐个校验后删除，用户自己放的文件、被改过的文件一律不动 |
| 从 GitHub 更新 | 目录条目指向仓库 Release，`tagPattern` + `assetPattern` 解析；框架更新复用 `DuolaD/HoYoShade` 的 Release |
| 不依赖 GitHub API 限额 | 解析 release **优先走 atom + HTML 页**（见 §2.1），匿名 60 次/小时的 REST 配额打爆了也能装 |
| 对标 RHI 的插件市场 | 内置目录 + 远程目录 + 任意 GitHub 仓库，条目是纯 JSON，加插件不用重新发版 |

---

## 1.1 ⚠️ 别再让安装依赖 GitHub REST API 的限额

**踩过的坑**：解析 release 里该下哪个资产，原来只有「资产名写死」那条路能绕开 API；
用 `assetPattern` 选资产（内置目录里 5/8 条都是）**必须**调 `api.github.com/repos/.../releases`。
匿名 API 只有 **60 次/小时，而且按出口 IP 算** —— 同一个代理后面只要有人（或某个脚本）把配额打爆，
用户那边就只看到「获取文件失败」，根本猜不到原因。实测就是这么被打爆的。

现在 `GithubReleaseResolver.ResolveAsync` 按这个顺序走：

| 顺序 | 做什么 | 吃 API 限额吗 |
| --- | --- | --- |
| 1 | 有 `assetName`：`releases.atom` 拿 tag → 直接拼下载地址 | ❌ 不吃 |
| 2 | 有 `assetPattern`：atom 拿 tag → 抓 `releases/expanded_assets/{tag}`（HTML）拿资产名 → 拼地址 | ❌ 不吃 |
| 2b | atom 只有最近 10 条，老插件族翻 `/releases?page=N`（HTML，最多 6 页）找 tag | ❌ 不吃 |
| 3 | 上面都不行才读 Release JSON（有 size / prerelease 这些更准的信息） | ✅ 吃 |

所以正常情况下**一次 API 都不调**。自测 `--online` 会真连一遍所有内置条目，
跑完 8 条全过、且完全不碰 `api.github.com`。

> 万一真撞上 403，现在也会报人话：「GitHub API 限流了（匿名只有 60 次/小时，按出口 IP 算）：HTTP 403。等一会儿再试。」

---

## 2. 顺手修掉的一个 Hub 的 Bug

**Hub 现在装进去的 addon 实际不会生效。**

ReShade 的 addon 加载逻辑（`crosire/reshade` → `source/addon_manager.cpp`）：

```cpp
// Get directory from where to load add-ons from
std::filesystem::path addon_search_path = g_reshade_base_path;
if (config.get("ADDON", "AddonPath", addon_search_path))
        addon_search_path = g_reshade_base_path / addon_search_path;
// 随后对 addon_search_path 做【非递归】目录扫描，匹配 *.addon / *.addon64
```

三个事实：

1. 键名是 `[ADDON] AddonPath=` —— **不是** `AddonSearchPaths`（ReShade 源码里根本没有这个字符串）；
2. 它相对 ReShade DLL 所在目录，也就是 `<游戏目录>\HoYoShade`；
3. 扫描是**非递归**的，addon 必须直接躺在这个目录里。

而 Hub 的 `HoYoShadeInstallService` 把 addon 放进 `reshade-shaders\Addons\`，然后
`ReShadeIniService.WriteSearchPaths()` 只写了：

```ini
[GENERAL]
EffectSearchPaths=.\reshade-shaders\Shaders\**
TextureSearchPaths=.\reshade-shaders\Textures\**
```

**没有 `[ADDON] AddonPath`**。于是 ReShade 只去扫 `HoYoShade` 根目录，扫不到 `reshade-shaders\Addons` 里的东西。

本扩展在安装任何 addon 时会自动补齐：

```ini
[ADDON]
AddonPath=.\reshade-shaders\Addons
```

只增不改，`[GENERAL]` / `[INPUT]` 里的既有内容和注释原样保留。

> ⚠️ 这一条是从 ReShade 源码读出来的结论，**还没有在真机上验证过**。第一次上机请开着 ReShade 日志
> （`ReShade.ini` 里 `[GENERAL] LogLevel=debug`）确认它打印了
> `Searching for add-ons (*.addon64) in '...\HoYoShade\reshade-shaders\Addons'`。

---

## 3. 目录布局与账本

```
<游戏目录>\
└── HoYoShade\
    ├── ReShade64.dll            ← 宿主判定的依据
    ├── ReShade.ini              ← 我们只补 [ADDON] AddonPath
    ├── reshade-shaders\
    │   ├── Shaders\             ← .fx
    │   ├── Textures\
    │   └── Addons\              ← *.addon64 / *.addon32
    ├── Presets\                 ← .ini
    └── .hysx\                   ← 我们自己用，Hub 不碰
        ├── installed.json       ← 账本：装了哪些扩展、每个文件的 sha256
        └── backup\              ← 覆盖别人文件之前的备份
```

账本长这样：

```json
{
  "schema": 1,
  "extensions": [
    {
      "id": "renodx.hkrpg",
      "name": "RenoDX — 崩坏：星穹铁道",
      "version": "nightly",
      "installedAt": "2026-09-17T10:00:00+08:00",
      "resolvedTag": "nightly-20260917",
      "files": [
        {
          "path": "reshade-shaders/Addons/renodx-honkai-starrail.addon64",
          "size": 123456,
          "sha256": "…",
          "created": true
        }
      ]
    }
  ]
}
```

`created: false` 表示这个文件**安装前就存在**（被我们覆盖了，原文件已进备份）。
卸载时这类文件只摘账本、不删文件。

---

## 4. 扩展条目格式

内置目录：`src/HoYoShadeHub.Extensions/Resources/catalog.builtin.json`（嵌入 DLL）。
远程目录：默认留空，可指向本仓库 raw 上的 `catalog.json` —— 这样加插件不用重新发版。

```jsonc
{
  "id": "renodx.hkrpg",
  "name": "RenoDX — 崩坏：星穹铁道",
  "version": "nightly",
  "author": "clshortfuse",
  "description": "…",
  "homepage": "https://github.com/clshortfuse/renodx",
  "tags": ["hdr", "tonemap"],
  "gameBiz": ["hkrpg_cn", "hkrpg_global", "hkrpg_bilibili"],  // 空 = 不限
  "hosts": ["HoYoShade"],                                      // 空 = 只允许 HoYoShade
  "source": {
    "type": "github-release",          // github-release | direct | local
    "repository": "clshortfuse/renodx",
    "includePrerelease": true,
    "tagPattern": "^nightly-\\d{8}$",
    "assetPattern": "renodx-honkai-starrail.addon64"
  },
  "rules": [
    { "match": "*.addon64", "to": "reshade-shaders/Addons", "flatten": true }
  ]
}
```

`rules` 语义：

| 字段 | 含义 |
| --- | --- |
| `match` | 相对压缩包根的 glob。`**` 跨层级，`*` 单层，`?` 单字符 |
| `to` | 落到 HoYoShade 目录下的相对路径 |
| `flatten` | `true` 只保留文件名；`false` 保留 `match` 里第一个通配符之前的字面前缀之下的目录结构 |
| `rename` | 重命名（不带扩展名时自动沿用原扩展名） |
| `optional` | 匹配不到时忽略（默认 `true`）；设成 `false` 则匹配不到就整单失败 |

规则按顺序匹配，**第一条命中的生效**。没有规则匹配的文件不会被安装。

### 目前内置的条目

| id | 内容 | 来源 |
| --- | --- | --- |
| `renodx.hkrpg` | RenoDX 的星穹铁道 HDR tonemap addon | `clshortfuse/renodx` 的 nightly Release，资产 `renodx-honkai-starrail.addon64` |
| `hoyoshade.presets` | 官方预设合集（GI / HSR / ZZZ / Universal） | `HoYoShade-Dev/HoYoShade.Presets` 的 codeload zip |

> RenoDX 目前**只覆盖星铁**这一款 miHoYo 游戏（原神/绝区零没有对应资产），所以目录里只有它一个游戏插件。

---

## 5. 代码结构

```
src/HoYoShadeHub.Extensions/          ← net10.0 类库，不依赖 WinUI，可单独测试
├── GlobMatcher.cs                    glob → 正则；sha256 / 路径越界防护等工具
├── Models/                           Manifests、Source、Rules、Installed ledger、ShadeHost、GithubRelease
├── Networking/HysxHttp.cs            复用 Core 的 DohService（DoH / ECH / 连接回退与 Hub 一致）+ 代理前缀
└── Services/
    ├── ExtensionPackageFetcher.cs    解析 source → 本地载荷（zip 解压 / 单文件）
    ├── GithubReleaseResolver.cs      Release 列表 + tagPattern/assetPattern 选资产
    ├── DownloadService.cs            带进度 + sha256 校验的下载
    ├── InstalledExtensionStore.cs    账本读写（损坏自动留档）
    ├── ReShadeIniService.cs          只增不改地补 [ADDON] AddonPath
    ├── ExtensionInstaller.cs         规则 → 落位计划 → 写盘 → 记帐 → 回滚
    ├── ExtensionCatalogService.cs    内置 / 远程 / 本地目录合并（同 id 覆盖）
    ├── ShadeHostLocator.cs           定位 <游戏目录>\HoYoShade
    └── ExtensionManagerService.cs    门面：状态、安装、卸载、查更新、体检
```

UI：`src/HoYoShadeHub/Features/Extensions/ExtensionManagerWindow.xaml(.cs)`

自测宿主：`src/HoYoShadeHub.Extensions.Tests/`

```powershell
dotnet run --project src\HoYoShadeHub.Extensions.Tests -c Release
```

它会在临时目录里搭一个假的 HoYoShade（假的 `ReShade64.dll`、Hub 风格的 `ReShade.ini`、用户自己放的 fx / 预设），
跑完整的安装 → 冲突保护 → 卸载流程，共 39 条断言。重点覆盖「不该删的东西不能删」：

- 用户自己的 `UserOwn.fx`、`MyOwnPreset.ini` 在卸载后必须还在；
- 被扩展覆盖过的既有文件，卸载时只摘账本、不删文件；
- 安装后被手工改过（哈希对不上）的文件，卸载时跳过；
- 同一文件被两个扩展同时声明时，第二个安装直接失败。

---

## 6. 上机验证

还没在真机上跑过。第一次上机建议按这个顺序：

1. 启动 Hub → 设置 → 工具箱，应该多出一个「扩展管理」磁贴。
2. 点进去，顶部应显示当前区服的 `<游戏目录>\HoYoShade` 路径。
   - 如果显示「未找到 HoYoShade 目录」，用右上角「指定目录」手动选到 `HoYoShade` 那一层。
3. 如果 ReShade.ini 缺 `[ADDON] AddonPath`，会弹一条黄色提示 —— 这是**预期行为**，说明 Bug 确实存在。
4. 点 `renodx.hkrpg` 的「安装」，看下载进度、`.hysx\installed.json`、`ReShade.ini` 的变化。
5. 开 ReShade 日志确认 addon 被扫到（见第 2 节）。
6. 点「卸载」，确认 `renodx-honkai-starrail.addon64` 被删、`reshade-shaders` 里其它东西没动。

---

## 7. 已知未做

- **Hub 自更新走 GitHub**：`MetadataClient.API_PREFIX` 目前硬编码指向
  `https://cdn.cf.storage.hub.hoyosha.de/release`，改成可配置的通道（官方 / 本仓库 GitHub Release）是下一步。
- **扩展自动更新**：`ExtensionManagerService.CheckUpdateAsync` 已经能拿到远端 tag，但 UI 还没接「有新版本」徽标和批量更新。
- **本地化**：新界面用中文字面量，`ToolboxItem` 走 `useResourceKey: false`，没有改 `HoYoShadeHub.Language` 的 resx。
  要接 Crowdin 的话需要补资源键。
- **签名 / 白名单**：从 GitHub 拉任意 DLL 进游戏进程，目前只有 sha256（且 GitHub Release 大部分没有 `digest` 字段）。
  真要防投毒得引入仓库白名单。
- **OpenHoYoShade**：引擎支持（`ShadeHostKind`），UI 只暴露 HoYoShade。
