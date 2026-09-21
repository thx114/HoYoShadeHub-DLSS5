# HoYoShade-Hub 扩展管理器 —— 设计方案

> 目标：为 [HoYoShade-Hub](https://github.com/DuolaD/HoYoShade-Hub) 增加一套「扩展（Addon / 着色器 / 预设）管理 + 框架更新 + 插件市场」能力，
> **不改动 HoYoShade-Hub 本体**。
>
> 对标参考：[RHI — ReShade HDR Installer](https://github.com/RankFTW/RHI)（C# / .NET，`manifest.json` 驱动的一键安装器）

---

## 1. 结论先说

| 需求 | 能否在不改 Hub 的前提下做到 | 做法 |
| --- | --- | --- |
| 在客户端内添加/删除扩展 | ✅ 可以 | 直接读写 `<游戏目录>\HoYoShade\`（或 `OpenHoYoShade\`）下的 `reshade-shaders\` 与 `Presets\` |
| 从 GitHub 更新客户端 | ✅ 可以 | 复用 `DuolaD/HoYoShade` 的 Release 流，安装后回写 Hub 的 `hoyoshade_manifest.json` 保持状态一致 |
| 从 GitHub 下载插件（对标 RHI） | ✅ 可以 | 自带 catalog + 任意 GitHub 仓库解析，安装到 `reshade-shaders\Addons` |

**唯一做不到的**：把新页面「画进」Hub 的导航栏里 —— Hub 没有插件系统，`ToolboxSetting.xaml` 是硬编码的 `GridView`，
`AppConfig.BuildServiceProvider()` 里也没有任何外部程序集加载点。
所以扩展自己的 UI 必须是**独立进程**（托盘常驻 / 独立窗口 / CLI），或者后期接受一个 ~20 行的 fork 补丁把入口挂进 Toolbox。

---

## 2. 事实依据（对 HoYoShade-Hub `main` 分支源码的核查）

### 2.1 部署布局

`src/HoYoShadeHub.RPC/HoYoShadeInstall/HoYoShadeInstallService.cs`

```
<游戏根目录>\                     ← base_path
├── HoYoShade\                    ← install_target = HoYoShadeOnly
│   ├── ReShade64.dll
│   ├── inject.exe                ← 注入器（不是把 dll 丢进游戏 exe 目录！）
│   ├── ReShade.ini               ← Hub 写入 EffectSearchPaths / TextureSearchPaths
│   ├── reshade-shaders\
│   │   ├── Shaders\              ← .fx
│   │   ├── Textures\
│   │   └── Addons\               ← *.addon64 / *.addon32 / *.addon
│   └── Presets\                  ← 预设
└── OpenHoYoShade\                ← install_target = OpenHoYoShadeOnly
    └── ... 同上
```

- `L620 / L624`：`Path.Combine(basePath, "HoYoShade")` / `"OpenHoYoShade"` —— `Both` 时两边都装。
- `L860 / L864`：着色器包落到 `<目标>\reshade-shaders\Shaders`、`Textures`。
- `L989 / L1041`：addon 二进制统一改名成 `<name>.addon64` 放进 `<目标>\reshade-shaders\Addons`。
- `L1113 WriteSearchPaths`：Hub **只在 ReShade.ini 不存在时**创建它，内容为

  ```ini
  [GENERAL]
  EffectSearchPaths=.\reshade-shaders\Shaders\**
  TextureSearchPaths=.\reshade-shaders\Textures\**
  ```

  ⚠️ 注意：**没有写 `[ADDON] AddonPath`**。见下一节 —— 这导致 Hub 装的 addon 实际不生效。

### 2.1.1 addon 到底怎么加载（已从 ReShade 源码确认）

`crosire/reshade` → `source/addon_manager.cpp`：

```cpp
// Get directory from where to load add-ons from
std::filesystem::path addon_search_path = g_reshade_base_path;
if (config.get("ADDON", "AddonPath", addon_search_path))
        addon_search_path = g_reshade_base_path / addon_search_path;

log::message(log::level::info, "Searching for add-ons (*.addon64) in '%s' ...", ...);

// 对 addon_search_path 做【非递归】directory_iterator，匹配 *.addon / *.addon32 / *.addon64
```

三个结论：

1. ini 键是 **`[ADDON] AddonPath=`**，值是**单个路径**。
   全文检索 ReShade 源码，`AddonSearchPath`（复数 / 搜索路径列表）**这个字符串根本不存在**。
2. 路径相对 `g_reshade_base_path`，也就是 ReShade 主 DLL 所在目录 = `<游戏目录>\HoYoShade`。
3. 扫描**非递归**，addon 必须直接躺在该目录里。

Hub 把 addon 放进 `reshade-shaders\Addons\` 却从不写这个键 → ReShade 只扫 `HoYoShade` 根目录 → **addon 不加载**。
这是我们这台扩展管理器必须补的一课，也是「凭源码就能确定的、目前最值钱的一条修正」。

### 2.2 框架更新的行为（决定扩展会不会被冲掉）

`L356`：`CopyDirectory(tempExtractPath, targetPath, true, ...)` —— **是叠加覆盖，不是镜像同步**，全程没有清空目标目录。

实测 `OpenHoYoShade-V3.0.0-Beta.9.zip` 的内容：

```
inject.exe / inject_mod.cpp / InjectResource / LauncherResource / LICENSE
Presets\                ← 有内容，受 presets_handling 三策略保护
reshade-shaders\        ← Addons\ / Shaders\ / Textures\  三个【空目录】占位
ReShade64.dll / ReShade_LICENSE / ScreenShot / Starter(*).bat
ReShade.ini             ← 不在包里，由 Hub 生成
```

结论：**框架更新不会删除我们装进 `reshade-shaders` 和 `Presets` 的扩展**。
唯一要小心的是 `presets_handling`：

| 值 | 含义 | 对扩展的影响 |
| --- | --- | --- |
| 0 | Overwrite（默认） | `Presets\` 被包的版本覆盖 |
| 1 | KeepExisting | 保留现有 `Presets\` |
| 2 | SeparateFolder | 包的预设进 `Presets\<版本号>\` |

我们的更新器要暴露这个选项，并在执行 `Presets` 覆盖前**自动备份**到 `.hysx/backup/`。

### 2.3 版本状态记录

`src/HoYoShadeHub.Core/HoYoShade/HoYoShadeVersionService.cs` —— Hub 把当前版本写在

```
<AppConfig.UserDataFolder>\hoyoshade_manifest.json
```

```json
{
  "HoYoShade":     { "version": "V3.0.0-Beta.9", "installed_at": "...", "source": "github_release", "sha256": "..." },
  "OpenHoYoShade": { "...": "..." }
}
```

我们更新完框架后**回写这个文件**，Hub 打开时就认为状态一致，不会提示重装。这是「不改 Hub 还能协同」的关键。

### 2.4 定位 Hub 与游戏

`src/HoYoShadeHub/AppConfig.cs`

- `ConfigPath`：便携版在 `<安装目录>\config.ini`；安装版在 `%APPDATA%\HoYoShadeHub\config.ini`。
- `config.ini` 里有 `Language=` 和 `UserDataFolder=`（相对或绝对路径）。
- `UserDataFolder` 缺省 = `%LOCALAPPDATA%\HoYoShadeHub`，DB 为 `HoYoShadeHubDatabase.db`。
- 游戏安装路径：`AppConfig.GetGameInstallPath(gameBiz)` → 存在 DB 的 `Setting` / `KVT` 表；
  另有 `miHoYo` 注册表键（`src/HoYoShadeHub.Core/GameRegistry.cs`）可作兜底。

**定位优先级**（我们的发现器按这个顺序来，每步都能被用户手动覆盖）：

1. `%APPDATA%\HoYoShadeHub\config.ini` → `UserDataFolder`
2. `<Hub 安装目录>\config.ini`（便携版）
3. `UserDataFolder\HoYoShadeHubDatabase.db` → `Setting` 表里的游戏路径
4. `HKCU\Software\miHoYo\*` / `HKCU\Software\Cognosphere\*` 注册表兜底
5. 「扫盘找 `HoYoShade\ReShade64.dll`」兜底（暴力但有效）

### 2.5 Hub 的既有能力（我们不要重复造）

- `ReShadePackageModels.cs`：Hub 已经会解析官方 `crosire/reshade-shaders` 的
  `EffectPackages.ini` / `Addons.ini`（`ReShadeDownloadServer` 常量）。
  → 我们的目录里可以直接**引用同一个源**，用户视角是「多了一批非官方插件」。
- `Features/Update/UpdateService.cs`：Hub 自更新走 GitHub Release。
- `docs/UrlProtocol.md`：Hub 只注册了 `hoyoshadehub://startgame/...` 和 `playtime/...` 两个动作，
  **不能**用来注册第三方动作。想联动得靠我们自己起进程。

---

## 3. 架构

```
HYSX.sln
├─ src/HYSX.Core/            net8.0 类库，无 UI，CLI / GUI / 未来的 Hub 补丁共用
│   ├─ Discovery/            HubLocator（config.ini+DB+注册表）、GameLocator、ShadeTargetLocator
│   ├─ Hub/                  HubManifestService（读写 hoyoshade_manifest.json）
│   │                        ReShadeIniService（合并 EffectSearchPaths/AddonSearchPaths，保留用户其它键）
│   ├─ Extensions/           IExtensionPackage、ExtensionInstaller、ExtensionUninstaller
│   │                        InstalledRegistry（.hysx/installed.json，精确回滚）
│   ├─ Catalog/              ICatalogSource：
│   │                          · BuiltinCatalogSource（读本仓库 catalog.json）
│   │                          · GithubReleaseCatalogSource（owner/repo + asset 匹配）
│   │                          · LocalFileCatalogSource（本地 zip / 文件夹，给作者自测）
│   │                          · ReshadeOfficialCatalogSource（复用 crosire 的 ini）
│   ├─ Updater/              FrameworkUpdater（Release 解析 → sha256 校验 → 解压 → presets 策略 → 回写 manifest）
│   ├─ Networking/           Downloader（分块、断点、代理、进度、并发）、Sha256、ZipExtractor
│   └─ Config/               HysxConfig（用户偏好、代理、备份策略）
├─ src/HYSX.Cli/             dotnet tool 风格：hysx list / install <id> / remove <id> / update / doctor
└─ src/HYSX.App/             WinUI3（与 Hub 同栈）或 WPF 的窗口 + 托盘
```

### 3.1 扩展包清单格式

官方 catalog（跟随本仓库发布，等价于 RHI 的 `manifest.json`）：

```jsonc
{
  "schema": 1,
  "updatedAt": "2026-01-01T00:00:00Z",
  "extensions": [
    {
      "id": "renodx.genshin",
      "name": "RenoDX for Genshin Impact",
      "version": "1.2.0",
      "author": "clshortfuse",
      "description": "把原神的 tonemap 换成 HDR 路径",
      "tags": ["hdr", "tonemap"],
      "gameBiz": ["hk4e_cn", "hk4e_global", "hk4e_bilibili"],
      "targets": ["HoYoShade", "OpenHoYoShade"],
      "source": {
        "type": "github-release",
        "repo": "clshortfuse/renodx",
        "asset": "*.addon64",
        "tagRegex": "^v?\\d+\\.\\d+"
      },
      "rules": [
        { "match": "**/*.addon64", "to": "reshade-shaders/Addons" },
        { "match": "**/*.fx",      "to": "reshade-shaders/Shaders" },
        { "match": "**/*.png",     "to": "reshade-shaders/Textures" },
        { "match": "*.ini",        "to": "Presets", "optional": true }
      ],
      "links": { "homepage": "...", "issues": "..." }
    }
  ]
}
```

单个扩展也可以单独发布成一个 zip（`.hysx`），结构：

```
manifest.json      ← 上面那个对象（去掉外层 extensions 包装）
payload/           ← 要安装的文件（或 source 为 github-release 时留空）
```

### 3.2 精确卸载（重点）

不能靠「删目录」卸载——用户自己放进去的 fx 会一起没。

`<目标>\reshade-shaders\.hysx\installed.json`：

```json
{
  "renodx.genshin": {
    "version": "1.2.0",
    "installedAt": "2026-01-01T12:00:00Z",
    "source": { "type": "github-release", "repo": "clshortfuse/renodx", "tag": "v1.2.0" },
    "files": [
      { "path": "reshade-shaders/Addons/RenoDXGenshin.addon64", "sha256": "…", "size": 123456 }
    ]
  }
}
```

卸载时：**逐个校验 sha256 后再删**——文件被改过（比如用户手动换了版本）就保留并提示，绝不误删。
扩展留下的空目录顺手清理，但只清理我们记录的路径。

### 3.3 更新框架的流程

```
1. GET /repos/DuolaD/HoYoShade/releases?per_page=20
   → 取 tag 形如 V3.x / 语义化版本、按 published_at 倒序（含/不含 prerelease 由用户开关控制）
2. 从 Hub manifest 读出当前已装版本做比较（NuGet.Versioning，和 Hub 同款）
3. 下载 <Framework>-<tag>.zip 与同名 .sha256，校验
4. 解压到临时目录，按 presetsHandling 处理 Presets\
5. 目标目录 = <游戏目录>\HoYoShade 或 OpenHoYoShade
   写前快照 → 覆盖拷贝 → 失败则回滚
6. 回写 <UserDataFolder>\hoyoshade_manifest.json
7. 若游戏在运行 → 拒绝执行（和 Hub 一样检查）
```

代理支持：和 Hub 一致，允许 `https://ghproxy.../https://api.github.com/...` 这种前缀式代理，
也支持我们自己的 `%HTTPS_PROXY%`。

---

## 4. 分阶段落地

| 阶段 | 内容 | 交付物 |
| --- | --- | --- |
| P0 | `HYSX.Core` 骨架：`HubLocator` + `ShadeTargetLocator` + `doctor` 命令 | `hysx doctor` 能打印出「找到了哪个游戏、HoYoShade 装在哪、当前版本、已装扩展」 |
| P1 | `ExtensionInstaller/Uninstaller` + `installed.json` + 本地 zip 源 | `hysx install ./mypack.hysx` / `hysx remove <id>` 可用 |
| P2 | `FrameworkUpdater`（GitHub Release + sha256 + presets 策略 + 回写 manifest） | `hysx update --framework` 可用 |
| P3 | 目录源：内置 catalog + 任意 GitHub 仓库解析 | `hysx search` / `hysx install renodx.genshin` |
| P4 | GUI（WinUI3 窗口 + 托盘），一键安装/更新/回滚 | 可发布的 `HYSX.App` |
| P5（可选） | fork 补丁：Hub 的 `ToolboxSetting.xaml` 加一项，点了启动 `HYSX.App` | 在 Hub 里能直接进 |

P0–P3 全部不依赖 Hub 内部任何代码，只用它的**文件格式**当接口。

---

## 5. 已定决策与进度

### 已定

1. **「客户端」= HoYoShade-Hub** → 管理界面做进 Hub（设置 → 工具箱 → 扩展管理）。
   代价是必须 fork；我们把它压到最小 diff（见 §6）。
2. **安装目标 = 只装 `HoYoShade`**（`OpenHoYoShade` 引擎层已支持，UI 不暴露）。
3. **技术栈 = .NET 10 + WinUI 3 + CommunityToolkit.Mvvm**，与 Hub/RHI 同栈。
   核心逻辑单独一个 `HoYoShadeHub.Extensions` 项目，**不依赖 WinUI**，可以脱离 Hub 单独测试。
4. **插件来源 = 内置目录 + 远程目录 + 任意 GitHub 仓库 + 本地包**。

### 进度

| 阶段 | 状态 |
| --- | --- |
| P0 `ShadeHostLocator` + 体检 | ✅ 完成 |
| P1 安装 / 卸载 + 账本 + 本地包 | ✅ 完成，端到端 39 项断言全通过 |
| P2 GitHub Release 解析 + sha256 | ✅ 完成（框架自更新待做） |
| P3 目录（内置 + 远程 + 自定义仓库） | ✅ 引擎完成，远程目录地址待填 |
| P4 UI（扩展管理窗口 + Toolbox 入口） | ✅ 完成并通过编译 |
| P5 真机验证 | ⬜ 未做 |
| P6 Hub 从 GitHub 自更新 | ⬜ 未做，见 §7 |

详见 [EXTENSIONS.md](./EXTENSIONS.md)。

---

## 6. 相对上游的改动清单（fork diff）

新增（上游没有，升级时不会冲突）：

```
src/HoYoShadeHub.Extensions/**                 整个新项目
src/HoYoShadeHub/Features/Extensions/**        扩展管理窗口
NuGet.config                                   修机器级 fallback 包目录导致的 NU1301
docs/DESIGN.md, docs/EXTENSIONS.md             文档
```

改动（升级合并时只需盯这 4 个文件）：

| 文件 | 改动 |
| --- | --- |
| `HoYoShadeHub.sln` | 加入新项目 |
| `src/HoYoShadeHub/HoYoShadeHub.csproj` | 加一行 `ProjectReference` |
| `src/HoYoShadeHub/Features/Setting/ToolboxSetting.xaml.cs` | 加一个磁贴 + 一个分支（约 8 行） |
| `global.json` | 加 `"rollForward": "latestFeature"`，让 10.0.4xx 也能构建 |

---

## 7. 下一步

1. **Hub 从 GitHub 自更新**：`MetadataClient.API_PREFIX` 目前硬编码
   `https://cdn.cf.storage.hub.hoyosha.de/release`，`release_info_stable.json` + `manifest_*.json` 都是官方 CDN 的私有格式。
   做法：把 API 前缀抽成可配置的「更新通道」，再加一个直接从 `github.com/<repo>/releases` 拉整包 zip 的通道。
2. **扩展自动更新**：`CheckUpdateAsync` 已能拿到远端 tag，UI 需要「有新版本」徽标 + 一键更新全部。
3. **真机验证**（尤其是 `[ADDON] AddonPath` 那条）。
4. **本地化**：接 `HoYoShadeHub.Language` 的 resx / Crowdin。

