# 当前进度与注意事项

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
