# 开发环境备忘

这份是给「下次开工」用的，避免重新踩一遍。

---

## 1. 构建

### .NET 10 SDK 不在 PATH 里

`global.json` 要求 SDK 10.0.1xx，但本机 PATH 上只有 9.0.317。SDK 装在**用户级**：

```
C:\Users\thx11\.dotnet10\dotnet.exe     (10.0.401)
```

直接敲 `dotnet build` 会静默用回 9.x 然后报错。用仓库里的脚本，它会自己找：

```powershell
.\build-local.ps1                                   # 编整个 sln（Debug/x64）
.\build-local.ps1 -Clean                            # 先删 bin/obj
.\build-local.ps1 -Publish -Version 1.0.0-local     # 发布可运行版本
```

`global.json` 加了 `"rollForward": "latestFeature"`，所以 10.0.4xx 也能编（上游锁的是 10.0.101）。

### NuGet 源

机器级配置 `C:\Program Files (x86)\NuGet\Config\Microsoft.VisualStudio.FallbackLocation.config`
指向一个**不存在的** `...\Microsoft Visual Studio\Shared\NuGetPackages`（本机没装 VS），
导致所有 restore 以 `NU1301` 失败。

仓库根的 `NuGet.config` 用 `<clear/>` 干掉了它。**这是本仓库相对上游多出来的文件。**

### 便携版（`-Portable`）

```powershell
.\build-local.ps1 -Portable -Version 1.0.0 -Output build/HoYoShadeHub-Portable
```

出来的目录结构和上游 `build.ps1` / `publish.ps1` 一致：

```
build/HoYoShadeHub-Portable/
├─ HoYoShadeHub.exe      ← 外层启动器（双击这个）
├─ version.ini           ← exe_path=app-1.0.0\HoYoShadeHub.exe
├─ config.ini            ← Language=zh-CN；UserDataFolder 留空
└─ app-1.0.0/            ← 主程序（dotnet publish 的结果）
```

顺手会打个 zip：`build/release/HoYoShadeHub_Portable_<版本>_x64.zip`。

数据目录不用配：**便携版首次向导会默认把 UserDataFolder 设成这个文件夹本身**
（`WelcomeView.InitializeDefaultUserDataFolder` 里 `IsPortable` 分支 = parentFolder），
实测启动后 `HoYoShadeHubDatabase.db` 直接长在便携根目录里。

### ⚠️ 外层启动器是本仓库用 C# 复刻的

上游那个外层 `HoYoShadeHub.exe` 来自 `src/HoYoShadeHub.Launcher`（**C++ .vcxproj**），
要 VS 的 C++ 工作负载 + MSBuild，本机没有 → 编不了。
所以这里加了 `src/HoYoShadeHub.PortableLauncher`（**故意不进 sln**，和上游那个 vcxproj 一样），
行为照着 `HoYoShadeHub.Launcher.cpp` 复刻：

1. 读 `version.ini` 的 `exe_path` → 把参数原样传给它；
2. 没有 version.ini 就扫 `app-*`，挑 exe 最新的那个；
3. 起来之后把其它 `app-*` 目录删掉；
4. 都没有 → 弹窗问要不要去 GitHub 下载。

产物 13 MB（`PublishSingleFile` + `SelfContained` + `PublishTrimmed`），不依赖机器上装没装 .NET。

> 顺带一个作用：`AppConfig` 判定「便携版」的依据就是 **父目录里存在 `HoYoShadeHub.exe`**
> —— 这个文件同时也是那个标记。

### 编不了的部分

`build.ps1` / `publish.ps1` **跑不完**：

- `src/HoYoShadeHub.Launcher` 是 **C++ `.vcxproj`**，要 VS 的 C++ 工作负载 + MSBuild，而且它不在 sln 里
- `publish.ps1` 还要 `7Zip4Powershell` 模块和 `src/BuildTool` 那套打包/差分工具链

所以只能编主程序。直接跑 `app-*\HoYoShadeHub.exe` 没问题（找不到外层 Launcher 时按「安装版」处理）。

---

## 2. 三个**务必记住**的坑

### ⚠️ 多进程 MSBuild 会「静默失败」

有时 `dotnet build` / `dotnet publish` 报 `Build FAILED` 却是 `0 Error(s)`，
日志里连编译都没开始（`-v diag` 能看到它卡在 evaluation 就断了）。
加 `-m:1 -nodeReuse:false` 再跑就正常，**而且能看见真正的错误**。

### ⚠️ 这个终端是 Windows PowerShell 5.1，不是 pwsh 7

- 它读**无 BOM 的 UTF-8 源码会按 GBK 解码**。用 `Get-Content -Raw` + `Set-Content` 批量改带中文的源码，
  会把文件**彻底改坏**（我毁过一次 `Program.cs`，只能用编辑器重写）。
- **结论：改源码一律用编辑器工具，不要用 PowerShell 拼字符串重写文件。**
- `.ps1` 脚本必须写成 **UTF-8 with BOM**，否则中文注释会把 here-string 终止符顶掉，直接语法错误
  （`build-local.ps1` 踩过）。

### ⚠️ 那个 RPC 宿主进程杀不掉

应用启动后会拉一个 `HoYoShadeHub.exe <dll> rpc ...` 的提权子进程（RPC 服务），
它锁住 `HoYoShadeHub.dll`。非管理员 shell **杀不掉**（`taskkill /F` 报 Access denied），
于是重新 publish 到同一目录会 `MSB3027 file is locked`。

**绕法：换一个输出目录名再发布**，然后把沙盒 junction 指过去。

---

## 3. 隔离沙盒（用来实机验证，不碰用户的真实安装）

```
build\smoke2\
├─ HoYoShadeHub.exe      ← 复制一份，让 AppConfig 判定为「便携版」
├─ config.ini            ← 关键：UserDataFolder 指向独立目录
└─ app-1.0.0-view\       ← junction → 真实发布目录
```

`config.ini`：

```ini
Language=zh-CN
UserDataFolder=D:\CODE\HoyoDLSS5\build\smoke\data
```

这样数据库、HoYoShade 安装、日志的前缀都被隔离到 `build\smoke\data`，
只有 `%LOCALAPPDATA%\HoYoShadeHub\log` 是和用户真实实例共用的（`CacheFolder` 写死在那）。

启动：

```powershell
Start-Process "D:\CODE\HoyoDLSS5\build\smoke2\app-1.0.0-view\HoYoShadeHub.exe" `
  -WorkingDirectory "D:\CODE\HoyoDLSS5\build\smoke2\app-1.0.0-view"
```

> 2026-09-18 之后又有一套 **smoke4**（`build\smoke4\app-9.9.9-smoke` → 指向 `build\HoYoShadeHub\app-9.9.9-smoke`，
> 版本号 9.9.9 是为了压过更新检查，免得每次启动都弹更新窗），`UserDataFolder` 同样指 `build\smoke\data`。
> 里面预置了两条测试数据：
> `build\smoke\data\fake-game\`（假游戏 + 自己的 ReShade.ini）和 `build\smoke\data\fake-addons\`（假 addon 目录），
> 配合 `build\smoke\data\.hysx\games.json` 里那条自定义游戏条目，用来验证「自定义游戏 → 顶部游戏列表 → 每游戏插件开关」。
>
> （2026-09-18 之后 DSH 换成了 danger-full-access，文件沙盒不再拦写入了；
> 早先那些「Access denied」都是旧沙盒规则造成的，不是代码问题。）

`build\smoke\data\HoYoShade\` 里已经有一份**真实的 HoYoShade V3.0.0-Beta.9**（当初冒烟测试时装的），
带真实 `ReShade64.dll` / `ReShade.ini` / `Presets` —— 可以直接拿来跑插件安装。

> 用户的**真实** HoYoShade 在 `D:\APPS\HoYoShadeHub\HoYoShade\`，
> addon 目录里有真的 `renodx-dlss(*).addon64` / `nvngx_dll*.dll` / `sl.*.dll`。
> 自测第 13 节会直接读它们做验证（找不到就 SKIP，不会失败）。

---

## 4. 自测

```powershell
& "$env:USERPROFILE\.dotnet10\dotnet.exe" run --project src\HoYoShadeHub.Extensions.Tests -c Release
& "$env:USERPROFILE\.dotnet10\dotnet.exe" run --project src\HoYoShadeHub.Extensions.Tests -c Release -- --online
```

- 默认 189 条断言，在临时目录里搭假 HoYoShade 跑完整流程
- `--online` 额外连 GitHub 校验内置目录里的 `tagPattern` / `assetPattern` 是否仍然正确（199 条）
  **（改了 catalog 一定要跑这个 —— 它抓出过 3 个真 bug）**
- **联网要走代理**（本机没有直连），跑 `--online` 前先给进程加上：
  ```powershell
  $env:HTTPS_PROXY = 'http://127.0.0.1:7897'
  $env:HTTP_PROXY  = 'http://127.0.0.1:7897'
  ```
  裸的 `Invoke-RestMethod` 也要加 `-Proxy http://127.0.0.1:7897`。
- 第 14~19 节是游戏条目 / 每游戏插件开关 / INIBuild / 改名式全局开关 / 自定义游戏的合成 biz / 版本号解析。
  其中 INIBuild 那条会拿
  `build\smoke\data\HoYoShade\LauncherResource\INIBuild.exe` 在**副本**里真跑一次
  （找不到就 SKIP，不会失败）

---

## 5. 参考资料

`D:\CODE\_ref\` 是研究用的临时目录（可以删）：

```
nuget.clean.config      上面说的 NuGet 绕法，现在仓库里有 NuGet.config 了，用不上
```

用户真实数据：

```
D:\APPS\HoYoShadeHub\HoYoShade\                    真实 HoYoShade 安装
D:\APPS\miHoYo Launcher\games\ZenlessZoneZero Game\ReShade.ini
D:\APPS\miHoYo Launcher\games\Genshin Impact Game\ReShade.ini
D:\APPS\Star Rail\games\Star Rail Game\ReShade.ini
D:\WeGameApps\rail_apps\蓝色星原：旅谣(2002738)\ReShade.ini
```

外部清单：

```
https://raw.githubusercontent.com/RankFTW/RHI/main/dlss_manifest.json     DLL 版本清单
https://github.com/duolad/hoyoshade                                      HoYoShade 框架本体
```
