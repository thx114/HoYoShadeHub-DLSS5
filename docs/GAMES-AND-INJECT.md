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




### 13 上游同步（1.3.7-hotfix4）

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

#### (5) 核对过、本分支已有等价实现（不用再搬）
- `4cef52d`「忽略 DX12 兼容性检测」= 已有（`AppConfig.GetIgnoreDX12Check` + 设置对话框 + `HoYoPlayService`）。
- `9b6e0fa`「安装状态」= 框架下载页（`HoYoShadeDownloadView`）已有已装 / 版本面板。
- `bdadc23` / `23469a6`：独立的框架与启动器自动检查开关、两者「有新版本」提示都已有
  （自研更新渠道 + 24 小时节流）。上游把框架自动检查再拆成 HoYoShade / OpenHoYoShade 两个开关这点没做。
- `687318e`（快速开始页整体重做，含退回全量安装）与本分支「只装必要」语义冲突，不整体搬。
