# 魔改 GIMI/SRMI：启动器阶段覆盖 + 远端仓库下载 + 覆盖包安装（调研与方案）

日期：2026-10-08　仓库：`D:\CODE\HoyoDLSS5`　部署：`D:\APPS\HoYoShadeHub`
标签：【已验证】= 有 file:line 或文件哈希支撑；【推断】= 证据支持但未实测；【未验证】= 需实机。

---

## 0. 结论速览

1. **链路已经有一个现成开关面**：原神的注入链里有 `migoto <loader路径>` 这一步，桥在游戏进程内 `LoadLibraryW` 它。【已验证】
   → 想让"启动器用 Hub 自己那份魔改 loader"，**不需要新的注入器、不需要新 API**，只需要（a）Hub 有一份 loader 的存放/部署处，（b）解决 3DMigoto 的"邻居配置"（`d3dx.ini` + `Core` + `Mods`）。
2. **唯一收口是 `XxmiInjector.FindLoader`**（`src\HoYoShadeHub\Features\GameLauncher\XxmiInjector.cs:775-786`）：改这一处即可同时管住原神链（`GameLauncherPage.StartGame.cs:2202-2208`）与崩铁配套路线（`:1946`、`XxmiInjector.cs:521`）。【已验证】
3. **配置面上不可能让 XXMI 去加载别处的 loader**（`xxmi_dll_inject_mode` 只认 `DIRECT/HOOK/SKIP`，loader 路径硬编码为实例目录）→ 一切"覆盖"最终都要落到"Hub 自己把 loader 摆到某个目录，并让**链**或**挂起注入**去用它"。【已验证】
4. **今天是"覆盖了用户 XXMI 安装"的状态**：`D:\APPS\XXMI\GIMI\d3d11.dll` 与 `SRMI\d3d11.dll` 都已被换成仓库里编译的 coexist 版（SHA256 `D269ABC6…2462F`），覆盖记录在 `.build-temp\gimi-coexist-native\deployment.json`，回滚脚本同目录。【已验证】
   → 本需求的实质是：**把这次覆盖从"仓库外脚本"搬进 Hub，并改成"不落用户安装"**。
5. **远端下载 / 覆盖包安装所需的组件几乎都已存在**（catalog 管线、GithubReleaseResolver、DownloadService、ZipExtractor、OverlayTree、InstalledExtensionStore 账本），缺的主要是"一类新的 loader 包定义 + 一个账本 + 一个开关"。
6. ⚠️ **最高优先级缺口**：现有远端下载**全部传 `expectedSha256: null`（零哈希校验）**，覆盖类安装**无账本、不可卸载**。新增 loader 覆盖包时必须一并补上，否则等于把"改用户渲染栈"的权力交给一个不可校验、不可回滚的通道。

---

## 1. 需求与物理约束

| 需求 | 落到代码上是 | 现状 |
|---|---|---|
| 启动器阶段覆盖魔改 loader | 让链里的 `migoto` 步（原神）或挂起注入（崩铁）指向 Hub 自己的 loader 副本 | 收口存在，但 Hub 没有副本存放处 |
| 从远端仓库下载 | catalog 增加一类 loader 包，走现有下载/校验/版本目录 | 管线齐全，缺字段与校验 |
| 通过覆盖包安装 | 离线 zip 走 `LocalPackageInstaller` 的覆盖分支 | 分支存在，但无账本/无卸载 |

**3DMigoto 的硬约束【已验证】**：loader 从**自己所在目录**读配置 —— `_research\XXMI-Libs-v1.2.2-coexist\DirectX11\DLLMainHook.cpp:238-250`（`GetModuleFileName(our_dll)` + `\d3dx.ini`）、`DirectX11\IniHandler.cpp:1501`（默认 `d3dx_user.ini`）。
所以 Hub 副本目录**必须带 `d3dx.ini` 邻居**，且 `d3dx.ini` 里的 `include = Core\GIMI\main.ini` 指向的 `Core`、以及 `Mods`/`ShaderFixes` 都要能看到（`D:\APPS\XXMI\GIMI\d3dx.ini:9/15/25/50`）。
只放一个 `d3d11.dll` = "能加载但没 mod"（静默失效）。

**XXMI 侧的约束【已验证】**：`.build-temp\xxmi-src\game_launcher.py:299-305` 里 `xxmi_dll_path = importer_path / 'd3d11.dll'` 是硬编码；`enums.py:53-55` 只有 `DIRECT/HOOK/SKIP`。签名保护名单 `security.py:42-47` 不含 `xxmi_dll_inject_mode` → Hub 改它不会被 XXMI 判为篡改（与 `XxmiInjector.cs:283-285` 注释一致）。

---

## 2. 现状（今天到底是什么样）

### 2.1 用户 XXMI 里的 loader 已经是魔改版【已验证】

| 文件 | SHA256 | 大小 | 来源 |
|---|---|---|---|
| `D:\APPS\XXMI\GIMI\d3d11.dll` | `D269ABC6BF57BD3E830E410585890A3E90ACA0E5D9110D19D5E98A721E82462F` | 3,193,344 | `_research\XXMI-Libs-v1.2.2-coexist\x64\Release\d3d11.dll` |
| `D:\APPS\XXMI\SRMI\d3d11.dll` | 同上 | 同上 | 同上 |
| `D:\APPS\XXMI\ZZMI\d3d11.dll` | `EA97846A…`（官方 XXMI-PACKAGE v1.2.2） | 3,192,832 | 未替换 |

- 覆盖记录：`.build-temp\gimi-coexist-native\deployment.json:17-25`（含 `previousSha256`、`backup=d3d11.dll.pre-native-coexist-20261008-082705.bak`），回滚 `Rollback.ps1` 同目录。
- 也就是说：**GIMI/SRMI 今天已经是"Hub 想要的 loader"**，只是它是被外部脚本写进用户安装的。

### 2.2 Hub 里没有"自己那份 loader"【已验证】

- `D:\APPS\HoYoShadeHub` 递归搜 `d3d11.dll`：**空**（只有 `_backups\single-prenr-20261008-122644\05-d3d11.dll` 是官方备份、`_evidence\…\modules\GIMI-d3d11.dll` 是取证副本）。
- 现成"Hub 自带 payload 铺到用户数据目录"的机制：**内置模块** `ModuleRegistry.EnsureBundledModuleInstalled`（`src\HoYoShadeHub\Features\Modules\ModuleRegistry.cs:551-580`，从 `AppContext.BaseDirectory\Assets\Modules\<id>` 逐文件比较复制、被占用留到下次）；但 `app-1.4.3.13\Assets\` 下没有 `Modules`，且 `genshin-fsr-bridge` 定义 `IsBundled=false`（`:98-109`）→ 机制可复用，资产要补打包。

### 2.3 三条路线差异【已验证】

| 游戏 | 路线 | loader 从哪来 | 能否用 Hub 副本 |
|---|---|---|---|
| 原神 hk4e | 有序注入链（唯一走桥链的） | 链里 `migoto` 步 → `FindLoader` → `<GIMI 实例>\d3d11.dll` | ✅ 改 `FindLoader` 或改覆写链即可 |
| 崩铁 hkrpg | SRMI-first 挂起注入（不用桥/不用链） | `FindLoader`（`:1946`）→ 挂起注入 SRMI d3d11 | ✅ 同一收口，但需满足 `HasPairedLoaderExports` 前置 |
| 绝区零 nap | 通用 XXMI 分支，**由 XXMI 自己注入** ZZMI loader；Hub 只额外注 ReShade/Opti/模块 | `ZZMI\d3d11.dll`（官方版） | ❌ 无 `migoto` 步可用；要走同一套需先解决 ZZMI 用 DX12/`-use-d3d12` 等前提 |

---

## 3. 方案：启动器阶段覆盖（候选接入点对照）

| # | 方案 | 接入点 | 改动面 | 风险 |
|---|---|---|---|---|
| S1 | 只改覆写链清单：`migoto <Hub 副本>` | 覆写读取 `GameLauncherPage.StartGame.cs:2236-2261`；常量 `OptiScalerRuntime.cs:1252`；解析 `:1259-1305`；桥侧 `Dx11FsrBridge.cpp:14931-14932,15061-15101` | **0 行代码**（一个文本文件） | 只覆盖原神链；副本缺邻居=静默无 mod；写错=整局无模型替换 |
| **S2** | **在 `FindLoader` 加"Hub 副本优先"解析层**（配置键 + 存在性/版本校验 + 失败回退用户实例） | `XxmiInjector.cs:775-786`、`XxmiLocator.cs:237`；影响 `StartGame.cs:2202-2208`（GIMI）与 `:1946/:2091/:521`（SRMI） | **小**（1 方法 + 1~2 配置键） | SRMI 副本必须带两个私有导出（`StarRailXxmiLaunchRouting.cs:10-11,26-69`；缺则维持"安全拒绝启动"`XxmiInjector.cs:528-531`）；仍需 S3 解决邻居；**不写用户 XXMI** |
| S3 | Hub 侧建**影子 MI 实例目录**（loader + `d3dx.ini` + `Core\GIMI\main.ini` 链；`Mods`/`ShaderFixes` 用 junction 指回用户实例） | 部署器复用 `ModuleRegistry.cs:551-592`；解析接 `XxmiInjector.cs:784` | 中（部署器 + junction + 清理） | junction 与中文路径对 3DMigoto 的友好性需实测（`XxmiPage.xaml.cs:473` 有中文路径提示）；Mods 双写一致性 |
| S4 | 把 loader 提升为 Hub **内置模块**（`Assets\Modules\<id>`，获得版本/回滚/下载） | 定义 `ModuleRegistry.cs:98-109`、部署 `:551-580`、`catalog\modules.json` | 中-大（打包资产） | **必须继续用 `migoto` 动词**：模块的"外部批量注入"语义拿不到 `Local\3DMigotoLoader` 互斥体上下文（`Dx11FsrBridge.cpp:15066-15074`），顺序也不可控 |
| S5 | 就地覆盖用户 XXMI 实例的 `d3d11.dll`（= 现状脚本化） | 现状在 Hub 外（`deployment.json`）；内置挂点 `StartGame.cs:2204` 前 | 小-中 | **直接写用户安装**；XXMI 官方更新会覆盖（`StartGame.cs:2060-2062` 正为此不启动官方启动器） |
| S6 | 铺到游戏目录当代理 dll（历史规划） | 死配置 `AppConfig.cs:2306-2311`（`hysx_xxmi_deployed_game_dir` **零引用**） | 大 | `d3d11.dll` 落 mhyprot 扫盘视野，**不推荐** |

**推荐落地顺序**：S1（先验证物理约束）→ S2 + S3 → S4。明确不做 S5 / S6。

---

## 4. 方案：远端仓库下载

### 4.1 可直接复用的现有组件【已验证】

| 能力 | 现有实现 | file:line |
|---|---|---|
| catalog 抓取 + 原子落盘 + 24h 节流 + 离线种子 | `RemoteCatalogService` | `Features\Plugins\RemoteCatalogService.cs:22`（URL 前缀 `https://raw.githubusercontent.com/thx114/HoYoShadeHub-DLSS5/main/catalog/`）、`:49-63`、`:106-143`、`:146-159`、`:206-234` |
| catalog 解析 + 同 id 覆盖 + 墓碑 | `ModuleCatalogFile` / `ModuleRegistry` | `Features\Modules\ModuleCatalogFile.cs:11-53,77-87,103-148`；`ModuleRegistry.cs:120-151` |
| 版本/资产解析（不吃 API 限额） | `GithubReleaseResolver` | `Extensions\Services\GithubReleaseResolver.cs:42,322,729-755`；缓存 `:78-83` |
| 字节下载（续传/校验/原子发布） | `DownloadService` | `Extensions\Services\DownloadService.cs:78-167`（**`expectedSha256` 传了才校验** `:151-160`）、`:174-243` |
| 解压 | `ZipExtractor` | `Extensions\Archives\ZipExtractor.cs:37-58` |
| 版本化目录布局 + `build.json` + `state.json` | `OptiScalerLibrary` / `OptiScalerDownloader` | `Extensions\OptiScaler\OptiScalerLibrary.cs:55,67-68`；`OptiScalerDownloader.cs:109-246,262-273` |
| 每游戏选版本 | `AppConfig` | `AppConfig.cs:1502-1507`（`Get/SetModuleVersion`）、`:1745-1753`（`GetUsedModuleKeysOrNull`） |
| 注入集解析（勾选 ∩ 全局开 ∩ 文件在 + 顺序） | `ModuleRegistry.ResolveInjectionDlls` | `ModuleRegistry.cs:407-463` |

### 4.2 建议形态

- 新 catalog：`catalog/loader-packs.json`，条目在 `ModuleManifest` 基础上补 **`sha256` / `size` / `files[]` / `gameBiz[]` / `installTarget`**（这些字段在 `ExtensionManifest` 里已有先例：`Sha256:74-76`、`ConflictsWith:54-55`、`Requires:61-62`、`GameBiz:35-36`）。
- 落盘：`<CacheRoot>\loader-packs\<id>\<tag>\`（照 `OptiScalerLibrary.DirectoryFor` 的 `<root>\<sourceId>\<tag>` 布局；`CacheRoot` 定义见 `AppConfig.cs:435-480`）。
- 下载：`DownloadService.DownloadToFileAsync(url, zipPath, manifest.Sha256, …)` —— **务必把 hash 传进去**。

---

## 5. 方案：覆盖包安装（离线）

现有覆盖类机制对照（供选型）：

| 机制 | 发现方式 | 校验 | 账本/卸载 |
|---|---|---|---|
| `bridge-chain.override.txt` | 固定路径 | 仅"能解析出 ≥1 步" | 无（删文件即回退） |
| addon `pack.json` | `GameAddonPack.IsPackDirectory` `:101-117` | `HysxFileLink.IsUpToDate` | 目录级删除 `:314-321` |
| 包内用户内容 `game_files\`/`launcher_files\` | `GameAddonPackUserContent.cs:17-27` | 仅 `auto.json` 的 SHA256 同意键 | **无** |
| **一键覆盖包 `filelist.json`** | `LocalPackageInstaller.DetectKindFromEntries` `:162-199` | 仅 `hysxOverlay`/`schema>0`，**无 hash/size 比对** | **无账本、无备份**；`OverlayTree` 换 inode `:1035-1075` |
| `.hysx` 扩展包 | `manifest.json`/`hysx.json` | **逐文件 SHA256 账本** `ExtensionInstaller.cs:341-347` | **唯一有真卸载 + 回滚** `:441-506`、`:632-649` |

**建议**：loader 覆盖包 = 「`filelist.json` 覆盖包」的识别 + 「`.hysx` 扩展包」的账本/卸载。
- 识别：`LocalPackageInstaller` 加一个 `LocalPackageKind.LoaderPack`，用顶层 `filelist.json` + `loader\` 树判定。
- 落位：复用 `OverlayTree` 的"先写 `.hysx-new` 再 Move 换 inode"（`:1035-1075`，跳过 `.hysx`）。
- 账本：安装前把每个将被替换文件的相对路径 + 现有 sha256 写进 `<CacheRoot>\loader-packs\<id>\installed.json`（照 `InstalledExtensionStore` 的 temp+move 与坏文件归档 `:42-55,82-93`）。
- 卸载：校验 sha256 后再还原/删除（照 `ExtensionInstaller.UninstallAsync`）。
- 路径防逃逸：复用 `ShadeHost.ResolveRelative`（`ShadeHost.cs:65-75`，仓库里唯一显式 `..` 防护）。

---

## 6. 落地步骤与验收判据

| 阶段 | 做什么 | 验收判据 |
|---|---|---|
| 0（今天可做） | 在 `D:\APPS\HoYoShadeHub\bridge-chain.override.txt` 把 `migoto` 指向一份 Hub 侧 loader 副本 + 邻居配置 | 进游戏后桥日志出现 `migoto` 执行、游戏内模型替换生效；**不动用户 XXMI** |
| 1 | 实现 S2 + S3：Hub loader 影子实例目录 + `FindLoader` 优先解析 + 失败回退 | 删掉/改坏副本时自动回退用户实例，日志可辨；崩铁在缺导出时仍拒绝启动而不是崩 |
| 2 | 远端：`catalog/loader-packs.json` + 安装器 + **hash 校验** | 断网/更换 hash 时拒绝安装并报错；安装后 `build.json` 记录 tag 与来源 |
| 3 | 离线覆盖包：新 kind + 账本 + 卸载 | 卸载后逐文件哈希回到安装前；被用户改过的文件不删只报告 |
| 4 | 提升为内置模块（可选） | 版本下拉/回滚/删除版本可用，且链仍走 `migoto` |

---

## 7. 必须一并修的既有隐患【已验证】

1. **SKIP 残留（缺口 A）**：`GameLaunchPipeline.cs` 只调 `PrepareManualMode`（`:112`），**从不写/不还原 `SKIP`、也不写链** → 上一次 UI 启动留下的 `xxmi_dll_inject_mode=SKIP` 会让 CLI 启动"静默没有模型替换"。
2. **SKIP 残留（缺口 B）**：`RestoreInjectMode` 只在 `UseXxmiInject==true && !UseInjectMode` 分支调用（`StartGame.cs:1931`/`:2002`）→ 用户取消勾选「启用XXMI」后 SKIP 永久留在用户配置里。
   → 若把 loader 覆盖做成默认行为，建议把 SKIP 的写入/还原与链文件收敛到同一生命周期，并给 CLI 补齐。
3. 备份文件不自动清理（用户 XXMI 根目录已有多份 `.bak-before-*`）。

---

## 8. 风险与红线

- **不写用户 XXMI 安装**（S2/S3/S4 满足；S5/S6 不满足）。
- **反作弊可见面**：S1–S4 都不新增"外部注入窗口"，仍走"桥在游戏进程内 `LoadLibraryW`"的既有路径（`Dx11FsrBridge.cpp:14820-14856`）；增量只是"进程里出现一份非官方 3DMigoto 镜像"，而这一点**今天已经成立**。
- **载荷校验**：现有远端 module/overlay/zip **零哈希**、无签名；`filelist.files[].size` 声明了但从不比对。新增通道必须自带 sha256 校验。
- **并发**：没有跨进程安装锁（只有单实例 Mutex `Program.cs:193` 与 `MultiplayerGameGuard` 安全门）→ 两个游戏同时启动时靠"文件占用 → 报错"兜底。
- **卸载缺失**：覆盖类安装(`InstallOverlayAsync`)目前无账本 → 一旦覆盖用户的 loader，无法证明"改回了什么"。

---

## 9. 待你决定

1. loader 副本放哪：`<CacheRoot>\loader-packs\`（推荐）还是随包 `Assets\Modules\`？
2. 覆盖开关的默认值：默认"用 Hub 副本"，还是默认"用用户 XXMI、需手动勾选"？
3. 是否接受 S3 的 junction（`Mods`/`ShaderFixes` 指回用户实例），还是要整份拷贝（体积大、双写）？
4. 崩铁 SRMI 副本的来源：继续用 `_research\XXMI-Libs-v1.2.2-coexist` 构建，还是改从远端仓库按 tag 下载？
5. 是否要支持绝区零 ZZMI（需先解决 DX12 前提）？

---

## 10. 证据索引（关键 file:line）

- loader 路径收口：`src\HoYoShadeHub\Features\GameLauncher\XxmiInjector.cs:775-786`；`src\HoYoShadeHub\Features\Xxmi\XxmiLocator.cs:237`
- 链生成/覆写：`src\HoYoShadeHub.Extensions\OptiScaler\OptiScalerRuntime.cs:1117,1179-1241,1244-1245,1252,1259-1305`；`GameLauncherPage.StartGame.cs:2191-2234,2236-2261,2275-2286`
- 桥侧链执行：`D:\CODE\genshin_fsr_brigde\Dx11FsrBridge\Dx11FsrBridge.cpp:14857-14862,14914-14951,14990-15121`（`migoto` 互斥体 `:15066-15074`）
- XXMI 配置写入：`XxmiInjector.cs:178-270`（manual）、`:290-347`（SKIP）、`:360-420`（restore）
- 远端管线：`RemoteCatalogService.cs:22,49-63,106-143,146-159,206-234`；`GithubReleaseResolver.cs:42,78-83,322,729-755`；`DownloadService.cs:78-167,174-243`
- 覆盖类安装：`LocalPackageInstaller.cs:162-199,569-768,1035-1075`；`OverlayManifest.cs:7-19,22-30,36-41,44-49,52-61,93-114`
- 账本/卸载模板：`ExtensionInstaller.cs:341-347,441-506,632-649`；`ExtensionManifest.cs:8-92`；`ShadeHost.cs:60,65-75`
- 现状覆盖记录：`.build-temp\gimi-coexist-native\deployment.json:17-25`、`Rollback.ps1`、`.build-temp\prenr-control-snapshot-20261008\rollback.ps1`
