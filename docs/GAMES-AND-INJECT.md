# 游戏发现 + 多游戏 ReShade.ini 管理 + 注入模式 —— 规格

配合 [RESHADE-INI.md](./RESHADE-INI.md)（那里是 ReShade.ini 的解析规则）。

> **接手这个仓库先看 [AI-HANDOFF.md](./AI-HANDOFF.md)** —— 那是给下一个人/另一个 AI 的交接文档：环境与命令、代码地图、本分叉做了什么、最近一轮工作的「问题→根因→改法」、以及待办。本文（尤其 §8.x）是逐条实施记录，改完东西记得往下加一节。

---

## 1. 游戏条目模型

一切的前提是先有一个「游戏」概念。它需要同时喂三个东西：插件开关（要 ReShade.ini 路径）、
启动（要 exe 路径）、注入（要进程名）。

```csharp
public sealed class GameEntry
{
    public string Id { get; }              // 稳定标识，用于持久化勾选状态
    public string DisplayName { get; set; }
    public string? ExePath { get; set; }   // 用户选的 exe 全路径
    public string? GameDirectory => Path.GetDirectoryName(ExePath);
    public string? ProcessName  => Path.GetFileName(ExePath);        // → inject.exe 的参数
    public string? ReShadeIniPath => GameDirectory is null ? null
                                   : Path.Combine(GameDirectory, "ReShade.ini");
    public bool HasReShadeIni => ReShadeIniPath is not null && File.Exists(ReShadeIniPath);
    public GameBiz? Biz { get; set; }      // Hub 已知的游戏才有
    public bool IsCustom { get; set; }     // 用户手动加的
    public bool UseInjectMode { get; set; }// 这个游戏走不走注入模式
}
```

---

## 2. 游戏发现

### 2.1 手动添加（**用户明确要求**）

「添加自定义游戏」→ 文件选择器选一个 `*.exe` → 得到 `ExePath`。

**比 Hub 现有的「自定义注入」好在哪**：现有 `CustomInjectDialog` 要手输进程名（`GenshinImpact.exe`），
只能用于注入；选了 exe 之后我们额外白拿到游戏目录 → 于是能顺带定位 `ReShade.ini`，
插件开关和注入就共用同一份数据了。

选完之后校验：
- 同目录下有 `ReShade.ini` → 标记 ✅ 可直接管插件
- 没有 → 提示「这个目录里没有 ReShade.ini，可能没装 HoYoShade/ReShade」

### 2.2 自动发现

用户给的三份样本都在「启动器 + games 子目录」结构里：

```
D:\APPS\miHoYo Launcher\games\ZenlessZoneZero Game\        → 绝区零
D:\APPS\miHoYo Launcher\games\Genshin Impact Game\         → 原神
D:\APPS\Star Rail\games\Star Rail Game\                    → 星铁
D:\WeGameApps\rail_apps\蓝色星原：旅谣(2002738)\            → 蓝色星原
```

策略：
1. **Hub 已知游戏**：用 `GameLauncherService.GetGameInstallPath(GameBiz)` 拿到安装目录，
   目录下有 `ReShade.ini` 就是一个游戏条目（进程名取该目录里的主 exe）。
2. **扫配置的根目录**：递归深度 2~3 层找 `ReShade.ini`，找到就把**同目录里的正主 exe** 当游戏。
   - 哪些根目录要扫 → **待定，见 §5**
3. 结果是并集，按 `ReShade.ini` 全路径去重。

> ⚠️ 不要在游戏目录里瞎猜 exe。以「哪个 exe 是该游戏的」为准的判断：
> 优先用 Hub 已知的 exe 名；自定义条目直接用用户选的那个；扫描时如果目录里只有一个 exe 就用它，
> 多个 exe 时列出来让用户选。

---

## 3. 多游戏 ReShade.ini 管理界面

插件的**启用/禁用是按游戏分开的**（`DisabledAddons` 在每个游戏的 ini 里），
插件**文件是全局一份**（`AddonPath` 都指向同一个目录）。所以界面要体现这个层次。

```
插件                                        [刷新] [添加游戏] [指定目录]
全局插件目录：D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Addons\   （6 个 addon）
┌────────────────┬──────────────────────────────────────────────────┐
│ 游戏            │  插件矩阵                                        │
│ ─────────────  │  ┌────────────────────────┬──────┬──────┬──────┐ │
│ ☑ 原神      ✅ │  │ 插件                    │ 原神 │ 星铁 │ 绝区 │ │
│ ☑ 星铁      ✅ │  ├────────────────────────┼──────┼──────┼──────┤ │
│ ☑ 绝区零    ✅ │  │ RenoDX DLSS             │  ☑   │  ☐   │  ☐   │ │
│ ☐ 蓝色星原  ✅ │  │ RenoDX DLSS_A (super…)  │  ☐   │  ☐   │  ☑   │ │
│                │  │ RenoDX DLSS (ShortFuse) │  ☑   │  ☑   │  ☐   │ │
│ [+ 添加游戏]   │  │ DLSS 5 Bridge           │  ☑   │  ☑   │  ☑   │ │
│                │  └────────────────────────┴──────┴──────┴──────┘ │
└────────────────┴──────────────────────────────────────────────────┘
        每列顶部：LoadFromDllMain / DirectNeuralRenderingHookPoint 的高级设置入口
```

- 勾选 = 写/删那个游戏 ini 的 `DisabledAddons`
- 禁用的两种手段要区分开：
  - **按游戏**：写 `DisabledAddons`（矩阵里勾选框）
  - **全局**：重命名 `.addon64 ↔ .addon64x`（矩阵外的单独一列/按钮，会明确警告"影响所有游戏"）
- 版式上「一个游戏一列」在游戏多了以后会撑不下 → 也可以做成「左边选游戏，右边列该游戏的插件开关」。
  **两种都要能做，默认哪种待定，见 §5。**

### 每游戏的高级设置

| 键 | 控件 | 约束 |
| --- | --- | --- |
| `LoadFromDllMain` | 每个插件一个勾选（该游戏的 ini 里那一串） | **必须保留空槽位**，不能过滤 |
| `DirectNeuralRenderingHookPoint` | 下拉：`off`(0) / `1` / `2` / `3` / `4` | 0 显示成 `off`；1-4 **不做别名**；只有 super-anus 或 renodx-dlss(ShortFuse) 装了才可改 |

---

## 4. 注入模式

### 4.1 现有机制（复用，不重造）

`Features/GameLauncher/InjectorHelper.cs`：

```csharp
var startInfo = new ProcessStartInfo {
    FileName = injectExePath,        // <shadePath>\inject.exe
    Arguments = gameExeName,         // 进程名
    WorkingDirectory = shadePath,    // HoYoShade 目录
};
```

`inject.exe` 拿进程名，等那个进程起来后注入。现有入口：**设置 → 文件管理 → 自定义注入**
（`CustomInjectDialog`，要手输进程名）。

### 4.2 需求

- 启动按钮**左边**加一个「注入模式」开关
- **全游戏生效**（一个全局开关，不是每游戏一个）
- 勾了注入模式的游戏才走注入（「如果是原什么的打勾了，就唤起自带的那个注入 bat，注入对应游戏」）

### 4.3 实现方式（已定）

**不唤起 bat，直接调 `inject.exe`。**

`简体中文启动器.bat`（1188 行）的「额外步骤」只有两个：

1. **提权** —— `fltmc` 检测，不是管理员就用 PowerShell `Start-Process -Verb RunAs` 自我提权重启
2. **跑一次 `LauncherResource\INIBuild.exe`** —— 生成/修补 `ReShade.ini`（那些绝对路径就是它写的）

注入本体和 Hub 完全一样：

```bat
start "" /wait /b inject.exe YuanShen.exe
start "" /wait /b inject.exe GenshinImpact.exe
start "" /wait /b inject.exe StarRail.exe
start "" /wait /b inject.exe ZenlessZoneZero.exe
```

Hub 的 `InjectorHelper` 比 bat 那套更好（等 `HOYOSHADE_READY:9999` 标记、解析 1001-1004 错误码、不阻塞挂窗口），所以复用 Hub 的实现，不唤起 bat。

bat 内部列出的完整进程名表，当内置映射用：

| 游戏 | 进程名 |
| --- | --- |
| 原神（国服） | `YuanShen.exe` |
| 原神（国际） | `GenshinImpact.exe` |
| 崩坏3 | `BH3.exe` |
| 星铁 | `StarRail.exe` |
| 绝区零 | `ZenlessZoneZero.exe` |
| 测试服 | `Genshin.exe` / `ZZZ.exe` / `ZenlessZoneZeroBeta.exe` / `NexusAnima.exe` / `PetitPlanet.exe` |

流程：

```
「注入模式」开关（开始游戏左侧，只影响当前游戏）→ 存进该游戏条目
点开始游戏时若开了注入模式：
  1. ReShade.ini 缺失 → 先跑一次 LauncherResource\INIBuild.exe
  2. InjectorHelper.StartAndWaitForReadyAsync(
         injectExePath: <HoYoShade>\inject.exe,
         gameExeName  : 该游戏进程名,
         shadePath    : <HoYoShade>)
```

已知限制：bat 会提权，我们不做。游戏本身是管理员启动时注入会失败。

---

## 5. 已定（用户答复）

| 问题 | 答复 |
| --- | --- |
| 扫哪些根目录 | **不做扫描**。复用 Hub 现有游戏检测；只要给已知游戏加「目录里有 `ReShade.ini` 就纳入插件管理」 |
| 界面版式 | **不做矩阵**。改成**每个游戏一个插件页面**；再加一个**全局**页面，放**左下角「设置」上方**（= `NavigationView.FooterMenuItems`） |
| 注入 bat | 见 §4.3 |
| 注入模式粒度 | **只影响单个游戏**（按钮在开始游戏左侧） |
| 没有 ReShade.ini 的自定义游戏 | **允许添加**，只用于启动/注入，插件页显示「该游戏没有 ReShade.ini」 |

---

## 6. 实施计划（文件清单，照着开工即可）

### 第 1 步：GameEntry + 添加自定义游戏 + 已知游戏探测

**新增**

| 文件 | 内容 |
| --- | --- |
| `src/HoYoShadeHub.Extensions/Games/GameEntry.cs` | `Id` / `DisplayName` / `ExePath` / `GameDirectory` / `ProcessName` / `ReShadeIniPath` / `HasReShadeIni` / `Biz` / `IsCustom` / `UseInjectMode`。后四个是**派生属性**，不要单独存 |
| `src/HoYoShadeHub.Extensions/Games/GameEntryStore.cs` | 持久化到 `<UserDataFolder>\.hysx\games.json`。只存自定义游戏 + 用户状态（`UseInjectMode`）；自动发现的每次重算 |
| `src/HoYoShadeHub.Extensions/Games/GameDiscoveryService.cs` | `AddCustom(exePath)` 校验并生成条目；`DiscoverKnown(...)` 吃 Hub 的游戏列表；按 `ReShadeIniPath` 去重 |
| `src/HoYoShadeHub.Extensions/Games/KnownProcessNames.cs` | §4.3 那张进程名表 |

**复用**：`ReShade.ReShadeProfile`、`Services.ShadeHostLocator`（都已完成）

**修改**：`src/HoYoShadeHub/Features/Plugins/PluginManagerPage.xaml.cs` —— 本步只引入游戏概念，UI 留到第 2 步

**验收**：能选 exe 添加自定义游戏；已知游戏自动出现；每条能报出 `ReShadeIniPath` 在不在

---

### 第 2 步：每游戏插件页 + 全局插件页

**新增**

| 文件 | 内容 |
| --- | --- |
| `.../Features/Plugins/GamePluginPage.xaml(.cs)` | 左导航「插件」的目标页。按**当前选中游戏**过滤；没有 `ReShade.ini` 走空状态；列表 = 该游戏 `AddonPath` 下的 addon + 勾选框（写 `DisabledAddons`）；高级区放 `LoadFromDllMain` 勾选和 `DirectNeuralRenderingHookPoint` 下拉（`off`/1/2/3/4，只有 super-anus 或 renodx-dlss(ShortFuse) 装了才可改） |
| `.../Features/Plugins/GlobalPluginPage.xaml(.cs)` | 全局视角：`AddonPath` 目录里所有 addon（不分游戏）、版本、安装/删除。现 `PluginManagerPage` 的内容基本搬过来 |

**修改**

| 文件 | 改动 |
| --- | --- |
| `.../Features/ViewHost/MainView.xaml` | 「插件」item 留在 `MenuItems`（改指 `GamePluginPage`）；新增 `<NavigationView.FooterMenuItems>` 放「全局插件」，它会自动排在左下角设置项上方 |
| `.../Features/ViewHost/MainView.xaml.cs` | `ItemInvoked` 加两个 case；`NavigateTo` 里 `SupportedPages` 白名单例外补上这两个页面名（现在只放行 `SettingPage` + `PluginManagerPage`） |
| `.../Features/Plugins/PluginManagerPage.*` | 拆成 `GlobalPluginPage`，原安装/删除/本地安装逻辑保留 |

**坑（详见 §3 和 RESHADE-INI.md §2）**：`@` 必须有；`LoadFromDllMain` 空槽位必须保留；`HookPoint=0` 要写键不能删；往返不能丢 `[STYLE]`/`[OVERLAY]`

**验收**：同一个插件在 A 游戏关、B 游戏开，两份 ini 各自正确

---

### 第 3 步：INIBuild + 注入模式接进启动流程

**新增**

| 文件 | 内容 |
| --- | --- |
| `src/HoYoShadeHub.Extensions/ReShade/ReShadeIniBuilder.cs` | 调 `<HoYoShade>\LauncherResource\INIBuild.exe` 并等它退出。仅在目标游戏 `ReShade.ini` 缺失时自动跑，也可手动触发（对应 bat 的 `:ini_Reset`） |

**修改**

| 文件 | 改动 |
| --- | --- |
| `.../Features/GameLauncher/GameLauncherPage.xaml` | 开始游戏按钮**左侧**加「注入模式」`ToggleButton`，读写当前 `GameEntry.UseInjectMode` |
| `.../Features/GameLauncher/GameLauncherPage.xaml.cs` | 开始游戏分支：`UseInjectMode` 为真 → `ReShadeIniBuilder.EnsureAsync()` → `InjectorHelper.StartAndWaitForReadyAsync(...)`；否则走原流程。以 `LaunchShaderInjectorOnlyAsync`（1492 行）为模板 |
| `.../Features/Plugins/GamePluginPage.xaml(.cs)` | 同步显示注入模式开关（可选） |

**复用**：`Features.GameLauncher.InjectorHelper` / `InjectorErrorCodes`

**已知限制**：不提权，游戏以管理员启动时注入会失败

**验收**：勾上注入模式 → 点开始游戏 → `inject.exe <进程名>` 起来并打印 `READY_MARKER`

---

## 7. 当前状态

| 项 | 状态 |
| --- | --- |
| ReShade.ini 读写引擎 | ✅ 完成（含真机 DLL 实测） |
| 内置插件目录（7 条 / 6 个 GitHub 源） | ✅ 完成，联网校验通过（含 thx114/hoyodlss5）|
| 插件安装删除 + 账本 + 互斥 | ✅ 完成 |
| 游戏条目 / 发现 / 持久化 | ✅ 完成 |
| 每游戏插件页（「插件」）| ✅ 完成（切游戏用顶部游戏列表） |
| 全局插件页（左下角，设置上方）| ✅ 完成（含改名式全局开关） |
| 自定义游戏进顶部游戏列表 | ✅ 完成（合成 GameBiz） |
| 注入模式（启动按钮左侧 + INIBuild）| ✅ 完成 |
| 左侧「DLL 配置」页（dlssnr / streamline 下载安装）| ✅ 完成 |
| 缺运行时 dll 的红黄标记 + 启动弹窗 | ✅ 完成 |
| DLSS5 插件默认进 LoadFromDllMain | ✅ 完成 |
| 定位游戏选成上层目录的兜底 | ✅ 完成（只往下看 2 层目录名）|
| 顶部图标固定 / 右键菜单（含删 HoYoShade）| ✅ 完成 |
| 自测 | ✅ 230/230；`--online` 240/240（真连 GitHub 校验每个 source）|

---

## 8. 实施记录（第 1~3 步做完后的补充事实）

### 8.1 INIBuild.exe 实测（**和工作目录无关**）

在隔离副本里跑了真的 `LauncherResource\INIBuild.exe`，结论：

1. 它写的是**自己所在目录的上一级**（= HoYoShade 根目录）的 `ReShade.ini`，
   **把 exe 放在 A、cwd 设成 B，ini 仍然落在 A** —— 所以 bat 里那个 cwd 不是关键，
   `ProcessStartInfo.WorkingDirectory` 怎么设都行（我们设成 HoYoShade 根目录，跟 bat 保持一致语义）；
2. 写出来的是**模板**，里面全是绝对路径（`EffectSearchPaths` / `PresetPath` / `AddonPath`…）；
3. `DisabledAddons` 会把 AddonPath 目录里**所有** addon 都列进去（`Slug@文件名`），
   除了 `LauncherResource\AddonWhitelist.txt` 点名的那些 —— 也就是**新装的插件默认是关的**；
4. 游戏目录里那份 ini 是 `inject.exe` 从模板复制过去的（bat 原话：「注入器会自动检测并复制配置文件
   （ReShade.ini）到游戏进程根目录」）。所以 Hub 只在**游戏 ini 不存在**时补一次模板
   （`ReShadeIniBuilder.EnsureAsync`）。

### 8.2 比计划多出来的东西

| 文件 | 为什么 |
| --- | --- |
| `Extensions/Games/GamePluginService.cs` | 把「按游戏开关插件」的逻辑从界面里拿出来，能自测（同一插件 A 关 B 开就是它测的） |
| `Extensions/ReShade/AddonFileSwitcher.cs` | 全局禁用 = 重命名 `.addon64 ↔ .addon64x`，单独成一块好测 |
| `Features/Plugins/PluginHostLocator.cs` | 两个插件页 + 启动器页共用同一份宿主定位（用户手动指定的目录也要共享） |
| `Features/Plugins/GameCatalog.cs` | Hub 侧胶水：已知游戏检测 → 发现候选；当前客户端 → 游戏条目 |
| `GlobalPluginPage` 的「插件文件」视图 | §3 要求的「改名式全局开关」得有地方放，这里会明确警告影响所有游戏 |

`PluginManagerPage` 已删除，内容原样搬进 `GlobalPluginPage`（它现在多了「插件文件」视图）。

### 8.3 界面版式（用户后来定的两条）

1. **切游戏不在插件页里做** —— 顶部那行游戏列表（`GameSelector`）本来就是干这个的：
   换游戏 → `MainView.GameSelector_CurrentGameChanged` → `UpdateNavigationView()` 用新的 GameId
   重新导航当前页 → 插件页跟着换。所以插件页**不再自带游戏列表**。
2. **插件配置单独一栏**（右侧 320px）：`DirectNeuralRenderingHookPoint`、ReShade.ini 工具。
   左边留给插件列表。以后再加插件级配置直接往这一栏里塞。
   （注入模式**不放这儿** —— 它是启动相关的东西，只在启动器页开始游戏按钮左边，样式跟 DX12 选项一致。）
3. **「添加自定义游戏」在左上角**：游戏图标行里、游戏图标的右边，一张带背景图的 `+` 磁贴。
   插件页只留「指定主程序…」（给目录里有多个 exe、需要改主程序的情况），不再有自己的添加入口。
4. **全局插件页的排版**（用户要求，空间紧张）：
   - 搜索框放「本地安装…」左边；
   - 「扩展包 / 插件文件」放一行左端，「全部 / 已安装 / 可下载」放同一行右端，
     「共 N 个插件，已安装 M 个」贴在过滤右边，上下留白收紧；
   - 标签（`hdr` / `tonemap` / `miHoYo`…）显示在插件标题右侧；
   - 卡片默认只有一行，点一下才展开描述 / 版本 / 状态 / 文件清单 / 主页；
   - 只列**会装 addon 的**扩展包（滤镜 / 预设那类先不进这个页面）。

### 8.3.1 全局开关 = 改后缀

「全局插件」页里每个插件（以及「插件文件」里每个文件）都有一个开关，动的是**文件名后缀**：

```
renodx-dlss(9.17.12).addon64      ← 开
renodx-dlss(9.17.12).addon64x     ← 关（ReShade 直接扫不到，所有游戏一起失效）
```

和「按游戏禁用」（写那个游戏 `ReShade.ini` 的 `DisabledAddons`）是两套手段，界面上明确区分：

| 手段 | 在哪 | 作用域 |
| --- | --- | --- |
| `DisabledAddons` | 左侧「插件」→ 每个游戏的开关 | 那一个游戏 |
| 改后缀 | 「全局插件」→ 插件卡片上的开关 / 「插件文件」 | **所有游戏** |

细节两条，都是踩过的坑：

1. 账本里记的是**装进去时**的文件名，改过名之后那个路径就不存在了 —— 所以找文件时
   要同时认 `.addon64` 和 `.addon64x`（`GlobalPluginPage.ResolveAddonFiles`），
   不然全局关掉之后开关会消失、版本号也读不到。
2. **账本只记 Hub 自己装的东西**。用户从 Discord / GitHub 手动放进 addons 目录的插件，
   账本里什么都没有 → 卡片会显示成「安装」（明明文件就在那儿），也没有全局开关。
   所以每个目录条目还要写 <code>addonPatterns</code>（这个插件装出来叫什么名字），
   由 `ExtensionAddonMatcher` 去 addons 目录里**认领**文件（一个文件只归第一个匹配上的条目）。
   认领到之后：显示「已装 + 版本」、卡片上出现全局开关、展开里会有一句
   「这些文件不是 Hub 装的，点重装可以让 Hub 接管」。

> 「插件文件」那一栏**不依赖**这些 —— 它就是把 addons 目录整个列出来，任何文件都在那儿开关。

### 8.4 自定义游戏怎么进的顶部游戏列表

自定义游戏（Hub 数据库里没有的游戏）要能被顶部列表选中，就得有个**身份**。
`GameBiz` 本来就是"一个字符串 + 一堆 per-biz 设置"（`AppConfig.GetGameInstallPath(biz)`、
`enable_dx12_{biz}`、`SelectedGameBizs`…），所以给每条自定义游戏一个**合成 biz**就够了：

```
custom_<短id>          短id = SHA256(条目 id) 的前 12 位，稳定、可复现
```

- `GameBiz.IsKnown()` 对它是 false → Hub **不会**拿它去 HoYoPlay 查任何东西；
- `GameFeatureConfig.FromGameId` 落到 `Default`（只有「启动器」页）→ 游戏设置/截图页对自定义游戏自动隐藏；
- 添加游戏时顺手 `AppConfig.SetGameInstallPath(customBiz, 游戏目录)`，启动器页就能照常认路；
- `SelectedGameBizs` **不写**自定义游戏（写了下次 `GameBiz.TryParse` 也认不出来），
  它们每次从 `games.json` 重算，加了就有、删了就没；
- 顶部那张图标是从 exe 的缩略图抠出来缓存到 `<用户数据目录>\.hysx\gameicons\<短id>.png`，
  抠不到就用兜底图。

启动器页对自定义游戏走的是独立分支：`GameState = StartGame`、安装目录 = exe 所在目录、
「开始游戏」直接起 exe，**不碰** HoYoPlay 那套版本/安装/更新流程。
勾了注入模式就只架 `inject.exe`、**不替用户启动游戏**（原因见 §8.7）。

### 8.4.1 自定义游戏的图标 / 背景 / 名字

| 东西 | 从哪来 |
| --- | --- |
| 图标 | 从 exe 抠（`GameIconExtractor`：`StorageItemThumbnail`，要 **256** 那一档），缓存到 `<用户数据目录>\.hysx\gameicons\<短id>.png`；抠不到用内置兜底图 |
| 背景 | Hub 没有它的官方背景 → 用应用默认背景图；**用户可以自己换**：选择界面里那张卡片的菜单里有「设置背景图…」（也可以直接把图片/视频拖到启动器页面上）。存的是同一套 `custom_bg_{biz}` 设置，所以启动器页背景会一起换 |
| 名字 | 图标磁贴的 tooltip + 选择界面卡片上的名字 |

> 抠图标实测：`AzurPromilia.exe` 里有 256x256 那一档（`PrivateExtractIcons` 验证过），
> 所以别只要 32 的 —— 拉到 40x40 会糊。

### 8.4.2 版本号从哪来

addon 显示版本按这个顺序（`AddonVersionResolver`）：

1. **文件名括号里**的（`renodx-dlss(9.17.12).addon64` → `9.17.12`）—— 最准；
2. 没有括号就**读 PE 版本资源**。实测：

| 文件 | 文件名版本 | PE `FileVersion` | 用哪个 |
| --- | --- | --- | --- |
| `renodx-dlss(9.17.12).addon64` | 9.17.12 | 1789595968（时间戳，丢弃） | 文件名 |
| `dlss5-bridge.addon64` | 无 | 1.4.13-pre7 | PE |
| `renodx-dlss5-super-anus(1.0.8.18).addon64` | 1.0.8.18 | 1.0.8.18 | 文件名 |
| `renodx-neural-interposer-nvngx.dll (23.1.0 RC1).addon64` | 23.1.0 RC1 | 19.0.0.1 | 文件名 |

全局插件页那些**扩展包**卡片原来显示「目录 latest」—— 因为内置目录里每个条目的 `version`
都是占位的 `latest`。现在改成显示**盘上那个文件的版本**（文件名 → PE），
只有目录里写的是真版本号时才把「目录 x.y.z」也带上。

### 8.4.3 内置目录里的 DLSS5 相关条目

| id | 来源 | 实测 release |
| --- | --- | --- |
| `renodx.dlss5` | `RankFTW/rhi-repo` | `renodx-dlss5-5.2.1` → `renodx-dlss5_5.2.1.zip` |
| `renodx.dlss5.superanus` | `An0sTheGreat/DLSS-5-Super-Anus-Manual` | `v1.0.9.21` → `renodx-dlss5-super-anus.addon64` |
| `renodx.dlss.sf`（ShortFuse 分支）| `RankFTW/rhi-repo` | `renodx-dlss-SF-26.0917.1904` |
| `dlss5.neural.interposer` | `thx114/hoyodlss5` | `renodx-neural-interposer-nvngx.dll(23.1.0RC1)` → `renodx-neural-interposer-nvngx.dll.23.1.0RC1.addon64` |
| `dlss5.bridge` | `NIGos/dlss5-bridge` | `v1.4.13-pre8` → `dlss5-bridge.addon64`（DX11 游戏用）|
| `renodx.ue.doffix` | `RankFTW/rhi-repo` | 只对虚幻引擎有意义，Unity 游戏装了没用 |

每个条目还带一个 **`addonPatterns`**：这个插件装到 addons 目录里之后文件名长什么样。实测：

| 条目 | 装出来叫什么 |
| --- | --- |
| `renodx.dlss5` | `renodx-dlss5.addon64`（zip 里就这个名字，**没有版本号**）|
| `renodx.dlss.sf` | `renodx-dlss.addon64`（同上）|
| `renodx.dlss5.superanus` | `renodx-dlss5-super-anus.addon64` |
| `dlss5.bridge` | `dlss5-bridge.addon64` |
| `dlss5.neural.interposer` | `renodx-neural-interposer-nvngx.dll.23.1.0RC1.addon64` |

有它之后，**不是 Hub 装的**插件（自己从 Discord 拿的）也能在全局插件页被认出来：
显示「已装 + 版本」、卡片上出现全局开关（改名那套）。

> 最后那个（神经插帧器）以前只在 Discord 里发，现在从作者自己的仓库取。
> 它的资产名是 `名字.版本.addon64` 这种写法 —— 没有括号，所以 `AddonFileInfo.Parse`
> 多了一条「点号版本」的解析规则（`_dottedPattern`）。

### 8.4.4 「指定目录」会记住

插件页右上角那个「指定目录」选过的 HoYoShade 目录会存进设置（`hysx_manual_shade_root`）。
便携版 / 测试沙盒里 `<用户数据目录>\HoYoShade` 未必是用户实际在用的那一份
（沙盒那份只有 1 个 addon），指定一次之后两个插件页都会一直用它。

### 8.4.5 插件要的 dll（DLL 配置 / 红黄标记）

DLSS5 那几个插件光有 `.addon64` 是跑不起来的 —— ReShade 只从 addons 目录加载，
所以运行时也必须躺在**同一个目录**里：

| 文件 | 级别 | 缺了会怎样 |
| --- | --- | --- |
| `nvngx_dlssnr.dll` | **必需（标红）** | DLSS5 的神经渲染运行时，没有它插件直接加载不了 |
| `sl.interposer.dll` + `sl.dlss_nr.dll` 等 `sl.*.dll` | 建议（标黄） | Streamline 一整套，缺了插件能加载但**大概率不出画面** |

判定「是不是 DLSS5 类插件」用的是目录条目的 `tags` 里有没有 `dlss5`（`DlssDllRequirements`）。

- **红黄标记**出现在三处：全局插件页的扩展包卡片、插件文件那一栏、以及每个游戏的插件列表；
- **启动游戏时**如果当前游戏开着 DLSS5 插件而 `nvngx_dlssnr.dll` 不在，会弹窗
  （「仍然启动」/「去 DLL 配置」/「取消」）；
- 左侧新增 **「DLL 配置」** 页：左边列出插件目录里现有的 dll（带 PE 版本），
  右边按 `dlssnr / streamline / dlss / dlssd / dlssg` 分组，选版本一键装，
  还有一个「补齐 DLSS5 需要的」按钮。

清单来源是用户指定的 `RankFTW/RHI` 的 `dlss_manifest.json`（见 RESHADE-INI.md §4）：

```
dlss 11 条 / dlssd 10 条 / dlssg 12 条 / streamline 13 条 / dlssnr 2 条
```

清单里 `dlssnr` 只有 `310.8.0` 和 `310.8.SF-v2`，但 rhi-repo 上还有
`310.8.0-RTX40` / `310.8.SF`（实测 tag 存在），这几个硬编码补进去了 ——
30/40 系如果不出画面就换带 `RTX40` / `SF` 的试试。

> 已装版本靠 **PE 版本资源** 读（文件名里没版本）：实测用户机器上
> `nvngx_dlssnr.dll` = `310,8,0,0`、`sl.*.dll` = `2,13,0,0`，
> 归一化之后能和清单里的 `310.8.0` / `2.13.0.0` 对上（`DllVersion.Normalize`）。

### 8.4.6 LoadFromDllMain 默认勾上（只对 DLSS5 类）

DLSS5 的帧生成要在 `DllMain` 阶段就加载，所以：

- 在游戏插件页**打开**一个 DLSS5 插件时，自动把它加进该游戏的 `LoadFromDllMain`；
- 页面加载时还会**同步一次**（`GamePluginService.SyncDlss5LoadFromDllMain()`）：
  已经启用但没勾的 DLSS5 插件补上 —— 手动改过 ini 或者旧数据也能被拉回来；
- 非 DLSS5 插件一律不动。

### 8.4.7 定位游戏：选成上层目录的兜底

真实布局往往是「启动器根目录 + games 子目录」：

```
D:\APPS\miHoYo Launcher\games\Genshin Impact Game\YuanShen.exe
D:\APPS\Star Rail\games\Star Rail Game\StarRail.exe
```

用户点「定位游戏」时经常会选到 `D:\APPS\miHoYo Launcher` 这一层，于是报「找不到 xx.exe」。
`GameFolderLocator` 就是干这个的：**只看目录名，最多往下 2 层**（`MaxDepth`），
最多看 200 个目录（`MaxDirectories`），跳过 junction，不做全盘遍历。
候选里优先挑**名字像游戏的**（含 `game`，正好命中 miHoYo 那套 `&lt;游戏名&gt; Game`），
`AntiCheatExpert` / `cache` / `logs` 这类噪声目录排到最后。

找到就往里定（并弹一句「往里找了一层：…」）；找不到还是照旧报错。
启动器页的「定位游戏」和游戏设置对话框里的「添加游戏目录」两处都接了。

### 8.4.8 顶部游戏图标的固定（pin）

- **定位到的游戏自动固定到顶部**（启动器页「定位游戏」和游戏设置里的「添加游戏目录」成功后
  各发一条 `PinGameBizMessage`，`GameSelector` 收到就把它的图标加进顶部那行）。
  没定位过的游戏**默认不自动固定**。
- 顶部那行是靠 `AppConfig.SelectedGameBizs` 持久化的，所以固定/取消固定重启后还在。
- **右键菜单**（顶部图标、以及左上角按钮里选择界面的每一行服务器）三件事：

  | 菜单项 | 做什么 |
  | --- | --- |
  | 取消固定 | 从顶部那行拿掉（之后它还在左上角按钮的选择界面里） |
  | 固定到顶部 | 加回顶部那行 |
  | 删除 HoYoShade（这个客户端）| 同游戏设置里的「卸载/还原 ReShade 改动」，两步确认 |

  这两个菜单项按 `GameBizIcon.IsPinned` / `NotPinned` 二选一显示。

- 卸载那段逻辑从 `GameLauncherSettingDialog` 抽到了 `ReShadeClientUninstaller`，两处共用：
  删根目录下那几个文件（ReShade.ini/.log、opengl32/64.dll、ReShade32/64.dll、inject.exe、
  ShaderToggler.ini）+ `*.addon32/64` + `reshade-shaders` 目录。

### 8.5 落盘的位置

| 东西 | 路径 |
| --- | --- |
| 游戏条目 + 注入模式开关 | `<用户数据目录>\.hysx\games.json` |
| addon 内部名缓存（自愈用） | `<用户数据目录>\.hysx\addon-names.json` |
| 各游戏的插件开关 | `<游戏目录>\ReShade.ini` 的 `[ADDON] DisabledAddons` |

> `games.json` 只存「用户产生的状态」：自定义游戏、exe 覆盖、注入模式开关。
> 自动发现的游戏每次重算 —— 游戏卸载了就不该再出现在列表里。

### 8.6 快速安装只装必要的（用户反馈）

首启向导的「一键开始安装」原本下发 `install_mode = 0`（All），会把
`crosire/reshade-shaders` 的**全部效果包 + 全部 Addons 插件**都拉下来，
用户反馈「怎么还是安装全量插件，应该是仅安装必要」。

现在改成 `install_mode = 1`（EssentialOnly），包的选择由官方
`EffectPackages.ini` 里 `Required=1` 或 `Enabled=1` 决定：

| 模式 | 效果包 | Addons 插件 |
| --- | --- | --- |
| 0 = All（改之前） | 44 个（全部） | 20 个（全部带下载地址的） |
| 1 = EssentialOnly（改之后，向导默认） | 2 个：`00 Standard effects`、`01 SweetFX` | 0 |
| 2 = Custom | 用户勾选 | 用户勾选 |

> 数字是 2026-09 抓 `EffectPackages.ini`（44 节 / Required=1 一个 / Enabled=1 两个）
> 和 `Addons.ini`（24 节 / 20 个有 DownloadUrl64）得到的。

- 代码：`src/HoYoShadeHub/Features/ViewHost/QuickSetupView.xaml.cs` 里
  `InstallReShadePackRequest.InstallMode` 由 0 改成 1，向导页文案同步改成
  「仅必要的 ReShade 着色器 / 不下载 Addons 插件」。
- **DLSS5 的插件不从这里装**：那些 addon（`thx114/hoyodlss5` 等）走插件页 /
  全局插件页的下载按钮；缺的 dll 走 DLL 配置页的「安装必要组件」。
- 想要全量或自选：向导里的「自定义 / 高级安装」进 `ReShadeDownloadView`，
  那里三个模式（全量 / 仅必要 / 自定义）仍由用户自己选，默认仍是全量。
### 8.7 注入模式不替用户启动游戏（用户反馈）

用户反馈：「注入模式启动蓝色星原，他不是注入模式，仍然自己启动了游戏」。

原因是 `StartGameWithInjectModeAsync` 之前多做了第 3 步 —— 架好注入器后
又调 `LaunchGameForInjectModeAsync` 把游戏拉起来（自定义游戏直接
`Process.Start(游戏 exe)`，Hub 认识的游戏走 `GameLauncherService.StartGameAsync`，
而后者同样是**直接起 exe**，见 `GameLauncherService.cs:367`）。

这一步和 HoYoShade 的说明正好相反。`简体中文启动器.bat`（GBK 编码，第 138~139 行）：

> 重要：你必须要使用一个游戏启动器来启动游戏（无论是官方启动器还是第三方启动器），
> **不能直接双击运行进程/进程快捷方式以启动游戏。否则会注入失败。**

所以现在的行为（`GameLauncherPage.xaml.cs`）：

| 模式 | 点「开始游戏」之后 |
| --- | --- |
| 普通模式 | 照旧，Hub 起游戏 exe |
| 注入模式 | 只做两件事：补 `ReShade.ini`（缺的话跑 `INIBuild.exe`）+ 起 `inject.exe <进程名>` 等 `READY`；**不启动游戏**，弹一条提示让用户用自己的启动器（HoYoPlay / WeGame / 官方启动器）把游戏拉起来 |

- 原来那段「`!EnableGameLaunch` 就只注入不启动」的判断一并删掉了 —— 注入模式下
  现在**永远**不启动游戏，那个勾选框在这个模式里没有意义。
- `LaunchGameForInjectModeAsync` 随之删除（只剩这一个调用点）。
- 副作用：注入模式下 Hub 不再能记录游戏进程 / 游玩时长，也不会把状态切成「游戏运行中」——
  游戏是用户的启动器起的，Hub 这边就不认领它了。
### 8.8 「用哪个启动器，ini 就对应到哪」—— 启动时把绝对路径对回当前 HoYoShade（用户反馈）

用户反馈：自定义游戏（蓝色星原）的插件页标红「缺 nvngx_dlssnr.dll」，可全局插件页和 DLL 配置页
都显示这个 dll 装好了；换自己的启动器也是一样。原话：**「应该用哪个启动器，ini 就对应到哪才对」**。

查下来不是 bug，是两个目录：

| 界面 | 按哪个目录算 |
| --- | --- |
| 每游戏插件页的红/黄标 | **那个游戏自己 ReShade.ini 的 `[ADDON] AddonPath`**（`GamePluginService.cs:119`）—— ReShade 运行时就是去那儿找 addon 和 dll 的 |
| 全局插件页 / DLL 配置页 | **当前 HoYoShade 宿主**的 `reshade-shaders\Addons`（`DllConfigPage.xaml.cs:170`） |

实测那台机器：蓝色星原的 ini 里 `AddonPath=D:\CODE\HoyoDLSS5\build\smoke\data\HoYoShade\...`
（smoke 沙盒那份，只有 1 个 addon、没有 dll），而真实的 `D:\APPS\HoYoShadeHub\HoYoShade` 里
`nvngx_dlssnr.dll` 158 MB 好好的 —— 所以红标是**实话**，只是两页看的不是同一个目录。

根因：`INIBuild.exe` 写出来的模板全是**绝对路径**（AddonPath / EffectSearchPaths / TextureSearchPaths /
PresetPath / 字体 / 截图目录），而 `inject.exe` 只在游戏目录**没有** ini 时才复制模板。所以换过
HoYoShade（换启动器、换用户数据目录）之后，老 ini 会一直指着旧目录 —— 连 HoYoShade 自己的 bat 也一样。

**做法**：新增 `ShadePathAligner`（`Extensions/ReShade/ShadePathAligner.cs`），启动/注入前把该游戏的 ini 对一次：

- 只动上表那 8 个键，而且**只换根前缀**：靠 `reshade-shaders` / `Presets` / `InjectResource` / `ScreenShot`
  这几个「只可能长在 HoYoShade 根下」的目录名反推旧根，再把前缀换成当前根；
- **相对路径不动**（`.\reshade-shaders\Addons` 跟着 ReShade DLL 走，换哪个启动器都对）；
- 认不出旧根的自定义路径不动（比如用户自己指定的插件目录）、分号写法不动；
- 其余键、注释、顺序、`[RENODX-*]` 里调好的参数原样保留；重复跑是幂等的。

调用点：

| 位置 | 说明 |
| --- | --- |
| `StartGameWithInjectModeAsync` | 补完 `ReShade.ini` 模板之后、起 `inject.exe` 之前 |
| `LaunchGameWithShadeAsync` | 普通模式（不走注入）启动前同样对一次 |
| 每游戏插件页「指回当前 HoYoShade」按钮 | 手动改，改完立刻刷新列表 |

界面上另外加了一条提示（用户要求）：当「这个游戏 ini 的 AddonPath」≠「当前 HoYoShade 的插件目录」时，
标题下面用黄字写清楚红/黄标是按哪个目录算的，右边配那个按钮。

> 顺手把用户机器上那份被 smoke 沙盒写坏的 ini 修了：
> `D:\WeGameApps\rail_apps\蓝色星原：旅谣(2002738)\ReShade.ini` 里的 8 处旧根路径全部改成
> `D:\APPS\HoYoShadeHub\HoYoShade`，备份留在同目录 `ReShade.ini.hysx-bak-<时间戳>`。
### 8.9 自定义游戏的内置背景（蓝色星原的宣传动图）

用户要求：把蓝色星原官网页面上那段背景动图「搞下来丢启动器」，**用户加蓝色星原就带上这个背景**。

来源（WeGame 活动页 `lsxyly20260821ogt6wwte`，2026-09 抓）：

| 文件 | 来源 | 大小 |
| --- | --- | --- |
| `azurpromilia.mp4` | `wegame.gtimg.com/g.2002738-r.7d74a/57df57fd84d8f4217e75e9ca09d6d316.mp4`（页面里 `<video loop muted autoplay>` 那段，6 秒循环） | 14.2 MB |
| `azurpromilia.jpg` | 同页 `images/0814/bg_01.jpg`（1920×1080 静帧，给选择界面的卡片用） | 0.2 MB |

两个都放在 `src/HoYoShadeHub/Assets/Video/`；资源包 `HoYoShadeHub.Assets` 里没有 Assets 目录，
所以 csproj 里显式加了 `<Content Include="Assets\Video\**">`。

**落地方式**（`GameCatalog`）：

- `RegisterCustomGame`（加游戏 / 选中自定义游戏 / 「指定主程序」都会走到）里调 `ApplyBuiltinBackground`；
- 认游戏看 exe 名 `AzurPromilia.exe`，其次显示名含「蓝色星原 / 旅谣 / Azur Promilia」；
- 把随包的 mp4 复制到 `<用户数据目录>\bg\azurpromilia.mp4`，再写 `custom_bg_{biz}` + `enable_custom_bg_{biz}`；
- **用户自己设过背景就不动**（`custom_bg_` 已经有值就跳过），失败也只是不设背景，不影响加游戏。

启动器页的背景本来就走 `BackgroundService.GetCachedBackgroundFile` → `FileIsSupportedVideo` → 用
`MediaPlayerElement` 播，所以 mp4 直接能转。**但选择界面那张卡片用的是 `CachedImage`，播不了视频**，
所以 `GameCatalog.CardBackgroundFor` 在「自定义背景是视频」时换成那张随包的静帧，避免卡片变空白。
> 卡片背景不是 `MediaPlayerElement`：启动器背景走的是 `AppBackground` 里的 `MediaPlayer`
> （`IsVideoFrameServerEnabled = true` + Win2D 逐帧画），播放逻辑在那边。

**占用优化（用户反馈「背景占的显卡太多了」）**：原始素材是官网的 **3840×2160 / 30fps / 19.9 Mbps**，
而 `AppBackground` 原来是按 `NaturalVideoWidth/Height` 建 `CanvasRenderTarget` 的 —— 每帧拷
3840×2160×4B ≈ 33 MB，30fps 就是 ~1 GB/s 的显存带宽。两处改掉：

1. **素材重压**：用系统自带的 `MediaTranscoder`（`build/probe/transcode` 那个一次性小工具）
   转成 **1920×1080 / 30fps / 3.4 Mbps、无音轨**：14.23 MB → **2.44 MB**（便携包跟着小 12 MB）；
2. **按窗口大小渲染**：`MediaPlayer_VideoFrameAvailable` 先算 `VideoRenderSize()`（够铺满窗口就行、
   不放大；背景是 `UniformToFill` 所以取能盖住窗口的缩放比），surface / `CanvasImageSource` 都按这个
   尺寸建，缩放交给 `CopyFrameToVideoSurface(surface, destRect)` 在 GPU 上做。1200×676 的窗口现在
   每帧只拷窗口那么大，跟源是 4K 还是 1080p 无关。

另外**窗口隐藏到托盘 / 锁屏时本来就 `Pause()`**（`OnMainWindowStateChanged`），这部分不用改；
不想要动图也可以「停止视频背景」只留那张静帧。

### 8.10 顶部游戏栏点不动（拖拽区把图标吃掉了）

用户反馈：「在软件之间切来切去后，启动器顶部的游戏栏没法左键也没法右键，得重启启动器」。

**根因**：窗口是 `ExtendsContentIntoTitleBar` 的无边框窗口，标题栏靠 `AppWindow.TitleBar.SetDragRectangles`
划出来。这个矩形有**两个地方**在写：

| 谁 | 写什么 |
| --- | --- |
| `MainWindow.UpdateDragRectangles()` | **整条**标题栏（`leftInset, 0, 窗口宽, 48`）—— 启动时、以及每次 `AppWindow.Changed`（尺寸/呈现器变化） |
| `GameSelector.UpdateDragRectangles()` | 从 `Border_CurrentGameIcon`（+ 展开的图标行）右边开始，把左上角那排游戏图标**挖出去** |

后写的赢。只要窗口尺寸变过一次（`Show()` → `CenterInScreen` → `MoveAndResize`，或换屏/DPI 变化，
`AppWindow.Changed` 就来了），`MainWindow` 就把整条标题栏设成拖拽区 —— 那排图标正好落在里面。
落在拖拽区里的像素被系统当标题栏吃掉，**左键右键都到不了 XAML**；更麻烦的是指针事件也进不来，
所以 `Grid_GameIconsArea_PointerExited` / `Border_CurrentGameIcon_PointerEntered` 再也不会触发，
`GameSelector.UpdateDragRectangles()` 没有机会跑 —— 自锁，只能重启。

**修法**：拖拽区只留一个「权威」——

1. `GameSelector.TryUpdateDragRectangles()`：算好矩形并返回是否成功（布局没跑完、`ActualWidth == 0` 时
   返回 false，调用方别把整个左上角当拖拽区），`Border_CurrentGameIcon` 至少按 56 算；
2. `MainWindow.UpdateDragRectangles()` 改成先问 `MainView.Current.TryUpdateWindowDragRectangles()`，
   算不出来才退回「整条标题栏」（向导页那会儿没有 MainView，退回是对的）；
3. `WM_ACTIVATE` 里再调一次 `UpdateDragRectangles()` —— 从托盘/别的窗口切回来时自愈。
### 8.11 插件管理 / hook / 背景的一批反馈（用户 2026-09-19 提的）

#### (1) 「插件下载和删除很混乱：很容易下到 2 个一样的插件，删插件却没法删除全部」

根因在账本：`InstalledExtensionStore.UpsertAsync` 是**整条替换**，装新版本时老版本的文件留在目录里、
账本里却没了记录 —— 于是目录里两份同名插件，卸载也只删账本里那几份。

| 场景 | 现在的行为 |
| --- | --- |
| 装新版本 | 装完顺手删掉**同族旧文件**：账本里上次那份（这次计划里没有的）+ 插件目录里同一个 slug 的其它 `*.addon64*`；结果里有 `RemovedStaleFiles` |
| 手改过的旧文件 | 删之前核 sha256，对不上就**保留**并给一条 warning（用户的东西不能动） |
| 删插件 | `UninstallAsync(..., removeSiblings: true)`（界面默认开）：连同族的其它版本 / `.addon64x` 一起删，结果里有 `DeletedStaleFiles`；**被用户改过、以及本次没删掉的文件不会被当成同族再删一遍** |

同族判定 = `AddonFileInfo.Parse` 出来的 `slug` 相同（`renodx-dlss` 与 `renodx-dlss5-super-anus` 是两个族）。

#### (2) 「没有检测更新；最好能下拉选版本」

- `GithubReleaseResolver.ResolveAsync(source, tagOverride, ct)`：给了 tag 就装**指定版本**
  （资产名写死直接拼 URL，否则抓 `releases/expanded_assets/{tag}` 的 HTML 挑，仍然不碰 API 限额）；
- `GithubReleaseResolver.ListVersionsAsync(source, max, ct)`：新→旧列出可装版本
  （先 `releases.atom`，再翻 `/releases?page=N` 的 HTML，同样不吃限额），返回 `ExtensionVersion(Tag, Published)`；
- 全局插件页：**点开卡片**时拉版本列表填进「版本」下拉 + 「装这个版本」按钮；
  进页面刷新时后台跑一遍 `CheckUpdateAsync`，有新版就在卡片上挂「有新版本 xxx」徽标。

#### (3) 「renodx-neural-interposer-nvngx.dll 要把 nrdll 放在游戏目录」

`GamePluginService.EnsureInterposerDlls()`：启用这个插件时把 `nvngx_dlssnr.dll` 从插件目录复制到
**游戏 exe 旁边**（已经在且大小一样就不重复拷）。开关操作里自动做，改完会给一句提示。

#### (4) 「背景在游戏运行时要停止运行并释放显存」

`AppBackground` 订阅 `GameStartedMessage` → `DisposeVideoResource()` + 换成内置静态图（真的把 MediaPlayer /
Win2D surface 释放掉，不只是 Pause）；游戏退出时 `GameLauncherPage.CheckGameExited` 发新的
`GameExitedMessage` → 重新 `UpdateBackgroundAsync()` 把背景拉回来。
（Hub 之外自己起的游戏它不知道，所以那种情况只靠「隐藏到托盘 → Pause」。）

#### (5) 「hook 没有实时读取，起码焦点回到启动器时重读」

`GamePluginPage` 订阅 `MainWindowStateChangedMessage`，`Activate` 时 `UpdateHookPointUi()` 重读 ini。
提示文案也改成人话：「ini 里现在是 4（读的是 [RENODX-DLSS] DirectNeuralRenderingHookPoint）」。

#### (6) 「启动游戏时强制 off」

每游戏一个开关（`AppConfig` 的 `force_hook_off_{biz}`，插件页配置栏里的勾选框）。
启动器页 `StartGameAsync` 开头调 `ApplyForceHookOffOnLaunch()`：勾了就先把 hook 写 0 再启动/注入。

#### (7) 「hook 没适配 renodx-dlss」——查了，段名是对的

把真插件二进制抠字符串验证：`renodx-dlss.addon64` 里出现的段名就是 <code>RENODX-DLSS</code>
（`RenoDX DLSS attached..RENODX-DLSS.RenoDX DLSS detaching`），键名 `DirectNeuralRenderingHookPoint`
是运行时拼出来的（二进制里搜不到整串，只搜得到 `DirectNeuralRendering` / `Hook*` 片段）。
所以 §8.9 之后写 `[RENODX-DLSS]` 的做法是对的 —— 之前看到「设了 off 不生效」是**旧构建**（写进了 `[ADDON]`）。
现在插件页的提示里直接写明「读的是 [RENODX-DLSS] DirectNeuralRenderingHookPoint」，免得再对不上。
### 8.12 注入模式的提示条（用户追加要求）

| 要求 | 做法 |
| --- | --- |
| 删掉「注入模式」的介绍提示 | `CheckBox_InjectMode_Changed` 不再弹 toast（说明留在那个勾选框的 tooltip 里） |
| 「注入器已就位」合并进「已启动 HoYoShade 注入器」，并去掉它的说明文字 | 两条合成一条：标题就是 `Lang.GameLauncher_InjectorStarted`（「已启动 {0} 注入器」），**没有** message |
| 只要注入器在运行就永久显示 | `InAppToast.ShowSticky(...)`：`duration = 0`，`InAppToast` 那个 30 秒定时器只清理 `IsOpen == false` 的条目，所以它会一直挂着；注入器进程退出时 `Exited` 回调把它收掉 |
| 标题右侧一个红色可点击的「停止」 | `ShowSticky` 用 `InfoBar.ActionButton`，按钮透明背景 + `SystemFillColorCriticalBrush` 前景 |
| 再次启动游戏先停掉上一个注入器 | `StartGameWithInjectModeAsync` 起新的之前先 `StopInjector("要重新注入")`（`Kill(entireProcessTree: true)`） |

注入器进程和提示条都是 **static** 字段：离开启动器页再回来（页面实例被重建）时，
「停止」按钮和「先停掉上一个」仍然指着同一个注入器。
### 8.13 「额外注入 DLL」（OptiScaler / DLSS Enabler 同时注入）

用户要求：**B 方案** —— Hub 自己再加一个通用 DLL 注入器，跟 HoYoShade 的 `inject.exe` **同时**注入
（目标是 DLSS Enabler 里那份 OptiScaler，而且只要帧生成）。

**实现**（`Features/GameLauncher/DllInjector.cs`）：最普通的 `OpenProcess` → `VirtualAllocEx` →
`WriteProcessMemory` → `CreateRemoteThread(LoadLibraryW)`；外加一个「等进程出现」的轮询
（游戏是用户自己用启动器拉起来的，所以要等）。

| 环节 | 说明 |
| --- | --- |
| 设置 | 每游戏一个 `extra_inject_dll_{biz}`（`AppConfig`），启动器页「注入模式」旁边那个「额外 DLL」按钮选文件 / 换 / 不再注入 |
| 触发 | 注入模式架好注入器之后 `StartExtraDllInjection(processName)`：等游戏进程（最多 20 分钟）→ 注入 → 弹成功/失败提示 |
| 取消 | 点提示条上的「停止」或再次启动游戏（`StopInjector`）会一起取消等待 |
| 失败原因 | 进程打不开（游戏提权而 Hub 没提权）、远端线程建不起来（被反作弊拦）、`LoadLibraryW` 返回 0（位数/依赖不对）都会写在提示里 |

**局限（写清楚，免得期望过高）**：这只保证「DLL 进了进程」，不保证它赶得上 swapchain / DLSS 初始化那几个 hook；
所以要在游戏进程刚出现时就注。另外 OptiScaler 官方推荐的是**代理 DLL** 或 **`LoadReshade=true`** 那种装载方式，
「注入」不是它文档里的部署模式 —— 能不能跑通要实测。

**DLSS Enabler 那套的事实**（查过）：DLSS Enabler 的自动化构建是把 OptiScaler 的 `nvngx.dll` 下载下来
改名成 `dlss-enabler-upscaler.dll`，配置就是 OptiScaler 的 `nvngx.ini`（`[Upscalers]` / `[FrameGen]` 那两段）。
「只开帧生成」= `[Upscalers] Dx12Upscaler=dlss`（保持 DLSS 上采样不动）+ `[FrameGen] Enabled=true`，
`FGInput` 用 `dlssg`（游戏自己的 DLSSG 当输入）/ `FGOutput` 按手里有的 dll 选 `nvngxfg`（要
`dlssg_to_fsr3_amd_is_better.dll` 或 `dlss-enabler-headless.dll`）/ `xefg`（要 `libxess_fg.dll` + `libxell.dll`）/
`fsrfg`（要 AMD 那两个 `amd_fidelityfx_*`）。
### 8.14 「启动器里直接能启用 DLSS Enabler」（部署 + 一键开关）

用户要求：别让他自己去 GitHub 找、手动铺文件 —— **帮我把 DLSS-Enabler 部署好，启动器里直接能启用**。

**部署**（`Features/GameLauncher/DlssEnablerService.cs`）：

1. 用 `GithubReleaseResolver` 解析 `artur-graniszewski/DLSS-Enabler` 的安装包（`dlss-enabler-setup*.exe`，
   走 atom + expanded_assets HTML，不吃 API 限额）；
2. `DownloadService` 下到临时目录；
3. 静默装：`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /DIR="<用户数据目录>\.hysx\dlss-enabler"`；
4. 装完调 `ConfigureFrameGenerationOnly()` 改 `nvngx.ini`（原件备份 `nvngx.ini.orig`）：
   `[Upscalers] Dx12Upscaler=dlss` + `[FrameGen] Enabled=true / FGInput=dlssg / FGOutput=nvngxfg`。

**找已有的部署**（`FindInstalledFolder()`，按顺序找，谁在盘上就先认谁）：

| 顺序 | 位置 |
| --- | --- |
| 1 | `<用户数据目录>\.hysx\dlss-enabler`（Hub 自己装的） |
| 2 | `<用户数据目录>\DLSS-Enabler` |
| 3 | `AppContext.BaseDirectory\DLSS-Enabler`（便携版旁边） |
| 4 | `<游戏目录>\DLSS-Enabler` |
| 5 | **当前 HoYoShade 根目录的同级** `\DLSS-Enabler` —— 手动部署最常见的位置（在本机就是 `D:\APPS\HoYoShadeHub\DLSS-Enabler`） |

**启用**：启动器页「注入模式」旁边多了一个绿色勾选框 **「DLSS Enabler」**：

- 勾上 → 找得到就直接把该游戏的 `extra_inject_dll_{biz}` 指到 `dlss-enabler-upscaler.dll`；找不到就弹一句
  「现在下载官方安装包并部署吗？（约 31 MB）」，确认后走上面的部署流程再自动启用；
- 取消 → 只有当前指的就是它时才清掉（不会冲掉用户自己选的 DLL）；

### 8.15 第三批 16 条反馈（2026-09-19）

（这一节在 2026-09-21 修文档事故时按 AI-HANDOFF §5.11 重新整理，结论不变。）

1. **DLSS Enabler 整套撤掉**（用户放弃帧生成）：删掉启动器页那个绿勾选框 + `DlssEnablerService.cs`；「额外注入 DLL」保留，
   入口搬进「开始游戏」右边的设置对话框（`GameLauncherSettingDialog` 第 5 页 Tag=4）。
2. 注入模式按钮文案改「启动注入器」；额外注入不再依赖注入模式（普通模式也等进程注）。
3. 顶部游戏栏：`IsPinned` 没标 → 右键菜单显示「固定到顶部」且点了没反应；现在入行即标。
4. 「设置背景图…」挪到顶部图标右键菜单；「添加游戏」那格换成静态毛玻璃 `CustomFrostBrush`（不实时模糊）。
5. 插件：装新版本清同族旧文件、删除连坐同族、版本下拉 + 更新徽标、interposer 的 nrdll 拷到游戏目录。
6. 插件：删 / 全局禁用时清 ReShade.ini 里的引用（新增 `AddonReferenceCleaner`）；`LoadFromDllMain` 跟着插件开关走。
7. 改 ini 与游戏运行的关系：现在只是「游戏在跑就给一句提醒」，不阻止。
8. hook：`DirectNeuralRenderingHookPoint` + `DirectNeuralRenderingHookStage` 两个键都写、读优先 Stage；界面标签改 `HookPoint`。
9. 新增 `NvidiaDriverCheck`（用 DLSS5 插件时按区间红/黄提示），同时删掉三段没必要的说明文字。

### 8.16 第四批 7 条反馈（2026-09-19）

- 驱动版本当时先改成读**真实显卡驱动**（显示适配器类键的 `DriverVersion`，`32.0.15.6636` → `566.36`）——
  但用户要的是 NV app 那串，见 §8.19 又被纠正。
- 装的是 `310.8.SF-v2` 却显示 `SF`：PE 版本号里没有变体信息 → 安装时 `AppConfig.SetInstalledDllVariant()` 记账，
  显示时用 `DllVersion.SameNumbers()` 判断记账还作不作数。
- zip 覆盖旧客户端后跳过首次引导（`HasExistingShadeInstall()`）。
- hook 灰的根因：`IsHookPointCapable` 要求分支含 ShortFuse 而文件名里没有 → 放宽成 `renodx-dlss*` 都可改。
- 全局插件「插件文件」误报缺 dll：只把 addon 文件名传给了检查 → 改成目录里全部文件；
  该视图加「删除」（二级确认）；`dlss5.neural.interposer` 名字改英文。

### 8.17 便携包不再带 config.ini（2026-09-20）

- 更新包解压覆盖旧客户端时，zip 里带着 `config.ini` 会把用户那份（`UserDataFolder` / 各种设置）覆盖成默认的 ——
  用户会以为「游戏没了」（真事）。现在 zip 根目录**只有** `HoYoShadeHub.exe` + `version.ini` + `app-<ver>/`；
  缺了由 `PortableLauncher.EnsureConfigIni()` 创建，App 保存设置时也会补上。
- 启动时自动找现成 profile：`AppConfig.TryFindExistingProfileFolder()`（候选含「解压目录的同级目录」，
  证据文件 = DB / `.hysx/games.json` / `HoYoShade/ReShade64.dll`，按最新时间挑）。

### 8.18 「数据看起来丢了」还差两块（2026-09-20）

1. **只设 `AppConfig.UserDataFolder` 不会换 DB** —— 游戏列表 / 安装路径都在数据库里，必须走
   `AppConfig.UseUserDataFolder()`（内部同时调 `DatabaseService.SetDatabase`）。漏这一步就只剩自定义游戏（它们来自 `.hysx/games.json`）。
2. `AutoSearchInstalledGames()` 原来是**覆盖式**写 `SelectedGameBizs`，且只遍历 DB 缓存 → 缓存空时写空串，一点就把顶部清空。
   现在：从已有列表起步只增不减、缓存空则遍历 `GameBiz.AllGameBizs` + 注册表 `HKCU\Software\miHoYo\HYP\1_1\<biz>\GameInstallPath`（全局服是 `Cognosphere`），
   一个都没找到就什么都不做，只弹一句提示。

### 8.19 驱动版本：要的是 NV app 那串，而且要本地化匹配（2026-09-20）

用户第三次纠正驱动检测：「我要的是 NV app 里『已安装 - GeForce Game Ready 驱动程序 版本 616.64』」（不是别的什么版本号）。

**两个毛病**：

1. **本地化没管**：卸载项在中文 Windows 上叫「NVIDIA **图形驱动程序** 616.64」，而代码只匹配 `"Graphics Driver"` → 直接漏掉，于是读到别的东西；
2. **适配器扫描认错了卡**：本机第一块是 `AMD Radeon(TM) 610M`（`DriverVersion=32.0.12011.1010`），过滤条件是「ProviderName 含 NVIDIA **或** 以 3 开头」→ AMD 那块也过 → 换算成 **110.10** ✗。

**改法**（`NvidiaDriverCheck`）：

| 来源 | 规则 |
| --- | --- |
| ① 卸载项（**首选**，就是 NV app 显示的那串） | 名字含 NVIDIA，且 `DisplayVersion` 形如 `^\d{3}\.\d{1,2}$`；名字命中「驱动包」的多语言表（`Graphics Driver` / `图形驱动程序` / `圖形驅動程式` / `Grafiktreiber` / `グラフィックス ドライバー` / `그래픽 드라이버` / `Pilote graphique` / `Controlador de gráficos`）→ 取其中最大的 |
| ② 多语言兜底 | 名字认不出来时，取所有 NVIDIA 里「NNN.NN」形状中最大的那个（本机是 `Platform Controllers 615.34`，不会盖过 616.64） |
| ③ 显示适配器 `DriverVersion` | **必须 ProviderName / DriverDesc 里真的是 NVIDIA**（`32.0.16.1664` → `616.64`） |

本机模拟结果：名字匹配到 `NVIDIA 图形驱动程序 616.64` → 返回 **616.64** ✓（和 NV app 一致）。

### 8.20 驱动检测读对了，但插件页上还是「没有提示」（2026-09-20）

用户回：「插件页没有提示」——检测修好了，可界面上一行都不显示。

**根因**：`GamePluginPage.UpdateDriverWarning()` 里判断的是

    if (result.Level is DriverCheckLevel.Ok || ...) { 隐藏; return; }

即**只有超范围才显示**。用户驱动 616.64 恰好落在区间内（Ok）→ 那一行永远藏着，用起来就像「驱动检测压根没生效」。

**改法**：装了 DLSS5 插件（`Addons.Any(a => a.IsDlss5)`，看 `dlss5` 标签）时**始终显示一行**：

| 等级 | 显示 | 颜色 |
| --- | --- | --- |
| Ok | `NVIDIA 驱动 616.64（DLSS5 插件要求区间内）` | `SystemFillColorSuccessBrush`（绿） |
| Warning | `⚠ 驱动版本低于616.56可能存在些微dlss5插件兼容性问题（当前 616.55）` | `SystemFillColorCautionBrush`（黄） |
| Error | `⚠ …高于616.64…` / `…低于610.47…` | `SystemFillColorCriticalBrush`（红） |
| 连版本都读不到 | 隐藏 | — |

### 8.21 注入模式下「没勾 HoYoShade」也被注入了 HoYoShade（2026-09-21）

用户报：「启动 HoYoShade 这个没勾选的情况下，在注入模式启动游戏，还是被注入了 HoYoShade」。

**根因**：`StartGameWithInjectModeAsync()` 第一行是

    bool useOpen = UseOpenHoYoShade && !UseHoYoShade;
    string shadeName = useOpen ? "OpenHoYoShade" : "HoYoShade";

两个都没勾时 `useOpen` 为 false → 于是「默认」落到了 HoYoShade，照样架 inject.exe。
（这条路径以前只从「勾了 HoYoShade 之一」的场景进来；加了 OptiScaler / 额外注入 DLL 之后，
「一个 HoYoShade 都不勾、只想注 OptiScaler」成了正常用法，就露馅了。）

**改法**：

1. 进 `StartGameWithInjectModeAsync()` 先判断 —— `!UseHoYoShade && !UseOpenHoYoShade` 时**只等进程**：
   `StopInjector("要重新注入")` → `StartExtraDllInjection(processName)`（额外 DLL / OptiScaler），
   不碰 inject.exe、不补 ReShade.ini、不弹注入器常驻提示；进程名认不出来仍然报原来的错。
2. 按钮文案跟着分三种状态：`IsShadeInjectMode`（勾了 → 「启动注入器」）、
   `IsWaitProcessMode`（没勾 → 「等游戏进程」）、否则原来的「开始游戏 / 开始 Mod」。

---

## 9. OptiScaler（社区 DLSS-NR 分支）：下载、单选、启动时注入（2026-09-21）

### 9.1 用户要的东西

1. 全局插件里能**下载**社区维护的「带 DLSS 神经渲染（NR）」的 OptiScaler 分支；
2. 全局插件加一栏 **OptiScaler**（跟「插件文件」并列），**同一时间只能启用一个**；
3. 启用开关放在启动器页「启动 HoYoShade / OpenHoYoShade」**下面**（按游戏）；
4. HoYoShade / OpenHoYoShade 与 OptiScaler 同时启用时，在「启动选项」**上方**显示一条**滚动**提示：
   「OptiScaler 与 HoYoShade 同时启用可能出现插件冲突或兼容性问题」。

### 9.2 三个来源（内置表 OptiScalerCatalog.Builtin）

| id | 仓库 | 说明 |
| --- | --- | --- |
| wilsjo2 | wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass | pre-SR 摆放、1~3 遍处理；-rtx40-mfg 是 40 系多帧生成特化包 |
| neurotic | MagicalPrincessUnicorn/NeuRotic-an-OptiScaler-DLSSNR-fork | alpha 系列；名字带 Patch 的是补丁包 |
| dlssnr-amd | danielblnc/DLSS-NR-on-AMD | A 卡用的 DLSS-NR 分支。release 里**只有一个它自己的安装程序**（dlssnr_on_amd_setup.exe），见 §9.7 |

（原来第三个来源 y4my4y4m/OptiScaler_DLSSNR_Multipass_MFG 一直是 404，用户让删掉了。）

### 9.3 本地库与「单选」

- 目录：<用户数据目录>/OptiScaler/<sourceId>/<版本>/ —— **不进** ReShade 的插件目录（跟 addon 不是一回事）
- 每个构建目录里写一个 build.json（sourceId / version / assetName / installedAt）
- 单选记在库根的 state.json：{"selected":"wilsjo2/v0.8.6"} —— 换一个就是改这一行，不动文件
- OptiScalerLibrary：List() / GetSelected() / SelectedDllPath / Select(id) / Delete(id)
- OptiScalerDownloader：列版本（atom + HTML，不吃 GitHub API 限额）→ 列资产（.zip / .exe）→ 下载 →（zip 解压）落库
  - **不猜资产**：一个 release 里常常同时挂主包 / -rtx40-mfg 变体 / 补丁 / 语言包，界面让用户选（只有一个包时直接用）
  - 自动挑的时候**优先 .zip**（有压缩包就不去顺手跑人家的安装程序）
  - 7z 之类明确报错，不静默失败

### 9.4 界面

- 全局插件页：第三个页签 **OptiScaler**（Tag=optiscaler，RadioButtons_View 改成 MaxColumns=3）；
  页签里**没有**说明性 InfoBar（用户要求删掉「一次只能启用一个」那条）
  - 版本下拉**点开就自动拉版本**（跟「插件」那边的下拉一样，没有单独「获取版本」按钮）；拉过的列表缓存在页面里，装完刷新不打网络
  - 下载按钮文案：选中的版本已经装过 → **更新到此版本**，否则 → **下载并安装**
  - 下拉的**默认值 = 当前版本**，并且它在列表里带 **"(当前)"** 后缀（当前版本 = 当前启用的那个，没启用过就是这个来源最近装的那个）
  - 「已下载」那栏：每个构建一行，「启用」是 RadioButton（`GroupName=OptiScalerBuild`）+ 打开目录 + 删除（二级确认）
- 启动器页：`CheckBox_UseOptiScaler`（「启动 OptiScaler」）紧跟在「启动 OpenHoYoShade」下面
  - 只有全局页里**启用了一个构建**（且那个包里找得到 OptiScaler.dll）时才可勾（`IsOptiScalerAvailable`）
  - 冲突时在「启动选项」标题上方显示滚动提示条（240x16 的画布 + TranslateTransform.X 循环动画，Storyboard）

### 9.5 「启用」= 启动时注入

勾上之后，启动游戏时把选中的那个 OptiScaler.dll **跟「额外注入 DLL」一起 LoadLibrary 进去**：
StartExtraDllInjection → InjectExtraDllsAsync（一个进程只等一次，两个 dll 依次注入，路径重复会去重）。

> 为什么是注入、不是像官方文档那样把整包铺到游戏目录：一是 Hub 本来就有「额外注入 DLL」这套现成机制
> （用户之前就是这么用 OptiScaler 的），二是启动选项里的开关语义就是「启动时做点什么」，铺文件属于安装动作。
> 真要铺到游戏目录的用户，仍然可以用「额外注入 DLL」手动指一个。

### 9.6 血泪坑：XAML 编译报错在这台机器上会「静默 exit 1」

现象：MSB3073 ... XamlCompiler.exe ... 已退出，代码为 1，**既没有 stdout 也没有 message**，obj 里全是 0 字节的 .g.cs。

原因链（三个问题叠在一起）：

1. net472/XamlCompiler.exe 里错误消息资源叫 Microsoft.Windows.UI.Xaml.Build.Tasks.ErrorMessages.resources，
   而代码找的是 Microsoft.UI.Xaml.Markup.Compiler.ErrorMessages.resources → **中立资源永远查不到**；
2. 中文系统上它先找附属程序集 zh-CN/XamlCompiler.resources.dll（包里没有）→ 一路回退到中立 → 抛 MissingManifestResourceException；
3. 这个异常发生在**报错的过程中**，于是真正的 XAML 解析错误永远看不到。

**怎么把真错误挖出来**（最后就是这么定位的）：写一个 .NET Framework 宿主（`tools/xaml-error-unmask/XamlCompilerHost.cs`），
把 XamlCompiler.exe 放进独立 AppDomain 里 ExecuteAssembly（ApplicationBase / ConfigurationFile 指向 tools/net472），
并挂 AppDomain.FirstChanceException。真错误会以这种形式出现：

    [FCE] Microsoft.UI.Xaml.Markup.Compiler.ParseException: Property 'UseOptiScaler' not found on type 'GameLauncherPage'.

这次的真错误就是：XAML 里 x:Bind UseOptiScaler / IsOptiScalerAvailable，而 GameLauncherPage 里**还没写这两个属性**。
把属性、跑马灯、注入接线补上就编过了。

**结论**：以后再看到 MSB3073 静默失败，先怀疑「XAML 引用了代码里没有的东西」，别去怀疑编译器和包。

另一个相关坑：**被 XAML 用到的模型类型不能带 C# 的 required** —— WinUI 生成的 XamlTypeInfo.g.cs 会 new 它，
带 required 就报 CS9035: 必须在对象初始值设定项或属性构造函数中设置所需的成员。用带默认值的 init 属性。

### 9.7 只有安装程序的来源（DLSS NR on AMD）+ 一批界面调整（2026-09-21）

**danielblnc/DLSS-NR-on-AMD**：它的 release 里只有一个 `dlssnr_on_amd_setup.exe`（约 7.2 MB，
不是 Inno/NSIS —— 查过字符串，是自带的原生安装程序），里面才会铺文件。所以对它走另一条流水线：

1. 下载安装程序到构建目录 <库>/<sourceId>/<版本>/；
2. 写 build.json；**运行之前先弹一个确认框**（用户要求）：
   「即将启动 OptiScaler 安装程序 —— 请安装到默认的目录（就是它的默认值）：
    <用户数据目录>\OptiScaler\dlssnr-amd\v0.3.1；装完这里会有 version.dll，到启动器页勾「启动 OptiScaler」就能注入。」
   点「先不装」就只下载不运行（文件留在库里，之后点「打开目录」自己双击也行）。
3. 运行它（UseShellExecute + WorkingDirectory=构建目录，等它退出）→ 重新扫库。
   **它的默认安装目录就是启动目录**（用户确认过），装完里面会出现 `version.dll` —— 那就是注入目标：
   - `OptiScalerLibrary.FindDll` 除了 `OptiScaler.dll` / `OptiScaler*.dll`，还认一批「代理 DLL 名」：
     `version.dll`（排第一）/ `dxgi.dll` / `winmm.dll` / `dinput8.dll` / `wininet.dll` / `dbghelp.dll`；
   - 找到 dll 之后这一行就是普通构建：会被自动设成「当前启用」，启动器页那个勾也能勾了；
   - 目录里暂时没有 dll（比如用户点了「先不装」）时，那行标「安装程序（还没装出 dll：点「打开目录」再运行一次）」，不报红字「缺 dll」。

同一批还改了几处（用户逐条提的）：

- 删掉全局插件 OptiScaler 页签顶上那条「OptiScaler 一次只能启用一个」InfoBar；
- 版本改成**点开下拉自动获取**（去掉「获取版本」按钮），拉过的列表缓存在页面里；
- 下载过之后，「可下载」那边：下拉默认选中当前版本、该版本带「(当前)」后缀、按钮变成「更新到此版本」；
- 删掉两个提示文本：「ini 里现在是 off（读的是 [RENODX-DLSS] DirectNeuralRenderingHookPoint）。」
  （`GamePluginPage.UpdateHookPointUi`，现在能改时那行是空的）和「可以装的组件（清单来自 RankFTW/RHI 的 dlss_manifest.json；
  装到插件目录里，ReShade 才找得到）」（`DllConfigPage.xaml`）。
### 9.8 远端目录：插件 + OptiScaler 都能从 GitHub 更新（2026-09-21）

用户要求：GitHub 上放一份**目录**（所有插件 + OptiScaler 来源），启动器自动拉最新（每天一次，设置里也能手动拉），
这样以后加插件 / 换来源 / 加 OptiScaler 分支**不用重新发版**。

**约定**（详见 docs/OPEN-SOURCE-PLAN.md §4）：仓库根 catalog/，三个文件：

- catalog/plugins.json = 内置目录的同一格式（extensions: [...]），按 id 覆盖内置插件条目；
- catalog/optiscaler.json = {"sources":[{ "id","name","repository","description","tagPattern","tags","homepage" }]}，同样按 id 覆盖内置来源；
- catalog/modules.json = {"modules":[{ "id","name","description","repository","tagPattern","homepage","dllHint","tags" }]}，按 id 覆盖内置模块
  （模块 = 要注入游戏进程的独立 DLL，DLSS-NR on AMD 那类；两侧都支持 { "id":"...", "removed": true } 墓碑）。

**客户端**（Features/Plugins/RemoteCatalogService.cs）：

1. 地址前缀 AppConfig.CatalogBaseUrl（默认值在 RemoteCatalogDefaults.BaseUrl，**换仓库只改这一行**）；
2. **每天最多自动拉一次**：AppConfig.LastCatalogFetchUtc，超过 24h 才拉；两个文件都没拿到就不盖时间戳（下次还会试）；
   拉失败只用上次缓存 / 内置表，不打扰用户；
3. 缓存到 <用户数据目录>\.hysx\catalog\{plugins,optiscaler}.json，先写 .tmp 再搬过去（避免半截文件）；
4. 插件侧：PluginHostLocator.ResolveManager() 把缓存文件塞进 ExtensionManagerService.Catalog.ExtraCatalogFiles
   （这个机制本来就有：内置 → 远程 → 本地文件，按 id 覆盖）；
5. OptiScaler 侧：OptiScalerCatalog.MergeWithBuiltin(OptiScalerCatalog.LoadFile(缓存))；
6. 手动拉：设置 → 关于 → **「拉取插件目录」**（RemoteCatalogService.RefreshAsync(force: true)）。

### 9.9 给 OptiScaler 补 nvngx_dlssnr.dll（各分支手册要求的「放在包旁边」）

各分支的手册（wilsjo2 的 INSTALL-DLSSNR.md 第 2/3 步、NeuRotic、DLSS NR on AMD）都写着：解压完整包之后，
**把 `nvngx_dlssnr.dll` 放到同一个目录**。我们这边是注入不是铺游戏目录，所以「同一个目录」= 构建目录
`<用户数据目录>\OptiScaler\<来源>\<版本>\`。

- `OptiScalerRuntime.EnsureNrdll(buildDirectory, addonsDirectory, extraSearchDirectories)`（Extensions 层，有自测）：
  从**插件目录**（「DLL 配置」把运行时装在这儿）或其它构建目录里找一份复制过去；已经有**同样大小**的就什么都不做
  （用户可能自己换过版本，不覆盖）；找不到 → `NotFound`，界面提示去 DLL 配置装一个。
- 触发点：① 装完 OptiScaler（zip / 安装程序）自动补一次；② 卡片上缺运行时的行标黄「缺 nvngx_dlssnr.dll」+「放入 nvngx_dlssnr.dll」按钮。
- 实测背景：用户机器上 `nvngx_dlssnr.dll`（158 MB）本来就在各游戏目录和插件目录里，所以注入的 OptiScaler 直接就能用 ——
  这个功能是给「干净机器」和「新下载的构建目录」兜底的。


（GitHub 更新渠道已经在 1.0.23 落地，设计见 docs/OPEN-SOURCE-PLAN.md §5。）


### 10 插件界面汉化：把 addon 里的英文原地换成中文（开发中）

ReShade 的 addon 界面是 ImGui 画的，文字是**写在 addon DLL 里的 UTF-8 字符串常量**（`.rdata`），
没有 i18n 机制，也不能从外面塞语言包。实测 ReShade 自己的 UI 能显示中文 → 共享的 ImGui 字体图集里已经有 CJK 字形，
所以「直接改 DLL 里的字面量」这条路走得通。

- **只做原地替换**（方案 A）：新串的 UTF-8 字节数 ≤ 旧串时，覆盖末尾字节不足的用空格补齐（NUL 结尾不变）。
  放不下的就跳过并计数（比如 `Reset`(5 字节) 装不下 `重置`(6 字节)）。长句子要换得往 PE 里加节并改指针（方案 B），暂时不做。
- `AddonLocalizer`（Extensions 层，`I18n/AddonLocalizer.cs`，有 11 个自测）：
  - 内置表 `Resources/i18n.builtin.json`（嵌入资源）：`renodx-dlss5` / `renodx-dlss` / `dlss5-bridge` 三张，
    条目形如 `{ "en": "Ultra Performance", "zh": "极致性能" }`。
  - `LoadTables(extraDirectory)` = 内置 + `<用户数据目录>\.hysx\i18n\*.json`（用户可自己加 / 覆盖）。
  - `SelectTable(tables, 插件文件名)`：按**最长的 slug 前缀**匹配，所以 `renodx-dlss-super-anus` 会命中 `renodx-dlss` 这张。
  - `Apply(dllPath, table, backupDirectory)`：先备份到 `<用户数据目录>\.hysx\i18n-backup\<名字>.bak`，
    再在内存里替换所有出现（含同一个字面量出现多次），最后临时文件 + 原子替换；失败不动原文件。
  - `Restore(dllPath, backupDirectory)`：从备份还原；返回 `Applied / SkippedTooLong / Missing / Message / BackupPath`。
- **界面入口**：全局插件页 →「插件文件」每一行有「汉化」和「还原」两个按钮；有备份的行才显示「还原」。
  点完在页面底部写状态提示，重启游戏生效。
- 采集工具：`tools/addon-i18n/harvest-strings.ps1`（扫 `.rdata` 里的可打印 ASCII 串，输出到 `tools/addon-i18n/out/`）；
  目前覆盖：`renodx-dlss5-super-anus` 22 条标签里挑的、`renodx-dlss` 25 条、`dlss5-bridge` 9 条。

**还差的**：① 表只覆盖了一小部分界面文本（`out/*.labels.txt` 里还有没翻的）；② 放不下的长句需要方案 B（PE 加节 + 重定位指针）。

**入口已按用户要求收起**（2026-09-21）：插件页那两个「汉化 / 还原」按钮和「全部汉化」都隐藏了，现在入口在
**「设置 → 实验性功能 → 汉化插件（实验性）」**；实现上多了一个不依赖页面状态的 `AddonLocalizationJob.LocalizeAllAsync()`。

### 10.1 短标签根本不在 .rdata 里 —— 代码立即数（踩坑记录）

用真文件对照才发现，这个插件里**很多界面文本不是字符串**，而是编译器把常量写成 `mov` 立即数、运行时在栈上拼出来：

```
48 B8 55 70 73 63 61 6C 65 64    mov rax, "Upscaled"
48 BE 53 74 72 65 6E 67 74 68    mov rsi, "Strength"     ← 「Skin Structure Strength」的尾巴
48 89 70 1E                      mov [rax+0x1E], rsi
```

所以只改 `.rdata` 时会出现「中文头 + 英文尾」（头从 `.rdata` 读、尾是这个立即数）。现在的做法：

1. **ModRM/SIB 解码器**（`TryParseImmediate`）认出所有「写常量」的指令：`B8+r imm32/imm64`、`C6/C7 /0` 的各种
   ModRM+SIB+disp8/disp32（含 r12 基址、RIP 相对），并且按整条指令长度跳字节；
2. **全局分配**：每个立即数只归一条串。轮次 = 优先级：`长串的一块` > `整条串就在这一个立即数里` > `接在串尾的短块`。
   为什么必须这样：实测 `"Scaling "` + `"g Domain"` 两条立即数拼出的是 `Scaling Domain`，若被短的 `Scaling` 抢走一块，
   重叠区就对不上、中文被切成半个 UTF-8 → 界面显示 `?握采` 这种怪字；
3. **串尾判定只认 NUL**（空格说明后面还接着内容，不能当"整条串结束"）；
4. **单字节尾巴用位移精确接**：某块的位移是 `0x80`、窗口 0、长 8 → 下一字节必须落在位移 `0x88` 的那个
   `mov byte ptr [...], imm8` 上（早期版本靠"附近找字节值对得上的 store"，会认错、偏移错一位就把中文写坏）；
5. 备份按**路径哈希**分开存（`<名字>.<hash>.bak`），同一个插件在多个 HoYoShade 目录里各有各的备份；
6. 点过汉化的插件会记账，**启动游戏前自动重打一遍**（HoYoShade 启动时会把自己的插件部署回原版）。

仍然换不了的：中文比英文长的短词（`On`/`Auto`/`Model`/`About`/`Debug`/`Links`/`Never`/`Reset`/`Area`/`Bicubic`/`Build`）
和 `sRGB`/`PQ`/`scRGB`/`BT.2100` 这类标准名 —— 要全中文只能上方案 B（PE 加新节 + 改引用）。


### 11 XXMI 注入（实验性启动选项）

用户问「能不能像 XXMI 那样做模型替换」。查了 [SpectrumQT/XXMI-Launcher](https://github.com/SpectrumQT/XXMI-Launcher)：

- XXMI 的本质是**把 3DMigoto 的 `d3d11.dll` 注入游戏进程**（`DllInjector`，两种方式：Hook / Inject，默认 Hook = 挂起进程再注入）；
- 每游戏一个实例：GIMI（原神）/ SRMI（星铁）/ ZZMI（绝区零）/ HIMI（崩 3），包体是 `XXMI-Libs-Package`；
- 启动时走 `MigotoManager.StartAndInject(game_exe_path, start_exe_path, start_args, work_dir, use_hook)`；Mods 放在实例目录的 `Mods\`。

我们的实现（`XxmiInjector` + 启动页「启用 XXMI 注入（实验性）」）：

- 复用已有的「等游戏进程出现 → LoadLibrary 指定 DLL」那条路（`DllInjector` + `StartExtraDllInjection`），
  只是把要注的 DLL 换成 XXMI 实例里的 `d3d11.dll`；
- 自动找 XXMI：`<用户配置 hysx_xxmi_root>` → `%AppData%\XXMI Launcher` → `%LocalAppData%\XXMI Launcher` →
  各固定盘浅层目录（目录里有 `XXMI Launcher Config.json` 或 `Resources\Bin\XXMI Launcher.exe` 才算）；
- 找实例：`<根>\ZZMI|SRMI|GIMI|HIMI\d3d11.dll`，退一步认 `<根>\Resources\Packages\XXMI\d3d11.dll`；
- 按游戏记开关（`AppConfig.Get/SetUseXxmiInjectLaunchOption`）。

**已知限制**：我们这条是「进程起来之后再注入」，而 3DMigoto 正常要在 D3D 设备创建前加载（XXMI 用 Hook 挂起进程就是为了这个），
所以对某些游戏可能不生效或崩。要做稳的话下一步是：启动时挂起进程 → 注入 → 恢复（对应 XXMI 的 Hook 方式），
或者干脆只用 XXMI 的注入器来起游戏。





### 12 启动器四条功能（1.3.6b1）

#### (1) 驱动配置：DLSS-FG 多帧生成数量改为 N/A

- 用户要在「兼容性检测」里查这项，并且「启用 opt 启动游戏」时也要查、不对就弹窗。
- **事实来源（别再猜）**：`OptiScaler-MFG-Ada/external/nvapi/` 里有 NVAPI 官方头。
  - `nvapi_interface.h`：全部接口 ID（例如 `NvAPI_DRS_GetSettingIdFromName = 0xcb7309cd`、
    `NvAPI_DRS_FindApplicationByName = 0xeee566b2`、`NvAPI_DRS_SetSetting = 0x577dd202`）。
  - `NvApiDriverSettings.h`：设置项 **ID 与取值**。这条就是
    `NGX_DLSSG_MULTI_FRAME_COUNT_ID = 0x104D6667`（"Override DLSSG multi-frame count"），
    取值 `OFF=0 / MIN=1 / MAX=15 / DEFAULT=OFF`；Profile Inspector 的「N/A」= `0xFFFFFFFF`。
- **结构体布局**（手算，别再照抄网上的 C# 绑定）：`NvAPI_UnicodeString` 是内联的 `NvU16[2048]`（4096 字节），
  且 `NVAPI_BINARY_DATA_MAX = 4096`。于是 `NVDRS_SETTING_V1`：version@0、settingName@4(4096B)、settingId@4100、
  settingType@4104、settingLocation@4108、isCurrentPredefined@4112、isPredefinedValid@4116、
  union@4120(4100B) 2，**sizeof = 12320**，version 字段 = `12320 | (1<<16)`。
  `NVDRS_APPLICATION_V4` sizeof = 20492（version@0、isPredefined@4、appName@8、userFriendlyName@4104、
  launcher@8200、fileInFolder@12296、bits@16392、commandLine@16396）。
- 实现：`Features/Plugins/NvDrsMfgCount.cs`（`nvapi64.dll` + `nvapi_QueryInterface`；
  读/写都走「先读原值  写  回读确认」）；检测项在 `Dlss5CompatibilityCheck.cs` 第 17 条；
  启动前弹窗在 `GameLauncherPage.ConfirmNvMfgCountAsync()`。
- 读不到 / 没覆盖 / 已是 N/A / 写失败一律放行  驱动配置写坏了很难手工找回来，所以宁可不改也不拦。

#### (2) OptiScaler 配置预设

- 布局（用户原话）：卡片最左单选框不变；名称往左贴近单选框、**位置挪到名称右侧**；
  名称下方改成「当前配置：【下拉框】新增 修改」。
- `Features/OptiScaler/OptiScalerPresets.cs`：预设 = `<OptiScaler 根>\presets\<名字>.ini`；
  套用 = 写 `profiles\<游戏>.ini` + `OptiScalerProfiles.Activate()` 顶成主 ini。
  「修改」= 把构建当前生效的 `OptiScaler.ini` 覆盖回预设（`CaptureFromBuild`）。
- `Features/OptiScaler/OptiScalerPresetCatalog.cs`：远端索引 `catalog/optiscaler-presets.json` +
  内容 `catalog/preset-files/*.ini`，取文件按「GitHub 直连  gh-proxy.org  ghfast.top」并复用
  `CloudProxyManager` 的失败冷却。
- **DataTemplate 里只能用绑定 + `Click`**：本仓库已知 `RadioButtons.SelectionChanged` / `Toggled`
  会让 XamlCompiler 静默 exit 1（`OptiScalerPage.xaml` 里有注释）。下拉切换走 `CurrentPreset` 的
  TwoWay setter 回调，不在模板里挂事件。
- 首个远端预设 = 本机崩铁那份：**【40系6倍帧生成 NR50%2层】**（`40x6-nr50-2layers.ini`，13608 字节）。

#### (3)(4) 下载线路：别滥用 + 关于页和卡片要一致

- 实测证据（260924 大日志）：`Failed to fetch releases from server 0/1/2/3` 各 19 次 = 76 个必然失败的请求。
  `FetchLatestStableReleaseAsync` 只被「一键安装」调用，所以 19 次是用户反复重试，但每次都把候选服务器全打一遍。
- 为什么「关于页选了 GitHub，卡片还是 CDN」：**两个键**。关于页/更新窗口写 `LauncherUpdateDownloadServer`，
  HoYoShade/ReShade/OptiScaler 卡片读 `HoYoShadeFrameworkDownloadServer`。现在并成一个 `DownloadServer`
  （`AppConfig.HasValue()` 用来做老键迁移）。
- 自动选择序列原来没有新加的 gh 代理、还把 GitHub 直连排第一。现在：
  gh-proxy.org  ghfast.top  腾讯云  随机(Cloudflare/阿里云)  GitHub 直连兜底，且刚失败的服务器冷却 5 分钟。
- 下载**不是直连**：`cdn.xxx.tx.storage.hub.hoyosha.de/https://github.com/...` 是前缀式代理。
  卡片下方显示 CDN 是如实反映当前线路，不是没生效。




### 13 上游同步（1.3.7-z1）

对照上游 `DuolaD/HoYoShade-Hub` 1.3.7 之后的 8 个提交，按本分支结构手工挑（没有 cherry-pick）。

#### (1) 内测 / Beta 服没有 config.ini 也能启动
- 问题：Beta / 内测 / 创作者体验服常常没有 `config.ini`，读不出版本号，
  Hub 于是判成「游戏未安装」、启动按钮也不可用（上游 `d90b6dd`）。
- 改法：正式服仍要求「exe 在 + 能读出版本」；Beta 服只要主程序 exe 在就当作已安装 / 可启动。
- 文件：`GameLauncherPage.xaml.cs`（`canStart`）、`GameSettingPage.xaml.cs`（`isInstalled`）。

#### (2) 下载服务器的多 host 重试
- 问题：自动选择下载服务器时，每个服务器只随机取**一个** host；那个 host 不通，
  整个服务器就白给，直接跳到下一台（上游 `4a070dd`）。
- 改法：一个服务器名下的**所有** host 按随机顺序逐个试；接口返回空列表也算这次失败；
  手动指定服务器同样走多 host 重试；全失败才抛错。
- 文件：`HoYoShadeDownloadView.xaml.cs`（`GetOrderedProxies` + 取版本列表的两条路径）。

#### (3) 管理员模式下顶部游戏图标无法排列
- 问题：以管理员身份运行 Hub 时，顶部游戏图标拖不动、顺序改不了。
- 根因：WinUI 的拖动重排（`ListView.CanReorderItems`）在提权进程里本来就不可用 ——
  OLE 拖放跨完整性级别会被 UIPI 拦，管理员下开着它还可能崩（microsoft-ui-xaml#7690）。
  Hub 里它被绑成 `CanReorderItems="{x:Bind IsAdmin, Converter=BoolReversedConverter}"`，
  管理员下正好把拖动关掉，而右键菜单里又没有别的排序入口，于是谁也改不了顺序。
- 改法（不依赖拖动）：图标右键菜单加「左移 / 右移」，纯命令式移动
  （`GameBizIcons.Move` → `CollectionChanged` → `SelectedGameBizs` 照旧落盘）；
  菜单打开期间不收起图标行；到边界时对应项置灰。
  另外自定义游戏本来就不写进 `SelectedGameBizs`，它们的顺序仍不落盘（已知限制）。
- 文件：`GameSelector.xaml`（菜单项 + `Opening`/`Closed`）、`GameSelector.xaml.cs`
  （`_isContextMenuOpen` 守卫 + `MenuFlyoutItem_MoveLeft/Right_Click`）。

#### (4) 向导页安装请求漏字段 + 文案走语言资源
- 问题：向导页（快速开始）安装框架的 RPC 请求没带 `EnableEch` / `DohUrl` / `TotalBytes`，
  开了 ECH/DoH 的机器在这里安装不生效，进度条也因为没有总大小算不出百分比（上游 `102ec2d`）。
- 顺手：向导页底部按钮与 ReShade 下载页「下一步」改用 `WelcomeView_HoYoShadeHubStart`（上游 `190254f`）。
- 文件：`QuickSetupView.xaml.cs`、`QuickSetupView.xaml`、`ReShadeDownloadView.xaml`。

#### (5) RenoDX DLSS 也算 DLSS5 一类（用户报「缺少 LoadFromDllMain」）
- 现象：插件页上 `renodx-dlss.addon64`（显示名「RenoDX DLSS」）看不到「从 DllMain 加载」，
  启用它也不会写进该游戏 `ReShade.ini` 的 `LoadFromDllMain`。
- 根因：判「是不是 DLSS5 一类」的两条判据都漏了它 —— 扩展目录给 `renodx.dlss.sf` 的 tag 是
  `dlss`（不是 `dlss5`），文件名判据又只认 slug 里的 `dlss5`，而它的 slug 是 `renodx-dlss`。
  实测这个二进制里同样引用 `nvngx_dlssnr.dll` + `sl.interposer`、同样在 `[RENODX-DLSS]` 段做
  `DirectNeuralRendering`，与 super-anus 那版是同一类。用户手装的（没有扩展目录记录）更是只靠文件名兜底。
- 改法：`AddonFileInfo.IsDlss5ByName` 把 `renodx-dlss*` 整族都算进去（与 `IsHookPointCapable` 同一判据）；
  目录数据（内置 + `catalog/plugins.json`）给 `renodx.dlss.sf` 的 tags 补上 `dlss5`，
  这样「要 `nvngx_dlssnr.dll` / `sl.*`」的依赖检查对它也生效。
- 回归测试：Extensions 自测段 15 改成**完全不提供 `dlss5` 标签**（复现真机手装场景），
  新增 `IsDlss5ByName` 单元判据，段 21 断言 `renodx-dlss` 既被认成 DLSS5 一类、也会自动进 `LoadFromDllMain`。
- 文件：`ReShadeProfile.cs`、`GamePluginService.cs`、`catalog/plugins.json`、
  `catalog.builtin.json`、`Extensions.Tests/Program.cs`。

#### (6) 核对过、本分支已有等价实现（不用再搬）
- `4cef52d`「忽略 DX12 兼容性检测」= 已有（`AppConfig.GetIgnoreDX12Check` + 设置对话框 + `HoYoPlayService`）。
- `9b6e0fa`「安装状态」= 框架下载页（`HoYoShadeDownloadView`）已有已装 / 版本面板。
- `bdadc23` / `23469a6`：独立的框架与启动器自动检查开关、两者「有新版本」提示都已有
  （自研更新渠道 + 24 小时节流）。上游把框架自动检查再拆成 HoYoShade / OpenHoYoShade 两个开关这点没做。
- `687318e`（快速开始页整体重做，含退回全量安装）与本分支「只装必要」语义冲突，不整体搬。




### 14 「AI 插帧」开的时候要一起改低延迟模式（用户反馈）

- 现象：启动器页勾上「AI 插帧」（NVIDIA Smooth Motion）后，驱动里的「低延迟模式」纹丝不动 ——
  这台机器上已经开了 Smooth Motion 的原神 / 星铁，低延迟那几项在驱动里都还是「没存过」。
- 根因（真机探针实测，见 `build/probe/lowlat`）：旧实现先读低延迟原值，
  `if (latency.Ok && …)` 不成立就整段跳过。而驱动 DRS 对**没配过**的设置返回 `-160`
  （`NVAPI_SETTING_NOT_FOUND`，语义是「这条没存过」）是常态：原神 / 星铁 / 绝区零上
  `0x0005F543`、`0x10835000`、`0x007BA09E` 读出来全是 -160。于是「开插帧 → 设 Ultra」从来就没执行过。
- 另一个坑：旧实现只写 `0x0005F543`。按 NVIDIA Profile Inspector 的说明，它只是
  **「Ultra Low Latency - CPL State」**（给控制面板记下拉状态的镜像值，Off/On/Ultra = 0/1/2），
  「不用改」；真正让驱动启用低延迟调度的是 **`0x10835000`「Ultra Low Latency - Enabled」**（Off/On = 0/1）。
- 定值来源（别再猜）：`Orbmu2k/nvidiaProfileInspector` 仓库 `nvidiaProfileInspector/CustomSettingNames.xml`
  里的两条 CustomSetting —— 官方的 `NvApiDriverSettings.h` 里**没有**这两项（NVIDIA 论坛答复也说不通过 NVAPI 支持），
  只能像 Smooth Motion 那样走 DRS 扩展接口（`NvAPI_DRS_GetSettingExtended / SetSettingExtended`）。
- 改法：`ApplySmoothMotionAsync` 里**先写 Smooth Motion 总开关**（顺带保证驱动里有这个游戏的条目），
  再**无条件**调 `ApplyUltraLowLatency`（不再看读没读到原值）：
  读原值（读到就记、读不到记 -1）→ 写 `0x10835000 = 1` → 写 `0x0005F543 = 2`；
  关插帧时 `RestoreUltraLowLatency` 写回原值（没记过就还原成默认的「关」，CPL 镜像值没存过就不动它）。
  低延迟写失败只作为 toast 附注，不再影响 Smooth Motion 本身。
- 验证：同一台真机上用探针确认 `0x10835000 = 1`、`0x0005F543 = 2` 都能写进去、Save 后回读一致（探针里已还原）。
- 文件：`Features/Plugins/NvDrsInterop.cs`（新增 `UltraLowLatency*` 常量）、
  `Features/GameLauncher/GameLauncherPage.xaml.cs`（`ApplyUltraLowLatency` / `RestoreUltraLowLatency`）。




### 15 新插件「DLSS5 Feed」+ 一并装上并启用 LumeniteFX 滤镜（用户要求）

- 需求（用户原话）：「新增插件：dlss5 feed(用于原神这种没有 dlss 的 dx11 游戏)…他好像还得装个滤镜的，启用这个插件要一并装上并启用」。
  用户选的做法：**直接改当前共享预设，切游戏时改回去**（不新建游戏专用预设）。
- 事实来源（别再猜）：DLSS5-Feeder 的 README（`jlrouzies-fr/DLSS5-Feeder`）「Install for a 64-bit game」——
  下载 `dlss5-feed.addon64` 放游戏 exe 旁边、`DLSS5_Feed.fx` 进 `reshade-shaders\Shaders\`；
  动作矢量来源装 **LumeniteFX**（`umar-afzaal/LumeniteFX`，分支 `mainline`）的 `Shaders/` + `Textures/`；
  然后在 ReShade 叠加层里勾 `LUMENITE: Kernel 2.0`、把 `DLSS 5 Feed` 排在它下面，
  并给 `DLSS5_Feed.fx` 设预处理器定义 `DLSS5_MV_PROVIDER=3`（3 = LumeniteFX Kernel，见 fx 里的 provider 表）。
- 目录条目（内置 + `catalog/plugins.json` 各一份）：
  - `dlss5.feed`：github-release `jlrouzies-fr/DLSS5-Feeder`（`DLSS5-Feeder-*.zip`，要 `includePrerelease`），
    规则 `*.addon64` → `reshade-shaders/Addons`、`reshade-shaders/Shaders/*.fx` → `reshade-shaders/Shaders`；
    `requires: ["lumenitefx"]`；tags 带 `dlss5`（所以启用时自动进 `LoadFromDllMain`）。
  - `lumenitefx`：direct 取 `codeload.github.com/.../refs/heads/mainline`，规则把 `LumeniteFX-mainline/Shaders/**`、
    `Textures/**` 铺进 `reshade-shaders/Shaders`、`Textures`。它**没有** Addons 规则，所以不会出现在「全局插件」列表里，
    只在 Feed 需要时作为依赖自动装。
- `requires` 是这次新加的清单字段：`ExtensionManagerService.InstallAsync` 装完主包后按目录把依赖也装上
  （已装过跳过、成环检测、依赖失败不打断主包）。
- 效果开关的写法：**只存在于预设文件里**（`ReShade.ini` 的 `[GENERAL] PresetPath` 指的那份），所以新增
  `ReShade/ReShadePresetEditor.cs`：读预设 → 往根键 `Techniques=` / `TechniqueSorting=` 补
  `Lumenite_Kernel@lumenite_Kernel.fx`、`DLSS5_Feed@DLSS5_Feed.fx`（顺序有意义：provider 在前），
  往根键 `PreprocessorDefinitions=` 补 `DLSS5_MV_PROVIDER=3`；关的时候只摘这两个效果和这一条定义
  （`TechniqueSorting` 是全集清单，故意留着）。别人写的定义一律不动。
  - 坑：ReShade 预设的这几个键写在**第一个 `[` 之前**，没有节头。原来的 `IniDocument` 只认「节+键」，
    给它加了 `IniDocument.RootSection`（空字符串）表示根键，`FindSectionBounds` 里根节范围 = 开头到第一个节头。
- 接线：
  - 每游戏插件页开/关 `dlss5-feed*.addon64` → `GamePluginService.SetAddonEnabled` 顺带改预设（并给一句提示）；
  - 切游戏 → `MainView.GameSelector_CurrentGameChanged` / `GamePluginPage.LoadGame` 调
    `GamePluginService.SyncDlss5FeedPreset`（幂等）：当前游戏开着 Feed 就补上、没开就去掉。
    本机所有游戏的 `PresetPath` 都是共享的 `Presets\Mod OFF.ini`，所以这一步是必须的。
- 验证：扩展自测离线 **PASS 389 / FAIL 0**（新增第 27 段：根键 ini + 预设开关 + 幂等 + 目录条目）；
  联网 `--online` **PASS 454 / FAIL 0**（`dlss5.feed` 的 tag/资产解析、远端 `catalog/plugins.json` 逐条校验都过）。
  LumeniteFX 包结构（`LumeniteFX-mainline/Shaders|Textures`）与 Feed 包结构（`dlss5-feed.addon64` +
  `reshade-shaders/Shaders/DLSS5_Feed.fx`）都下载确认过。
- 文件：`Extensions/ReShade/ReShadePresetEditor.cs`（新）、`Extensions/ReShade/ReShadeProfile.cs`、
  `Extensions/Games/GamePluginService.cs`、`Extensions/Models/ExtensionManifest.cs`、
  `Extensions/Services/ExtensionManagerService.cs`、`Extensions/Resources/catalog.builtin.json`、
  `catalog/plugins.json`、`Features/Plugins/GameCatalog.cs`、`Features/Plugins/GamePluginPage.xaml.cs`、
  `Features/ViewHost/MainView.xaml.cs`、`Extensions.Tests/Program.cs`。




### 16 兼容性检测第 12 项：ini 路径「检测」与「修复」的两个 bug（用户反馈）

- 现象（用户给的 OCR）：卡片上明明写着 `[ADDON] AddonPath =（没写）`、还给了「指回当前 HoYoShade」按钮，
  点下去却提示「没有需要改的键（路径本来就对得上）。」—— 检测和修复互相打脸。
- 根因①（修复点了等于没点）：`ShadePathAligner.Align` 遍历 `PathKeys` 时
  `if (string.IsNullOrWhiteSpace(value)) continue;` —— 空值直接跳过，所以**缺 AddonPath 永远补不上**。
  而第 12 项 `CheckIniPaths` 把「没写 AddonPath」报成 Error 并把 fix 指向 `AlignIniPaths`：报得出、修不了。
- 根因②（检测漏判）：第 12 项对 EffectSearchPaths 只做 `Directory.Exists`，不判断它指向**哪个** HoYoShade。
  指着另一个启动器、而那个目录还在 → 判成「路径都对得上当前 HoYoShade」。
  但游戏目录那份 ini 是游戏里的 ReShade 读的，指到别的安装就等于当前 HoYoShade 的着色器根本没被用。
- 改法：
  - `ShadePathAligner.Align`：空值且键是 `[ADDON] AddonPath` 时补上**绝对**路径
    `<当前 HoYoShade>\reshade-shaders\Addons` 并记进 `ChangedKeys`。
    写绝对而不是相对：第 12 项和 `ReShadeProfile.ResolveAddonDirectory()` 都会拿这个值直接
    `Directory.Exists` / 拼路径，游戏目录那份 ini 里的相对路径会被算成「目录不存在」。
  - `CheckIniPaths`：EffectSearchPaths + TextureSearchPaths 一起查、两种问题都报 ——
    ① 目录不存在；② `ShadePathAligner.RootOf` 能反推出根、且和当前 `ShadeHost.RootPath` 不一致
    （报「搜索路径指到别的 HoYoShade：…（当前是 …）」）。卡片里 TextureSearchPaths 也单独列一行
    （以前标题写了它、内容只显示 EffectSearchPaths）。
- 验证：扩展自测离线 **PASS 393 / FAIL 0**（新增断言：缺 AddonPath 补成绝对路径、幂等、
  别的 HoYoShade 的搜索路径能反推出根）。
- 文件：`Extensions/ReShade/ShadePathAligner.cs`、`Features/Plugins/Dlss5CompatibilityCheck.cs`。




### 17 版本列表：atom 的 updated 不是发布时间（Veritas 顺序/时机都错，用户反馈）

- 现象（用户）：模块「Veritas（星铁伤害统计 / ACT）」的版本下拉里，0.2.52 排在 0.2.49/0.2.50/0.2.51 后面，
  显示的时间还比它们早 —— 顺序和时机都不对。
- 根因：`GithubReleaseResolver` 把 atom 里的 `<updated>` 当成 Release 的 **published_at**（注释就是这么写的），
  但它其实是 **updated_at（最后编辑时间）**。实测与 GitHub API 对照：
  - 0.2.52：published_at = 2026-06-23，atom updated = 2026-07-15
  - 0.2.51：published_at = 2026-06-04，atom updated = 2026-07-17
  - 0.2.49：published_at = 2026-06-02，atom updated = 2026-07-17
  作者后来批量编辑过旧 release，于是「编辑最晚的」变成 0.2.49/50/51 → 按 updated_at 倒序把它们排到了最前。
  坏处不只是显示：代码里 `versions[0]` 就是「装最新」，会装成 **0.2.49** 而不是 0.2.52。
- 顺带发现：`ListVersionsCoreAsync` 的分页结束条件是「这一页没加进新 tag」—— 第一页 10 条和 atom 完全重复，
  于是直接 break，**第 2 页以后的版本永远读不到**（Veritas 有 25 个 release，下拉里只有 10 条）。
- 改法：
  - atom 的日期降级成「低置信度兜底」，releases 列表页卡片里的 `datetime`（= published_at，
    实测和 API 一致）为高置信度；同一 tag 后到的可靠值**覆盖**先到的值（以前先到先得，被 atom 挡住了）。
  - 分页结束条件改成「这一页没有任何卡片」，第一页和 atom 重复也继续翻。
  - 找「最新 tag」的主路径改成 releases 列表页（按卡片发布时间取最晚的那条），atom 只做兜底 ——
    以前 atom 用 updated_at 比大小会挑错（Veritas 会挑成 0.2.51）。
  - 版本列表缓存键 `ver|` → `ver3|`，把旧的错列表直接作废（用户本机 `%LOCALAPPDATA%\HoYoShadeHub\github-cache`）。
- 验证：扩展自测联网 **PASS 461 / FAIL 0**，其中新增第 11.3 段实测 `hessiser/veritas`：
  第一条 **0.2.52**、20 条版本严格倒序、0.2.48 的发布时间是 2026-05-02（修复前显示 2026-06-05）。
- 文件：`Extensions/Services/GithubReleaseResolver.cs`、`Extensions/Models/ExtensionVersion.cs`、
  `Extensions.Tests/Program.cs`。




### 18 模块「已装」不能换版本（用户反馈；插件 / OptiScaler 本来就有）

- 现象（用户）：「Veritas 已下载的居然不能切换版本？其他的有类似情况吗」。
- 三边对比（全局插件页三个标签页）：
  - 「插件」卡片：展开后有版本下拉 + 「切换 / 重装这个版本」；
  - 「OptiScaler」卡片：展开后有版本下拉 + 换版本（「装过后下拉默认当前版本（带 (当前)）」）；
  - 「模块」**已装**卡片（`ModuleItemViewModel`）：只有「检查更新」，没有版本下拉 ——
    装完它就从「可下载」挪进「当前」，而版本下拉只挂在「可下载」那张卡（`ModuleDownloadItemViewModel`）上。
  结论：只有模块缺这个能力，插件和 OptiScaler 都有。
- 改法：
  - `ModuleItemViewModel` 加：`Definition`、`CurrentTag`（`ModuleRegistry.InstalledTag` 读下载器写的 `build.json`）、
    `CanSwitchVersion`（内置且非「仓库树直连」型）、`Versions` / `SelectedVersion` / `ApplyVersions`
    （当前那个带「(当前)」并默认选中）、`SwitchVersionText`（选中当前 → 「重装这个版本」，否则「切换到这个版本」）。
  - XAML：模块「当前」卡片展开区加版本下拉 + 按钮，和插件卡同款。
  - 展开卡片时懒加载版本列表（`Button_ModuleHeader_Click` → `LoadModuleVersionsAsync(item)`），
    和「可下载」卡片共用一个 `ListModuleVersionsAsync(module, force)`。
  - 装完 `RefreshModules()` 后把版本列表重新铺一遍，否则重建出来的卡片下拉是空的、「(当前)」也不对。
- 顺带修一个「切了没生效」的坑：模块目录布局是 `<模块>\<来源>\<tag>\`，换 tag 会**新建**一个目录、
  旧的留着；而 `FindInjectDll` 是递归扫所有 dll 按文件系统顺序挑 —— 可能挑到旧版本。
  现在 `ModuleRegistry.InstallAsync` 装完会调 `PruneOtherVersions`，把同来源的其它版本目录清掉
  （模块只保留一个版本）。
- 验证：x64 Release 发布 0 错误；部署后在 `HoYoShadeHub.dll` 里核对到「切换到这个版本」「重装这个版本」
  「模块换版本失败」「PruneOtherVersions」。
- 文件：`Features/Plugins/GlobalPluginPage.xaml(.cs)`、`Features/Modules/ModuleRegistry.cs`。




### 19 模块多版本共存 + 每游戏选版本（用户要求 ①②③ 的模块部分）

- 需求（用户）：① 换版本保留旧版本（缓存，反复切换不用重下）；② 同名不同版本可同时存在；
  ③ 游戏 A 用 1.0、游戏 B 用 1.1 —— 在「插件 / OptiScaler / 模块」页对应卡片切换要用的版本。
  追加：要兼容旧版，做「版本更新引导」一键移动存储库并给现有游戏建对应库。
- 现状：模块目录本来就是 `<用户数据>\Modules\<模块 id>\<来源>\<tag>\`，多版本天然能共存；
  但 §18 刚加了「换版本删旧目录」，按这次要求改掉。
- 改法（本轮只做模块）：
  - `ModuleRegistry.InstallAsync` **不再**清理同来源旧版本（`PruneOtherVersions` 整个删掉）。
  - 新增 `InstalledTags(module)`（新→旧）与 `VersionDirectory(module, tag)`。
  - `ResolveDllPath(module, versionTag)`：指定版本时**只**在那个版本目录里找；没指定时优先
    「最新装的那份」（`OptiScalerLibrary.List()` 按安装时间倒序），最后才递归兜底 ——
    直接递归扫整个模块目录会按文件系统顺序随机挑到旧版本。
  - `ResolveInjectionDlls(gameId)`：按 `AppConfig.GetModuleVersion(gameId, moduleId)` 选中的版本注入。
  - `AppConfig.GetModuleVersion/SetModuleVersion`（每个游戏一份，键 `module_version_<id>`）。
  - 「模块」页（左侧按游戏）每行加版本下拉：只有一个版本就不显示；选的版本被删了自动退回最新。
- 兼容性：模块存储位置与目录结构都没变 —— 旧安装只有一份版本，默认就解析到它，**不需要迁移**。
  存储库搬到启动器 `cache\` + 一键升级引导（需要迁移的那部分）在后续步骤做。
- 验证：x64 Release 发布 0 错误；部署后在 `HoYoShadeHub.dll` 里核对到
  `GetModuleVersion` / `InstalledTags` / `VersionDirectory`。
- 文件：`Features/Modules/ModuleRegistry.cs`、`Features/Modules/ModulesPage.xaml(.cs)`、`AppConfig.cs`。




### 20 插件 / OptiScaler 多版本 + 每游戏选版本 + 启动器缓存 + 一键迁移（用户要求 ①②③④；模块见 §19）

- 需求：①换版本保留旧版本（启动器目录里的缓存，反复切换不用重下）；②同插件多版本共存；
  ③游戏 A 用 1.0、游戏 B 用 1.1 —— 插件 / OptiScaler / 模块页各自切换；
  ④全局插件页卡片列出「已安装版本」（每行文本 + 删除），下面才是全部版本下拉，选中已装版本时按钮变「重装」。
- 存储：`AppConfig.CacheRoot` = 便携版根目录 `cache\`（非便携回退 `<用户数据>\.hysx\cache`）：
  - `cache\plugins\<扩展 id>\<tag>\` 插件版本归档（安装后归档，旧版本保留）
  - `cache\optiscaler` / `cache\modules` 迁移后的 OptiScaler / 模块库
  - `cache\games\<游戏>\Addons\` 每个游戏的插件包（硬链接，跨盘退回复制）
- 每游戏选版本：`AppConfig.Get/SetPluginVersion`、`Get/SetOptiScalerVersion`（模块见 §19）。
  插件换版本时 `GameAddonPackService.Sync(gameId, …)` 重拼该游戏的 addon 目录，并把该游戏
  `ReShade.ini` 的 `[ADDON] AddonPath` 指过去；`ShadePathAligner` 不再把它对回共享目录。
  启动 / 注入前（`GameLauncherPage`）也会 Sync 一次。
- 一键迁移（版本更新引导）：`CacheMigrationPlanner` + `CacheMigrationService`（`NeedsMigration()` 检测旧布局），
  全局插件页弹一次引导，也可手动再跑：搬 OptiScaler / 模块库进 cache（跨盘复制+删）、归档当前插件版本、
  给所有游戏建插件包并改 AddonPath；先备份到 `<用户数据>\.hysx\migrate-backup\<时间戳>\`。
  迁移前 resolver 回退读旧位置 —— 不点引导也照常能用。
- 验证：x64 Release **0 错误**；扩展自测 **PASS 427 / FAIL 0**（含迁移规划 / 版本归档 / 硬链接等新断言）。
- 文件：`Extensions/Services/AddonVersionStore.cs`、`HysxFileLink.cs`、`CacheMigrationPlanner.cs`（新）、
  `Extensions/ReShade/GameAddonPack.cs`（新）、`Features/Plugins/GameAddonPackService.cs`、`CacheMigrationService.cs`（新）、
  `Features/Plugins/GlobalPluginPage.xaml(.cs)`、`GamePluginPage.xaml(.cs)`、`Features/GameLauncher/GameLauncherPage.xaml.cs`、
  `Features/Modules/*`、`AppConfig.cs`、`Extensions.Tests/Program.cs`。




### 21 修：全局插件页卡死 + 引导改成启动时弹（用户反馈）

- 现象①：进「全局插件」页面卡死。根因：`RefreshCatalogThenPageAsync` 末尾 `await MaybePromptMigrationAsync()`
  —— 页面导航 / 刷新还没走完就 `await ContentDialog.ShowAsync()` 弹模态框，WinUI 下这个 await 回不来，
  页面就挂住了（所以对话框根本没显示，用户只看见右上角那个手动按钮）。
- 现象②：用户要求「版本更新引导」直接在**启动时**弹，不要按钮。
- 改法：
  - 弹窗 + 迁移 + 结果对话框整体搬到 `CacheMigrationService.PromptIfNeededAsync(XamlRoot, logger)`，
    改在 `MainView.MainView_Loaded`（窗口已显示之后）fire-and-forget 调用一次。
    `NeedsMigration()` 会同步读扩展账本（sync-over-async），用 `Task.Run` 丢后台，别卡启动。
  - 全局插件页**删掉**「版本更新引导」按钮（XAML + handler）和刷新流程里的自动弹窗调用。
  - 顺带：新加的「重拼所有游戏的插件包」`GameAddonPackService.SyncAll()` 从 UI 线程挪到 `Task.Run`
    （`QueueAddonPackSync()`，带重入保护）—— 纯磁盘活，同步跑同样会卡页面。
- 验证：x64 Release **0 错误**；部署后核对到 `PromptIfNeededAsync` / `QueueAddonPackSync`，按钮 handler 已不在。
- 文件：`Features/Plugins/CacheMigrationService.cs`、`Features/Plugins/GlobalPluginPage.xaml(.cs)`、`Features/ViewHost/MainView.xaml.cs`。




### 22 修：迁移因为 OptiScaler 目录「Access denied」而失败（用户反馈）

- 现象：一键升级报告「optiscaler 搬迁失败（原目录保留）：Access to the path … is denied」；
  modules 已成功搬进 `cache\modules`。手动 `Rename-Item` 也一样被拒 —— Windows 在**目录里有文件被打开**时
  会拒绝整目录改名（注入型 dll / 日志被别的进程（游戏 / 注入器）打开着）。
- 改法：`CacheMigrationService.MoveStore` 不再依赖 `Directory.Move` 成功：
  同盘改名被拒 → 退回复制（`CopyTree`）再尽力删源；**老目录删不掉只记一条「两边都保留」的冲突，不算失败**，
  这样迁移能收尾并置上迁移标记（否则每次启动都重试、永远失败）。App 用 cache 里那份。
- 验证：x64 Release 0 错误；已部署 z1k。老目录可在没有游戏 / 注入进程占用时自行删除。
- 文件：`Features/Plugins/CacheMigrationService.cs`。



### 23 每游戏版本 UI + 每游戏插件包感知 + 运行时 dll 归档 + 注入换进程重试（用户要求 7 条 + 追加 2 条）

> 本轮把「同一个插件 / OptiScaler / 模块多版本」这套系统收口到三个页面的 UI 上，并补上运行时 dll 的版本归档、
> DLSS5 兼容性检测对每游戏插件包的识别，以及「注入目标中途换 pid」的重试。以下逐条：问题 → 根因 → 改法 → 文件。

#### 23.1 每游戏插件版本下拉搬进插件卡片（要求 1）
- 问题：每游戏插件页把「这个游戏用哪一版」做成了右侧配置栏里一个独立的 `Panel_PluginVersions` 面板，
  只有某个扩展归档了 >=1 个版本才整体出现，用户看不出「这行属于哪个插件」。
- 根因：版本选择按「扩展」而不是按「addon 文件」铺，和左边的插件卡片是两套列表。
- 改法：
  - `AddonItemViewModel` 自己带 `VersionOptions` / `SelectedVersionOption` / `VersionChoiceVisibility` /
    `VersionChoiceHint` / `SelectedVersionTag`，用 **TwoWay 绑定 + setter 回调**（数据模板里不能挂 `SelectionChanged`，
    和 OptiScaler 配置选择同一套绕法）；归档里 **>=2 个版本**才显示这条下拉，否则整条不出现。
  - addon 文件 → 扩展 id 靠新增的 `ExtensionAddonMatcher.MatchExtensionId(...)` /
    `GameCatalog.ExtensionIdOfAddonFile(...)`（就是目录里的 `addonPatterns` 认领，和红/黄标同一套判据）。
  - 版本 tag 从 `AddonVersionStore.InstalledTags(extId)` 来；换了版本仍然走 `GameAddonPackService.Sync(...)`
    重拼这个游戏的插件包 + 重写 ini 的 AddonPath。
  - 删掉右侧那个 `Panel_PluginVersions`（XAML + `BuildVersionChoices` + `PluginVersionChoiceViewModel` 整段），避免两套入口。
- 文件：`Features/Plugins/GamePluginPage.xaml(.cs)`、`Features/Plugins/GameCatalog.cs`、
  `Extensions/Services/ExtensionAddonMatcher.cs`。

#### 23.2 每游戏 OptiScaler 版本选择：按来源分组 + 「使用中」+ 启动路径兜底（要求 2）
- 问题：OptiScaler 页虽然有构建单选，但是一锅端铺出来、看不出哪个来源，也没有「这个游戏现在用哪一版」的角标。
- 根因：`OptiScalerLibrary.List()` 是全局按安装时间倒序，页面没分组；`Enabled` 只跟全局 `state.json` 走。
- 改法：
  - `Load()` 按 `SourceId` 分组（`GroupBy`，组内保持 List 的「新装在前」），每组第一张卡片显示来源标题
    （VM 加 `GroupHeader` / `GroupHeaderVisibility`）。
  - 选中的构建（`AppConfig.GetOptiScalerVersion(gameId)`）加 `InUseVisibility` 角标「使用中」。
  - 选择明确走 `AppConfig.SetOptiScalerVersion(gameId, buildId)`（= `SetSelectedOptiScalerId`，同一份键）。
  - **启动/注入路径核对**：`AppConfig.GetSelectedOptiScalerDll(gameId)` 本来就是「这个游戏选过就用那个构建」，
    现在补上「选过的构建已被删 → 退回全局 `state.json` 的选择」，不会突然不注入。`StartExtraDllInjection` /
    `ConfirmGameDlssgAsync` / `Dlss5CompatContext.ResolveDelivery` 都走这个方法，所以天然按游戏生效。
- 文件：`Features/OptiScaler/OptiScalerPage.xaml(.cs)`、`AppConfig.cs`。

#### 23.3 「每游戏插件包」不再是「路径不一致」（要求 + 追加）
- 问题：插件页的路径提示 / 兼容性检测第 12 项会把 `AddonPath = <CacheRoot>\games\<游戏>\Addons` 报成
  「和当前 HoYoShade 的插件目录不一致，点『指回当前 HoYoShade』」—— 但每游戏插件包正是新系统的正常状态，
  点那个按钮反而会把包路径改回共享目录，让这个游戏用错版本。
- 根因：这几处判据只比 `AddonPath == 宿主 AddonsPath`，不知道 `pack.json` 标记（`GameAddonPack.IsPackDirectory`）。
- 改法：
  - `GamePluginPage.UpdatePathHint`：包路径 → 中性说明「这个游戏用的是专属插件目录（…），红/黄标就是按它算的」
    并**收起**「指回当前 HoYoShade」按钮；只有「非包 + 和当前宿主不一致」才保留原来的红字 + 按钮。
    `Button_AlignIni_Click` 也加了同样的防御（读到包路径就直接说明，不做对齐）。
  - `Dlss5CompatContext.AlignIniPaths`：包路径时说明「AddonPath 不会被动，只对齐其它路径」。
  - `Dlss5CompatibilityCheck` 第 12 项：`usesPack` 时不再报「不一致」，并在结果里写「这个游戏用的插件版本：id = tag」；
    修复按钮文案在包场景下改成「对齐其它路径」。
  - `AsciiPathHelper` 改名提示补一句「用专属插件目录的游戏自动跟着当前 HoYoShade 走，不用管」。
- 文件：`Features/Plugins/GamePluginPage.xaml(.cs)`、`Features/Plugins/Dlss5CompatContext.cs`、
  `Features/Plugins/Dlss5CompatibilityCheck.cs`、`Features/Plugins/AsciiPathHelper.cs`。

#### 23.4 全局插件页「已安装版本」行看得见 + 每行一个删除按钮（要求 3）
- 问题：插件 / OptiScaler / 模块卡片里的「已安装版本」行只在**展开后**才渲染，而且插件那边 `cache\plugins`
  可能压根没归档过，用户就以为功能没做。
- 根因：① 展开摘要只在 expanded 区域；② `SummaryText`（收起时那一行）没带「已安装版本：N 个」；
  ③ 归档为空时一行都没有；④ 行里的删除按钮是个「删除」文字按钮，不够显眼。
- 改法：
  - 三个 VM（插件 / OptiScaler 构建 / 模块）的收起摘要都补上「已安装版本：N 个」，并在 `SetInstalledVersions` 里
    触发 `SummaryText` / 按钮文案的 `OnPropertyChanged`。
  - 插件侧加兜底：归档为空、但账本里有一个已装版本时，也补一行（`isCurrent: true`），并且把该 tag 记进
    `ArchivedTags`，让「下载 / 重装」判断仍然对。
  - 三个行模板的删除按钮统一成 **FontIcon（`&#xE74D;`）+ ToolTip**，不再用文字。
- 文件：`Features/Plugins/GlobalPluginPage.xaml(.cs)`。

#### 23.5 模块 / OptiScaler 的按钮文案统一成「下载 / 重装」（要求 4、5）
- 问题：模块「当前」卡片是「切换到这个版本」，OptiScaler 是「更新到此版本 / 下载并安装」，同一个语义三种说法。
- 根因：各 VM 各写各的判断，且模块那个用 `CurrentTag`（当前装的那个）而不是「选中的版本装没装过」。
- 改法：规则统一成「选中的版本没装过 → `下载`，装过 → `重装`」：
  - `ModuleItemViewModel.SwitchVersionText`：看 `InstalledVersionRows` 里有没有这个 tag。
  - `OptiScalerBuildItemViewModel.ActionText`：新增 `InstalledVersions` 集合（同来源装过的所有 tag），
    由 `RefreshOptiScaler` 填。
  - `OptiScalerSourceItemViewModel.ActionText`：`重装` / `下载`。
  - `ModuleDownloadItemViewModel` 加 `ActionText` + `InstalledTags`，XAML 按钮从硬编码「下载 / 更新」改成 `x:Bind`。
- 文件：`Features/Plugins/GlobalPluginPage.xaml(.cs)`。

#### 23.6 运行时 dll 走版本归档：`<CacheRoot>\dlls\<family>\<version>\`（要求 6，用户选的 B 方案）
- 问题：`nvngx_dlssnr.dll` / `sl.*.dll` / `nvngx_dlss*.dll` 由 `DllInstaller` 直接解压进共享 Addons 目录并覆盖，
  绕过版本系统 —— 换版本看不到历史，也没法单独删。
- 根因：`DllInstaller.InstallAsync` 只认 `addonsDirectory`，装完不归档。
- 改法（保持「共享目录里永远一份当前生效版本」不变，ReShade 照旧能用）：
  - 新增 `DllVersionStore`（Extensions 层）：`<CacheRoot>\dlls\<familyId>\<version>\`，硬链接优先、跨盘复制，
    幂等；`Archive` / `ListVersions` / `InstalledVersions` / `DeleteVersion` 全部不抛。
  - `DllConfigPage.RunInstallAsync` 装成功后**尽力**归档（`store.Archive(family, version, 装出来的文件全路径)`）；
    归档异常只写日志，**绝不影响安装结果**。
  - `DllFamilyViewModel` 加 `InstalledVersionRows` / `InstalledVersionsSummary` / `CurrentVersion`；
    卡片里列出归档版本（当前那份带「使用中」）+ 每行一个删除（`Button_DeleteDllVersion_Click`，只删归档、不动共享目录）。
- 文件：`Extensions/Dlls/DllVersionStore.cs`（新）、`Features/Plugins/DllConfigPage.xaml(.cs)`、`Extensions.Tests/Program.cs`。

#### 23.7 DLSS5 兼容性检测适配新系统 + 新增第 20 项（要求 7）
- 问题：第 12 项误报「AddonPath 不一致」；而且没有任何一条检查「每游戏插件包和共享目录 / 归档是否同步」。
- 根因：见 23.3；另外包目录的 `pack.json` 语义（选了哪些版本）没有地方读。
- 改法：
  - `GameAddonPack.ReadSelections(addonDirectory)`：读 `pack.json` 的 `extId → tag`。
  - 新增 `GameAddonPackAudit.Inspect(cacheRoot, gameKey, sharedAddonsDirectory)`：只读比对包目录和
    共享目录 + 归档 —— 缺归档版本 / 包里缺文件 / 硬链接断了（`HysxFileLink.IsUpToDate`）/ 包里多出文件。
  - 新增第 **20** 项 `CheckAddonPack`（分组「游戏目录」）：报告这个游戏用的是共享目录还是专属包、
    在用的插件版本（`id = tag`）、包文件数；不同步时给「重拼插件包」（调 `GameAddonPackService.Sync`）。
  - 结论：游戏级检查（第 8 / 20 项）一律按 `GamePluginService.AddonDirectory`（= 这个游戏 ini 的 AddonPath，
    有包就是包）算，不再拿共享目录代表游戏。
- 文件：`Extensions/ReShade/GameAddonPack.cs`、`Features/Plugins/Dlss5CompatibilityCheck.cs`（UTF-16LE！）、
  `Extensions.Tests/Program.cs`。

#### 23.8 注入目标「换了 pid」不再算注入失败（追加要求）
- 现象（用户）：星铁 + Veritas 连续启动 2 次，第 1 次报「注入失败」。日志里 `injection ok (... -> pid 37868)` 紧接着
  `System.ArgumentException: Process with an Id of 37868 is not running` —— 第二次 pid 45636 才活下来。
- 根因：星铁会先起壳进程再重启本体（反作弊 / 自身更新同理）。`InjectExtraDllsAsync` 拿到第一个 pid、
  `CreateRemoteThread` 返回成功就收工；那个 pid 随即退出，DLL 跟着没了。现有代码只在「等进程」和「逐条注入」
  两个阶段有日志，没有「目标存活确认 / 换进程重试」。
- 改法：
  - `DllInjector.WaitForProcessAsync` 加可选 `excludeProcessIds`（跳过已经注过的 pid），新增 `IsProcessAlive(pid)`。
  - `InjectExtraDllsAsync` 改成最多 **3 次尝试、共用 20 分钟预算**的循环：等一个没注过的同名进程 → 注入 →
    **隔 8 秒确认 pid 还在**；在 → 成功、挂 `Exited` 钩子；不在 → 记 `target-exited-retry` 日志 + 提示条
    「像是壳进程 / 更新重启，正在等新进程重试」，换新 pid 再注。已经注过的 pid 用 `HashSet<int>` 排除。
  - 每步日志：`injection attempt`、`injection ok/failed`、`target-exited-retry`、`injection finished on stable target`；
    最终失败只提示一次，并写清楚是哪一步（没等到进程 / 打开进程失败（权限或反作弊）/ LoadLibraryW 返回 0 / 目标进程中途退出）。
- 文件：`Features/GameLauncher/DllInjector.cs`、`Features/GameLauncher/GameLauncherPage.xaml.cs`。

- 验证：x64 Debug 构建 **0 错误**；扩展自测 **PASS 446 / FAIL 0**（新增第 29 段：addon→扩展归属、`pack.json`
  版本选择、插件包体检（缺文件 / 硬链接断了 / 归档缺失）、`DllVersionStore` 归档与删除）。
- 文件（本轮全部）：见上面各条；新增 `Extensions/Dlls/DllVersionStore.cs`，文档本节。



#### 23.9 「注入时机（秒）」：全局默认 + 按模块 / 按插件 / 按 OptiScaler 覆盖（追加要求）
- 现象（用户实测）：星铁 + Veritas 第一次启动注入后游戏弹窗报错、进程消失，`veritas.log` 里**没有本次条目** ——
  不是「进程被换掉」，是**注入太早**：进程一出现（500ms 轮询）就 `LoadLibraryW`，那时游戏还在初始化自己的模块。
- 改法：把「注入前等进程稳一稳」做成可配的**注入时机**，并允许三处各自覆盖全局默认。
  - **全局默认（按游戏）**：`inject_warmup_enabled_{biz}`（默认 **true**）/ `inject_warmup_seconds_{biz}`
    （默认 **4** 秒，范围 0~30，0 = 立即）。入口：启动器页「开始游戏」右边的设置对话框 →「额外注入 / 预热」页
    （这一页现在重新可见了，导航项从 Collapsed 改回显示）。
  - **模块（按模块 id，和游戏无关）**：`module_inject_delay_{模块 id}`，没设过 = null = 跟随全局默认。
    两处 UI：① 左下「全局插件 → 模块」每张卡片的展开区；② 左侧「模块」页每行。两处读写同一个值。
  - **插件 / ReShade（按游戏）**：`shade_inject_delay_{biz}`，null = 跟随全局默认。UI：
    「插件」页右侧那一栏（和 HookPoint / 强制 off 同一列）。因为 inject.exe 内部的等待改不了，
    这个值在**游戏已经在跑**时才生效：先等它稳一稳再起 inject.exe；没在跑就照旧立刻架好（晚了会错过 ReShade 早期 hook）。
  - **OptiScaler（按游戏）**：`opti_inject_delay_{biz}`，null = 跟随全局默认。UI：左侧「OptiScaler」页顶部栏「注入时机」下拉。
  - 下拉选项统一是「默认（跟随全局）」+ 0/2/4/6/8/10 秒，文案统一叫「注入时机」，旁边一句「越早注入越省事，
    但游戏刚启动就注可能把它带崩；默认 4 秒，0 = 立即」。实现见 `Helpers/InjectionDelayOptions.cs`。
  - 注入侧：`InjectDllSpec` 多了 `DelaySeconds`（模块用 `GetModuleInjectDelayEffective`、OptiScaler 用
    `GetOptiScalerInjectDelayEffective`、其它用全局默认）；`InjectExtraDllsAsync` 在**每一项注入前**按它自己的秒数
    调 `WaitForInjectionSteadyAsync`（进程存活 >= 阈值 且主窗口出现，取不到窗口只按时间；换 pid 重试会重新等）。
    日志：`injection waiting for steady state (alive {n}s / window={bool})`（每 2 秒一条）、`injection steady after {n}s`。
  - 「等进程出现」的 20 分钟预算语义没变；等待「稳」的时间算在同一个预算里（每次尝试顶部重算 remaining）。
- 文件：`AppConfig.cs`、`Helpers/InjectionDelayOptions.cs`（新）、`Features/GameLauncher/GameLauncherPage.xaml.cs`、
  `Features/GameLauncher/GameLauncherSettingDialog.xaml(.cs)`、`Features/Modules/ModuleRegistry.cs`、
  `Features/Modules/ModulesPage.xaml(.cs)`、`Features/Plugins/GamePluginPage.xaml(.cs)`、
  `Features/Plugins/GlobalPluginPage.xaml(.cs)`、`Features/OptiScaler/OptiScalerPage.xaml(.cs)`。

- 补充验证：x64 Debug 构建 **0 错误**（新增注入时机三处 UI 后重编）；扩展自测 **PASS 446 / FAIL 0**。



### 24 UI / 交互 16 条调整（用户反馈）

> 逐条：问题 → 改法 → 文件。构建 0 错误；扩展自测 **PASS 451 / FAIL 0**。

1. **注入时机改成直接输入数值**：四处（全局默认 / 模块 / 插件 / OptiScaler）从下拉改成 `NumberBox`，
   0~30 整数、留空 = 跟随全局、0 = 立即；说明保留。删掉 `Helpers/InjectionDelayOptions.cs`。
   文件：`GameLauncherSettingDialog.xaml(.cs)`、`GlobalPluginPage.xaml(.cs)`、`ModulesPage.xaml(.cs)`、
   `GamePluginPage.xaml(.cs)`、`OptiScalerPage.xaml(.cs)`。
2. **OptiScaler 页不再套娃**：去掉 ListView 单选 + SourceId 分组标题，改成一个来源一张卡片，卡片里一个
   「切换版本」下拉（`SetBuilds` 铺这个来源在本地装的版本，新 → 旧；换版本 = 改这个游戏的选择并重挂配置）。
   顶部栏保留「注入时机」。文件：`OptiScalerPage.xaml(.cs)`。
3. 模块页删掉「只装了一个版本；到「全局插件 → 模块」再「换版本」装一个才能切。」——`VersionHint` 单版本时返回空，
   并用 `VersionHintVisibility` 收起。文件：`ModulesPage.xaml(.cs)`。
4. 模块页每行的「版本」下拉和「注入时机」输入框合并到同一个横向 StackPanel。文件：`ModulesPage.xaml`。
5. 插件卡片版本只留一处：收起摘要不再带版本号（见 11/12），展开里的「已安装版本：N 个」删掉（见 15）。
6. 插件的启用开关统一位置：卡片右侧改成顶对齐的 StackPanel，启用开关和 LoadFromDllMain 在同一块区域。
   文件：`GamePluginPage.xaml`。
7. **LoadFromDllMain 只在插件自己不会登记时显示**：新增 Extensions 层 `AddonSelfRegistrationDetector`
   （ASCII / UTF-16 扫 `LoadFromDllMain`、`WritePrivateProfileString` 两条痕迹），
   `GamePluginService.GetAddons` 给每个 addon 算 `SelfRegistersInIni`，
   `AddonItemViewModel.LoadFromDllMainVisibility` = `IsDlss5 && !SelfRegistersInIni`（判不了保持显示）。
   自测喂假二进制验证（第 30 段）。实测真机：`dlss5-bridge` = False（显示）、`dlss5-feed` / `renodx-dlss` = True（隐藏）。
   文件：`Extensions/Services/AddonSelfRegistrationDetector.cs`（新）、`Extensions/Games/GamePluginService.cs`、
   `GamePluginPage.xaml(.cs)`、`Extensions.Tests/Program.cs`。
8. 「已安装版本」每行的删除按钮收紧：行改成横向 StackPanel，按钮紧跟文字（不再被 `*` 列撑开）。
   文件：`GlobalPluginPage.xaml`（三行模板）。
9. 每行再加一个「重装」：插件 = `RunInstallAsync(manifest, tag)`；OptiScaler = `DownloadOptiScalerVersionAsync`；
   模块 = `ModuleRegistry.InstallAsync(module, tag)`。文件：`GlobalPluginPage.xaml(.cs)`。
10. 每行左边加一条竖条（当前 = `AccentFillColorDefaultBrush`，其它 = `ControlStrokeColorDefaultBrush`），
    由 `CatalogRowVisual.Bar` 提供。文件：`GlobalPluginPage.xaml(.cs)`。
11. 卡片右侧不再显示版本号（收起摘要只报数量）；左侧版本 tag / 下拉保留。文件：`GlobalPluginPage.xaml(.cs)`。
12. 摘要文案由「已安装版本：N 个」改成「N 个版本」。文件：`GlobalPluginPage.xaml.cs`。
13. 插件「当前」卡片右侧的「重装」按钮删掉（重装改由每行的小按钮负责；「可下载」列表里的安装按钮保留）。
    文件：`GlobalPluginPage.xaml`。
14. **孤儿条目**：账本里没记录、只有 addonPatterns 认领到的文件时，黄字改成列出真实文件名（`DiskOnlyHint`）；
    「删除」在孤儿条目上直接删这些盘上文件 + 清 ini 残留，提示「已移除：未发现对应文件」（不再点了没反应）；
    没有匹配文件且没账本记录的条目本来就不会进「当前」（`foundOnDiskOnly` 要求真的扫到文件）。
    根因：以前删除只走 `UninstallAsync`（依赖账本记录），孤儿条目账本为空 → 删 0 个文件、静默。
    文件：`GlobalPluginPage.xaml(.cs)`。
15. 展开内容里的「已安装版本：N 个」删掉。文件：`GlobalPluginPage.xaml`。
16. 展开内容里的「文件清单」删掉。文件：`GlobalPluginPage.xaml`。

- 验证：x64 Debug 构建 **0 错误**；扩展自测 **PASS 451 / FAIL 0**（新增第 30 段自登记检测断言）。

### 25 游戏时长记录不上（`no such table: PlayTimeItem`）

> 根因：便携版的「只认自己目录树」过滤器把**便携根目录自己**排除掉了，导致无界面子进程的
> DB 从来没初始化过。构建 0 错误；扩展自测 **PASS 461 / FAIL 0**。

**症状**：日志里刷 `Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 1: 'no such table: PlayTimeItem'.`
（`HoYoShadeHub_260925.log` 一天 376 条、`260926.log` 18 条），游戏时长一条都记不下来，
主界面的时长卡片永远是 0。启动器界面本身**正常**，所以一直没被发现。

**定位过程**：

1. 日志里那条异常的调用栈是 `PlayTimeService.Log` → `INSERT OR REPLACE INTO PlayTimeItem`；
   上一条日志是 `Welcome to HoYoShadeHub v...` +
   `Command Line: <app-版本>\HoYoShadeHub.dll playtime --biz ... --pid ...`
   —— 说明它发生在 **`playtime` 子进程**里，不是主进程。
2. 手工复现：`<app-版本>\HoYoShadeHub.exe playtime --biz hkrpg_bilibili --pid <随便一个进程>`，
   立刻复现同样的异常；同时 `D:\APPS\HoYoShadeHub\HoYoShadeHubDatabase.db` 的 mtime **没变**
   —— 说明这个子进程根本没用上那个库。
3. 那个库里 `PlayTimeItem` 是**存在**的（`PRAGMA user_version` = 17，表齐全），
   所以不是「库坏了」，是「连的不是这个库」。
4. 于是看 `AppConfig`：`playtime` 子进程没有主界面，DB 只能靠静态构造里的
   `TryFindExistingProfileFolder()`。而 `AddCandidate` 的便携版过滤器写的是
   `path.StartsWith(root + '\\')`，`D:\APPS\HoYoShadeHub` 不以
   `D:\APPS\HoYoShadeHub\\` 开头 → **便携根目录自己被排除了**；
   它下面没有任何子目录带 profile 证据文件 → 返回 null →
   `SetDatabase` 一次都没被调用 → `_connectionString` 是 null →
   Microsoft.Data.Sqlite 拿到空连接串会**静默开一个空临时库** → 任何查询都是 `no such table`。
5. 主进程为什么没事：`MainWindow.LoadContentView` 在 `HasExistingShadeInstall()` 为真时
   显式调了一次 `AppConfig.UseUserDataFolder(AppConfig.ResolveDefaultUserDataFolder())`，
   把库指对了。子进程走不到那条路。

**改法**：

- 新增 Extensions 层 `PortableDataFolderScope.IsInside(root, path)`：
  纯字符串作用域判断，**根目录自己也算在树内**，同时保留「同前缀的隔壁目录不算」这条本意
  （`D:\APPS\HoYoShadeHub-new` 仍然不算）。`AppConfig.AddCandidate` 改用它。
- `AppConfig` 静态构造：`TryFindExistingProfileFolder()` 找不到也要**兜底到
  `ResolveDefaultUserDataFolder()`**，绝不让 DB 停在「没初始化」状态；
  新增 `UserDataFolderSource`（`config.ini` / `profile-scan` / `default`）并在启动日志里打出来。
- `DatabaseService`：
  - `SetDatabase` 不再要求目录已存在（`Directory.CreateDirectory`），并且不再**静默吞掉**异常
    —— 记到 `InitializationError`；
  - `CreateConnection()` 在没初始化时直接抛 `InvalidOperationException`
    （而不是默默开空临时库，那种「数据丢了还不报错」最难查）；
  - 新增 `EnsureCoreTables`：`USER_VERSION` 说迁移过了、但 `KVT` / `PlayTimeItem` 其实不在时补建
    （纯 `CREATE TABLE IF NOT EXISTS`，不动 `USER_VERSION`）；
  - 新增 `DatabasePath` / `IsInitialized` 供日志使用。
- 启动日志多两行：`UserDataFolder: ... (source: ..., portable: ...)`、`Database: ... (error: ...)`
  —— 下次这类问题不用再猜。
- 自测第 31 段：10 条断言，覆盖「根目录自己算在内」以及各种负例。

**文件**：`Extensions/Services/PortableDataFolderScope.cs`（新）、`AppConfig.cs`、
`Features/Database/DatabaseService.cs`、`Extensions.Tests/Program.cs`。

**验证**：`<app-版本>\HoYoShadeHub.exe playtime --biz hkrpg_bilibili --pid <pid>`
能正常落库、日志不再报 `no such table`。

> 说明：这是上游（便携版）自带的老问题，不是本次改动引入的。



### 26 修：OptiScaler 页滚不动 / 删除后不刷新 / 「清理失效条目」并进刷新（用户反馈）

#### 26.1 OptiScaler 页（和模块页）无法滚动
- **症状**：OptiScaler 页构建多起来之后整页滚不动。
- **根因**：根 Grid 第 2 行（`Height="*"`）放的是 `ItemsControl`（第 2 条的来源卡片列表），
  `ItemsControl` 没有内置滚动；`ModulesPage` 第 2 行虽然是 `ListView`（自带滚动），但两页写法不一。
- **改法**：OptiScaler 页用 `<ScrollViewer Grid.Row="2" Padding="0,0,6,0">` 包住 `ItemsControl`
  （和 `GlobalPluginPage.xaml` 插件列表同款：工具栏/标题固定、只有列表滚）。
  模块页统一成 `ScrollViewer + ItemsControl`（模板内容 `x:Bind` / `Click` / `Visibility` 一字未动）。
  结论：`ListView` 在这里确实滚得动，但这一页 `SelectionMode="None"`、不需要虚拟化/选中，
  统一成 ScrollViewer + ItemsControl 更一致；长列表的虚拟化收益在本页（一屏几个模块）可以忽略。
- 文件：`Features/OptiScaler/OptiScalerPage.xaml`、`Features/Modules/ModulesPage.xaml`。

#### 26.2 删除插件后列表不刷新（删除没反馈）
- **症状**：删掉插件 / OptiScaler 构建 / 模块 / 某个版本后，卡片还在，或者点了像没反应。
- **根因**：删除流程都包在 `RunAsync(...)` 里，`RunAsync` 执行期间 `_isWorking == true`；
  而 `RefreshAsync()` 开头就是 `if (_isWorking) return;` —— 所以在 `RunAsync` 里调 `RefreshAsync()` 是**空操作**。
  另外 `ReloadPluginDataAsync()` 开头会把状态写成「正在读取插件目录…」，删除消息如果写在刷新之前也会被覆盖。
- **改法**：删除入口改调 `ReloadPluginDataAsync()`（不带 `_isWorking` 锁），并把删除结果文案挪到刷新**之后**；
  去掉重复的 `RefreshOptiScaler()` / `RefreshModules()` / `ApplyInstalledVersions()`（一次全量刷新就够）。
  覆盖：`Button_DeleteAddonFile_Click`、`Button_Uninstall_Click`（含孤儿条目分支）、`Button_DeletePluginVersion_Click`、
  `Button_DeleteOptiScalerBuild_Click`、`Button_DeleteOptiScalerVersion_Click`、`Button_DeleteModule_Click`、
  `Button_DeleteModuleVersion_Click`（右键菜单四个入口都是转发到这些 handler，不用单独改）。`PurgeAddonReferences` 的 ini 清理保持不动。
- 文件：`Features/Plugins/GlobalPluginPage.xaml.cs`。

#### 26.3 「清理失效条目」按钮并进刷新
- **症状 / 需求**：工具栏那个「清理失效条目」按钮要拿掉，清理改成刷新时自动做。
- **改法**：删掉 `Button_CleanStale`（XAML）和 `Button_CleanStaleReferences_Click`；
  新增 `CleanStaleAddonReferences()`（`GameDiscoveryService.DiscoverAll` + `AddonReferenceCleaner.RemoveStale`），
  在 `ReloadPluginDataAsync` 末尾跑一次，**只有真的清掉东西时**才把结果用「；」追加到 `TextBlock_Status`
  （刷新自己的文案是「已读取：N 个插件扩展、M 个插件文件。」），失败只写日志、不影响刷新。
- 文件：`Features/Plugins/GlobalPluginPage.xaml(.cs)`。

#### 26.4 注入时机默认值改回 0（立即注入）
- **症状**：用户问「注入以前是 4 秒吗？如果以前是 0 就不要改成默认 4」。
- **事实核查**：查 `HEAD` 版的 `GameLauncherPage.InjectExtraDllsAsync` —— **原来完全没有等待**，
  进程一出现就 `DllInjector.Inject`；唯一的等待是 DLL 之间等 `FsrBridge` 就绪标记（`WaitForFsrBridgeReadyAsync`），
  那是就绪检测、不是固定秒数。所以「默认 4 秒」是加预热开关时写错的默认值。
- **改法**：
  - `AppConfig.DefaultInjectionWarmupSeconds`：4 → **0**；
  - `AppConfig.GetInjectionWarmupEnabled`：没设过 → **默认关**（`HasValue(key) && GetValue(false, key)`）；
  - `WaitForInjectionSteadyAsync` 在 `thresholdSeconds <= 0` 时直接 `IsProcessAlive` 后放行 —— 和以前一致；
  - 文案 6 处同步：`ModulesPage.xaml` / `GlobalPluginPage.xaml` / `GamePluginPage.xaml` 的 tooltip、
    `OptiScalerPage.xaml` 注释、`GameLauncherSettingDialog.xaml` 的说明 + 开关 On/Off 文案、
    `GameLauncherSettingDialog.xaml.cs` 的 `GetInjectionWarmupHint`（关 = 「默认，和以前一样」）。
    预热本身保留为**可选**：星铁这类游戏首启动被带崩时，打开开关设 3~5 秒。
  - 数据侧核对：DB 里只有 `shade_inject_delay_hkrpg_bilibili = 0`，没有 `inject_warmup_*` 残留，不会被旧默认值粘住。
- **文件**：`AppConfig.cs`、`Features/GameLauncher/GameLauncherSettingDialog.xaml(.cs)`、
  `Features/OptiScaler/OptiScalerPage.xaml`（注释）、`Features/Modules/ModulesPage.xaml`（tooltip）、
  `Features/Plugins/GlobalPluginPage.xaml`（tooltip）、`Features/Plugins/GamePluginPage.xaml`（说明文字）。

- **验证**：`dotnet build src/HoYoShadeHub/HoYoShadeHub.csproj -c Release -p:Platform=x64 -m:1 -nodeReuse:false` → **0 错误**；
  `dotnet run --project src/HoYoShadeHub.Extensions.Tests -c Release` → **PASS 459 / FAIL 0**。
> **自测 PASS 数是环境相关的**：第 13 段末尾那几条会拿
> `D:\APPS\HoYoShadeHub\HoYoShade\reshade-shaders\Addons` 里**真实存在**的 addon 文件做断言
> （`renodx-dlss5-super-anus(1.0.8.18).addon64` / `renodx-dlss(9.17.12).addon64` / `dlss5-bridge.addon64`），
> 文件改名 / 删掉就少跑几条（`File.Exists` 为假时直接跳过，并打印 `[SKIP]`）。所以 PASS 从 461 变 459
> 不是用例被删（`git diff HEAD` 里没有任何被删的 `Check(`），只是那几个文件名对不上了
> （现在是 `renodx-dlss5-super-anus.addon64` / `renodx-dlss.addon64`，`dlss5-bridge.addon64` 已不在）。
> **判断标准始终是 FAIL 0。**



### 27 六条 UI 调整 + 「删了又出现」按模式清理（用户反馈）

> 第 3 条（Super Anus 反复出现）由用户自己查；本节第 8 条是其中的**代码侧**修复（按模式删干净 + 插件包副本）。

#### 27.1 模块卡片「版本」下拉被 Header 挤成两行
- **症状**：`ModulesPage` 每行的「版本」下拉（带 `Header`）和同一行的「注入时机」输入框对不齐 / 像两行。
- **根因**：`ComboBox.Header` 会占用垂直空间，把同一行里的其它控件推下去。
- **改法**：去掉 `Header="版本"`，改成左边一个 `TextBlock`「版本」+ 无 Header 的下拉，行内所有元素 `VerticalAlignment="Center"`；
  `GlobalPluginPage` 模块「当前 / 可下载」卡片的版本下拉同样处理。
- 文件：`Features/Modules/ModulesPage.xaml`、`Features/Plugins/GlobalPluginPage.xaml`。

#### 27.2 插件「当前」卡片单独一行显示「已装 x.y.z」
- **症状**：卡片展开区有一行「已装 0.2026.…」（`VersionText`），和版本列表 / 下拉重复。
- **改法**：删掉「当前」卡片展开区那个 `VersionText` TextBlock（「可下载」卡片保留它）。
- 文件：`Features/Plugins/GlobalPluginPage.xaml`。

#### 27.4 去掉蓝色「已安装」tag
- **症状**：插件「当前」卡片上有个蓝色「已安装」tag（卡片本来就在「当前」区，多余）。
- **改法**：删掉那个 Border（改用「使用中」，见 27.5）；同一位置不再显示「已安装」。
- 文件：`Features/Plugins/GlobalPluginPage.xaml`。

#### 27.5 插件也要「使用中」（5a 卡片 + 5b 版本行）
- **症状**：OptiScaler 卡片有「使用中」标记，插件卡片没有；版本列表里也看不出这个游戏在用哪一版。
- **根因**：全局插件页是「全局视角」，没有把「当前选中的游戏」的启用状态带过来。
- **改法**：
  - 5a：新增 `BuildEnabledAddonFileSet()` —— 用 `GamePluginService` 读**当前选中游戏**的 ReShade.ini，
    收集 `Enabled && !GloballyDisabled` 的文件名；`PluginItemViewModel.InUseForGame` = 该插件的任一文件在其中，
    卡片上显示「使用中」tag（`InUseVisibility`）。没选游戏 / 没 ini / 读不了 → 空集合，不乱标。
  - 5b：`ApplyInstalledVersions` 里「在用哪一版」= 选过单版本就用 `AppConfig.GetPluginVersion(gameId, extId)`，
    否则用共享目录当前那份；选了游戏但这个游戏没启用这个插件 → 不标。行左侧竖条 + 「使用中」都跟着这个判据。
- 文件：`Features/Plugins/GlobalPluginPage.xaml(.cs)`。

#### 27.6 / 27.7 下载按钮和版本下拉不在同一行
- **症状**：模块「可下载」卡片、插件「可下载」卡片里，版本下拉和下载 / 安装按钮各占一行。
- **改法**：各自包进一个横向 `StackPanel`（TextBlock「版本」+ 无 Header 的下拉 + 按钮），按钮 `VerticalAlignment="Center"`。
- 文件：`Features/Plugins/GlobalPluginPage.xaml`。

#### 27.8 删掉的插件（Super Anus）「删了又出现」
- **症状**：早就删掉的 `renodx.dlss5.superanus` 每次刷新又被认领回来；盘上 `renodx-dlss5-super-anus.addon64` 是硬链接，
  另一个名字在每游戏插件包 `cache\games\<游戏>\Addons\` 里。
- **根因**：① 删除只删「当前认领到的那几个名字 / 账本 + slug」，同族变体（`.addon64x`、带版本号等）漏掉，下次刷新又被
  `addonPatterns` 认领；② `GlobalPluginPage.QueueAddonPackSync()` 里 `Task.Run` 体**递归调用自己**，被重入保护挡住，
  等于从没调用过 `GameAddonPackService.SyncAll()` —— 共享目录删掉的文件，在每游戏插件包里的硬链接副本一直留着。
- **改法**：
  - 8a：新增 Extensions 层 `AddonPatternCleaner.DeleteMatching(addonsDir, patterns, alreadyDeleted)`，
    用 `GlobMatcher.IsMatch` 把共享 Addons 目录里匹配 `Manifest.AddonPatterns` 的文件**全删**（含 `.addon64x` / 各种版本变体）。
    孤儿分支和 `UninstallAsync` 分支都调；结果并进 ini 残留清理。
  - 8b：修好 `QueueAddonPackSync()` —— 真正 `Task.Run(() => GameAddonPackService.SyncAll())`；刷新末尾会重拼所有插件包，
    包目录里多出来的副本由 `GameAddonPack.Sync` 清掉（没有选版本的游戏直接 `GameAddonPack.Remove` 整个包目录）。
  - 8c：删除文案加「按模式另外清了 N 个同族文件」，删不掉的报「N 个同族文件删不掉（被占用 / 权限）」（原来的「N 个删不掉」保留）。
- **边界**：按模式删用的是 `GlobMatcher`（`*` 不跨 `/`），不是字符串前缀 —— 例如模式 `foo-*` 不会误删 `foobar`。
  自测第 31 段：`foo.addon64` / `foo(1.0).addon64x` / `foo-extra.addon64x` 三个变体被删，`foobar.addon64` / `bar.addon64` 不动；
  另造一个 pack 目录 + `pack.json`，共享文件删掉后重拼，包里的副本被清掉。
- 文件：`Extensions/Services/AddonPatternCleaner.cs`（新）、`Features/Plugins/GlobalPluginPage.xaml.cs`、`Extensions.Tests/Program.cs`。

- **验证**：`dotnet build src/HoYoShadeHub/HoYoShadeHub.csproj -c Release -p:Platform=x64 -m:1 -nodeReuse:false` → **0 错误**；
  `dotnet run --project src/HoYoShadeHub.Extensions.Tests -c Release` → **PASS 467 / FAIL 0**。

### 28 切换插件版本时崩溃（HoYoShadeHubTrayMenu 0xc0000005）+ 版本选择不生效

> 用户报：在插件栏切换插件版本 → 弹「HoYoShadeHubTrayMenu: HoYoShadeHub.exe - 系统错误 / Exception Processing Message 0xc0000005 - Unexpected parameters」，进程随后消失。
> 日志（`HoYoShadeHub_260926.log`）显示出事前正在反复刷新插件页，`addon dir` 在共享目录和专属包之间来回翻：
> `06:50:11.627 addon dir = HoYoShade\reshade-shaders\Addons` → `06:50:15.491 addon dir = cache\games\hk4e_cn\Addons` → `06:50:15.601` 又回到共享目录。
> 事件日志 / WER 里没有托管异常记录，是原生 AV。

#### 28.1 版本选择根本不生效（目录来回翻的根源）

- **根因**：`GameAddonPackService.Sync` 查这个游戏的版本选择时，候选扩展 id 只来自**账本**
  （`ReadLedgerIds(host)` → `.hysx\installed.json`）。用户自己放进 Addons 目录的插件（账本里没有记录、
  靠 `addonPatterns` 认领的那种）即使在下拉里选了版本，`GetPluginVersionSelections` 也查不到 →
  `selections.Count == 0` → 走 `RevertIfPack`，把 `[ADDON] AddonPath` **撤回共享目录**。
  用户看到的就是「切了版本没反应」，而且两个目录每隔几百毫秒来回翻一次。
- **改法**：新增 `AppConfig.GetPluginVersionSelectionIds(gameId)`（按 `launch_option_plugin_version_<extId>_<biz>_<id>`
  的键名扫设置缓存，取出所有存过选择的扩展 id，不管它在不在账本里）；`Sync` 把账本 id 和它取并集再查选择。
- 文件：`AppConfig.cs`、`Features/Plugins/GameAddonPackService.cs`。

#### 28.2 TwoWay 下拉的自反馈循环

- **根因**：`GamePluginPage` 的「版本」下拉是 TwoWay 绑定（`SelectedItem="{x:Bind SelectedVersionOption, Mode=TwoWay}"`）。
  换版本后 `OnAddonVersionChanged` 会 `_ = RefreshAsync()` 重建卡片，重建时 `ConfigureVersionChoice` 又给下拉赋一次**同样的值**；
  这次赋值穿过 TwoWay 绑定又回到 `OnAddonVersionChanged` → 再刷新 → 再赋值，形成自反馈。
  （`_suppressVersion` 只挡得住同步赋值那一次，挡不住绑定回写。）
- **改法**：`OnAddonVersionChanged` 开头比对「要设的值」和 `AppConfig.GetPluginVersion(...)` 现值，**一样就直接 return**，
  不再落盘 / 重拼 / 刷新。
- 文件：`Features/Plugins/GamePluginPage.xaml.cs`。

#### 28.3 `AppConfig._settingCache` 没有加锁（崩溃的直接嫌疑）

- **根因**：`_settingCache` 是普通 `Dictionary<string, string?>`，`GetValue` / `HasValue` / `SetValue` 都在裸读写。
  它**不是**只有 UI 线程在用：更新检查、以及刚被修好、真正开始执行的 `GameAddonPackService.SyncAll()`
  （跑在 `Task.Run` 上 → `Sync` → `AppConfig.GetPluginVersionSelections`）都会从后台线程读写它。
  多线程同时读写 Dictionary 会破坏桶结构 —— 轻则读到旧值，重则查询进入死循环或进程直接 AV，
  这正是「切版本时弹 0xc0000005 然后进程没了」的形态。
  之前 `SyncAll` 因为一个重入 bug 从来没真正跑过（见 §27.8），所以这个竞态一直没被踩到；
  上一版把它修好之后，后台线程真的开始碰 AppConfig 了，于是暴露出来。
- **改法**：新增 `_settingLock`（`System.Threading.Lock`），`InitializeSettingProvider` / `GetValue` / `HasValue` /
  `SetValue` / `ClearCache` 全部改成「持锁读写缓存、DB 查询放到锁外、写回时再持锁」。
- 文件：`AppConfig.cs`。

- **验证**：`dotnet build src/HoYoShadeHub/HoYoShadeHub.csproj -c Release -p:Platform=x64 -m:1 -nodeReuse:false` → **0 错误**；
  `dotnet run --project src/HoYoShadeHub.Extensions.Tests -c Release` → **PASS 467 / FAIL 0**。

> 说明：0xc0000005 是原生 AV 且没有 WER 报告；以上三条是「出事那一刻正在发生的事」+「确定存在的竞态」。
> 修完切换版本不再产生刷新循环，后台线程也不再裸读写共享缓存。若仍能复现请告知时间点，我会看那一刻的日志调用序列。

### 29 RTX HDR 在显示器没开 HDR 时是失效的（第 4 条兼容检测）

> 用户要求：「rtx hdr 在显示器没开 hdr 的情况下，就算开着也是失效的，从兼容检测里修复这个问题」。

#### 29.1 问题

NVIDIA 的 RTX HDR（把 SDR 游戏重映射成 HDR 那个滤镜）**要求显示器的 Windows HDR 打开**。
NVIDIA app 里的开关开着、显示器 HDR 没开的时候，这个开关实际什么都不做 —— 画面一点 HDR 都不会有，
反而白多挂一道滤镜链。老的第 4 条只看驱动里的 `RTX HDR - Enable` 这一项，判不出这种情况。

#### 29.2 老代码还有一处读错了

第 4 条原来用 `NvidiaAppSettings.TryReadSystemHdr()` 当「系统 HDR」。它读的是
`HKCU\SOFTWARE\Microsoft\Windows NT\CurrentVersion\VideoSettings` 下带 HDR 字样的值 ——
那是「**播放流式 HDR 视频**」（`EnableHDRForPlayback`），**和桌面 HDR / 高级颜色不是一回事**：
显示器 HDR 没开时这个键也可能是 1。所以那个值只能当参考，不能当判据。
（本次把它保留但改名为「播放流式 HDR 视频」并在注释里写明别拿它当桌面 HDR。）

#### 29.3 改法

1. 新增 `Features/Plugins/DisplayHdrState.cs`：走 DisplayConfig 读 / 写显示器的**高级颜色（Windows HDR）**状态。
   - `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` 拿活动路径，按 `DISPLAYCONFIG_PATH_INFO`（72 字节）解析
     source / target 的 adapterId + id；
   - `DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME`(1) 取 `\\.\DISPLAY1` 这种名字；
   - `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO`(9) 取 union 的 bit0 = 支持 HDR、bit1 = HDR 已开；
   - `DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE`(10) 用来一键打开 HDR（改完再读一遍确认）；
   - 结构布局对不上（`SizeOf<PathInfo>() != 72`）或读失败一律返回空表 / 说明，**绝不猜一个值**。
2. 新增 Extensions 层 `Services/RtxHdrCoexistence.Evaluate(rtxHdrEnabled, anyDisplayHdrEnabled)`，
   把「RTX HDR 开关 × 显示器 HDR」判成 6 种结论（可自测）。
3. 第 4 条按结论给结果：

   | 结论 | 等级 | 处理 |
   |---|---|---|
   | RTX HDR 没开 | 绿 | 只说显示器状态 |
   | **开着 + 显示器 HDR 全没开** | **红（失效）** | 有支持 HDR 的显示器 → 出现「打开系统 HDR」修复按钮；没有 → 建议到 NVIDIA app 关掉 RTX HDR |
   | 开着 + 显示器 HDR 也开着 | 黄 | 能用，但提醒和 DLSS5 色调映射叠加容易过曝/发灰 |
   | 开着 + 读不到显示器 HDR | 黄 | 照实说读不到，让用户自己确认 |
   | 读不到开关 + 显示器 HDR 开着 | 黄 | 提醒去 NVIDIA app 确认 RTX HDR |
   | 读不到开关 + 显示器 HDR 没开 | 绿 | 一般可以放心 |

   「打开系统 HDR」按主显示器优先（`\\.\DISPLAY1`）挑一台支持的，调 `SET_ADVANCED_COLOR_STATE` 打开，
   再读一遍确认，回一句「已给 X 打开 Windows HDR（屏幕黑一下是正常的）」。
4. 标题也从「是否开了 RTX HDR」改成「RTX HDR 是否真的生效（要显示器开着 HDR）」。

**文件**：`Features/Plugins/DisplayHdrState.cs`（新）、`Extensions/Services/RtxHdrCoexistence.cs`（新）、
`Features/Plugins/Dlss5CompatibilityCheck.cs`（UTF-16，用 PowerShell 改的）、
`Features/Plugins/NvidiaAppSettings.cs`（订正注释）、`Extensions.Tests/Program.cs`。

#### 29.4 实测（本机）

用同一套 DisplayConfig 调用单独探了一遍这台机器：

```
SizeOf<Path> = 72
paths=1 modes=2
QueryDisplayConfig rc=0 count=1
  \\.\DISPLAY1  available=True  supported=True enabled=False raw=0x00000001 colorEncoding=0 bits=10
```

`supported=True enabled=False` —— 显示器支持 HDR、Windows HDR 没开，正好就是用户说的那种情况；
所以这一条现在会报红并给出「打开系统 HDR」。也顺带证实了 72 字节的结构布局和 bit0/bit1 的位定义是对的。

- **验证**：构建 **0 错误**；扩展自测 **PASS 476 / FAIL 0**（新增第 32 段 9 条断言，覆盖 6 种结论 + 边界）。

### 30 OptiScaler 页点不动 + 外层启动器把「正在跑的那份」删掉

> 用户报：「启动器的 opt 栏没法选 opt 了」→ 追问后明确：「左侧 opt 页，应该是多个 opt 单选，以前就是这样子的，现在卡片是点不动的」。

#### 30.1 OptiScaler 页：改回「一个构建一张卡片、点一下就是单选」

- **症状**：卡片点不动，找不到地方换 opt。
- **根因**：上一批为了让页面「不套娃」，把原来的 `ListView SelectionMode=Single`（点卡片=单选）换成了
  「一个来源一张卡片 + 卡片里一个『切换版本』下拉」。卡片本身不再响应点击，只有下拉能动 ——
  用户的习惯是点卡片就换，于是看起来就是「点不动的」。
- **改法**：回到**一个构建一张卡片**的平铺列表（仍然没有嵌套）：
  - `Load()` 不再按 `SourceId` 分组，`OptiScalerLibrary.List()` 里每个构建生成一个 VM（`SetBuilds([build], …)`）；
  - 卡片左边的单选圈（`RadioButton IsHitTestVisible=False`，`IsChecked` 绑 `IsInUse` 只做显示）+ 蓝色「使用中」；
  - 卡片 `Border` 挂 `Tapped="Card_Tapped"`：点一下就把这个游戏切到这个构建（写 `SetOptiScalerVersion` +
    勾上「启用OptiScaler」+ 同步 `state.json` + 重挂「当前配置」），单选 —— 和原来 ListView 单选一个意思；
  - 卡片标题改成「来源名 · 版本」（一个来源装了多版时每版一张卡，靠版本号区分）；
  - 删掉卡片里那行「切换版本」下拉（一个构建一张卡之后它没意义了），
    状态栏文案改成「共 N 个构建，点卡片选一个。」
- 文件：`Features/OptiScaler/OptiScalerPage.xaml(.cs)`。

#### 30.2 外层启动器会删掉「正在运行的那份」app-* 目录（真事故）

- **症状**：某个时刻开始，运行中的 Hub 各种怪：点「打开 OptiScaler 目录」弹
  `System.IO.FileNotFoundException: System.Threading.Thread, Version=10.0.0.0`（日志 09:26:50），
  部分页面操作没反应。
- **根因**：外层启动器 `src/HoYoShadeHub.PortableLauncher`（产物就是便携根目录那个 `HoYoShadeHub.exe`）
  每次启动都会把**其它** `app-*` 目录整个删掉（`CleanupOldAppFolders`）。
  而「再启动一次」时如果旧实例还开着（单实例锁只是让新实例退出），旧实例就还在旧的 `app-Y` 里跑 ——
  `app-Y` 被删，它**懒加载**还没加载到的程序集就全找不到了。
  更糟的是删除只删掉了「当时没被加载」的那部分文件：留下一个残包
  （之前那个只剩 150 个文件的 `app-1.3.7-z1i` 就是这么来的）。本次事故是 `app-1.3.7-z1x`。
- **改法**：`CleanupOldAppFolders` 删之前先判断这份是不是正在被用：
  试着**独占读写**该目录的 `HoYoShadeHub.exe`（进程映像的文件是只读映射的，开着必然 SharingViolation），
  打不开就跳过并记一行 `Skip in-use version`。比查进程列表靠谱 —— 旧实例可能是提权跑的，普通权限读不到它的路径。
- **部署**：`dotnet publish src/HoYoShadeHub.PortableLauncher -c Release -r win-x64 -o <tmp>
  -p:PublishSingleFile=true -p:SelfContained=true -p:PublishTrimmed=true -p:Version=…`，
  产物拷成 `D:\APPS\HoYoShadeHub\HoYoShadeHub.exe`（旧的那份备份为 `HoYoShadeHub.exe.bak-20260925`）。
- 文件：`src/HoYoShadeHub.PortableLauncher/Program.cs`。

> 教训：**不要在用户的实例还开着的时候自己启动一遍验证** —— 那会把用户正在跑的那份目录删掉。
> 这次之后一律让用户自己从托盘重启。

- **验证**：`dotnet build src/HoYoShadeHub/HoYoShadeHub.csproj -c Release -p:Platform=x64 -m:1 -nodeReuse:false` → **0 错误**；
  `dotnet run --project src/HoYoShadeHub.Extensions.Tests -c Release` → **PASS 476 / FAIL 0**。#### 28.4 换版本时把整页列表重建掉（崩溃 + 下拉变空的直接原因）

- **症状**：在插件卡片里选完版本，界面刷新后**下拉框是空的**；紧接着弹
  `HoYoShadeHubTrayMenu: HoYoShadeHub.exe - 系统错误 / Exception Processing Message 0xc0000005`，点确定进程就没了。
  日志里看不到任何托管异常（`App Crash` / `Program Crash` 都没有），事件日志 / WER 里也没有记录 —— 是原生 AV。
- **根因**：`OnAddonVersionChanged` 里 `_ = RefreshAsync()` 会 `Addons.Clear()` 再重建整张卡片列表。
  而这个回调是 ComboBox 改 `SelectedItem` 触发的 —— 用户此刻正开着下拉框（或刚关）。
  在 ComboBox/ItemsControl 交互过程中换掉它们绑定的集合，是 WinUI 3 已知的崩法（原生 AV → user32 那个对话框）；
  就算侥幸不崩，重建后 ComboBox 铺新 ItemsSource 也会把刚设的选中项清成空（用户看到的「下拉是空的」）。
  而且换版本只改游戏 ini 的 `[ADDON] AddonPath` + 专属包目录，**插件清单本身没变**，整页重读本来就没必要。
- **改法**：
  - `OnAddonVersionChanged` 不再调 `RefreshAsync()`，只 `UpdatePathHint(ResolveCurrentAddonDirectory())`
    更新「插件目录」那行说明（`_plugins.AddonDirectory` 是建服务时缓存的，换版本后会过期，所以现读 ini）；
  - 选中项改成**延迟一个分发轮次**再写进下拉（`AddonItemViewModel.VersionDispatchQueue` +
    `SetSelectedVersionOptionSilently`），避免被随后铺进来的 ItemsSource 顶掉。
- 文件：`Features/Plugins/GamePluginPage.xaml.cs`。

- **验证（z1v 起）**：连点版本切换不再出现刷新循环；DB 里 `launch_option_plugin_version_renodx.dlss.sf_hk4e_cn_*`
  从一直是空变成真的落下 `renodx-dlss-SF-26.0922.0041`，`cache\games\hk4e_cn\Addons` 建起来了，
  该游戏 ReShade.ini 的 `AddonPath` 也指过去了。

### 31 鸣潮（自定义游戏）的 DX12 启动选项

- **背景**：鸣潮 PC 客户端是 DX11 / DX12 双 RHI，DX11 是默认；DX12（光线追踪 / DLSS 4 那套）靠启动项 `-dx12` 进。
  证据：XXMI Launcher 的 WWMI 启动命令固定传 `-dx11`（DX12 对应 `-dx12`），Steam 讨论区官方答复也是「launch options 加 `-dx12`」。
  Hub 里鸣潮走自定义游戏（`custom_*`），而 `CheckGameVersion` 的自定义分支原来在 `CheckDX12ConfigAsync` 之前就 return 了
  —— 所以启动器页永远不出 DX12 开关；`StartCustomGameAsync` 也不带任何启动项。
- **改法**（用法对齐绝区零的 DX12 开关）：
  - `CheckGameVersion` 自定义分支也跑一次 `CheckDX12ConfigAsync()`；
  - `CheckDX12ConfigAsync` 开头对自定义游戏**短路**（不去 HoYoPlay 查配置，那边没有它的数据）：
    `GameCatalog.IsWutheringWaves` 认得出的鸣潮 → 显示 DX12 开关，本地拼一份 `GameDXConfig`
    （`CmdArgs = "-dx12"`、无官方预览图、说明文案内置中文）；
    开关状态照旧存 `enable_dx12_<biz>`（自定义 biz 是 `custom_<id>`，键天然可用）；
  - `StartCustomGameAsync` 勾了开关就带 `-dx12` 启动（没勾不带参数，行为不变）；
  - `GameLauncherPage.xaml` DX12 那个边框的 `Visibility` 从不写 Mode 的 x:Bind（= OneTime）
    改成 `Mode=OneWay` —— 原来异步把 `IsDX12OptionVisible` 设 true 根本刷不出来，自定义游戏必踩；
  - `DX12IntroDialog` 在 `DX12PreviewImage` 为空时把预览图整行折叠，不留两张空图。
- 文件：`Features/GameLauncher/GameLauncherPage.xaml(.cs)`、`Features/GameLauncher/DX12IntroDialog.xaml(.cs)`。
- **验证**：构建 0 错误；扩展测试 FAIL 0。开关/启动项的真实 UI 未实测 —— 用户从托盘重启后到鸣潮的启动器页看。

### 32 兼容检测第 12 条：第三方整合包路径「指回无效」（群友 Seri 案例）

- **现象**：群友的游戏 ReShade.ini 里 EffectSearchPaths 混着
  `C:\ProgramData\ReShade Addons\Seri\reshade-shaders\Shaders\**`（Seri 整合包写的，目录形状和 HoYoShade 一样：`<根>\reshade-shaders\Shaders`），
  第 12 条报红「搜索路径指到别的 HoYoShade」；点「指回当前 HoYoShade」之后**红标还在**，像是修复无效。
- **「指回无效」的真正原因不在修复，在显示**：`Dlss5CompatDialog` 把开窗时构造的 `Dlss5CompatContext` 存成 readonly，
  而 `context.Profile` 是**开窗那一刻**读的 ini 快照（`GamePluginService.Profile` 同样是构造时读一次的缓存）。
  修复其实把盘上的 ini 改好了，但「重新检测」还用旧 context → 读到的永远是修复前的样子。
- **改法**：
  - `Dlss5CompatDialog` 加可选的 `Func<Dlss5CompatContext>? contextFactory`；每次跑检测（包括修完的自动重检）
    都用工厂**重建 context**（两个调用页都改成传工厂，工厂里重建 `GamePluginService` 重读 ini / 插件目录）；
  - 单条修复成功后自动重跑一遍检测 —— 红标当场消掉，不用用户再点「重新检测」；
  - **语义按用户口径**：第三方整合包的 reshade-shaders 形状路径**照样爆红**，修复一样是「指回」
    （`ShadePathAligner.AlignValue` 原本就会改写任何认得出根的路径 —— 中途试过「只指回根名叫 HoYoShade 的」，
    被用户否掉，已还原）；文案改成「搜索路径指到别的 HoYoShade / 第三方目录：…（当前是 …）」；
  - 顺手：`AlignValue` 对去重后的列表**去重**（重复写两遍的当前路径，ReShade 会扫两遍）；
    中途加的 `IsHoYoShadeRoot` / `RemoveForeignSearchPaths` 已随方案还原一起删掉。
- 文件：`HoYoShadeHub.Extensions/ReShade/ShadePathAligner.cs`、`Features/Plugins/Dlss5CompatDialog.xaml.cs`（UTF-16）、
  `Features/Plugins/Dlss5CompatContext.cs`、`Features/Plugins/Dlss5CompatibilityCheck.cs`（UTF-16）、
  `Features/Plugins/GamePluginPage.xaml.cs`、`Features/Plugins/GlobalPluginPage.xaml.cs`。
- **验证**：构建 0 错误；测试 PASS 481 / FAIL 0（§24 新增 5 条 Seri 用例：根能反推、Align 改写 Seri 路径、
  重复条目去重后只剩一条、修完幂等）。
- **给群友的说明**：点修复后那条 Seri 路径会被改写成当前 HoYoShade 的 Shaders 路径并去重 ——
  相当于不再从 Seri 的目录加载效果。要是他**想留** Seri 的效果，那就别点修复；红标是提醒「这不是当前 HoYoShade 的路径」。

### 33 双 ReShade 同进程互崩（XXMI + HoYoShade 同时注入，星穹铁道案例）

- **现象**：群友星穹铁道（miHoYo 官启动器 `E:\miHoYo Launcher\games\Star Rail Game\StarRail.exe`，D3D11 b100）
  XXMI（SRMI）和 HoYoShade 同时启用就崩游戏，各自单独开没事。崩溃日志（ReShade (6).log，z20）没有任何 ERROR：
  12:45:17 初始化（从 `F:\...\ho\HoYoShade\ReShade64.dll` 注入）→ 12:45:18.5 建完 3840x2160 交换链 →
  12:45:20 进程直接退出 —— **建完交换链约 1.4 秒即死，效果（着色器编译）压根没开始加载**，典型的静默硬崩。
- **根因**：进程里被注进了**两个 ReShade64.dll**（XXMI 自带的 + HoYoShade 的）。ReShade 不支持同进程双实例：
  后挂的那个会把先挂的包装过的设备/交换链再包一层，两套 runtime 抢同一条 Present 链；
  本包的 RenoDX DLSS 加重冲突（device_upgrade / 观察「外层交换链」/ 接管 nvngx+Streamline，包到的却是对方的包装层）。
  本日志只记录自己的实例，第二个 ReShade 的日志在 XXMI 那边（如 `XXMI Launcher\SRMI\ReShade.log`），
  能看到同一时刻第二条 `Initializing crosire's ReShade ... into StarRail.exe` —— 双实例实锤。
- **结论 / 给群友的口径**：一个游戏进程**只能留一个 ReShade**。模型挂机（3DMigoto）不冲突，冲突的是两个 ReShade：
  - 留 HoYoShade：XXMI Launcher 设置里关掉 SRMI 的 ReShade 开关（模型功能照用），HoYoShade 继续注入；
  - 留 XXMI 自带 ReShade：把 HoYoShade 的 ReShade64.dll 从注入里去掉。
  与第 12 条无关（那条是 ini 路径问题；这份日志里 Addons 搜索路径已指向当前 HoYoShade，属正常）。

### 34 启动器全面 bug 扫描（z22 批次：修了 7 类，1 类记档未动）

- **背景**：用户要求「扫描启动器目前还有没有 bug」。自查 z20/z21 热点（DX12 链路、兼容检测、路径对齐、
  便携启动器）+ 两个并行扫描（UI 层全量 / Extensions+Portable 层）+ 全库高危模式（sync-over-async、
  async void 兜底、空 catch、deferral、编码往返）。构建 0 错误；测试 PASS 481 / FAIL 0。

**修复清单（全部落地）**：

1. **拖放 deferral 泄漏（3 处）**：`RootGrid_Drop`（GameLauncherPage）、`Grid_BackgroundDragIn_Drop`
   （GameLauncherSettingDialog）在拖入不支持文件时提前 `return`，`ScreenshotPage2.Grid_ImageItem_DragStarting`
   在取文件抛异常时跳过 `Complete()` —— deferral 不 Complete，拖放源（资源管理器）会一直挂着，
   表现为拖放卡死。三处都改成 `finally` 里必达 `defer.Complete()`。
2. **视频背景永久冻结**：`AppBackground.MediaPlayer_VideoFrameAvailable` 在 `DispatcherQueue.TryEnqueue`
   返回 false（窗口正在拆）时不补信号量 —— 计数永久少一，之后每帧都在 `CurrentCount == 0` 处直接跳过。
   现在检查返回值，失败立即 `Release()`。
3. **帧率解锁静默失败**：`StartFpsUnlockAsync` 是 fire-and-forget，但前半段（同步上游数据 / 读 shellcode、
   pattern / 等游戏进程）没有 try/catch —— meta.json 损坏或 config.ini 读不了时异常逃逸，无提示无日志。
   现在同步失败给 toast + 日志后返回；读 shellcode/pattern 失败走内置兜底；等进程异常也留痕。
4. **关键持久化静默吞错**：`DatabaseService.SetValue<T>` 的 `catch { }`（KVT 存安装路径/插件版本选择等）
   和 `AppConfig.SaveConfiguration` 的 `catch { }`（config.ini 存 UserDataFolder/登录票据）改为记日志
   （Serilog `Log.Error`）—— 「config.ini 丢失 → 游戏全丢」那类事故至少能在日志里看到源头。
5. **截图页字典并发损坏**：`_screenshotDict` 是普通 `Dictionary`，但 FileSystemWatcher 回调在线程池线程
   上跑（每个截图目录一个 watcher），批量截图/删除时并发读写会损坏内部结构 —— 新截图不显示且零日志。
   换成 `ConcurrentDictionary`；注意它没有公开的 `Remove(key)`（显式接口实现），4 处改用 `TryRemove(key, out _)`。
6. **兼容检测弹窗修复并发**：`Dlss5CompatDialog.RunFixAsync` 期间整窗没锁 —— 修复按钮只绑了可见性、
   「重新检测」只绑 `_isBusy`（修复不置位），连点两条修复会并发写同一份 ReShade.ini（后写覆盖先写），
   又会造出「指回了也没用」的观感。现在修复期间 `_isBusy = true` 整窗锁住，修完的自动重检放在
   锁释放之后（否则会被 `RunAsync` 的忙碌判定打回）。顺手删了一行重复的「正在检测」赋值。
7. **启动器页火后不理的两处确认有兜底**（ApplySmoothMotionAsync / StartFpsUnlockAsync 主体有 catch）。

**确认没问题的（扫过，不修）**：Program.cs 三处同步阻塞都在 SynchronizationContext 安装前的 Main 里；
ReShade.ini 的 IniDocument 保存保留原 BOM；version.ini 只 Copy 不读写；全仓 XAML 无 OneTime 绑动态属性；
其余 fire-and-forget 目标都有内部 try/catch。

**记档未动**：

- **OptiScaler 配置 ini 编码往返**：`OptiScalerPage` / `OptiScalerPresets` 读写预设/游戏 profile 用默认
  UTF-8 无 BOM —— 用户拿外部编辑器存成 ANSI(GBK) 或带 BOM 后，再经启动器读-改-写一次就乱码/丢 BOM。
  改编码行为要先拿真实 GBK 文件验证（写回会不会影响 OptiScaler 自己的解析），这批不动。
- `GlobalPluginPage.MaybePromptMigrationAsync` 是死代码（无调用点），真正的迁移提示走
  `CacheMigrationService.PromptIfNeededAsync`（已包 Task.Run）。留着不碍事，下批清理。
- `RunningGameService.OpenOverlayWindow` 开头 `return false` 是有意禁用悬浮窗，不是 bug。
- 源文件编码杂音：`Dlss5CompatDialog.xaml.cs` 是 UTF-16、`BlenderRepairToolWindow.xaml.cs` 是 GBK ——
  编译没问题，但统一转 UTF-8 能省掉编辑工具的坑（此前多次 pwsh 拼接都因此绕行）。

**扩展层同批修复（8 处）**：

1. **断点续传从未生效（高）**：`DownloadService.TryResumeAsync` 只发一次 Range，206 追加一段后返回
   「追加后的总字节数」，调用方拿它跟 `existing` 比永远不等 → 刚续下来的字节整个删掉从零重下；
   叠加 sidecar ETag 手工解析取到转义反斜杠（ETag 本来就带引号），If-Range 发的是垃圾 → 服务器回 200。
   类文档宣称的 Range 续传实际 100% 退化为整包重下（几百 MB 的 OptiScaler 包每次中断都白下）。
   修法：循环发 Range 直到 416 收口或 `ContentRange.Total` 拿满；`knownETag` 为空禁止续传
   （没有对账就没法保证续上的字节还属于同一个文件）；sidecar 改 `JsonDocument` 解析。
2. **卸载在游戏运行时必炸（高）**：`ExtensionInstaller.UninstallAsync` 主删除循环的 `File.Delete` 裸调用，
   addon 被游戏加载后 DLL 锁着 → 异常上抛 → 账本摘不掉 → 重试永远卡同一个文件。改为逐文件 try/catch
   记 `Warnings`（结果类型补了 `Warnings` 列表），账本照常摘。
3. **本地安装覆盖共享 inode（高）**：`LocalPackageInstaller` 直接 `File.Copy(overwrite)` 落到共享 Addons ——
   Win32 复制保留目标 inode，指向它的硬链接（版本归档 / 每游戏插件包）内容被一起改掉，「切版本」静默失效。
   改成与 `ExtensionInstaller` 相同的「`.hysx-new` 临时文件 + Move 换 inode」。
4. **下载失败泄漏 %TEMP%（中）**：`ExtensionPackageFetcher.FetchAsync` 的 workRoot 没有 try/catch，
   下载/解压一抛就泄漏整个目录；`FetchLocal` 的单文件分支漏 `TrackTempRoot`（成功路径也泄漏）。都已补。
5. **OptiScaler 构建目录被占用（中）**：三条安装路径先 `TryDeleteDirectory`（吞异常）再写，
   游戏开着删不动时静默写出新旧混杂目录。补 `EnsureTargetWritable` 复查：删不动直接报
   「先关掉游戏再试」，不再制造坏目录。
6. **GitHub 工件缓存 key 漏参（中·潜伏）**：`GithubReleaseResolver.ResolveAsync` 的缓存 key 没有
   `TagPattern` / `IncludePrerelease` —— 同仓库不同 tagPattern 的来源会互串（磁盘缓存活 24h）。已补进 key。
7. **安装失败 `.hysx-new` 残留（中低）**：copy/move 包 try/finally，失败清 staged。
8. **tar.exe 兜底被主流水线绕过（中低）**：fetcher 两处 + downloader 一处从裸 `ZipFile` 换成
   `ZipExtractor.ExtractToDirectory`（7-Zip/WinRAR 压的 LZMA/Deflate64 包现在能装了）。
   `build.ps1` 的 `Add-Content version.ini`（追加攒多条 exe_path，便携启动器取第一条 → 启动旧版本）
   改成带 BOM 的覆盖写。

**扩展层记档未动**：`ShadePathAligner` 的 `ScreenShot` 锚点会把「游戏目录自带的 ScreenShot」误判成旧根
（SavePath 被悄悄改走）—— 改法要动对齐器语义（用户数据类键只认强特征根），牵动 481 条测试里的核心行为，
下批单独做 + 补测试；`HttpClient` 每调用新建不复用（TIME_WAIT 堆积，低危）；HTML/atom 解析不识别
prerelease 标记（includePrerelease=false 的来源主路径可能解析到预发布版）。

- **版本**：1.3.8（发布版；开发实例为 app-1.3.7-z22 / app-1.3.8 同一产物）。文件：GameLauncherPage.xaml.cs、GameLauncherSettingDialog.xaml.cs、
  ScreenshotPage.xaml.cs、ScreenshotPage2.xaml.cs、AppBackground.xaml.cs、DatabaseService.cs、
  AppConfig.cs、Dlss5CompatDialog.xaml.cs（UTF-16）、DownloadService.cs、ExtensionInstaller.cs、
  LocalPackageInstaller.cs、ExtensionPackageFetcher.cs、GithubReleaseResolver.cs、
  OptiScalerDownloader.cs、build.ps1。

### 35 版本迁移专项测试（发布前最后一轮，z22 收口）

- **覆盖面**：
  - 既有测试 §28（迁移规划器真值表 + PlanMoves：装过插件没归档 → 引导；已迁移 / 全新安装 → 不引导；
    OptiScaler **不参与迁移**（用户要求，原地多版本共存）；模块要搬；目标已存在标记冲突不覆盖；
    源不存在不规划）—— 481 条全绿。
  - 编排层 `CacheMigrationService.RunAsync` 逐段复核：备份账本 → 建 cache 骨架 → 搬 modules
    （同卷先改名、被占用退回复制、老目录删不掉不算失败）→ 归档已装插件 → 拼每游戏包 →
    **全部成功才置 `cache_migrated`**；失败路径幂等（重跑只补没搬完的）。
  - 读侧复核：`ModulesRootPath` 只要 `cache\modules` 存在就用 cache（`CacheMigrated || Directory.Exists`）；
    OptiScaler 根跟「插件所在的 HoYoShade 根」走，不迁。
  - **真机只读冒烟**（D:\APPS\HoYoShadeHub）：迁移在真机上已成功跑过两轮
    （`.hysx\migrate-backup\20260926_0333*` / `_0339*` 两份账本备份在位），产物完整 ——
    `Cache\plugins` 5 个插件归档（renodx.dlss.sf 有 2 个 tag）、`Cache\games` 2 个游戏包
    （hk4e_cn / hkrpg_bilibili）、`Cache\modules\veritas`，OptiScaler 原地未动 —— 与设计一致。
- **补一个缺口**：`MoveStore` 半路失败（CopyTree 中途抛）会留下**残缺**的 cache 目标，而读侧只要
  目录存在就用 cache —— 下次成功迁移前模块列表是缺的。失败路径现在清掉残缺目标
  （目标已存在的合并路径不受影响，那条路本来就返回 true）。
- **验证**：构建 0 错误；测试 PASS 481 / FAIL 0；以 1.3.8 发布（发布包 `HoYoShadeHub-1.3.8-x64.zip`）
  （174.3 MB / 1011 条目，与 z21 结构一致）；`version.ini` 指向 z22。
- **文件**：`Features/Plugins/CacheMigrationService.cs`。
- **发布前追加（1.3.8 随版）**：「游戏运行中可一键关闭游戏」—— `StartGameButton` 新增
  `CloseGameCommand` / `IsClosingGame`（运行中主按钮左侧出现停止小按钮，关闭过程禁用防连点），
  `GameLauncherPage.CloseGameAsync`：`Kill(entireProcessTree: true)` + 10 秒等待，收尾复用自然退出
  同一套（`StopFpsUnlocker` + `GameExitedMessage` + `CheckGameVersion` 重算状态）。
  文件：StartGameButton.xaml / .cs、GameLauncherPage.xaml / .cs。


