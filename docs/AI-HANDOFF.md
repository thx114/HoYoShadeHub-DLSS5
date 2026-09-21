# HoYoDLSS5（HoYoShadeHub 魔改版）—— 交接文档（写给另一个 AI）

> 目的：让没有上下文的人/AI 读完这一份，就能接着干活。
> 事实来源：仓库里的 `docs/GAMES-AND-INJECT.md`（主规格 + 实施记录 §1~§8.14）、`docs/DLSS-ENABLER.md`、
> `docs/RESHADE-INI.md`、`docs/EXTENSIONS.md`、`docs/DEV-ENV.md`，以及本文。**冲突时以代码为准，其次以 GAMES-AND-INJECT.md 为准。**

---

## 0. 一句话

`D:\CODE\HoyoDLSS5` 是 **HoYoShadeHub**（WinUI 3 / .NET 10 的 HoYoShade 管理器）的一个魔改分叉，
方向是：**把 DLSS5 神经渲染（RenoDX DLSS5 那一类 ReShade addon）注入到米哈游 / WeGame 的游戏里**，
顺带把「插件下载/开关/版本管理」「每游戏 ReShade.ini」「注入模式」「便携版打包」这些做顺。

**这个目录不是 git 仓库**（没有 `.git`），所有变更都在工作区里；版本靠 `-Version` 参数区分，不靠提交历史。

---

## 1. 环境与常用命令

| 事情 | 命令 / 位置 |
| --- | --- |
| .NET SDK | `C:\Users\thx11\.dotnet10\dotnet.exe`（10.0.401；`build-local.ps1` 会自己找，PATH 里没有 10） |
| 编译（Debug 全解决方案） | `.\build-local.ps1` |
| 编译（Release x64） | `.\build-local.ps1 -Configuration Release -Architecture x64` |
| **发布开发实例** | `.\build-local.ps1 -Publish -Version 9.9.20 -Configuration Release -Architecture x64 -Output build/HoYoShadeHub` → 产物 `build\HoYoShadeHub\app-9.9.20\` |
| **打便携包** | `.\build-local.ps1 -Portable -Version 1.0.11 -Configuration Release -Architecture x64 -Output build/portable-1.0.11/HoYoShadeHub` → zip 落在 `build\release\HoYoShadeHub_Portable_1.0.11_x64.zip` |
| 扩展层自测（**改动后的门禁**） | `dotnet run --project src\HoYoShadeHub.Extensions.Tests -c Release`（联网版加 `-- --online`） |
| 代理（联网必需） | 系统 WinINET 代理已是 `127.0.0.1:7897`；命令行里要 `-Proxy http://127.0.0.1:7897` 或 `$env:HTTPS_PROXY` |

**这台机器上的坑**：

- 用 `dotnet build` 编 `.sln` 时**必须** `-m:1 -nodeReuse:false`，否则多进程 MSBuild 会静默失败；`build-local.ps1` 单项目 publish 没这个问题。
- `build-local.ps1` / 其它 `.ps1` 必须保持 **UTF-8 BOM**，否则中文注释会乱码、脚本报错。
- 这里的 `pwsh` 实际是 Windows PowerShell 5.1 语义：没有 `&&`、没有 `if` 当表达式、`Select-String` 返回对象要 `.Line`。
- 用户跑的 Hub 实例**通常是提权的**，非提权 shell 杀不掉（`Stop-Process` / `taskkill` 会 Access denied）；只能请用户自己关。
- `build\HoYoShadeHub\app-9.9.9x` 这种历史 dev 目录会越攒越多，可清理。
- **XAML 编译报错在这台机器上是「静默 exit 1」**（MSB3073，没有 stdout/message）。原因是 net472 `XamlCompiler.exe` 的错误消息资源名不匹配 + 中文系统附属程序集缺失，异常发生在报错过程中。**先怀疑「XAML 引用了代码里没有的属性/方法」**，别怀疑编译器或包。定位办法：写个 .NET Framework 宿主，在独立 AppDomain 里 `ExecuteAssembly("XamlCompiler.exe", args)` 并挂 `FirstChanceException` —— 真错误会以 `[FCE] ... ParseException: Property 'Xxx' not found on type 'Yyy'` 出现（详见 GAMES-AND-INJECT.md §9.6）。
- **被 XAML 用到的模型类型不能带 C# 的 `required`**：WinUI 生成的 `XamlTypeInfo.g.cs` 会 `new` 它 → `CS9035`。用带默认值的 `init` 属性。
- **别用「read 整个大文件 → write 回去」的方式改文档**：`read` 单次输出有上限，会**静默截断**（这次把 `GAMES-AND-INJECT.md` 从 1047 行截到 830 行，§8.15~§9.6 一度丢失）。改文档一律用 `edit` 追加/替换，或分页读。
- **打便携包时必须给 `-Output` 指一个干净目录**（如 `build/portable-1.0.16/HoYoShadeHub`）。`Compress-Archive` 是整目录打包，用默认的 `build\HoYoShadeHub` 会把攒下来的历史 dev 目录一起塞进 zip（1.0.16 第一次就这么干的，zip 里 28,240 个条目 / 一堆 `app-9.9.9x`）。正常 zip 是 **914 个条目 / 166.4 MB**，打包后拿这个数对一下。

**产物/沙箱约定**（当前）

| 东西 | 值 |
| --- | --- |
| 最近 dev 实例 | `build\HoYoShadeHub\app-9.9.32\`（每次 +1，如 9.9.32 → 9.9.33） |
| 沙箱启动器 | `build\smoke4\{HoYoShadeHub.exe, config.ini}`，`build\smoke4\app-9.9.9-smoke` 是指向 dev app 目录的 **junction**（换版本 = `cmd /c rmdir` 后重建 junction） |
| 沙箱用户数据 | `D:\CODE\HoyoDLSS5\build\smoke\data`（`config.ini` 里的 UserDataFolder；里面有 `HoYoShadeHubDatabase.db`，**手动 HoYoShade 根目录**就存在这个库里 = `D:\APPS\HoYoShadeHub\HoYoShade`） |
| 便携包产物 | `build\release\HoYoShadeHub_Portable_<ver>_x64.zip`（约 166 MB，含背景动图压到 2.4 MB） |
| 便携包根目录 | **只有** `HoYoShadeHub.exe` + `version.ini`（+ `app-<ver>\`）—— **故意不带 config.ini**：带了会在解压覆盖旧客户端时把用户那份配置冲掉（§8.17）；缺了由 `PortableLauncher.EnsureConfigIni()` 创建 |

---

## 2. 代码地图（本仓库相关部分）

```
src/
  HoYoShadeHub/                      WinUI 3 主程序
    Features/GameLauncher/           ★ 启动器页 / 注入模式 / 启动流程
      GameLauncherPage.xaml(.cs)     启动按钮、注入模式开关、强制 hook off、额外 DLL、DLSS Enabler 勾选框
      InjectorHelper.cs              跑 HoYoShade 的 inject.exe 并等 HOYOSHADE_READY:9999
      DllInjector.cs                 ★ 通用 DLL 注入（CreateRemoteThread + LoadLibraryW）+ 等进程
      DlssEnablerService.cs          ★ DLSS Enabler 部署/定位/只开帧生成配置
      GameExitedMessage.cs           游戏进程退出消息（背景恢复用）
      ReShadeClientUninstaller.cs    卸载/还原 ReShade 对客户端的改动
    Features/Plugins/                ★ 插件三页 + 胶水
      GamePluginPage.xaml(.cs)       每游戏插件页（红/黄 dll 标、hook 下拉、强制 off、指回当前 HoYoShade）
      GlobalPluginPage.xaml(.cs)     全局插件页（安装/删除/全局开关/版本下拉/更新徽标）
      DllConfigPage.xaml(.cs)        DLL 配置（nvngx_dlssnr.dll / sl.*.dll 分组 + 版本下拉 + 安装必要组件）
      PluginHostLocator.cs           当前 HoYoShade 宿主（含持久化的手动根目录）
      GameCatalog.cs                 游戏条目胶水 + 内置背景 + 自定义游戏注册
    Features/GameSelector/           顶部游戏图标栏（切游戏 / 加自定义游戏 / pin / 右键菜单）
    Features/Background/             AppBackground（图片/视频背景、样式）+ BackgroundService
    Features/ViewHost/               MainWindow / MainView / 向导页（QuickSetupView 等）
    AppConfig.cs                     全局配置（很多按游戏存：custom_bg_ / extra_inject_dll_ / force_hook_off_ …）
  HoYoShadeHub.Extensions/           **不依赖 WinUI，可测**（自测项目直接引用它）
    Games/                           GameEntry / GameEntryStore / GameDiscoveryService / GamePluginService / GameFolderLocator …
    ReShade/                         ReShadeProfile / IniDocument / ShadePathAligner / ReShadeIniBuilder / AddonFileSwitcher …
    Dlls/                            DllRequirements / DllComponentCatalog / DllInstaller
    Services/                        ExtensionInstaller / ExtensionManagerService / GithubReleaseResolver / DownloadService …
    Resources/catalog.builtin.json   内置插件目录（id / 名字 / 来源 / addonPatterns / tags）
  HoYoShadeHub.Extensions.Tests/     **自测宿主（不是 xunit）**：Program.cs 里一节一节 Check()，末尾打印 PASS/FAIL
  HoYoShadeHub.RPC/                  gRPC 侧：安装 HoYoShade 框架 / ReShade 包（InstallMode 0=全量 1=仅必要 2=自定义）
  HoYoShadeHub.PortableLauncher/     便携版外层启动器（读 version.ini 拉起 app-<ver>，**不在 .sln 里**）
  HoYoShadeHub.Language/            Lang*.resx + Lang.Designer.cs
docs/
  GAMES-AND-INJECT.md                ★★ 主规格 + §1~§8.14 实施记录（**每次改完都往里加一节**）
  DLSS-ENABLER.md                    DLSS Enabler / OptiScaler 用法与设置
  RESHADE-INI.md                     真实 ReShade.ini 样本反推（三个关键键、addon 命名约定）
  EXTENSIONS.md / DEV-ENV.md         扩展层设计 / 本机开发环境
```

---

## 3. 本分叉相对上游做了什么（功能清单）

上游 = HoYoShadeHub（DuolaD / 官方仓库）。**本分叉新增**：

1. **游戏条目 + 发现**：已知游戏用 Hub 现有检测（不扫盘），自定义游戏（WeGame 等）手动加；条目存 `<用户数据>\.hysx\games.json`。
2. **顶部游戏栏 = 唯一切换入口**：加自定义游戏、pin/取消 pin、右键删 HoYoShade 都在那儿。
3. **每游戏插件页**：插件文件全局一份，开关写各游戏 `ReShade.ini` 的 `[ADDON] DisabledAddons`；DLSS5 类插件启用时自动加 `LoadFromDllMain`。
4. **全局插件页**：装/删扩展包、全局开关（重命名 `.addon64 ↔ .addon64x`）、按文件名认领非 Hub 装的插件、**版本下拉 + 更新徽标**、**装新版本自动清同族旧文件**、**删除连坐同族**。
5. **DLL 配置页**：`nvngx_dlssnr.dll` / `sl.*.dll` 分组 + 版本下拉 + 「安装必要组件」；缺必需 dll 的插件在插件页标**红**，缺推荐标**黄**。
6. **注入模式**（每个游戏一个开关，在开始游戏按钮左边）：补 `ReShade.ini` → 跑 `inject.exe <进程名>` → **不替用户启动游戏**；提示条常驻 + 右上红色「停止」；再次启动先停上一个。
7. **额外注入 DLL + DLSS Enabler**：见 §5.9 / §5.10。
8. **ReShade.ini 路径对齐**：启动/注入前把游戏 ini 里的绝对路径指回当前 HoYoShade（见 §5.4）。
9. **自定义游戏内置背景**：蓝色星原带随包宣传动图（见 §5.5）。
10. **便携版打包**：`-Portable` 出 `HoYoShadeHub.exe + version.ini + app-<ver>\`，外层启动器是 C# 重写（上游是 C++）。
11. **快速安装只装必要**：向导页 `InstallMode=1`（见 §5.2）。

---

## 4. 用户真机环境（调试时的现实）

| 东西 | 位置 |
| --- | --- |
| 真实 HoYoShade 根目录 | `D:\APPS\HoYoShadeHub\HoYoShade`（`ReShade64.dll` / `inject.exe` / `LauncherResource\INIBuild.exe` / `reshade-shaders\Addons`：`nvngx_dlssnr.dll` 158 MB、`sl.*.dll`、RenoDX 插件若干） |
| 米哈游游戏 | `D:\APPS\miHoYo Launcher\games\{Genshin Impact Game, ZenlessZoneZero Game, Star Rail Game, test}` |
| 自定义游戏（测试目标） | 蓝色星原：旅谣 → `D:\WeGameApps\rail_apps\蓝色星原：旅谣(2002738)\AzurPromilia.exe` |
| DLSS Enabler | 已部署 `D:\APPS\HoYoShadeHub\DLSS-Enabler`（v0.9.4，已配「只开帧生成」） |
| 游戏目录里的 DLSS 运行时 | 三个游戏目录都有 `nvngx_dlss/d/nr.dll`；蓝色星原另有 `nvngx-wrapper.dll` + `nvngx.ini` |
| **用户的 profile（便携版数据目录）** | `D:\APPS\HoYoShadeHub`（DB 592 KB / 70 条 Setting：`SelectedGameBizs=nap_cn,hk4e_cn,hkrpg_bilibili`、四个 `install_path_*`、`.hysx\games.json` = 蓝色星原）。**游戏列表和安装路径都在 DB 里**，只有自定义游戏走 `games.json` —— 读不到 DB 就"只剩自定义游戏"（§8.18） |
| 官方启动器的安装记录（自动查找用） | `HKCU\Software\miHoYo\HYP\1_1\<biz>\GameInstallPath`（全局服在 `Cognosphere`）；本机只有 `hk4e_cn` / `nap_cn` 有值，`hkrpg_cn` 是空的 |

注意：

- 游戏目录里的 `ReShade.ini` **是运行时真正读的那份**（ReShade 按它加载 addon 和 dll），改之前先备份。
- 蓝色星原那份 ini 曾被 smoke 沙盒写坏（AddonPath 指向 `build\smoke\data\HoYoShade`），已修好，备份 `ReShade.ini.hysx-bak-20260918-232643`。
- 用户会同时开好几个实例，改完要让用户**重启 Hub** 才生效。

---

## 5. 最近一轮工作（按时间顺序，逐条：问题 → 根因 → 改法 → 文件）

### 5.1 一键开始安装装全量插件
- 现象：向导页「一键开始安装」把 44 个效果包 + 20 个 addon 全拉下来。
- 根因：`QuickSetupView` 里 `InstallReShadePackRequest.InstallMode = 0`（All）。
- 改法：改成 `1`（EssentialOnly，官方 `EffectPackages.ini` 里 `Required=1/Enabled=1` 的 2 个包，0 个 addon），文案同步改。
- 文件：`src/HoYoShadeHub/Features/ViewHost/QuickSetupView.xaml(.cs)`；见 §8.6。

### 5.2 注入模式仍然自己启动游戏
- 现象：注入模式下点开始游戏，Hub 自己把游戏 exe 起起来了 → 注入失败（蓝色星原走 WeGame 更明显）。
- 根因：`StartGameWithInjectModeAsync` 架好注入器后又调 `LaunchGameForInjectModeAsync()` 起游戏；而 HoYoShade 自带 bat 明确说「**不能直接双击运行进程/进程快捷方式以启动游戏，否则会注入失败**」。
- 改法：删掉第三步（连方法一起删），只架注入器并提示用户用自己的启动器。
- 文件：`GameLauncherPage.xaml.cs`；见 §8.7。

### 5.3 插件页红标「缺 nvngx_dlssnr.dll」其实没错
- 现象：每游戏插件页标红，但全局页 / DLL 页显示已装。
- 根因：**每游戏页按「该游戏 ini 的 `[ADDON] AddonPath`」算**（`GamePluginService.cs:119`），全局页按「当前 HoYoShade 宿主」算；蓝色星原的 ini 指向 smoke 沙盒那份（只有 1 个 addon）。
- 改法：新增 `ShadePathAligner`，启动/注入前把游戏 ini 里的绝对路径对回当前 HoYoShade；插件页加黄字提示 + 「指回当前 HoYoShade」按钮。
- 文件：`Extensions/ReShade/ShadePathAligner.cs`（新）、`GameLauncherPage.xaml.cs`、`GamePluginPage.xaml(.cs)`；见 §8.8。

### 5.4 蓝色星原内置背景动图 + 显卡占用
- 需求：把 WeGame 活动页那段背景动图放进 Hub，加蓝色星原就自动带上。
- 做法：扒 `wegame.gtimg.com/.../57df57fd84d8f4217e75e9ca09d6d316.mp4`（6 秒循环）+ 同页 1920×1080 静帧 → `src/HoYoShadeHub/Assets/Video/`；csproj 里 `Content Include="Assets\Video\**"`（**注意：图像资源本来来自 `HoYoShadeHub.Assets` NuGet 包，仓库里没有 Assets 目录，所以必须显式 Include**）。
- 触发：`GameCatalog.ApplyBuiltinBackground`（认 `AzurPromilia.exe` / 名字含「蓝色星原 / 旅谣 / Azur Promilia」）→ 复制到 `<用户数据>\bg\` + 写 `custom_bg_{biz}` / `enable_custom_bg_{biz}`；**用户自己设过就不动**。选择界面卡片用静帧（`CachedImage` 播不了视频）。
- 显卡优化（用户反馈「背景占的显卡太多」）：① 素材用系统 `MediaTranscoder` 重压成 1080p30 / 3.4 Mbps / 无音轨（14.23 MB → 2.44 MB）；② `AppBackground.MediaPlayer_VideoFrameAvailable` 改成按**窗口大小**建 surface（`VideoRenderSize()` + `CopyFrameToVideoSurface(surface, destRect)`），原来按 4K 原始尺寸每帧拷 33 MB（≈1 GB/s）。
- 文件：`Assets/Video/*`、`GameCatalog.cs`、`GameSelector.xaml.cs`、`Background/AppBackground.xaml.cs`、`HoYoShadeHub.csproj`；见 §8.9。
- 一次性小工具（可删）：`build/probe/transcode/`（MediaTranscoder 转码）。

### 5.5 顶部游戏栏点不动（左右键都失灵，要重启）
- 根因：无边框窗口靠 `AppWindow.TitleBar.SetDragRectangles` 划标题栏区域，而**两个地方都在写**：`MainWindow.UpdateDragRectangles()`（每次 `AppWindow.Changed` 写**整条**）和 `GameSelector.UpdateDragRectangles()`（把图标行挖出去）。窗口尺寸一变（`Show()` → `CenterInScreen` → `MoveAndResize`）就被覆盖成整条 → 图标落在拖拽区里，左右键被系统当标题栏吃掉；指针事件也进不来 → 自锁。
- 改法：拖拽区只留一个权威 —— `GameSelector.TryUpdateDragRectangles()`（返回 bool + 布局没跑完时 `ActualWidth` 兜底 56）；`MainWindow.UpdateDragRectangles()` 先问 `MainView.Current.TryUpdateWindowDragRectangles()`；`WM_ACTIVATE` 里再调一次自愈。
- 文件：`GameSelector.xaml.cs`、`MainView.xaml.cs`、`MainWindow.xaml.cs`；见 §8.10。

### 5.6 插件下载/删除混乱 + 没有更新检测 + 下拉选版本
- 根因：`InstalledExtensionStore.UpsertAsync` 是**整条替换** → 装新版本时旧文件留在目录里（"下到 2 个一样的"）、账本里没了记录（"删不干净"）。
- 改法：
  - 安装：装完删**同族旧文件**（老账本文件 + 同 slug 的其它 `*.addon64*`），`ExtensionInstallResult.RemovedStaleFiles`；删旧文件前核 sha256，**用户改过的不删**（给 warning）。
  - 卸载：`UninstallAsync(..., removeSiblings: true)`（界面默认开）连坐同族，`DeletedStaleFiles`；**keep 集合 = 账本里全部文件**（别把「本次没删掉的」再删一遍）。
  - 版本：`GithubReleaseResolver.ListVersionsAsync()`（atom + `/releases?page=N` HTML，**不吃 API 限额**）+ `ResolveAsync(source, tagOverride, ct)` 装指定版本；全局插件页点开卡片拉版本列表 + 「装这个版本」，进页面后台 `CheckUpdateAsync` 挂「有新版本 xxx」徽标。
- 文件：`ExtensionInstaller.cs`、`ExtensionManagerService.cs`、`GithubReleaseResolver.cs`、`Models/ExtensionVersion.cs`（新）、`GlobalPluginPage.xaml(.cs)`；见 §8.11。

### 5.7 其余小项（同批）
- **interposer 要 nrdll 在游戏目录**：`GamePluginService.EnsureInterposerDlls()`，启用 `renodx-neural-interposer-nvngx*` 时把 `nvngx_dlssnr.dll` 复制到游戏 exe 旁边（大小一样就不重复拷）。
- **游戏运行时释放视频背景显存**：`AppBackground` 订阅 `GameStartedMessage` → `DisposeVideoResource()`（真释放，不只是 Pause）；游戏退出由 `GameLauncherPage.CheckGameExited` 发 `GameExitedMessage` → 重新 `UpdateBackgroundAsync()`。
- **hook 实时读取**：`GamePluginPage` 订阅 `MainWindowStateChangedMessage`，窗口激活时重读 hook（提示里写明读的是 `[RENODX-DLSS] DirectNeuralRenderingHookPoint`）。
- **启动游戏时强制 off**：按游戏存 `AppConfig` 的 `force_hook_off_{biz}`，插件页勾选框；`GameLauncherPage.StartGameAsync` 开头 `ApplyForceHookOffOnLaunch()`。
- **hook 段名核对**：真实插件二进制里 `renodx-dlss.addon64` 的段名就是 `RENODX-DLSS`，键名是运行时拼的 → 我们写 `[RENODX-DLSS]` 是对的（旧构建写进 `[ADDON]`，所以「设 off 不生效」）。

### 5.8 注入模式提示条（用户追加要求）
- 删掉「注入模式」介绍 toast；「注入器已就位」合并进「已启动 HoYoShade 注入器」并去掉说明文字；**只要注入器在跑就一直显示**；标题右边一个**红色可点击「停止」**；再次启动游戏先停掉上一个注入器。
- 实现：`InAppToast.ShowSticky(title, actionText, action, danger)`（duration=0 不自动关，返回 `InfoBar` 供收掉）；`_injectorProcess` / `_injectorToast` 做成 **static**（页面重建后「停止」和「先停上一个」仍有效）。
- 文件：`Helpers/InAppToast.cs`、`GameLauncherPage.xaml(.cs)`；见 §8.12。

### 5.9 「额外注入 DLL」（用户选的 B 方案）
- 目的：跟 HoYoShade 的 `inject.exe` **同时**把第三方 DLL（OptiScaler / DLSS Enabler）注进游戏。
- 实现：`Features/GameLauncher/DllInjector.cs` —— `OpenProcess` → `VirtualAllocEx` → `WriteProcessMemory` → `CreateRemoteThread(LoadLibraryW)`，外加 `WaitForProcessAsync`（游戏是用户自己启动的，最多等 20 分钟）。
- 接线：每游戏 `AppConfig.GetExtraInjectDll(biz)`（键 `extra_inject_dll_{biz}`）；启动器页「注入模式」右边一个「额外 DLL」按钮选文件/换/清除；`StartGameWithInjectModeAsync` 架好注入器后 `StartExtraDllInjection(processName)`。
- 失败会写明原因：打不开进程（游戏提权而 Hub 没提权）、远端线程建不起来（反作弊）、`LoadLibraryW` 返回 0（位数/依赖）。
- 文件：`DllInjector.cs`（新）、`GameLauncherPage.xaml(.cs)`、`AppConfig.cs`；见 §8.13。

### 5.10 DLSS Enabler 部署 + 启动器一键启用
- 事实：DLSS Enabler（`artur-graniszewski/DLSS-Enabler`）的自动化构建 = 把 **OptiScaler 的 `nvngx.dll` 改名成 `dlss-enabler-upscaler.dll`**，配置就是 OptiScaler 的 `nvngx.ini`。
- 部署：`DlssEnablerService.DeployAsync()` —— `GithubReleaseResolver` 解析 `dlss-enabler-setup*.exe` → `DownloadService` 下载 → `/VERYSILENT /DIR="<用户数据>\.hysx\dlss-enabler"` 静默装 → `ConfigureFrameGenerationOnly()` 把 `nvngx.ini` 改成：
  `[Upscalers] Dx12Upscaler=dlss` + `[FrameGen] Enabled=true / FGInput=dlssg / FGOutput=nvngxfg`（原件备份 `nvngx.ini.orig`）。
- 定位已有部署 `FindInstalledFolder()`：`<用户数据>\.hysx\dlss-enabler` → `<用户数据>\DLSS-Enabler` → 便携版旁边 → `<游戏目录>\DLSS-Enabler` → **当前 HoYoShade 根目录的同级** `\DLSS-Enabler`（本机就是 `D:\APPS\HoYoShadeHub\DLSS-Enabler`）。
- UI：启动器页「注入模式」旁绿色勾选框「DLSS Enabler」→ 勾上就把该游戏的额外注入 DLL 指到 upscaler；没部署会问「下载并部署吗（约 31 MB）」；取消只在「当前指的就是它」时清掉。
- 文件：`DlssEnablerService.cs`（新）、`GameLauncherPage.xaml(.cs)`、`AppConfig.cs`；见 §8.14；用法文档 `docs/DLSS-ENABLER.md` + 部署目录里的 `怎么用-只开帧生成.txt`。

### 5.11 第三批（16 条，§8.15）—— 概要
- **DLSS Enabler 整套撤掉**（用户放弃帧生成）：删掉启动器页那个绿勾选框 + `DlssEnablerService.cs`；「额外注入 DLL」保留。
- 额外 DLL 的入口搬进「开始游戏」右边的设置对话框（`GameLauncherSettingDialog` 第 5 页 Tag=4）；注入模式按钮文案改「启动注入器」；额外注入不再依赖注入模式。
- 顶部游戏栏：`IsPinned` 没标导致右键菜单显示「固定到顶部」且点了没反应 → 入行即标；「设置背景图…」挪到顶部图标右键菜单；「添加游戏」那格换成静态毛玻璃 `CustomFrostBrush`。
- 插件：装新版本清同族旧文件、删除连坐同族、版本下拉 + 更新徽标、interposer 的 nrdll 拷到游戏目录、删/全局禁用时清 ini 引用（新增 `AddonReferenceCleaner`）、LoadFromDllMain 跟着插件开关走。
- hook：`DirectNeuralRenderingHookPoint` + `DirectNeuralRenderingHookStage` 两个键都写、读优先 Stage；界面标签改 `HookPoint`。
- 新增 `NvidiaDriverCheck`（用 DLSS5 插件时按区间红/黄提示），删掉三段没必要的说明文字。

### 5.12 第四批（7 条，§8.16）—— 概要
- 驱动版本当时先改成读**真实显卡驱动**（注册表显示适配器类键的 `DriverVersion`，`32.0.15.6636` → `566.36`）——但用户要的是 NV app 那串，见 §5.14 又被纠正。
- 装的是 `310.8.SF-v2` 却显示 `SF`：PE 版本号里没有变体信息 → 安装时 `AppConfig.SetInstalledDllVariant()` 记账，显示时用 `DllVersion.SameNumbers()` 判断记账还作不作数。
- zip 覆盖旧客户端后跳过首次引导（`HasExistingShadeInstall()`）；hook 灰的根因是 `IsHookPointCapable` 要求分支含 ShortFuse 而文件名里没有 → 放宽成 `renodx-dlss*` 都可改。
- 全局插件「插件文件」误报缺 dll：只把 addon 文件名传给了检查 → 改成目录里全部文件；该视图加「删除」（二级确认）；`dlss5.neural.interposer` 名字改英文。

### 5.13 「数据看起来丢了」那次 —— 两个真 bug（§8.17 + §8.18）
- §8.17：便携包不再带 `config.ini`（解压不再覆盖用户配置）+ 启动器缺失时创建 + `AppConfig.TryFindExistingProfileFolder()` 自动找现成 profile（含扫同级目录，按证据文件最新时间挑）。
- §8.18（关键）：**只设 `AppConfig.UserDataFolder` 不会换 DB** —— 必须 `AppConfig.UseUserDataFolder()`（内部同时调 `DatabaseService.SetDatabase`）。游戏列表 / 安装路径都在 DB 里，漏这一步就只剩自定义游戏。
- §8.18：`AutoSearchInstalledGames()` 原来是覆盖式写 `SelectedGameBizs`，且只遍历 DB 缓存 → 缓存空时写空串，一点就把顶部清空。现在：从已有列表起步只增不减、缓存空则遍历 `GameBiz.AllGameBizs` + 注册表、一个都没找到就什么都不做。

### 5.14 驱动版本：第三/四次纠正（§8.19 + §8.20）
- §8.19：要的是 NV app「已安装 - GeForce Game Ready 驱动程序 版本 616.64」（**注册表卸载项** `DisplayVersion`），不是显卡适配器的 `DriverVersion`。
  两个坑：① 只匹配英文 `Graphics Driver`，中文机上是「NVIDIA **图形驱动程序** 616.64」→ 漏掉；② 适配器扫描过滤条件「ProviderName 含 NVIDIA **或** 以 3 开头」把 AMD Radeon 610M（`32.0.12011.1010` → `110.10`）算了进来。
  现在：多语言驱动包名表 + `^\d{3}\.\d{1,2}$` 形状校验 + 取最大；适配器要 ProviderName/DriverDesc 真的含 NVIDIA。
- §8.20：检测对但**页面不显示** —— `UpdateDriverWarning()` 原本 Ok 就隐藏，用户驱动 616.64 恰在区间内 → 一行都没有。
  现在装了 DLSS5 插件（`IsDlss5`，即 `dlss5` 标签）就**始终显示**：Ok 绿色 `NVIDIA 驱动 x（DLSS5 插件要求区间内）`，Warning/Error 黄/红 `⚠ …`，读不到版本才隐藏。

### 5.15 OptiScaler：下载 + 单选 + 启动时注入（§9）
- 全局插件页新增第三个页签 **OptiScaler**：3 个社区 DLSS-NR 分支的 release，整包下载到 `<用户数据目录>\OptiScaler\<sourceId>\<版本>\`，
  **单选**记在库根 `state.json`（只改一行，不动文件）。核心：`OptiScalerCatalog` / `OptiScalerLibrary` / `OptiScalerDownloader`（都在 Extensions 层，有离线自测）。
- 一个 release 常挂多个 zip（主包 / `-rtx40-mfg` / 补丁 / 语言包）→ **不猜资产**，界面上让用户选。
- 启动器页「启动选项」新增「启动 OptiScaler」（按游戏，`use_optiscaler_<game>`）；勾了就在启动游戏时把选中的
  `OptiScaler.dll` 跟「额外注入 DLL」**一起注入**（`StartExtraDllInjection` → `InjectExtraDllsAsync`，一个进程只等一次）。
- 与 HoYoShade / OpenHoYoShade 同时启用 → 「启动选项」上方滚动提示（`Storyboard` + `TranslateTransform`）。
- 来源表：wilsjo2 / neurotic / **dlssnr-amd**（danielblnc/DLSS-NR-on-AMD，只有 setup.exe → 下载后**直接运行**，不参与注入；
  见 §9.7）；404 的 `multipass-mfg` 已删。UI 上也改成：点开版本下拉自动获取、装过后下拉默认当前版本（带「(当前)」）、按钮变「更新到此版本」。
- dlssnr-amd 那条流水线：**运行安装程序前先弹确认框**（默认目录 = 构建目录）；它装出来的 `version.dll` 由 `FindDll` 的代理名表认成注入目标，
  所以这个来源照样能「启用 + 启动时注入」（`version.dll` 排第一位：`version` / `dxgi` / `winmm` / `dinput8` / `wininet` / `dbghelp`）。

### 5.16 注入模式没勾 HoYoShade 也被注入（§8.21）
- `StartGameWithInjectModeAsync()` 开头 `useOpen = UseOpenHoYoShade && !UseHoYoShade`，两个都没勾时默认落到 **HoYoShade** → 照样架 inject.exe。
  现在：`!UseHoYoShade && !UseOpenHoYoShade` 时**只等进程**注「额外注入 DLL / OptiScaler」，不碰 ReShade；
  按钮文案三态：`IsShadeInjectMode`（启动注入器）/ `IsWaitProcessMode`（等游戏进程）/ 普通。
- 用户给的第三个来源 `y4my4y4m/OptiScaler_DLSSNR_Multipass_MFG` 目前 **404**，界面上会显示拿不到版本。

### 5.17 远端目录（插件 + OptiScaler，§9.8）+ 开源前的协议/上游对照
- **远端目录**：`catalog/plugins.json`（内置目录同格式）+ `catalog/optiscaler.json`（sources 数组），按 id 覆盖内置；
  客户端 `Features/Plugins/RemoteCatalogService.cs`：**每天最多自动拉一次**（`AppConfig.LastCatalogFetchUtc`），缓存到
  `<用户数据目录>\.hysx\catalog\`，插件侧走 `Catalog.ExtraCatalogFiles`（现成机制），OptiScaler 侧走 `OptiScalerCatalog.MergeWithBuiltin/LoadFile`；
  手动拉 = 设置 → 关于 →「拉取插件目录」。**换仓库只改 `RemoteCatalogDefaults.BaseUrl` 一行**（当前是占位 `HoyoDLSS5/HoYoShadeHub`，等用户给准名字）。
- **协议**（`docs/OPEN-SOURCE-PLAN.md`）：HoYoShadeHub 是 **MIT**（保留版权+许可全文即可 fork/再发布）、HoYoShade 框架是 **BSD-3-Clause**
  （只约束二进制再分发）、Starward 是 MIT；**不要把 ReShade / HoYoShade / addon / OptiScaler 的二进制放进源码仓库**。
- **上游对照**（同一份文档 §2）：我们落后上游两个提交 `d90b6dd`（Beta 客户端 config.ini 修复）和 `b11c63b`（HEA/PP 测试服选项迁到官方 API，14 个文件）；
  这两个提交和我们改过的文件大面积重叠，**不能直接覆盖** —— 正路是 `git init` + `upstream` remote + cherry-pick 手工合并（工作目录现在没有 `.git`）。
- **开源仓库（已建并推送）**：`https://github.com/thx114/HoYoShadeHub-DLSS5`（public / main，首次提交 `f8c8909`，494 文件 / 16 MB；
  `.gitignore` 里加了 `build/`；README 顶部有 fork 声明）。`thx114/hoyodlss5` 是放 DC 插件的仓库，**别混用**。
- **Release v1.0.22**（首个公开发布，Latest）挂的资产就是 `HoYoShadeHub_Portable_1.0.22_x64.zip`；远端目录
  `raw.githubusercontent.com/thx114/HoYoShadeHub-DLSS5/main/catalog/{plugins,optiscaler}.json` 已验证 HTTP 200。
- **还没做**：GitHub 更新渠道 + 一键更新 + 退回旧版本（设计见 `OPEN-SOURCE-PLAN.md` §5，仓库地址已定，可以开工）。

---

## 6. DLSS5 / NR / FG 的关键技术结论（用户当前在纠结的点）

1. **RenoDX DLSS 的 hook 阶段**（从真插件二进制抠出来的原文）：
   - `Render` = "processes the native D3D12 input **before** DLSS SR/DLAA/RR"
   - `Upscaled` = "processes its native D3D12 output **after** successful reconstruction"
   - `Present` = "processes the **Streamline swapchain buffer before presentation**"
   - 另有 `DirectNeuralRenderingReuseDlssUpscalingResources`（Never/Optional/Required：复用 DLSS SR/RR 的运动矢量与深度，否则用 dummy）。
   - ini 键是 `[RENODX-DLSS] DirectNeuralRenderingHookPoint`（Hub 里是个 0-4 的数字下拉，**数字↔阶段名的对应关系插件二进制里没写**，要在 ReShade 覆盖层（`Home`）里看 "Hook Method" 下拉）。
2. **「帧生成放在 NR 之后」**：任何 FG 都在**呈现那一刻**插帧，插值帧是 FG 引擎重新合成的（不会重跑 NR）。所以正确做法是 **让 NR 在 present 之前就烘进真帧** → Hook 用 `Upscaled`（或 `Render`），**别用 `Present`**（Present 会和 DLSSG 抢同一层）；同时把 `ReuseDlssUpscalingResources` 设 Optional/Required，保证 DLSSG 有 MV/深度。
3. **DLSS Enabler 在「游戏已有 DLSSG」时的价值很低**：OptiScaler 文档原话 "Always best to use native FG inputs if the game already supports FG!"；它只在 ① 游戏不给这张卡开 FG（伪 GPU 硬开）② 想把 DLSSG 翻成 FSR FG ③ 游戏完全没有 FG 时才值得用。
4. **DLSS5 那套和 OptiScaler 抢同一层 DLSS 调用**，同时开容易互相打架 —— 测试时一次只开一个变量。

---

## 7. 未完成 / 待定（下一个人从这里接手）

> 注意：**用户已经明确放弃帧生成**。§8.13/§8.14 那套「额外注入 DLL + DLSS Enabler」里，DLSS Enabler 已整套删除
> （§8.15 第 1 条），「额外注入 DLL」保留但入口搬进了「开始游戏」右边的设置对话框。

1. **把 NR 的 Hook Method 做成看得懂的 UI**（`HookPoint` 现在还是 `0-4` 数字下拉）：贴上 `Render / Upscaled / Present`
   文案（三个阶段的原文见 §6），并把 `DirectNeuralRenderingReuseDlssUpscalingResources` 一起放上去。
2. **OptiScaler「代理路线」一键开关**：注入不生效时的官方退路（把 `version.dll` 或改名 `dxgi.dll` 放进游戏目录 +
   `nvngx.ini`），目前只有 `docs/DLSS-ENABLER.md` 的说明，没有 Hub 按钮。
3. **ReShade 包安装页（`ReShadeDownloadView`）默认仍是「全量」**（向导页已改成仅必要）；要不要一起改没确认。
4. **版本列表上限 30**（atom 只有最近 10 条，其余靠翻 `/releases?page=N` 6 页）；更老的版本可能列不全。
5. **删除同族文件是按 slug 判定的**，用户手放的**同族**文件也会被一起删（确认框里有说明）；是否改成二次勾选待定。
6. **背景动图还可以再小**（720p/24fps ≈ 1.2 MB），当前 1080p30 / 3.4 Mbps。
7. **`build/probe/`、旧 dev app 目录、失败的临时构建产物**可清理。
8. **`docs/GAMES-AND-INJECT.md` 的 §8.x 需要继续追加**：每做一件事加一节，格式照旧（问题 → 根因 → 改法 → 文件）。
9. **「添加游戏」那个静态毛玻璃要用户确认观感**（§8.15 第 8 条）：现在是 `App.xaml` 的 `CustomFrostBrush`
   （静态线性渐变）；如果他要真·Mica 或别的透明度，改那一个画刷即可。
10. **改 ReShade.ini 与游戏运行的关系**（§8.15 第 7 条）：现在只是「游戏在跑就给一句提醒」，没有阻止；
    ReShade 退出时会不会真的覆盖 `DisabledAddons` 没实测过，值得验证一次。

11. **星铁的注册表路径是空的**：`HKCU\Software\miHoYo\HYP\1_1\hkrpg_cn\GameInstallPath` 为空，自动查找只能靠
    DB 里已有的 `install_path_hkrpg_bilibili` 认出来；DB 真丢的话星铁得手动定位。可以考虑加个「扫常见根目录」的兜底。
12. **profile 自动发现的边界**：现在会扫「解压根的同级目录」（一层），按证据文件的最新改动时间挑；如果用户把包解到很远的目录，
    或者同盘有多个 profile，会挑到最近用过的那个 —— 冷启动（时间戳都很旧）时可能挑错，值得再看看。

---

## 8. 硬性约定（踩过坑的）

| 约定 | 为什么 |
| --- | --- |
| 改 Extensions 后跑 `dotnet run --project src\HoYoShadeHub.Extensions.Tests -c Release` | 这是本仓库唯一的回归门禁（当前离线 **PASS 252 / FAIL 0**，数量会因真机插件文件存在与否小幅浮动） |
| 别用 GitHub REST API 解析 release | 匿名 60 次/小时按出口 IP 算，超了直接 403；一律走 `releases.atom` + `releases/expanded_assets/{tag}` / `/releases?page=N` 的 HTML |
| 新加静态资源要显式 `<Content Include>` | 仓库没有 `Assets/` 目录，现有图像来自 `HoYoShadeHub.Assets` NuGet 包 |
| XAML 绑定对象不要用 `init`-only 属性 | `XamlTypeInfo` 生成会报错；用 `{ get; set; }`（`GameBizIcon` 就是这么改的） |
| JSON 里 get-only 集合会被丢掉 | `GameEntryStore` 用 `PreferredObjectCreationHandling = Populate` |
| 改完 UI 要让用户重启 Hub | 老实例不加载新 dll；而且用户实例常是提权的，脚本杀不掉 |
| 便携版布局 | `HoYoShadeHub.exe`（外层启动器）+ `version.ini`（`exe_path=app-<ver>\HoYoShadeHub.exe`）+ `config.ini` + `app-<ver>\`；`AppConfig.IsPortable` = 父目录存在 `HoYoShadeHub.exe` |
| 文档要跟着改 | 用户明确要求：每个功能记进 `docs/GAMES-AND-INJECT.md`（§8.x 编号递增） |

---

## 9. 快速自检清单（接手后 10 分钟看懂现状）

```powershell
# 1. 编译 + 跑门禁
cd D:\CODE\HoyoDLSS5
& "$env:USERPROFILE\.dotnet10\dotnet.exe" run --project src\HoYoShadeHub.Extensions.Tests -c Release   # 期望 PASS 全绿

# 2. 看最近一次 dev 构建 & 沙箱指向
Get-ChildItem build\HoYoShadeHub -Directory | Select-Object -Last 3
cmd /c dir build\smoke4 | Select-String 'app-9.9.9-smoke'    # junction 指向哪个 app-<ver>

# 3. 看主规格的实施记录（最新几节就是最近做的事）
Select-String -Path docs\GAMES-AND-INJECT.md -Pattern '^### 8\.' | Select-Object -Last 5
```

---

## 10. 联系上下文的关键数字（本机）

| 东西 | 值 |
| --- | --- |
| 最近 dev 版本 | app-9.9.32 |
| 最近便携包 | `HoYoShadeHub_Portable_1.0.22_x64.zip`（914 条目 / 166.9 MB，不带 config.ini），已发 GitHub Release v1.0.22 |
| 扩展自测 | PASS 255 / FAIL 0（离线） |
| 内置插件目录条目 | `src/HoYoShadeHub.Extensions/Resources/catalog.builtin.json`（8 条，含 `dlss5.neural.interposer`、`dlss5.bridge`） |
| DLSS Enabler | v0.9.4，`D:\APPS\HoYoShadeHub\DLSS-Enabler` |
