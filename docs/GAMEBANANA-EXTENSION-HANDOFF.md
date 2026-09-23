# GameBanana 浏览器扩展 —— 交接文档（写给接管的 AI）

> 目的：让没有上下文的 AI 读完这一份就能接着开发浏览器扩展及其与启动器的对接。
> 事实以代码为准。本文对应版本：启动器 **1.3.4b5**（提交 `8b294a2`，已推 origin/main）。

---

## 0. 一句话需求

做一个 Chrome / Edge（Manifest V3）扩展对接 gamebanana.com：

- 在 `https://gamebanana.com/mods/games/19567`（绝区零）这类游戏模组列表页，给每个模组卡片加一个按钮（下载 / 更新 / 已安装）。
- 点击后经自定义协议 `hoyoshadehub://` 拉起本地启动器 **HoYoShade Hub**，由启动器把 zip 下载并解压到对应游戏的 XXMI 模型目录（`19567 → ZZMI 的 Mods`）。
- 支持检查全部已装模组更新；有更新时按钮变成「更新」，点击让启动器下载最新版替换旧版，**替换前自动备份**到 `Mods\_backup\`。

当前状态（2026-09-23，b5）：**端到端已打通并真机验证**——全新安装、旧版备份后更新都成功。

---

## 1. 整体架构

两条独立代码线，靠自定义 URL 协议连接。扩展侧只负责「选哪个模组、组装协议 URL」，真正的下载/解压全在启动器里：

```
浏览器扩展 (extensions/gamebanana/)            HoYoShade Hub 启动器 (WinUI3/.NET10)
  content.js  注入卡片按钮                       GameBananaModInstaller.cs
  background.js  查 GameBanana API、记账    ──hoyoshadehub://mod/install?...──▶   下载 zip
  popup.*     列出已装、批量查更新                UrlProtocolService.cs 解析参数      → 备份旧版
            (扩展侧不下载任何模组文件)                                (无回传通道)   → 解压进 Mods
```

关键设计决定：

- **扩展不下载文件**，只组装协议 URL。下载必须走系统代理（见 §5），浏览器和 .NET 的代理环境不同，统一让启动器处理最稳。
- **拉起即记账**：扩展在点按钮的瞬间就把模组信息写入 `chrome.storage.local`。启动器下载成功后没有便捷回传通道（接 native messaging 成本过高），所以不回传。代价：用户点了按钮但启动器失败时，扩展里仍显示「已安装」——这是已知偏差，列入 §7。

---

## 2. 扩展侧文件结构（`extensions/gamebanana/`）

| 文件 | 职责 |
| --- | --- |
| `manifest.json` | MV3。权限只要 `storage` + gamebanana 的 host；content script 匹配 `/mods/games/*` 和 `/games/*`；service worker 是 module。 |
| `content.js` | 注入卡片按钮的 IIFE。页面是 Vue 客户端渲染 + 无限滚动，用 `MutationObserver(subtree)` 持续扫描新卡片。 |
| `background.js` | service worker。响应三类消息：`GET_STATE` / `INSTALL_MOD` / `CHECK_UPDATES`；查 GameBanana API、读写账本、组装协议 URL。 |
| `lib/api.js` | GameBanana **apiv11** 封装：`fetchMod(id)`、`fetchModsByGame(id)`、`pickDefaultFile(mod)`。 |
| `lib/store.js` | `chrome.storage.local` 账本，只有 `getAll()` / `put(record)`。 |
| `lib/protocol.js` | `buildInstallUrl(args)` 拼 `hoyoshadehub://` URL；`launchProtocol`（隐藏 iframe）。 |
| `popup.html` / `popup.js` | 点扩展图标弹出的面板：列已装模组、手动输入游戏 id 批量查更新、对有更新的项点更新。popup 里用 `window.location.href` 触发协议（没有宿主页面，不能用 iframe）。 |
| `content.css` | 按钮样式，`.hysx-mod-button` 及状态类 `hysx-update/hysx-installed/hysx-error`。 |
| `icons/` | 16/48/128 png。 |

**content.js 注入规则（容易回归，重点看）**：

- 以卡片根 `.Record` 为单位扫描，一张卡片只处理一次；用 `Object.defineProperty(card,'hysxInjected',...)` 防重。
- 模组 id **只从标题链接 `a.Name[href*="/mods/"]`** 取。卡片里预览图 `a.Preview` 和标题 `a.Name` 都指向同一模组，早期版本两个锚点各注入一次，出现「一张卡片 2 个按钮」。
- 启动时 `removeOldButtons()` 清掉残留按钮和标志（扩展热重载场景）。
- 按钮插到 `.Identifiers` 之后。

### content/background 消息协议

所有消息走 `chrome.runtime.sendMessage`，响应统一 `{ ok:true, data }` 或 `{ ok:false, error }`。background 的 listener 必须 `return true` 以支持异步 `sendResponse`。

| type | 入参 | 返回 |
| --- | --- | --- |
| `GET_STATE` | `gameId` | `{ installed:{...账本}, outdated:number[] }` |
| `INSTALL_MOD` | `gameId, modId, replace` | 解析模组详情→选默认文件→记账，返回协议 URL 字符串 |
| `CHECK_UPDATES` | `gameId` | 有更新的条目数组 `[{ installed, remote }]` |

---

## 3. GameBanana apiv11 要点

- Base：`https://gamebanana.com/apiv11`，**匿名可用，响应带 CORS 头**，扩展可直接 `fetch`。
- 游戏模组列表：`GET /Mod/Index?_nPage=1&_nPerpage=15&_csvProperties=...&_aFilters[Generic_Game]={gameId}`。
- 单模组详情：`GET /Mod/{id}?_csvProperties=_idRow,_sName,_sVersion,_tsDateUpdated,_aFiles,_aGame,...`。
- 文件对象 `_aFiles[]` 字段：`_idRow`、`_sFile`（文件名）、`_nFilesize`、`_sDownloadUrl`（如 `/dl/1824010`）、`_sAvResult`（杀毒结果 clean/unknown）、`_bIsArchived`、`_tsDateAdded`。
- 下载链：`https://gamebanana.com/dl/{fileId}` 返回 **302** → `https://filecacheNN.gamebanana.com/mods/xxx.zip`（中间可能先跳 `files.gamebanana.com` 再跳 filecache）。必须允许自动重定向。
- 更新判定字段：`_tsDateUpdated`（也有 `_tsDateModified`）。安装时记下当时的 `_tsDateUpdated`，日后远端值更大即有更新；`_sVersion` 仅作展示。
- `pickDefaultFile`：过滤掉归档文件，优先 `_sAvResult` 为 clean/unknown/空 的，按 `_tsDateAdded` 降序取第一个。

---

## 4. 启动器侧（C#）

涉及文件：

| 文件 | 职责 |
| --- | --- |
| `src/HoYoShadeHub/Features/UrlProtocol/UrlProtocolService.cs` | 注册/解析协议。识别 `uri.Host=="mod"` 且路径 `install`，`HandleModInstallAsync` 解析参数→构造 request→调 installer→弹窗。 |
| `src/HoYoShadeHub/Features/Xxmi/GameBananaModInstaller.cs` | 核心：下载 zip→解压到 `Mods\gb_{modId}`，Replace 时先备份。 |
| `src/HoYoShadeHub/Features/Xxmi/XxmiLocator.cs` | 定位 XXMI 根（`D:\APPS\XXMI`）、实例（ZZMI）、Mods 目录。 |
| `src/HoYoShadeHub/AppConfig.cs` | DI：`using ...Features.Xxmi;` + `sc.AddSingleton<GameBananaModInstaller>();`。 |
| `src/HoYoShadeHub/Program.cs` | Main 里识别 `hoyoshadehub://` 前缀，同步阻塞调用 `HandleUrlProtocolAsync`。 |

### URL 协议格式

```
hoyoshadehub://mod/install?game_id=19567&mod_id=719975&file_id=1824270
    &file_name=xxx.zip&mod_name=...&download_url=https://gamebanana.com/dl/1824270
    [&version=...][&replace=1]
```

`replace=1`（或 `true`）= 更新：启动器把同名旧目录移进 `Mods\_backup\gb_{modId}_{yyyyMMdd_HHmmss}`，再装新版。参数解析用 `HttpUtility.ParseQueryString`；`game_id/mod_id/file_id` 缺失会报中文错误。

### 安装流程（GameBananaModInstaller.InstallAsync）

1. `ImporterForGameBananaId(gameId)`：`19567 → "ZZMI"`（其余游戏在这里登记 case 即可支持）。
2. 解析实例 → `XxmiLocator.ModsDirectory`；`Directory.CreateDirectory`。
3. 固定目标 `Mods\gb_{modId}`；临时解压目录 `Mods\_cache\gb_{id}_{rand}`。
4. 下载 zip 到 `%TEMP%\gamebanana_{modId}_{fileId}{ext}`。
5. Replace 且旧目录存在：`BackupOldVersion` 用 **`Directory.Move` 原子移进 `_backup`**。
6. 解压到 staging，`UnwrapSingleRoot` 剥掉 zip 内多余的单层根目录，再 `Directory.Move` 到目标。
7. `finally` 清 staging 和临时 zip。

---

## 5. 踩坑点（务必先读，能省几个小时）

### 5.1 .NET SDK 与构建

- 系统 PATH 上是 **.NET 9**；.NET 10 SDK 在 `C:\Users\thx11\.dotnet10\dotnet.exe`（10.0.401，其 `shared` 里有 10.0.12 运行时）。构建前把它放到 PATH 最前：
  `$env:Path = 'C:\Users\thx11\.dotnet10;' + $env:Path; $env:DOTNET_ROOT='C:\Users\thx11\.dotnet10'`
- 构建铁律（机器上多 AI / 多进程共用）：dotnet 命令一律加 **`-m:1 -nodeReuse:false`**，避免并行和节点复用导致的诡异 MSBuild 错误。
- 推荐分步：先 `dotnet restore ... -r win-x64 -m:1 -nodeReuse:false`，再 `dotnet publish ... --no-restore`。隐式 restore 在 publish 里偶发失败（MSB4181 / NETSDK1018）。
- **版本号必须是合法 NuGet 版本串**：`-p:Version=1.3.4-b5` 合法，`1.3.4b5` 非法（restore 报 NETSDK1018）。发布目录名可以另叫 `app-1.3.4b5`。

### 5.2 必须自包含发布（否则进程根本不启动）

- 全局没有 x64 的 .NET 10 运行时（只有 `C:\Program Files (x86)\dotnet` 下 x86 的 8/9）。框架依赖版会在进代码前直接退出，退出码 **`-2147450730`（0x80000006）**，事件日志写「You must install or update .NET … No frameworks were found」。
- 即使输出目录里已经拷了运行时 dll，只要 `HoYoShadeHub.runtimeconfig.json` 写的是 `"frameworks"` 而不是 `"includedFrameworks"`，仍会去全局找而失败。
- 正确做法：publish 显式 **`--self-contained true`**。验证产物：`runtimeconfig.json` 含 `includedFrameworks`、目录有 `hostfxr.dll`（约 914 个文件）。注意：仅给 `-o` 而不显式 `--self-contained` 时可能丢成框架依赖，即使 pubxml 里写了 SelfContained。

### 5.3 PowerShell 文本编码

- Windows PowerShell 5.1 按 **GBK** 解析**无 BOM** 的 `.ps1`，中文会乱码。新建/修改 `.ps1` 要么纯 ASCII，要么存成 **带 BOM 的 UTF-8**。
- 本仓库 `NuGet.config` 显式 `<clear/>` 掉机器级回退包目录（VS 的 `Shared\NuGetPackages`）。在仓库外建临时测试项目若报 NU1301 / ResolvePackageAssets 找不到兜底文件夹，把仓库根 `NuGet.Config` 复制过去。

### 5.4 下载挂起：DOH 直连 vs 系统代理（最隐蔽）

- 启动器 DI 给所有 `IHttpClientFactory.CreateClient()` 配了 `DohService.CreateSocketsHttpHandler()`（AppConfig.cs 的 ConfigureHttpClientDefaults）：它用 **DOH 自己解析域名 + `ConnectCallback` 裸 socket 直连，完全绕过系统代理**；命中 ECH 时还会 fork `Resources\curl.exe`。
- 本机联网依赖系统代理 `127.0.0.1:7897`（Clash Verge，winhttp/wininet 都是它，绕过 localhost）。
- 现象差异：经 DOH 直连，`/dl` 302 能跟上，但落到个别 `filecacheNN` 节点时请求挂死（进程停在 "Downloading" 日志，无托管异常）。不同 `filecacheNN`（如 filecache47=209.222.98.224 / filecache52=104.243.43.3）表现不同，且随时间变化，**不要因为某次成功就判定 DOH 路径没问题**。
- 修法（已采用）：`GameBananaModInstaller` 不用工厂客户端，自建一个 handler：`UseProxy=true`（走系统代理）、`AllowAutoRedirect=true`、`AutomaticDecompression=All`。这样稳定。以后凡是 GameBanana / 外网大文件下载都走这条路，别复用 DOH 默认客户端。

### 5.5 同步阻塞与 ConfigureAwait

- Main 在主线程上 `.GetAwaiter().GetResult()` 同步等待协议处理，内部 HttpClient 异步延续若要回到主线程会死锁。所以 Program.cs 用 `.ConfigureAwait(false).GetAwaiter().GetResult()`，installer 内部各 await 也都带 `ConfigureAwait(false)`。

### 5.6 Replace 不要重复删除

- `BackupOldVersion` 用 `Directory.Move` 已把旧目录原子移走，移动后目标路径不存在。早期在其后又调 `Directory.Delete(destination,true)`，必抛 `DirectoryNotFoundException`，导致新版装不上。删掉那次多余删除即可。

### 5.7 Edge 加载的扩展目录可能不是工作区这份

- 用户在 Edge 里「加载解压缩的扩展」，加载的是**桌面上的一个副本文件夹**，不是仓库 `extensions/gamebanana/`。改了 content.js 等文件后，要确认 Edge 实际加载的路径并同步过去（或让用户重新指到仓库目录），否则改了不生效。以后不再打包 crx/zip。

---

## 6. 协议注册与验证

### 注册表（HKCU，免管理员）

`HKCU\Software\Classes\HoYoShadeHub`：`(默认)="URL:HoYoShadeHub Protocol"`、`"URL Protocol"=""`、`DefaultIcon`、`Shell\Open\Command` = `"<exe 全路径>" "%1"`。`UrlProtocolService.RegisterProtocol()` 会写；也可手动改。当前（b5）指向：

```
"D:\APPS\HoYoShadeHub\app-1.3.4b5\HoYoShadeHub.exe" "%1"
```

正式渠道里协议通常只在用户到设置页开启时注册（见 `docs/UrlProtocol.md`）。为联调可直接手动注册 HKCU。

### 真机验证命令（PowerShell）

```powershell
$url='hoyoshadehub://mod/install?game_id=19567&mod_id=719975&file_id=1824270' +
     '&file_name=lshatmodmod.zip&mod_name=LSHatMod' +
     '&download_url=https://gamebanana.com/dl/1824270&replace=1'
$p = Start-Process -FilePath 'D:\APPS\HoYoShadeHub\app-1.3.4b5\HoYoShadeHub.exe' -ArgumentList $url -PassThru
Start-Sleep -Seconds 15   # 下完会弹置顶 MessageBox，进程会一直活到点确定
```

判定：`D:\APPS\XXMI\ZZMI\Mods\gb_719975` 出现且含 `.ini/.ib/.dds/.buf`；Replace 时 `Mods\_backup\gb_719975_<时间戳>` 出现。日志：`C:\Users\thx11\AppData\Local\HoYoShadeHub\log\HoYoShadeHub_<yyMMdd>.log`（搜 modId / PID）。

安装布局：`D:\APPS\HoYoShadeHub\app-1.3.4b5`，`version.ini` 内容 `exe_path=app-1.3.4b5\HoYoShadeHub.exe`。改 `version.ini` 用 ASCII，改前备份。

---

## 7. 待办 / 下一步（按优先级）

1. **右下角 toast 通知替代 MessageBox（用户点名要的）**：现在成功/失败用 Win32 `MessageBox(HWND.NULL,...)`，观感突兀。目标是在显示器右下角显示一个更精致、自动消失的通知（带下载进度/结果）。可选实现：WinUI 3 的 `AppNotification`（系统 toast，需相关包/清单），或自绘一个无边框置顶小窗（DesktopWindowXamlSource / 无标题 WinUI 窗口定位到工作区右下角）。协议进程是无主窗口的短命进程，注意通知窗口要能独立存活。现有关键字：`UrlProtocolService` 里的 `ProtocolBoxFlags`。
2. **修正「拉起即记账」偏差**：启动器下载失败时扩展仍显示已安装。可改为启动器成功后回传（native messaging host），或扩展在拉起后延迟复核磁盘/状态。
3. **支持更多游戏**：在 `ImporterForGameBananaId` 登记 gameId→MI 实例（目前只有 19567→ZZMI）。
4. **更新判定更稳健**：现在只比 `_tsDateUpdated`；可结合 `_sVersion` / 文件 id，处理「换文件但时间戳没变」等情况。
5. content script 与 GameBanana 页面 DOM 强耦合（`.Record/.Name/.Identifiers`），页面改版会失效，需留意。
6. popup 的游戏 id 目前靠手动输入，可从当前标签页 URL 自动读取。

---

## 8. 给接管 AI 的协作提醒

- 这个仓库有**另一个 AI 在同时改启动器**（主战场是启动选项 / 帧率解锁等）。动文件前先 `git -C D:\CODE\HoyoDLSS5 status` 和 `git worktree list`，避开对方未提交的文件；浏览器扩展相关文件（`extensions/gamebanana/`）与启动器 C# 基本不重叠。
- 工作目录：主仓库 `D:\CODE\HoyoDLSS5`（git，main 分支）；本功能开发在 Grok worktree `C:\Users\thx11\.grok\worktrees\code-hoyodlss5\gamebanana`。
- 相关旧文档：`docs/UrlProtocol.md`（协议总览）、`docs/AI-HANDOFF.md`（整个魔改分叉的交接）、`docs/DEV-ENV.md`（环境）。
- 构建/发布前先读 §5；改 `.ps1` 先想编码；改下载先想代理。
