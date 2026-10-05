# 当前进度与注意事项

> 最后更新：2026-10-04。下面是「XXMI 启用后游戏起不来」的修复（源码已改，**按用户要求本次不部署**），
> 之后是原神 FSR Bridge / OptiScaler / HoYoShadeHub 迁移工作的状态；旧的崩铁事项保留在下方历史进度中。

## XXMI「启用XXMI」启动失败修复（2026-10-04）

**症状**：启动选项勾了「启用XXMI」时点「启动游戏」，游戏起不来 —— 启动器日志 `Failed to start game process`
（12 秒等不到进程），XXMI 自己的日志 `无法检测到游戏进程 xxx.exe 的窗口`。

**根因（实机日志坐实）**：Hub 唤起 XXMI Launcher 后 **0.25 秒**就起游戏，而 XXMI `GameLauncher.launch()`
的**第一件事**是 `_ensure_game_close()`「确保游戏已关闭」—— 按进程名把已经在跑的游戏**杀掉**。
- 2026-10-04 00:30（星铁）：Hub 00:30:45.335 起 XXMI → 45.582 起游戏 → XXMI 46.193
  `Stopping process … StarRail.exe`（0.6 秒后把刚起来的游戏杀掉）；
- 同一次：HoYoShade inject.exe 已经注入成功（退出码 0），但游戏进程随即消失 →
  `StartProcessToLogAsync` 12 秒轮询全空 → `Failed to start game process`；
- 00:11 那次一模一样（09.137 起 XXMI → 09.307 起游戏 → 09.718 被杀）。

**正确流程（用户给的，已按它实现）**：1 点「启动游戏」→ 2 启动器把 XXMI 本游戏导入器写成**手动模式**
→ 3 启动器唤起 XXMI 走它自己的启动流程**并等它把注入器挂好**（手动模式下 XXMI 不拉起游戏，只挂
3dmloader 钩子等进程）→ 4 启动器注自己的东西（shade / OptiScaler / 模块）→ 5 启动器启动游戏。
不启用 XXMI 时就是 4 + 5。

**改了什么（源码，尚未部署）**
- `src/HoYoShadeHub/Features/GameLauncher/XxmiInjector.cs`
  - `PrepareManualMode()` 除旧键 `process_start_method` 外，**还写 XXMI 2.3.9 真正生效的
    `game_launch = "MANUAL"`**。2.3.9 把「启动方式」搬进了 `GameLaunch` 枚举（DIRECT/STEAM/EPIC_GAMES/
    CUSTOM/MANUAL），旧键只剩占位 `OPTION_REMOVED` —— **只写旧键等于没写**，XXMI 仍按 DIRECT 自己拉游戏
    （以前那次「原神启动后去找崩铁」就是它）。这两个键都不在 XXMI 的签名保护名单
    （`config/security.py` 只保护 unsafe_mode / run_pre_launch / custom_launch / run_post_load /
    extra_libraries），外改不会被重置。
  - **还必须钉住进程名：`game_process_exe_enabled = true` + `game_process_exe = <真实 exe 名>`。**
    手动模式下 XXMI 拿不到游戏 exe 路径（`game_launcher.py get_game_path()` 对手动模式直接返回 None），
    `get_game_exe_name()` 于是退回**导入器的默认值** —— GIMI 默认 `GenshinImpact.exe`，而国服原神是
    `YuanShen.exe`（本机 `Genshin Impact Game` 目录里只有 `YuanShen.exe`，Hub 日志里的注入目标也一直是
    YuanShen.exe）。这个名字会喂给 `WaitForInjection` / `_ensure_game_close` / 等窗口检测，
    名字不对 XXMI 就盯着一个永远不会出现的进程，60 秒后报「无法检测到游戏进程 GenshinImpact.exe 的窗口」，
    **模型替换直接失效**（比 3 星铁不受影响是因为 SRMI 默认恰好就是 `StarRail.exe`）。
  - `StartLauncherIfNeeded()`（fire-and-forget）→ **`ArmForManualLaunchAsync()`**：以
    `"<游戏 exe>" -x <导入器> -n` 唤起后**等注入器就绪再返回**，超时 25 秒。就绪判据两个（任一成立）：
    `Local\3DMigotoLoader` 互斥体出现（`HookLibrary` 建，跟 XXMI 日志级别无关）、或启动日志出现
    `Waiting for user to start the game process`。两者都在 `_ensure_game_close()` **之后**。
  - 就绪前若互斥体已被占（上次没等到游戏、弹着错误框停在那儿的残留 XXMI —— 实机见到好几个，还占着
    互斥体让新实例 `HookLibrary` 返回 100）→ 先收掉残留实例；最终没就绪就收回本次实例并**照常启动游戏**
    （只是这次没有模型替换，不拦启动）。
  - 备注：`-x` 存在时 XXMI 会**忽略**位置参数里的 exe 路径（`application.py get_active_importer` 只在
    没给 `-x` 时才用它认游戏），带上只是把「这次是哪个游戏」写进命令行，手动模式下不会真把它拉起来。
- `src/HoYoShadeHub/Features/GameLauncher/GameLauncherPage.StartGame.cs`
  - 按 2 → 3(等待) → 4 → 5 接线；结果记进 `AppConfig.XxmiLastLaunch`，没就绪时弹 warning toast。
  - 真实 exe 名用 `GetGameExeNameAsync()` 取一次，同时喂给「写手动模式」和步骤 3 的命令行。
  - 更新两处过时注释 / 日志（原「当前实现还未接上 Hook（3dmloader 的 HookLibrary 在 App 进程里会 200）」）。

**验证方法（部署后）**
- 启动器日志顺序应是：`XXMI Launcher 已调起（pid …）` → `XXMI 注入器已就绪（x.x 秒）` → 之后才是
  `Starting "HoYoShade" injector` / `Start game`；
- XXMI 日志（`D:\APPS\XXMI\XXMI Launcher Log.txt`）应是：`Waiting for user to start the game process
  <真实 exe 名>…`（原神应是 **YuanShen.exe**，不能是 GenshinImpact.exe）→ 游戏起来 →
  `Successfully passed late d3d11.dll -> … hook check!` → `App Exit`；
- 游戏内模型替换生效（`d3d11_log.txt`）；
- 配置回读：`D:\APPS\XXMI\XXMI Launcher Config.json` 里对应导入器应是 `game_launch: "MANUAL"` +
  `game_process_exe_enabled: true` + 真实 exe 名（备份 `<配置>.bak-before-manual-mode`）。

**部署状态：已部署（2026-10-04 22:13:41）。** 覆盖 `D:\APPS\HoYoShadeHub\app-1.4.1.2\HoYoShadeHub.dll`
（只需这一个文件，改动全在 HoYoShadeHub 程序集；根目录 `HoYoShadeHub.exe` 是 PortableLauncher，读
`version.ini` 的 `exe_path` 拉 `app-1.4.1.2\HoYoShadeHub.exe`，不用重编）。

- 出包：`.build-temp\publish-xxmi-arm-20261004-221230`（`-p:PublishReadyToRun=true -p:Version=1.4.1.2`），
  部署后 `HoYoShadeHub.dll` = 9,375,744 字节，SHA256 `ADC2E003F09953F3CB338379CFCB9484DCE3055C9FF9ACF1C8A4EEC2F88AD765`
  （与出包产物哈希一致）；
- 二进制核对：新增标记 `game_process_exe_enabled` / `3DMigotoLoader` / `Waiting for user to start the game process`
  / `XXMI 注入器已就绪` 均在，旧标记「当前实现还未接上 Hook」「StartLauncherIfNeeded」已不在；
- 冒烟：启动器重启正常（pid 32416，窗口 HoYoShade Hub，启动日志无异常）；
- 回滚：关掉启动器 → 把 `D:\APPS\HoYoShadeHub\_backup-app-1.4.1.2-xxmi-arm-deploy-20261004-221351\HoYoShadeHub.dll`
  覆盖回 `app-1.4.1.2\`（同目录还有更早一份 `_backup-app-1.4.1.2-xxmi-arm-20261004-005249\`，字节相同）。
- **源码尚未 git 提交**（工作区改动：`XxmiInjector.cs`、`GameLauncherPage.StartGame.cs`、`PROGRESS.md`）。

重新出包：
```powershell
D:\CODE\HoyoDLSS5\.dotnet10\dotnet.exe publish src\HoYoShadeHub -c Release -r win-x64 -o <out> `
  -p:Platform=x64 -p:PublishReadyToRun=true -p:PublishTrimmed=false -p:Version=1.4.1.2
```

**已知边界**
- 只有启动页「启动游戏」这条路做了「写手动模式 + 等 XXMI 就绪」。CLI / 覆盖包 `run` 动作走的是
  `GameLaunchPipeline.LaunchAsync`（`LauncherActionRunner`），**没接** —— 那条路上的「启用XXMI」不会有
  模型替换（改动前也一样）。
- 「注入模式」与「启用XXMI」仍然互斥（勾注入模式会自动关掉 XXMI），本次没动。
- 残留场景：XXMI 已经挂好钩子之后，如果**后面的启动流程自己中途 return**（例如 `inject.exe` 起不来
  → `Failed to start {ShadeName} injector`），XXMI 会一直等到它自己的 `process_timeout`（星铁 30 秒 /
  原神 60 秒）再弹错误框并留在内存里。这时候没有游戏可注，属于既有失败路径，本次没管
  （下次启动会被「收掉挂着钩子的残留实例」清掉）。
- XXMI 等游戏窗口的预算是它自己的 `process_timeout`（星铁 30 秒，从挂钩子后开始算）；我们最多等它 25 秒
  就绪，就绪后 1 秒左右起游戏，留有余量。

## 原神 FSR Bridge + OptiScaler（2026-10-03）

### Opti 超分切换 / FSR 近原生测试（2026-10-03 17:19）

- **本次用户目标**：OptiScaler 可切换超分，并尝试 FSR + 0.99 渲染精度。
- **已确认切换问题**：16:59 启动的会话在 17:01–17:02 多次切换后仍记录 `Creating new DLSS upscaler`；活动根 DLL SHA256 仍为 `D24D3D9E...`。解除 FG-only 后端锁定的源码/12:32 构建未部署到该会话。
- **已完成**：补充 FG-only 策略回归测试（已配置 DLSS/FSR31/XeSS/W12 保留、auto 回退、重载幂等、用户再选择、FG 参数不变），FG-only 和完整构建模式均通过；重新构建 Release x64 FG-only DLL，退出码 0。日志标记改为 `selectable DX11/DX12 upscaler + FG`。
- 新 DLL SHA256：`2C806DE7ECC3C7C6BF4DFCA27653BD475A67D8A2800DF844239F3AB0F32C6284`。
- **精度差异未隐藏**：已安装 v2.3.1 Bridge 的浮点档位数组和菜单字符串确认最高档为 **0.999，不是精确 0.99**。最新运行日志仍为 2304×1440 → 2560×1600（0.9）。本地 Bridge 源码版本为 2.2.0，不能为了改精度直接替换已验证的 2.3.1；本次保留 Bridge DLL。**精确 0.99 尚未实现**，先准备 FSR 3.1 + 现有 0.999 近原生档测试。
- **部署状态：已完成（2026-10-03 17:26:51）**。因启动器仍占用文件，已结束 HoYoShadeHub PID 66508，让后台部署继续；未结束原神进程。根/嵌套 Opti DLL、主 INI / `profiles\hk4e_cn.ini` / 嵌套 INI / Bridge payload sidecar 已更新为 `Dx11Upscaler=fsr31`，Bridge 精度缓存为 `index 8`。DLL SHA256：`2C806DE7ECC3C7C6BF4DFCA27653BD475A67D8A280DF844239F3AB0F32C6284`。部署文件校验通过；其他 INI 键（含插帧）未改，真实游戏行为仍待新会话验证。
- 暂存、脚本、回滚点及实时状态：`D:\CODE\HoyoDLSS5\build\handoff\fsr-near-native-20261003-171240\`。真实完成状态以 `deployment-status.json` 为准；超时可在关闭游戏/启动器后运行该目录 `deploy.ps1`。等待前备份在 `backup-before-wait`，应用前最新备份在 `backup-before-apply-*`。
- **待验收**：重开后日志确认新 DLL 的 `selectable` 标记、`FSR 3.1` 创建成功、渲染尺寸接近 0.999 输出，并测试菜单切回 DLSS / 再切 FSR 与画面、帧时间、FG。编译/文件状态不代表实机已修复；未发布或推送。

### 当前活动版本 / 文件

- 启动器：`D:\APPS\HoYoShadeHub`，活动版本 `1.4.1.2`。
- 当前模块 DLL：`D:\APPS\HoYoShadeHub\Cache\modules\genshin-fsr-bridge\Dx11FsrBridge.dll`
  - 来源：`D:\APPS\test\GenshinFSRBridge_v2.3.1\payload\Bridge\Dx11FsrBridge.dll`
  - FileVersion：`2.3.1.0`
  - 长度：`628736`
  - SHA256：`D707AC90BF9AB2FD8C90BCC7A4CAB1D216030177CC75E8C1874864E1AB46D6A3`
- 当前 OptiScaler DLL 与 test 包 DLL SHA256 一致：
  - `D:\APPS\HoYoShadeHub\OptiScaler\mfg-ada\mfg-ada-0.1.8\OptiScaler.dll`
  - test：`D:\APPS\test\GenshinFSRBridge_v2.3.1\payload\OptiScaler\OptiScaler.dll`
  - FileVersion：`0.1.6.0`
  - SHA256：`D24D3D9E1EC516EDC6E176DDDAB8644D6ED2A7781E8E5CAFABBAAD1FAAD93EF3`

### 已确认的关键事实

1. **OptiScaler DLL 本身没有坏。** 同一份 OptiScaler DLL 在 test 包中能工作。
2. **v2.3.1 Bridge 不读取 `Dx11FsrBridge.autoload.txt`。** test 包的 `fps_config.json` 是 Bridge、OptiScaler、AntiPlayerMosaic 三个 DLL 分开注入。
3. test 包的 `OptiScaler.ini` 与启动器正确的主配置内容基本相同，主要差异是 `OptiDllPath`；当前主配置和 `profiles\hk4e_cn.ini` 已保持：
   ```ini
   Enabled = true
   FGInput = Upscaler
   FGOutput = DLSSG
   AdaMfgUnlock = true
   AllowedFrameAhead = 1
   ```
4. test 的 Opti 日志显示 `amd_fidelityfx_upscaler_dx12.dll already loaded at memory`，而启动器之前是 OptiScaler 自己后加载 FFX12。test 包额外有：
   ```text
   payload\AMD\amd_fidelityfx_upscaler_dx12.dll
   ```
   当前启动器缓存已经补入同一份 DLL：
   ```text
   D:\APPS\HoYoShadeHub\Cache\modules\AMD\amd_fidelityfx_upscaler_dx12.dll
   ```
5. 启动器透明窗口时的真正错误是：
   ```text
   Dx11WithDx12::OpenSharedHandle error: -7785FFFB
   PrepareFgResourceCache Dx11wDx12 FG input cache preparation failed
   slHookGetBuffer: proxyBuffer is NULL
   ```
   这不是单纯缺少 `Dx11FsrBridge.dll`，而是 OptiScaler 的 D3D11→D3D12 共享资源链没有复现 test 的初始化环境。

### 当前启动器改动

- `genshin-fsr-bridge` 已从随包 DLL 改为远端模块元数据；当前 `app-1.4.1.2\Assets\Modules\genshin-fsr-bridge` 不再内置 Bridge DLL。
- 模块来源指向 `thx114/genshin_fsr_brigde`；缓存中保留 test 的 v2.3.1 DLL。
- 加入 v2.3.1 Bridge 的两阶段注入：Bridge 后再注入 OptiScaler。
- 参考 `unlockfps_nc` 加入 `SeDebugPrivilege` 与批量 DLL 注入。
- 加入 test 风格的 `CreateProcess` 早期 Bridge/OptiScaler 注入路径。
- 预留 Bridge 旁的 payload sidecar：
  ```text
  D:\APPS\HoYoShadeHub\Cache\modules\OptiScaler\OptiScaler.ini
  ```
  该文件让 v2.3.1 Bridge 能按 test 的 payload 相对布局找到 OptiScaler 配置。
- 普通启动中 ReShade 曾被临时隔离，计划在 Bridge/OptiScaler 稳定后再后置注入；后置注入逻辑已接入但尚未重新完成实机验收。

### 当前未解决问题

- test：Bridge + OptiScaler 正常，Opti 日志没有 `OpenSharedHandle` 错误。
- HoYoShadeHub：Bridge/OptiScaler 可以显示注入成功，但仍出现透明窗口和 `proxyBuffer is NULL`。
- 当前最可疑的剩余差异是 **FFX12 在 Bridge/OptiScaler 初始化前后的加载顺序**，以及 test 使用的完整 payload 目录环境，而不是 DLL 版本或 FGInput/FGOutput 配置。
- 代码中已经尝试加入 FFX12 预加载，但最近一次完整包构建/部署还需要重新确认；不要把“编译成功”当成已经完成实机验证。

### GitHub Release 注意事项

- `v2.3.1-fg-20261003` 和 `v2.3.1-fg-delay-20261003` 当前归档包内的 Bridge 文件版本仍显示为 `2.2.0.0`，不能作为 test v2.3.1 DLL 的可靠下载来源。
- 启动器已增加 Bridge FileVersion 校验：不是 `2.3.1.0` 的 Bridge 不应被选为有效注入目标。
- 在远端 Release 资产修正前，当前缓存里的 test v2.3.1 DLL不能被“重新下载”覆盖。

### 重要回滚点

- `D:\APPS\HoYoShadeHub\_backup-app-1.4.1.2-batch-inject-20261003-153644`
- `D:\APPS\HoYoShadeHub\_backup-app-1.4.1.2-ffx-preload-20261003-143812`
- `D:\APPS\HoYoShadeHub\_backup-app-1.4.1.2-skip-reshade-20261003-142239`
- `D:\APPS\HoYoShadeHub\_backup-opti-root-dlssg-20261003`
- `D:\CODE\HoyoDLSS5\_backup-bridge-not-bundled-20261003`

### 下一步

- [ ] 让 Bridge 在加载 OptiScaler 前稳定预加载 test 的 FFX12 SDK，并在 Bridge 日志中确认与 test 一样的 `already loaded at memory`。
- [ ] 重新部署后做一次 test / HoYoShadeHub 同时段 A/B 日志比较。
- [ ] 透明问题未通过真实游戏验证前，不宣布修复完成。
- [ ] 修正 GitHub Release 资产后，再恢复模块页面的正常重新下载流程。

---


> 最后更新：2026-10-02。本文档记录启动器 + OptiScaler fork 的工作进度、部署状态和注意事项；
> 历史细节以 git log / release-notes / Claude 记忆（`~/.claude/projects/D--CODE-HoyoDLSS5/memory/`）为准。

## 版本状态

- **1.4.0.0 已发布**（tag `v1.4.0.0`，main @ `33a68ea` 起）：
  CLI 完整启动管线、覆盖包 v2 用户内容层、插件条件云端化、帧率解锁多版本、鸣潮黑名单绕行等。
  - 更新 zip：`build/release/HoYoShadeHub_Portable_1.4.0.0_x64.zip`（已传 GitHub Release）
  - 完整包：`build/release/HoYoShadeHub_Portable_1.4.0.0_x64_Full.zip`（**仅本地**，不上 GitHub）
- **1.4.0.0 之后未发布的提交**：`5d71012` 启动选项「不用 HoYoShade 注入器」（见下），尚未打 zip。

## 进行中（2026-10-02）

### 崩铁卡旧帧/NR 空转 —— 注入顺序竞态修复，待用户验证

- **根因假设**（前三个假设均被推翻：安装卫生→插件构成→NR 钩点配置，见记忆
  `hsr-stale-frame-replay-bug`）：inject.exe 注 ReShade 与启动器注 OptiScaler 是两条
  并行路径，各自等进程出现就注。OptiScaler 抢先 hook 上 D3D11/DXGI 时 NR 吃不到原生
  DLSS 数据 → 无 DLSS 时段卡旧帧 + NR 空转。时好时坏 = 竞态特征。
- 提交 `7bdd2d9`：`DllInjector.WaitForModuleAsync`（轮询模块表等目标模块）；
  `InjectDllSpec`/`InjectSpec` 新增 `WaitForModule`；shade 走 inject.exe 时 OptiScaler
  带 `WaitForModule: "ReShade64.dll"`（黑名单绕行/跳过注入器路径 shade 是 specs[0]
  顺序已保证，不等）；页面 + CLI 两条注入循环统一在 Inject 前等 60s，超时降级照旧注。
- **部署状态**：已 publish 到 `build/deploy-skipinject/HoYoShadeHub/`（和跳过注入器
  开关同一个暂存目录），后台任务等启动器退出后把这 6 个主二进制补拷进
  `app-1.3.9.9`。**用户需关一次启动器**，重开后连开崩铁多次验证（竞态是概率性的，
  单次成功不算数）。日志判据：`injection ordered after ReShade64.dll (pid ...)`。

### 「不用 HoYoShade 注入器」开关 —— 待用户实测

- 提交 `5d71012`：启动选项（勾了 HoYoShade / OpenHoYoShade 时显示）新增按游戏开关
  **「不用 HoYoShade 注入器」**：跳过 inject.exe，由 Hub 自己的 DllInjector 等游戏进程注
  ReShade64.dll（和鸣潮黑名单绕行同一条路径）。
- 三处启动路径全部接入：`LaunchGameWithShadeAsync` / `StartGameWithInjectModeAsync` §2.6 /
  CLI `GameLaunchPipeline`，条件统一为 `IsBlacklisted(...) || entry.SkipShadeInjector`。
- 持久化：`games.json` 新增 `skipShadeInjector` 字典（`GameEntryStore`）。
- **目的**：给崩铁实测自定义注入路径。
- **部署状态**：919 个文件已进 `D:\APPS\HoYoShadeHub\app-1.3.9.9`；6 个主二进制
  （HoYoShadeHub.exe / .dll / .Core / .Extensions / .Language / .RPC）因启动器在运行被占用，
  已挂后台任务等进程退出后自动补拷（源：`build/deploy-skipinject/HoYoShadeHub/`）。
  若后台任务丢了，手动补拷这 6 个文件即可。
- **测法**：启动器 → 崩铁 → 勾 HoYoShade → 勾「不用 HoYoShade 注入器」→ 开始游戏。
  日志判据：`按启动选项不用 HoYoShade 注入器 —— 跳过 inject.exe，改由 Hub 注入 ...`。

## 注意事项 / 红线

### 部署（D:\APPS\HoYoShadeHub）
- 只覆盖 `app-1.3.9.9\` 子目录；**绝不碰根目录** stub exe、`version.ini`、`config.ini`
  （动根 exe 会导致用户数据漂到 C 盘）。
- 拷贝前确认启动器已关闭（主二进制会被占用）；**不要 taskkill**。
- 发布用 `dotnet publish -r win-x64 --self-contained` + `robocopy //E //R:0 //W:0`。

### 构建
- 启动器必须用 `C:/Users/thx11/.dotnet10/dotnet.exe` 且 **`-p:Platform=x64`**
  （默认 AnyCPU 会报 `WindowsAppSDKSelfContained requires a supported Windows architecture`）。
- git push 需要 `-c http.proxy=http://127.0.0.1:7897`；gh CLI 不需要代理。

### 双 ReShade runtime（by design，勿当 bug 修）
- DX11 游戏 = runtime 1（游戏目录 `ReShade.ini`）；OptiScaler FG 桥的 D3D12 世界 = runtime 2
  （`ReShade2.ini`）。1.4.0.0 起每次启动镜像同步 ReShade2.ini，1.3.9.9 不会（永远 stale）。
- 排查「插件没生效」先看游戏日志 ReShade.log 的 `Loading add-on from` 列表。
- `overlay_frozen` 计数器 = ReShade 覆盖层状态，**不是**游戏画面冻结（崩铁卡旧帧案教训）。

### 崩铁卡旧帧案（已结）
- 根因是用户旧 DLSS5 安装残留 + 覆盖包叠盖，非包 bug；解法 = 清洁重装。
- 真桥 bug（fresh=0/replay 涨）09-29 已在 mfg-ada-0.1.6 修掉；勿再误判。

### OptiScaler fork 已知坑
- launcher profile 会**全量覆盖** `OptiScaler.ini`；`FGOutput=auto→NoFG` 是 FG 不跑的根因；
  DX11 需 Upscaler+DLSSG 且 `AdaMfgUnlock=false`；注意回写再感染。
- 原神 DX11+DLSSG：Streamline FG swapchain ResizeBuffers E_INVALIDARG → device removed，
  Upscaler 侧不可修，备选 FSRFG/XeFG。

## 待办 / 跟进

- [ ] 崩铁实测「不用 HoYoShade 注入器」（等部署完成）
- [ ] 实测结果出来后决定该开关去留 / 是否进 1.4.0.1
- [ ] mfg-ada-0.1.7 例行版本跟进 + 覆盖包重打包（用户没提就放着）
- [ ] 游戏侧残留卡顿排查（fg-dx11-bridge-stutter 记忆里的残留项）
- [ ] 原神 NR matched residual 花屏：疑 MV scale 量级链，未结

### 运动矢量方向修复候选（2026-10-03 19:26）

- 根因定位：当前实际走的是 `Fsr2TranslationLayer` 的标准 `ffxFsr2*` shim；其输入准备 shader 使用 `sign(d) * 4*d²`，而同一工程已验证的 `Ffx12Backend` 使用 `-sign(d) * 4*d²`。在当前 `Fsr2MotionVectorScaleMode=1` 下，shim 路径会把历史重投影方向反转，正好对应移动地面/墙体严重果冻。
- 已修复：`D:\CODE\genshin_fsr_brigde\Dx11FsrBridge\Fsr2TranslationLayer.cpp` motion decode 改为 `-sign(d) * 4*d²`；同时让 translation layer 的 `DepthInverted` 跟随实际配置，不再硬编码。
- 构建：`build-shim-tests\Dx11FsrBridge.dll`，FileVersion `2.3.1.0`，SHA256 `21B2483E456576B7559F1A33A719F986BAE276C3C7A5D05FB4F80CBFE4F9AFE9`。
- 测试：`Il2CppCallSiteHookTest`、`Fsr2InputDumpTest` 均通过，100%。
- 尚未覆盖当前 live。旁路候选：`D:\APPS\HoYoShadeHub\_staging-fsr-motion-sign-fix-20261003-1926\`。
- 用户已明确授权：后续经过验证的 runtime 变更可以直接覆盖 active installation；仍保留备份并校验 hash。
- 本次 motion-sign 修复已直接覆盖 `D:\APPS\HoYoShadeHub\Cache\modules\genshin-fsr-bridge\Dx11FsrBridge.dll`；部署后 SHA256 `21B2483E456576B7559F1A33A719F986BAE276C3C7A5D05FB4F80CBFE4F9AFE9`。回滚文件：`D:\CODE\HoyoDLSS5\build\handoff\live-backup-before-motion-sign-fix-20261003-1930\Dx11FsrBridge.dll.before`。

## 原神 6 倍覆盖包（2026-10-03）

- 已生成：`D:\APPS\test\原神6倍覆盖包_2.4.zip`
- 工作目录：`build\overlay-genshin-6x-20261003`
- 游戏 key：`hk4e_cn`
- 自动动作：关闭其它插件→只启用 `renodx-dlss5.addon64`→启用 OptiScaler→选择 `mfg-ada/mfg-ada-0.1.8`→关闭 Smooth Motion→启用 HoYoShade/OptiScaler/FSR Bridge→导入并激活 `40-原神 x6`
- FSR Bridge：随包放入 `modules\genshin-fsr-bridge`，AMD FFX12 SDK 放入 `modules\AMD`；已清除机器绝对路径，Bridge DLL SHA256 为 `21B2483E456576B7559F1A33A719F986BAE276C3C7A5D05FB4F80CBFE4F9AFE9`
- 包校验：57 个清单文件全部存在，manifest 自身大小一致；zip 大小 641,823,074 bytes。
- 启动器 `LocalPackageInstaller` 的 GamePack 文件名复制修复已在源代码并完成 Release 编译；当前 HoYoShadeHub 进程仍在运行，未强制覆盖正在加载的启动器文件。发布尝试因解决方案中的 Setup 项目要求 self-contained（NETSDK1102）失败，Extensions/主逻辑输出已生成到 `build\publish-overlay-fix-20261003`，待关闭启动器后按既有部署流程覆盖。

### 2026-10-03 23:02 修正
- 修复原神覆盖包遗漏的「启用模块」总开关：动作顺序现在是关闭模块总开关 → 启用 `genshin-fsr-bridge` → 重新启用模块总开关。
- 已重新生成 `D:\APPS\test\原神6倍覆盖包_2.4.zip`；57 个清单文件及 manifest 大小再次校验通过。
